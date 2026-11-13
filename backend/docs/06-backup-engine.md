# 06 — Backup Execution, Offload, Restore and Retention

## Execution model

All long-running work is a **leased operation** row (`backup_runs`, `offload_runs`, `restore_runs`) processed by `Backora.Worker`:

* **Claim:** `PostgresLeaseStore.ClaimNextAsync` locks the oldest due row (`next_attempt_at <= now()`, active status, no live lease) with `FOR UPDATE SKIP LOCKED` and upserts `operation_leases(kind, id, owner, expires_at)`. All times come from the DB clock.
* **Heartbeat:** the dispatcher renews the lease every lease/4. If renewal fails, or the lease is stolen after a long pause, local processing stops before the lease can expire, so two workers never act concurrently. Verified by `DatabaseTests.Concurrent_workers_never_claim_the_same_operation` (40 workers, 12 runs, no duplicates).
* **Crash:** the lease expires (default 120 s) and another replica adopts the operation (`Expired_lease_is_adopted…`).
* **Graceful shutdown:** stop claiming, cancel local steps, release leases. Remote processes keep running (`Worker_restart_during_a_backup_adopts_the_running_process`).
* **Each step** reloads the row in a fresh DbContext. API-side changes such as cancellation are picked up, and an `xmin` conflict just re-evaluates the step.
* **Concurrency limits:** `Worker:MaxConcurrentBackups` (10), `MaxConcurrentTransfers` (4), `MaxConcurrentRestores` (2) per replica. At most one active run per job and one active restore per recovery point are enforced by partial unique indexes.

## Backup run state machine

```
Pending ──► Starting ──► Running ──► Verifying ──► Completed
   │           │  ▲         │            │
   │           │  └─retry (transient, backoff 30s·2^n, max attempts)
   ▼           ▼            ▼            ▼
Cancelled   Cancelling ──► Cancelled   Failed (classified)
```

| Step | What happens |
|---|---|
| Starting | Renders the template (built-ins plus variables; secret variables are decrypted only here). Unresolved placeholders → `Configuration` failure. Records the template revision and script SHA-256. Records the worker clock. Opens a tunnel to the target with pinned host keys. If `/var/tmp/backora/backora-run-<id>/pid` already exists, the process is **adopted**. Otherwise a 0700 directory is created, `script.sh` (0700) is written over SFTP, and it is launched with `setsid nohup bash -c 'echo $$ > pid; bash ./script.sh > output.log 2>&1; echo $? > exit_code.tmp && mv exit_code.tmp exit_code'` |
| Running | Polls every `PollIntervalSeconds` and incrementally reads the log byte-exactly (base64). `exit_code` present → Verifying (0) or `ScriptError`. Pid gone without an exit code → `ProcessLost`. Over `TimeoutMinutes` → Backora first samples the I/O of every process in the run's session (`/proc/PID/io` rchar + wchar, previous sample kept in `{dir}/io_sample`): after a 5-minute measurement, a run still moving ≥ 1 MiB/min gets its deadline extended in 15-minute steps (logged), never past 4 × `TimeoutMinutes`. A run doing no I/O (lock wait, dead mount), one whose counters can't be read, or one at the hard limit → process group terminated, `Timeout`. Losing SSH while running is **not** a failure: polling continues until the timeout. `script.sh` is deleted as soon as the process exits (it may contain rendered secrets) |
| Verifying | On the worker, scans `NFS_PATH/<job>/` (excluding `restore-*`) for files modified since the run started (with a skew allowance), minus files already attributed to earlier runs. Computes size, mtime, and SHA-256 → `backup_artifacts`. **Success requires** exit 0, at least one artifact (unless the job disables `RequireArtifacts`), and no empty artifacts. Otherwise `NoArtifacts` / `VerificationFailed` |
| Completed | Inserts the offload row in the same transaction if the job has a destination assigned before the run was created (legacy semantics). Removes the remote work directory |
| Cancelling | Terminates the process group (TERM, grace period, KILL, then verifies via session id with `pgrep -s`; portable to busybox) |

Failure classes: `Configuration`, `Connectivity`, `Authentication`, `HostKeyMismatch`, `ScriptError`, `NoArtifacts`, `VerificationFailed`, `Timeout`, `ProcessLost`, `StorageError`, `Cancelled`, `Internal`. Only transient classes (`Connectivity`, `Timeout`) before the remote process starts are retried automatically. A script failure is never retried blindly.

## Built-in templates: output, locks, updates

The built-in templates (`backend/database/templates`) write each backup to the job's NFS folder as few, large,
sequential files. A folder of thousands of small files on NFS, read back into a tar and then deleted, used to double
or triple the time of large backups.

| Template | Output | Restore |
|---|---|---|
| MariaDB · Physical | `<job>-<time>.xbstream` (mariabackup `--stream=xbstream --parallel=4`) → `.xbstream.zst` | `zstd -dc file \| mbstream -x -C <dir>`, then `mariabackup --prepare --target-dir=<dir>` |
| PostgreSQL · Physical | `<job>-<time>/base.tar`, `pg_wal.tar`, `backup_manifest` (`pg_basebackup -F t -X stream`) → one `<job>-<time>.tar.zst`; in a container one `<job>-<time>.tar` → `.tar.zst` | `zstd -dc file \| tar -x`, then `base.tar` into an empty data directory and `pg_wal.tar` into its `pg_wal/` |
| MariaDB / PostgreSQL · Logical | `<job>-<time>.sql` → `.sql.zst` (or `.sql.gpg`, kept as is) | `zstd -dc file \| mysql` / `\| psql` |

The "→" step is the worker's compression after the backup (see *Compression and checksums*).

* **The job's folder** on the share is created by the worker (which mounts the whole share) before every run: a
  database server mounts `NFS_HOST/<job>`, and NFS cannot mount a folder that does not exist yet (a new job, or one whose
  folder was removed). A mount that still fails reports mount's own error.
* **PostgreSQL checkpoint.** `pg_basebackup` starts with a checkpoint. PostgreSQL's default (spread) waits up to 90% of
  `checkpoint_timeout` (4.5 minutes with the default 5) before a single byte is copied, so the template asks for a fast
  checkpoint (`CHECKPOINT=fast`, a short burst of disk writes on the server). `CHECKPOINT=spread`, or your own
  `-c`/`--checkpoint` in EXTRA_OPTIONS, restores the gentle behaviour. The run page shows the wait as its own step.
* **PostgreSQL as the postgres user** runs through `as_postgres` (`runuser`, or `su -s /bin/bash`). It never uses the
  account's login shell, so it works where `postgres` has `/sbin/nologin` and no home folder.
* **MariaDB backup locks.** mariabackup waits at most 10 minutes for writes or DDL running over a minute
  (`--ftwrl-wait-timeout=600 --ftwrl-wait-threshold=60 --ftwrl-wait-query-type=update`), then gives up and the run
  fails with a clear message. Without this, a long-waiting `ALTER TABLE` (for example scheduled partition maintenance
  stuck behind an old open transaction) queues the backup's lock behind it. That queue blocks the application's
  inserts and can deadlock with the backup. When a run is stopped or fails, the template kills any `BACKUP STAGE` or
  `FLUSH TABLES` session it left behind: the server doesn't notice a dead client while the session waits on a lock.
  Your own `--ftwrl-wait*` or `--kill-long*` options in EXTRA_OPTIONS replace the default.
* **Updates.** The templates are embedded in the API. At startup a stored built-in template is replaced by the shipped
  file, as a new revision, only when its script is a version Backora shipped earlier (`shipped-versions.txt`). A
  template edited in Backora is left alone and logged. Changing a template file requires adding its new hash to
  `shipped-versions.txt`; `BuiltInTemplatesTests` fails until you do.

## Cleanup of failed runs

A template removes its own incomplete output when it fails (EXIT trap; SIGTERM from a timeout or cancel exits through
it). That is not enough on its own: Backora sends SIGKILL 10 seconds after SIGTERM, and deleting a large folder over
NFS takes longer; a lost process (reboot, OOM) cleans nothing. So every output path is also recorded in the run folder
(`add_partial` → `./partial`), the record is cleared when the run succeeds or its cleanup completed, and after any
failed, timed-out, cancelled or lost run the worker removes exactly the recorded paths (in the background on the
server) — only absolute paths without `..` whose name is `<job>-…`, the job's own output.

Copies recorded by Backora v1 (imported history) cannot be retried — v1 did not record their files — so a failed one is
shown as history under All, never under Failed or in alerts.

**Clearing failures.** Run history → Failed (and a failed run's page) and Transfers → Failed have *Clear* and *Clear all*.
For copies, *Clear* (one) or *Clear all* marks failures as dealt with
(`offload_runs.dismissed_at`/`dismissed_by`, audited as `offload.failures_cleared`): they leave the Failed list, alerts and
job health, so only new failures show there. Failed runs work the same way (`backup_runs.dismissed_at`, audited as
`run.failures_cleared`; they stop raising "last run failed"). Nothing is deleted — they stay in the history under All and
can still be retried. Cleared items are left out of every history list (runs, transfers, the overview) and kept
under the *Cleared* tab, where they can still be opened and retried.

**Notification emails** are written to the outbox when a copy fails its last attempt (not on attempts that are retried)
and sent by one replica at a time; each message is marked sent as soon as it is delivered, so a restart never sends it
again.

Verified backups are removed from the share only after their upload is verified; a backup whose upload keeps failing
stays on the share (it may be the only copy), so failed copies must be retried or the share fills.

## Live progress

A running backup shows a progress bar on its run page: what it is doing, percent done, bytes written of the expected
total, speed and the estimated finish time.

* **Templates report it** through `{run dir}/progress` — one line, `<bytes written> <bytes expected, 0 = unknown> <stage>`,
  replaced atomically. `_common.inc` provides `watch_progress TOTAL STAGE PATH…` (a background watcher that writes the size
  of the output every 10 s) and `watch_progress_with STAGE COMMAND…`; `stop_progress` ends it, and the EXIT trap always does.
  The helper only writes inside a `backora-run-*` folder, so running a template by hand leaves nothing behind.
* **Expected totals:** PostgreSQL physical uses `pg_basebackup -P` (exact; the `checkpoint` stage covers the wait before
  copying starts). MariaDB physical sums the data files in the datadir (binary, relay and redo logs excluded). Logical
  dumps use the table data size (indexes are not dumped). Estimates can be exceeded, so the page never shows 100% before
  the run has finished. Encrypting (`encrypt`) reports its own progress; compression on the worker reports `compress`.
* **Older and custom templates** that write no progress file still show movement: the worker reports the bytes written by
  the run's processes (`/proc/PID/io` wchar), with an indeterminate bar.
* **Every long step is tracked**, not only the copy: the pre-backup command (`pre-command`), the PostgreSQL checkpoint
  (`checkpoint`), the copy itself (`copy`/`dump`), mariabackup waiting for its backup lock behind long writes or DDL
  (`lock-wait`) and copying the last changes (`finishing`), the extra paths (`extra-files`), encrypting (`encrypt`)
  and the worker's compression (`compress`).
* **Verification** (SHA-256 of every file the backup produced) runs detached on the worker in
  `{work dir}/backora-verify-<run>` and reports the `verify` stage; inside a large file the progress is the bytes
  `sha256sum` has read (`/proc/PID/io` rchar). A worker restart adopts it like a backup; a cancelled run stops it.
* **Checksum speed:** files are hashed by a small python3 helper on the worker that reads each file with 8 parallel
  readers (16 MiB blocks, 12 in flight) and feeds the hashes in order: the same SHA-256 as `sha256sum`, plus the MD5 in
  the same pass. Measured on the execution host against the NFS share: one sequential reader gets ~260 MB/s, the
  helper ~740 MB/s (the CPU hashes SHA-256 at ~1 GB/s with SHA-NI). Without python3 the scripts fall back to `sha256sum`.
* **No second read for copies:** the MD5 is stored per artifact (`backup_artifacts.md5`; for client-side encrypted
  copies the ciphertext MD5 comes from the inspection pass). The copy check then compares it with the hash object
  storage keeps for each object (`rclone md5sum`, metadata only). If storage has no hash for an object, or an older
  artifact has no MD5, it falls back to `rclone check`, which reads the source. A hash that differs fails the copy.
* **Copies** record their own step with its start time (`progress_*` on `offload_runs`): `encrypt` (staged ciphertext
  size), `upload` (rclone stats), then, detached in `{copy dir}/check`, `inspect` (age header + SHA-256 of the staged
  ciphertext) and `check` (`rclone check`, progress = bytes rclone read). The run page shows each copy's step, amount,
  speed and time left.
* **The worker** reads the file on every poll and stores bytes, total, stage, a smoothed rate and the sample time on the
  run (`progress_*` columns). A minute without new bytes sets the rate to 0; after two minutes the page flags the run as
  not writing (it keeps running; the timeout rules above decide when it is stopped).

## Compression and checksums (on the worker)

The database servers only write the backup (raw, as the tool produces it) to the job's NFS folder. Compression runs on
the worker, as the first part of verification, so no database server spends CPU on it or needs zstd installed:

* **Only this run's own outputs.** The built-in templates record their outputs (`./outputs` in the run folder); the
  worker compresses exactly those. For templates that record nothing, only files written since the run started. A file an
  earlier backup recorded as its own is never touched, and an output that changes while it is compressed (something else
  still writing it) is kept as it is.
* **One file per output.** A file `F` becomes `F.zst`; a backup folder `D` (pg_basebackup's base.tar + pg_wal.tar +
  backup_manifest) is packed whole into ONE `D.tar.zst` (PAX tar). Already-compressed or encrypted outputs (`.zst`,
  `.gz`, `.gpg`, `.age`, …) stay as they are. A folder that also holds files older than the run is not this run's output
  alone and is left as it is.
* **Fast.** Each file is read with 8 parallel readers (one sequential NFS reader gets ~260 MB/s, eight ~1 GB/s) and
  compressed by `zstd -T0 -1` on all of the worker's cores (~2.6 GB/s, ~5x smaller on database backups).
* **Checksums of the compressed file, in the same pass.** The compressed stream is written through the hasher, which
  computes its SHA-256 and MD5 as it writes: verification does not read it again, and the copy check compares each
  object's MD5 in storage with it.
* **The original is removed only after.** The compressed file is written as `NAME.backora-partial`, fsynced, its size
  checked against what was written, and renamed; then the raw output is renamed `NAME.backora-delete` and deleted. A step
  that starts again (worker restart) finishes such a delete and drops unfinished `.backora-partial` files; a cancelled run
  drops them too. The raw output is never removed before its compressed file is complete.
* **Never fatal.** No zstd on the worker, not enough free space for the worst case (the compressed file as large as the
  original), or an error: that output stays uncompressed, the run log says why, and the backup is still verified.
* **Progress:** the `compress` step, bytes of the original read against its total, then `verify` for anything without
  checksums. The run log records each output's sizes, ratio, time and speed.
* **Turning it off:** variable `COMPRESSION__<job>` (or `COMPRESSION` for all jobs) set to `none`; default from
  `Worker:BackupCompression` (`zstd`).
* **Restore:** `zstd -dc FILE | mbstream -x` (MariaDB physical), `zstd -dc FILE | tar -x` (PostgreSQL physical, then the
  tar files into an empty data directory), `zstd -dc FILE | mysql` / `| psql` (dumps).

Uploads are not compressed again (`Worker:UploadCompression`, default `none`).

## Offload (S3) and integrity

1. rclone config is written over SFTP as a 0600 file inside a 0700 per-attempt directory (never on the command line; deleted afterwards). `files.txt` holds the manifest.
2. `rclone copy <nfs>/<job> backora:<bucket>/<prefix> --files-from files.txt --checksum --use-json-log --contimeout 30s --timeout 300s`. Progress comes from rclone JSON stats (real bytes; no fake percentages). The prefix is `<job>/<yyyy-MM-dd-HH>-r<offloadId>/`, so runs in the same hour never collide.
3. **Stall detection:** if transferred bytes do not advance for `Worker:TransferStallSeconds` (900 s), rclone is terminated and the attempt is retried with backoff (`Storage_outage_fails_attempt_then_recovers_on_retry`).
4. **Verification, twice:** `rclone check --one-way` (size plus hash), **and** an independent S3 API listing comparing every manifest key and size.
5. **Only then** are the manifest's files (never a wildcard) deleted from NFS, unless the job keeps local copies. A cleanup failure leaves the offload *Completed* with `CleanupStatus=Failed`, reported honestly.
6. Up to 3 attempts. A final failure produces a notification and a manual "Retry upload" action. Failure history is kept (legacy deleted failed rows and retried forever).

### Customer-key destinations (age client-side encryption)

When the destination is `CustomerKey` at the moment the offload's first attempt starts, the recipient and fingerprint are snapshotted onto the offload (later destination changes do not affect it).

1. `age` (≥ 1.1, `ClientEncryption:AgePath`) must exist on the worker host; otherwise the offload fails with `Configuration` and nothing is uploaded.
2. A detached script (`offload.sh`, 0700 work dir) runs `age -e -R recipients.txt -o stage/<rel>.age <nfs>/<rel>` for every manifest file, checks that each staged file starts with `age-encryption.org/v1`, then `rclone copy stage → <prefix>` with object keys `<rel>.age`. The stage directory only ever contains ciphertext; everything streams through `age`/`rclone` on the worker host (no data passes through .NET).
3. Verification: header + size + SHA-256 of every staged file (recorded on the offload as `encrypted_objects_json`), `rclone check --one-way` stage → bucket, and the S3 listing compared with the **ciphertext** sizes.
4. Only then: NFS plaintext cleanup (manifest files only), removal of the staged ciphertext, *Completed + Verified*. A failed or interrupted attempt is never completed; the retry uses a fresh attempt directory, re-encrypts and overwrites partial objects. Final failure removes the work dir.

Retention deletes by prefix and therefore handles `.age` keys unchanged. Restore pre-checks compare object sizes with the recorded ciphertext sizes.

## Storage sites: S3 buckets and NFS folders

A storage site (primary or DR) is either an **S3-compatible bucket** or an **NFS / folder** site: a folder on the
execution host, typically an NFS share mounted there (Storage → Add storage → *NFS / folder*). A folder site has no
endpoint or keys; only its path (and an optional sub-folder).

* **Copies, checks, restores** go through rclone's `local` backend exactly like a bucket: `rclone copy` into
  `<folder>/<sub-folder>/<job>/<point>/`, the copy check compares MD5s (`rclone md5sum`, which reads the copy back from
  the share), restores read from the folder. Client-side encryption works the same way.
* **Listing, retention deletes and browsing** run as shell commands on the execution host over SSH; every path is
  validated and quoted, and a delete removes one recovery point's folder, never the site's root or anything outside it.
* **Test access** checks that the folder exists, is a mount point (a warning otherwise: copies would land on the
  execution host's own disk), can be written, read and deleted, and has free space.
* **Allowed folders:** absolute, plain paths (letters, digits, `.`, `_`, `-`, `/`), not a system folder (`/etc`, `/usr`,
  …) and not overlapping the staging share the backups are written to (a copy there would not be a separate copy).
* **Moving DR jobs:** a site's page has *Move DR jobs to another site…*: every job copying to it copies to the chosen
  site from its next backup on (audited as `storage.dr_jobs_moved`). Copies already on the old site stay restorable
  until retention removes them.

## DR replication (S3 → S3, multiple destinations)

A job has one **primary** destination (the upload from NFS) and any number of **DR copy** destinations (`backup_job_replication_targets`). Each copy is itself a recovery point (`offload_runs` row with `source_offload_run_id` set), so it can be listed, protected, retained and **restored** exactly like the primary.

1. **Trigger.** When a primary upload is verified, the same transaction that marks it *Completed* inserts one queued copy per DR destination. Nothing is copied from an unverified source. Any completed point can also be copied manually (`POST /recovery-points/{id}/replicate`), for example to seed a new DR site with existing backups.
2. **Layout.** The copy's prefix mirrors the source under the DR destination's base prefix: `<dr-prefix>/<job>/<yyyy-MM-dd-HH>-r<id>/`. The DR bucket has the same structure as the primary.
3. **Transfer.** It runs as an ordinary leased offload operation on the worker node (leases, retries with backoff, stall detection, adoption after a worker restart). The rclone config holds two named remotes (`backora-src`, `backora-dst`) in a 0600 file. When source and target are the same storage account (same endpoint, region and credentials), a single remote is used so rclone performs an S3 **server-side copy** and no data flows through the worker. Otherwise data streams source → worker → DR, so the worker node must reach both endpoints.
4. **Verification, twice, on the DR side:** `rclone check --one-way` between source and target (size plus hash), plus an S3 API listing of the target compared with the SHA-256 manifest recorded at backup time. Legacy points without a manifest use the source listing.
5. **Safety.** At most one active transfer per (backup run, destination) is enforced by a unique index. A second copy of a point to the same destination is refused (`409`). Source and target must differ and must not resolve to the same bucket and prefix. A destination still used as a DR target cannot be deleted.
6. **Failure.** Up to 3 attempts, then *Failed* with a notification and a "Retry copy" action. A failed DR copy never affects the primary recovery point or NFS cleanup.

7. **Encryption.** Target `CustomerKey`, source unencrypted → `rclone cat` from the source piped through `age -e` into the stage, then upload and verify as above (no plaintext is written on the target). Source already encrypted to the **same** recipient → ciphertext copied unchanged. Source encrypted to a **different** recipient → fails with `Configuration` (re-encryption would need the private key). Encrypted source to an unencrypted target → ciphertext copied unchanged; the copy keeps the source's encryption metadata and needs the same key to restore.

Verified by `Dr_replication_copies_to_secondary_s3_and_restores_from_the_dr_copy`, which exercises both transfer paths and a restore from the DR copy with byte-for-byte comparison.

## Restore safety model

* Permission `restores.execute` (Restore Operator, Backup Administrator, Platform Administrator).
* **Pre-check** (`POST /restores/precheck`): recovery-point state, integrity at backup time, no concurrent restore, every object present with the expected size in S3, NFS writable, free space ≥ 105% of size, rclone present.
* **Authorization:** a reason (audited) and the exact typed phrase `restore <job>`. A mismatch is rejected and audited as *Denied*. `Idempotency-Key` prevents double submission.
* **Isolation:** files go to a new directory `NFS_PATH/<job>/restore-rp<id>-<timestamp>`. The target must not exist or must be empty, so Backora never overwrites data. Targets mount `NFS_HOST/<job>` at `NFS_PATH`, so restored files are visible on the server for the DBA. Database-level recovery remains a deliberate manual step, as in the legacy system.
* **Validation:** every restored file is SHA-256 compared against the manifest recorded at backup time. Silent corruption in storage is detected (`Corrupted_object_in_storage_is_detected_on_restore`). Legacy points without a manifest are verified against object-store checksums and marked *Partial*.
* **Encrypted recovery points:** the request must carry the matching private key (validated in the API before anything is created; `422` otherwise). The worker stages the restore and waits in `AwaitingKey`; the key reaches the worker host only through a 0600 FIFO (see `05-security.md`). Objects are streamed `rclone cat | age -d` into `<file>.partial` and renamed on success; the SHA-256 manifest check then proves the plaintext is byte-identical to what was backed up. An authentication failure (corrupted/tampered ciphertext) fails the restore with `VerificationFailed` and removes every restored file. The pre-check adds "Decryption key required" and "Decryption tool". The key is not supplied within `ClientEncryption:KeyRequestTimeoutSeconds` → the restore fails ("decryption key was not supplied in time") and the staged script and FIFO are removed.
* Restores are never executed against production databases by Backora and never automatically.

## Retention

`RetentionService` (cluster singleton, every 15 min) applies `RetentionPlanner` per job **and per destination**: the primary bucket and every DR bucket each keep their own minimum of verified points. A DR copy ages from when its source point was taken, so all copies of a backup expire together. A point is deleted only if **all** of the following hold:
- It is older than `RetentionDays` (fractional days honoured).
- It is not protected or on hold.
- No restore is using it, and no in-progress DR copy is reading from it.
- At least `MinRecoveryPointsToKeep` newer **verified** points remain.
- No other live point shares its prefix (relevant for imported legacy points).

Deletion uses the S3 API, verifies the prefix is empty, marks the point `Deleted` (history kept), and writes an audit event. Failures become `DeleteFailed` and are retried. Points kept only by the minimum floor show as `Expired`. Backups are full backups (the legacy scripts produce independent archives), so there are no incremental chains. The planner nevertheless supports a dependents map for future incremental support.

## Scheduling

`SchedulerService` runs under the `scheduler` cluster lock every 20 s.
- `next_run_at` is computed with Cronos in the job's IANA timezone (DST: skipped hour runs once, repeated hour runs once; see unit tests).
- A due occurrence creates one run with `scheduled_for`. The unique index `(job, scheduled_for)` makes double scheduling impossible even across lock handover.
- If the previous run is still active, the occurrence is skipped (legacy behaviour).
- Missed occurrences (downtime) run once if within the job's grace window (default 60 min, policy `RunOnceWithinGrace`), otherwise they are logged as missed. Several missed occurrences collapse into one.
- Pause/resume is done with job status. Manual runs are always possible.

## Notifications

State changes write `notification_messages` (outbox) in the same transaction. The `notifications` singleton sends them:
- **Email:** MailKit, certificate validation always on, HTML-encoded placeholders.
- **Microsoft Teams:** HTTPS only, no redirects, JSON-escaped placeholders, validated JSON.
- **Retries:** exponential backoff, up to 5 attempts. Unconfigured channels are marked *Skipped*.

The legacy `{{placeholders}}` and templates remain compatible.
