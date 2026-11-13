#!/bin/bash
# @template name="PostgreSQL · Physical" category="PostgreSQL" description="Full cluster backup with pg_basebackup (as the postgres user) in tar format (base.tar + pg_wal.tar) in the job's NFS folder; Backora's worker then packs and compresses the whole folder into ONE .tar.zst file with its checksums (restore: zstd -dc FILE | tar -x, then the tar files into an empty data directory)."
# @setting PG_HOST "Database host" help="Leave empty to connect over the local socket as the postgres user (peer)."
# @setting PG_PORT "Port" help="Default 5432."
# @setting PG_USER "Database user" help="Default postgres."
# @setting PG_PASSWORD "Password" secret help="Only needed with PG_HOST."
# @setting PG_BIN_DIR "PostgreSQL bin directory" help="For example /usr/pgsql-16/bin. Empty: found in PATH."
# @setting OUTPUT_OWNER "Owner of the NFS folder" default=postgres:postgres help="The postgres user writes the backup into it."
# @setting EXTRA_OPTIONS "Extra pg_basebackup options" default="-X stream"
# @setting CHECKPOINT "Checkpoint" default=fast help="fast (default): copying starts at once, with a short burst of disk writes on the server. spread: PostgreSQL spreads the checkpoint over up to 90% of checkpoint_timeout (4.5 minutes with the default 5) before copying starts."
# @setting CONTAINER "Container" help="Docker/Podman container that runs the database. Empty: found automatically when the tool is not installed on the server."
# @setting CONTAINER_ENGINE "Container engine" help="docker or podman. Empty: detected."
# @setting PRE_COMMAND "Run before the backup" help="A shell command run on the server first; a failure is logged, not fatal."
# @setting EXTRA_PATHS "Also back up these paths" help="Space-separated files or folders, saved as <job>-files-<time>.tar."
set -euo pipefail
#@include _common.inc
resolve PG_HOST HOST; resolve PG_PORT PORT; resolve PG_USER USER; resolve PG_PASSWORD PASSWORD

log INFO "PostgreSQL physical backup for $JOB"
pg_tool pg_basebackup
find_container "$([[ -n "$PG_TOOL" ]] && echo 1 || echo 0)" 'postgres|postgis|timescale|patroni|spilo'
in_container || [[ -n "$PG_TOOL" ]] || fail "pg_basebackup is not installed and no database container was found"
mount_nfs
run_pre_command
pg_connection

BACKUP_DIR="$NFS_PATH/$JOB-$TIMESTAMP"
# -P makes pg_basebackup report exact progress ("<done>/<total> kB") on stderr; it goes to a file the progress watcher
# reads, and anything else pg_basebackup says is copied to the log afterwards.
PG_ERR="$(mktemp)"; TMPFILES+=("$PG_ERR")
basebackup_progress() {
    local kb; kb="$(tr '\r' '\n' < "$PG_ERR" | grep -o '[0-9]\+/[0-9]\+ kB' | tail -n 1)"
    if [[ -n "$kb" ]]; then awk -F'[/ ]' '{ printf "%.0f %.0f", $1 * 1024, $2 * 1024 }' <<< "$kb"
    elif grep -q 'checkpoint' "$PG_ERR"; then echo "0 0 checkpoint"; fi   # the server first finishes a checkpoint
}
basebackup_messages() { tr '\r' '\n' < "$PG_ERR" | grep -v ' kB (' | grep -v '^[[:space:]]*$' || true; }
basebackup_failed() { stop_progress; basebackup_messages | tail -n 20 >&2; fail "pg_basebackup failed"; }
# The backup starts with a checkpoint. Spread (PostgreSQL's default) waits up to 90% of checkpoint_timeout before a single
# byte is copied; fast starts at once. An explicit -c/--checkpoint in EXTRA_OPTIONS wins.
case "${CHECKPOINT:-fast}" in fast|spread) CK=(-c "${CHECKPOINT:-fast}") ;; *) fail "CHECKPOINT must be fast or spread, not '$CHECKPOINT'" ;; esac
if [[ " ${EXTRA_OPTIONS:-} " =~ \ (-c|--checkpoint)[\ =] ]]; then CK=(); fi
[[ ${#CK[@]} -eq 0 ]] || log INFO "Checkpoint: ${CHECKPOINT:-fast}"
watch_progress_with copy basebackup_progress
if in_container; then
    # Streamed out of the container as one tar (WAL included with -X fetch) straight into the job folder: the NFS share
    # never has to be mounted inside the container, and nothing is unpacked and packed again.
    BACKUP_FILE="$BACKUP_DIR.tar"; add_partial "$BACKUP_FILE"
    OPTS="${EXTRA_OPTIONS:-}"; [[ "$OPTS" != "-X stream" ]] || OPTS=""
    PGENV_ARGS=(); if [[ -n "$PG_PASSWORD" ]]; then PGENV_ARGS=(-e PGPASSWORD); fi
    # shellcheck disable=SC2086
    "$CTR_ENGINE" exec "${PGENV_ARGS[@]+"${PGENV_ARGS[@]}"}" -u postgres "$CTR" "${PG_BIN_DIR:+${PG_BIN_DIR%/}/}pg_basebackup" "${PG_CONN[@]}" -D - -F t -X fetch -P "${CK[@]+"${CK[@]}"}" $OPTS \
        > "$BACKUP_FILE" 2> "$PG_ERR" || basebackup_failed
    stop_progress; basebackup_messages
    # pg_basebackup sends backup_label first; read only that header (listing a huge tar would read all of it).
    [[ "$(head -c 100 "$BACKUP_FILE" | tr -d '\0')" == "backup_label" ]] || fail "pg_basebackup did not produce a complete backup in $BACKUP_FILE"
    archive "$BACKUP_FILE"
else
    add_partial "$BACKUP_DIR"
    # Only the folder the backup is written into needs the owner (not every older archive on the share).
    chown "${OUTPUT_OWNER:-postgres:postgres}" "$NFS_PATH" || fail "Cannot change the owner of $NFS_PATH"
    # Tar format (-F t): pg_basebackup writes base.tar and pg_wal.tar as two sequential files, instead of a copy of the
    # data directory (tens of thousands of small files on NFS) that then has to be read back into a tar and deleted.
    # -F p in EXTRA_OPTIONS still works (it comes last) and is then archived as before.
    as_postgres "${PG_ENV}$(printf '%q ' "$PG_TOOL" "${PG_CONN[@]}" -D "$BACKUP_DIR" -F t -P "${CK[@]+"${CK[@]}"}") ${EXTRA_OPTIONS:-}" </dev/null 2> "$PG_ERR" || basebackup_failed
    stop_progress; basebackup_messages
    [[ -f "$BACKUP_DIR/backup_label" || -f "$BACKUP_DIR/base.tar" || -f "$BACKUP_DIR/base.tar.gz" ]] \
        || fail "pg_basebackup did not produce a complete backup in $BACKUP_DIR"
    # The whole folder (base.tar, pg_wal.tar, backup_manifest) becomes ONE compressed file on the worker, as the next step.
    archive "$BACKUP_DIR"
fi
archive_extra_paths
