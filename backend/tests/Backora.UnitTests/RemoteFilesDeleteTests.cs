using System.Diagnostics;
using Backora.Application.Abstractions;
using Backora.Application.Execution;
using FluentAssertions;
using Xunit;

namespace Backora.UnitTests;

/// <summary>
/// After an upload the backup files are deleted from NFS. A backup kept as a folder (pg_basebackup base.tar +
/// pg_wal.tar) must not leave its empty folder behind, and nothing that is still in use may be removed.
/// </summary>
public class RemoteFilesDeleteTests
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
        public Task WriteFileAsync(string path, string content, short mode, CancellationToken ct) => throw new NotSupportedException();
        public Task<CommandResult> RunWithInputAsync(string command, ReadOnlyMemory<byte> stdin, TimeSpan timeout, CancellationToken ct) => throw new NotSupportedException();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    [Fact]
    public async Task Deletes_the_files_and_only_the_folders_left_empty()
    {
        if (OperatingSystem.IsWindows()) return;
        var root = Directory.CreateTempSubdirectory("bko-del-").FullName;
        try
        {
            void Touch(string rel) { var f = Path.Combine(root, rel); Directory.CreateDirectory(Path.GetDirectoryName(f)!); File.WriteAllText(f, "x"); }
            Touch("job-1.sql"); Touch("job-2/base.tar"); Touch("job-2/pg_wal.tar"); Touch("job-3/base.tar"); Touch("job-3/newer.tar");
            Touch("deep/a/b/file.tar");

            var r = await RemoteFiles.DeleteFilesAsync(new LocalBash(), root, ["job-1.sql", "job-2/base.tar", "job-2/pg_wal.tar", "job-3/base.tar", "deep/a/b/file.tar"], CancellationToken.None);

            r.ExitCode.Should().Be(0, r.StdErr);
            File.Exists(Path.Combine(root, "job-1.sql")).Should().BeFalse();
            Directory.Exists(Path.Combine(root, "job-2")).Should().BeFalse("its files were all uploaded and deleted");
            File.Exists(Path.Combine(root, "job-3/newer.tar")).Should().BeTrue("a folder with anything left in it stays");
            Directory.Exists(Path.Combine(root, "deep")).Should().BeFalse("emptied parents are removed deepest first");
            Directory.Exists(root).Should().BeTrue("the job folder itself is never removed");
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task A_missing_directory_still_reports_failure()
    {
        if (OperatingSystem.IsWindows()) return;
        var r = await RemoteFiles.DeleteFilesAsync(new LocalBash(), "/nonexistent/backora-x", ["a/b.tar"], CancellationToken.None);
        r.ExitCode.Should().Be(3);
    }
}
