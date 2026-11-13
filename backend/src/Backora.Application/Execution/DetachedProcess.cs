using System.Globalization;
using System.Text;
using Backora.Application.Abstractions;
using Backora.Domain;
using Backora.Domain.Execution;

namespace Backora.Application.Execution;

public enum RemoteProcessState { Running, Exited, Lost, NotStarted }

public sealed record RemoteProcessStatus(RemoteProcessState State, int? ExitCode, int? Pid, long LogSize, ProgressSample? Progress = null);

/// <summary>
/// How far a running script has got: bytes written so far and the bytes expected (null = unknown) as the template
/// reports them in <c>{dir}/progress</c> ("done total stage"); or, for a template that reports nothing, the bytes
/// written by the run's processes (<see cref="FromTemplate"/> false, no total).
/// </summary>
public sealed record ProgressSample(long Bytes, long? TotalBytes, string? Stage, bool FromTemplate);

public sealed record LogChunk(string Text, long NewOffset);

/// <summary>I/O done by a run's processes (bytes read + written) now and at the previous sample (remote clock).</summary>
public sealed record IoSample(long Epoch, long Bytes, long? PreviousEpoch, long? PreviousBytes);

/// <summary>
/// What happens when a backup reaches its timeout. A large backup that is still reading and writing data is not stuck,
/// and killing it throws away hours of work (and the retry would hit the same wall), so the deadline is extended in
/// short steps while the run keeps doing I/O — never past <see cref="HardLimitFactor"/> × the configured timeout.
/// A run that does no I/O (waiting on a lock, a dead mount) or whose counters cannot be read is stopped as before.
/// </summary>
public static class RunTimeout
{
    public const int HardLimitFactor = 4;
    public static readonly TimeSpan FirstCheck = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan Step = TimeSpan.FromMinutes(15);
    /// <summary>Less than this per minute is not progress (a session polling a lock moves a few bytes).</summary>
    public const long MinBytesPerMinute = 1L << 20;

    public enum Verdict { Measure, Extend, Stalled, Unreadable, HardLimit }

    public static DateTimeOffset HardLimit(DateTimeOffset startedAt, int timeoutMinutes) => startedAt.AddMinutes((double)timeoutMinutes * HardLimitFactor);

    public static Verdict Decide(IoSample? sample, DateTimeOffset now, DateTimeOffset startedAt, int timeoutMinutes)
    {
        if (now >= HardLimit(startedAt, timeoutMinutes)) return Verdict.HardLimit;
        if (sample is null) return Verdict.Unreadable;
        if (sample.PreviousEpoch is not { } pe || sample.PreviousBytes is not { } pb || sample.Epoch <= pe) return Verdict.Measure;
        var delta = sample.Bytes - pb;
        // A finished child takes its counters with it, so a lower total means a step ended and the next one began.
        if (delta < 0) return Verdict.Extend;
        var minutes = Math.Max(1.0, (sample.Epoch - pe) / 60.0);
        return delta >= MinBytesPerMinute * minutes ? Verdict.Extend : Verdict.Stalled;
    }

    /// <summary>Next deadline after a Measure/Extend verdict, capped at the hard limit.</summary>
    public static DateTimeOffset NextDeadline(Verdict verdict, DateTimeOffset now, DateTimeOffset startedAt, int timeoutMinutes)
    {
        var next = now + (verdict == Verdict.Measure ? FirstCheck : Step);
        var cap = HardLimit(startedAt, timeoutMinutes);
        return next < cap ? next : cap;
    }
}

/// <summary>
/// Runs a command detached on a remote host inside a private per-operation directory:
/// <c>{dir}/pid</c>, <c>{dir}/output.log</c>, <c>{dir}/exit_code</c>. The wrapper runs in its own session
/// (setsid) so the whole process tree can be signalled for cancellation, and it records the real exit code
/// atomically (write-then-rename). Because the process is detached, a Backora worker restart does not
/// interrupt a backup: another worker adopts the operation by reading these files.
/// </summary>
public static class DetachedProcess
{
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(60);

    public static async Task PrepareDirectoryAsync(IRemoteSession session, string directory, CancellationToken ct)
    {
        EnsureSafe(directory);
        var q = ShellQuote.Quote(directory);
        var parent = ShellQuote.Quote(directory[..directory.LastIndexOf('/')] is { Length: > 0 } p ? p : "/");
        var result = await session.RunAsync($"umask 077 && mkdir -p {parent} && mkdir -p -m 700 {q} && test -w {q} && echo ok", CommandTimeout, ct);
        if (!result.Succeeded || !result.StdOut.Contains("ok", StringComparison.Ordinal))
        {
            throw new RemoteOperationException(FailureClass.Configuration, $"Cannot create working directory {directory}: {Trim(result.StdErr)}");
        }
    }

    /// <summary>Starts <paramref name="innerCommand"/> (already shell-safe) detached. Idempotent: an existing pid file is adopted.</summary>
    public static async Task<int> StartAsync(IRemoteSession session, string directory, string innerCommand, CancellationToken ct)
    {
        EnsureSafe(directory);
        var q = ShellQuote.Quote(directory);
        var existing = await session.RunAsync($"cat {q}/pid 2>/dev/null", CommandTimeout, ct);
        if (existing.Succeeded && int.TryParse(existing.StdOut.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var adopted))
        {
            return adopted;
        }

        var wrapper = $"echo $$ > ./pid; {innerCommand} > ./output.log 2>&1; rc=$?; echo $rc > ./exit_code.tmp && mv ./exit_code.tmp ./exit_code";
        var launch = $"cd {q} && (command -v setsid >/dev/null 2>&1 && S=setsid || S=; nohup $S bash -c {ShellQuote.Quote(wrapper)} </dev/null >/dev/null 2>&1 &) ; " +
                     "for i in $(seq 1 50); do [ -s ./pid ] && break; sleep 0.1; done; cat ./pid";
        var result = await session.RunAsync(launch, CommandTimeout, ct);
        if (!int.TryParse(result.StdOut.Trim().Split('\n').LastOrDefault()?.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var pid))
        {
            throw new RemoteOperationException(FailureClass.Internal, $"Remote process did not report a PID. stderr: {Trim(result.StdErr)}");
        }
        return pid;
    }

    public static async Task<RemoteProcessStatus> GetStatusAsync(IRemoteSession session, string directory, CancellationToken ct)
    {
        EnsureSafe(directory);
        var q = ShellQuote.Quote(directory);
        var script =
            $"cd {q} 2>/dev/null || {{ echo NODIR; exit 0; }}; " +
            "P=$(cat ./pid 2>/dev/null); " +
            "if [ -f ./exit_code ]; then echo \"EXIT $(cat ./exit_code)\"; " +
            "elif [ -n \"$P\" ] && kill -0 \"$P\" 2>/dev/null; then echo RUNNING; " +
            "elif [ -n \"$P\" ]; then echo LOST; else echo NOTSTARTED; fi; " +
            "echo \"PID ${P:-0}\"; echo \"SIZE $(wc -c < ./output.log 2>/dev/null || echo 0)\"; " +
            "if [ -f ./progress ]; then echo \"PROGRESS $(head -c 200 ./progress)\"; " +
            "elif [ -n \"$P\" ] && [ ! -f ./exit_code ]; then W=0; for p in $( { pgrep -s \"$P\" 2>/dev/null; echo \"$P\"; } | sort -u); do " +
            "v=$(awk '/^wchar:/{print $2}' /proc/$p/io 2>/dev/null); [ -n \"$v\" ] && W=$((W+v)); done; [ \"$W\" -gt 0 ] && echo \"WRITTEN $W\"; fi";
        var result = await session.RunAsync(script, CommandTimeout, ct);
        var lines = result.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (lines.Length == 0 || lines[0] == "NODIR") return new RemoteProcessStatus(RemoteProcessState.NotStarted, null, null, 0);

        int? pid = null;
        long size = 0;
        ProgressSample? progress = null;
        foreach (var l in lines)
        {
            if (l.StartsWith("PID ", StringComparison.Ordinal) && int.TryParse(l[4..], out var p) && p > 0) pid = p;
            if (l.StartsWith("SIZE ", StringComparison.Ordinal)) long.TryParse(l[5..].Trim(), out size);
            if (l.StartsWith("PROGRESS ", StringComparison.Ordinal)) progress = ParseProgress(l[9..]);
            if (l.StartsWith("WRITTEN ", StringComparison.Ordinal) && long.TryParse(l[8..].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var w))
                progress = new ProgressSample(w, null, null, FromTemplate: false);
        }

        var head = lines[0];
        if (head.StartsWith("EXIT ", StringComparison.Ordinal))
        {
            return new RemoteProcessStatus(RemoteProcessState.Exited, int.TryParse(head[5..], out var rc) ? rc : -1, pid, size, progress);
        }
        return head switch
        {
            "RUNNING" => new RemoteProcessStatus(RemoteProcessState.Running, null, pid, size, progress),
            "LOST" => new RemoteProcessStatus(RemoteProcessState.Lost, null, pid, size),
            _ => new RemoteProcessStatus(RemoteProcessState.NotStarted, null, pid, size),
        };
    }

    /// <summary>"done total stage" as written by the templates' progress helper; null when malformed.</summary>
    public static ProgressSample? ParseProgress(string line)
    {
        var parts = line.Trim().Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 1 || !long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var done)) return null;
        long? total = parts.Length > 1 && long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var t) && t > 0 ? t : null;
        var stage = parts.Length > 2 ? new string(parts[2].Trim().Where(c => !char.IsControl(c)).Take(40).ToArray()) : null;
        return new ProgressSample(done, total, string.IsNullOrEmpty(stage) ? null : stage, FromTemplate: true);
    }

    /// <summary>Reads up to 256 KiB of new log output starting at <paramref name="offset"/> (byte exact via base64).</summary>
    public static async Task<LogChunk> ReadLogAsync(IRemoteSession session, string directory, long offset, CancellationToken ct, string fileName = "output.log")
    {
        EnsureSafe(directory);
        var file = ShellQuote.Quote(directory + "/" + fileName);
        var result = await session.RunAsync($"tail -c +{offset + 1} {file} 2>/dev/null | head -c 262144 | base64 | tr -d '\\n'", CommandTimeout, ct);
        if (!result.Succeeded || string.IsNullOrWhiteSpace(result.StdOut)) return new LogChunk(string.Empty, offset);
        byte[] bytes;
        try { bytes = Convert.FromBase64String(result.StdOut.Trim()); }
        catch (FormatException) { return new LogChunk(string.Empty, offset); }
        return new LogChunk(Encoding.UTF8.GetString(bytes), offset + bytes.Length);
    }

    /// <summary>
    /// Bytes read and written so far by every process in the run's session (/proc/PID/io rchar + wchar), together
    /// with the previous sample, which is kept in <c>{dir}/io_sample</c> so the comparison survives a worker restart.
    /// Returns null when the counters cannot be read (no /proc, not permitted, process gone).
    /// </summary>
    public static async Task<IoSample?> SampleIoAsync(IRemoteSession session, string directory, CancellationToken ct)
    {
        EnsureSafe(directory);
        var q = ShellQuote.Quote(directory);
        var script =
            $"cd {q} 2>/dev/null || exit 0; P=$(cat ./pid 2>/dev/null); [ -n \"$P\" ] || exit 0; " +
            "N=0; B=0; for p in $( { pgrep -s \"$P\" 2>/dev/null; echo \"$P\"; } | sort -u); do " +
            "v=$(awk '/^(rchar|wchar):/{s+=$2} END{printf \"%.0f\", s}' /proc/$p/io 2>/dev/null) || continue; " +
            "[ -n \"$v\" ] || continue; N=$((N+1)); B=$((B+v)); done; [ \"$N\" -gt 0 ] || exit 0; " +
            "E=$(date +%s); echo \"NOW $E $B\"; [ -f ./io_sample ] && echo \"PREV $(cat ./io_sample)\"; echo \"$E $B\" > ./io_sample";
        var result = await session.RunAsync(script, CommandTimeout, ct);
        long? epoch = null, bytes = null, prevEpoch = null, prevBytes = null;
        foreach (var line in result.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var p = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (p.Length != 3 || !long.TryParse(p[1], NumberStyles.None, CultureInfo.InvariantCulture, out var e) || !long.TryParse(p[2], NumberStyles.None, CultureInfo.InvariantCulture, out var b)) continue;
            if (p[0] == "NOW") { epoch = e; bytes = b; }
            else if (p[0] == "PREV") { prevEpoch = e; prevBytes = b; }
        }
        return epoch is null || bytes is null ? null : new IoSample(epoch.Value, bytes.Value, prevEpoch, prevBytes);
    }

    /// <summary>Terminates the process group: SIGTERM, grace period, then SIGKILL.</summary>
    public static async Task<bool> TerminateAsync(IRemoteSession session, string directory, CancellationToken ct)
    {
        EnsureSafe(directory);
        var q = ShellQuote.Quote(directory);
        // Portable across GNU coreutils/procps and busybox: signal the process group and the session
        // (the wrapper runs under setsid, so session id == pid), then verify nothing in the session survives.
        var script =
            $"P=$(cat {q}/pid 2>/dev/null); [ -z \"$P\" ] && {{ echo NOPID; exit 0; }}; " +
            "sig() { kill -$1 -- -\"$P\" 2>/dev/null || kill -s $1 -\"$P\" 2>/dev/null; pkill -$1 -s \"$P\" 2>/dev/null; kill -$1 \"$P\" 2>/dev/null; true; }; " +
            "alive() { pgrep -s \"$P\" >/dev/null 2>&1 || kill -0 \"$P\" 2>/dev/null; }; " +
            "sig TERM; for i in $(seq 1 20); do alive || break; sleep 0.5; done; " +
            "if alive; then sig KILL; sleep 0.5; fi; " +
            "alive && echo ALIVE || echo DEAD";
        var result = await session.RunAsync(script, TimeSpan.FromSeconds(45), ct);
        return result.StdOut.Contains("DEAD", StringComparison.Ordinal) || result.StdOut.Contains("NOPID", StringComparison.Ordinal);
    }

    public static async Task RemoveDirectoryAsync(IRemoteSession session, string directory, CancellationToken ct)
    {
        EnsureSafe(directory);
        await session.RunAsync($"rm -rf -- {ShellQuote.Quote(directory)}", CommandTimeout, ct);
    }

    private static void EnsureSafe(string directory)
    {
        if (!ShellQuote.IsSafeAbsolutePath(directory) || directory.Count(c => c == '/') < 2 || !directory.Contains("backora", StringComparison.OrdinalIgnoreCase))
        {
            throw new RemoteOperationException(FailureClass.Configuration, $"Refusing to operate on unsafe working directory '{directory}'.");
        }
    }

    internal static string Trim(string s) => s.Length > 500 ? s[..500] : s.Trim();
}
