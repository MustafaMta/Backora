using Backora.Domain;
using Backora.Domain.Backups;
using Backora.Domain.Common;
using Backora.Domain.Execution;
using Backora.Domain.Retention;
using Backora.Domain.Scheduling;
using FluentAssertions;

namespace Backora.UnitTests;

public class SchedulePlannerTests
{
    [Theory]
    [InlineData("0 2 * * *", true)]
    [InlineData("*/15 * * * *", true)]
    [InlineData("0 2 * *", false)]
    [InlineData("0 25 * * *", false)]
    [InlineData("0 2 * * * *", false)]
    [InlineData("", false)]
    public void Parses_standard_five_field_cron(string expr, bool valid) =>
        SchedulePlanner.TryParse(expr, out _, out _).Should().Be(valid);

    [Fact]
    public void Next_occurrence_respects_timezone()
    {
        // 02:00 Europe/Berlin in winter (CET, UTC+1) is 01:00 UTC.
        var next = SchedulePlanner.NextOccurrence("0 2 * * *", "Europe/Berlin", new DateTimeOffset(2026, 1, 10, 12, 0, 0, TimeSpan.Zero));
        next.Should().Be(new DateTimeOffset(2026, 1, 11, 1, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public void Spring_forward_skipped_hour_runs_once_at_transition()
    {
        // 2026-03-29: Europe/Berlin jumps 02:00 → 03:00. A 02:30 job must still run exactly once that day.
        var after = new DateTimeOffset(2026, 3, 28, 23, 0, 0, TimeSpan.Zero);
        var occurrences = SchedulePlanner.NextOccurrences("30 2 * * *", "Europe/Berlin", after, 2);
        occurrences[0].UtcDateTime.Date.Should().Be(new DateTime(2026, 3, 29));
        occurrences[1].UtcDateTime.Date.Should().Be(new DateTime(2026, 3, 30));
        (occurrences[1] - occurrences[0]).Should().BeGreaterThan(TimeSpan.FromHours(23));
    }

    [Fact]
    public void Fall_back_ambiguous_hour_runs_once()
    {
        // 2026-10-25: Europe/Berlin repeats 02:00–03:00. A 02:30 job runs once.
        var after = new DateTimeOffset(2026, 10, 24, 23, 0, 0, TimeSpan.Zero);
        var occurrences = SchedulePlanner.NextOccurrences("30 2 * * *", "Europe/Berlin", after, 2);
        occurrences.Select(o => o.UtcDateTime.Date).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void Due_within_grace_runs_and_advances()
    {
        var now = new DateTimeOffset(2026, 5, 1, 2, 10, 0, TimeSpan.Zero);
        var d = SchedulePlanner.Evaluate("0 2 * * *", "UTC", now.AddMinutes(-10), now, MissedRunPolicy.RunOnceWithinGrace, 60);
        d.ShouldRun.Should().BeTrue();
        d.ScheduledFor.Should().Be(now.AddMinutes(-10));
        d.NextRunAt.Should().Be(new DateTimeOffset(2026, 5, 2, 2, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public void Missed_beyond_grace_is_skipped_not_run()
    {
        var now = new DateTimeOffset(2026, 5, 1, 9, 0, 0, TimeSpan.Zero);
        var d = SchedulePlanner.Evaluate("0 2 * * *", "UTC", new DateTimeOffset(2026, 5, 1, 2, 0, 0, TimeSpan.Zero), now, MissedRunPolicy.RunOnceWithinGrace, 60);
        d.ShouldRun.Should().BeFalse();
        d.ScheduledFor.Should().NotBeNull("the missed occurrence is reported");
        d.NextRunAt.Should().Be(new DateTimeOffset(2026, 5, 2, 2, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public void Skip_policy_tolerates_only_scheduler_jitter()
    {
        var now = new DateTimeOffset(2026, 5, 1, 2, 5, 0, TimeSpan.Zero);
        SchedulePlanner.Evaluate("0 2 * * *", "UTC", now.AddMinutes(-5), now, MissedRunPolicy.Skip, 60).ShouldRun.Should().BeFalse();
        SchedulePlanner.Evaluate("0 2 * * *", "UTC", now.AddMinutes(-1), now, MissedRunPolicy.Skip, 60).ShouldRun.Should().BeTrue();
    }

    [Fact]
    public void Uninitialised_job_gets_next_occurrence_without_running()
    {
        var now = DateTimeOffset.UtcNow;
        var d = SchedulePlanner.Evaluate("*/5 * * * *", "UTC", null, now, MissedRunPolicy.RunOnceWithinGrace, 60);
        d.ShouldRun.Should().BeFalse();
        d.NextRunAt.Should().BeAfter(now);
    }

    [Theory]
    [InlineData("0 2 * * *", "UTC", "Daily at 02:00 (UTC)")]
    [InlineData("30 4 * * 1", "UTC", "Every Monday at 04:30 (UTC)")]
    [InlineData("*/15 * * * *", "UTC", "Every 15 minutes")]
    [InlineData(null, "UTC", "Manual only")]
    public void Describes_common_schedules(string? expr, string tz, string expected) =>
        SchedulePlanner.Describe(expr, tz).Should().Be(expected);
}

public class RetentionPlannerTests
{
    private static readonly DateTimeOffset Now = new(2026, 6, 1, 0, 0, 0, TimeSpan.Zero);

    private static RecoveryPointInfo P(long id, double ageDays, bool verified = true, bool prot = false, bool restore = false) =>
        new(id, 1, Now.AddDays(-ageDays), verified, prot, restore, RetentionState.Active);

    [Fact]
    public void Deletes_only_points_older_than_retention()
    {
        var d = RetentionPlanner.Plan([P(1, 1), P(2, 5), P(3, 10)], 7m, 1, Now);
        d.Single(x => x.RecoveryPointId == 3).Action.Should().Be(RetentionAction.Delete);
        d.Where(x => x.RecoveryPointId != 3).Should().OnlyContain(x => x.Action == RetentionAction.Keep);
    }

    [Fact]
    public void Always_keeps_newest_verified_point_even_if_expired()
    {
        var d = RetentionPlanner.Plan([P(1, 30), P(2, 40)], 7m, 1, Now);
        d.Single(x => x.RecoveryPointId == 1).Action.Should().Be(RetentionAction.Keep);
        d.Single(x => x.RecoveryPointId == 2).Action.Should().Be(RetentionAction.Delete);
    }

    [Fact]
    public void Unverified_points_do_not_count_towards_minimum()
    {
        var d = RetentionPlanner.Plan([P(1, 30, verified: false), P(2, 40)], 7m, 1, Now);
        d.Single(x => x.RecoveryPointId == 2).Action.Should().Be(RetentionAction.Keep, "it is the only verified point");
        d.Single(x => x.RecoveryPointId == 1).Action.Should().Be(RetentionAction.Delete);
    }

    [Fact]
    public void Protected_and_in_restore_points_are_never_deleted()
    {
        var d = RetentionPlanner.Plan([P(1, 1), P(2, 50, prot: true), P(3, 60, restore: true)], 7m, 1, Now);
        d.Should().OnlyContain(x => x.Action == RetentionAction.Keep);
    }

    [Fact]
    public void Fractional_retention_days_are_exact()
    {
        // 0.0417 days ≈ 1 hour (legacy supported decimal retention).
        var d = RetentionPlanner.Plan([P(1, 0), new RecoveryPointInfo(2, 1, Now.AddHours(-2), true, false, false, RetentionState.Active)], 0.0417m, 1, Now);
        d.Single(x => x.RecoveryPointId == 2).Action.Should().Be(RetentionAction.Delete);
    }

    [Fact]
    public void Zero_retention_disables_deletion()
    {
        RetentionPlanner.Plan([P(1, 400)], 0m, 1, Now).Should().OnlyContain(x => x.Action == RetentionAction.Keep);
    }

    [Fact]
    public void Dependents_block_deletion()
    {
        var d = RetentionPlanner.Plan([P(1, 1), P(2, 50)], 7m, 1, Now, new Dictionary<long, int> { [2] = 1 });
        d.Single(x => x.RecoveryPointId == 2).Action.Should().Be(RetentionAction.Keep);
    }
}

public class BackupRunStateMachineTests
{
    private static readonly DateTimeOffset T = DateTimeOffset.UnixEpoch;

    [Fact]
    public void Happy_path_transitions_are_allowed()
    {
        var r = new BackupRun();
        r.TransitionTo(RunStatus.Starting, T);
        r.Attempt.Should().Be(1);
        r.TransitionTo(RunStatus.Running, T);
        r.TransitionTo(RunStatus.Verifying, T);
        r.TransitionTo(RunStatus.Completed, T);
        r.IsTerminal.Should().BeTrue();
        r.CompletedAt.Should().Be(T);
    }

    [Theory]
    [InlineData(RunStatus.Pending, RunStatus.Completed)]
    [InlineData(RunStatus.Pending, RunStatus.Running)]
    [InlineData(RunStatus.Completed, RunStatus.Running)]
    [InlineData(RunStatus.Failed, RunStatus.Pending)]
    [InlineData(RunStatus.Cancelled, RunStatus.Starting)]
    [InlineData(RunStatus.Running, RunStatus.Completed)]
    public void Invalid_transitions_throw(RunStatus from, RunStatus to)
    {
        var r = new BackupRun { Status = from };
        var act = () => r.TransitionTo(to, T);
        act.Should().Throw<InvalidStateTransitionException>();
    }

    [Fact]
    public void Cancel_pending_run_is_immediate()
    {
        var r = new BackupRun();
        r.RequestCancel(7, T).Should().BeTrue();
        r.Status.Should().Be(RunStatus.Cancelled);
        r.CancelRequestedByUserId.Should().Be(7);
    }

    [Fact]
    public void Cancel_running_run_requests_termination()
    {
        var r = new BackupRun { Status = RunStatus.Running, RemotePid = 42 };
        r.RequestCancel(1, T).Should().BeTrue();
        r.Status.Should().Be(RunStatus.Cancelling);
    }

    [Fact]
    public void Cancel_terminal_run_is_rejected()
    {
        new BackupRun { Status = RunStatus.Completed }.RequestCancel(1, T).Should().BeFalse();
        new BackupRun { Status = RunStatus.Cancelling }.RequestCancel(1, T).Should().BeFalse();
    }

    [Fact]
    public void Retry_backoff_is_exponential_and_bounded()
    {
        var r = new BackupRun { MaxAttempts = 5 };
        r.Attempt = 1;
        r.TryScheduleRetry(T).Should().BeTrue();
        r.NextAttemptAt.Should().Be(T.AddSeconds(30));
        r.Attempt = 3;
        r.TryScheduleRetry(T).Should().BeTrue();
        r.NextAttemptAt.Should().Be(T.AddSeconds(120));
        r.Attempt = 5;
        r.TryScheduleRetry(T).Should().BeFalse("max attempts reached");
    }

    [Fact]
    public void Log_is_bounded_and_keeps_the_tail()
    {
        var r = new BackupRun();
        r.AppendLog(new string('a', LeasedOperationMax()));
        r.AppendLog("TAIL-MARKER");
        r.Log!.Length.Should().BeLessThanOrEqualTo(LeasedOperationMax());
        r.Log.Should().EndWith("TAIL-MARKER").And.Contain("truncated");
    }

    private static int LeasedOperationMax() => Domain.Operations.LeasedOperation.MaxLogChars;

    [Fact]
    public void Fail_records_class_and_message()
    {
        var r = new BackupRun { Status = RunStatus.Running };
        r.Fail(FailureClass.ScriptError, "exit 3", T);
        r.Status.Should().Be(RunStatus.Failed);
        r.FailureClass.Should().Be(FailureClass.ScriptError);
        r.Log.Should().Contain("exit 3");
    }
}

public class OffloadAndRestoreTests
{
    [Fact]
    public void Offload_prefix_is_unique_per_run_and_keeps_legacy_layout()
    {
        var t = new DateTimeOffset(2026, 4, 2, 13, 45, 0, TimeSpan.Zero);
        OffloadRun.BuildPrefix(null, "orders-db", t, 17).Should().Be("orders-db/2026-04-02-13-r17/");
        OffloadRun.BuildPrefix("/prod/", "orders-db", t, 18).Should().Be("prod/orders-db/2026-04-02-13-r18/");
    }

    [Theory]
    [InlineData(null, null, "orders-db/2026-04-02-13-r17/", "orders-db/2026-04-02-13-r17/")]
    [InlineData("dr", null, "orders-db/2026-04-02-13-r17/", "dr/orders-db/2026-04-02-13-r17/")]
    [InlineData("/dr-site/", "prod", "prod/orders-db/2026-04-02-13-r17/", "dr-site/orders-db/2026-04-02-13-r17/")]
    [InlineData(null, "prod", "prod/orders-db/2026-04-02-13-r17/", "orders-db/2026-04-02-13-r17/")]
    [InlineData(null, "other", "legacy/2026-04-02-13", "legacy/2026-04-02-13/")]
    public void Replica_prefix_mirrors_source_layout_under_target_base(string? target, string? sourceBase, string source, string expected) =>
        OffloadRun.BuildReplicaPrefix(target, sourceBase, source).Should().Be(expected);

    [Fact]
    public void Replica_is_identified_by_its_source()
    {
        new OffloadRun().IsReplica.Should().BeFalse();
        new OffloadRun { SourceOffloadRunId = 5 }.IsReplica.Should().BeTrue();
    }

    [Fact]
    public void Restore_cannot_complete_without_verification()
    {
        var r = new RestoreRun { Status = RestoreStatus.Running };
        var act = () => r.TransitionTo(RestoreStatus.Completed, DateTimeOffset.UtcNow);
        act.Should().Throw<InvalidStateTransitionException>();
    }

    [Theory]
    [InlineData("orders-db", true)]
    [InlineData("db_01.prod", true)]
    [InlineData("../etc", false)]
    [InlineData("a b", false)]
    [InlineData("x;rm -rf /", false)]
    [InlineData("-leading-dash", false)]
    [InlineData("..", false)]
    [InlineData("", false)]
    public void Job_names_must_be_safe_path_segments(string name, bool valid) => BackupJob.IsValidName(name).Should().Be(valid);

    [Fact]
    public void Assigning_destination_records_timestamp_only_on_change()
    {
        var job = new BackupJob();
        var t1 = DateTimeOffset.UnixEpoch;
        job.AssignDestination(5, t1);
        job.S3DestinationAssignedAt.Should().Be(t1);
        job.AssignDestination(5, t1.AddDays(1));
        job.S3DestinationAssignedAt.Should().Be(t1, "re-saving the same destination keeps the original timestamp");
        job.AssignDestination(null, t1.AddDays(2));
        job.S3DestinationAssignedAt.Should().BeNull();
    }
}

public class ScriptRendererTests
{
    private static ScriptRenderContext Ctx(Dictionary<string, string>? vars = null) =>
        new("orders-db", "PostgreSQL", "pg_dumpall", 7m, "nfs01:/backora", "/BackoraDB", vars ?? new Dictionary<string, string>());

    [Fact]
    public void Replaces_builtins_and_variables()
    {
        var r = ScriptRenderer.Render("echo {JOB_NAME} {CATEGORY} {NFS_HOST}/{JOB_NAME} {NFS_PATH} {RETENTION_DAYS} {mysql_user}", Ctx(new() { ["mysql_user"] = "backup" }));
        r.Content.Should().Be("echo orders-db PostgreSQL nfs01:/backora/orders-db /BackoraDB 7 backup");
        r.UnresolvedPlaceholders.Should().BeEmpty();
        r.Sha256.Should().HaveLength(64);
    }

    [Fact]
    public void Does_not_touch_bash_parameter_expansion_or_double_braces()
    {
        var r = ScriptRenderer.Render("echo ${JOB_NAME} {{JOB_NAME}} {JOB_NAME}", Ctx());
        r.Content.Should().Be("echo ${JOB_NAME} {{JOB_NAME}} orders-db");
    }

    [Fact]
    public void Reports_unresolved_placeholders()
    {
        ScriptRenderer.Render("pw={mysql_password}", Ctx()).UnresolvedPlaceholders.Should().Equal("mysql_password");
    }

    [Fact]
    public void Normalises_crlf()
    {
        ScriptRenderer.Render("#!/bin/bash\r\necho hi\r\n", Ctx()).Content.Should().NotContain("\r");
    }

    [Fact]
    public void Fractional_retention_is_rendered_invariantly()
    {
        ScriptRenderer.Render("{RETENTION_DAYS}", Ctx() with { RetentionDays = 0.0417m }).Content.Should().Be("0.0417");
    }
}

public class ShellQuoteTests
{
    [Theory]
    [InlineData("simple", "'simple'")]
    [InlineData("it's", "'it'\"'\"'s'")]
    [InlineData("$(rm -rf /); `id`", "'$(rm -rf /); `id`'")]
    public void Quotes_safely(string input, string expected) => ShellQuote.Quote(input).Should().Be(expected);

    [Fact]
    public void Rejects_nul_bytes()
    {
        var act = () => ShellQuote.Quote("a\0b");
        act.Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData("/BackoraDB", true)]
    [InlineData("/var/tmp/backora", true)]
    [InlineData("relative/path", false)]
    [InlineData("/a/../etc", false)]
    [InlineData("/a/./b", false)]
    [InlineData("/a\nb", false)]
    [InlineData("", false)]
    public void Validates_absolute_paths(string p, bool ok) => ShellQuote.IsSafeAbsolutePath(p).Should().Be(ok);

    [Fact]
    public void Join_blocks_traversal()
    {
        ShellQuote.Join("/BackoraDB", "orders", "restore-1").Should().Be("/BackoraDB/orders/restore-1");
        var act = () => ShellQuote.Join("/BackoraDB", "../etc");
        act.Should().Throw<ArgumentException>();
    }
}
