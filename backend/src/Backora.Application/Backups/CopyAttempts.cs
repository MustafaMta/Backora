using Backora.Application.Abstractions;
using Backora.Domain.Backups;
using Microsoft.EntityFrameworkCore;

namespace Backora.Application.Backups;

/// <summary>
/// Retrying a copy (a primary upload or a DR copy) creates a new <see cref="OffloadRun"/>; the failed attempt stays
/// as history. An attempt is <em>superseded</em> when a later attempt exists for the same backup run and destination.
/// A superseded failure is resolved: it isn't offered for retry, alerted on, or listed under "Failed".
/// </summary>
public static class CopyAttempts
{
    public readonly record struct Key(long Id, long? BackupRunId, long DestinationId);

    /// <summary>For each superseded attempt, the id of the attempt that directly followed it (same run and destination).</summary>
    public static Dictionary<long, long> NextAttempts(IEnumerable<Key> attempts)
    {
        var next = new Dictionary<long, long>();
        foreach (var site in attempts.Where(a => a.BackupRunId is not null).GroupBy(a => (a.BackupRunId!.Value, a.DestinationId)))
        {
            var ordered = site.OrderBy(a => a.Id).ToList();
            for (var i = 0; i < ordered.Count - 1; i++) next[ordered[i].Id] = ordered[i + 1].Id;
        }
        return next;
    }

    /// <summary>Superseded attempts among <paramref name="page"/>, judged against every attempt of the runs involved.</summary>
    public static async Task<Dictionary<long, long>> NextAttemptsAsync(IAppDbContext db, IReadOnlyCollection<OffloadRun> page, CancellationToken ct)
    {
        var runs = page.Where(o => o.BackupRunId is not null).Select(o => o.BackupRunId!.Value).Distinct().ToList();
        if (runs.Count == 0) return new Dictionary<long, long>();
        var keys = await db.OffloadRuns.AsNoTracking()
            .Where(o => o.BackupRunId != null && runs.Contains(o.BackupRunId!.Value))
            .Select(o => new Key(o.Id, o.BackupRunId, o.S3DestinationId))
            .ToListAsync(ct);
        return NextAttempts(keys);
    }

    /// <summary>Keeps attempts that no later attempt replaced (one correlated NOT EXISTS in SQL).</summary>
    public static IQueryable<OffloadRun> WhereCurrent(this IQueryable<OffloadRun> query, IQueryable<OffloadRun> all) =>
        query.Where(o => o.BackupRunId == null
            || !all.Any(x => x.BackupRunId == o.BackupRunId && x.S3DestinationId == o.S3DestinationId && x.Id > o.Id));

    /// <summary>
    /// Failures that still need attention: the current attempt, not a record imported from Backora v1 (whose files
    /// Backora never tracked, so it cannot be retried), and not cleared by someone. The others stay visible as history
    /// under "All".
    /// </summary>
    public static IQueryable<OffloadRun> WhereNeedsAttention(this IQueryable<OffloadRun> query, IQueryable<OffloadRun> all) =>
        query.WhereCurrent(all).Where(o => !o.LegacyPrefix && o.DismissedAt == null);
}
