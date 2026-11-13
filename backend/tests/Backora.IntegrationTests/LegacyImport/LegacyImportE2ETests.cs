using System.Security.Cryptography;
using Backora.Contracts.Security;
using Backora.Domain;
using Backora.Infrastructure.Persistence;
using Backora.Tools;
using Backora.Tools.Legacy;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.MariaDb;
using Testcontainers.PostgreSql;

namespace Backora.IntegrationTests.LegacyImport;

/// <summary>Runs only when BACKORA_LEGACY_IMPORT_E2E=1 (requires Docker).</summary>
public sealed class LegacyE2EFactAttribute : FactAttribute
{
    public LegacyE2EFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("BACKORA_LEGACY_IMPORT_E2E") != "1")
        {
            Skip = "Set BACKORA_LEGACY_IMPORT_E2E=1 to run the legacy import end-to-end test (requires Docker).";
        }
    }
}

public sealed class LegacyImportE2ETests : IAsyncLifetime
{
    private MariaDbContainer? _maria;
    private PostgreSqlContainer? _pg;

    public async Task InitializeAsync()
    {
        if (Environment.GetEnvironmentVariable("BACKORA_LEGACY_IMPORT_E2E") != "1") return;
        _maria = new MariaDbBuilder().WithImage("mariadb:10.11").Build();
        _pg = new PostgreSqlBuilder().WithImage("postgres:18-alpine").Build();
        await Task.WhenAll(_maria.StartAsync(), _pg.StartAsync());
        var sql = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "LegacyImport", "fixtures", "legacy-sample.sql"));
        var result = await _maria.ExecScriptAsync(sql);
        result.ExitCode.Should().Be(0, result.Stderr);
    }

    public async Task DisposeAsync()
    {
        if (_maria is not null) await _maria.DisposeAsync();
        if (_pg is not null) await _pg.DisposeAsync();
    }

    [LegacyE2EFact]
    public async Task Imports_synthetic_legacy_database_idempotently()
    {
        using var host = ToolsProgram.BuildHost(c => c.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Backora"] = _pg!.GetConnectionString(),
            ["Encryption:ActiveKeyId"] = "test",
            ["Encryption:Keys:test"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
        }));

        await using (var scope = host.Services.CreateAsyncScope())
        {
            var init = scope.ServiceProvider.GetRequiredService<DatabaseInitializer>();
            await init.MigrateAsync(default);
            await init.SeedAsync(default);
        }

        var snapshot = await LegacySnapshot.ReadAsync(_maria!.GetConnectionString(), default);
        snapshot.Tables.Keys.Should().NotContain("jobs").And.NotContain("roles_permissions");
        // Guard against a partially loaded fixture (the client may report success on statement errors).
        snapshot["backup_job_executions"].Should().HaveCount(6);
        snapshot["upload_to_s3_job_executions"].Should().HaveCount(3);
        snapshot["restore_to_nfs_job_executions"].Should().HaveCount(3);

        // Dry run writes nothing.
        ImportReport dry;
        await using (var scope = host.Services.CreateAsyncScope())
        {
            dry = await scope.ServiceProvider.GetRequiredService<LegacyImporter>().ImportAsync(snapshot, new ImportOptions(true, false), default);
            dry.Passed.Should().BeTrue(dry.ToMarkdown());
            (await scope.ServiceProvider.GetRequiredService<BackoraDbContext>().Servers.CountAsync()).Should().Be(0);
        }

        var first = await ImportAsync(host, snapshot, allowNonEmpty: false);
        first.Passed.Should().BeTrue(first.ToMarkdown());
        first.Table("backup_job_executions").Skipped.Should().ContainSingle(s => s.Id == 105);
        first.Table("restore_to_nfs_job_executions").Skipped.Should().ContainSingle(s => s.Id == 303);
        first.Table("api_tokens").Skipped.Should().ContainSingle();

        await AssertTargetAsync(host);

        // A second run without --allow-non-empty is refused; with it, the import is idempotent.
        await using (var scope = host.Services.CreateAsyncScope())
        {
            var act = () => scope.ServiceProvider.GetRequiredService<LegacyImporter>().ImportAsync(snapshot, new ImportOptions(false, false), default);
            await act.Should().ThrowAsync<InvalidOperationException>();
        }
        var second = await ImportAsync(host, snapshot, allowNonEmpty: true);
        second.Passed.Should().BeTrue(second.ToMarkdown());
        await AssertTargetAsync(host);

        await using (var scope = host.Services.CreateAsyncScope())
        {
            var verify = await scope.ServiceProvider.GetRequiredService<LegacyImporter>().VerifyAsync(snapshot, default);
            verify.Passed.Should().BeTrue(verify.ToMarkdown());
            var db = scope.ServiceProvider.GetRequiredService<BackoraDbContext>();
            (await db.AuditEvents.CountAsync(a => a.Action == "legacy.import")).Should().Be(2);
        }
    }

    private static async Task<ImportReport> ImportAsync(Microsoft.Extensions.Hosting.IHost host, LegacySnapshot snapshot, bool allowNonEmpty)
    {
        await using var scope = host.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<LegacyImporter>().ImportAsync(snapshot, new ImportOptions(false, allowNonEmpty), default);
    }

    private static async Task AssertTargetAsync(Microsoft.Extensions.Hosting.IHost host)
    {
        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<BackoraDbContext>();

        // Users: ids preserved, bcrypt hash kept, roles mapped (unknown → Viewer), inactive status respected.
        var users = await db.Users.OrderBy(u => u.Id).ToListAsync();
        users.Select(u => u.Id).Should().Equal(3, 7, 9);
        users.Should().OnlyContain(u => u.PasswordHash!.StartsWith("$2y$") && u.LegacyId == u.Id);
        users.Single(u => u.Id == 7).IsActive.Should().BeFalse();
        var roles = await (from ur in db.UserRoles join r in db.Roles on ur.RoleId equals r.Id select new { ur.UserId, r.Name }).ToListAsync();
        roles.Should().HaveCount(3);
        roles.Single(r => r.UserId == 3).Name.Should().Be(Roles.PlatformAdministrator);
        roles.Single(r => r.UserId == 9).Name.Should().Be(Roles.Viewer);

        // Servers: ids preserved, duplicate names disambiguated, keys encrypted, host keys untrusted.
        var servers = await db.Servers.OrderBy(s => s.Id).ToListAsync();
        servers.Select(s => s.Id).Should().Equal(11, 12, 15);
        servers.Single(s => s.Id == 12).Name.Should().Be("db-01 (#12)");
        servers.Single(s => s.Id == 12).PrivateKeyCiphertext.Should().StartWith("bk1:");
        servers.Single(s => s.Id == 15).PrivateKeyCiphertext.Should().BeNull();
        servers.Should().OnlyContain(s => s.HostKeyFingerprint == null);

        (await db.WorkerNodes.SingleAsync()).PrivateKeyCiphertext.Should().StartWith("bk1:");
        var ssh = await db.SshSettings.SingleAsync();
        ssh.DefaultPrivateKeyCiphertext.Should().StartWith("bk1:");
        ssh.DefaultPublicKey.Should().StartWith("ssh-ed25519 ");
        (await db.S3Destinations.SingleAsync(d => d.Id == 2)).SecretKeyCiphertext.Should().StartWith("bk1:");
        var smtp = await db.SmtpSettings.SingleAsync();
        smtp.PasswordCiphertext.Should().StartWith("bk1:");
        smtp.Security.Should().Be("SslOnConnect");
        (await db.TeamsSettings.SingleAsync()).WebhookUrlCiphertext.Should().StartWith("bk1:");
        (await db.NotificationSettings.SingleAsync()).Recipients.Should().Equal("ops@example.test", "dba@example.test");

        var vars = await db.Variables.OrderBy(v => v.Id).ToListAsync();
        vars.Single(v => v.Name == "mysql_user").Value.Should().Be("backup");
        var secret = vars.Single(v => v.Name == "mysql_password");
        secret.IsSecret.Should().BeTrue();
        secret.Value.Should().BeNull();
        secret.ValueCiphertext.Should().StartWith("bk1:");

        // Templates: CRLF normalised; placeholder created for the missing legacy template.
        var templates = await db.ScriptTemplates.OrderBy(t => t.Id).ToListAsync();
        templates.Should().HaveCount(3);
        templates.Single(t => t.Id == 1).Script.Should().NotContain("\r");
        var placeholder = templates.Single(t => t.Name == "mongodump (missing legacy template)");
        placeholder.Id.Should().BeGreaterThan(5, "new ids are generated after preserved legacy ids");

        // Jobs: all inactive by default; unsafe name kept; invalid cron cleared; missing template linked to placeholder.
        var jobs = await db.BackupJobs.OrderBy(j => j.Id).ToListAsync();
        jobs.Select(j => j.Id).Should().Equal(21, 22, 23, 24);
        jobs.Should().OnlyContain(j => j.Status == JobStatus.Inactive && j.NextRunAt == null);
        jobs.Single(j => j.Id == 22).Name.Should().Be("bad name!");
        jobs.Single(j => j.Id == 22).RetentionDays.Should().Be(0.0417m);
        jobs.Single(j => j.Id == 23).CronExpression.Should().BeNull();
        jobs.Single(j => j.Id == 23).S3DestinationAssignedAt.Should().NotBeNull();
        jobs.Single(j => j.Id == 21).CronExpression.Should().Be("0 2 * * *");
        jobs.Single(j => j.Id == 24).ScriptTemplateId.Should().Be(placeholder.Id);

        // Runs: never active; incomplete ones become ProcessLost failures; legacy log extracted.
        var runs = await db.BackupRuns.OrderBy(r => r.Id).ToListAsync();
        runs.Select(r => r.Id).Should().Equal(101, 102, 103, 104, 106);
        runs.Should().OnlyContain(r => r.Trigger == RunTrigger.Imported && r.IsTerminal);
        runs.Single(r => r.Id == 101).Status.Should().Be(RunStatus.Completed);
        runs.Single(r => r.Id == 101).Log.Should().Be("backup ok");
        runs.Single(r => r.Id == 102).FailureClass.Should().Be(FailureClass.ProcessLost);
        runs.Single(r => r.Id == 104).ErrorMessage.Should().Be(LegacyMapping.IncompleteRunMessage);
        runs.Single(r => r.Id == 103).ErrorMessage.Should().Be("pg_dump: connection refused");
        runs.Single(r => r.Id == 106).Status.Should().Be(RunStatus.Cancelled);

        // Offloads: legacy prefix derived from upload start hour; sizes parsed; orphan link dropped.
        var offloads = await db.OffloadRuns.OrderBy(o => o.Id).ToListAsync();
        offloads.Select(o => o.Id).Should().Equal(201, 202, 203);
        var completed = offloads.Single(o => o.Id == 201);
        completed.Status.Should().Be(OffloadStatus.Completed);
        completed.RemotePrefix.Should().Be("orders-db/2025-08-01-04/");
        completed.LegacyPrefix.Should().BeTrue();
        completed.BytesTotal.Should().Be(1610612736);
        completed.BackupJobId.Should().Be(21);
        completed.ExpiresAt.Should().NotBeNull();
        offloads.Single(o => o.Id == 202).Status.Should().Be(OffloadStatus.Failed);
        offloads.Single(o => o.Id == 202).BytesTotal.Should().Be(57671680);
        var orphan = offloads.Single(o => o.Id == 203);
        orphan.BackupRunId.Should().BeNull();
        orphan.JobName.Should().Be("deleted-job");

        // Restores: shared legacy target paths disambiguated, incomplete ones failed.
        var restores = await db.RestoreRuns.OrderBy(r => r.Id).ToListAsync();
        restores.Select(r => r.Id).Should().Equal(301, 302);
        restores.Single(r => r.Id == 301).TargetPath.Should().Be("/BackoraDB/orders-db");
        restores.Single(r => r.Id == 302).TargetPath.Should().Be("/BackoraDB/orders-db#legacy-restore-302");
        restores.Single(r => r.Id == 302).Status.Should().Be(RestoreStatus.Failed);

        // Identity sequences continue after the preserved ids.
        var conn = db.Database.GetDbConnection();
        await db.Database.OpenConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT last_value FROM pg_sequences WHERE sequencename = (SELECT split_part(pg_get_serial_sequence('backup_runs','id'), '.', 2))";
        Convert.ToInt64(await cmd.ExecuteScalarAsync()).Should().BeGreaterThanOrEqualTo(106);
    }
}
