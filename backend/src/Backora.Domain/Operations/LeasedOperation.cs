using Backora.Domain.Common;

namespace Backora.Domain.Operations;

/// <summary>
/// Durable unit of background work. Ownership is coordinated through the <c>operation_leases</c> table
/// (claimed with <c>FOR UPDATE SKIP LOCKED</c>, renewed by heartbeats); an expired lease means the owning
/// worker died and another worker adopts the operation. Lease renewals never touch this row, so they do not
/// interfere with optimistic concurrency on the operation itself.
/// </summary>
public abstract class LeasedOperation : Entity
{
    public int Attempt { get; set; }
    public int MaxAttempts { get; set; } = 3;
    public DateTimeOffset NextAttemptAt { get; set; }
    /// <summary>Worker instance that last processed this operation (informational).</summary>
    public string? ProcessedBy { get; set; }
    public DateTimeOffset? HeartbeatAt { get; set; }

    public FailureClass FailureClass { get; set; } = FailureClass.None;
    public string? ErrorMessage { get; set; }

    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }

    /// <summary>Accumulated output (bounded tail kept in the database).</summary>
    public string? Log { get; set; }

    /// <summary>Byte offset already read from the remote log file.</summary>
    public long LogOffset { get; set; }

    public string? RemoteDirectory { get; set; }
    public int? RemotePid { get; set; }
    public string? CorrelationId { get; set; }

    public const int MaxLogChars = 512 * 1024;

    public void AppendLog(string? text)
    {
        if (string.IsNullOrEmpty(text)) return;
        var combined = string.Concat(Log, text);
        if (combined.Length > MaxLogChars)
        {
            combined = "…[earlier output truncated]…\n" + combined[^(MaxLogChars - 64)..];
        }
        Log = combined;
    }

    public void AppendLogLine(DateTimeOffset now, string line) =>
        AppendLog($"[{now:yyyy-MM-dd HH:mm:ss}Z] {line}\n");

    public TimeSpan? Duration => StartedAt is null ? null : (CompletedAt ?? DateTimeOffset.UtcNow) - StartedAt.Value;

    /// <summary>Schedules another attempt with exponential backoff (30s, 60s, 120s … capped at 15 min).</summary>
    public bool TryScheduleRetry(DateTimeOffset now)
    {
        if (Attempt >= MaxAttempts) return false;
        var delaySeconds = Math.Min(900, 30 * Math.Pow(2, Math.Max(0, Attempt - 1)));
        NextAttemptAt = now.AddSeconds(delaySeconds);
        return true;
    }
}
