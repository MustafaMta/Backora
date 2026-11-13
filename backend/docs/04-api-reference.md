# 04 — API Reference (v1)

The machine-readable OpenAPI 3.1 document is served at `GET /openapi/v1.json`. In Development, an interactive reference is also served at `/scalar`. DTOs are defined in `src/Backora.Contracts` (namespace `Backora.Contracts.V1`), which is the single source of truth shared with the Blazor frontend.

## Conventions

| Topic | Convention |
|---|---|
| Base path | `/api/v1`. Breaking changes ship as `/api/v2` with a `Backora.Contracts.V2` namespace |
| Auth | `Authorization: Bearer <JWT access token>` (frontend BFF) or `Authorization: Bearer bko_<personal access token>` (automation) |
| Errors | RFC 9457 `application/problem+json` with `traceId`, `correlationId`, and `errors` (validation map) for 400. Production responses never contain stack traces or secrets |
| Status codes | 200/201/202/204 success · 400 validation · 401 unauthenticated · 403 forbidden · 404 not found · 409 conflict (state, uniqueness, active run) · 412 version mismatch · 422 domain rule · 429 rate limited |
| Paging | `?page=1&pageSize=25` (max 200) → `PagedResult<T> { items, page, pageSize, total, totalPages }` |
| Sorting | `?sort=field` or `?sort=-field` (descending). Allowed fields are listed per endpoint |
| Filtering | Endpoint-specific query parameters; `q` is free-text search |
| Concurrency | Mutable resources expose `version` (PostgreSQL `xmin`). Send it back on `PUT`; a stale version returns **409** with code `concurrency_conflict` |
| Idempotency | `POST /backup-jobs/{id}/runs` and `POST /restores` accept `Idempotency-Key`. Replays return the original resource with `alreadyExisted: true` |
| Correlation | `X-Correlation-ID` is accepted or generated and echoed on every response. It is recorded in logs, audit events, and runs |
| Time | All timestamps are UTC ISO-8601 (`DateTimeOffset`) |
| Enums | Serialized as strings |

## Endpoints

Permission names come from `Backora.Contracts.Security.Permissions`.

### Authentication & profile
| Method | Path | Permission | Notes |
|---|---|---|---|
| POST | `/auth/login` | anonymous, rate limited | → `LoginResponse` (tokens or 2FA challenge) |
| POST | `/auth/login/2fa` | anonymous, rate limited | TOTP or recovery code → `TokenResponse` |
| POST | `/auth/refresh` | anonymous | Rotating refresh token. Reuse revokes the whole token family |
| POST | `/auth/logout` | authenticated | Revokes the refresh token family |
| POST | `/auth/password/forgot` | anonymous, rate limited | Always 202 (no account enumeration) |
| POST | `/auth/password/reset` | anonymous | Identity reset token |
| GET | `/me` | authenticated | `CurrentUserDto` with effective permissions |
| PUT | `/me/profile` | authenticated | |
| POST | `/me/password` | authenticated | Revokes other sessions |
| POST | `/me/2fa/setup` · `/me/2fa/enable` · `/me/2fa/disable` | authenticated | TOTP (RFC 6238) and recovery codes |
| GET/POST/DELETE | `/me/tokens[/{id}]` | authenticated | Personal access tokens (`bko_…`, SHA-256 stored) |
| GET/DELETE | `/me/sessions[/{id}]` | authenticated | Refresh-token sessions |

### Users & roles
| Method | Path | Permission |
|---|---|---|
| GET | `/users`, `/users/{id}` | `users.read` |
| POST | `/users` · PUT `/users/{id}` · POST `/users/{id}/password` · POST `/users/{id}/unlock` | `users.manage` (only Platform Administrators can grant Platform Administrator; users cannot change their own roles or disable themselves) |
| GET | `/roles` | `users.read` |

### Servers
| Method | Path | Permission |
|---|---|---|
| GET | `/servers` (`q`, `page`, `pageSize`, `sort=name\|host\|createdAt`) · `/servers/{id}` | `servers.read` |
| POST · PUT · DELETE | `/servers[/{id}]` | `servers.write` (+ `credentials.manage` when a private key is supplied). Delete returns 409 while jobs reference the server |
| POST | `/servers/{id}/test` · `/servers/test` | `servers.test` |
| POST | `/servers/{id}/trust-host-key` | `servers.write` |

### Settings
| Method | Path | Permission |
|---|---|---|
| GET | `/settings/worker` · `/settings/nfs` · `/settings/ssh` · `/settings/smtp` · `/settings/teams` · `/settings/notifications` | `settings.read` |
| PUT | same paths | `settings.write` (+ `credentials.manage` for keys/passwords/webhooks) |
| POST | `/settings/worker/test` · `/settings/worker/trust-host-key` | `settings.write` |
| POST | `/settings/smtp/preview` · `/settings/smtp/test` · `/settings/teams/preview` · `/settings/teams/test` | `settings.write` |

### Variables, templates, storage
| Method | Path | Permission |
|---|---|---|
| GET | `/variables` | `templates.read` (secret values are never returned) |
| POST · PUT · DELETE | `/variables[/{id}]` | `templates.write` (+ `credentials.manage` for secret variables) |
| GET | `/templates` (`q`, `category`) · `/templates/categories` · `/templates/{id}` | `templates.read` |
| POST · PUT · DELETE | `/templates[/{id}]` | `templates.write`. Delete returns 409 while jobs reference the template |
| GET | `/storage/s3` · `/storage/s3/{id}` | `storage.read` |
| POST · PUT · DELETE | `/storage/s3[/{id}]` | `storage.write` + `credentials.manage` |
| POST | `/storage/s3/test` | `storage.write` |
| POST | `/storage/s3/encryption/inspect` `{ "recipient": "age1…" }` → `{ valid, fingerprint, error, format }`. Nothing is saved | `storage.write` |
| POST | `/storage/s3/encryption/generate` → `{ recipient, privateKey, fingerprint }`. The private key is returned **once** (`Cache-Control: no-store`), never stored; the audit records only the fingerprint. Sensitive rate limit | `storage.write` + `credentials.manage` |

S3 destinations carry `encryptionMode` (`None` \| `CustomerKey`), `encryptionRecipient`, `encryptionKeyFingerprint`, `encryptionKeyActivatedAt`, `encryptionKeyHistory[]` (`fingerprint`, `recipient`, `activatedAt`, `retiredAt`, `recoveryPointCount`) and `lastEncryptedUploadAt`. On `PUT`: an empty `encryptionRecipient` keeps the active key; a different recipient rotates it (old key retired, existing points unchanged); switching to `None` requires `"confirmDisableEncryption": true`, otherwise `422 encryption_disable_unconfirmed`. `CustomerKey` without a valid recipient is a `400` validation error.

### Backup jobs, runs, offloads
| Method | Path | Permission |
|---|---|---|
| GET | `/backup-jobs` (`q`, `status`, `category`, `serverId`, `health`, `sort=name\|nextRunAt\|createdAt`) · `/backup-jobs/{id}` · `/backup-jobs/categories` | `jobs.read` |
| POST · PUT · DELETE | `/backup-jobs[/{id}]` | `jobs.write` (delete is a soft delete; history is preserved). Body field `s3DestinationId` = primary destination, `replicaDestinationIds` = DR copy destinations (must differ from the primary) |
| POST | `/backup-jobs/{id}/status` | `jobs.write` |
| POST | `/backup-jobs/schedule-preview` | `jobs.read` |
| GET | `/backup-jobs/{id}/script-preview` | `templates.read` (secret variables masked) |
| POST | `/backup-jobs/{id}/runs` | `jobs.run` (+ `Idempotency-Key`). 409 when a run is already active |
| GET | `/runs` (`status`, `jobId`, `serverId`, `trigger`, `from`, `to`, `q`, `sort=createdAt\|startedAt\|duration`) · `/runs/{id}` | `runs.read` (log text requires `runs.logs`) |
| POST | `/runs/{id}/cancel` | `runs.cancel` |
| POST | `/runs/{id}/retry` | `jobs.run` |
| GET | `/offloads` (`status`, `jobId`, `serverId`, `destinationId`, `replica`=true\|false, `q`) · `/offloads/{id}` | `recovery.read` |
| POST | `/offloads/{id}/retry` (failed upload or failed DR copy) | `jobs.run` |

### Recovery points & restores
| Method | Path | Permission |
|---|---|---|
| GET | `/recovery-points` (`jobId`, `serverId`, `destinationId`, `from`, `to`, `q`, `includeDeleted`, `encrypted`) · `/recovery-points/servers` | `recovery.read` |
| POST | `/recovery-points/{id}/verify-key` `{ "privateKey": "AGE-SECRET-KEY-1…" }` → `{ matches, fingerprint, message }`. Checks only; nothing is stored. Sensitive rate limit | `restores.execute` |
| POST | `/recovery-points/{id}/protection` | `recovery.protect` |
| POST | `/recovery-points/{id}/replicate` `{ "destinationId": 2 }`: copy a point S3 → S3 to another destination (DR) | `jobs.run` |
| POST | `/restores/precheck` | `restores.execute` |
| POST | `/restores` | `restores.execute` (typed confirmation phrase required, `Idempotency-Key`). For an encrypted point `decryptionKey` is required: missing → `422 decryption_key_required`, not matching the point's recipient → `422 decryption_key_mismatch`; malformed → `400`. Nothing is created in those cases |
| POST | `/restores/{id}/decryption-key` `{ "privateKey": "…" }` → `202` + restore summary. For a restore in `AwaitingKey` (e.g. after an API restart lost the in-memory key, or a retry attempt). `409` if it is not waiting for a key, `422` if the key does not match. Sensitive rate limit | `restores.execute` |
| GET | `/restores` · `/restores/{id}` | `restores.read` |
| POST | `/restores/{id}/cancel` | `restores.execute` |

Offload/recovery-point summaries include `encrypted`, `encryptionFormat` (`age-v1`) and `encryptionKeyFingerprint`. Restore summaries include `requiresDecryptionKey`, `decryptionKeyFingerprint` and `keyRequestExpiresAt` (set while `status` is `AwaitingKey`). Private keys are never returned by any read endpoint, never echoed in validation errors and never written to logs or the audit trail.

### Dashboard, audit, search, system
| Method | Path | Permission |
|---|---|---|
| GET | `/dashboard?from=&to=` · `/dashboard/alerts` | `dashboard.read` |
| GET | `/audit` (`from`, `to`, `action`, `actor`, `resourceType`, `outcome`) | `audit.read` |
| GET | `/search?q=` | authenticated (results filtered by permissions) |
| GET | `/system/info` | `dashboard.read` |
| GET | `/health/live` · `/health/ready` | anonymous (no sensitive detail) |

### Legacy-compatible automation API (unversioned, preserved from Laravel)
| Method | Path | Behaviour |
|---|---|---|
| POST | `/api/token` | `{email, password, token_name?, expires_in_minutes?}` → `{success, token_type, access_token, expires_at}` |
| DELETE | `/api/token` | Revokes the presented token |
| POST | `/api/servers` | `{server_name, server_ip, ssh_user, ssh_port, private_key?}` |
| POST | `/api/servers/test-ssh` | `{server_ip, ssh_user, ssh_port, private_key?}` |
| POST | `/api/backup-jobs` | `{name, category, script_name, status, schedule_at?, server_id, retention_days, s3_config_id?}` |

Legacy endpoints enforce the same permissions as their v1 equivalents. A legacy `viewer` token can no longer create servers.
