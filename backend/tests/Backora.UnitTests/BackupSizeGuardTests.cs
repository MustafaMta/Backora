using Backora.Domain.Backups;

namespace Backora.UnitTests;

/// <summary>A "successful" backup that is a sliver of recent good ones is refused (wrong database, user or privileges).</summary>
public class BackupSizeGuardTests
{
    private const long MB = 1024 * 1024, GB = 1024 * MB;

    [Fact]
    public void A_10_KB_dump_of_a_23_GB_database_is_suspicious() =>
        Assert.True(BackupSizeGuard.IsSuspicious(10_240, 25_242_596_540));

    [Theory]
    [InlineData(300 * MB, 23 * GB)]       // 1.3%: a big shrink, but plausible (purge, partitions dropped)
    [InlineData(10_240, 49 * MB)]          // baseline too small for a ratio to mean anything
    [InlineData(10_240, null)]             // no history yet
    [InlineData(25 * GB, 23 * GB)]         // grew
    public void Normal_variation_and_missing_history_are_accepted(long current, long? baseline) =>
        Assert.False(BackupSizeGuard.IsSuspicious(current, baseline));
}
