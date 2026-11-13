using System.Net.Http.Json;
using Backora.Application.Backups;
using Backora.Contracts.V1;
using Backora.Domain;
using Backora.Domain.Backups;
using Backora.Domain.Infrastructure;
using Backora.Infrastructure.Persistence;
using Backora.IntegrationTests.Infrastructure;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace Backora.IntegrationTests.Database;

/// <summary>The "superseded copy attempt" rule runs as SQL on PostgreSQL: a retried failure stops counting as a failure.</summary>
[Collection(PostgresCollection.Name)]
public sealed class CopyAttemptsDbTests(PostgresContainer pg) : IAsyncLifetime
{
    private BackoraApiFactory _factory = null!;

    public async Task InitializeAsync()
    {
        _factory = new BackoraApiFactory(await pg.CreateDatabaseAsync("copies"));
        _ = _factory.Server;
    }

    public async Task DisposeAsync() => await _factory.DisposeAsync();

    private static async Task<(BackupRun Run, S3Destination Primary, S3Destination Dr)> SeedAsync(BackoraDbContext db)
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var server = new Server { Name = "srv-" + suffix, Host = "10.1.1.1", SshUser = "root" };
        var template = new ScriptTemplate { Name = "tpl-" + suffix, Category = "MariaDB", Script = "echo hi" };
        var primary = new S3Destination { Name = "ecs-" + suffix, Endpoint = "https://ecs.example", Region = "r", Bucket = "b1", AccessKeyId = "a", SecretKeyCiphertext = "x" };
        var dr = new S3Destination { Name = "dr-" + suffix, Endpoint = "https://dr.example", Region = "r", Bucket = "b2", AccessKeyId = "a", SecretKeyCiphertext = "x" };
        db.AddRange(server, template, primary, dr);
        await db.SaveChangesAsync();
        var job = new BackupJob { Name = "job-" + suffix, Category = "MariaDB", ScriptTemplateId = template.Id, ServerId = server.Id, Status = JobStatus.Active };
        db.Add(job);
        await db.SaveChangesAsync();
        var run = new BackupRun { BackupJobId = job.Id, ServerId = server.Id, JobName = job.Name, Category = job.Category, TemplateName = "t", Status = RunStatus.Completed, NextAttemptAt = DateTimeOffset.UtcNow };
        db.Add(run);
        await db.SaveChangesAsync();
        return (run, primary, dr);
    }

    private static OffloadRun Copy(BackupRun run, S3Destination dest, OffloadStatus status) => new()
    {
        BackupRunId = run.Id, BackupJobId = run.BackupJobId, ServerId = run.ServerId, S3DestinationId = dest.Id, JobName = run.JobName,
        SourcePath = "/x", RemotePrefix = "p/", Status = status, CompletedAt = DateTimeOffset.UtcNow, NextAttemptAt = DateTimeOffset.UtcNow,
    };

    [Fact]
    public async Task A_retried_failure_is_superseded_and_drops_out_of_current_failures()
    {
        await _factory.WithDbAsync(async db =>
        {
            var (run, primary, dr) = await SeedAsync(db);
            var upload = Copy(run, primary, OffloadStatus.Completed);
            var failed = Copy(run, dr, OffloadStatus.Failed);
            db.AddRange(upload, failed);
            await db.SaveChangesAsync();

            // Before the retry: the DR failure is current.
            (await db.OffloadRuns.Where(o => o.BackupRunId == run.Id && o.Status == OffloadStatus.Failed).WhereCurrent(db.OffloadRuns).Select(o => o.Id).ToListAsync())
                .Should().Equal(failed.Id);

            var retry = Copy(run, dr, OffloadStatus.Completed);
            db.Add(retry);
            await db.SaveChangesAsync();

            // After it: superseded, so no longer a current failure (this is the query behind alerts and the "Failed" view).
            (await db.OffloadRuns.Where(o => o.BackupRunId == run.Id && o.Status == OffloadStatus.Failed).WhereCurrent(db.OffloadRuns).AnyAsync())
                .Should().BeFalse();
            var next = await CopyAttempts.NextAttemptsAsync(db, [upload, failed, retry], CancellationToken.None);
            next.Should().ContainKey(failed.Id).WhoseValue.Should().Be(retry.Id);
            next.Should().NotContainKey(retry.Id).And.NotContainKey(upload.Id);
        });
    }

    [Fact]
    public async Task A_failure_imported_from_v1_is_history_not_a_failure_needing_attention()
    {
        await _factory.WithDbAsync(async db =>
        {
            var (run, primary, dr) = await SeedAsync(db);
            var current = Copy(run, primary, OffloadStatus.Failed);
            var legacy = Copy(run, dr, OffloadStatus.Failed);
            legacy.LegacyPrefix = true;
            db.AddRange(current, legacy);
            await db.SaveChangesAsync();

            (await db.OffloadRuns.Where(o => o.BackupRunId == run.Id && o.Status == OffloadStatus.Failed).WhereNeedsAttention(db.OffloadRuns).Select(o => o.Id).ToListAsync())
                .Should().Equal(current.Id);
        });
    }

    [Fact]
    public async Task A_cleared_failure_leaves_the_failed_list_but_stays_in_the_history()
    {
        await _factory.WithDbAsync(async db =>
        {
            var (run, primary, dr) = await SeedAsync(db);
            var stillFailing = Copy(run, primary, OffloadStatus.Failed);
            var cleared = Copy(run, dr, OffloadStatus.Failed);
            cleared.DismissedAt = DateTimeOffset.UtcNow;
            cleared.DismissedBy = "admin";
            db.AddRange(stillFailing, cleared);
            await db.SaveChangesAsync();

            (await db.OffloadRuns.Where(o => o.BackupRunId == run.Id && o.Status == OffloadStatus.Failed).WhereNeedsAttention(db.OffloadRuns).Select(o => o.Id).ToListAsync())
                .Should().Equal(stillFailing.Id);
            (await db.OffloadRuns.CountAsync(o => o.BackupRunId == run.Id)).Should().Be(2, "nothing is deleted");
        });
    }

    [Fact]
    public async Task Clearing_failed_runs_and_copies_through_the_api_empties_the_failed_lists_and_deletes_nothing()
    {
        long runId = 0, copyId = 0;
        await _factory.WithDbAsync(async db =>
        {
            var (run, primary, _) = await SeedAsync(db);
            var failedRun = new BackupRun { BackupJobId = run.BackupJobId, ServerId = run.ServerId, JobName = run.JobName, Category = run.Category, TemplateName = "t",
                Status = RunStatus.Failed, FailureClass = FailureClass.ScriptError, CompletedAt = DateTimeOffset.UtcNow, NextAttemptAt = DateTimeOffset.UtcNow };
            db.Add(failedRun);
            var failedCopy = Copy(run, primary, OffloadStatus.Failed);
            db.Add(failedCopy);
            await db.SaveChangesAsync();
            runId = failedRun.Id; copyId = failedCopy.Id;
        });
        var admin = await _factory.AdminClientAsync();

        (await admin.GetFromJsonAsync<PagedResult<RunSummaryDto>>("/api/v1/runs?status=Failed&pageSize=200"))!.Items.Should().Contain(r => r.Id == runId);
        var runs = await (await admin.PostAsJsonAsync("/api/v1/runs/dismiss-failed", new { })).EnsureSuccessStatusCode().Content.ReadFromJsonAsync<DismissedResponse>();
        runs!.Count.Should().BeGreaterThanOrEqualTo(1);
        (await admin.GetFromJsonAsync<PagedResult<RunSummaryDto>>("/api/v1/runs?status=Failed&pageSize=200"))!.Items.Should().BeEmpty();
        (await admin.GetFromJsonAsync<PagedResult<RunSummaryDto>>("/api/v1/runs?pageSize=200"))!.Items.Should().NotContain(r => r.Id == runId, "a cleared failure is out of the history");
        (await admin.GetFromJsonAsync<PagedResult<RunSummaryDto>>("/api/v1/runs?cleared=true&pageSize=200"))!.Items.Should().Contain(r => r.Id == runId, "and kept under Cleared");

        var copies = await (await admin.PostAsJsonAsync("/api/v1/offloads/dismiss-failed", new { })).EnsureSuccessStatusCode().Content.ReadFromJsonAsync<DismissedResponse>();
        copies!.Count.Should().BeGreaterThanOrEqualTo(1);
        (await admin.GetFromJsonAsync<PagedResult<OffloadSummaryDto>>("/api/v1/offloads?status=Failed&pageSize=200"))!.Items.Should().BeEmpty();
        (await admin.GetFromJsonAsync<PagedResult<OffloadSummaryDto>>("/api/v1/offloads?pageSize=200"))!.Items.Should().NotContain(o => o.Id == copyId);
        (await admin.GetFromJsonAsync<PagedResult<OffloadSummaryDto>>("/api/v1/offloads?cleared=true&pageSize=200"))!.Items.Should().Contain(o => o.Id == copyId);

        await _factory.WithDbAsync(async db =>
        {
            (await db.BackupRuns.SingleAsync(r => r.Id == runId)).DismissedAt.Should().NotBeNull("the run is kept, marked as cleared");
            (await db.OffloadRuns.SingleAsync(o => o.Id == copyId)).DismissedBy.Should().NotBeNullOrEmpty();
        });
    }
}
