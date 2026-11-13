using Backora.Application.Operations;
using FluentAssertions;
using Xunit;

namespace Backora.UnitTests;

/// <summary>After a failed or killed run Backora removes the outputs the script recorded — and nothing that isn't one.</summary>
public class PartialOutputTests
{
    [Theory]
    [InlineData("/BackoraDB/billing-db-2026-11-12_15", true)]
    [InlineData("/BackoraDB/billing-db-2026-11-12_15.tar", true)]
    [InlineData("/BackoraDB/billing-db-files-2026-11-12_15.tar.gpg", true)]
    [InlineData("/BackoraDB-billing-db/billing-db-2026-11-12_15.xbstream", true)]
    [InlineData("/BackoraDB", false)]                                   // the share itself
    [InlineData("/BackoraDB/", false)]
    [InlineData("/BackoraDB/OtherJob-2026-11-12_15.tar", false)]        // another job's backup
    [InlineData("/BackoraDB/billing-db2-2026-11-12_15.tar", false)]     // a job whose name only starts the same
    [InlineData("/BackoraDB/../etc/billing-db-x", false)]
    [InlineData("BackoraDB/billing-db-2026-11-12_15", false)]           // relative
    [InlineData("/billing-db-2026", false)]                             // directly under /
    public void Only_this_jobs_recorded_outputs_are_removed(string path, bool removable) =>
        BackupRunProcessor.IsOwnOutput(path, "billing-db").Should().Be(removable);

    [Fact]
    public void The_worker_compress_record_becomes_readable_log_lines()
    {
        var log = "COMPRESSED\tjob-2026-11-13_07\tjob-2026-11-13_07.tar.zst\t107374182400\t21474836480\t120\n"
                + "SKIPPED\tjob-2026-11-13_07-files\tthe folder also holds files older than this run\n"
                + "FAILED\tjob.sql\tcompression failed (read 0, zstd 1, write 0); the backup is kept uncompressed\n"
                + "SKIPPED\t*\tzstd is not installed on the worker\n"
                + "garbage line\n";
        var lines = BackupRunProcessor.CompressionLogLines(log).ToList();
        lines.Should().HaveCount(4);
        lines[0].Should().Be("Compressed job-2026-11-13_07 into job-2026-11-13_07.tar.zst on the worker: 100 GiB → 20.0 GiB (5.0× smaller) in 2m 0s (853 MiB/s); checksums taken of the compressed file, original removed");
        lines[1].Should().Be("job-2026-11-13_07-files left as it is: the folder also holds files older than this run");
        lines[2].Should().StartWith("Warning: job.sql could not be compressed");
        lines[3].Should().Be("Not compressed: zstd is not installed on the worker");
    }

    [Theory]
    [InlineData("none", true)]
    [InlineData(" Off ", true)]
    [InlineData("zstd", false)]
    [InlineData("", false)]
    public void Compression_is_turned_off_only_by_an_explicit_value(string value, bool off) =>
        BackupRunProcessor.IsOff(value).Should().Be(off);

    [Fact]
    public void The_template_output_record_names_only_this_jobs_entries()
    {
        var record = "/BackoraDB/billing-db-2026-11-13_10.xbstream\n/BackoraDB-billing-db/billing-db-2026-11-13_10\n/BackoraDB/Other-2026.tar\n../x\n\n/BackoraDB/billing-db-2026-11-13_10.xbstream\n";
        BackupRunProcessor.OutputNames(record, "billing-db").Should().Equal("billing-db-2026-11-13_10.xbstream", "billing-db-2026-11-13_10");
    }
}
