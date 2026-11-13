namespace Backora.Domain.Retention;

public sealed record RecoveryPointInfo(
    long Id,
    long? BackupJobId,
    DateTimeOffset CreatedAt,
    bool Verified,
    bool IsProtected,
    bool HasActiveRestore,
    RetentionState State);

public enum RetentionAction { Keep, Delete }

public sealed record RetentionDecision(long RecoveryPointId, RetentionAction Action, string Reason);

/// <summary>
/// Pure retention policy. A recovery point is deleted only when ALL of the following hold:
/// it is older than the retention period, it is not protected (legal/operational hold), no restore is
/// using it, and deleting it would still leave at least <c>minimumToKeep</c> newer verified points.
/// Full backups produced by the legacy scripts are independent (no incremental chains), so there are no
/// chain dependencies to honour; the planner keeps the hook for that via <paramref name="dependents"/>.
/// </summary>
public static class RetentionPlanner
{
    public static IReadOnlyList<RetentionDecision> Plan(
        IEnumerable<RecoveryPointInfo> points,
        decimal retentionDays,
        int minimumToKeep,
        DateTimeOffset now,
        IReadOnlyDictionary<long, int>? dependents = null)
    {
        var ordered = points
            .Where(p => p.State is RetentionState.Active or RetentionState.Expired or RetentionState.DeleteFailed)
            .OrderByDescending(p => p.CreatedAt)
            .ToList();

        var decisions = new List<RetentionDecision>(ordered.Count);
        if (retentionDays <= 0)
        {
            decisions.AddRange(ordered.Select(p => new RetentionDecision(p.Id, RetentionAction.Keep, "retention disabled")));
            return decisions;
        }

        var cutoff = now - TimeSpan.FromSeconds((double)(retentionDays * 86400m));
        var keepFloor = Math.Max(1, minimumToKeep);
        var verifiedKept = 0;

        foreach (var p in ordered)
        {
            string? keepReason = null;
            if (p.IsProtected) keepReason = "protected (hold)";
            else if (p.HasActiveRestore) keepReason = "restore in progress";
            else if (dependents is not null && dependents.TryGetValue(p.Id, out var deps) && deps > 0) keepReason = "in use (copy or dependent recovery point in progress)";
            else if (p.CreatedAt > cutoff) keepReason = "within retention period";
            else if (p.Verified && verifiedKept < keepFloor) keepReason = $"minimum of {keepFloor} verified recovery point(s) kept";

            if (p.Verified && keepReason is not null)
            {
                verifiedKept++;
            }

            decisions.Add(keepReason is null
                ? new RetentionDecision(p.Id, RetentionAction.Delete, $"older than {retentionDays} day(s)")
                : new RetentionDecision(p.Id, RetentionAction.Keep, keepReason));
        }

        return decisions;
    }
}
