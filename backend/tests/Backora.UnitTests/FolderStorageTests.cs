using System.Diagnostics;
using Backora.Application.Abstractions;
using Backora.Application.Execution;
using Backora.Application.Storage;
using Backora.Contracts.V1;
using Backora.Domain;
using Backora.Domain.Infrastructure;
using Backora.Infrastructure.Storage;
using FluentAssertions;
using Xunit;

namespace Backora.UnitTests;

/// <summary>
/// A storage site can be a folder on the execution host (a mounted NFS share, e.g. a DR site). Copies go through rclone's
/// local backend; listing, retention deletes and the connection test run as shell commands on the execution host. The
/// commands run here against real files (Linux), and the rclone config against real rclone when it is installed.
/// </summary>
public class FolderStorageTests
{
    private static bool CanRun => OperatingSystem.IsLinux();
    private static bool HasRclone => CanRun && Process.Start(new ProcessStartInfo("/bin/sh", ["-c", "command -v rclone"]) { RedirectStandardOutput = true })!.StandardOutput.ReadToEnd().Trim().Length > 0;

    private static async Task<(int Code, string Out, string Err)> BashAsync(string command, string? cwd = null)
    {
        using var p = Process.Start(new ProcessStartInfo("/bin/bash", ["-c", command]) { RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = cwd ?? "/" })!;
        var o = await p.StandardOutput.ReadToEndAsync();
        var e = await p.StandardError.ReadToEndAsync();
        await p.WaitForExitAsync();
        return (p.ExitCode, o, e);
    }

    [Theory]
    [InlineData("/mnt/dr-site", null)]
    [InlineData("/mnt/dr-site/", null)]
    [InlineData("relative/path", "absolute")]
    [InlineData("/", "absolute")]
    [InlineData("/etc/backups", "system folder")]
    [InlineData("/mnt/../etc", "absolute")]
    [InlineData("/mnt/dr site", "absolute")]
    [InlineData("/mnt/x;rm -rf /", "absolute")]
    [InlineData("/BackoraDB", "staging share")]
    [InlineData("/BackoraDB/dr", "staging share")]
    [InlineData("", "Enter the folder")]
    public void Only_a_plain_folder_outside_system_paths_and_the_staging_share_can_be_a_site(string path, string? problem)
    {
        var result = StorageConnections.LocalPathProblem(path, "/BackoraDB");
        if (problem is null) result.Should().BeNull();
        else result.Should().Contain(problem);
    }

    [Fact]
    public void A_folder_site_connects_through_rclones_local_backend_without_credentials()
    {
        var d = new S3Destination { Name = "Branch DR", Kind = StorageKind.Filesystem, LocalPath = "/mnt/dr-site/" };
        var c = StorageConnections.For(d, null!);
        c.IsLocal.Should().BeTrue();
        Rclone.BuildConfig(c).Should().Contain("type = local").And.NotContain("access_key");
        Rclone.RemotePath(c, "backora/db/2026-11-13-10-r1/").Should().Be("backora:/mnt/dr-site/backora/db/2026-11-13-10-r1/");
        Rclone.RemotePath(c, "db/x.zst", Rclone.ReplicaTargetRemote).Should().Be("backora-dst:/mnt/dr-site/db/x.zst");
        FluentActions.Invoking(() => Rclone.RemotePath(c, "../etc/")).Should().Throw<ArgumentException>();
        StorageConnections.Describe(d, "db/p/").Should().Be("/mnt/dr-site/db/p/");
        Rclone.SameAccount(c, new S3Connection("https://s3.example", "r", "b", "a", "s", false)).Should().BeFalse("an S3 source and a folder target need two remotes");
    }

    [Fact]
    public async Task Rclone_copies_into_a_folder_site_and_checks_it_by_md5()
    {
        if (!HasRclone) return;
        var root = Directory.CreateTempSubdirectory("dr-site-").FullName;
        var src = Directory.CreateTempSubdirectory("src-").FullName;
        var work = Directory.CreateTempSubdirectory("work-").FullName;
        try
        {
            await File.WriteAllTextAsync(Path.Combine(src, "db-2026-11-13_10.xbstream.zst"), "compressed backup");
            await File.WriteAllTextAsync(Path.Combine(work, "files.txt"), "db-2026-11-13_10.xbstream.zst\n");
            var c = new S3Connection("", "", "", "", "", false, root);
            await File.WriteAllTextAsync(Path.Combine(work, "rclone.conf"), Rclone.BuildConfig(c));
            var target = Rclone.RemotePath(c, "db/2026-11-13-10-r1/");
            var copy = await BashAsync($"rclone copy '{src}' '{target}' --files-from ./files.txt --checksum --config ./rclone.conf", work);
            copy.Code.Should().Be(0, copy.Err);
            File.ReadAllText(Path.Combine(root, "db", "2026-11-13-10-r1", "db-2026-11-13_10.xbstream.zst")).Should().Be("compressed backup");
            var md5 = await BashAsync($"rclone md5sum '{target}' --files-from ./files.txt --config ./rclone.conf", work);
            md5.Code.Should().Be(0, md5.Err);
            md5.Out.Should().Contain("db-2026-11-13_10.xbstream.zst").And.Contain(Convert.ToHexStringLower(System.Security.Cryptography.MD5.HashData("compressed backup"u8.ToArray())));
        }
        finally { Directory.Delete(root, true); Directory.Delete(src, true); Directory.Delete(work, true); }
    }

    [Fact]
    public async Task Listing_browsing_and_deleting_work_on_real_folders_and_never_leave_the_site()
    {
        if (!CanRun) return;
        var root = Directory.CreateTempSubdirectory("dr-site-").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "db", "2026-11-13-10-r1"));
            Directory.CreateDirectory(Path.Combine(root, "db", "2026-11-12-10-r0"));
            await File.WriteAllTextAsync(Path.Combine(root, "db", "2026-11-13-10-r1", "a.zst"), "12345");
            await File.WriteAllTextAsync(Path.Combine(root, "db", "2026-11-13-10-r1", "b.zst"), "1");
            await File.WriteAllTextAsync(Path.Combine(root, "db", "2026-11-12-10-r0", "old.zst"), "old");

            var all = WorkerFolderStorage.ParseFiles((await BashAsync(WorkerFolderStorage.FilesCommand(root, "db/"))).Out, "db/");
            all.Select(o => o.Key).Should().BeEquivalentTo(["db/2026-11-13-10-r1/a.zst", "db/2026-11-13-10-r1/b.zst", "db/2026-11-12-10-r0/old.zst"]);
            all.Single(o => o.Key.EndsWith("a.zst")).Size.Should().Be(5);
            all.Should().OnlyContain(o => o.LastModified > DateTimeOffset.UtcNow.AddHours(-1));
            (await BashAsync(WorkerFolderStorage.FilesCommand(root, "missing/"))).Out.Should().BeEmpty("a folder that doesn't exist lists nothing");

            var children = (await BashAsync(WorkerFolderStorage.ChildrenCommand(root, "db/"))).Out;
            children.Should().Contain("D\t2026-11-13-10-r1").And.Contain("D\t2026-11-12-10-r0");

            var deleted = await BashAsync(WorkerFolderStorage.DeleteCommand(root, "db/2026-11-12-10-r0"));
            deleted.Out.Trim().Should().Be("1");
            Directory.Exists(Path.Combine(root, "db", "2026-11-12-10-r0")).Should().BeFalse();
            Directory.Exists(Path.Combine(root, "db", "2026-11-13-10-r1")).Should().BeTrue("only that recovery point goes");

            FluentActions.Invoking(() => WorkerFolderStorage.DeleteCommand(root, "/")).Should().Throw<ArgumentException>();
            FluentActions.Invoking(() => WorkerFolderStorage.DeleteCommand(root, "db/../..")).Should().Throw<ArgumentException>();
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task A_scan_of_a_large_share_stops_at_its_limit_instead_of_stalling()
    {
        if (!CanRun) return;
        var root = Directory.CreateTempSubdirectory("dr-site-").FullName;
        try
        {
            for (var i = 0; i < 50; i++) await File.WriteAllTextAsync(Path.Combine(root, $"f{i}.dat"), "x");
            var capped = WorkerFolderStorage.ParseFiles((await BashAsync(WorkerFolderStorage.FilesCommand(root, "", limit: 10))).Out, "");
            capped.Should().HaveCount(10);
            var all = WorkerFolderStorage.ParseFiles((await BashAsync(WorkerFolderStorage.FilesCommand(root, ""))).Out, "");
            all.Should().HaveCount(50);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task The_connection_test_reports_folder_mount_write_access_and_free_space()
    {
        if (!CanRun) return;
        var root = Directory.CreateTempSubdirectory("dr-site-").FullName;
        try
        {
            var checks = WorkerFolderStorage.ParseTest((await BashAsync(WorkerFolderStorage.TestCommand(root))).Out, root);
            checks.Single(c => c.Name == "Folder").State.Should().Be(CheckState.Passed);
            checks.Single(c => c.Name == "Write, read, delete").State.Should().Be(CheckState.Passed);
            checks.Should().Contain(c => c.Name == "Free space");
            checks.Single(c => c.Name == "Mounted").State.Should().Be(CheckState.Warning, "a plain temp folder is not a mount point");
            Directory.GetFiles(root).Should().BeEmpty("the probe file is removed");

            var missing = WorkerFolderStorage.ParseTest((await BashAsync(WorkerFolderStorage.TestCommand(root + "-missing"))).Out, root + "-missing");
            missing.Should().ContainSingle().Which.State.Should().Be(CheckState.Failed);
        }
        finally { Directory.Delete(root, true); }
    }
}
