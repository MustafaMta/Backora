using System.Diagnostics;
using Backora.Application.Abstractions;
using Backora.Application.Execution;
using Backora.Domain.Backups;
using FluentAssertions;
using Xunit;

namespace Backora.UnitTests;

/// <summary>The live progress shown on a running backup: parsing the template's report, rate smoothing, percent and ETA.</summary>
public class RunProgressTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 12, 20, 0, 0, TimeSpan.Zero);
    private const long GB = 1L << 30;

    [Theory]
    [InlineData("1024 4096 copy", 1024L, 4096L, "copy")]
    [InlineData("1024 0 dump", 1024L, null, "dump")]
    [InlineData("0 0 checkpoint", 0L, null, "checkpoint")]
    [InlineData("77", 77L, null, null)]
    public void Parses_the_template_report(string line, long bytes, long? total, string? stage)
    {
        var p = DetachedProcess.ParseProgress(line);
        p.Should().Be(new ProgressSample(bytes, total, stage, FromTemplate: true));
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc 12 copy")]
    [InlineData("-5 10 copy")]
    public void Ignores_a_malformed_report(string line) => DetachedProcess.ParseProgress(line).Should().BeNull();

    [Fact]
    public void Stage_text_is_bounded_and_has_no_control_characters()
    {
        var p = DetachedProcess.ParseProgress("1 2 " + new string('x', 100) + "\u001b[31m");
        p!.Stage!.Length.Should().BeLessThanOrEqualTo(40);
        p.Stage.Should().NotContain("\u001b");
    }

    [Fact]
    public void Rate_is_measured_between_samples_and_smoothed()
    {
        var run = new BackupRun();
        RunProgress.Apply(run, new ProgressSample(10 * GB, 100 * GB, "copy", true), T0);
        run.ProgressBytesPerSecond.Should().BeNull("one sample is not a rate");

        RunProgress.Apply(run, new ProgressSample(10 * GB + 500_000_000, 100 * GB, "copy", true), T0.AddSeconds(10));
        run.ProgressBytesPerSecond.Should().Be(50_000_000);

        RunProgress.Apply(run, new ProgressSample(10 * GB + 500_000_000 + 1_000_000_000, 100 * GB, "copy", true), T0.AddSeconds(20));
        run.ProgressBytesPerSecond.Should().Be((long)(0.6 * 100_000_000 + 0.4 * 50_000_000));
        run.ProgressAt.Should().Be(T0.AddSeconds(20));
    }

    [Fact]
    public void Polls_between_template_updates_do_not_read_as_a_stall()
    {
        var run = new BackupRun();
        RunProgress.Apply(run, new ProgressSample(1 * GB, null, "copy", true), T0);
        RunProgress.Apply(run, new ProgressSample(2 * GB, null, "copy", true), T0.AddSeconds(10));
        var rate = run.ProgressBytesPerSecond;
        RunProgress.Apply(run, new ProgressSample(2 * GB, null, "copy", true), T0.AddSeconds(15));   // the file was not refreshed yet
        run.ProgressBytesPerSecond.Should().Be(rate);
        run.ProgressAt.Should().Be(T0.AddSeconds(10));
    }

    [Fact]
    public void A_minute_without_new_bytes_shows_a_rate_of_zero()
    {
        var run = new BackupRun();
        RunProgress.Apply(run, new ProgressSample(1 * GB, null, "copy", true), T0);
        RunProgress.Apply(run, new ProgressSample(2 * GB, null, "copy", true), T0.AddSeconds(10));
        RunProgress.Apply(run, new ProgressSample(2 * GB, null, "copy", true), T0.AddSeconds(75));
        run.ProgressBytesPerSecond.Should().Be(0, "a stall must read as idle at once, not fade out over many minutes");
        RunProgress.Apply(run, new ProgressSample(3 * GB, null, "copy", true), T0.AddSeconds(85));
        run.ProgressBytesPerSecond.Should().Be(GB / 10, "after a stall the speed is measured afresh");
    }

    [Fact]
    public void A_new_stage_starts_measuring_again()
    {
        var run = new BackupRun();
        RunProgress.Apply(run, new ProgressSample(1 * GB, null, "copy", true), T0);
        RunProgress.Apply(run, new ProgressSample(2 * GB, null, "copy", true), T0.AddSeconds(10));
        RunProgress.Apply(run, new ProgressSample(5_000, 2 * GB, "archive", true), T0.AddSeconds(20));
        run.ProgressStage.Should().Be("archive");
        run.ProgressBytesPerSecond.Should().BeNull();
        run.ProgressTotalBytes.Should().Be(2 * GB);
    }

    [Fact]
    public void Process_counters_are_labelled_as_written_bytes()
    {
        var run = new BackupRun();
        RunProgress.Apply(run, new ProgressSample(42, null, null, FromTemplate: false), T0);
        run.ProgressStage.Should().Be("written");
    }

    [Theory]
    [InlineData(50L, 100L, 50.0)]
    [InlineData(120L, 100L, 99.9)]   // estimates can be exceeded: never show 100% before the run has finished
    [InlineData(5L, null, null)]
    public void Percent_is_capped_below_complete(long bytes, long? total, double? expected) =>
        RunProgress.Percent(bytes, total).Should().Be(expected);

    [Fact]
    public void Remaining_time_comes_from_the_rate()
    {
        RunProgress.Remaining(767 * GB, 937 * GB, 50 * 1024 * 1024).Should().BeCloseTo(TimeSpan.FromSeconds(170.0 * 1024 / 50), TimeSpan.FromSeconds(1));
        RunProgress.Remaining(767 * GB, 937 * GB, 0).Should().BeNull();
        RunProgress.Remaining(767 * GB, null, 100).Should().BeNull();
    }

    private sealed class LocalBash : IRemoteSession
    {
        public async Task<CommandResult> RunAsync(string command, TimeSpan timeout, CancellationToken ct)
        {
            using var p = Process.Start(new ProcessStartInfo("/bin/bash", ["-c", command]) { RedirectStandardOutput = true, RedirectStandardError = true })!;
            var stdout = await p.StandardOutput.ReadToEndAsync(ct);
            var stderr = await p.StandardError.ReadToEndAsync(ct);
            await p.WaitForExitAsync(ct);
            return new CommandResult(p.ExitCode, stdout, stderr);
        }
        public Task WriteFileAsync(string path, string content, short mode, CancellationToken ct) => throw new NotSupportedException();
        public Task<CommandResult> RunWithInputAsync(string command, ReadOnlyMemory<byte> stdin, TimeSpan timeout, CancellationToken ct) => throw new NotSupportedException();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    [Fact]
    public async Task Status_reports_the_template_progress_or_else_the_bytes_written()
    {
        if (!OperatingSystem.IsLinux() || !File.Exists("/usr/bin/setsid") && !File.Exists("/bin/setsid")) return;
        var dir = Directory.CreateTempSubdirectory("backora-progress-").FullName;
        var session = new LocalBash();
        try
        {
            await DetachedProcess.StartAsync(session, dir, "for i in $(seq 1 40); do head -c 3000000 /dev/zero >> ./data; sleep 0.25; done", CancellationToken.None);
            await Task.Delay(1500);
            var measured = await DetachedProcess.GetStatusAsync(session, dir, CancellationToken.None);
            measured.State.Should().Be(RemoteProcessState.Running);
            measured.Progress.Should().NotBeNull();
            measured.Progress!.FromTemplate.Should().BeFalse();
            measured.Progress.Bytes.Should().BeGreaterThan(3_000_000);

            await File.WriteAllTextAsync(Path.Combine(dir, "progress"), "5000 20000 copy\n");
            var reported = await DetachedProcess.GetStatusAsync(session, dir, CancellationToken.None);
            reported.Progress.Should().Be(new ProgressSample(5000, 20000, "copy", FromTemplate: true));
            await DetachedProcess.TerminateAsync(session, dir, CancellationToken.None);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
