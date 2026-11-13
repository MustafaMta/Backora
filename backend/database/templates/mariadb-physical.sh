#!/bin/bash
# @template name="MariaDB · Physical" category="MariaDB" description="Full physical backup with mariabackup, streamed into one .xbstream file in the job's NFS folder, which Backora's worker then compresses into one .xbstream.zst file with its checksums (restore: zstd -dc FILE | mbstream -x, then mariabackup --prepare)."
# @setting MYSQL_HOST "Database host" help="Leave empty to use the local socket."
# @setting MYSQL_PORT "Port" help="Default 3306."
# @setting MYSQL_USER "Database user" help="Usually set once for all jobs (backup_usr)."
# @setting MYSQL_PASSWORD "Database password" secret help="Leave empty for a password-less local user."
# @setting EXTRA_OPTIONS "Extra mariabackup options" help="Added to the mariabackup command. Defaults: --parallel=4 and a 10-minute wait for long writes/DDL before the backup lock (--ftwrl-wait-*); giving your own value replaces that default."
# @setting CONTAINER "Container" help="Docker/Podman container that runs the database. Empty: found automatically when the tool is not installed on the server."
# @setting CONTAINER_ENGINE "Container engine" help="docker or podman. Empty: detected."
# @setting PRE_COMMAND "Run before the backup" help="A shell command run on the server first; a failure is logged, not fatal."
# @setting EXTRA_PATHS "Also back up these paths" help="Space-separated files or folders, saved as <job>-files-<time>.tar."
set -euo pipefail
#@include _common.inc
resolve MYSQL_HOST HOST; resolve MYSQL_PORT PORT; resolve MYSQL_USER USER; resolve MYSQL_PASSWORD PASSWORD

log INFO "MariaDB physical backup for $JOB"
BIN="$(command -v mariabackup || command -v mariadb-backup || true)"
find_container "$([[ -n "$BIN" ]] && echo 1 || echo 0)" 'mariadb|mysql|percona'
in_container || [[ -n "$BIN" ]] || fail "mariabackup is not installed and no database container was found"
mount_nfs
run_pre_command

BACKUP_FILE="$NFS_PATH/$JOB-$TIMESTAMP.xbstream"
LOG_FILE="/tmp/$JOB-$TIMESTAMP.log"
add_partial "$BACKUP_FILE"
ulimit -n 500000 2>/dev/null || log WARN "Could not raise the open-files limit"

# One sequential stream into one file: no folder of thousands of files on NFS, no second copy into a tar, no rm -rf.
# Parallel copy threads speed up reading the data files.
OPTS=(--backup --stream=xbstream --target-dir=/tmp)
[[ " ${EXTRA_OPTIONS:-} " == *" --parallel"* ]] || OPTS+=(--parallel=4)
# Never queue for the backup lock behind long-running writes or DDL (e.g. a partition ALTER waiting on an old
# transaction): that queue blocks the application's inserts and can deadlock with the backup. Wait up to 10 minutes
# for writes running over a minute to finish, then give up cleanly; the run fails with a clear message and retries.
if [[ " ${EXTRA_OPTIONS:-} " != *" --ftwrl-wait"* && " ${EXTRA_OPTIONS:-} " != *" --kill-long"* ]]; then
    OPTS+=(--ftwrl-wait-timeout=600 --ftwrl-wait-threshold=60 --ftwrl-wait-query-type=update)
fi

# sql 'QUERY' — runs a query with the backup's own credentials (host client or inside the container).
sql() {
    if in_container; then
        # shellcheck disable=SC2016
        "$CTR_ENGINE" exec "${CTR_ENV[@]+"${CTR_ENV[@]}"}" "$CTR" sh -c 'exec "$(command -v mariadb || command -v mysql)" "$@"' sql \
            "${MYSQL_CTR_ARGS[@]+"${MYSQL_CTR_ARGS[@]}"}" --connect-timeout=10 -N -e "$1"
    else
        local client; client="$(command -v mariadb || command -v mysql || true)"; [[ -n "$client" ]] || return 1
        HOME="$MYSQL_HOME_DIR" "$client" "${MYSQL_ARGS[@]+"${MYSQL_ARGS[@]}"}" --connect-timeout=10 -N -e "$1"
    fi
}
# A backup that is stopped (cancel, timeout, failure) can leave its server session waiting on BACKUP STAGE / FTWRL:
# the server does not notice the client is gone while the session waits on a lock, and the waiting lock keeps blocking
# the application. Kill those sessions of this user that started during this run.
MB_STARTED=""
on_failure() {
    [[ -n "$MB_STARTED" ]] || return 0
    local ids id
    ids="$(sql "select id from information_schema.processlist where user = substring_index(current_user(), '@', 1) and id <> connection_id() and (info like 'BACKUP STAGE%' or info like 'FLUSH TABLE%') and time <= $(( SECONDS - MB_STARTED + 5 ))" 2>/dev/null)" || return 0
    for id in $ids; do
        [[ "$id" =~ ^[0-9]+$ ]] || continue
        sql "KILL $id" >/dev/null 2>&1 && log WARN "Released backup lock session $id left by the stopped backup"
    done
}

# Expected size for the progress bar: the data files mariabackup copies (binary and relay logs and the redo log are not).
expected_size() {
    local dd bin pat='-' row
    row="$(sql "select @@datadir, coalesce(@@log_bin_basename, '')" 2>/dev/null | head -n 1)" || return 0
    dd="${row%%$'\t'*}"; bin="${row#*$'\t'}"; [[ "$row" == *$'\t'* ]] || bin=""
    [[ -n "$dd" ]] || return 0
    if [[ -n "$bin" ]]; then pat="${bin##*/}.*"; fi
    local cmd=(find "$dd" -type f ! -name "$pat" ! -name '*relay-bin*' ! -name 'ib_logfile*' -printf '%s\n')
    if in_container; then "$CTR_ENGINE" exec "$CTR" "${cmd[@]}" 2>/dev/null; else "${cmd[@]}" 2>/dev/null; fi | awk '{ s += $1 } END { printf "%.0f", s }'
}

# Progress: bytes streamed so far, plus what mariabackup is doing once the data files are copied — waiting for the
# backup lock behind long writes/DDL (lock-wait, at most the --ftwrl-wait-timeout) or copying the last changes (finishing).
MB_TOTAL=0
mb_progress() {
    local stage="" last
    last="$(tail -n 2 "$LOG_FILE" 2>/dev/null || true)"
    case "$last" in
        *"Waiting for query"*|*"Waiting for table"*|*"Waiting for "*" lock"*) stage=lock-wait ;;
        *"BACKUP STAGE"*|*"non-InnoDB"*|*"Acquiring BACKUP LOCKS"*|*"Executing FLUSH"*|*"Executing BACKUP"*|*"Starting to backup"*|*"Writing "*|*"Redo log"*) stage=finishing ;;
    esac
    echo "$(bytes_of "$BACKUP_FILE") $MB_TOTAL${stage:+ $stage}"
}

if in_container; then
    mysql_container_args
    CBIN="$("$CTR_ENGINE" exec "$CTR" sh -c 'command -v mariabackup || command -v mariadb-backup' 2>/dev/null | tail -n 1 || true)"
    [[ -n "$CBIN" ]] || fail "mariabackup is not installed in the container $CTR"
    log INFO "Running $(basename "$CBIN") in container $CTR"
    MB_TOTAL="$(expected_size || true)"; [[ "$MB_TOTAL" =~ ^[0-9]+$ ]] || MB_TOTAL=0
    watch_progress_with copy mb_progress
    MB_STARTED=$SECONDS
    # shellcheck disable=SC2086
    "$CTR_ENGINE" exec "${CTR_ENV[@]+"${CTR_ENV[@]}"}" "$CTR" "$CBIN" "${OPTS[@]}" "${MYSQL_CTR_ARGS[@]+"${MYSQL_CTR_ARGS[@]}"}" ${EXTRA_OPTIONS:-} \
        > "$BACKUP_FILE" 2> "$LOG_FILE" || MB_RC=$?
else
    mysql_credentials
    log INFO "Running $(basename "$BIN")"
    MB_TOTAL="$(expected_size || true)"; [[ "$MB_TOTAL" =~ ^[0-9]+$ ]] || MB_TOTAL=0
    watch_progress_with copy mb_progress
    MB_STARTED=$SECONDS
    # shellcheck disable=SC2086
    HOME="$MYSQL_HOME_DIR" "$BIN" "${OPTS[@]}" "${MYSQL_ARGS[@]+"${MYSQL_ARGS[@]}"}" ${EXTRA_OPTIONS:-} > "$BACKUP_FILE" 2> "$LOG_FILE" || MB_RC=$?
fi
stop_progress
if [[ -n "${MB_RC:-}" ]]; then
    tail -n 20 "$LOG_FILE" >&2
    if grep -q "Unable to obtain lock" "$LOG_FILE"; then
        # Name the statement that held the backup up, so whoever reads the alert knows exactly what to act on.
        BLOCKER="" W_ID="" W_SEC="" W_SQL=""
        W_LINE="$(grep -o 'Waiting for query [0-9]* (duration [0-9]* sec): .*' "$LOG_FILE" | tail -n 1 || true)"
        if [[ "$W_LINE" =~ query\ ([0-9]+)\ \(duration\ ([0-9]+)\ sec\):\ (.*)$ ]]; then
            W_ID="${BASH_REMATCH[1]}"; W_SEC="${BASH_REMATCH[2]}"; W_SQL="${BASH_REMATCH[3]:0:200}"
            BLOCKER=" Blocking statement: id $W_ID, running $(( W_SEC / 3600 ))h $(( W_SEC % 3600 / 60 ))m: $W_SQL."
        fi
        fail "The backup lock could not be taken: a long-running write or DDL kept the database busy past the wait limit, and the backup gave up without blocking it.${BLOCKER} It has to finish or be stopped on the server (KILL ${W_ID:-<id>}) before a consistent backup is possible (log: $LOG_FILE)"
    fi
    fail "mariabackup failed (log: $LOG_FILE)"
fi
grep -q "completed OK" "$LOG_FILE" || { tail -n 20 "$LOG_FILE" >&2; fail "mariabackup did not report 'completed OK' (log: $LOG_FILE)"; }
[[ -s "$BACKUP_FILE" ]] || fail "The backup stream is empty: $BACKUP_FILE"
MB_STARTED=""
archive "$BACKUP_FILE"
archive_extra_paths
