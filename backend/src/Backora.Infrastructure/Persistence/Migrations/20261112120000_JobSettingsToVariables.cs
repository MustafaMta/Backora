using Backora.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Backora.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Runs resolve template settings only from Backora variables (NAME__JOBKEY for one job, NAME for every job), but the
    /// legacy import stored the old scripts' settings on the jobs (backup_job_settings), which nothing reads, and under the
    /// pre-per-engine names DB_HOST/DB_PORT/DB_USER/DB_PASSWORD. Jobs therefore ran without their database list, user,
    /// password or encryption passphrase (e.g. a MariaDB dump of nothing as the default user).
    ///
    /// This moves each job setting to the variable a run reads: DB_* becomes MYSQL_* or PG_* by the job's template
    /// category, the name gets the job key, secrets keep their ciphertext (same protector). It never overwrites an
    /// existing variable and skips values equal to the all-jobs variable (e.g. DB_USER=backup_usr when MYSQL_USER is
    /// backup_usr already). backup_job_settings is left as it was.
    /// </summary>
    [DbContext(typeof(BackoraDbContext))]
    [Migration("20261112120000_JobSettingsToVariables")]
    public partial class JobSettingsToVariables : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql(MoveSql);

        /// <summary>The data move (idempotent: existing variables are never touched).</summary>
        public const string MoveSql = """
                WITH moved AS (
                    SELECT
                        CASE
                            WHEN s.name IN ('DB_HOST', 'DB_PORT', 'DB_USER', 'DB_PASSWORD') AND t.category ILIKE 'postgre%' THEN 'PG_' || substr(s.name, 4)
                            WHEN s.name IN ('DB_HOST', 'DB_PORT', 'DB_USER', 'DB_PASSWORD') THEN 'MYSQL_' || substr(s.name, 4)
                            ELSE s.name
                        END AS setting,
                        regexp_replace(upper(j.name), '[^A-Z0-9]+', '_', 'g') AS job_key,
                        j.name AS job_name,
                        s.is_secret,
                        s.value,
                        s.value_ciphertext
                    FROM backup_job_settings s
                    JOIN backup_jobs j ON j.id = s.backup_job_id
                    JOIN script_templates t ON t.id = j.script_template_id
                    WHERE j.deleted_at IS NULL
                      AND (s.value IS NOT NULL AND s.value <> '' OR s.value_ciphertext IS NOT NULL)
                )
                INSERT INTO variables (name, is_secret, value, value_ciphertext, description, created_at, updated_at)
                SELECT m.setting || '__' || m.job_key,
                       m.is_secret,
                       CASE WHEN m.is_secret THEN NULL ELSE m.value END,
                       CASE WHEN m.is_secret THEN m.value_ciphertext ELSE NULL END,
                       left('Moved from the imported settings of job ' || m.job_name, 500),
                       now(), now()
                FROM moved m
                WHERE length(m.setting || '__' || m.job_key) <= 100
                  AND NOT EXISTS (
                      SELECT 1 FROM variables g
                      WHERE g.name = m.setting AND NOT g.is_secret AND NOT m.is_secret AND g.value = m.value)
                ON CONFLICT (name) DO NOTHING;
                """;

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Data move only. The variables it created are ordinary variables now and may have been edited; keep them.
        }
    }
}
