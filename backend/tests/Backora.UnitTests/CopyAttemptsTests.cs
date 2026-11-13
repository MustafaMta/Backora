using Backora.Application.Backups;

namespace Backora.UnitTests;

/// <summary>A retry creates a new copy attempt; the one it replaced is superseded (same backup run and destination).</summary>
public class CopyAttemptsTests
{
    private static CopyAttempts.Key K(long id, long? run, long dest) => new(id, run, dest);

    [Fact]
    public void Each_attempt_points_to_the_attempt_that_replaced_it()
    {
        var next = CopyAttempts.NextAttempts([
            K(51443, 49711, 1),                     // primary upload, never retried
            K(51444, 49711, 2), K(51446, 49711, 2), // DR copy failed, then retried
            K(51450, 49711, 2),                     // retried again
        ]);
        Assert.Equal(51446, next[51444]);
        Assert.Equal(51450, next[51446]);
        Assert.False(next.ContainsKey(51450));   // the latest attempt is current
        Assert.False(next.ContainsKey(51443));
    }

    [Fact]
    public void Other_runs_destinations_and_legacy_rows_never_replace_an_attempt()
    {
        var next = CopyAttempts.NextAttempts([
            K(10, 1, 2), K(11, 2, 2),       // same destination, different run
            K(20, 3, 2), K(21, 3, 5),       // same run, different destination
            K(30, null, 2), K(31, null, 2), // legacy copies without a run
        ]);
        Assert.Empty(next);
    }
}
