# ---- common (inlined into every template by the consolidation tool; do not edit separately) ----
# Works with bash 4.2+ (RHEL/CentOS 7 and newer). Connection settings arrive as exported variables from Backora's
# app environment (BACKORA__<SETTING>__<JOB> / BACKORA__<SETTING>); see resolve() for the server-side fallback.
log()  { echo "$(date +'%Y-%m-%d %T') - $1 - $2"; }
fail() { log ERROR "$1" >&2; exit 1; }
JOB="{JOB_NAME}"
NFS_PATH="{NFS_PATH}"
NFS_PATH="${NFS_PATH%/}"
NFS_SOURCE="{NFS_HOST}/{JOB_NAME}"
TIMESTAMP="$(date +%Y-%m-%d_%H)"
STARTED=$SECONDS

# Temporary files are always removed; work in progress (PARTIAL) only when the script fails, so a retry starts
# clean and no half-written backup is ever left on the NFS share for the offload to pick up.
TMPFILES=(); PARTIAL=()
# Every output path is also recorded in the run folder (./partial): when the script is killed before its own cleanup
# can finish (a timeout or cancel ends in SIGKILL; a large folder takes long to delete over NFS) or the process is
# lost, Backora removes exactly those paths itself. A successful run clears the record, so good backups stay.
PARTIAL_RECORD=""
if [[ "$PWD" == */backora-run-* && -w "$PWD" ]]; then PARTIAL_RECORD="$PWD/partial"; fi
add_partial() {
    PARTIAL+=("$1")
    if [[ -n "$PARTIAL_RECORD" ]]; then printf '%s\n' "$1" >> "$PARTIAL_RECORD" 2>/dev/null || true; fi
}
cleanup() {
    local rc=$?
    stop_progress
    # A template may define on_failure (e.g. to release database locks); it runs before the temporary files it may need go.
    if (( rc != 0 )) && declare -F on_failure >/dev/null; then on_failure || true; fi
    if (( ${#TMPFILES[@]} > 0 )); then rm -rf "${TMPFILES[@]}"; fi
    if (( rc != 0 && ${#PARTIAL[@]} > 0 )); then rm -rf "${PARTIAL[@]}"; log WARN "Removed incomplete output after the failure"; fi
    # Nothing left to clean up: the outputs are either complete (success) or gone (removed just above). On success the
    # record becomes ./outputs: Backora's worker then compresses exactly these outputs and nothing else in the folder.
    if [[ -n "$PARTIAL_RECORD" ]]; then
        if (( rc == 0 )) && [[ -s "$PARTIAL_RECORD" ]]; then mv -f "$PARTIAL_RECORD" "${PARTIAL_RECORD%/partial}/outputs" 2>/dev/null || rm -f "$PARTIAL_RECORD"
        else rm -f "$PARTIAL_RECORD"; fi
    fi
}
trap cleanup EXIT
# Backora stops a run (cancel, timeout) with SIGTERM to the whole process group: exit so the EXIT trap still runs.
trap 'exit 143' TERM; trap 'exit 130' INT; trap 'exit 129' HUP

# ---- progress ----
# Backora shows a live progress bar from ./progress in the run folder (the script runs there): "<bytes written>
# <bytes expected, 0 = unknown> <stage>". A background watcher refreshes it every 10 seconds while a step runs.
PROGRESS_FILE=""; PROGRESS_PID=""
if [[ "$PWD" == */backora-run-* && -w "$PWD" ]]; then PROGRESS_FILE="$PWD/progress"; fi
progress() { # progress DONE TOTAL STAGE
    [[ -n "$PROGRESS_FILE" ]] || return 0
    printf '%s %s %s\n' "${1:-0}" "${2:-0}" "${3:-}" > "$PROGRESS_FILE.tmp" 2>/dev/null && mv -f "$PROGRESS_FILE.tmp" "$PROGRESS_FILE" 2>/dev/null || true
}
bytes_of() { du -sb "$@" 2>/dev/null | awk '{ s += $1 } END { printf "%.0f", s }'; }
size_against() { local total="$1"; shift; echo "$(bytes_of "$@") $total"; }
# watch_progress_with STAGE COMMAND... — reports what COMMAND prints ("<done> <total> [stage]") every 10 s until stop_progress.
watch_progress_with() {
    [[ -n "$PROGRESS_FILE" ]] || return 0
    local stage="$1"; shift
    stop_progress
    ( trap - EXIT TERM INT HUP
      while :; do
          v="$("$@" 2>/dev/null)" || v=""
          if [[ "$v" =~ ^([0-9]+)\ ([0-9]+)(\ ([a-z-]+))?$ ]]; then progress "${BASH_REMATCH[1]}" "${BASH_REMATCH[2]}" "${BASH_REMATCH[4]:-$stage}"; fi
          sleep "${BACKORA_PROGRESS_INTERVAL:-10}"
      done
    ) </dev/null >/dev/null 2>&1 &
    PROGRESS_PID=$!
}
# watch_progress TOTAL STAGE PATH... — reports the size of PATH (a file or folder being written) against TOTAL.
watch_progress() {
    local total="${1:-0}" stage="$2"; shift 2
    [[ "$total" =~ ^[0-9]+$ ]] || total=0
    watch_progress_with "$stage" size_against "$total" "$@"
}
stop_progress() {
    if [[ -n "${PROGRESS_PID:-}" ]]; then kill "$PROGRESS_PID" 2>/dev/null || true; wait "$PROGRESS_PID" 2>/dev/null || true; PROGRESS_PID=""; fi
    return 0
}

# Server-side values (the convention of the original "-ENVs" scripts): BACKORA__<KEY>__<job> / BACKORA__<KEY>, read from this
# shell or, as those scripts saw them, from a login shell (/etc/profile.d, ~/.bash_profile), loaded at most once per run.
LOGIN_ENV=""; LOGIN_ENV_LOADED=0; SERVER_VALUE=""
server_env() {
    SERVER_VALUE="$(printenv "$1" 2>/dev/null || true)"
    [[ -z "$SERVER_VALUE" ]] || return 0
    if (( LOGIN_ENV_LOADED == 0 )); then
        LOGIN_ENV_LOADED=1
        LOGIN_ENV="$(timeout 15 bash -lc 'echo "<<BACKORA-ENV>>"; env' </dev/null 2>/dev/null | sed -n '/^<<BACKORA-ENV>>$/,$p' | grep '^BACKORA__' || true)"
    fi
    SERVER_VALUE="$(printf '%s\n' "$LOGIN_ENV" | awk -v k="$1=" 'index($0, k) == 1 { print substr($0, length(k) + 1); exit }')"
}

# resolve VAR KEY — final value of a connection setting. Order: a Backora variable for this job (VAR__<JOB>), the server's
# own BACKORA__<KEY>__<job>, a Backora variable for all jobs (VAR; listed in BACKORA_ALL_JOBS), the server's BACKORA__<KEY>.
resolve() {
    local var="$1" key="$2"
    if [[ -n "${!var:-}" && " ${BACKORA_ALL_JOBS:-} " != *" $var "* ]]; then return 0; fi
    server_env "BACKORA__${key}__$JOB"
    if [[ -n "$SERVER_VALUE" ]]; then printf -v "$var" '%s' "$SERVER_VALUE"; return 0; fi
    if [[ -n "${!var:-}" ]]; then return 0; fi
    server_env "BACKORA__$key"
    printf -v "$var" '%s' "$SERVER_VALUE"
}

# ---- containers (Docker or Podman) ----
# CONTAINER names the database container; when it is empty and the tool is not installed on the server, a single running
# container whose image matches the engine is used automatically. CONTAINER_ENGINE forces docker or podman.
CTR=""; CTR_ENGINE=""
find_container() { # find_container <host-tool-found:0|1> <image regex>
    local on_host="$1" pattern="$2" e found engines=()
    if [[ -n "${CONTAINER_ENGINE:-}" ]]; then engines=("$CONTAINER_ENGINE")
    else for e in podman docker; do if command -v "$e" >/dev/null 2>&1; then engines+=("$e"); fi; done; fi
    if [[ -n "${CONTAINER:-}" ]]; then
        for e in "${engines[@]+"${engines[@]}"}"; do
            if "$e" inspect --type container "$CONTAINER" >/dev/null 2>&1; then CTR_ENGINE="$e"; CTR="$CONTAINER"; break; fi
        done
        [[ -n "$CTR" ]] || fail "Container $CONTAINER was not found (engines: ${engines[*]:-none installed})"
    elif (( on_host == 0 )); then
        for e in "${engines[@]+"${engines[@]}"}"; do
            found="$("$e" ps --format '{{.Names}} {{.Image}}' 2>/dev/null | awk -v p="$pattern" 'tolower($2) ~ p { print $1 }')"
            if [[ -z "$found" ]]; then continue; fi
            if [[ "$found" == *$'\n'* ]]; then fail "Several $e containers match (${found//$'\n'/, }); set the CONTAINER variable for this job"; fi
            CTR_ENGINE="$e"; CTR="$found"; break
        done
    fi
    if [[ -n "$CTR" ]]; then log INFO "Using the $CTR_ENGINE container $CTR"; fi
}
in_container() { [[ -n "$CTR" ]]; }

same_source() { local a="${1%/}" b="${2%/}"; while [[ "$a" == *//* ]]; do a="${a//\/\//\/}"; done; while [[ "$b" == *//* ]]; do b="${b//\/\//\/}"; done; [[ "$a" == "$b" ]]; }
mounted_source() { awk -v m="$1" '$2 == m { s = $1 } END { print s }' "${BACKORA_PROC_MOUNTS:-/proc/mounts}" 2>/dev/null; }

# Mounts the job's own NFS folder. If the mount point already holds a different share (another job on this server),
# a job-specific mount point is used instead, so a backup can never land in another job's folder.
mount_nfs() {
    local current
    mkdir -p "$NFS_PATH" || fail "Cannot create $NFS_PATH"
    current="$(mounted_source "$NFS_PATH")"
    if [[ -n "$current" ]] && ! same_source "$current" "$NFS_SOURCE"; then
        log WARN "$NFS_PATH already holds $current; using a job-specific mount point"
        NFS_PATH="$NFS_PATH-$(printf '%s' "$JOB" | tr -c 'A-Za-z0-9_.-' '_')"
        mkdir -p "$NFS_PATH" || fail "Cannot create $NFS_PATH"
        current="$(mounted_source "$NFS_PATH")"
        if [[ -n "$current" ]] && ! same_source "$current" "$NFS_SOURCE"; then fail "$NFS_PATH holds $current, not $NFS_SOURCE"; fi
    fi
    if [[ -z "$current" ]]; then
        log INFO "Mounting NFS share $NFS_SOURCE on $NFS_PATH"
        local err; err="$(mount -t nfs "$NFS_SOURCE" "$NFS_PATH" 2>&1)" \
            || fail "Could not mount the NFS share $NFS_SOURCE on $NFS_PATH: ${err:-mount failed} (is this server allowed by the export, and does the job's folder exist on the share?)"
    else
        log INFO "NFS share $NFS_SOURCE is mounted on $NFS_PATH"
    fi
    local probe="$NFS_PATH/.backora-write-test.$$"
    { : > "$probe" && rm -f "$probe"; } 2>/dev/null || fail "The NFS share is not writable: $NFS_PATH"
    # Keep the legacy hourly name; a second backup in the same hour gets minutes and seconds instead of overwriting.
    if compgen -G "$NFS_PATH/$JOB-$TIMESTAMP*" >/dev/null; then TIMESTAMP="$(date +%Y-%m-%d_%H-%M-%S)"; fi
}

# Optional, per job: PRE_COMMAND runs before the backup (a failure is logged, not fatal, as in the original scripts).
run_pre_command() {
    [[ -n "${PRE_COMMAND:-}" ]] || return 0
    log INFO "Running the pre-backup command"
    progress 0 0 pre-command
    local out; out="$(mktemp)"; TMPFILES+=("$out")
    if ! bash -c "$PRE_COMMAND" > "$out" 2>&1; then
        tail -n 20 "$out" >&2
        log WARN "The pre-backup command failed; continuing"
    fi
}

# encrypt FILE — gpg AES-256 with ENCRYPT_PASSPHRASE (fd 3, never on the command line); sets ENCRYPTED to the new path.
# (Called directly, not in $(...), so failures and the cleanup list stay in this shell.)
ENCRYPTED=""
encrypt() {
    local file="$1" pinentry=()
    command -v gpg >/dev/null 2>&1 || fail "gpg is not installed"
    # gpg 2.1+ needs loopback pinentry for a batch passphrase; gpg 2.0 (RHEL 7) rejects the option.
    if gpg --batch --pinentry-mode loopback --version >/dev/null 2>&1; then pinentry=(--pinentry-mode loopback); fi
    add_partial "$file.gpg"
    watch_progress "$(bytes_of "$file")" encrypt "$file.gpg"
    gpg --batch --yes "${pinentry[@]}" --passphrase-fd 3 --symmetric --cipher-algo AES256 -o "$file.gpg" "$file" 3<<< "$ENCRYPT_PASSPHRASE" \
        || fail "Encryption of $(basename "$file") failed"
    stop_progress
    rm -f "$file"
    ENCRYPTED="$file.gpg"
}

# Optional, per job: EXTRA_PATHS (space-separated) are archived as <job>-files-<time>.tar, encrypted when a passphrase is set.
archive_extra_paths() {
    [[ -n "${EXTRA_PATHS:-}" ]] || return 0
    local paths=() existing=() p
    read -r -a paths <<< "$EXTRA_PATHS"
    for p in "${paths[@]}"; do
        if [[ -e "$p" ]]; then existing+=("${p#/}"); else log WARN "Extra path not found, skipped: $p"; fi
    done
    (( ${#existing[@]} > 0 )) || return 0
    local tarfile="$NFS_PATH/$JOB-files-$TIMESTAMP.tar"
    add_partial "$tarfile"
    log INFO "Archiving extra paths: $EXTRA_PATHS"
    watch_progress "$(cd / && bytes_of "${existing[@]}")" extra-files "$tarfile"
    tar -cf "$tarfile" -C / "${existing[@]}" || fail "tar of the extra paths failed"
    stop_progress
    if [[ -n "${ENCRYPT_PASSPHRASE:-}" ]]; then encrypt "$tarfile"; tarfile="$ENCRYPTED"; fi
    log INFO "Extra files saved: $(basename "$tarfile") ($(du -h "$tarfile" | cut -f1))"
}

# archive PATH — a single file (a dump, a backup stream) is already one artifact and stays as it is: packing it into a
# tar only copies it a second time over NFS. A folder is packed next to itself as PATH.tar and then removed.

# archive PATH — the backup is left as the tool wrote it. Backora's worker then compresses it into ONE file on the share
# (zstd on all cores; a folder becomes PATH.tar.zst), computes that file's checksums in the same pass and removes the
# raw output, as its own step with progress: no database server spends CPU on it or needs zstd installed.
archive() {
    if [[ -d "$1" ]]; then chmod -R a+rX "$1" || fail "Cannot make $1 readable"; fi
    log INFO "Backup completed: $1 ($(du -sh "$1" | cut -f1) in $(( SECONDS - STARTED ))s)"
}
# MariaDB/MySQL credentials. The client reads ~/.my.cnf after --defaults-extra-file, so a root ~/.my.cnf would override
# them; instead the client runs with a private HOME whose .my.cnf first includes the real one (other options still
# apply) and then sets ours, so ours win, exactly like the command-line flags of the original scripts.
# Sets MYSQL_HOME_DIR and MYSQL_ARGS (an explicit empty password, as the original scripts passed, when none is set).
MYSQL_HOME_DIR=""; MYSQL_ARGS=()
mysql_credentials() {
    local real_home="${HOME:-/root}" group p where="over the local socket"
    MYSQL_HOME_DIR="$(mktemp -d)"; TMPFILES+=("$MYSQL_HOME_DIR"); chmod 700 "$MYSQL_HOME_DIR"
    {
        if [[ -r "$real_home/.my.cnf" ]]; then echo "!include $real_home/.my.cnf"; fi
        for group in client mysqldump mariadb-dump mariabackup mariadb-backup xtrabackup; do
            echo "[$group]"
            if [[ -n "$MYSQL_HOST" ]]; then echo "host=$MYSQL_HOST"; echo "port=${MYSQL_PORT:-3306}"; fi
            if [[ -n "$MYSQL_USER" ]]; then echo "user=$MYSQL_USER"; fi
            if [[ -n "$MYSQL_PASSWORD" ]]; then p="${MYSQL_PASSWORD//\\/\\\\}"; echo "password=\"${p//\"/\\\"}\""; fi
        done
    } > "$MYSQL_HOME_DIR/.my.cnf"
    chmod 600 "$MYSQL_HOME_DIR/.my.cnf"
    MYSQL_ARGS=()
    # shellcheck disable=SC2034  # used by the MariaDB templates
    if [[ -n "$MYSQL_USER" && -z "$MYSQL_PASSWORD" ]]; then MYSQL_ARGS=(--password=); fi
    [[ -z "$MYSQL_HOST" ]] || where="to $MYSQL_HOST:${MYSQL_PORT:-3306}"
    log INFO "Connecting as ${MYSQL_USER:-the default user} $where"
}

# The same credentials for a client inside a container: user/host on the command line, the password through the
# inherited MYSQL_PWD variable (never on the command line). Sets MYSQL_CTR_ARGS and CTR_ENV (exec options).
MYSQL_CTR_ARGS=(); CTR_ENV=()
mysql_container_args() {
    MYSQL_CTR_ARGS=(); CTR_ENV=()
    if [[ -n "$MYSQL_HOST" ]]; then MYSQL_CTR_ARGS+=(--host="$MYSQL_HOST" --port="${MYSQL_PORT:-3306}"); fi
    if [[ -n "$MYSQL_USER" ]]; then MYSQL_CTR_ARGS+=(--user="$MYSQL_USER"); fi
    if [[ -n "$MYSQL_USER" && -z "$MYSQL_PASSWORD" ]]; then MYSQL_CTR_ARGS+=(--password=); fi
    # shellcheck disable=SC2034  # used by the MariaDB templates
    if [[ -n "$MYSQL_PASSWORD" ]]; then export MYSQL_PWD="$MYSQL_PASSWORD"; CTR_ENV=(-e MYSQL_PWD); fi
}

# PostgreSQL runs as the postgres OS user (peer authentication on the local socket, or TCP with PG_HOST). A password goes
# into a private .pgpass owned by postgres, never onto the command line. Sets PG_CONN (client args) and PG_ENV.
PG_CONN=(); PG_ENV=""

# as_postgres 'COMMAND' — runs a bash command line as the postgres OS user. It never goes through the account's login
# shell or home folder, so it also works where postgres is locked down (shell /sbin/nologin, no home directory), which
# `su - postgres` cannot: nologin prints "This account is currently not available." and the tool never runs.
# Starts in / so the tools never begin in a folder postgres cannot read.
as_postgres() {
    local home=""
    IFS=: read -r _ _ _ _ _ home _ <<<"$(getent passwd postgres 2>/dev/null || true)" || true
    [[ -n "$home" && -d "$home" ]] || home=/
    if command -v runuser >/dev/null 2>&1; then
        (cd / && HOME="$home" runuser -u postgres -- /bin/bash -c "$1")
    else
        (cd / && HOME="$home" su -s /bin/bash postgres -c "$1")
    fi
}

pg_connection() {
    local f p
    PG_CONN=(); PG_ENV=""
    if [[ -n "$PG_HOST" ]]; then PG_CONN+=(-h "$PG_HOST" -p "${PG_PORT:-5432}"); fi
    PG_CONN+=(-U "${PG_USER:-postgres}" -w)
    if [[ -n "$PG_PASSWORD" ]]; then
        p="${PG_PASSWORD//\\/\\\\}"; p="${p//:/\\:}"
        if in_container; then export PGPASSWORD="$PG_PASSWORD"
        else
            f="$(mktemp)"; TMPFILES+=("$f"); printf '*:*:*:*:%s\n' "$p" > "$f"
            chown postgres "$f" && chmod 600 "$f" || fail "Cannot prepare the password file for the postgres user"
            # shellcheck disable=SC2034  # used by the PostgreSQL templates
            PG_ENV="PGPASSFILE=$(printf '%q' "$f") "
        fi
    fi
    if [[ -n "$PG_HOST" ]]; then log INFO "Connecting to $PG_HOST:${PG_PORT:-5432} as ${PG_USER:-postgres}"; else log INFO "Connecting over the local socket as ${PG_USER:-postgres} (peer)"; fi
}

# pg_sql DB 'QUERY' — one value from psql with the backup's connection (as postgres on the host, or in the container).
pg_sql() {
    local psql env=()
    if in_container; then
        if [[ -n "$PG_PASSWORD" ]]; then env=(-e PGPASSWORD); fi
        "$CTR_ENGINE" exec "${env[@]+"${env[@]}"}" -u postgres "$CTR" "${PG_BIN_DIR:+${PG_BIN_DIR%/}/}psql" "${PG_CONN[@]}" -d "$1" -XAtc "$2" </dev/null 2>/dev/null
    else
        psql="${PG_TOOL%/*}/psql"; [[ -n "$PG_TOOL" && -x "$psql" ]] || psql="$(command -v psql 2>/dev/null)" || return 1
        as_postgres "${PG_ENV}$(printf '%q ' "$psql" "${PG_CONN[@]}" -d "$1" -XAtc "$2")" </dev/null 2>/dev/null
    fi
}

# pg_tool NAME — full path of a PostgreSQL client tool for the postgres user: PG_BIN_DIR when set, else the newest
# versioned install (/usr/pgsql-NN/bin on RHEL, /usr/lib/postgresql/NN/bin on Debian/Ubuntu), else root's PATH, else
# the postgres user's login PATH. Sets PG_TOOL; empty when it is not installed on this server.
PG_TOOL=""
pg_tool() {
    local dir best="" ver bestver=-1
    PG_TOOL=""
    if [[ -n "${PG_BIN_DIR:-}" ]]; then if [[ -x "${PG_BIN_DIR%/}/$1" ]]; then PG_TOOL="${PG_BIN_DIR%/}/$1"; fi; return 0; fi
    for dir in /usr/pgsql-*/bin /usr/lib/postgresql/*/bin; do
        [[ -x "$dir/$1" ]] || continue
        ver=0; if [[ "$dir" =~ ([0-9]+)(\.([0-9]+))? ]]; then ver=$(( 10#${BASH_REMATCH[1]} * 100 + 10#${BASH_REMATCH[3]:-0} )); fi
        if (( ver > bestver )); then bestver=$ver; best="$dir/$1"; fi
    done
    if [[ -n "$best" ]]; then PG_TOOL="$best"; return 0; fi
    PG_TOOL="$(command -v "$1" 2>/dev/null || true)"
    if [[ -z "$PG_TOOL" ]] && id postgres >/dev/null 2>&1; then PG_TOOL="$(as_postgres "exec /bin/bash -lc $(printf '%q' "command -v $1")" 2>/dev/null </dev/null | tail -n 1 || true)"; fi
}
# ---- end common ----
