using Backora.Contracts.Security;
using Backora.Domain;
using Backora.Infrastructure.Identity;
using Backora.Tools.Legacy;
using FluentAssertions;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace Backora.IntegrationTests.LegacyImport;

public sealed class LegacyMappingTests
{
    [Theory]
    [InlineData("1.234 GiB", 1324997411L)]
    [InlineData("500 MiB", 524288000L)]
    [InlineData("12 KB", 12000L)]
    [InlineData("100 B", 100L)]
    [InlineData("55M", 57671680L)]
    [InlineData("5.6G", 6012954214L)]
    [InlineData("0", 0L)]
    [InlineData("2 TiB", 2199023255552L)]
    [InlineData("3 GB", 3000000000L)]
    public void ParseSize_handles_rclone_and_du_formats(string text, long expected) =>
        LegacyMapping.ParseSize(text).Should().Be(expected);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Unknown")]
    [InlineData("12 parsecs")]
    public void ParseSize_returns_null_for_unparseable(string? text) => LegacyMapping.ParseSize(text).Should().BeNull();

    [Theory]
    [InlineData("completed", RunStatus.Completed, FailureClass.None, false)]
    [InlineData("failed", RunStatus.Failed, FailureClass.ScriptError, false)]
    [InlineData("cancelled", RunStatus.Cancelled, FailureClass.Cancelled, false)]
    [InlineData("running", RunStatus.Failed, FailureClass.ProcessLost, true)]
    [InlineData("pending", RunStatus.Failed, FailureClass.ProcessLost, true)]
    [InlineData(null, RunStatus.Failed, FailureClass.ProcessLost, true)]
    public void Run_status_mapping_never_imports_active_runs(string? legacy, RunStatus status, FailureClass failure, bool hasMessage)
    {
        var m = LegacyMapping.MapRunStatus(legacy);
        m.Status.Should().Be(status);
        m.FailureClass.Should().Be(failure);
        (m.Message is not null).Should().Be(hasMessage);
        BackupRun_ActiveStatuses().Should().NotContain(m.Status);
    }

    [Theory]
    [InlineData("completed", OffloadStatus.Completed)]
    [InlineData("failed", OffloadStatus.Failed)]
    [InlineData("running", OffloadStatus.Failed)]
    [InlineData("pending", OffloadStatus.Failed)]
    public void Offload_status_mapping(string legacy, OffloadStatus expected) => LegacyMapping.MapOffloadStatus(legacy).Status.Should().Be(expected);

    [Theory]
    [InlineData("completed", RestoreStatus.Completed)]
    [InlineData("running", RestoreStatus.Failed)]
    [InlineData("pending", RestoreStatus.Failed)]
    public void Restore_status_mapping(string legacy, RestoreStatus expected) => LegacyMapping.MapRestoreStatus(legacy).Status.Should().Be(expected);

    [Theory]
    [InlineData("super admin", Roles.PlatformAdministrator, false)]
    [InlineData("Super Admin", Roles.PlatformAdministrator, false)]
    [InlineData("admin", Roles.BackupAdministrator, false)]
    [InlineData("viewer", Roles.Viewer, false)]
    [InlineData("Employee", Roles.Viewer, true)]
    [InlineData(null, Roles.Viewer, true)]
    public void Role_mapping_defaults_to_least_privilege(string? legacy, string role, bool defaulted)
    {
        var (r, d) = LegacyMapping.MapRole(legacy);
        r.Should().Be(role);
        d.Should().Be(defaulted);
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("Active", true)]
    [InlineData("Inactive", false)]
    [InlineData("Disable", false)]
    [InlineData("disabled", false)]
    public void User_status_mapping(string? status, bool active) => LegacyMapping.IsActiveUserStatus(status).Should().Be(active);

    [Theory]
    [InlineData("mysql_password", true)]
    [InlineData("API_TOKEN", true)]
    [InlineData("s3_secret", true)]
    [InlineData("ssh_key_path", true)]
    [InlineData("db_pwd", true)]
    [InlineData("mysql_user", false)]
    [InlineData("backup_dir", false)]
    public void Secret_variable_heuristic(string name, bool secret) => LegacyMapping.IsSecretVariableName(name).Should().Be(secret);

    [Fact]
    public void Cron_validation_falls_back_to_manual_for_invalid_expressions()
    {
        LegacyMapping.MapCron("0  2 * * *").Should().Be(new LegacyMapping.CronMapping("0 2 * * *", true, null));
        LegacyMapping.MapCron(null).Should().Be(new LegacyMapping.CronMapping(null, true, null));
        LegacyMapping.MapCron("").Cron.Should().BeNull();
        var invalid = LegacyMapping.MapCron("61 * * * *");
        invalid.Valid.Should().BeFalse();
        invalid.Cron.Should().BeNull();
        LegacyMapping.MapCron("0 2 * *").Valid.Should().BeFalse();
    }

    [Fact]
    public void Unsafe_names_invalid_cron_and_missing_templates_force_inactive()
    {
        LegacyMapping.MapJobStatus("active", "orders-db", true, true).Status.Should().Be(JobStatus.Active);
        LegacyMapping.MapJobStatus("inactive", "orders-db", true, true).Status.Should().Be(JobStatus.Inactive);

        var unsafeName = LegacyMapping.MapJobStatus("active", "bad name!", true, true);
        unsafeName.Status.Should().Be(JobStatus.Inactive);
        unsafeName.Reasons.Should().ContainSingle(r => r.StartsWith("rename required", StringComparison.Ordinal));

        LegacyMapping.MapJobStatus("active", "../etc", true, true).Status.Should().Be(JobStatus.Inactive);
        LegacyMapping.MapJobStatus("active", "orders-db", false, true).Status.Should().Be(JobStatus.Inactive);
        LegacyMapping.MapJobStatus("active", "orders-db", true, false).Status.Should().Be(JobStatus.Inactive);
    }

    [Fact]
    public void Legacy_prefix_uses_hour_granular_upload_start()
    {
        LegacyMapping.LegacyPrefix("orders-db", new DateTimeOffset(2025, 8, 1, 4, 59, 0, TimeSpan.Zero)).Should().Be("orders-db/2025-08-01-04/");
        LegacyMapping.LegacyPrefix("orders-db", new DateTimeOffset(2025, 8, 1, 6, 10, 0, TimeSpan.FromHours(2))).Should().Be("orders-db/2025-08-01-04/");
    }

    [Theory]
    [InlineData(465, true, "SslOnConnect")]
    [InlineData(25, false, "None")]
    [InlineData(25, true, "StartTls")]
    [InlineData(587, true, "StartTls")]
    public void Smtp_security_mode(int port, bool creds, string expected) => LegacyMapping.SmtpSecurity(port, creds).Should().Be(expected);

    [Fact]
    public void Recipients_and_log_extraction()
    {
        LegacyMapping.ParseRecipients("[\"a@x.test\",\"b@x.test\",\"A@x.test\"]").Should().Equal("a@x.test", "b@x.test");
        LegacyMapping.ParseRecipients("a@x.test, b@x.test").Should().Equal("a@x.test", "b@x.test");
        LegacyMapping.ParseRecipients(null).Should().BeEmpty();
        LegacyMapping.ExtractLog("{\"pid\":\"1\",\"final_log\":\"done\"}").Should().Be("done");
        LegacyMapping.ExtractLog("plain output").Should().BeNull();
        LegacyMapping.LastSegment("/BackoraDB/orders-db/").Should().Be("orders-db");
    }

    [Fact]
    public void Key_detection()
    {
        LegacyMapping.LooksLikePublicKey("ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIFAKE user@host").Should().BeTrue();
        LegacyMapping.LooksLikePublicKey("-----BEGIN OPENSSH PRIVATE KEY-----").Should().BeFalse();
        LegacyMapping.LooksLikePrivateKey("-----BEGIN RSA PRIVATE KEY-----\nX\n-----END RSA PRIVATE KEY-----").Should().BeTrue();
    }

    [Fact]
    public void Legacy_bcrypt_hashes_verify_and_request_rehash()
    {
        var hasher = new CompatPasswordHasher(Options.Create(new PasswordHasherOptions()));
        var legacyHash = BCrypt.Net.BCrypt.HashPassword("Test-Password-123", 10).Replace("$2a$", "$2y$", StringComparison.Ordinal);
        legacyHash.Should().StartWith("$2y$");
        var user = new ApplicationUser();

        hasher.VerifyHashedPassword(user, legacyHash, "Test-Password-123").Should().Be(PasswordVerificationResult.SuccessRehashNeeded);
        hasher.VerifyHashedPassword(user, legacyHash, "wrong-password").Should().Be(PasswordVerificationResult.Failed);

        // After rehash the modern PBKDF2 hash verifies without requesting another rehash.
        var upgraded = hasher.HashPassword(user, "Test-Password-123");
        upgraded.Should().NotStartWith("$2");
        hasher.VerifyHashedPassword(user, upgraded, "Test-Password-123").Should().Be(PasswordVerificationResult.Success);
    }

    private static RunStatus[] BackupRun_ActiveStatuses() => Domain.Backups.BackupRun.ActiveStatuses;
}
