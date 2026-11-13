using Backora.Domain.Backups;

namespace Backora.Application.Execution;

/// <summary>
/// Folds a progress sample into the run: bytes, expected total, stage and a smoothed transfer rate. The template refreshes
/// its progress every ~10 seconds while the worker polls more often, so a sample only counts once the bytes moved (or a
/// minute passed without movement, which shows a rate of 0: the run is really not writing anything).
/// </summary>
public static class RunProgress
{
    private static readonly TimeSpan MinInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan StallInterval = TimeSpan.FromSeconds(60);

    public static void Apply(BackupRun run, ProgressSample sample, DateTimeOffset now)
    {
        var stage = sample.Stage ?? (sample.FromTemplate ? null : "written");
        if (run.ProgressAt is not { } at || run.ProgressBytes is not { } previous || run.ProgressStage != stage)
        {
            Set(run, sample, stage, now, rate: null);
            return;
        }

        run.ProgressTotalBytes = sample.TotalBytes;
        var elapsed = now - at;
        if (elapsed < MinInterval || (sample.Bytes == previous && elapsed < StallInterval)) return;

        var delta = sample.Bytes - previous;
        if (delta < 0)
        {
            // A process of the run finished and took its counters with it; start measuring again.
            Set(run, sample, stage, now, rate: null);
            return;
        }
        // A whole minute without a single new byte is a stall, shown as such at once (smoothing would only fade it).
        var current = (long)(delta / elapsed.TotalSeconds);
        var smoothed = delta == 0 ? 0 : run.ProgressBytesPerSecond is { } old and > 0 ? (long)(0.6 * current + 0.4 * old) : current;
        Set(run, sample, stage, now, smoothed);
    }

    /// <summary>Percent done (0–99.9 while running), or null when the total is unknown.</summary>
    public static double? Percent(long? bytes, long? total) =>
        bytes is { } b && total is > 0 ? Math.Min(99.9, Math.Max(0, 100.0 * b / total.Value)) : null;

    /// <summary>Time left at the current rate, or null when it cannot be estimated.</summary>
    public static TimeSpan? Remaining(long? bytes, long? total, long? bytesPerSecond) =>
        bytes is { } b && total is { } t && bytesPerSecond is > 0 && t > b ? TimeSpan.FromSeconds((t - b) / (double)bytesPerSecond.Value) : null;

    private static void Set(BackupRun run, ProgressSample s, string? stage, DateTimeOffset now, long? rate)
    {
        run.ProgressBytes = s.Bytes;
        run.ProgressTotalBytes = s.TotalBytes;
        run.ProgressStage = stage;
        run.ProgressAt = now;
        run.ProgressBytesPerSecond = rate;
    }
}
