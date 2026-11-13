using Backora.Domain;
using Backora.Domain.Backups;
using Backora.Domain.Infrastructure;
using Backora.Infrastructure.Persistence;
using Backora.Infrastructure.Persistence.Migrations;
using Backora.IntegrationTests.Infrastructure;
using Backora.Tools;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace Backora.IntegrationTests.Database;

/// <summary>
/// Imported job settings sat in backup_job_settings (unread) under DB_* names. The migration moves them to the
/// variables runs read, with per-engine names, without overwriting anything and without duplicating all-jobs values.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class JobSettingsToVariablesTests(PostgresContainer pg) : IAsyncLifetime
{
    private BackoraApiFactory _factory = null!;

    public async Task InitializeAsync()
    {
        _factory = new BackoraApiFactory(await pg.CreateDatabaseAsync("jobvars"));
        _ = _factory.Server;
    }

    public async Task DisposeAsync() => await _factory.DisposeAsync();

    [Fact]
    public async Task Imported_settings_become_the_variables_a_run_reads()
    {
        await _factory.WithDbAsync(async db =>
        {
            var server = new Server { Name = "zbx-" + Guid.NewGuid().ToString("N")[..6], Host = "10.0.0.20", SshUser = "root" };
            var maria = new ScriptTemplate { Name = "MariaDB · Logical t", Category = "MariaDB", Script = "echo" };
            var pgTpl = new ScriptTemplate { Name = "PostgreSQL · Physical t", Category = "PostgreSQL", Script = "echo" };
            db.AddRange(server, maria, pgTpl);
            await db.SaveChangesAsync();
            BackupJob Job(string name, ScriptTemplate t, bool deleted = false) => new()
            {
                Name = name, Category = t.Category, ScriptTemplateId = t.Id, ServerId = server.Id, Status = JobStatus.Inactive,
                DeletedAt = deleted ? DateTimeOffset.UtcNow : null,
            };
            var zabbix = Job("zabbix", maria);
            var plain = Job("analytics-db", maria);
            var crmJob = Job("crm", pgTpl);
            var irq = Job("metrics-eu", maria);
            var gone = Job("old-job", maria, deleted: true);
            db.AddRange(zabbix, plain, crmJob, irq, gone);
            await db.SaveChangesAsync();

            BackupJobSetting S(BackupJob j, string n, string v) => new() { BackupJobId = j.Id, Name = n, Value = v };
            BackupJobSetting Secret(BackupJob j, string n, string c) => new() { BackupJobId = j.Id, Name = n, IsSecret = true, ValueCiphertext = c };
            db.AddRange(
                S(zabbix, "DB_USER", "backup"), Secret(zabbix, "DB_PASSWORD", "cipher-zbx"), S(zabbix, "DATABASES", "zabbix"),
                S(zabbix, "DUMP_OPTIONS", "--single-transaction --ignore-table=zabbix.history"),
                S(plain, "DB_USER", "backup_usr"),                                   // same as the all-jobs MYSQL_USER
                S(crmJob, "DB_HOST", "localhost"), S(crmJob, "DB_USER", "backup_user"), Secret(crmJob, "DB_PASSWORD", "cipher-pg"),
                S(crmJob, "PG_BIN_DIR", "/usr/pgsql-16/bin"),
                S(irq, "DATABASES", "zabbix"),
                S(gone, "DB_USER", "nobody"));
            db.AddRange(
                new Variable { Name = "MYSQL_USER", Value = "backup_usr" },
                new Variable { Name = "DUMP_OPTIONS__ZABBIX", Value = "--edited-by-hand" });   // must not be overwritten
            await db.SaveChangesAsync();

            await db.Database.ExecuteSqlRawAsync(JobSettingsToVariables.MoveSql);
            await db.Database.ExecuteSqlRawAsync(JobSettingsToVariables.MoveSql);   // idempotent

            var vars = await db.Variables.AsNoTracking().ToDictionaryAsync(v => v.Name);
            vars["MYSQL_USER__ZABBIX"].Value.Should().Be("backup");
            vars["MYSQL_PASSWORD__ZABBIX"].Should().Match<Variable>(v => v.IsSecret && v.ValueCiphertext == "cipher-zbx" && v.Value == null);
            vars["DATABASES__ZABBIX"].Value.Should().Be("zabbix");
            vars["DUMP_OPTIONS__ZABBIX"].Value.Should().Be("--edited-by-hand");
            vars["DATABASES__METRICS_EU"].Value.Should().Be("zabbix");
            vars.Should().NotContainKey("MYSQL_USER__ANALYTICS_DB");               // equal to the all-jobs value
            vars["PG_HOST__CRM"].Value.Should().Be("localhost");
            vars["PG_USER__CRM"].Value.Should().Be("backup_user");
            vars["PG_PASSWORD__CRM"].ValueCiphertext.Should().Be("cipher-pg");
            vars["PG_BIN_DIR__CRM"].Value.Should().Be("/usr/pgsql-16/bin");
            vars.Keys.Should().NotContain(k => k.StartsWith("DB_", StringComparison.Ordinal) || k.EndsWith("__OLD_JOB", StringComparison.Ordinal));
        });
    }

    [Theory]
    [InlineData("DB_USER", "MariaDB", "MYSQL_USER")]
    [InlineData("DB_PASSWORD", "PostgreSQL", "PG_PASSWORD")]
    [InlineData("DATABASES", "MariaDB", "DATABASES")]
    public void Future_imports_use_the_per_engine_names(string legacy, string category, string expected) =>
        TemplateConsolidation.EngineSettingName(legacy, category).Should().Be(expected);
}
