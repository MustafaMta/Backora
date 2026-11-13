using Backora.Application.Abstractions;
using Backora.Application.Common;
using Backora.Contracts.Security;
using Backora.Contracts.V1;
using Backora.Domain;
using Backora.Domain.Backups;
using Microsoft.EntityFrameworkCore;

namespace Backora.Application.Backups;

/// <param name="Cleared">False (default): cleared failures are left out of every list; true: only cleared failures.</param>
public sealed record RunQuery(RunStatusDto? Status, long? JobId, long? ServerId, RunTriggerDto? Trigger, DateTimeOffset? From, DateTimeOffset? To, string? Q, bool Cleared = false);

public sealed class RunService(IAppDbContext db, ICurrentUser user, IAuditLog audit, IClock clock, BackupJobService jobs)
{
    /// <summary>
    /// Clears failed runs from the Failed list (one, or every failed run not cleared yet): they stay in the history and
    /// stop counting in alerts and job health. Nothing is deleted.
    /// </summary>
    public async Task<DismissedResponse> DismissFailedAsync(long? id, CancellationToken ct, long? jobId = null)
    {
        var query = db.BackupRuns.Where(r => r.Status == RunStatus.Failed && r.DismissedAt == null);
        if (jobId is { } job) query = query.Where(r => r.BackupJobId == job);
        if (id is { } one)
        {
            var run = await db.BackupRuns.FirstOrDefaultAsync(r => r.Id == one, ct) ?? throw new NotFoundException("Run", one);
            if (run.Status != RunStatus.Failed) throw new ConflictException("run_not_failed", "Only failed runs can be cleared.");
            query = query.Where(r => r.Id == one);
        }
        var failed = await query.ToListAsync(ct);
        var now = clock.UtcNow;
        foreach (var r in failed)
        {
            r.DismissedAt = now;
            r.DismissedBy = user.Name;
        }
        audit.Record("run.failures_cleared", "BackupRun", id?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "all",
            details: new { count = failed.Count, ids = failed.Select(r => r.Id).Take(500).ToList() });
        await db.SaveChangesAsync(ct);
        return new DismissedResponse(failed.Count);
    }

    public async Task<PagedResult<RunSummaryDto>> ListAsync(RunQuery f, PageRequest page, CancellationToken ct)
    {
        var query = db.BackupRuns.AsNoTracking().Include(r => r.Server).AsQueryable();
        if (f.Status is { } s) query = query.Where(r => r.Status == s.ToDomain());
        // A cleared failure is out of the way everywhere (it is kept, under "Cleared").
        query = f.Cleared ? query.Where(r => r.DismissedAt != null) : query.Where(r => r.DismissedAt == null);
        if (f.JobId is { } j) query = query.Where(r => r.BackupJobId == j);
        if (f.ServerId is { } sid) query = query.Where(r => r.ServerId == sid);
        if (f.Trigger is { } t) query = query.Where(r => r.Trigger == t.ToDomain());
        if (f.From is { } from) query = query.Where(r => r.CreatedAt >= from);
        if (f.To is { } to) query = query.Where(r => r.CreatedAt <= to);
        if (!string.IsNullOrWhiteSpace(f.Q))
        {
            var term = $"%{f.Q.Trim()}%";
            query = query.Where(r => EF.Functions.ILike(r.JobName, term) || EF.Functions.ILike(r.Category, term)
                || EF.Functions.ILike(r.Server!.Name, term) || EF.Functions.ILike(r.Server!.Host, term) || (r.ErrorMessage != null && EF.Functions.ILike(r.ErrorMessage, term)));
        }
        query = (page.SortField, page.Descending) switch
        {
            ("startedat", false) => query.OrderBy(r => r.StartedAt),
            ("startedat", true) => query.OrderByDescending(r => r.StartedAt),
            ("duration", false) => query.OrderBy(r => r.CompletedAt - r.StartedAt),
            ("duration", true) => query.OrderByDescending(r => r.CompletedAt - r.StartedAt),
            ("createdat", false) => query.OrderBy(r => r.CreatedAt),
            _ => query.OrderByDescending(r => r.CreatedAt).ThenByDescending(r => r.Id),
        };
        var total = await query.LongCountAsync(ct);
        var runs = await query.Skip(page.Skip).Take(page.SafePageSize).ToListAsync(ct);
        var ids = runs.Select(r => r.Id).ToList();
        var offloads = await db.OffloadRuns.AsNoTracking().Where(o => o.BackupRunId != null && ids.Contains(o.BackupRunId!.Value) && o.SourceOffloadRunId == null)
            .GroupBy(o => o.BackupRunId!.Value).Select(g => new { g.Key, Status = g.OrderByDescending(o => o.CreatedAt).Select(o => o.Status).First() })
            .ToDictionaryAsync(x => x.Key, x => x.Status, ct);
        var items = runs.Select(r => r.ToSummary(null, offloads.TryGetValue(r.Id, out var os) ? os : null)).ToList();
        return new PagedResult<RunSummaryDto>(items, page.SafePage, page.SafePageSize, total);
    }

    public async Task<RunDetailDto> GetAsync(long id, CancellationToken ct)
    {
        var run = await db.BackupRuns.AsNoTracking().Include(r => r.Server).Include(r => r.Artifacts).FirstOrDefaultAsync(r => r.Id == id, ct)
            ?? throw new NotFoundException("Run", id);
        var offloads = await db.OffloadRuns.AsNoTracking().Include(o => o.S3Destination).Include(o => o.Server)
            .Include(o => o.SourceOffloadRun!).ThenInclude(s => s.S3Destination)
            .Where(o => o.BackupRunId == id).OrderByDescending(o => o.CreatedAt).ToListAsync(ct);
        var canSeeLogs = user.HasPermission(Permissions.RunsReadLogs);
        var latestOffload = offloads.FirstOrDefault(o => o.SourceOffloadRunId == null);
        var next = CopyAttempts.NextAttempts(offloads.Select(o => new CopyAttempts.Key(o.Id, o.BackupRunId, o.S3DestinationId)));
        return new RunDetailDto(
            run.ToSummary(null, latestOffload?.Status),
            run.TemplateName, run.TemplateRevision, run.ScriptSha256, run.ExitCode,
            canSeeLogs ? run.Log : null, canSeeLogs,
            run.ScheduledFor, run.HeartbeatAt, run.ProcessedBy, run.Status == RunStatus.Pending ? run.NextAttemptAt : null,
            run.CancelRequestedAt, run.RemoteDirectory, run.RemotePid, run.TimeoutAt, run.CorrelationId,
            run.Artifacts.OrderBy(a => a.RelativePath, StringComparer.Ordinal).Select(a => a.ToDto()).ToList(),
            offloads.Select(o => o.ToSummary(null, next.TryGetValue(o.Id, out var n) ? n : null)).ToList(),
            Timeline(run, offloads),
            ProgressOf(run));
    }

    public async Task<RunSummaryDto> CancelAsync(long id, CancellationToken ct)
    {
        var run = await db.BackupRuns.Include(r => r.Server).FirstOrDefaultAsync(r => r.Id == id, ct) ?? throw new NotFoundException("Run", id);
        if (!run.RequestCancel(user.UserId, clock.UtcNow))
        {
            throw new ConflictException("run_not_cancellable", $"The run is {run.Status} and cannot be cancelled.");
        }
        run.AppendLogLine(clock.UtcNow, $"Cancellation requested by {user.Name}");
        audit.Record("backup_run.cancel_requested", "BackupRun", id.ToString(), details: new { run.JobName, status = run.Status.ToString() });
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw new ConflictException("run_state_changed", "The run changed state while cancelling. Refresh and try again.");
        }
        return run.ToSummary();
    }

    public async Task<RunCreatedResponse> RetryAsync(long id, string? idempotencyKey, CancellationToken ct)
    {
        var run = await db.BackupRuns.AsNoTracking().FirstOrDefaultAsync(r => r.Id == id, ct) ?? throw new NotFoundException("Run", id);
        if (run.Status is not (RunStatus.Failed or RunStatus.Cancelled)) throw new ConflictException("run_not_retryable", "Only failed or cancelled runs can be retried.");
        return await jobs.RunNowAsync(run.BackupJobId, idempotencyKey, RunTrigger.Retry, ct);
    }

    private static RunProgressDto? ProgressOf(BackupRun r) =>
        r.Status is not (RunStatus.Running or RunStatus.Verifying) || r.ProgressBytes is not { } bytes || r.ProgressAt is not { } at ? null
            : new RunProgressDto(bytes, r.ProgressTotalBytes,
                Execution.RunProgress.Percent(bytes, r.ProgressTotalBytes), r.ProgressBytesPerSecond,
                Execution.RunProgress.Remaining(bytes, r.ProgressTotalBytes, r.ProgressBytesPerSecond)?.TotalSeconds,
                r.ProgressStage, Measured: r.ProgressStage != "written", at);

    private static List<TimelineEntryDto> Timeline(BackupRun r, List<OffloadRun> offloads)
    {
        var t = new List<TimelineEntryDto> { new(r.CreatedAt, "Queued", $"{r.Trigger} by {r.RequestedByName ?? "system"}", "neutral") };
        if (r.ScheduledFor is { } sf) t.Add(new(sf, "Scheduled occurrence", null, "neutral"));
        if (r.StartedAt is { } st) t.Add(new(st, "Started", r.RemotePid is { } p ? $"Remote pid {p}" : null, "info"));
        if (r.CancelRequestedAt is { } cr) t.Add(new(cr, "Cancellation requested", null, "warning"));
        if (r.CompletedAt is { } c)
        {
            var (title, tone) = r.Status switch
            {
                RunStatus.Completed => ("Completed and verified", "success"),
                RunStatus.Failed => ($"Failed · {r.FailureClass}", "danger"),
                RunStatus.Cancelled => ("Cancelled", "muted"),
                _ => (r.Status.ToString(), "neutral"),
            };
            t.Add(new(c, title, r.Status == RunStatus.Completed ? $"{r.ArtifactCount} artifact(s)" : r.ErrorMessage, tone));
        }
        foreach (var o in offloads.OrderBy(o => o.CreatedAt))
        {
            var what = o.IsReplica ? $"DR copy to {o.S3Destination?.Name}" : $"Upload to {o.S3Destination?.Name}";
            if (o.StartedAt is { } os) t.Add(new(os, $"{what} started", $"s3://{o.S3Destination?.Bucket}/{o.RemotePrefix}", "info"));
            if (o.CompletedAt is { } oc)
            {
                t.Add(new(oc, o.Status == OffloadStatus.Completed ? $"{what} verified" : $"{what} {o.Status.ToString().ToLowerInvariant()}",
                    o.Status == OffloadStatus.Completed ? o.CleanupMessage : o.ErrorMessage, o.Status == OffloadStatus.Completed ? "success" : "danger"));
            }
        }
        return t.OrderBy(x => x.At).ToList();
    }
}
