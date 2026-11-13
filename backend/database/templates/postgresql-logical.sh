#!/bin/bash
# @template name="PostgreSQL · Logical" category="PostgreSQL" description="SQL dump of the whole cluster with pg_dumpall (as the postgres user) to the job's NFS folder, optionally gpg-encrypted (.sql or .sql.gpg); Backora's worker compresses a plain dump into one .sql.zst file with its checksums (restore: zstd -dc FILE | psql)."
# @setting PG_HOST "Database host" help="Leave empty to connect over the local socket as the postgres user (peer)."
# @setting PG_PORT "Port" help="Default 5432."
# @setting PG_USER "Database user" help="Default postgres."
# @setting PG_PASSWORD "Password" secret help="Only needed with PG_HOST."
# @setting PG_BIN_DIR "PostgreSQL bin directory" help="For example /usr/pgsql-16/bin. Empty: found in PATH."
# @setting ENCRYPT_PASSPHRASE "Encryption passphrase" secret help="If set, the dump (and the extra paths) are encrypted with gpg (AES-256) before archiving."
# @setting CONTAINER "Container" help="Docker/Podman container that runs the database. Empty: found automatically when the tool is not installed on the server."
# @setting CONTAINER_ENGINE "Container engine" help="docker or podman. Empty: detected."
# @setting PRE_COMMAND "Run before the backup" help="A shell command run on the server first; a failure is logged, not fatal."
# @setting EXTRA_PATHS "Also back up these paths" help="Space-separated files or folders, saved as <job>-files-<time>.tar."
set -euo pipefail
#@include _common.inc
resolve PG_HOST HOST; resolve PG_PORT PORT; resolve PG_USER USER; resolve PG_PASSWORD PASSWORD

log INFO "PostgreSQL logical dump for $JOB"
pg_tool pg_dumpall
find_container "$([[ -n "$PG_TOOL" ]] && echo 1 || echo 0)" 'postgres|postgis|timescale|patroni|spilo'
in_container || [[ -n "$PG_TOOL" ]] || fail "pg_dumpall is not installed and no database container was found"
mount_nfs
run_pre_command
pg_connection

DUMP_FILE="$NFS_PATH/$JOB-$TIMESTAMP.sql"
add_partial "$DUMP_FILE"
# Expected size for the progress bar: the table data of every database pg_dumpall writes (indexes are not dumped).
expected_size() {
    local db n total=0
    for db in $(pg_sql postgres "select datname from pg_database where datallowconn and not datistemplate and has_database_privilege(oid, 'CONNECT')"); do
        n="$(pg_sql "$db" "select coalesce(sum(pg_table_size(c.oid)), 0) from pg_class c join pg_namespace s on s.oid = c.relnamespace where c.relkind in ('r', 'm') and s.nspname not in ('pg_catalog', 'information_schema')")" || continue
        [[ "$n" =~ ^[0-9]+$ ]] && total=$(( total + n ))
    done
    echo "$total"
}
watch_progress "$(expected_size || true)" dump "$DUMP_FILE"
# The dump is written by this (root) shell, so the postgres user needs no access to the share.
if in_container; then
    PGENV_ARGS=(); if [[ -n "$PG_PASSWORD" ]]; then PGENV_ARGS=(-e PGPASSWORD); fi
    "$CTR_ENGINE" exec "${PGENV_ARGS[@]+"${PGENV_ARGS[@]}"}" -u postgres "$CTR" "${PG_BIN_DIR:+${PG_BIN_DIR%/}/}pg_dumpall" "${PG_CONN[@]}" > "$DUMP_FILE" || fail "pg_dumpall failed"
else
    as_postgres "${PG_ENV}$(printf '%q ' "$PG_TOOL" "${PG_CONN[@]}")" </dev/null > "$DUMP_FILE" || fail "pg_dumpall failed"
fi
stop_progress
[[ -s "$DUMP_FILE" ]] || fail "Dump file is empty: $DUMP_FILE"
tail -c 300 "$DUMP_FILE" | grep -q "PostgreSQL database cluster dump complete" || fail "The dump is incomplete: $DUMP_FILE"

if [[ -n "${ENCRYPT_PASSPHRASE:-}" ]]; then
    log INFO "Encrypting the dump (gpg, AES-256)"
    encrypt "$DUMP_FILE"; DUMP_FILE="$ENCRYPTED"
fi
archive "$DUMP_FILE"
archive_extra_paths
