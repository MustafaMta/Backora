using Backora.Application.Abstractions;
using Backora.Application.Backups;
using Backora.Application.Common;
using Backora.Contracts.Security;
using Backora.Contracts.V1;
using Backora.Domain;
using Backora.Domain.Backups;
using Backora.Domain.Platform;
using Backora.Domain.Scheduling;
using Microsoft.EntityFrameworkCore;

namespace Backora.Application.Insights;

/// <summary>Dashboard aggregations computed from real operational data (no synthetic values).</summary>
public sealed class DashboardService(IAppDbContext db, IClock clock)
{
    public async Task<DashboardDto> GetAsync(DateTimeOffset? fromParam, DateTimeOffset? toParam, CancellationToken ct)
    {
        var now = clock.UtcNow;
        var to = toParam ?? now;
        var from = fromParam ?? to.AddDays(-7);
        if (from > to) (from, to) = (to, from);
        if (to - from > TimeSpan.FromDays(366)) from = to.AddDays(-366);

        var servers = await db.Servers.AsNoTracking().ToListAsync(ct);
        var jobs = await db.BackupJobs.AsNoTracking().Include(j => j.Server).Where(j => j.DeletedAt == null).ToListAsync(ct);
        var runsInRange = await db.BackupRuns.AsNoTracking()
            .Where(r => r.CreatedAt >= from && r.CreatedAt <= to)
            .Select(r => new { r.Id, r.Status, r.FailureClass, r.CreatedAt, r.StartedAt, r.CompletedAt, r.BackupJobId })
            .ToListAsync(ct);
        var running = await db.BackupRuns.CountAsync(r => BackupRun.ActiveStatuses.Contains(r.Status), ct);
        var lastSuccess = await db.BackupRuns.Where(r => r.Status == RunStatus.Completed).MaxAsync(r => (DateTimeOffset?)r.CompletedAt, ct);

        var points = await db.OffloadRuns.AsNoTracking()
            .Where(o => o.Status == OffloadStatus.Completed)
            .Select(o => new { o.CompletedAt, o.CreatedAt, o.DeletedAt, o.RetentionState, Bytes = o.BytesTotal ?? 0 })
            .ToListAsync(ct);
        var livePoints = points.Where(p => p.RetentionState != RetentionState.Deleted).ToList();

        var protectedServerIds = jobs.Where(j => j.Status == JobStatus.Active).Select(j => j.ServerId).ToHashSet();
        var succeeded = runsInRange.Count(r => r.Status == RunStatus.Completed);
        var failed = runsInRange.Count(r => r.Status == RunStatus.Failed);
        double? successRate = succeeded + failed == 0 ? null : Math.Round(succeeded * 100.0 / (succeeded + failed), 1);

        // Daily series bucketed in UTC.
        var days = Enumerable.Range(0, (int)Math.Ceiling((to.UtcDateTime.Date - from.UtcDateTime.Date).TotalDays) + 1)
            .Select(i => DateOnly.FromDateTime(from.UtcDateTime.Date.AddDays(i))).ToList();
        var runTrend = days.Select(d => new DailyRunPointDto(d,
            runsInRange.Count(r => DateOnly.FromDateTime(r.CreatedAt.UtcDateTime) == d && r.Status == RunStatus.Completed),
            runsInRange.Count(r => DateOnly.FromDateTime(r.CreatedAt.UtcDateTime) == d && r.Status == RunStatus.Failed),
            runsInRange.Count(r => DateOnly.FromDateTime(r.CreatedAt.UtcDateTime) == d && r.Status == RunStatus.Cancelled))).ToList();
        var durationTrend = days.Select(d =>
        {
            var ds = runsInRange.Where(r => DateOnly.FromDateTime(r.CreatedAt.UtcDateTime) == d && r.Status == RunStatus.Completed && r.StartedAt != null && r.CompletedAt != null)
                .Select(r => (r.CompletedAt!.Value - r.StartedAt!.Value).TotalSeconds).ToList();
            return new DailyDurationPointDto(d, ds.Count == 0 ? null : Math.Round(ds.Average(), 1), ds.Count == 0 ? null : Math.Round(ds.Max(), 1));
        }).ToList();
        var storageGrowth = days.Select(d =>
        {
            var end = new DateTimeOffset(d.ToDateTime(TimeOnly.MaxValue), TimeSpan.Zero);
            var bytes = points.Where(p => (p.CompletedAt ?? p.CreatedAt) <= end && (p.DeletedAt is null || p.DeletedAt > end)).Sum(p => p.Bytes);
            return new StoragePointDto(d, bytes);
        }).ToList();
        var failures = runsInRange.Where(r => r.Status == RunStatus.Failed).GroupBy(r => r.FailureClass)
            .Select(g => new FailureSliceDto(g.Key.ToDto(), g.Count())).OrderByDescending(f => f.Count).ToList();

        var upcoming = jobs.Where(j => j.IsScheduled && j.NextRunAt != null).OrderBy(j => j.NextRunAt).Take(6)
            .Select(j => new UpcomingRunDto(j.Id, j.Name, j.Server?.Name ?? string.Empty, j.NextRunAt!.Value, SchedulePlanner.Describe(j.CronExpression, j.TimeZone))).ToList();

        // Attention list from latest run per job.
        var jobIds = jobs.Select(j => j.Id).ToList();
        var lastRuns = await db.BackupRuns.AsNoTracking().Where(r => jobIds.Contains(r.BackupJobId))
            .GroupBy(r => r.BackupJobId).Select(g => g.OrderByDescending(r => r.CreatedAt).First()).ToListAsync(ct);
        var lastRunByJob = lastRuns.ToDictionary(r => r.BackupJobId);
        var successByJob = await db.BackupRuns.Where(r => jobIds.Contains(r.BackupJobId) && r.Status == RunStatus.Completed)
            .GroupBy(r => r.BackupJobId).Select(g => new { g.Key, At = g.Max(r => r.CompletedAt) }).ToDictionaryAsync(x => x.Key, x => x.At, ct);
        var offloadFailed = await db.OffloadRuns.Where(o => o.BackupJobId != null && jobIds.Contains(o.BackupJobId!.Value))
            .GroupBy(o => o.BackupJobId!.Value).Select(g => new { g.Key, Failed = g.OrderByDescending(o => o.CreatedAt).Select(o => o.Status == OffloadStatus.Failed && o.DismissedAt == null).First() })
            .ToDictionaryAsync(x => x.Key, x => x.Failed, ct);
        var attention = jobs.Select(j =>
        {
            lastRunByJob.TryGetValue(j.Id, out var lr);
            var (h, reason) = JobHealth.Evaluate(j, lr, successByJob.GetValueOrDefault(j.Id), offloadFailed.GetValueOrDefault(j.Id), now);
            return new { j, lr, h, reason };
        }).Where(x => x.h is HealthState.Critical or HealthState.Warning)
          .OrderBy(x => x.h == HealthState.Critical ? 0 : 1).ThenBy(x => x.j.Name)
          .Select(x => new AttentionItemDto(x.j.Id, x.j.Name, x.j.Server?.Name ?? string.Empty, x.h, x.reason ?? string.Empty, x.lr?.CreatedAt)).ToList();

        var recentRuns = await db.BackupRuns.AsNoTracking().Include(r => r.Server).Where(r => r.DismissedAt == null).OrderByDescending(r => r.CreatedAt).Take(8).ToListAsync(ct);
        var recentRestores = await db.RestoreRuns.AsNoTracking().Include(r => r.Server).OrderByDescending(r => r.CreatedAt).Take(5).ToListAsync(ct);
        var restoresInRange = await db.RestoreRuns.CountAsync(r => r.CreatedAt >= from && r.CreatedAt <= to, ct);

        var worker = await db.WorkerNodes.AsNoTracking().FirstOrDefaultAsync(ct);
        var nfs = await db.StorageSettings.AsNoTracking().FirstOrDefaultAsync(ct);
        var heartbeats = await db.WorkerHeartbeats.AsNoTracking().ToListAsync(ct);
        var onlineWorkers = heartbeats.Where(h => now - h.LastSeenAt < TimeSpan.FromMinutes(2)).ToList();
        var failingDestinations = await db.S3Destinations.CountAsync(d => d.LastConnectionTestSucceeded == false, ct);

        var system = new SystemHealthDto(true, onlineWorkers.Count > 0, heartbeats.Count == 0 ? null : heartbeats.Max(h => h.LastSeenAt), onlineWorkers.Count,
            worker is not null, worker?.HostKeyFingerprint is not null, nfs is not null && !string.IsNullOrEmpty(nfs.NfsPath),
            servers.Count(s => s.HostKeyFingerprint is null), failingDestinations);

        var kpis = new DashboardKpisDto(protectedServerIds.Count, servers.Count, jobs.Count(j => j.Status == JobStatus.Active), jobs.Count(j => j.IsScheduled),
            runsInRange.Count, succeeded, failed, running, successRate, lastSuccess, livePoints.Sum(p => p.Bytes), livePoints.Count, restoresInRange, attention.Count);

        return new DashboardDto(from, to, kpis, runTrend, durationTrend, storageGrowth, failures, upcoming, attention,
            recentRuns.Select(r => r.ToSummary()).ToList(), recentRestores.Select(r => r.ToSummary()).ToList(), system);
    }

    public async Task<IReadOnlyList<AlertDto>> AlertsAsync(CancellationToken ct)
    {
        var now = clock.UtcNow;
        var since = now.AddHours(-24);
        var alerts = new List<AlertDto>();
        var failedRuns = await db.BackupRuns.AsNoTracking().Include(r => r.Server).Where(r => r.Status == RunStatus.Failed && r.DismissedAt == null && r.CompletedAt >= since)
            .OrderByDescending(r => r.CompletedAt).Take(20).ToListAsync(ct);
        alerts.AddRange(failedRuns.Select(r => new AlertDto($"run-{r.Id}", HealthState.Critical, $"Backup failed: {r.JobName}",
            $"{r.Server?.Name} · {r.FailureClass} · {Truncate(r.ErrorMessage, 160)}", r.CompletedAt ?? r.CreatedAt, $"/runs/{r.Id}")));
        var failedOffloads = await db.OffloadRuns.AsNoTracking().Where(o => o.Status == OffloadStatus.Failed && o.CompletedAt >= since)
            .WhereNeedsAttention(db.OffloadRuns)   // a retried failure or a v1 record is no longer an alert
            .OrderByDescending(o => o.CompletedAt).Take(10).ToListAsync(ct);
        alerts.AddRange(failedOffloads.Select(o => new AlertDto($"offload-{o.Id}", HealthState.Critical, $"S3 offload failed: {o.JobName}",
            Truncate(o.ErrorMessage, 160) ?? string.Empty, o.CompletedAt ?? o.CreatedAt, o.BackupRunId is { } br ? $"/runs/{br}" : "/recovery-points")));
        var failedRestores = await db.RestoreRuns.AsNoTracking().Where(r => r.Status == RestoreStatus.Failed && r.CompletedAt >= since).Take(10).ToListAsync(ct);
        alerts.AddRange(failedRestores.Select(r => new AlertDto($"restore-{r.Id}", HealthState.Critical, $"Restore failed: {r.JobName}",
            Truncate(r.ErrorMessage, 160) ?? string.Empty, r.CompletedAt ?? r.CreatedAt, $"/restores/{r.Id}")));

        var heartbeats = await db.WorkerHeartbeats.AsNoTracking().ToListAsync(ct);
        if (heartbeats.Count == 0 || heartbeats.All(h => now - h.LastSeenAt > TimeSpan.FromMinutes(2)))
        {
            alerts.Add(new AlertDto("worker-offline", HealthState.Critical, "No backup worker online",
                "Scheduled and manual runs will not execute until a Backora worker is running.", heartbeats.Count == 0 ? now : heartbeats.Max(h => h.LastSeenAt), "/settings"));
        }
        var untrusted = await db.Servers.CountAsync(s => s.HostKeyFingerprint == null, ct);
        if (untrusted > 0) alerts.Add(new AlertDto("untrusted-servers", HealthState.Warning, $"{untrusted} server(s) awaiting host-key trust", "Backups to these servers are blocked until their SSH fingerprint is verified.", now, "/servers"));
        var failedNotifications = await db.NotificationMessages.CountAsync(m => m.Status == NotificationStatus.Failed && m.UpdatedAt >= since, ct);
        if (failedNotifications > 0) alerts.Add(new AlertDto("notifications-failed", HealthState.Warning, $"{failedNotifications} notification(s) could not be delivered", "Check SMTP and Teams settings.", now, "/settings"));
        return alerts.OrderByDescending(a => a.Severity == HealthState.Critical).ThenByDescending(a => a.OccurredAt).ToList();
    }

    private static string? Truncate(string? s, int max) => s is null ? null : s.Length <= max ? s : s[..max] + "…";
}

public sealed record AuditQuery(DateTimeOffset? From, DateTimeOffset? To, string? Action, string? Actor, string? ResourceType, AuditOutcomeDto? Outcome);

public sealed class AuditQueryService(IAppDbContext db)
{
    public async Task<PagedResult<AuditEventDto>> ListAsync(AuditQuery f, PageRequest page, CancellationToken ct)
    {
        var q = db.AuditEvents.AsNoTracking().AsQueryable();
        if (f.From is { } from) q = q.Where(e => e.OccurredAt >= from);
        if (f.To is { } to) q = q.Where(e => e.OccurredAt <= to);
        if (!string.IsNullOrWhiteSpace(f.Action)) q = q.Where(e => EF.Functions.ILike(e.Action, f.Action.Trim() + "%"));
        if (!string.IsNullOrWhiteSpace(f.Actor)) q = q.Where(e => EF.Functions.ILike(e.ActorName, "%" + f.Actor.Trim() + "%"));
        if (!string.IsNullOrWhiteSpace(f.ResourceType)) q = q.Where(e => e.ResourceType == f.ResourceType);
        if (f.Outcome is { } o) q = q.Where(e => e.Outcome == o.ToDomain());
        q = page.Descending || page.Sort is null ? q.OrderByDescending(e => e.OccurredAt).ThenByDescending(e => e.Id) : q.OrderBy(e => e.OccurredAt);
        var total = await q.LongCountAsync(ct);
        var items = await q.Skip(page.Skip).Take(page.SafePageSize).ToListAsync(ct);
        return new PagedResult<AuditEventDto>(items.Select(e => new AuditEventDto(e.Id, e.OccurredAt, e.ActorUserId, e.ActorName, e.ActorIp, e.Action,
            e.ResourceType, e.ResourceId, e.Outcome.ToDto(), e.CorrelationId, e.DetailsJson)).ToList(), page.SafePage, page.SafePageSize, total);
    }
}

public sealed class SearchService(IAppDbContext db, ICurrentUser user)
{
    public async Task<IReadOnlyList<SearchResultDto>> SearchAsync(string? q, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(q) || q.Trim().Length < 2) return [];
        var term = $"%{q.Trim()}%";
        var results = new List<SearchResultDto>();
        if (user.HasPermission(Permissions.JobsRead))
        {
            results.AddRange(await db.BackupJobs.AsNoTracking().Include(j => j.Server).Where(j => j.DeletedAt == null && EF.Functions.ILike(j.Name, term))
                .OrderBy(j => j.Name).Take(6).Select(j => new SearchResultDto("job", j.Id, j.Name, j.Category + " · " + j.Server!.Name, "/jobs/" + j.Id)).ToListAsync(ct));
        }
        if (user.HasPermission(Permissions.ServersRead))
        {
            results.AddRange(await db.Servers.AsNoTracking().Where(s => EF.Functions.ILike(s.Name, term) || EF.Functions.ILike(s.Host, term))
                .OrderBy(s => s.Name).Take(6).Select(s => new SearchResultDto("server", s.Id, s.Name, s.SshUser + "@" + s.Host, "/servers/" + s.Id + "/edit")).ToListAsync(ct));
        }
        if (user.HasPermission(Permissions.TemplatesRead))
        {
            results.AddRange(await db.ScriptTemplates.AsNoTracking().Where(t => EF.Functions.ILike(t.Name, term) || EF.Functions.ILike(t.Category, term))
                .OrderBy(t => t.Name).Take(5).Select(t => new SearchResultDto("template", t.Id, t.Name, t.Category, "/templates/" + t.Id)).ToListAsync(ct));
        }
        if (user.HasPermission(Permissions.StorageRead))
        {
            results.AddRange(await db.S3Destinations.AsNoTracking().Where(d => EF.Functions.ILike(d.Name, term) || EF.Functions.ILike(d.Bucket, term))
                .OrderBy(d => d.Name).Take(4).Select(d => new SearchResultDto("destination", d.Id, d.Name, d.Kind == StorageKind.Filesystem ? d.LocalPath ?? "" : d.Bucket, "/storage/" + d.Id)).ToListAsync(ct));
        }
        if (user.HasPermission(Permissions.RunsRead) && long.TryParse(q.Trim().TrimStart('#'), out var runId))
        {
            var run = await db.BackupRuns.AsNoTracking().FirstOrDefaultAsync(r => r.Id == runId, ct);
            if (run is not null) results.Insert(0, new SearchResultDto("run", run.Id, $"Run #{run.Id}", $"{run.JobName} · {run.Status}", "/runs/" + run.Id));
        }
        return results;
    }
}

public sealed class SystemService(IAppDbContext db, IClock clock)
{
    public async Task<SystemInfoDto> InfoAsync(string environment, CancellationToken ct)
    {
        var now = clock.UtcNow;
        var hbs = await db.WorkerHeartbeats.AsNoTracking().OrderByDescending(h => h.LastSeenAt).ToListAsync(ct);
        return new SystemInfoDto(typeof(SystemService).Assembly.GetName().Version?.ToString() ?? "2.0.0", environment,
            hbs.Select(h => new WorkerInstanceDto(h.WorkerId, h.HostName, h.Version, h.StartedAt, h.LastSeenAt, h.ActiveOperations, now - h.LastSeenAt < TimeSpan.FromMinutes(2))).ToList(), now);
    }
}
