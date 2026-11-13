using System.Diagnostics;
using System.Security.Cryptography;
using Backora.Application.Abstractions;
using Backora.Application.Encryption;
using Backora.Application.Execution;
using Backora.Domain.Backups;
using FluentAssertions;
using Xunit;

namespace Backora.UnitTests;

/// <summary>
/// The worker compresses (zstd) and/or encrypts (age) each backup into a staging folder on the share, uploads it and
/// later restores it. A backup folder (PostgreSQL base.tar + pg_wal.tar + backup_manifest) becomes ONE archive; a
/// single-file backup becomes one .zst. Run against real zstd/age/python3/tar with a stand-in for rclone that stores
/// objects in a folder: the stored objects must restore to the exact backup, and a corrupted object must never restore.
/// </summary>
public sealed class TransferScriptsTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("backora-transfer-").FullName;
    private string Src => Path.Combine(_root, "share", "job");
    private string Staging => Path.Combine(_root, "share", ".backora-staging", "offload-1-a1");
    private string Bucket => Path.Combine(_root, "bucket");
    private string Work => Path.Combine(_root, "backora-offload-1-a1");
    private string Rclone => Path.Combine(_root, "rclone");

    private static bool Has(string tool) => OperatingSystem.IsLinux() &&
        Process.Start(new ProcessStartInfo("/bin/sh", ["-c", $"command -v {tool}"]) { RedirectStandardOutput = true })!.StandardOutput.ReadToEnd().Trim().Length > 0;
    private static bool CanRun => Has("setsid") && Has("zstd") && Has("python3") && Has("tar");

    private sealed class LocalBash : IRemoteSession
    {
        public async Task<CommandResult> RunAsync(string command, TimeSpan timeout, CancellationToken ct)
        {
            using var p = Process.Start(new ProcessStartInfo("/bin/bash", ["-c", command]) { RedirectStandardOutput = true, RedirectStandardError = true })!;
            var o = await p.StandardOutput.ReadToEndAsync(ct); var e = await p.StandardError.ReadToEndAsync(ct);
            await p.WaitForExitAsync(ct);
            return new CommandResult(p.ExitCode, o, e);
        }
        public Task WriteFileAsync(string path, string content, short mode, CancellationToken ct) => File.WriteAllTextAsync(path, content, ct);
        public Task<CommandResult> RunWithInputAsync(string command, ReadOnlyMemory<byte> stdin, TimeSpan timeout, CancellationToken ct) => throw new NotSupportedException();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    // A PostgreSQL physical backup folder and a MariaDB physical single file, as the templates write them.
    private readonly Dictionary<string, byte[]> _files = new()
    {
        ["pg-2026-11-13_07/backup_manifest"] = "{\"PostgreSQL-Backup-Manifest-Version\": 1}\n"u8.ToArray(),
        ["pg-2026-11-13_07/base.tar"] = Compressible(24 << 20),
        ["pg-2026-11-13_07/pg_wal.tar"] = Compressible(3 << 20),
        ["db-2026-11-13_07.xbstream"] = Compressible(20 << 20),
    };

    private static byte[] Compressible(int size)
    {
        var b = new byte[size];
        var noise = new byte[4096]; Random.Shared.NextBytes(noise);
        for (var i = 0; i < size; i += 64 * 1024) Array.Copy(noise, 0, b, i, Math.Min(noise.Length, size - i));
        return b;
    }

    private List<StageItem> _items = [];

    private async Task SetupAsync(OffloadRun offload)
    {
        foreach (var (rel, content) in _files)
        {
            var f = Path.Combine(Src, rel); Directory.CreateDirectory(Path.GetDirectoryName(f)!); await File.WriteAllBytesAsync(f, content);
        }
        Directory.CreateDirectory(Path.Combine(Work, "members")); Directory.CreateDirectory(Bucket);
        _items = StageItem.For(offload, _files.Select(kv => (kv.Key, (long)kv.Value.Length)));
        for (var n = 0; n < _items.Count; n++)
            if (_items[n].Packed) await File.WriteAllTextAsync(Path.Combine(Work, "members", $"{n}.txt"), string.Join('\n', _items[n].Members) + "\n");
        await File.WriteAllTextAsync(Path.Combine(Work, "hasher.py"), ChecksumScripts.Hasher);
        await File.WriteAllTextAsync(Path.Combine(Work, TransferScripts.ItemsList), StageItem.ItemsFile(_items));
        await File.WriteAllTextAsync(Path.Combine(Work, AgeScripts.EncryptedFilesList), string.Join('\n', _items.Select(i => i.Key)) + "\n");
        await File.WriteAllTextAsync(Rclone, $$"""
            #!/bin/bash
            B={{Bucket}}
            case "$1" in
              copy) src="$2"; while [ $# -gt 0 ]; do [ "$1" = --files-from ] && list="$2"; shift; done
                    while IFS= read -r k; do mkdir -p "$B/$(dirname "$k")"; cp "$src/$k" "$B/$k" || exit 1; done < "$list" ;;
              cat)  for a; do p="$a"; done; cat "$B/${p#remote:bucket/p/}" ;;
              md5sum) for a; do [ "$prev" = --files-from ] && list="$a"; prev="$a"; done
                      while IFS= read -r k; do printf '%s  %s\n' "$(md5sum "$B/$k" | cut -d' ' -f1)" "$k"; done < "$list" ;;
              check) touch {{_root}}/check.called; exit 0 ;;
            esac
            """);
        File.SetUnixFileMode(Rclone, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    private async Task<RemoteProcessStatus> RunAsync(string dir, string script)
    {
        await File.WriteAllTextAsync(Path.Combine(dir, "run.sh"), script);
        var s = new LocalBash();
        await DetachedProcess.StartAsync(s, dir, "bash ./run.sh", CancellationToken.None);
        for (var i = 0; i < 600; i++)
        {
            var st = await DetachedProcess.GetStatusAsync(s, dir, CancellationToken.None);
            if (st.State != RemoteProcessState.Running) return st;
            await Task.Delay(200);
        }
        throw new TimeoutException();
    }

    private async Task<string> RestoreDirAsync(string name, OffloadRun offload)
    {
        var dir = Path.Combine(_root, name); Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(Path.Combine(dir, AgeScripts.FilesList), string.Join('\n', _files.Keys) + "\n");
        await File.WriteAllTextAsync(Path.Combine(dir, TransferScripts.RestoreList), StageItem.RestoreFile(_items.Select(i => (i.Name, offload.ObjectKeyFor(i.Name)))));
        return dir;
    }

    private static string Sha(byte[] b) => Convert.ToHexStringLower(SHA256.HashData(b));

    [Fact]
    public void A_backup_folder_becomes_one_archive_and_a_single_file_one_object()
    {
        var offload = new OffloadRun { Compression = OffloadRun.Zstd };
        var items = StageItem.For(offload, _files.Select(kv => (kv.Key, (long)kv.Value.Length)));
        items.Select(i => i.Key).Should().Equal("db-2026-11-13_07.xbstream.zst", "pg-2026-11-13_07.tar.zst");
        var folder = items.Single(i => i.Packed);
        folder.Name.Should().Be("pg-2026-11-13_07/");
        folder.Members.Should().Equal("pg-2026-11-13_07/backup_manifest", "pg-2026-11-13_07/base.tar", "pg-2026-11-13_07/pg_wal.tar");
        folder.Size.Should().Be((24 << 20) + (3 << 20) + _files["pg-2026-11-13_07/backup_manifest"].Length);
        new OffloadRun { Compression = OffloadRun.Zstd, Encrypted = true }.ObjectKeyFor("pg-2026-11-13_07/").Should().Be("pg-2026-11-13_07.tar.zst.age");
    }

    [Fact]
    public async Task Compressed_upload_packs_the_folder_verifies_by_storage_hashes_and_restores_exactly()
    {
        if (!CanRun) return;
        var offload = new OffloadRun { Compression = OffloadRun.Zstd };
        await SetupAsync(offload);
        var total = _files.Values.Sum(v => (long)v.Length);

        var stage = await RunAsync(Work, TransferScripts.StageAndUpload(Src, Staging, compress: true, agePath: null, Rclone, "remote:bucket/p/", "--config ./rclone.conf", total));
        stage.ExitCode.Should().Be(0, await File.ReadAllTextAsync(Path.Combine(Work, "output.log")));

        // Two objects in storage: the whole PostgreSQL folder as one archive, and the single file. The share is untouched.
        Directory.GetFiles(Bucket, "*", SearchOption.AllDirectories).Select(Path.GetFileName).Should().BeEquivalentTo(["pg-2026-11-13_07.tar.zst", "db-2026-11-13_07.xbstream.zst"]);
        foreach (var (rel, content) in _files) (await File.ReadAllBytesAsync(Path.Combine(Src, rel))).Should().Equal(content);
        new FileInfo(Path.Combine(Bucket, "pg-2026-11-13_07.tar.zst")).Length.Should().BeLessThan((27 << 20) / 5, "the worker compressed the folder");
        DetachedProcess.ParseProgress(await File.ReadAllTextAsync(Path.Combine(Work, "progress"))).Should().Be(new ProgressSample(total, total, "compress", true),
            "one compress step covers every file, folder and single file alike");

        // The copy check uses the checksums recorded while staging: no second read, no rclone check.
        var check = Path.Combine(Work, "check"); Directory.CreateDirectory(check);
        await File.WriteAllTextAsync(Path.Combine(check, "hasher.py"), ChecksumScripts.Hasher);
        var verified = await RunAsync(check, ChecksumScripts.CopyCheck(Rclone, Staging, "remote:bucket/p/", "./" + AgeScripts.EncryptedFilesList, Staging, AgeScripts.FilesList, null, ".zst", 0));
        verified.ExitCode.Should().Be(0, await File.ReadAllTextAsync(Path.Combine(Work, "check.log")));
        (await File.ReadAllTextAsync(Path.Combine(Work, "check.log"))).Should().Contain("Verified 2 object(s) against the MD5 recorded");
        File.Exists(Path.Combine(_root, "check.called")).Should().BeFalse();

        // Restore: the folder is unpacked and the file decompressed, byte for byte the backup.
        var restoreDir = await RestoreDirAsync("backora-restore-1", offload);
        var target = Path.Combine(_root, "share", "job", "restore-1");
        Directory.CreateDirectory(target);
        var restored = await RunAsync(restoreDir, TransferScripts.DecompressRestore(Rclone, "remote:bucket/p/", target));
        restored.ExitCode.Should().Be(0, await File.ReadAllTextAsync(Path.Combine(restoreDir, "output.log")));
        foreach (var (rel, content) in _files) Sha(await File.ReadAllBytesAsync(Path.Combine(target, rel))).Should().Be(Sha(content), rel);
    }

    [Fact]
    public async Task A_corrupted_archive_never_restores()
    {
        if (!CanRun) return;
        var offload = new OffloadRun { Compression = OffloadRun.Zstd };
        await SetupAsync(offload);
        (await RunAsync(Work, TransferScripts.StageAndUpload(Src, Staging, true, null, Rclone, "remote:bucket/p/", "", 1))).ExitCode.Should().Be(0);
        var obj = Path.Combine(Bucket, "pg-2026-11-13_07.tar.zst");
        var bytes = await File.ReadAllBytesAsync(obj); bytes[bytes.Length / 2] ^= 0xFF; await File.WriteAllBytesAsync(obj, bytes);

        var restoreDir = await RestoreDirAsync("backora-restore-2", offload);
        var target = Path.Combine(_root, "restore-2"); Directory.CreateDirectory(target);
        var restored = await RunAsync(restoreDir, TransferScripts.DecompressRestore(Rclone, "remote:bucket/p/", target));
        restored.ExitCode.Should().Be(TransferScripts.ExitCompress, "zstd's frame checksum catches the corruption");
        Directory.GetFiles(target, "*", SearchOption.AllDirectories).Should().BeEmpty("a failed restore leaves nothing behind");
    }

    [Fact]
    public async Task Compressed_and_encrypted_upload_holds_only_ciphertext_and_decrypts_to_the_backup()
    {
        if (!CanRun || !Has("age") || !Has("age-keygen")) return;
        var offload = new OffloadRun { Compression = OffloadRun.Zstd, Encrypted = true };
        await SetupAsync(offload);
        var key = (await new LocalBash().RunAsync("age-keygen 2>/dev/null", TimeSpan.FromSeconds(30), default)).StdOut;
        var recipient = key.Split('\n').First(l => l.StartsWith("# public key: ", StringComparison.Ordinal))["# public key: ".Length..].Trim();
        var identity = key.Split('\n').First(l => l.StartsWith("AGE-SECRET-KEY-", StringComparison.Ordinal)).Trim();
        await File.WriteAllTextAsync(Path.Combine(Work, AgeScripts.RecipientsFile), recipient + "\n");

        var stage = await RunAsync(Work, TransferScripts.StageAndUpload(Src, Staging, true, "age", Rclone, "remote:bucket/p/", "", 1));
        stage.ExitCode.Should().Be(0, await File.ReadAllTextAsync(Path.Combine(Work, "output.log")));
        Directory.GetFiles(Bucket, "*", SearchOption.AllDirectories).Select(Path.GetFileName).Should().BeEquivalentTo(["pg-2026-11-13_07.tar.zst.age", "db-2026-11-13_07.xbstream.zst.age"]);
        foreach (var stored in Directory.GetFiles(Bucket, "*", SearchOption.AllDirectories))
            (await File.ReadAllTextAsync(stored)).Should().StartWith(AgeKeys.HeaderLine, "only ciphertext reaches storage");

        await File.WriteAllTextAsync(Path.Combine(_root, "id.txt"), identity + "\n");
        var unpacked = Path.Combine(_root, "unpacked"); Directory.CreateDirectory(unpacked);
        (await new LocalBash().RunAsync($"age -d -i '{_root}/id.txt' '{Bucket}/pg-2026-11-13_07.tar.zst.age' | zstd -dc | tar -x -C '{unpacked}'", TimeSpan.FromMinutes(1), default)).ExitCode.Should().Be(0);
        foreach (var rel in _files.Keys.Where(k => k.Contains('/')))
            Sha(await File.ReadAllBytesAsync(Path.Combine(unpacked, rel))).Should().Be(Sha(_files[rel]), "decrypt, decompress, unpack: the original file");
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
