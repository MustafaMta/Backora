using Backora.Application.Inventory;
using FluentAssertions;

namespace Backora.UnitTests;

public class ExecutionHostProbeTests
{
    [Fact]
    public void Parse_reads_host_facts_tools_disks_and_services()
    {
        var output = """
            hostname=exec-host-01
            os=Rocky Linux 9.5 (Blue Onyx)
            kernel=5.14.0-503.19.1.el9_5.x86_64
            cpus=2
            mem_kb=9856000
            mem_avail_kb=6600000
            uptime_s=38278920
            tool.pg_basebackup=pg_basebackup (PostgreSQL) 13.20
            tool.mariadb-backup=mariadb-backup based on MariaDB server 10.6.22-MariaDB Linux (x86_64)
            tool.age=
            svc.postgresql=active
            svc.mariadb=active
            svc.docker=inactive
            disk=/|xfs|601882624000|140660326400
            disk=/backoradb|nfs4|16492674416640|5057754316800
            garbage line without equals
            """;

        var p = ExecutionHostService.Parse(output, DateTimeOffset.UnixEpoch);

        p.Hostname.Should().Be("exec-host-01");
        p.Cpus.Should().Be(2);
        p.MemoryBytes.Should().Be(9856000L * 1024);
        p.Tools.Single(t => t.Name == "pg_basebackup").Version.Should().Contain("13.20");
        p.Tools.Single(t => t.Name == "age").Version.Should().BeNull("an empty value means the binary is not installed");
        p.Tools.Single(t => t.Name == "zstd").Version.Should().BeNull("tools missing from the output are reported as not installed");
        p.Disks.Should().HaveCount(2).And.Contain(d => d.Path == "/backoradb" && d.FileSystem == "nfs4");
        p.Services.Should().Contain(s => s.Name == "postgresql" && s.State == "active");
        p.Services.Should().NotContain(s => s.Name == "docker", "inactive optional services are not listed");
    }

    [Fact]
    public void Script_is_fixed_and_quotes_admin_paths()
    {
        var script = ExecutionHostService.BuildScript("/var/tmp/backora", "/backoradb");
        script.Should().Contain("'/backoradb'").And.Contain("'/var/tmp/backora'");
        script.Should().Contain("command -v pg_basebackup").And.Contain("command -v mariadb-backup");

        var unsafeScript = ExecutionHostService.BuildScript("/tmp/x; rm -rf /", "relative/path");
        unsafeScript.Should().NotContain("rm -rf").And.NotContain("relative/path");
    }
}
