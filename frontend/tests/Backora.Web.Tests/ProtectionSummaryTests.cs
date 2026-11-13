using Backora.Contracts.V1;
using Backora.Web.Services;

namespace Backora.Web.Tests;

public class ProtectionSummaryTests
{
    private static DashboardDto Dash(SystemHealthDto s, int total = 2, int protectedServers = 2, params AttentionItemDto[] attention) => new(
        DateTimeOffset.UtcNow.AddDays(-14), DateTimeOffset.UtcNow,
        new DashboardKpisDto(protectedServers, total, 1, 1, 10, 9, 1, 0, 90, DateTimeOffset.UtcNow, 1, 1, 0, attention.Length),
        [], [], [], [], [], attention, [], [], s);

    private static SystemHealthDto Ok() => new(true, true, DateTimeOffset.UtcNow, 1, true, true, true, 0, 0);

    [Fact]
    public void Healthy_system_is_all_protected() => Assert.Equal("All protected", ProtectionSummary.From(Dash(Ok())).Title);

    [Fact]
    public void Failing_site_and_critical_job_are_problems_warnings_are_reviews()
    {
        var s = ProtectionSummary.From(Dash(Ok() with { DestinationsFailing = 1 }, 2, 1,
            new AttentionItemDto(1, "a", "db", HealthState.Critical, "failed", null), new AttentionItemDto(2, "b", "db", HealthState.Warning, "late", null)));
        Assert.Equal(2, s.Problems);
        Assert.Equal(2, s.ToReview); // warning + unprotected server
    }
}
