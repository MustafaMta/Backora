using System.Reflection;
using Backora.Contracts.Security;
using Backora.Contracts.V1;
using Backora.Web.Components.Layout;
using Backora.Web.Components.Pages.Backups;
using Backora.Web.Components.Pages.Servers;
using Bunit;
using Microsoft.AspNetCore.Components;

namespace Backora.Web.Tests;

/// <summary>State-changing actions need an explicit confirmation; every navigation entry is a real V2 page.</summary>
public class SafetyAndNavigationTests : BunitContext
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    [Fact]
    public void Run_backup_starts_nothing_until_confirmed()
    {
        var backend = this.AddBackora(Permissions.JobsRead, Permissions.JobsRun, Permissions.JobsWrite, Permissions.RecoveryPointsRead);
        backend.OnJson(HttpMethod.Get, "/api/v1/backup-jobs/1", TestData.Job(1));
        backend.OnJson(HttpMethod.Get, "/api/v1/runs", TestData.Page<RunSummaryDto>());
        backend.OnJson(HttpMethod.Get, "/api/v1/offloads", TestData.Page<OffloadSummaryDto>());
        backend.OnJson(HttpMethod.Post, "/api/v1/backup-jobs/1/runs", new RunCreatedResponse(99, false));

        var cut = Render<BackupDetail>(p => p.Add(x => x.Id, 1));
        cut.WaitForAssertion(() => Assert.Contains("Run backup", cut.Markup, StringComparison.Ordinal));
        cut.FindAll("button").First(b => b.TextContent.Trim() == "Run backup").Click();

        bool IsRun((HttpRequestMessage Request, string? Body) r) => r.Request.Method == HttpMethod.Post && r.Request.RequestUri!.AbsolutePath.EndsWith("/runs", StringComparison.Ordinal);
        Assert.DoesNotContain(backend.Requests, IsRun);              // the button only opens the confirmation
        cut.WaitForAssertion(() => Assert.Contains("Run this backup now?", cut.Markup, StringComparison.Ordinal));
        cut.Find(".js-confirm").Click();
        cut.WaitForAssertion(() => Assert.Single(backend.Requests, IsRun));
    }

    [Fact]
    public void Trusting_a_host_key_needs_confirmation_and_sends_the_shown_fingerprint()
    {
        var backend = this.AddBackora(Permissions.ServersRead, Permissions.ServersWrite, Permissions.ServersTest, Permissions.JobsRead);
        var server = new ServerDto(4, "db-01", "10.0.0.5", 22, "backup", false, "SHA256:abc", null, null, null, null, null, 0, null, HealthState.Unknown, 1, Now, Now);
        backend.OnJson(HttpMethod.Get, "/api/v1/servers/4", server);
        backend.OnJson(HttpMethod.Get, "/api/v1/backup-jobs", TestData.Page<BackupJobDto>());
        backend.OnJson(HttpMethod.Post, "/api/v1/servers/4/trust-host-key", new { });

        var cut = Render<ServerDetail>(p => p.Add(x => x.Id, 4));
        cut.WaitForAssertion(() => Assert.Contains("SHA256:abc", cut.Markup, StringComparison.Ordinal));
        cut.FindAll("button").First(b => b.TextContent.Contains("Trust this key", StringComparison.Ordinal)).Click();
        Assert.DoesNotContain(backend.Requests, r => r.Request.RequestUri!.AbsolutePath.EndsWith("/trust-host-key", StringComparison.Ordinal));
        cut.Find(".js-confirm").Click();
        cut.WaitForAssertion(() => Assert.Contains("\"fingerprint\":\"SHA256:abc\"", backend.Requests.Single(r => r.Request.RequestUri!.AbsolutePath.EndsWith("/trust-host-key", StringComparison.Ordinal)).Body, StringComparison.Ordinal));
    }

    private static IEnumerable<string> Routes() =>
        typeof(Nav).Assembly.GetTypes().SelectMany(t => t.GetCustomAttributes<RouteAttribute>()).Select(r => r.Template);

    [Fact]
    public void Every_sidebar_entry_has_its_own_page()
    {
        var routes = Routes().ToHashSet(StringComparer.OrdinalIgnoreCase);
        var hrefs = Nav.Items.Select(i => i.Href).Concat(Nav.Items.SelectMany(i => i.Subs ?? []).Select(s => s.Href));
        Assert.All(hrefs, h => Assert.Contains(h, routes));
    }

    [Fact]
    public void No_page_sends_people_back_to_the_old_frontend()
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../src/Backora.Web/Components"));
        var offenders = Directory.EnumerateFiles(root, "*.razor", SearchOption.AllDirectories)
            .Where(f => File.ReadAllText(f).Contains("8443", StringComparison.Ordinal)).ToList();
        Assert.Empty(offenders);
    }
}
