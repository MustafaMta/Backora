using System.Diagnostics;
using System.Security.Cryptography;
using Backora.Application.Abstractions;
using Backora.Application.Execution;
using FluentAssertions;
using Xunit;

namespace Backora.UnitTests;

/// <summary>
/// The checksum steps run detached on the worker and report live progress. Both the fast hasher (parallel read-ahead,
/// SHA-256 + MD5 in one pass) and the sha256sum fallback must produce exactly the checksums verification relies on,
/// and a copy must be checked against storage's own hashes without reading the source again — falling back to a full
/// rclone check whenever storage cannot vouch for an object.
/// </summary>
public class ChecksumScriptsTests
{
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
        public async Task WriteFileAsync(string path, string content, short mode, CancellationToken ct) => await File.WriteAllTextAsync(path, content, ct);
        public Task<CommandResult> RunWithInputAsync(string command, ReadOnlyMemory<byte> stdin, TimeSpan timeout, CancellationToken ct) => throw new NotSupportedException();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static bool CanRun => OperatingSystem.IsLinux() && (File.Exists("/usr/bin/setsid") || File.Exists("/bin/setsid"));
    private static bool HasPython => CanRun && Process.Start(new ProcessStartInfo("/bin/sh", ["-c", "command -v python3"]) { RedirectStandardOutput = true })!.StandardOutput.ReadToEnd().Trim().Length > 0;
    private const string Header = "age-encryption.org/v1";
    private static string Md5(byte[] b) => Convert.ToHexStringLower(MD5.HashData(b));
    private static string Sha(byte[] b) => Convert.ToHexStringLower(SHA256.HashData(b));

    private static async Task<RemoteProcessStatus> UntilDoneAsync(IRemoteSession s, string dir, List<ProgressSample> seen)
    {
        for (var i = 0; i < 900; i++)
        {
            var st = await DetachedProcess.GetStatusAsync(s, dir, CancellationToken.None);
            if (st.Progress is { } p) seen.Add(p);
            if (st.State != RemoteProcessState.Running) return st;
            await Task.Delay(200);
        }
        throw new TimeoutException();
    }

    private static bool HasZstd => CanRun && Process.Start(new ProcessStartInfo("/bin/sh", ["-c", "command -v zstd"]) { RedirectStandardOutput = true })!.StandardOutput.ReadToEnd().Trim().Length > 0;

    private static async Task<byte[]> ZstdDecompressAsync(string path)
    {
        using var p = Process.Start(new ProcessStartInfo("zstd", ["-dc", path]) { RedirectStandardOutput = true })!;
        using var ms = new MemoryStream();
        await p.StandardOutput.BaseStream.CopyToAsync(ms);
        await p.WaitForExitAsync();
        p.ExitCode.Should().Be(0);
        return ms.ToArray();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task The_worker_compresses_each_output_into_one_file_checksums_it_and_removes_the_original(bool fast)
    {
        if (!CanRun || !HasZstd || fast && !HasPython) return;
        var job = Directory.CreateTempSubdirectory("job-").FullName;
        var work = Directory.CreateTempSubdirectory("backora-verify-").FullName;
        var s = new LocalBash();
        try
        {
            // A dump (one file), a pg_basebackup folder (several files), an already-compressed file, a folder that also
            // holds an old file (not this run's output alone), restore staging, and leftovers of an interrupted attempt.
            var dump = new byte[64 << 20];
            for (var i = 0; i < dump.Length; i++) dump[i] = (byte)"insert into t values (1);\n"[i % 26];
            await File.WriteAllBytesAsync(Path.Combine(job, "job-2026-11-13_10.sql"), dump);
            var folder = Path.Combine(job, "job-2026-11-13_11");
            Directory.CreateDirectory(folder);
            var baseTar = new byte[256 << 20]; Random.Shared.NextBytes(baseTar); // incompressible: long enough to watch the progress
            await File.WriteAllBytesAsync(Path.Combine(folder, "base.tar"), baseTar);
            await File.WriteAllTextAsync(Path.Combine(folder, "pg_wal.tar"), "wal");
            await File.WriteAllTextAsync(Path.Combine(folder, "backup_manifest"), "{}");
            await File.WriteAllTextAsync(Path.Combine(job, "already.sql.gz"), "gz");
            var mixed = Path.Combine(job, "archive");
            Directory.CreateDirectory(mixed);
            await File.WriteAllTextAsync(Path.Combine(mixed, "new.wal"), "new");
            await File.WriteAllTextAsync(Path.Combine(mixed, "old.wal"), "old");
            File.SetLastWriteTimeUtc(Path.Combine(mixed, "old.wal"), DateTime.UtcNow.AddDays(-3));
            Directory.CreateDirectory(Path.Combine(job, "restore-1"));
            await File.WriteAllTextAsync(Path.Combine(job, "restore-1", "ignored"), "x");
            await File.WriteAllTextAsync(Path.Combine(job, "x.zst" + ChecksumScripts.CompressPartialSuffix), "half-written");
            Directory.CreateDirectory(Path.Combine(job, "y" + ChecksumScripts.RawDeleteSuffix));

            await File.WriteAllTextAsync(Path.Combine(work, "hasher.py"), ChecksumScripts.Hasher);
            await File.WriteAllTextAsync(Path.Combine(work, "scan.sh"), ChecksumScripts.BackupFiles(job, 60, compress: true));
            await DetachedProcess.StartAsync(s, work, fast ? "bash ./scan.sh" : "BACKORA_NO_PYTHON=1 bash ./scan.sh", CancellationToken.None);
            var seen = new List<ProgressSample>();
            var done = await UntilDoneAsync(s, work, seen);
            done.ExitCode.Should().Be(0, await File.ReadAllTextAsync(Path.Combine(work, "output.log")));

            var files = RemoteFiles.Parse(await File.ReadAllTextAsync(Path.Combine(work, "result")));
            files.Select(f => f.RelativePath).Should().BeEquivalentTo(
                ["job-2026-11-13_10.sql.zst", "job-2026-11-13_11.tar.zst", "already.sql.gz", "archive/new.wal"]);
            File.Exists(Path.Combine(job, "job-2026-11-13_10.sql")).Should().BeFalse("the original is removed once the compressed file is complete");
            Directory.Exists(folder).Should().BeFalse();
            Directory.Exists(mixed).Should().BeTrue("a folder with older files is not this run's output alone");
            File.Exists(Path.Combine(job, "x.zst" + ChecksumScripts.CompressPartialSuffix)).Should().BeFalse();
            Directory.Exists(Path.Combine(job, "y" + ChecksumScripts.RawDeleteSuffix)).Should().BeFalse();

            // The checksums are those of the compressed file, and it decompresses to exactly the original.
            var zst = Path.Combine(job, "job-2026-11-13_10.sql.zst");
            var zbytes = await File.ReadAllBytesAsync(zst);
            var z = files.Single(f => f.RelativePath == "job-2026-11-13_10.sql.zst");
            z.Sha256.Should().Be(Sha(zbytes));
            z.SizeBytes.Should().Be(zbytes.Length).And.BeLessThan(dump.Length / 20);
            if (fast) z.Md5.Should().Be(Md5(zbytes));
            (await ZstdDecompressAsync(zst)).Should().Equal(dump);
            var tarZst = Path.Combine(job, "job-2026-11-13_11.tar.zst");
            files.Single(f => f.RelativePath == "job-2026-11-13_11.tar.zst").Sha256.Should().Be(Sha(await File.ReadAllBytesAsync(tarZst)));
            var unpacked = Directory.CreateTempSubdirectory("unpacked-").FullName;
            var tar = Process.Start(new ProcessStartInfo("/bin/bash", ["-c", $"zstd -dc '{tarZst}' | tar -x -C '{unpacked}'"]))!;
            await tar.WaitForExitAsync();
            tar.ExitCode.Should().Be(0);
            (await File.ReadAllBytesAsync(Path.Combine(unpacked, "job-2026-11-13_11", "base.tar"))).Should().Equal(baseTar);
            (await File.ReadAllTextAsync(Path.Combine(unpacked, "job-2026-11-13_11", "pg_wal.tar"))).Should().Be("wal");
            Directory.Delete(unpacked, true);

            seen.Should().Contain(p => p.Stage == "compress" && p.TotalBytes > 0, "the compress step reports its own progress");
            var log = await File.ReadAllTextAsync(Path.Combine(work, ChecksumScripts.CompressLog));
            log.Should().Contain("COMPRESSED\tjob-2026-11-13_10.sql\tjob-2026-11-13_10.sql.zst\t")
                .And.Contain("COMPRESSED\tjob-2026-11-13_11\tjob-2026-11-13_11.tar.zst\t")
                .And.Contain("SKIPPED\tarchive\t");
        }
        finally
        {
            Directory.Delete(job, true);
            Directory.Delete(work, true);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Backup_checksums_match_and_progress_moves_inside_a_large_file(bool fast)
    {
        if (!CanRun || fast && !HasPython) return;
        var job = Directory.CreateTempSubdirectory("job-").FullName;
        var work = Directory.CreateTempSubdirectory("backora-verify-").FullName;
        var s = new LocalBash();
        try
        {
            var big = Path.Combine(job, "db.xbstream");
            var bytes = new byte[400 << 20]; Random.Shared.NextBytes(bytes);
            await File.WriteAllBytesAsync(big, bytes);
            Directory.CreateDirectory(Path.Combine(job, "folder"));
            await File.WriteAllTextAsync(Path.Combine(job, "folder", "small file.sql"), "select 1;");
            Directory.CreateDirectory(Path.Combine(job, "restore-1"));
            await File.WriteAllTextAsync(Path.Combine(job, "restore-1", "ignored"), "x");

            await File.WriteAllTextAsync(Path.Combine(work, "hasher.py"), ChecksumScripts.Hasher);
            await File.WriteAllTextAsync(Path.Combine(work, "scan.sh"), ChecksumScripts.BackupFiles(job, 60));
            await DetachedProcess.StartAsync(s, work, fast ? "bash ./scan.sh" : "BACKORA_NO_PYTHON=1 bash ./scan.sh", CancellationToken.None);
            var seen = new List<ProgressSample>();
            var done = await UntilDoneAsync(s, work, seen);

            done.ExitCode.Should().Be(0, await File.ReadAllTextAsync(Path.Combine(work, "output.log")));
            var files = RemoteFiles.Parse(await File.ReadAllTextAsync(Path.Combine(work, "result")));
            files.Select(f => f.RelativePath).Should().BeEquivalentTo(["db.xbstream", "folder/small file.sql"], "restore staging is never verified");
            var db = files.Single(f => f.RelativePath == "db.xbstream");
            db.Sha256.Should().Be(Sha(bytes));
            db.SizeBytes.Should().Be(bytes.Length);
            if (fast) db.Md5.Should().Be(Md5(bytes), "the fast hasher computes MD5 in the same read");
            else db.Md5.Should().BeNull();
            files.Single(f => f.RelativePath == "folder/small file.sql").Sha256.Should().Be(Sha("select 1;"u8.ToArray()));
            // (Before the script writes its first line, the worker reports its I/O counter instead: FromTemplate false.)
            seen.Where(p => p.FromTemplate).Should().OnlyContain(p => p.Stage == "verify");
            // The final report: everything hashed, against the exact total.
            var final = DetachedProcess.ParseProgress(await File.ReadAllTextAsync(Path.Combine(work, "progress")));
            final.Should().Be(new ProgressSample(bytes.Length + 9, bytes.Length + 9, "verify", FromTemplate: true));
        }
        finally { Directory.Delete(job, true); Directory.Delete(work, true); }
    }

    [Fact]
    public async Task Missing_job_folder_reports_no_files()
    {
        if (!CanRun) return;
        var work = Directory.CreateTempSubdirectory("backora-verify-").FullName;
        var s = new LocalBash();
        try
        {
            await File.WriteAllTextAsync(Path.Combine(work, "scan.sh"), ChecksumScripts.BackupFiles("/nonexistent/backora-job", 60));
            await DetachedProcess.StartAsync(s, work, "bash ./scan.sh", CancellationToken.None);
            (await UntilDoneAsync(s, work, [])).ExitCode.Should().Be(0);
            (await File.ReadAllTextAsync(Path.Combine(work, "result"))).Should().Contain("NODIR");
        }
        finally { Directory.Delete(work, true); }
    }

    /// <summary>A copy folder with staged ciphertext and a fake rclone whose md5sum answers from <paramref name="remoteMd5"/>.</summary>
    private static async Task<(string Dir, string Check)> CopyFolderAsync(Dictionary<string, byte[]> staged, Func<string, string> remoteMd5, int checkExit)
    {
        var dir = Directory.CreateTempSubdirectory("backora-offload-").FullName;
        foreach (var (rel, content) in staged)
        {
            var f = Path.Combine(dir, "stage", rel + ".age");
            Directory.CreateDirectory(Path.GetDirectoryName(f)!);
            await File.WriteAllBytesAsync(f, content);
        }
        await File.WriteAllTextAsync(Path.Combine(dir, "files.txt"), string.Join('\n', staged.Keys) + "\n");
        await File.WriteAllTextAsync(Path.Combine(dir, "files.enc.txt"), string.Join('\n', staged.Keys.Select(k => k + ".age")) + "\n");
        await File.WriteAllTextAsync(Path.Combine(dir, "remote.md5.src"), string.Concat(staged.Select(kv => $"{remoteMd5(kv.Key).PadRight(32)}  {kv.Key}.age\n")));
        var fake = Path.Combine(dir, "rclone");
        await File.WriteAllTextAsync(fake, $"""
            #!/bin/bash
            case "$1" in
              md5sum) cat {dir}/remote.md5.src ;;
              check) touch {dir}/check.called; head -c 3000000 /dev/zero > /dev/null; sleep 1; echo "checked $*"; exit {checkExit} ;;
            esac
            """);
        File.SetUnixFileMode(fake, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var check = Path.Combine(dir, "check");
        Directory.CreateDirectory(check);
        await File.WriteAllTextAsync(Path.Combine(check, "hasher.py"), ChecksumScripts.Hasher);
        await File.WriteAllTextAsync(Path.Combine(check, "check.sh"), ChecksumScripts.CopyCheck(fake, dir + "/stage", "backora:bucket/p/", "./files.enc.txt", dir + "/stage", "files.txt", Header, ".age", 3_000_000));
        return (dir, check);
    }

    private static readonly Dictionary<string, byte[]> Staged = new()
    {
        ["sub/a.tar"] = System.Text.Encoding.ASCII.GetBytes(Header + "\nciphertext one"),
        ["b.sql"] = System.Text.Encoding.ASCII.GetBytes(Header + "\nciphertext two"),
    };

    [Fact]
    public async Task Copy_is_verified_against_storage_hashes_without_reading_the_source_again()
    {
        if (!HasPython) return;
        var (dir, check) = await CopyFolderAsync(Staged, k => Md5(Staged[k]), checkExit: 0);
        try
        {
            await DetachedProcess.StartAsync(new LocalBash(), check, "bash ./check.sh", CancellationToken.None);
            var seen = new List<ProgressSample>();
            (await UntilDoneAsync(new LocalBash(), check, seen)).ExitCode.Should().Be(0);
            (await File.ReadAllTextAsync(Path.Combine(dir, "check.log"))).Should().Contain("Verified 2 object(s) against the MD5 recorded");
            File.Exists(Path.Combine(dir, "check.called")).Should().BeFalse("no full rclone check when storage vouches for every object");
            var stage = await File.ReadAllTextAsync(Path.Combine(check, "stage.tsv"));
            stage.Should().Contain(Sha(Staged["b.sql"])).And.Contain(Md5(Staged["b.sql"]));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task A_hash_mismatch_in_storage_fails_the_copy()
    {
        if (!HasPython) return;
        var (dir, check) = await CopyFolderAsync(Staged, k => k == "b.sql" ? new string('0', 32) : Md5(Staged[k]), checkExit: 0);
        try
        {
            await DetachedProcess.StartAsync(new LocalBash(), check, "bash ./check.sh", CancellationToken.None);
            (await UntilDoneAsync(new LocalBash(), check, [])).ExitCode.Should().Be(1);
            (await File.ReadAllTextAsync(Path.Combine(dir, "check.log"))).Should().Contain("b.sql.age: md5 differs");
            File.Exists(Path.Combine(dir, "check.called")).Should().BeFalse("a real difference is a failure, not a reason to look again");
        }
        finally { Directory.Delete(dir, true); }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task Without_a_hash_in_storage_the_source_is_compared_by_rclone_check(int checkExit)
    {
        if (!HasPython) return;
        var (dir, check) = await CopyFolderAsync(Staged, k => k == "b.sql" ? "" : Md5(Staged[k]), checkExit);
        try
        {
            await DetachedProcess.StartAsync(new LocalBash(), check, "bash ./check.sh", CancellationToken.None);
            var seen = new List<ProgressSample>();
            (await UntilDoneAsync(new LocalBash(), check, seen)).ExitCode.Should().Be(checkExit, "the result is then rclone check's");
            File.Exists(Path.Combine(dir, "check.called")).Should().BeTrue();
            (await File.ReadAllTextAsync(Path.Combine(dir, "check.log"))).Should().Contain("did not return an MD5 for every object");
            seen.Select(p => p.Stage).Should().Contain("check");
        }
        finally { Directory.Delete(dir, true); }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_staged_file_without_the_age_header_is_flagged(bool fast)
    {
        if (!CanRun || fast && !HasPython) return;
        var staged = new Dictionary<string, byte[]> { ["sub/a.tar"] = Staged["sub/a.tar"], ["b.sql"] = "not age at all"u8.ToArray() };
        var (dir, check) = await CopyFolderAsync(staged, k => "", checkExit: 0);
        try
        {
            await DetachedProcess.StartAsync(new LocalBash(), check, fast ? "bash ./check.sh" : "BACKORA_NO_PYTHON=1 bash ./check.sh", CancellationToken.None);
            (await UntilDoneAsync(new LocalBash(), check, [])).ExitCode.Should().Be(0);
            var lines = (await File.ReadAllTextAsync(Path.Combine(check, "stage.tsv"))).Split('\n', StringSplitOptions.RemoveEmptyEntries).Where(l => l != "END").ToList();
            lines.Should().HaveCount(2);
            lines.Should().Contain(l => l.StartsWith("BADHEADER\t", StringComparison.Ordinal));
            lines.Should().Contain(l => l.StartsWith(Sha(Staged["sub/a.tar"]) + "\t", StringComparison.Ordinal));
            File.Exists(Path.Combine(dir, "check.called")).Should().BeTrue("without a complete set of MD5s the copy is compared by reading it");
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task Only_this_runs_own_outputs_are_compressed()
    {
        if (!CanRun || !HasZstd || !HasPython) return;
        var job = Directory.CreateTempSubdirectory("job-").FullName;
        var work = Directory.CreateTempSubdirectory("backora-verify-").FullName;
        var s = new LocalBash();
        try
        {
            await File.WriteAllTextAsync(Path.Combine(job, "db-2026-11-13_17.xbstream"), new string('x', 100_000));   // this run's output
            await File.WriteAllTextAsync(Path.Combine(job, "db-2026-11-13_16.xbstream"), new string('y', 100_000));   // an earlier backup's
            await File.WriteAllTextAsync(Path.Combine(job, "v1-folder-file.tar"), new string('z', 100_000));         // written by something else
            await File.WriteAllTextAsync(Path.Combine(job, "old.tar"), new string('o', 100_000));
            File.SetLastWriteTimeUtc(Path.Combine(job, "old.tar"), DateTime.UtcNow.AddHours(-3));                      // before the run started
            await File.WriteAllTextAsync(Path.Combine(work, "outputs.txt"), "db-2026-11-13_17.xbstream\ndb-2026-11-13_16.xbstream\nold.tar\n");
            await File.WriteAllTextAsync(Path.Combine(work, "exclude.txt"), "db-2026-11-13_16.xbstream\n");
            await File.WriteAllTextAsync(Path.Combine(work, "hasher.py"), ChecksumScripts.Hasher);
            var started = DateTimeOffset.UtcNow.AddMinutes(-30).ToUnixTimeSeconds();
            await File.WriteAllTextAsync(Path.Combine(work, "scan.sh"), ChecksumScripts.BackupFiles(job, 240, compress: true, notBeforeEpoch: started));
            await DetachedProcess.StartAsync(s, work, "bash ./scan.sh", CancellationToken.None);
            (await UntilDoneAsync(s, work, [])).ExitCode.Should().Be(0, await File.ReadAllTextAsync(Path.Combine(work, "output.log")));

            Directory.GetFiles(job).Select(Path.GetFileName).Should().BeEquivalentTo(
                ["db-2026-11-13_17.xbstream.zst", "db-2026-11-13_16.xbstream", "v1-folder-file.tar", "old.tar"]);
            var log = await File.ReadAllTextAsync(Path.Combine(work, ChecksumScripts.CompressLog));
            log.Should().Contain("SKIPPED\tdb-2026-11-13_16.xbstream\tit belongs to an earlier backup")
                .And.Contain("SKIPPED\tv1-folder-file.tar\tnot an output of this backup")
                .And.NotContain("old.tar", "a file from before the run started is not a candidate at all");
        }
        finally { Directory.Delete(job, true); Directory.Delete(work, true); }
    }

    [Fact]
    public async Task When_every_file_has_its_checksum_each_is_listed_once_and_none_is_read_again()
    {
        // The usual case since compression moved to the worker: every output is a compressed file whose checksums are known.
        if (!CanRun) return;
        var job = Directory.CreateTempSubdirectory("job-").FullName;
        var work = Directory.CreateTempSubdirectory("backora-verify-").FullName;
        try
        {
            await File.WriteAllTextAsync(Path.Combine(job, "db.tar.zst"), "compressed backup");
            await File.WriteAllTextAsync(Path.Combine(job, "files.tar.zst"), "more");
            var fake = new string('a', 64); var fakeMd5 = new string('b', 32);
            await File.WriteAllTextAsync(Path.Combine(work, "known.txt"), $"{fake}\t{fakeMd5}\t17\tdb.tar.zst\n{fake}\t{fakeMd5}\t4\tfiles.tar.zst\n");
            await File.WriteAllTextAsync(Path.Combine(work, "hasher.py"), ChecksumScripts.Hasher);
            await File.WriteAllTextAsync(Path.Combine(work, "scan.sh"), ChecksumScripts.BackupFiles(job, 60));
            await DetachedProcess.StartAsync(new LocalBash(), work, "bash ./scan.sh", CancellationToken.None);
            (await UntilDoneAsync(new LocalBash(), work, [])).ExitCode.Should().Be(0);

            var files = RemoteFiles.Parse(await File.ReadAllTextAsync(Path.Combine(work, "result")));
            files.Select(f => f.RelativePath).Should().BeEquivalentTo(["db.tar.zst", "files.tar.zst"], "each file exactly once");
            files.Should().OnlyContain(f => f.Sha256 == fake, "a known checksum is used as is: nothing is read again");
        }
        finally { Directory.Delete(job, true); Directory.Delete(work, true); }
    }

    [Fact]
    public async Task Checksums_the_compress_step_computed_are_used_instead_of_reading_the_file_again()
    {
        if (!CanRun) return;
        var job = Directory.CreateTempSubdirectory("job-").FullName;
        var work = Directory.CreateTempSubdirectory("backora-verify-").FullName;
        try
        {
            await File.WriteAllTextAsync(Path.Combine(job, "db.xbstream.zst"), "compressed backup");
            await File.WriteAllTextAsync(Path.Combine(job, "other.sql.zst"), "another file");
            await File.WriteAllTextAsync(Path.Combine(job, "resized.tar.zst"), "grew after it was hashed");
            var fake = new string('a', 64); var fakeMd5 = new string('b', 32);
            await File.WriteAllTextAsync(Path.Combine(work, "known.txt"),
                $"{fake}\t{fakeMd5}\t17\tdb.xbstream.zst\n{fake}\t{fakeMd5}\t1\tresized.tar.zst\n");
            await File.WriteAllTextAsync(Path.Combine(work, "hasher.py"), ChecksumScripts.Hasher);
            await File.WriteAllTextAsync(Path.Combine(work, "scan.sh"), ChecksumScripts.BackupFiles(job, 60));
            await DetachedProcess.StartAsync(new LocalBash(), work, "bash ./scan.sh", CancellationToken.None);
            (await UntilDoneAsync(new LocalBash(), work, [])).ExitCode.Should().Be(0);

            var files = RemoteFiles.Parse(await File.ReadAllTextAsync(Path.Combine(work, "result"))).ToDictionary(f => f.RelativePath);
            files.Should().HaveCount(3);
            files["db.xbstream.zst"].Sha256.Should().Be(fake, "the checksum taken while compressing is used as is: the file is not read again");
            files["db.xbstream.zst"].Md5.Should().Be(fakeMd5);
            files["other.sql.zst"].Sha256.Should().Be(Sha("another file"u8.ToArray()), "a file without one is hashed here");
            files["resized.tar.zst"].Sha256.Should().Be(Sha("grew after it was hashed"u8.ToArray()), "a size that no longer matches is never trusted");
        }
        finally { Directory.Delete(job, true); Directory.Delete(work, true); }
    }
}
