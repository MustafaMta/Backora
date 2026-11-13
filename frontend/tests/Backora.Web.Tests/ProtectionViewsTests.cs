using Backora.Contracts.Security;
using Backora.Contracts.V1;
using Backora.Web.Components.Pages;
using Backora.Web.Components.Shared;
using Bunit;

namespace Backora.Web.Tests;

/// <summary>The protection views tell the truth: real DR routes, and no "running" claim when every job is paused.</summary>
public class ProtectionViewsTests : BunitContext
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    private static S3DestinationDto Site(long id, string name) => new(id, name, $"https://s3-{id}.example", "eu-1", $"b{id}", "AK…", false, null, Now, true, null, 1, 1, 10, 1, Now);

    [Fact]
    public void Dr_map_shows_one_primary_and_every_dr_site_with_the_latest_copy_state()
    {
        this.AddBackora();
        var job = TestData.Job(1) with { S3DestinationId = 1, ReplicationTargets = [new(2, "DR A", "b2"), new(3, "DR B", "b3")] };
        var okCopy = TestData.RecoveryPoint(50) with { DestinationId = 2, DestinationName = "DR A", SourceOffloadId = 40, SourceDestinationName = "Primary S3" };
        var failedCopy = TestData.RecoveryPoint(51) with { DestinationId = 3, DestinationName = "DR B", SourceOffloadId = 40, SourceDestinationName = "Primary S3", Status = OffloadStatusDto.Failed };

        var cut = Render<DrTopology>(p => p.Add(x => x.Jobs, [job]).Add(x => x.Sites, [Site(1, "Primary S3"), Site(2, "DR A"), Site(3, "DR B"), Site(4, "Spare")])
            .Add(x => x.Copies, [okCopy, failedCopy]));

        Assert.Single(cut.FindAll("[data-node^='p']"));
        Assert.Equal(2, cut.FindAll("[data-node^='d']").Count);
        Assert.Contains("topo__link--ok", cut.Find("path[data-from='p1'][data-to='d2']").GetAttribute("class"));
        Assert.Contains("topo__link--bad", cut.Find("path[data-from='p1'][data-to='d3']").GetAttribute("class"));
        Assert.Contains("1 storage site isn't used by any backup yet", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void Overview_never_says_backups_are_running_when_every_job_is_paused()
    {
        var backend = this.AddBackora(Permissions.DashboardRead, Permissions.JobsRead, Permissions.StorageRead, Permissions.RecoveryPointsRead);
        var system = new SystemHealthDto(true, true, Now, 1, true, true, true, 0, 0);
        var dash = new DashboardDto(Now.AddDays(-14), Now, new DashboardKpisDto(0, 2, 0, 0, 10, 10, 0, 0, 100, Now.AddHours(-3), 1, 1, 0, 0),
            [], [], [], [], [], [], [], [], system);
        backend.OnJson(HttpMethod.Get, "/api/v1/dashboard", dash);
        backend.OnJson(HttpMethod.Get, "/api/v1/storage/s3", new List<S3DestinationDto>());
        backend.OnJson(HttpMethod.Get, "/api/v1/backup-jobs", TestData.Page(TestData.Job(1) with { Status = JobStatusDto.Inactive }));
        backend.OnJson(HttpMethod.Get, "/api/v1/recovery-points", TestData.Page<OffloadSummaryDto>());

        var cut = Render<Overview>();
        cut.WaitForAssertion(() => Assert.Contains("every job is paused", cut.Markup, StringComparison.Ordinal));
        Assert.DoesNotContain("Backups are running", cut.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain("operating normally", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("Not yet", cut.Markup, StringComparison.Ordinal); // no completed restore → restore not proven
    }
}
