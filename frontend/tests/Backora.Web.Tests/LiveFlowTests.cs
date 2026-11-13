using Backora.Web.Components.Shared;
using Bunit;
using static Backora.Web.Components.Shared.LiveFlow;

namespace Backora.Web.Tests;

/// <summary>The live pipeline shown on running backups and restores: states map to the right classes and announcements.</summary>
public class LiveFlowTests : BunitContext
{
    [Fact]
    public void Stages_render_their_state_and_links_follow_progress()
    {
        IReadOnlyList<Stage> stages =
        [
            new("clock", "Queued", "Picked up", FlowState.Done),
            new("ssh", "Connect", "db-01", FlowState.Done),
            new("database", "Back up", "Running the script", FlowState.Active),
            new("verified", "Verify", "Checksums", FlowState.Pending),
        ];
        var cut = Render<LiveFlow>(p => p.Add(x => x.Stages, stages).Add(x => x.Label, "Backup progress"));

        var nodes = cut.FindAll(".lf__stage");
        Assert.Equal(4, nodes.Count);
        Assert.Contains("lf__stage--done", nodes[0].ClassName);
        Assert.Contains("lf__stage--active", nodes[2].ClassName);
        Assert.Equal("step", nodes[2].GetAttribute("aria-current"));
        Assert.Contains("lf__stage--pending", nodes[3].ClassName);

        var links = cut.FindAll(".lf__link");
        Assert.Equal(3, links.Count);
        Assert.Contains("lf__link--done", links[0].ClassName);
        Assert.Contains("lf__link--flowing", links[1].ClassName);   // light travels into the active stage
        Assert.Contains("lf__link--pending", links[2].ClassName);
        Assert.Equal("Backup progress", cut.Find(".lf").GetAttribute("aria-label"));
    }

    [Fact]
    public void Waiting_and_failed_stages_are_announced()
    {
        IReadOnlyList<Stage> stages =
        [
            new("clock", "Queued", null, FlowState.Done),
            new("private-key", "Unlock", "Needs your key", FlowState.Waiting),
            new("verified", "Verify", null, FlowState.Failed),
        ];
        var cut = Render<LiveFlow>(p => p.Add(x => x.Stages, stages));
        Assert.Contains("waiting for you", cut.Markup);
        Assert.Contains("lf__stage--failed", cut.FindAll(".lf__stage")[2].ClassName);
        Assert.Contains("lf__link--failed", cut.FindAll(".lf__link")[1].ClassName);
    }
}
