# 05 — Security Model

Backora can reach privileged hosts and holds credentials for them, so every subsystem treats input as hostile and fails closed.

## Authentication

* **Users:** ASP.NET Core Identity.
  - Passwords: PBKDF2-HMAC-SHA512 with 600k iterations, at least 12 characters, upper, lower and digit.
  - Lockout: 5 failures → 15 minutes.
  - Failures return generic messages and burn hashing time on unknown accounts, so account existence is not revealed.
* **Legacy passwords:** `CompatPasswordHasher` verifies Laravel bcrypt (`$2y$`) and returns `SuccessRehashNeeded`. Identity upgrades the hash at first login, so users keep their passwords.
* **MFA:** TOTP (RFC 6238) with 10 recovery codes. The 2FA challenge is a Data-Protection time-limited token (5 minutes) bound to the security stamp.
* **Tokens between the frontend BFF and the API:**
  - **Access:** JWT HS256, 15 minutes, issuer and audience validated. The security stamp is checked on every request, so disabling a user or changing roles or passwords revokes access immediately.
  - **Refresh:** opaque refresh tokens (48 bytes, SHA-256 at rest), rotated on every use. **Reuse revokes the whole session family.**
* **Automation:** personal access tokens `bko_…` (SHA-256 at rest, expiring, revocable, last-used tracking). The legacy `POST /api/token` is preserved, but it refuses accounts with 2FA enabled and grants only the account's permissions.
* **Browser:** the Blazor BFF keeps tokens server-side. The browser holds only an HttpOnly, Secure, `SameSite=Strict` cookie carrying a session key. Logout is a POST protected by antiforgery. The return URL must be local.
* **Bootstrap admin:** created only when no user exists, from out-of-band configuration, and forced to change password. A middleware blocks every API call except profile, password change and logout until the password is changed.
* **Password reset:** Identity tokens with a 60-minute lifetime, sent through the outbox. Responses are uniform. A reset revokes all sessions.
* **Not implemented:** OIDC/Keycloak federation (see limitations).

## Authorization

Policies are generated per permission (`Backora.Contracts.Security.Permissions`) and enforced on **every** endpoint, with a fallback policy of *authenticated*. Credential changes additionally require `credentials.manage` inside the use case.

| Role | Scope |
|---|---|
| Platform Administrator | Everything, including users and roles |
| Backup Administrator | Servers, credentials, templates, jobs, storage, retention, restores, settings; read users |
| Backup Operator | Read operations, run/cancel jobs, view logs, test connections |
| Restore Operator | Read operations, execute restores, view logs |
| Auditor | Read operations, audit trail, settings and users (read-only) |
| Read-only Viewer | Read operations (no logs, settings, users or audit) |

Escalation guards:
- Only Platform Administrators can grant or revoke Platform Administrator.
- Users cannot change their own roles or disable themselves.
- The last active Platform Administrator cannot be removed.

`SecurityTests.Viewer_cannot_call_any_mutating_endpoint` and `Every_protected_endpoint_rejects_unauthenticated_requests` enumerate all routes at runtime, so newly added endpoints are covered automatically.

## Secrets

* AES-256-GCM envelope `bk1:<keyId>:<base64(nonce|ciphertext|tag)>`, random 96-bit nonces, with the key id bound as associated data. Keys come from configuration or a secret store, **never** the database.
* **Rotation:** add a new key id, switch `Encryption:ActiveKeyId`, run `Backora.Tools rotate-keys`, then retire the old key.
* Secrets are write-only in the API. Responses expose `hasPrivateKey`, masked access-key ids (last 4 characters) and webhook host only. `SecretHandlingTests` asserts that no secret appears in any response or in the audit trail.
* Secret variables are decrypted only while rendering a script. The rendered script lives in a 0700 directory and is deleted when the process exits. Previews mask secrets.
* rclone credentials are passed in a 0600 file over SFTP, never on a command line (`ps` would show it), and deleted after each attempt.

## Customer-key client-side encryption (age)

Selected S3 destinations can be set to `CustomerKey`. Backups to them are encrypted **on the worker host, before upload**, with the official [`age`](https://age-encryption.org/v1) binary (X25519 recipients). Backora implements no cipher: it only parses keys (Bech32/BIP-173, implemented in `Bech32.cs`) and derives X25519 public keys with BouncyCastle to check that a supplied private key matches.

* **What Backora stores:** the recipient public key (`age1…`, canonical lowercase), its fingerprint (`SHA256:` + unpadded base64 of SHA-256 over the 32 raw public-key bytes) and the key history per destination. Each recovery point records the recipient/fingerprint it was encrypted to and the ciphertext size and SHA-256 of every object. For customer keys there is **no private key** anywhere; only sites set to the Backora key store one (below).
* **What Backora cannot do:** restore an encrypted point without the customer's private key (`AGE-SECRET-KEY-1…`). A lost key means the data is unrecoverable. Backora never substitutes an older unencrypted copy and never falls back to uploading plaintext to a `CustomerKey` destination (missing/failing `age` fails the offload).
* **Backora key (optional, chosen per site):** a site can instead use Backora's own age key pair. Its private key is stored in `s3_destination_encryption_keys.identity_ciphertext`, encrypted with the app's encryption key (`Encryption:Keys`, the same protection as S3 secret keys and SSH keys, re-encrypted by `rotate-keys`). One key is shared by every site with this option. Restores of points encrypted to it decrypt automatically (audited as `restore.decryption_key_from_backora`); the key is decrypted only in memory for the restore. Sites with "Encrypt with my key" keep the customer-held model above. Anyone holding both the database and the app encryption key can decrypt Backora-key backups, so protect `Encryption__Keys__*` like the data itself.
* **Key validation:** recipients and identities are checked strictly (bech32 checksum, HRP, 32 bytes). A supplied identity's derived public key is compared with the point's recipient in constant time. Private bytes are zeroed (`CryptographicOperations.ZeroMemory`) after use. Validation messages describe the problem and never echo the value (unit- and API-tested).
* **Key delivery for restores** (keeps the key out of PostgreSQL, queues, logs, files, command lines and the worker process):
  1. The worker stages the restore: 0700 work dir, `key.fifo` (`mkfifo -m 600`) and a detached script that blocks on `IFS= read -r KEY < key.fifo`. Status becomes `AwaitingKey` with `KeyRequestExpiresAt` (`ClientEncryption:KeyRequestTimeoutSeconds`, default 1800 s).
  2. The API validates the key, keeps it only in memory in the singleton `DecryptionKeyCourier` (lifetime ≤ the expiry), and when the restore is `AwaitingKey` opens an SSH session to the worker host and writes it into the FIFO through the command's **stdin** (`IRemoteSession.RunWithInputAsync`, wrapped in `timeout` so a writer never blocks). The buffer is zeroed on delivery, cancellation, supersession or expiry.
  3. The script uses the key only via `age -d -i <(printf '%s\n' "$KEY")` (bash builtin, no argv), streams `rclone cat | age -d` per file into `*.partial`, renames on success, then `unset KEY`. Any failure (e.g. tampered ciphertext → age authentication error) removes every file the script wrote.
  4. API restart before delivery → the key is gone; the operator re-submits via `POST /restores/{id}/decryption-key`. A retry attempt asks for the key again. Expiry or cancel kills the script and removes the FIFO and work dir.
* **Generated key pairs** (`POST /storage/s3/encryption/generate`, `storage.write` + `credentials.manage`) are returned once with `Cache-Control: no-store`; the audit stores the fingerprint only.
* **Audit:** `storage.s3.encryption_enabled|rotated|disabled`, `storage.s3.encryption_key_pair_generated`, `restore.decryption_key_rejected|submitted`, `recovery_point.decryption_key_checked` — public fingerprints only. Disabling encryption requires `confirmDisableEncryption=true`.
* **Verified by** `EncryptedBackupTests` (E2E): a pg_dump of the database, all captured API/worker log lines, S3 object metadata and the worker host's disk are scanned for the private keys after real encrypted backups and restores.
* **Residual risk (documented):** the key exists in the API process memory (and, unavoidably, in the immutable request string and SSH.NET buffers) until delivered, and in the restore script's bash memory during the restore; root on the API host or the worker host can read process memory. See `08-known-limitations.md`.

## Remote execution and injection defences

* No shell is ever invoked with user-controlled strings unquoted. All dynamic values pass through `ShellQuote.Quote`.
* Paths are validated with `ShellQuote.IsSafeAbsolutePath` / `Join`, so traversal is rejected. Remote work directories must contain `backora`. Deletions list explicit files, never globs.
* Input rules:
  - Hosts must be RFC 1123 names or IP literals.
  - Users must be POSIX names.
  - Job names are safe path segments, because they are NFS directories and S3 prefixes.
  - rclone paths are `rclone` or an absolute path.
* Template scripts are intentionally arbitrary bash, because that is the product. Editing them requires `templates.write` (administrators). Every change is audited with old and new SHA-256, and every run records the exact script hash and revision.
* **SSH host keys:** trust-on-first-use with **explicit** administrator approval. Execution refuses unknown or mismatched keys (`HOST KEY MISMATCH`, tested). Changing a server's host or port clears trust. There is no way to disable verification.
* **TLS:** S3 (AWS SDK), SMTP (MailKit) and Teams use default certificate validation. There is no bypass option.
* **SSRF:**
  - S3 endpoints reject link-local and metadata addresses.
  - Teams webhooks must be HTTPS and redirects are not followed.
  - Connection tests are restricted to `servers.test` / `storage.write` / `settings.write` and are rate limited.

## Application security

* **Parameterised SQL only:** EF Core, plus Npgsql parameters in the lease store; table names come from a constant map.
* **Errors:** RFC 9457 problem details with a correlation id; no stack traces outside Development (tested).
* **Headers:** API — `nosniff`, `DENY` framing, `default-src 'none'` CSP, `no-store` on /api, no `Server` header, HSTS outside Development. Frontend — strict CSP without inline scripts.
* **Limits:** 2 MB request bodies; rate limits on login (10/min/IP), sensitive operations (30/min/user) and a global limit (600/min/user).
* **Idempotency keys:** restricted alphabet, maximum 200 characters.
* **Proxy headers:** `X-Forwarded-For` is honoured only from configured proxy networks (`ForwardedHeaders:KnownNetworks`). The BFF forwards the real client IP.

## Audit

`audit_events` records actor, IP, action, resource, outcome (Success/Failure/Denied), correlation id and details (never secrets). It covers:
- Authentication: logins, failures, lockouts, 2FA, password changes and resets, tokens, sessions.
- User and role changes.
- Credential and settings changes.
- Templates, servers, jobs, runs, cancellations, restores and their confirmation mismatches.
- Protection holds and retention deletions.

The table is append-only at the database level (triggers) and the app role is DML-only.

## Remaining hardening recommendations

* Run the frontend behind TLS (ingress or reverse proxy) with `Frontend:AllowInsecureCookies=false` (the default).
* Store `Encryption__Keys__*` and `Jwt__SigningKey` in a secret manager (Vault, OpenShift Secrets with encryption at rest).
* Give the worker node a dedicated unprivileged user with `sudo` only for the mount and backup commands the templates need. Scope target `authorized_keys` with `from=` restrictions to the worker's IP.
* Add OIDC (Keycloak) federation if central identity is required.
