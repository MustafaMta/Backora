using System.Diagnostics;
using Backora.Application.Abstractions;
using Backora.Application.Execution;
using FluentAssertions;
using Xunit;

namespace Backora.UnitTests;

/// <summary>
/// A backup that reaches its timeout while still reading and writing data is not stuck: it gets more time (up to
/// 4× the timeout). One that does nothing, or whose counters cannot be read, is stopped as before.
/// </summary>
public class RunTimeoutTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 12, 13, 0, 0, TimeSpan.Zero);
    private const int Timeout = 360;
    private static readonly DateTimeOffset AtTimeout = Start.AddMinutes(Timeout + 1);

    [Fact]
    public void First_check_measures_for_a_few_minutes()
    {
        var s = new IoSample(1000, 5_000_000, null, null);
        RunTimeout.Decide(s, AtTimeout, Start, Timeout).Should().Be(RunTimeout.Verdict.Measure);
        RunTimeout.NextDeadline(RunTimeout.Verdict.Measure, AtTimeout, Start, Timeout).Should().Be(AtTimeout + RunTimeout.FirstCheck);
    }

    [Fact]
    public void A_run_still_moving_data_is_extended()
    {
        var s = new IoSample(1300, 5_000_000 + 1_500L * 1048576, 1000, 5_000_000);   // 1.5 GiB in 5 min
        RunTimeout.Decide(s, AtTimeout, Start, Timeout).Should().Be(RunTimeout.Verdict.Extend);
        RunTimeout.NextDeadline(RunTimeout.Verdict.Extend, AtTimeout, Start, Timeout).Should().Be(AtTimeout + RunTimeout.Step);
    }

    [Fact]
    public void A_run_waiting_on_a_lock_is_stopped()
    {
        var s = new IoSample(1300, 5_000_000 + 40_000, 1000, 5_000_000);   // a few polling queries in 5 min
        RunTimeout.Decide(s, AtTimeout, Start, Timeout).Should().Be(RunTimeout.Verdict.Stalled);
    }

    [Fact]
    public void A_finished_step_counts_as_progress()
    {
        var s = new IoSample(1300, 1_000, 1000, 900_000_000);   // the copy process exited, the next step started
        RunTimeout.Decide(s, AtTimeout, Start, Timeout).Should().Be(RunTimeout.Verdict.Extend);
    }

    [Fact]
    public void Unreadable_counters_keep_the_old_behaviour()
    {
        RunTimeout.Decide(null, AtTimeout, Start, Timeout).Should().Be(RunTimeout.Verdict.Unreadable);
    }

    [Fact]
    public void Never_past_the_hard_limit()
    {
        var limit = Start.AddMinutes(Timeout * RunTimeout.HardLimitFactor);
        var busy = new IoSample(1300, 9_000_000_000, 1000, 1);
        RunTimeout.Decide(busy, limit, Start, Timeout).Should().Be(RunTimeout.Verdict.HardLimit);
        RunTimeout.NextDeadline(RunTimeout.Verdict.Extend, limit.AddMinutes(-3), Start, Timeout).Should().Be(limit);
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
    public async Task Sampler_reads_the_session_io_and_remembers_the_last_sample()
    {
        if (!OperatingSystem.IsLinux() || !File.Exists("/usr/bin/setsid") && !File.Exists("/bin/setsid")) return;
        var dir = Directory.CreateTempSubdirectory("backora-io-").FullName;
        var session = new LocalBash();
        try
        {
            var pid = await DetachedProcess.StartAsync(session, dir, "for i in $(seq 1 40); do head -c 2000000 /dev/zero > ./junk; sleep 0.25; done", CancellationToken.None);
            pid.Should().BeGreaterThan(0);
            await Task.Delay(1500);
            var first = await DetachedProcess.SampleIoAsync(session, dir, CancellationToken.None);
            first.Should().NotBeNull();
            first!.PreviousEpoch.Should().BeNull();
            await Task.Delay(1500);
            var second = await DetachedProcess.SampleIoAsync(session, dir, CancellationToken.None);
            second!.PreviousBytes.Should().Be(first.Bytes);
            second.PreviousEpoch.Should().Be(first.Epoch);
            await DetachedProcess.TerminateAsync(session, dir, CancellationToken.None);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public async Task Sampler_returns_null_without_a_process()
    {
        if (OperatingSystem.IsWindows()) return;
        var dir = Directory.CreateTempSubdirectory("backora-io-").FullName;
        try { (await DetachedProcess.SampleIoAsync(new LocalBash(), dir, CancellationToken.None)).Should().BeNull(); }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
