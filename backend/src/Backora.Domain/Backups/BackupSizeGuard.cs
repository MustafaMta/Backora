namespace Backora.Domain.Backups;

/// <summary>
/// A "successful" backup that is a sliver of the job's recent good backups almost always means the script dumped the
/// wrong thing (another database, a user without privileges, an empty mount) rather than that the data vanished.
/// Such a run must not count as a recovery point: it would rotate good points out under retention.
/// </summary>
public static class BackupSizeGuard
{
    /// <summary>Below this, recent backups are too small for a ratio to mean anything.</summary>
    public const long MinimumBaselineBytes = 50L * 1024 * 1024;

    /// <summary>A backup smaller than 1/<see cref="Ratio"/> of the baseline is refused.</summary>
    public const int Ratio = 100;

    /// <summary>Per-job variable that turns the check off when a database really did shrink: <c>SKIP_SIZE_CHECK__JOBKEY</c>.</summary>
    public const string SkipVariable = "SKIP_SIZE_CHECK";

    /// <param name="current">Bytes produced by this run.</param>
    /// <param name="baseline">Largest total of the job's recent completed runs, or null when there are none.</param>
    public static bool IsSuspicious(long current, long? baseline) =>
        baseline is { } b && b >= MinimumBaselineBytes && current * (long)Ratio < b;

    /// <summary>True for a compressed backup file (zstd or gzip/pigz), possibly encrypted afterwards.</summary>
    public static bool IsCompressed(string path)
    {
        var p = path.EndsWith(".gpg", StringComparison.OrdinalIgnoreCase) ? path[..^4] : path;
        return p.EndsWith(".zst", StringComparison.OrdinalIgnoreCase) || p.EndsWith(".gz", StringComparison.OrdinalIgnoreCase);
    }
}
