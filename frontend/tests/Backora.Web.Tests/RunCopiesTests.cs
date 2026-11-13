using Backora.Contracts.Security;
using Backora.Contracts.V1;
using Backora.Web.Components.Pages.Runs;
using Backora.Web.Components.Shared;
using Bunit;

namespace Backora.Web.Tests;

/// <summary>A successful retry replaces the failed copy it retried: one row per site, no stale "Copy failed" or Retry.</summary>
public class RunCopiesTests : BunitContext
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    private static OffloadSummaryDto Copy(long id, long destination, string site, OffloadStatusDto status, long run = 49711) =>
        TestData.RecoveryPoint(id) with { BackupRunId = run, DestinationId = destination, DestinationName = site, Status = status };

    [Fact]
    public void Only_the_latest_attempt_per_site_is_kept()
    {
        var primary = Copy(51443, 1, "ECS", OffloadStatusDto.Completed);
        var failed = Copy(51444, 2, "Amazon DR", OffloadStatusDto.Failed);
        var retried = Copy(51446, 2, "Amazon DR", OffloadStatusDto.Completed);
        var otherRun = Copy(60000, 2, "Amazon DR", OffloadStatusDto.Completed, run: 1);

        Assert.True(Ui.IsSuperseded(failed, [primary, failed, retried]));
        Assert.False(Ui.IsSuperseded(retried, [primary, failed, retried]));
        Assert.False(Ui.IsSuperseded(failed, [primary, failed, otherRun]));   // a different run never replaces it
        Assert.Equal([51443, 51446], Ui.LatestPerSite([primary, failed, retried]).Select(o => o.Id));
    }

    [Fact]
    public void The_api_mark_wins_even_when_the_newer_attempt_is_not_in_the_list()
    {
        // e.g. the Transfers "Failed" filter or another page: the retry itself isn't loaded
        var failed = Copy(51444, 2, "Amazon DR", OffloadStatusDto.Failed) with { SupersededById = 51446 };
        Assert.True(Ui.IsSuperseded(failed, [failed]));
        Assert.Empty(Ui.LatestPerSite([failed]));
    }

    [Fact]
    public void Run_page_hides_a_failed_copy_once_its_retry_is_stored()
    {
        var backend = this.AddBackora(Permissions.RunsRead, Permissions.JobsRun);
        var summary = new RunSummaryDto(49711, 1, "billing-db", "MariaDB", 5, "billing-db", RunStatusDto.Completed, RunTriggerDto.Manual, FailureClassDto.None,
            null, Now.AddHours(-2), Now.AddHours(-2), Now.AddHours(-2), 592, 1, 19_777_331_200, VerificationStatusDto.Verified, 1, 3, OffloadStatusDto.Completed, "admin");
        var detail = new RunDetailDto(summary, "mariabackup", 4, null, 0, null, true, null, null, null, null, null, null, null, null, null, [],
            [Copy(51443, 1, "ECS", OffloadStatusDto.Completed), Copy(51444, 2, "Amazon DR", OffloadStatusDto.Failed), Copy(51446, 2, "Amazon DR", OffloadStatusDto.Completed)], []);
        backend.OnJson(HttpMethod.Get, "/api/v1/runs/49711", detail);

        var cut = Render<RunDetail>(p => p.Add(x => x.Id, 49711));
        cut.WaitForAssertion(() => Assert.Contains("Amazon DR", cut.Markup, StringComparison.Ordinal));

        Assert.DoesNotContain("Retry this site", cut.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain("Copy failed", cut.Markup, StringComparison.Ordinal);
        Assert.Single(cut.FindAll(".ui-list__title"), t => t.TextContent == "Amazon DR");
    }

    [Fact]
    public void Each_copy_shows_its_current_step_with_amount_speed_and_time_left()
    {
        var backend = this.AddBackora(Permissions.RunsRead);
        var summary = new RunSummaryDto(49711, 1, "BillingDB01", "MariaDB", 5, "BillingDB01", RunStatusDto.Completed, RunTriggerDto.Manual, FailureClassDto.None,
            null, Now.AddHours(-2), Now.AddHours(-2), Now.AddHours(-1), 592, 1, 80L << 30, VerificationStatusDto.Verified, 1, 3, OffloadStatusDto.Running, "admin");
        const long GB = 1L << 30;
        var uploading = Copy(51443, 1, "ECS", OffloadStatusDto.Running) with { BytesTotal = 80 * GB, BytesTransferred = 20 * GB, ProgressStage = "upload", ProgressBytes = 20 * GB, ProgressTotalBytes = 80 * GB, ProgressStageStartedAt = Now.AddMinutes(-10) };
        var checking = Copy(51446, 2, "Amazon DR", OffloadStatusDto.Verifying) with { Encrypted = true, ProgressStage = "inspect", ProgressBytes = 40 * GB, ProgressTotalBytes = 80 * GB, ProgressStageStartedAt = Now.AddMinutes(-5) };
        var encrypting = Copy(51447, 3, "Vault", OffloadStatusDto.Running) with { Encrypted = true, BytesTotal = 80 * GB, BytesTransferred = 0, ProgressStage = "encrypt", ProgressBytes = 8 * GB, ProgressTotalBytes = 80 * GB, ProgressStageStartedAt = Now.AddMinutes(-2) };
        var detail = new RunDetailDto(summary, "mariabackup", 4, null, 0, null, true, null, null, null, null, null, null, null, null, null, [], [uploading, checking, encrypting], []);
        backend.OnJson(HttpMethod.Get, "/api/v1/runs/49711", detail);

        var cut = Render<RunDetail>(p => p.Add(x => x.Id, 49711));
        cut.WaitForAssertion(() => Assert.Equal(3, cut.FindAll(".copy-line").Count));
        var lines = cut.FindAll(".copy-line").Select(l => l.TextContent).ToList();

        Assert.Contains(lines, l => l.StartsWith("Uploading · 25% · 20.0 GB of 80.0 GB", StringComparison.Ordinal) && l.Contains("/min", StringComparison.Ordinal) && l.Contains("min left", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.StartsWith("Checking the ciphertext · 50% · 40.0 GB of 80.0 GB", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.StartsWith("Encrypting · 10% · 8.00 GB of 80.0 GB", StringComparison.Ordinal));
    }

    [Fact]
    public void Verifying_a_backup_shows_checksum_progress()
    {
        var backend = this.AddBackora(Permissions.RunsRead);
        var summary = new RunSummaryDto(49711, 1, "BillingDB01", "MariaDB", 5, "BillingDB01", RunStatusDto.Verifying, RunTriggerDto.Manual, FailureClassDto.None,
            null, Now.AddHours(-2), Now.AddHours(-2), null, null, 0, null, VerificationStatusDto.NotVerified, 1, 3, null, "admin");
        var progress = new RunProgressDto(30L << 30, 80L << 30, 37.5, 400_000_000, 140, "verify", true, Now);
        var detail = new RunDetailDto(summary, "mariabackup", 4, null, 0, null, true, null, null, null, null, null, null, null, null, null, [], [], [], progress);
        backend.OnJson(HttpMethod.Get, "/api/v1/runs/49711", detail);

        var cut = Render<RunDetail>(p => p.Add(x => x.Id, 49711));
        cut.WaitForAssertion(() => Assert.Contains("Verifying checksums", cut.Markup, StringComparison.Ordinal));
        Assert.Contains("37.5", cut.Find(".rp__figure").TextContent);
        Assert.Contains("Checked", cut.Markup);
        Assert.Contains("37% · checksums", cut.Markup);
    }

    [Fact]
    public void Compressing_on_the_worker_shows_its_progress_as_the_checksum_step()
    {
        var backend = this.AddBackora(Permissions.RunsRead);
        var summary = new RunSummaryDto(49809, 1, "thumbnail", "PostgreSQL", 5, "thumbnail", RunStatusDto.Verifying, RunTriggerDto.Scheduled, FailureClassDto.None,
            null, Now.AddHours(-1), Now.AddHours(-1), null, null, 0, null, VerificationStatusDto.NotVerified, 1, 3, null, "scheduler");
        var progress = new RunProgressDto(40L << 30, 100L << 30, 40, 800_000_000, 75, "compress", true, Now);
        var detail = new RunDetailDto(summary, "pg_basebackup", 4, null, 0, null, true, null, null, null, null, null, null, null, null, null, [], [], [], progress);
        backend.OnJson(HttpMethod.Get, "/api/v1/runs/49809", detail);

        var cut = Render<RunDetail>(p => p.Add(x => x.Id, 49809));
        cut.WaitForAssertion(() => Assert.Contains("Compressing into one file and computing its checksums", cut.Markup, StringComparison.Ordinal));
        Assert.Contains("40% · compress + checksums", cut.Markup);
        Assert.Contains("Script finished", cut.Markup);
    }

    [Fact]
    public void Failed_transfers_can_be_cleared_so_only_new_failures_show()
    {
        var backend = this.AddBackora(Permissions.RunsRead, Permissions.JobsRun);
        var failed = Copy(51500, 1, "ECS", OffloadStatusDto.Failed);
        backend.OnJson(HttpMethod.Get, "/api/v1/offloads", TestData.Page(failed));
        backend.OnJson(HttpMethod.Post, "/api/v1/offloads/dismiss-failed", new DismissedResponse(1));

        var cut = Render<Backora.Web.Components.Pages.Storage.Transfers>();
        cut.WaitForAssertion(() => Assert.Contains("ECS", cut.Markup, StringComparison.Ordinal));
        cut.FindAll("nav.ui-tabs button").Single(b => b.TextContent == "Failed").Click();
        cut.WaitForAssertion(() => Assert.Contains("Clear all", cut.Markup, StringComparison.Ordinal));
        Assert.Contains(cut.FindAll("button"), b => b.TextContent.Trim() == "Clear");

        cut.FindAll("button").First(b => b.TextContent.Trim() == "Clear all").Click();
        cut.WaitForAssertion(() => Assert.Contains("Nothing is deleted", cut.Markup, StringComparison.Ordinal));
        cut.FindAll("button").Last(b => b.TextContent.Trim() == "Clear all").Click();
        cut.WaitForAssertion(() => Assert.Single(backend.Calls(HttpMethod.Post, "/api/v1/offloads/dismiss-failed")));
    }

    [Fact]
    public void A_cleared_failure_is_labelled_and_can_still_be_retried()
    {
        var backend = this.AddBackora(Permissions.RunsRead, Permissions.JobsRun);
        var cleared = Copy(51501, 1, "ECS", OffloadStatusDto.Failed) with { DismissedAt = Now };
        backend.OnJson(HttpMethod.Get, "/api/v1/offloads", TestData.Page(cleared));

        var cut = Render<Backora.Web.Components.Pages.Storage.Transfers>();
        cut.WaitForAssertion(() => Assert.Contains("Cleared", cut.Markup, StringComparison.Ordinal));
        Assert.Contains(cut.FindAll("button"), b => b.TextContent.Trim() == "Retry");
        Assert.DoesNotContain(cut.FindAll("button"), b => b.TextContent.Trim() == "Clear");
    }

    [Fact]
    public void Failed_runs_can_be_cleared_from_run_history()
    {
        var backend = this.AddBackora(Permissions.RunsRead, Permissions.JobsRun);
        var failed = new RunSummaryDto(49800, 1, "orders-db", "PostgreSQL", 5, "orders-db", RunStatusDto.Failed, RunTriggerDto.Scheduled, FailureClassDto.ScriptError,
            "boom", Now.AddHours(-1), Now.AddHours(-1), Now.AddHours(-1), 10, 0, null, VerificationStatusDto.NotVerified, 1, 3, null, "scheduler");
        backend.OnJson(HttpMethod.Get, "/api/v1/runs", TestData.Page(failed));
        backend.OnJson(HttpMethod.Post, "/api/v1/runs/dismiss-failed", new DismissedResponse(1));

        var cut = Render<Backora.Web.Components.Pages.Runs.RunsList>();
        cut.WaitForAssertion(() => Assert.Contains("orders-db", cut.Markup, StringComparison.Ordinal));
        cut.FindAll("nav.ui-tabs button").Single(b => b.TextContent == "Failed").Click();
        cut.WaitForAssertion(() => Assert.Contains("Clear all", cut.Markup, StringComparison.Ordinal));
        cut.FindAll("button").First(b => b.TextContent.Trim() == "Clear all").Click();
        cut.WaitForAssertion(() => Assert.Contains("Nothing is deleted", cut.Markup, StringComparison.Ordinal));
        cut.FindAll("button").Last(b => b.TextContent.Trim() == "Clear all").Click();
        cut.WaitForAssertion(() => Assert.Single(backend.Calls(HttpMethod.Post, "/api/v1/runs/dismiss-failed")));
    }

    [Fact]
    public void A_dr_site_without_the_newest_recovery_point_offers_to_copy_it_now()
    {
        this.AddBackora(Permissions.RunsRead, Permissions.JobsRun);
        var newest = Copy(51600, 1, "ECS", OffloadStatusDto.Completed);
        OffloadSummaryDto? requested = null;

        var empty = Render<Backora.Web.Components.Shared.CopyNode>(p => p.Add(x => x.SiteId, 8).Add(x => x.Name, "Branch DR").Add(x => x.Role, "DR site")
            .Add(x => x.CanRetry, true).Add(x => x.CopyFrom, newest).Add(x => x.OnCopyNow, (OffloadSummaryDto o) => requested = o));
        empty.FindAll("button").Single(b => b.TextContent.Contains("Copy now", StringComparison.Ordinal)).Click();
        Assert.Equal(51600, requested?.Id);

        var upToDate = Render<Backora.Web.Components.Shared.CopyNode>(p => p.Add(x => x.SiteId, 8).Add(x => x.Name, "Branch DR").Add(x => x.Role, "DR site")
            .Add(x => x.CanRetry, true).Add(x => x.CopyFrom, newest).Add(x => x.OnCopyNow, (OffloadSummaryDto _) => { })
            .Add(x => x.Latest, Copy(51601, 8, "Branch DR", OffloadStatusDto.Completed) with { SourceOffloadId = 51600 }));
        Assert.DoesNotContain(upToDate.FindAll("button"), b => b.TextContent.Contains("Copy now", StringComparison.Ordinal));

        var copying = Render<Backora.Web.Components.Shared.CopyNode>(p => p.Add(x => x.SiteId, 8).Add(x => x.Name, "Branch DR").Add(x => x.Role, "DR site")
            .Add(x => x.CanRetry, true).Add(x => x.CopyFrom, newest).Add(x => x.OnCopyNow, (OffloadSummaryDto _) => { })
            .Add(x => x.Latest, Copy(51602, 8, "Branch DR", OffloadStatusDto.Running) with { SourceOffloadId = 51500 }));
        Assert.DoesNotContain(copying.FindAll("button"), b => b.TextContent.Contains("Copy now", StringComparison.Ordinal));
    }
}
