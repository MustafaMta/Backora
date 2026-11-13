using Backora.Application.Abstractions;
using Backora.Application.Operations;
using Backora.Domain;
using Backora.Domain.Backups;
using Backora.Domain.Infrastructure;
using Backora.Domain.Platform;
using Backora.Infrastructure.Persistence;
using Backora.IntegrationTests.Infrastructure;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Backora.IntegrationTests.Database;

[Collection(PostgresCollection.Name)]
public sealed class DatabaseTests(PostgresContainer pg) : IAsyncLifetime
{
    private BackoraApiFactory _factory = null!;

    public async Task InitializeAsync()
    {
        _factory = new BackoraApiFactory(await pg.CreateDatabaseAsync("db"));
        _ = _factory.Server; // starts host → migrations + seed
    }

    public async Task DisposeAsync() => await _factory.DisposeAsync();

    private async Task<(Server Server, ScriptTemplate Template, BackupJob Job)> SeedJobAsync(BackoraDbContext db, string? cron = null)
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var server = new Server { Name = "srv-" + suffix, Host = "10.1.1.1", SshUser = "root" };
        var template = new ScriptTemplate { Name = "tpl-" + suffix, Category = "PostgreSQL", Script = "echo hi" };
        db.AddRange(server, template);
        await db.SaveChangesAsync();
        var job = new BackupJob { Name = "job-" + suffix, Category = "PostgreSQL", ScriptTemplateId = template.Id, ServerId = server.Id, Status = JobStatus.Active, CronExpression = cron };
        db.Add(job);
        await db.SaveChangesAsync();
        return (server, template, job);
    }

    private static BackupRun Run(BackupJob job, RunStatus status = RunStatus.Pending, DateTimeOffset? scheduledFor = null) => new()
    {
        BackupJobId = job.Id, ServerId = job.ServerId, JobName = job.Name, Category = job.Category, TemplateName = "t", Status = status,
        NextAttemptAt = DateTimeOffset.UtcNow.AddSeconds(-1), ScheduledFor = scheduledFor,
    };

    [Fact]
    public async Task All_migrations_are_applied_and_roles_seeded()
    {
        await _factory.WithDbAsync(async db =>
        {
            (await db.Database.GetPendingMigrationsAsync()).Should().BeEmpty();
            (await db.Roles.Select(r => r.Name).ToListAsync()).Should().BeEquivalentTo(Contracts.Security.Roles.All);
        });
    }

    [Fact]
    public async Task Database_rejects_a_second_active_run_for_the_same_job()
    {
        await _factory.WithDbAsync(async db =>
        {
            var (_, _, job) = await SeedJobAsync(db);
            db.BackupRuns.Add(Run(job));
            await db.SaveChangesAsync();
            db.BackupRuns.Add(Run(job, RunStatus.Running));
            var act = () => db.SaveChangesAsync();
            (await act.Should().ThrowAsync<DbUpdateException>()).Which.InnerException!.Message.Should().Contain("ux_backup_runs_one_active_per_job");
        });
    }

    [Fact]
    public async Task Terminal_runs_do_not_block_new_runs()
    {
        await _factory.WithDbAsync(async db =>
        {
            var (_, _, job) = await SeedJobAsync(db);
            db.BackupRuns.AddRange(Run(job, RunStatus.Completed), Run(job, RunStatus.Failed), Run(job, RunStatus.Pending));
            await db.SaveChangesAsync();
            (await db.BackupRuns.CountAsync(r => r.BackupJobId == job.Id)).Should().Be(3);
        });
    }

    [Fact]
    public async Task A_scheduled_occurrence_can_only_create_one_run()
    {
        await _factory.WithDbAsync(async db =>
        {
            var (_, _, job) = await SeedJobAsync(db);
            var occurrence = new DateTimeOffset(2026, 1, 1, 2, 0, 0, TimeSpan.Zero);
            db.BackupRuns.Add(Run(job, RunStatus.Completed, occurrence));
            await db.SaveChangesAsync();
            db.BackupRuns.Add(Run(job, RunStatus.Completed, occurrence));
            var act = () => db.SaveChangesAsync();
            (await act.Should().ThrowAsync<DbUpdateException>()).Which.InnerException!.Message.Should().Contain("ux_backup_runs_job_occurrence");
        });
    }

    [Fact]
    public async Task Audit_events_are_append_only()
    {
        await _factory.WithDbAsync(async db =>
        {
            db.AuditEvents.Add(new AuditEvent { OccurredAt = DateTimeOffset.UtcNow, Action = "test.append", ActorName = "it", Outcome = AuditOutcome.Success });
            await db.SaveChangesAsync();
            var update = () => db.Database.ExecuteSqlRawAsync("UPDATE audit_events SET action = 'tampered'");
            (await update.Should().ThrowAsync<PostgresException>()).Which.MessageText.Should().Contain("append-only");
            var delete = () => db.Database.ExecuteSqlRawAsync("DELETE FROM audit_events");
            await delete.Should().ThrowAsync<PostgresException>();
            var truncate = () => db.Database.ExecuteSqlRawAsync("TRUNCATE audit_events");
            await truncate.Should().ThrowAsync<PostgresException>();
        });
    }

    [Fact]
    public async Task Optimistic_concurrency_detects_lost_updates()
    {
        long id = 0;
        await _factory.WithDbAsync(async db => id = (await SeedJobAsync(db)).Server.Id);
        await using var scope1 = _factory.Services.CreateAsyncScope();
        await using var scope2 = _factory.Services.CreateAsyncScope();
        var db1 = scope1.ServiceProvider.GetRequiredService<BackoraDbContext>();
        var db2 = scope2.ServiceProvider.GetRequiredService<BackoraDbContext>();
        var a = await db1.Servers.FirstAsync(s => s.Id == id);
        var b = await db2.Servers.FirstAsync(s => s.Id == id);
        a.Description = "first";
        await db1.SaveChangesAsync();
        b.Description = "second";
        var act = () => db2.SaveChangesAsync();
        await act.Should().ThrowAsync<DbUpdateConcurrencyException>();
    }

    [Fact]
    public async Task Server_with_jobs_cannot_be_deleted_at_database_level()
    {
        await _factory.WithDbAsync(async db =>
        {
            var (server, _, _) = await SeedJobAsync(db);
            // Bypass EF's change tracker: the foreign key itself (ON DELETE RESTRICT) must refuse.
            var act = () => db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM servers WHERE id = {server.Id}");
            (await act.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be(PostgresErrorCodes.RestrictViolation);
        });
    }

    [Fact]
    public async Task Check_constraints_reject_invalid_values()
    {
        await _factory.WithDbAsync(async db =>
        {
            var (_, _, job) = await SeedJobAsync(db);
            var act = () => db.Database.ExecuteSqlInterpolatedAsync($"UPDATE backup_jobs SET timeout_minutes = 0 WHERE id = {job.Id}");
            await act.Should().ThrowAsync<PostgresException>();
            var act2 = () => db.Database.ExecuteSqlRawAsync("INSERT INTO worker_nodes (id, name, host, ssh_port, ssh_user, private_key_ciphertext, rclone_path, work_directory, created_at, updated_at) VALUES (2, 'x', 'h', 22, 'u', 'k', 'rclone', '/w', now(), now())");
            (await act2.Should().ThrowAsync<PostgresException>()).Which.ConstraintName.Should().Be("ck_worker_nodes_singleton");
        });
    }

    // ───────────── Leasing ─────────────

    [Fact]
    public async Task Concurrent_workers_never_claim_the_same_operation()
    {
        var ids = new List<long>();
        await _factory.WithDbAsync(async db =>
        {
            for (var i = 0; i < 12; i++)
            {
                var (_, _, job) = await SeedJobAsync(db);
                var run = Run(job);
                db.BackupRuns.Add(run);
                await db.SaveChangesAsync();
                ids.Add(run.Id);
            }
        });

        var leases = _factory.Services.GetRequiredService<ILeaseStore>();
        var claims = await Task.WhenAll(Enumerable.Range(0, 40).Select(async w =>
        {
            var mine = new List<long>();
            for (var i = 0; i < 3; i++)
            {
                var id = await leases.ClaimNextAsync(OperationKinds.Backup, $"worker-{w}", TimeSpan.FromMinutes(5), CancellationToken.None);
                if (id is { } x && ids.Contains(x)) mine.Add(x);
            }
            return mine;
        }));

        var all = claims.SelectMany(c => c).ToList();
        all.Should().OnlyHaveUniqueItems("an operation must never be leased to two workers");
        all.Should().BeEquivalentTo(ids, "every claimable operation is eventually claimed");
    }

    [Fact]
    public async Task Expired_lease_is_adopted_and_renewal_by_stale_owner_fails()
    {
        long runId = 0;
        await _factory.WithDbAsync(async db =>
        {
            var (_, _, job) = await SeedJobAsync(db);
            var run = Run(job);
            run.NextAttemptAt = DateTimeOffset.UtcNow.AddYears(-1); // first in queue
            db.BackupRuns.Add(run);
            await db.SaveChangesAsync();
            runId = run.Id;
        });
        var leases = _factory.Services.GetRequiredService<ILeaseStore>();
        (await leases.ClaimNextAsync(OperationKinds.Backup, "dead-worker", TimeSpan.FromMilliseconds(300), CancellationToken.None)).Should().Be(runId);
        (await leases.ClaimNextAsync(OperationKinds.Backup, "other", TimeSpan.FromMinutes(1), CancellationToken.None)).Should().NotBe(runId, "the lease is still valid");
        await Task.Delay(1000);
        long? adopted = null;
        for (var i = 0; i < 30 && adopted != runId; i++)
        {
            adopted = await leases.ClaimNextAsync(OperationKinds.Backup, "adopter", TimeSpan.FromMinutes(1), CancellationToken.None);
        }
        adopted.Should().Be(runId, "an expired lease means the owner died");
        (await leases.RenewAsync(OperationKinds.Backup, runId, "dead-worker", TimeSpan.FromMinutes(1), CancellationToken.None)).Should().BeFalse();
        (await leases.RenewAsync(OperationKinds.Backup, runId, "adopter", TimeSpan.FromMinutes(1), CancellationToken.None)).Should().BeTrue();
    }

    [Fact]
    public async Task Future_retries_are_not_claimed_early()
    {
        long runId = 0;
        await _factory.WithDbAsync(async db =>
        {
            var (_, _, job) = await SeedJobAsync(db);
            var run = Run(job);
            run.NextAttemptAt = DateTimeOffset.UtcNow.AddHours(1);
            db.BackupRuns.Add(run);
            await db.SaveChangesAsync();
            runId = run.Id;
        });
        var leases = _factory.Services.GetRequiredService<ILeaseStore>();
        for (var i = 0; i < 20; i++)
        {
            (await leases.ClaimNextAsync(OperationKinds.Backup, "w", TimeSpan.FromMinutes(1), CancellationToken.None)).Should().NotBe(runId);
        }
    }

    [Fact]
    public async Task Cluster_lock_is_exclusive_until_expiry()
    {
        var leases = _factory.Services.GetRequiredService<ILeaseStore>();
        var name = "lock-" + Guid.NewGuid().ToString("N")[..6];
        (await leases.TryAcquireClusterLockAsync(name, "a", TimeSpan.FromMilliseconds(500), CancellationToken.None)).Should().BeTrue();
        (await leases.TryAcquireClusterLockAsync(name, "b", TimeSpan.FromSeconds(30), CancellationToken.None)).Should().BeFalse();
        (await leases.TryAcquireClusterLockAsync(name, "a", TimeSpan.FromMilliseconds(500), CancellationToken.None)).Should().BeTrue("the owner renews");
        await Task.Delay(800);
        (await leases.TryAcquireClusterLockAsync(name, "b", TimeSpan.FromSeconds(30), CancellationToken.None)).Should().BeTrue();
    }

    // ───────────── Scheduler ─────────────

    [Fact]
    public async Task Scheduler_creates_exactly_one_run_per_due_occurrence_and_skips_when_active()
    {
        long jobId = 0;
        await _factory.WithDbAsync(async db =>
        {
            var (_, _, job) = await SeedJobAsync(db, "*/5 * * * *");
            job.NextRunAt = DateTimeOffset.UtcNow.AddMinutes(-1);
            await db.SaveChangesAsync();
            jobId = job.Id;
        });

        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<SchedulerService>().TickAsync(CancellationToken.None);
        }
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<SchedulerService>().TickAsync(CancellationToken.None);
        }

        await _factory.WithDbAsync(async db =>
        {
            var runs = await db.BackupRuns.Where(r => r.BackupJobId == jobId).ToListAsync();
            runs.Should().ContainSingle().Which.Trigger.Should().Be(RunTrigger.Scheduled);
            var job = await db.BackupJobs.FirstAsync(j => j.Id == jobId);
            job.NextRunAt.Should().BeAfter(DateTimeOffset.UtcNow);

            // Next occurrence becomes due while the first run is still pending → skipped, not stacked.
            job.NextRunAt = DateTimeOffset.UtcNow.AddSeconds(-30);
            await db.SaveChangesAsync();
        });
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<SchedulerService>().TickAsync(CancellationToken.None);
        }
        await _factory.WithDbAsync(async db => (await db.BackupRuns.CountAsync(r => r.BackupJobId == jobId)).Should().Be(1));
    }
}
