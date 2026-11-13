# 02 — Architecture

```
            Browser
               │  HTTPS, HttpOnly SameSite=Strict session cookie (no tokens in the browser)
               ▼
┌──────────────────────────────┐        ┌───────────────────────────────────────────┐
│ frontend/  Backora.Web       │ JWT    │ backend/  Backora.Api  (ASP.NET Core)      │
│ Blazor Web App (Interactive  ├───────►│ minimal APIs /api/v1 · Identity · policies │
│ Server) acting as BFF        │ XFF    │ legacy /api/token… compatibility           │
└──────────────────────────────┘        └───────────────┬───────────────────────────┘
                                                        │ EF Core / Npgsql (DML-only role)
                                                        ▼
                                        ┌───────────────────────────────────────────┐
                                        │ PostgreSQL 18                              │
                                        │ config · runs · leases · outbox · audit    │
                                        └───────────────▲───────────────────────────┘
                                                        │ FOR UPDATE SKIP LOCKED leases
┌───────────────────────────────────────────────────────┴───────────────────────────┐
│ backend/  Backora.Worker  (N replicas)                                            │
│ dispatchers: backup · offload · restore │ cluster singletons: scheduler ·         │
│ retention · notifications │ heartbeat                                             │
└───────┬───────────────────────────────────────────────┬───────────────────────────┘
        │ SSH (SSH.NET, host keys pinned)               │ S3 API (AWS SDK): list/verify/delete
        ▼                                               ▼
┌──────────────────────────────┐ direct-tcpip ┌──────────────────┐    ┌─────────────────┐
│ Worker node (jump host)      ├─────────────►│ Target servers   │    │ S3-compatible   │
│ NFS mounted at NFS_PATH      │   tunnel     │ run backup script│    │ object storage  │
│ rclone copy/check/restore ───┼──────────────┼──────────────────┼───►│                 │
└──────────────────────────────┘              └──────────────────┘    └─────────────────┘
```

## Solution layout

| Project | Responsibility | Depends on |
|---|---|---|
| `Backora.Domain` | Entities, state machines (`BackupRun`, `OffloadRun`, `RestoreRun`), pure policies: `SchedulePlanner` (Cronos, TZ/DST), `RetentionPlanner`, `ScriptRenderer`, `ShellQuote`, `PlaceholderTemplate` | Cronos only |
| `Backora.Contracts` | Versioned API DTOs (`V1`), roles, permissions and the role→permission matrix. Shared by backend and frontend | — |
| `Backora.Application` | Use cases (`ServerService`, `BackupJobService`, `RecoveryService`, …), validators, operation processors, scheduler/retention/notification services, abstractions (`ISshConnector`, `IObjectStorage`, `ILeaseStore`, `ISecretProtector`, …) | Domain, Contracts, EF Core (PostgreSQL provider for `ILIKE`) |
| `Backora.Infrastructure` | `BackoraDbContext` + migrations, Identity (bcrypt-compatible hasher, JWT/refresh tokens, PATs), AES-GCM secret protector, SSH.NET connector, AWS S3 storage, MailKit, Teams sender, audit log, PostgreSQL lease store | Application |
| `Backora.Api` | HTTP: authentication (JWT + PAT), permission policies, rate limiting, correlation IDs, security headers, RFC 9457 errors, endpoints, health, OpenAPI, OpenTelemetry | Infrastructure |
| `Backora.Worker` | Separate process: lease-based dispatchers, cluster-locked singletons, heartbeat, graceful drain | Infrastructure |
| `Backora.Tools` | `migrate`, `create-admin`, `rotate-keys`, `import-legacy`, `verify-import` | Infrastructure |
| `frontend/Backora.Web` | Blazor BFF: server-side session store, token refresh, typed API clients, all screens | Contracts |
| `frontend/Backora.UiComponents` | Design system and SVG charts | — |

Pragmatic clean architecture: there is no generic repository; use cases work against `IAppDbContext`. The Application layer references the Npgsql EF provider deliberately, because PostgreSQL is a fixed platform decision and `ILIKE` search is needed.

## Key decisions and trade-offs

| Decision | Why | Trade-off |
|---|---|---|
| **The PostgreSQL tables are the job queue** (operation rows plus `operation_leases`), not Hangfire/Quartz | Business state and execution state cannot diverge. `FOR UPDATE SKIP LOCKED` claims, DB-clock leases, and heartbeats live in a separate table, so they never clash with optimistic concurrency on the operation row | Each operation kind has its own claim SQL (three small queries) |
| **Detached remote processes** (`setsid` + pid + atomic `exit_code` file in a per-run 0700 directory) | A worker crash or deploy never kills a running backup. Another replica adopts it by reading the pid/exit files, and starting is idempotent (it adopts rather than duplicates) | Requires `bash`, `setsid`, `kill`, `sha256sum`, and `stat` on targets and worker (standard on RHEL/Debian; busybox also tested) |
| **SSH tunnel through the worker** (SSH.NET `ForwardedPortLocal`), not `ssh` invoked on the worker | Both host keys are verified against fingerprints the admin explicitly trusted. The target key never touches the worker's disk. The server's port and key are honoured (legacy ignored them) | Targets must authorise the server key or the global default key (legacy relied on the worker's own `~/.ssh` key) |
| **rclone stays on the worker** for transfers, AWS SDK for listing/verification/deletion | Data moves NFS→S3 without passing through Backora. Verification is independent of the transfer tool | rclone must be installed on the worker (never auto-installed) |
| **Blazor Interactive Server + BFF** | Tokens never reach the browser. Strict CSP. One C# stack | Sticky sessions are needed with more than one frontend replica. Sessions are kept in memory and written through, encrypted, to the frontend's private volume (`Frontend:SessionsPath`, key ring encrypted at rest with `DataProtection:KeyEncryptionKey`), so restarts and redeploys keep users signed in |
| **Soft delete for jobs; restrict delete for servers/destinations with history** | Recovery points and the audit trail stay meaningful | Decommissioned servers remain listed (documented limitation) |

## Request flow (Run now)

1. Blazor → BFF `TokenHandler` attaches the access token (refreshing it if needed) and `X-Forwarded-For`.
2. `POST /api/v1/backup-jobs/{id}/runs` → `BackupJobService.RunNowAsync`. An idempotency-key replay returns the existing run. The partial unique index `ux_backup_runs_one_active_per_job` rejects concurrent duplicates with 409. The audit event commits in the same transaction.
3. A worker dispatcher claims the run (lease), and `BackupRunProcessor` drives it: Pending → Starting → Running → Verifying → Completed, or Failed/Cancelled (see `06-backup-engine.md`).
4. On completion with an S3 destination, an `OffloadRun` is inserted **in the same transaction**. Failures write notification outbox rows in the same transaction.
5. The UI polls `GET /api/v1/runs/{id}` every 3 s while the run is non-terminal. Page views never open SSH connections.
