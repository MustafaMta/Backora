using System.Text.Json;
using Backora.Contracts.Security;
using Backora.Contracts.V1;
using Backora.Web.Components.Pages.Backups;
using Bunit;

namespace Backora.Web.Tests;

/// <summary>The V2 create-backup wizard: real request body, several DR sites, primary never repeated, paused sites not selectable.</summary>
public class BackupWizardTests : BunitContext
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    private static S3DestinationDto Dest(long id, string name, bool enabled = true, bool encrypted = false) => new(
        id, name, $"https://s3-{id}.example", "eu-1", $"bucket-{id}", "AKIA…", false, null, Now.AddHours(-1), true, null, 0, 0, 0, 1, Now.AddDays(-30),
        encrypted ? EncryptionModeDto.CustomerKey : EncryptionModeDto.None, IsEnabled: enabled);

    private FakeBackend Arrange()
    {
        var backend = this.AddBackora(Permissions.JobsWrite, Permissions.StorageRead, Permissions.StorageWrite);
        backend.OnJson(HttpMethod.Get, "/api/v1/templates", new List<TemplateSummaryDto> { new(2, "pg_dumpall", "PostgreSQL", "Logical dump", 1, 0, Now) });
        backend.OnJson(HttpMethod.Get, "/api/v1/servers", TestData.Page(new ServerDto(5, "db-01", "10.0.0.5", 22, "backup", true, "SHA256:k", Now.AddDays(-2),
            Now.AddHours(-1), true, null, null, 0, null, HealthState.Unknown, 1, Now.AddDays(-9), Now)));
        backend.OnJson(HttpMethod.Get, "/api/v1/storage/s3", new List<S3DestinationDto>
        {
            Dest(1, "Primary S3"), Dest(2, "DR Site A", encrypted: true), Dest(3, "DR Site B"), Dest(4, "DR Site C", enabled: false),
        });
        backend.OnJson(HttpMethod.Post, "/api/v1/backup-jobs/schedule-preview", new SchedulePreviewResponse(true, null, "Daily at 02:00 (UTC)", [Now.AddHours(3)]));
        backend.OnJson(HttpMethod.Post, "/api/v1/backup-jobs", TestData.Job(7, "pg_dumpall — db-01"));
        return backend;
    }

    [Fact]
    public void Wizard_creates_a_backup_with_every_selected_dr_site()
    {
        var backend = Arrange();
        var cut = Render<BackupWizard>();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".ui-tile")));

        void Choose(string text) => cut.FindAll(".ui-tile").First(b => b.TextContent.Contains(text, StringComparison.Ordinal)).Click();
        void Next() => cut.FindAll(".create-card__foot button").First(b => b.TextContent.Contains("Continue", StringComparison.Ordinal)).Click();

        // 1 — single server and single type are preselected and the name is suggested from them.
        cut.WaitForAssertion(() => Assert.Equal("pg_dumpall — db-01", cut.Find("#name").GetAttribute("value")));
        Next();
        // 2 — the first enabled storage is the default primary.
        cut.WaitForAssertion(() => Assert.Contains("Where should copies be kept?", cut.Markup, StringComparison.Ordinal));
        Assert.Equal("true", cut.FindAll(".ui-tile").First(c => c.TextContent.Contains("Primary S3", StringComparison.Ordinal)).GetAttribute("aria-checked"));
        Next();
        // 3 — default schedule, previewed by the API.
        cut.WaitForAssertion(() => Assert.Contains("Daily at 02:00 (UTC)", cut.Markup, StringComparison.Ordinal));
        Next();
        // 4 — DR sites: primary not offered, paused site disabled, two sites picked.
        cut.WaitForAssertion(() => Assert.Contains("Add disaster recovery sites", cut.Markup, StringComparison.Ordinal));
        var group = cut.Find("[aria-label='DR sites']");
        Assert.DoesNotContain("Primary S3", group.TextContent, StringComparison.Ordinal);
        Assert.True(cut.FindAll(".ui-tile").First(c => c.TextContent.Contains("DR Site C", StringComparison.Ordinal)).HasAttribute("disabled"));
        Choose("DR Site A");
        Choose("DR Site B");
        Assert.Equal(2, cut.FindAll("[aria-label='DR sites'] .ui-tile[aria-checked='true']").Count);
        Next();
        // 5 — review and create.
        cut.WaitForAssertion(() => Assert.Contains("DR Site A, DR Site B", cut.Markup, StringComparison.Ordinal));
        Assert.Contains("Encrypted sites need your private key to restore", cut.Markup, StringComparison.Ordinal);
        cut.FindAll(".create-card__foot button").First(b => b.TextContent.Contains("Create backup", StringComparison.Ordinal)).Click();

        static bool IsCreate((HttpRequestMessage Request, string? Body) r) => r.Request.Method == HttpMethod.Post && r.Request.RequestUri!.AbsolutePath == "/api/v1/backup-jobs";
        cut.WaitForAssertion(() => Assert.Single(backend.Requests, IsCreate));
        using var json = JsonDocument.Parse(backend.Requests.Single(IsCreate).Body!);
        var root = json.RootElement;
        Assert.Equal(1, root.GetProperty("s3DestinationId").GetInt64());
        Assert.Equal([2L, 3L], root.GetProperty("replicaDestinationIds").EnumerateArray().Select(e => e.GetInt64()).Order());
        Assert.Equal("0 2 * * *", root.GetProperty("cronExpression").GetString());
        Assert.Equal(5, root.GetProperty("serverId").GetInt64());
        Assert.Equal(2, root.GetProperty("templateId").GetInt64());
    }

    [Fact]
    public void Model_drops_dr_sites_and_staging_copy_when_there_is_no_off_site_primary()
    {
        var m = new BackupWizard.WizardModel { Name = " x ", ServerId = 1, TemplateId = 2, DestinationId = 0, KeepLocalCopy = true, ReplicaIds = [3, 4] };
        var r = m.ToRequest();
        Assert.Null(r.S3DestinationId);
        Assert.Empty(r.ReplicaDestinationIds!);
        Assert.False(r.KeepLocalCopy);
        Assert.Equal("x", r.Name);
    }

    [Fact]
    public void Model_never_sends_the_primary_as_a_dr_site_or_duplicates()
    {
        var m = new BackupWizard.WizardModel { Name = "x", ServerId = 1, TemplateId = 2, DestinationId = 3, ReplicaIds = [3, 4, 4, 5] };
        Assert.Equal([4L, 5L], m.ToRequest().ReplicaDestinationIds!);
    }
}
