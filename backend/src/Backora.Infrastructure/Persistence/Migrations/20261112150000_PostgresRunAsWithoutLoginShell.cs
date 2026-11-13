using Backora.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Backora.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// The built-in templates ran PostgreSQL tools with <c>su - postgres -c …</c>, which starts the postgres account's
    /// login shell. On hardened servers that shell is /sbin/nologin (and the home folder may not exist), so su prints
    /// "This account is currently not available." and pg_basebackup / pg_dumpall never run.
    ///
    /// The templates now call <c>as_postgres</c> (backend/database/templates/_common.inc), which runs the tool as postgres
    /// with an explicit bash via runuser (or su -s), independent of the login shell. Templates live in the database, so
    /// this applies the same edit to the stored scripts: only scripts that still contain the exact old lines and don't
    /// define as_postgres yet are touched, and each changed template gets a new revision. Custom templates are left as
    /// they are. <c>BackupTemplateFilesTests</c>/<c>PostgresRunAsMigrationTests</c> keep these strings equal to the files.
    /// </summary>
    [DbContext(typeof(BackoraDbContext))]
    [Migration("20261112150000_PostgresRunAsWithoutLoginShell")]
    public partial class PostgresRunAsWithoutLoginShell : Migration
    {
        public const string Anchor = "\nPG_CONN=(); PG_ENV=\"\"\npg_connection() {";

        public const string AsPostgres = """
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
            """;

        public static readonly string AnchorNew = "\nPG_CONN=(); PG_ENV=\"\"\n\n" + AsPostgres + "\n\npg_connection() {";

        /// <summary>(old line, new line) pairs, exactly as in the shipped templates.</summary>
        public static readonly (string Old, string New)[] Lines =
        [
            (Anchor, AnchorNew),
            ("""PG_TOOL="$(su - postgres -c "command -v $1" 2>/dev/null </dev/null | tail -n 1 || true)"; fi""",
             """PG_TOOL="$(as_postgres "exec /bin/bash -lc $(printf '%q' "command -v $1")" 2>/dev/null </dev/null | tail -n 1 || true)"; fi"""),
            ("""    su - postgres -c "${PG_ENV}$(printf '%q ' "$PG_TOOL" "${PG_CONN[@]}" -D "$BACKUP_DIR") ${EXTRA_OPTIONS:-}" </dev/null || fail "pg_basebackup failed" """.TrimEnd(),
             """    as_postgres "${PG_ENV}$(printf '%q ' "$PG_TOOL" "${PG_CONN[@]}" -D "$BACKUP_DIR") ${EXTRA_OPTIONS:-}" </dev/null || fail "pg_basebackup failed" """.TrimEnd()),
            ("""    su - postgres -c "${PG_ENV}$(printf '%q ' "$PG_TOOL" "${PG_CONN[@]}")" </dev/null > "$DUMP_FILE" || fail "pg_dumpall failed" """.TrimEnd(),
             """    as_postgres "${PG_ENV}$(printf '%q ' "$PG_TOOL" "${PG_CONN[@]}")" </dev/null > "$DUMP_FILE" || fail "pg_dumpall failed" """.TrimEnd()),
        ];

        /// <summary>The update (idempotent: a script that already defines as_postgres is never touched).</summary>
        public static string UpdateSql
        {
            get
            {
                static string Q(string s) => "$bko$" + s + "$bko$";
                var expr = "script";
                foreach (var (o, n) in Lines) expr = $"replace({expr}, {Q(o)}, {Q(n)})";
                return "UPDATE script_templates\n"
                    + "SET script = " + expr + ",\n"
                    + "    revision = revision + 1,\n"
                    + "    updated_at = now()\n"
                    + "WHERE strpos(script, 'su - postgres -c') > 0\n"
                    + "  AND strpos(script, 'as_postgres() {') = 0\n"
                    + "  AND strpos(script, " + Q(Anchor) + ") > 0;";
            }
        }

        protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql(UpdateSql);

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Not reverted: the old scripts fail on servers where postgres has no login shell.
        }
    }
}
