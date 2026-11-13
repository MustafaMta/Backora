#!/bin/bash
# @template name="MariaDB · Logical" category="MariaDB" description="SQL dump with mysqldump (all databases or a list) to the job's NFS folder as one .sql file, which Backora's worker then compresses into one .sql.zst file with its checksums (restore: zstd -dc FILE | mysql)."
# @setting MYSQL_HOST "Database host" help="Leave empty to use the local socket."
# @setting MYSQL_PORT "Port" help="Default 3306."
# @setting MYSQL_USER "Database user" help="Usually set once for all jobs (backup_usr)."
# @setting MYSQL_PASSWORD "Database password" secret help="Leave empty for a password-less local user."
# @setting DATABASES "Databases" help="Space-separated database names. Leave empty to dump all databases."
# @setting DUMP_OPTIONS "mysqldump options" default="--single-transaction" help="For example --ignore-table=db.table."
# @setting CONTAINER "Container" help="Docker/Podman container that runs the database. Empty: found automatically when the tool is not installed on the server."
# @setting CONTAINER_ENGINE "Container engine" help="docker or podman. Empty: detected."
# @setting PRE_COMMAND "Run before the backup" help="A shell command run on the server first; a failure is logged, not fatal."
# @setting EXTRA_PATHS "Also back up these paths" help="Space-separated files or folders, saved as <job>-files-<time>.tar."
set -euo pipefail
#@include _common.inc
resolve MYSQL_HOST HOST; resolve MYSQL_PORT PORT; resolve MYSQL_USER USER; resolve MYSQL_PASSWORD PASSWORD

log INFO "MariaDB logical dump for $JOB"
BIN="$(command -v mysqldump || command -v mariadb-dump || true)"
find_container "$([[ -n "$BIN" ]] && echo 1 || echo 0)" 'mariadb|mysql|percona'
in_container || [[ -n "$BIN" ]] || fail "mysqldump is not installed and no database container was found"
mount_nfs
run_pre_command

DUMP_FILE="$NFS_PATH/$JOB-$TIMESTAMP.sql"
LOG_FILE="/tmp/$JOB-$TIMESTAMP.log"
NAMES=(); read -r -a NAMES <<< "${DATABASES:-}"
if (( ${#NAMES[@]} > 0 )); then TARGETS=(--databases "${NAMES[@]}"); else TARGETS=(--all-databases); fi
add_partial "$DUMP_FILE"
ulimit -n 500000 2>/dev/null || true
log INFO "Dumping ${DATABASES:-all databases}"
# Expected size for the progress bar: the table data being dumped (indexes are not part of a dump).
expected_size() {
    local where="table_schema not in ('information_schema','performance_schema','sys','mysql')" n list=""
    if (( ${#NAMES[@]} > 0 )); then
        for n in "${NAMES[@]}"; do list+="${list:+,}'${n//\'/\'\'}'"; done
        where="table_schema in ($list)"
    fi
    local q="select coalesce(sum(data_length), 0) from information_schema.tables where $where"
    if in_container; then
        # shellcheck disable=SC2016
        "$CTR_ENGINE" exec "${CTR_ENV[@]+"${CTR_ENV[@]}"}" "$CTR" sh -c 'exec "$(command -v mariadb || command -v mysql)" "$@"' sql \
            "${MYSQL_CTR_ARGS[@]+"${MYSQL_CTR_ARGS[@]}"}" --connect-timeout=10 -N -e "$q" 2>/dev/null
    else
        local client; client="$(command -v mariadb || command -v mysql || true)"; [[ -n "$client" ]] || return 0
        HOME="$MYSQL_HOME_DIR" "$client" "${MYSQL_ARGS[@]+"${MYSQL_ARGS[@]}"}" --connect-timeout=10 -N -e "$q" 2>/dev/null
    fi
}
if in_container; then
    mysql_container_args
    watch_progress "$(expected_size || true)" dump "$DUMP_FILE"
    # shellcheck disable=SC2016,SC2086
    "$CTR_ENGINE" exec "${CTR_ENV[@]+"${CTR_ENV[@]}"}" "$CTR" sh -c 'exec "$(command -v mysqldump || command -v mariadb-dump)" "$@"' dump \
        "${MYSQL_CTR_ARGS[@]+"${MYSQL_CTR_ARGS[@]}"}" ${DUMP_OPTIONS:-} "${TARGETS[@]}" > "$DUMP_FILE" 2> "$LOG_FILE" \
        || { tail -n 20 "$LOG_FILE" >&2; fail "mysqldump failed (log: $LOG_FILE)"; }
else
    mysql_credentials
    watch_progress "$(expected_size || true)" dump "$DUMP_FILE"
    # shellcheck disable=SC2086
    HOME="$MYSQL_HOME_DIR" "$BIN" "${MYSQL_ARGS[@]+"${MYSQL_ARGS[@]}"}" ${DUMP_OPTIONS:-} "${TARGETS[@]}" > "$DUMP_FILE" 2> "$LOG_FILE" \
        || { tail -n 20 "$LOG_FILE" >&2; fail "mysqldump failed (log: $LOG_FILE)"; }
fi
stop_progress
[[ -s "$DUMP_FILE" ]] || fail "Dump file is empty: $DUMP_FILE"
if [[ " ${DUMP_OPTIONS:-} " != *" --skip-comments "* && " ${DUMP_OPTIONS:-} " != *" --compact "* ]]; then
    tail -c 200 "$DUMP_FILE" | grep -q "Dump completed" || fail "The dump is incomplete (no 'Dump completed' trailer): $DUMP_FILE"
fi
archive "$DUMP_FILE"
archive_extra_paths
