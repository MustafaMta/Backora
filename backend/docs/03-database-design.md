# 03 — Database Design (PostgreSQL)

The schema is created by EF Core migrations (`InitialSchema`, `AuditAppendOnly`) with snake_case naming. Identifiers are `bigint` identity columns, so legacy Laravel ids are preserved on import. All timestamps are `timestamptz` (UTC). Every mutable row has an `xmin` optimistic concurrency token, exposed as `version` in the API.

| Table | Purpose / legacy origin | Notable constraints & indexes |
|---|---|---|
| `users`, `roles`, `user_roles`, `user_claims`, `user_logins`, `user_tokens`, `role_claims` | ASP.NET Core Identity (legacy `users`). `legacy_id`, `legacy_user_code`, `must_change_password`, `is_active`, `last_login_at` | unique normalized email/username; unique `legacy_id` |
| `refresh_tokens` | Rotating refresh tokens (SHA-256), session families | unique `token_hash`; `(user_id, family_id)` |
| `api_tokens` | Personal access tokens (legacy `api_tokens`), SHA-256 | unique `token_hash`; FK user cascade |
| `servers` | Targets (legacy `servers`); encrypted key; trusted host-key fingerprint | unique `name`; `ssh_port` 1–65535 |
| `worker_nodes` | Worker/jump host (legacy `worker_settings`) | singleton `id = 1` |
| `storage_settings`, `ssh_settings`, `smtp_settings`, `teams_settings`, `notification_settings` | Singletons (legacy `nfs_settings`, `ssh_settings`, …); secrets encrypted; recipients `text[]` | `id = 1` checks |
| `variables` | Template variables; `is_secret` → `value_ciphertext` | unique `name`; check: plain XOR ciphertext |
| `script_templates` | Templates with `revision` | unique `(name, category)` |
| `backup_jobs` | Job policy; FK template/server/destination **RESTRICT**; soft delete | unique `name` where not deleted; partial index `next_run_at` for active jobs; checks on timeout, attempts, retention, minimum points |
| `backup_runs` | Executions (legacy `backup_job_executions`) with snapshots, lease/attempt fields, remote pid/dir, log, verification | **`ux_backup_runs_one_active_per_job`** (partial unique on active statuses); **`ux_backup_runs_job_occurrence`** `(job, scheduled_for)`; `ix_backup_runs_claimable`; `(job, created_at desc)`, `(server, created_at desc)`, `created_at desc` |
| `backup_artifacts` | SHA-256 manifest per run (new) | unique `(run, relative_path)`; `size_bytes >= 0` |
| `offload_runs` | S3 copies = recovery points (legacy `upload_to_s3_job_executions`) | one active per backup run; `(job, completed_at desc)`; `(status, retention_state)`; claimable index |
| `restore_runs` | Restores (legacy `restore_to_nfs_job_executions`) | unique `target_path`; one active per recovery point; claimable index |
| `operation_leases` | Worker ownership (kind, operation_id) with DB-clock expiry | PK `(kind, operation_id)`; `expires_at` |
| `cluster_locks` | Singleton task locks (scheduler, retention, notifications) | PK `name` |
| `notification_messages` | Outbox | partial index `(status, next_attempt_at)` where pending; `payload_json jsonb` |
| `audit_events` | Append-only audit trail | triggers reject UPDATE/DELETE/TRUNCATE; indexes on time, actor, resource, action; `details_json jsonb` |
| `idempotency_records` | Idempotency keys | unique `(user_id, scope, key)` |
| `worker_heartbeats` | Worker liveness | PK `worker_id` |

JSONB is used only where the content really is free-form: notification payloads and audit details. Run logs are bounded text (512 KB tail kept).

## Deliberate changes from the Laravel schema

* FKs to servers, templates and destinations are `RESTRICT`. Laravel `CASCADE` deleted a server's jobs **and their entire history**.
* Jobs reference templates by FK, not by `script_name` + `category` strings.
* Run history keeps snapshots (job name, category, template name/revision, script hash).
* Every Laravel enum and status became a string with explicit state machines in code.
* Unused template tables (`user_types`, `roles_permissions`, …) are dropped.

## Accounts and privileges

The migrator connects as `backora_owner` (DDL). The API and worker connect as `backora_app` (DML only, granted through default privileges in `database/init/01-create-roles.sh`). Verified in the compose deployment: `backora_app` can insert into `servers` but has no `CREATE` on the database.
