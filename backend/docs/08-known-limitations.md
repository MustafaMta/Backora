# 08 — Known Limitations and Behaviour Changes

## Not implemented (documented, not hidden)

| Item | Notes |
|---|---|
| OIDC / Keycloak federation | Optional in the brief. Not shipped, because it could not be verified against a live identity provider. Local accounts plus TOTP MFA are implemented |
| Incremental / differential backups | The legacy system produced full archives only. Backora does not claim incremental support. The retention planner has a dependency hook for future chains |
| Restore *into* databases or servers | As in the legacy system, restores land in an isolated NFS staging directory with checksum validation. Loading data into a database remains a deliberate DBA step |
| QR code for TOTP enrolment | The shared key and `otpauth://` URI are shown as text |
| Multi-replica frontend without affinity | Blazor Server circuits need sticky sessions (BFF sessions persist on the shared frontend volume, so a restart no longer signs users out; without `Frontend:SessionsPath` they are in memory only) |
| Server archive / decommission | Servers with backup history cannot be deleted, to preserve recovery points and the audit trail |
| Live push of progress | The UI polls every 3 s while an operation is active. Values are real; no simulated progress |
| Production SMTP / Teams delivery | Senders are implemented. Rendering and escaping are unit-tested and outbox behaviour is integration-tested. Delivery to a real SMTP server or Teams tenant was not exercised (no credentials available) |
| DR copy retention | DR copies follow the job's retention (per destination, with the same minimum). A separate, longer retention for the DR site is not configurable yet |
| DR copy between different storage accounts | Data streams through the worker node (rclone with two remotes). Server-side copy is used only when both destinations share endpoint and credentials. Verified against VersityGW only |
| Re-encryption of existing recovery points | Rotating a destination's recipient affects new uploads only. Existing points keep the key they were encrypted to and need that key to restore. Replicating an encrypted point to a destination with a different recipient is refused. Historical re-encryption is not implemented (it would require the private key) |
| Ciphertext staging space | Encrypted offloads stage the full ciphertext of a run in the worker host's work directory before upload, so it needs free space ≈ the run's artifact size (plus ~0.03 % age overhead) in addition to the NFS copy |
| Decryption key exposure window | The private key lives in the API process memory from submission until delivery (bounded by the 30-minute key request; also in the immutable request string and SSH.NET buffers, which cannot be zeroed) and in the restore script's bash memory during the restore. Root on the API host or the worker host can read process memory. An API restart drops the key; the operator must submit it again |
| Lost customer key | Unrecoverable by design. Backora cannot restore an encrypted point without the private key and never falls back to another copy |
| age plugins / passphrase / post-quantum recipients | Only native X25519 recipients (`age1…`) are accepted; `age1pq…`, plugin and SSH recipients are rejected |
| Production S3 provider | Verified against VersityGW (S3-compatible) through both rclone and the AWS SDK. Not verified against AWS S3 or the production Ceph/S3 endpoint |

## Behaviour changes from Laravel (intentional)

| Legacy behaviour | New behaviour | Reason |
|---|---|---|
| Upload marked complete by text heuristics, then `rm -rf <nfs>/<job>/*` | Copy, then `rclone check`, then an S3 listing; only then delete manifest files | Prevents destroying the only copy |
| `rclone sync` to `<job>/<Y-m-d-H>/` | `rclone copy` to `<job>/<Y-m-d-H>-r<id>/` | Two runs in one hour overwrote each other |
| Failed uploads deleted and retried every 30 s forever | 3 attempts with backoff, then Failed with notification and manual retry; history kept | Unbounded retries and lost history |
| Restore into the live job directory | Restore into `restore-rp<id>-<ts>/` under the job directory | Restored files were re-uploaded and then deleted |
| Target port and key ignored; worker's own key used; `StrictHostKeyChecking=no` | Tunnel with the server key or global key, honoured port, pinned host keys | Security |
| `exit_code` marker appended to the log | Atomic exit-code file, plus artifact verification | False success |
| Cancel with `pkill -f <script_name>` | Kill only the run's own process group | Killed other jobs |
| Deleting a server cascaded to jobs and history | RESTRICT; jobs are soft-deleted | Audit and recovery points |
| Scripts in `/tmp/<script>.sh` shared across jobs | Per-run 0700 directory, script deleted after exit | Collisions, secret exposure |
| `127.0.0.1` "simulation mode" (never finished) | Removed | Fake execution |
| Unresolved `{placeholder}` passed to bash literally | Run fails with `Configuration` before execution | Fail fast |
| Viewer role enforced only in some edit pages | Permission policy on every endpoint | Broken access control |
| Retention purged any listed folder by integer day age | DB-tracked points only; protection, minimum verified points, active restores and shared prefixes respected | Safety |
| rclone auto-installed with `curl | sudo bash` | Never auto-installed; worker test reports if rclone is missing | Supply chain |
| Imported jobs | Imported **Inactive** by default (`--preserve-job-status` to keep) | Nothing runs before host keys are trusted and the legacy scheduler is stopped |
| Template `{X}` inside `${...}` replaced | Not replaced (bash parameter expansion left intact) | Avoid corrupting scripts |

## Operational prerequisites

* For customer-key destinations the worker node also needs the official `age` binary ≥ 1.1 (`apk add age` / `apt install age`) at `ClientEncryption:AgePath` (default `age`), `mkfifo` and `timeout`. It is never auto-installed.
* The worker node needs `bash`, `rclone`, `sha256sum`, `stat`, `find`, `setsid` (util-linux) and the NFS share mounted at `NFS_PATH`. Targets need `bash`, `setsid` and whatever their backup templates use.
* The global default SSH key (or per-server keys) must be authorised on every target. The legacy system used the worker's own key. **Check this during cutover with Test connection.**
* The legacy `backup_jobs` table has no `s3_config_added_at` column in production, so imported jobs offload runs created after the import.
