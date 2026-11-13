# 07 — Development, Deployment, Configuration, Monitoring and Testing

## Local development

Requirements: .NET 10 SDK, Docker.

```bash
# PostgreSQL for development
docker run -d --name backora-pg-dev -e POSTGRES_USER=backora_owner -e POSTGRES_PASSWORD=<dev-password> \
  -e POSTGRES_DB=backora -p 127.0.0.1:56543:5432 postgres:18-alpine

# Secrets via environment (never committed); see Configuration below
export ConnectionStrings__Backora="Host=127.0.0.1;Port=56543;Database=backora;Username=backora_owner;Password=<dev-password>"
export Encryption__ActiveKeyId=dev Encryption__Keys__dev=$(openssl rand -base64 32) Jwt__SigningKey=$(openssl rand -base64 48)
export Bootstrap__AdminEmail=admin@backora.local Bootstrap__AdminPassword='<initial password>'

dotnet run --project src/Backora.Api -- migrate           # apply migrations + seed roles/admin, then exit
dotnet run --project src/Backora.Api                       # http://localhost:5080 (OpenAPI UI at /scalar in Development)
dotnet run --project src/Backora.Worker                    # background worker
cd ../frontend && Backend__BaseUrl=http://localhost:5080 dotnet run --project src/Backora.Web   # UI
```

## Containers (Docker Compose)

```bash
cd backend/deploy
./generate-env.sh                                  # random secrets into deploy/.env (git-ignored)
docker compose up -d --build                       # postgres → migrator → api, worker → frontend
docker compose --profile lab up -d --build         # + disposable SSH worker/target and S3 for trying Backora end to end
open http://localhost:8443                         # sign in with BACKORA_ADMIN_EMAIL / BACKORA_ADMIN_PASSWORD from .env
```

All containers run as UID 1001 with group 0, a read-only root filesystem, a `tmpfs` /tmp, `cap_drop: ALL`, `no-new-privileges`, CPU/memory limits and health checks. Start order is enforced: postgres healthy → migrator completed → api healthy → frontend. Verified on this machine: every service reported healthy; a backup, offload and restore cycle driven through the UI succeeded.

Lab configuration through the UI:
1. **Settings → Worker:** host `lab-worker`, user `root`, private key from `deploy/.secrets/lab_id_ed25519` → Test → Trust.
2. **Settings → NFS:** path `/BackoraDB`.
3. **Default SSH key:** the same key.
4. **Storage:** endpoint `http://lab-s3:7070`, bucket `backora-lab`, keys from `.env`.
5. **Servers:** host `lab-target` → Test → Trust.
6. Create a template and a job.

## Kubernetes / OpenShift

`deploy/kubernetes/backora.yaml` contains:
- A migrator `Job` and the API, worker and frontend `Deployment`s.
- Services, an OpenShift `Route` (edge TLS) and PVCs for Data Protection keys.

Pods set `runAsNonRoot`, a read-only root filesystem, `drop: ALL` and `RuntimeDefault` seccomp, and do not pin UIDs, so they are compatible with OpenShift's restricted SCC. Create `backora-secrets` out-of-band (`secret-template.yaml`). Rollout order: run the migrator Job, wait for completion, then roll out the Deployments.

**Network requirements:**
- API and worker → PostgreSQL.
- API and worker → worker node SSH. The API needs this for connection tests and restore prechecks.
- API and worker → S3 HTTPS.
- Worker node → targets on SSH; worker node → S3; targets and worker → NFS.
- Frontend → API.

## Configuration reference

| Key (env var form) | Used by | Description |
|---|---|---|
| `ConnectionStrings__Backora` | api, worker, tools | PostgreSQL connection string (`backora_app` for api/worker, `backora_owner` for the migrator). Use `SSL Mode=VerifyFull` in production |
| `Encryption__ActiveKeyId`, `Encryption__Keys__<id>` | api, worker, tools | AES-256-GCM keys (base64, 32 bytes). Validated at startup (fail fast) |
| `Jwt__SigningKey` | api | HMAC key, base64, at least 32 bytes. `Jwt__AccessTokenMinutes` (15), `Jwt__RefreshTokenDays` (14) |
| `Platform__PublicUrl` | api, worker | Frontend URL for links in emails and Teams cards |
| `Bootstrap__AdminEmail`, `Bootstrap__AdminPassword`, `Bootstrap__AdminName` | migrator/api | First admin, only when no user exists |
| `DataProtection__KeysPath` | api, frontend | Persistent key ring (shared volume across replicas) |
| `Database__MigrateOnStartup` | api | `false` in production; use the migrator |
| `ForwardedHeaders__KnownNetworks__N` | api | CIDRs of trusted proxies (BFF / ingress) |
| `RateLimits__LoginPerMinute` / `SensitivePerMinute` / `GlobalPerMinute` | api | 10 / 30 / 600 |
| `OpenApi__Enabled` | api | Expose `/openapi/v1.json` outside Development |
| `Worker__MaxConcurrentBackups` / `MaxConcurrentTransfers` / `MaxConcurrentRestores` | worker | 10 / 4 / 2 per replica |
| `Worker__PollIntervalSeconds`, `ClaimIntervalSeconds`, `LeaseSeconds` | worker | 5 / 3 / 120 |
| `Worker__TransferStallSeconds` | worker | 900. Terminate and retry transfers with no progress |
| `Worker__ArtifactDiscoverySkewMinutes` | worker | 5. Clock-skew allowance for NFS mtimes |
| `Worker__SchedulerIntervalSeconds`, `RetentionIntervalMinutes`, `NotificationIntervalSeconds` | worker | 20 / 15 / 10 |
| `Worker__EnableOperations` / `EnableScheduler` / `EnableRetention` / `EnableNotifications` | worker | Split roles across deployments if desired. Keep the scheduler **disabled** while the legacy scheduler still runs |
| `Worker__ShutdownTimeoutSeconds` | worker | 45 |
| `Ssh__ConnectTimeoutSeconds`, `Ssh__KeepAliveSeconds` | api, worker | 20 / 30 |
| `OTEL_EXPORTER_OTLP_ENDPOINT` | api, worker | Enables OTLP export of traces and metrics |
| `Backend__BaseUrl`, `Frontend__PublicUrl`, `Frontend__SessionIdleMinutes`, `Frontend__AllowInsecureCookies` | frontend | See `frontend/docs/README.md` |

## Monitoring

* **Health:**
  - API: `/health/live`, `/health/ready` (PostgreSQL).
  - Worker: the same paths on port 8080.
  - Frontend: `/health`.
* **Logs:** JSON console logs with scopes and correlation id, collected via the platform log pipeline. Secrets are never logged: keys, passwords and tokens are not passed to loggers, and rendered scripts are not logged.
* **Traces (OpenTelemetry):** ASP.NET Core, HttpClient, Npgsql, plus `Backora` activities per operation (`backup.process`, …).
* **Metrics (`Backora` meter, low-cardinality tags only):**
  - `backora.operations.started/completed` {kind, outcome, failure_class}
  - `backora.operations.duration` (s)
  - `backora.transfer.bytes`
  - `backora.leases.lost`
  - `backora.scheduler.runs` {decision}
  - `backora.retention.deleted`
  - `backora.notifications` {channel, outcome}
* **Suggested alerts:**
  - No worker heartbeat for 2 minutes (the dashboard shows this too).
  - `backora.operations.completed{outcome="failed"}` rate.
  - `backora.leases.lost` > 0.
  - Notifications failing.
  - `scheduler.runs{decision="missed"}` > 0.
  - Run duration p95 growth.
  - PostgreSQL readiness.
* **In-product:** dashboard system-health strip, alerts bell (failed runs, offloads and restores in 24 h; worker offline; untrusted hosts; undelivered notifications), audit log.

## Tests

```bash
cd backend
dotnet test tests/Backora.UnitTests                        # 103 tests, no dependencies
dotnet test tests/Backora.SecurityTests                    # 38 tests, Docker (PostgreSQL via Testcontainers)
BACKORA_LEGACY_IMPORT_E2E=1 dotnet test tests/Backora.IntegrationTests   # 77 tests incl. real E2E + legacy import (Docker)
dotnet test tests/Backora.IntegrationTests --filter Category=E2E          # only the backup/restore cycles
cd ../frontend && dotnet test                              # 82 tests (bUnit + BFF host tests)
dotnet list ../backend/Backora.sln package --vulnerable --include-transitive
```

The E2E tests build a disposable SSH image (`tests/Backora.IntegrationTests/E2E/docker`), start PostgreSQL, VersityGW (S3) and the worker and target containers on a private Docker network, and run the real API and worker in-process. Everything is removed afterwards.
