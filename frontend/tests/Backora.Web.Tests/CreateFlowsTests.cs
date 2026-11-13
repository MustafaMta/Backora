using System.Text.Json;
using Backora.Contracts.Security;
using Backora.Contracts.V1;
using Backora.Web.Components.Pages.Admin;
using Backora.Web.Components.Pages.Backups;
using Backora.Web.Components.Pages.Storage;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace Backora.Web.Tests;

/// <summary>The redesigned create flows: sensible defaults, honest encryption gating, DR assignment, invite drawer.</summary>
public class CreateFlowsTests : BunitContext
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    private static S3DestinationDto Dest(long id, string name, bool encrypted = false) => new(
        id, name, $"https://s3-{id}.example", "eu-1", $"bucket-{id}", "AKIA…", false, null, Now, true, null, 0, 0, 0, 1, Now,
        encrypted ? EncryptionModeDto.CustomerKey : EncryptionModeDto.None);

    private static ExecutionHostDto HostWithoutAge() => new(
        new WorkerNodeDto(true, "10.0.0.10", 22, "root", true, "SHA256:x", Now, "rclone", "/var/tmp/backora", Now, true, null, 1),
        new ExecutionHostProbeDto(Now, "exec", "Linux", "6.1", 2, 1, 1, 1, [new("age", "Encryption", null), new("gzip", "Archive", "1.12")], [], []),
        0, 0, 0, 0, 0, []);

    [Fact]
    public void Wizard_defaults_to_the_most_used_primary_and_blocks_encrypted_sites_without_the_encryption_tool()
    {
        var backend = this.AddBackora(Permissions.JobsWrite, Permissions.JobsRead, Permissions.StorageRead, Permissions.SettingsRead);
        backend.OnJson(HttpMethod.Get, "/api/v1/templates", new List<TemplateSummaryDto> { new(2, "pg_dumpall", "PostgreSQL", null, 1, 0, Now), new(3, "files-tar", "Files", null, 1, 0, Now) });
        backend.OnJson(HttpMethod.Get, "/api/v1/servers", TestData.Page(new ServerDto(5, "db-01", "10.0.0.5", 22, "backup", true, "fp", Now, Now, true, null, null, 0, null, HealthState.Unknown, 1, Now, Now)));
        backend.OnJson(HttpMethod.Get, "/api/v1/storage/s3", new List<S3DestinationDto> { Dest(1, "Alpha"), Dest(2, "Bravo"), Dest(3, "Vault", encrypted: true) });
        backend.OnJson(HttpMethod.Get, "/api/v1/backup-jobs", TestData.Page(TestData.Job(9) with { S3DestinationId = 2 }, TestData.Job(10) with { S3DestinationId = 2 }));
        backend.OnJson(HttpMethod.Get, "/api/v1/execution-host", HostWithoutAge());
        backend.OnJson(HttpMethod.Post, "/api/v1/backup-jobs/schedule-preview", new SchedulePreviewResponse(true, null, "Daily at 02:00 (UTC)", [Now]));
        Services.GetRequiredService<NavigationManager>().NavigateTo("/jobs/new?serverId=5&engine=postgresql");

        var cut = Render<BackupWizard>();
        cut.WaitForAssertion(() => Assert.Contains("Suggested", cut.Markup, StringComparison.Ordinal));
        Assert.Equal("true", cut.FindAll(".ui-tile").First(t => t.TextContent.Contains("pg_dumpall", StringComparison.Ordinal)).GetAttribute("aria-checked"));
        Assert.Contains("db-01", cut.Find(".ui-ss__trigger").TextContent, StringComparison.Ordinal);   // server is preselected in the searchable dropdown
        cut.FindAll(".create-card__foot button").First(b => b.TextContent.Contains("Continue", StringComparison.Ordinal)).Click();

        cut.WaitForAssertion(() => Assert.Contains("Where should copies be kept?", cut.Markup, StringComparison.Ordinal));
        var bravo = cut.FindAll(".ui-tile").First(t => t.TextContent.Contains("Bravo", StringComparison.Ordinal));
        Assert.Equal("true", bravo.GetAttribute("aria-checked"));                      // used by 2 jobs → default primary
        Assert.Contains("Most used", bravo.TextContent, StringComparison.Ordinal);
        Assert.True(cut.FindAll(".ui-tile").First(t => t.TextContent.Contains("Vault", StringComparison.Ordinal)).HasAttribute("disabled")); // encrypted, no age tool
        Assert.Contains("no encryption tool", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void New_dr_site_can_be_assigned_to_jobs_right_after_it_is_added()
    {
        var backend = this.AddBackora(Permissions.StorageWrite, Permissions.StorageRead, Permissions.JobsRead, Permissions.JobsWrite);
        var job = TestData.Job(7) with { S3DestinationId = 1, ReplicationTargets = [] };
        backend.OnJson(HttpMethod.Get, "/api/v1/backup-jobs", TestData.Page(job));
        backend.OnJson(HttpMethod.Post, "/api/v1/storage/s3", Dest(4, "DR Frankfurt"));
        backend.OnJson(HttpMethod.Get, "/api/v1/storage/s3/4", Dest(4, "DR Frankfurt"));
        backend.OnJson(HttpMethod.Put, "/api/v1/backup-jobs/7", job);
        Services.GetRequiredService<NavigationManager>().NavigateTo("/storage/new?purpose=dr");

        var cut = Render<StorageForm>();
        cut.WaitForAssertion(() => Assert.Contains("Where should extra copies live?", cut.Markup, StringComparison.Ordinal));
        cut.FindAll(".ui-tile").First(t => t.TextContent.Contains("Wasabi", StringComparison.Ordinal)).Click();
        cut.Find("#region").Change("eu-central-1");
        Assert.Contains("https://s3.eu-central-1.wasabisys.com", cut.Markup, StringComparison.Ordinal); // provider fills the endpoint
        cut.Find("#name").Change("DR Frankfurt"); cut.Find("#bucket").Change("dr-bucket"); cut.Find("#ak").Change("AKIA1"); cut.Find("#sk").Change("secret");
        cut.FindAll(".create-card__foot button").First(b => b.TextContent.Contains("Add DR site", StringComparison.Ordinal)).Click();

        cut.WaitForAssertion(() => Assert.Contains("Send copies to DR Frankfurt", cut.Markup, StringComparison.Ordinal));
        cut.FindAll(".ui-tile").First(t => t.TextContent.Contains(job.Name, StringComparison.Ordinal)).Click();
        cut.FindAll(".create-card__foot button").First(b => b.TextContent.Contains("Use as DR site", StringComparison.Ordinal)).Click();

        cut.WaitForAssertion(() => Assert.Single(backend.Requests, r => r.Request.Method == HttpMethod.Put));
        using var json = JsonDocument.Parse(backend.Requests.Single(r => r.Request.Method == HttpMethod.Put).Body!);
        Assert.Equal([4L], json.RootElement.GetProperty("replicaDestinationIds").EnumerateArray().Select(e => e.GetInt64()));
        using var created = JsonDocument.Parse(backend.Requests.Single(r => r.Request.Method == HttpMethod.Post).Body!);
        Assert.Equal("https://s3.eu-central-1.wasabisys.com", created.RootElement.GetProperty("endpoint").GetString());
    }

    [Fact]
    public void An_nfs_folder_on_the_execution_host_can_be_added_as_a_dr_site()
    {
        var backend = this.AddBackora(Permissions.StorageWrite, Permissions.StorageRead, Permissions.JobsRead, Permissions.JobsWrite);
        backend.OnJson(HttpMethod.Get, "/api/v1/backup-jobs", TestData.Page<BackupJobDto>());
        var created = Dest(5, "Branch DR") with { Kind = StorageKindDto.Filesystem, LocalPath = "/mnt/dr-site", Bucket = "", Endpoint = "file:///mnt/dr-site" };
        backend.OnJson(HttpMethod.Post, "/api/v1/storage/s3/test", new S3TestResultDto(true, "The folder /mnt/dr-site is reachable", [new CheckResultDto("Mounted", CheckState.Passed, "Mounted from 10.0.0.1:/dr nfs4.")]));
        backend.OnJson(HttpMethod.Post, "/api/v1/storage/s3", created);
        backend.OnJson(HttpMethod.Get, "/api/v1/storage/s3/5", created);
        Services.GetRequiredService<NavigationManager>().NavigateTo("/storage/new?purpose=dr");

        var cut = Render<StorageForm>();
        cut.WaitForAssertion(() => Assert.Contains("Where should extra copies live?", cut.Markup, StringComparison.Ordinal));
        cut.FindAll(".ui-tile").First(t => t.TextContent.Contains("NFS / folder", StringComparison.Ordinal)).Click();
        Assert.Empty(cut.FindAll("#bucket")); Assert.Empty(cut.FindAll("#ak"));   // no bucket or keys for a folder
        cut.Find("#name").Change("Branch DR"); cut.Find("#localpath").Change("/mnt/dr-site");
        cut.FindAll("button").First(b => b.TextContent.Contains("Test access", StringComparison.Ordinal)).Click();
        cut.WaitForAssertion(() => Assert.Contains("Access confirmed", cut.Markup, StringComparison.Ordinal));
        cut.FindAll(".create-card__foot button").First(b => b.TextContent.Contains("Add DR site", StringComparison.Ordinal)).Click();

        cut.WaitForAssertion(() => Assert.Contains("Send copies to Branch DR", cut.Markup, StringComparison.Ordinal));
        using var body = JsonDocument.Parse(backend.Requests.Single(r => r.Request.Method == HttpMethod.Post && r.Request.RequestUri!.AbsolutePath == "/api/v1/storage/s3").Body!);
        Assert.Equal("Filesystem", body.RootElement.GetProperty("kind").GetString());
        Assert.Equal("/mnt/dr-site", body.RootElement.GetProperty("localPath").GetString());
        using var test = JsonDocument.Parse(backend.Requests.Single(r => r.Request.RequestUri!.AbsolutePath == "/api/v1/storage/s3/test").Body!);
        Assert.Equal("Filesystem", test.RootElement.GetProperty("kind").GetString());
    }

    [Fact]
    public void Invite_drawer_creates_a_user_with_the_chosen_roles()
    {
        var backend = this.AddBackora(Permissions.UsersRead, Permissions.UsersManage);
        backend.OnJson(HttpMethod.Get, "/api/v1/roles", new List<RoleDto> { new(Roles.Viewer, "Read-only Viewer", "Read-only access", [], 0), new(Roles.RestoreOperator, "Restore Operator", "Runs restores", [], 0) });
        backend.OnJson(HttpMethod.Get, "/api/v1/users", TestData.Page<UserDto>());
        backend.OnJson(HttpMethod.Post, "/api/v1/users", new UserDto(12, "sam@example.com", "Sam Lee", [Roles.RestoreOperator], true, false, null, null, Now, null));

        var cut = Render<Users>();
        cut.WaitForAssertion(() => Assert.Contains("Invite user", cut.Markup, StringComparison.Ordinal));
        cut.FindAll("button").First(b => b.TextContent.Contains("Invite user", StringComparison.Ordinal)).Click();
        cut.Find("#iv-name").Change("Sam Lee"); cut.Find("#iv-email").Change("sam@example.com"); cut.Find("#iv-pw").Change("Temp-Pass-12345");
        cut.FindAll(".ui-choice").First(c => c.TextContent.Contains("Read-only Viewer", StringComparison.Ordinal)).Click(); // default off
        cut.FindAll(".ui-choice").First(c => c.TextContent.Contains("Restore Operator", StringComparison.Ordinal)).Click();
        cut.FindAll(".ui-drawer__foot button").First(b => b.TextContent.Contains("Create user", StringComparison.Ordinal)).Click();

        cut.WaitForAssertion(() => Assert.Single(backend.Requests, r => r.Request.Method == HttpMethod.Post));
        using var json = JsonDocument.Parse(backend.Requests.Single(r => r.Request.Method == HttpMethod.Post).Body!);
        Assert.Equal([Roles.RestoreOperator], json.RootElement.GetProperty("roles").EnumerateArray().Select(e => e.GetString()));
    }
}
