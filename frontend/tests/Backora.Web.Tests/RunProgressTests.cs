using Backora.Contracts.V1;
using Backora.Web.Components.Shared;
using Bunit;

namespace Backora.Web.Tests;

/// <summary>The live progress readout on a running backup: real figures, honest about what it doesn't know.</summary>
public class RunProgressTests : BunitContext
{
    private const long GB = 1L << 30;
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    public RunProgressTests() => this.AddBackora();

    private IRenderedComponent<RunProgress> Show(RunProgressDto p) =>
        Render<RunProgress>(x => x.Add(c => c.P, p).Add(c => c.StartedAt, Now.AddHours(-2)));

    [Fact]
    public void A_known_total_shows_percent_bar_speed_and_time_left()
    {
        var cut = Show(new RunProgressDto(767 * GB, 937 * GB, 81.9, 52_000_000, 3400, "copy", true, Now.AddSeconds(-5)));

        Assert.Contains("81.9", cut.Find(".rp__figure").TextContent);
        var bar = cut.Find("[role=progressbar]");
        Assert.Equal("82", bar.GetAttribute("aria-valuenow"));
        Assert.DoesNotContain("indeterminate", bar.ClassName);
        Assert.Contains("About 57 min left", cut.Find(".rp__eta-main").TextContent);
        Assert.Contains("767 GB", cut.Markup);
        Assert.Contains("of 937 GB", cut.Markup);
        Assert.Contains("GB/min", cut.Markup);
        Assert.Contains("Copying data files", cut.Find(".rp__stage").TextContent);
        Assert.Contains("rp--live", cut.Find(".rp").ClassName);
    }

    [Fact]
    public void An_unknown_total_shows_bytes_written_and_an_indeterminate_bar()
    {
        var cut = Show(new RunProgressDto(818 * GB, null, null, 49_000_000, null, "written", false, Now));

        Assert.Contains("818 GB", cut.Find(".rp__figure").TextContent);
        Assert.Contains("ui-progress--indeterminate", cut.Find("[role=progressbar]").ClassName);
        Assert.Contains("Total size not known in advance", cut.Markup);
        Assert.Contains("Measured from the server processes", cut.Markup);
    }

    [Fact]
    public void A_run_that_stopped_writing_is_flagged()
    {
        var cut = Show(new RunProgressDto(41 * GB, 562 * GB, 7.3, 0, null, "copy", true, Now.AddMinutes(-7)));

        Assert.Contains("rp--stalled", cut.Find(".rp").ClassName);
        Assert.Contains("No data written for", cut.Find(".rp__eta-main").TextContent);
        Assert.Contains("idle", cut.Markup);
    }

    [Fact]
    public void The_checkpoint_wait_is_explained_instead_of_zero_percent()
    {
        var cut = Show(new RunProgressDto(0, null, null, null, null, "checkpoint", true, Now));

        Assert.Equal("Waiting", cut.Find(".rp__figure").TextContent.Trim());
        Assert.Contains("Waiting for PostgreSQL's checkpoint; copying starts right after", cut.Markup);
        Assert.DoesNotContain("0 B written", cut.Markup);
    }

    [Fact]
    public void Nothing_renders_without_progress() => Assert.Empty(Render<RunProgress>(x => x.Add(c => c.P, null)).Markup.Trim());

    [Fact]
    public void Waiting_for_the_backup_lock_is_explained_not_shown_as_stuck_at_100()
    {
        var cut = Show(new RunProgressDto(75 * GB, 75 * GB, 99.9, 0, null, "lock-wait", true, Now.AddMinutes(-6)));

        Assert.Contains("rp--waiting", cut.Find(".rp").ClassName);
        Assert.Equal("Waiting", cut.Find(".rp__figure").TextContent.Trim());
        Assert.Contains("Waiting for long-running queries to finish", cut.Markup);
        Assert.DoesNotContain("No data written", cut.Markup);
    }

    [Fact]
    public void Finishing_and_an_exceeded_estimate_never_read_as_100_percent()
    {
        var finishing = Show(new RunProgressDto(76 * GB, 75 * GB, 99.9, 1_000, null, "finishing", true, Now));
        Assert.Equal("Finishing", finishing.Find(".rp__figure").TextContent.Trim());

        var over = Show(new RunProgressDto(76 * GB, 75 * GB, 99.96, 40_000_000, null, "copy", true, Now));
        Assert.Contains("99.9", over.Find(".rp__figure").TextContent);
        Assert.DoesNotContain("100", over.Find(".rp__figure").TextContent);
        Assert.Contains("Almost done", over.Markup);
    }
}
