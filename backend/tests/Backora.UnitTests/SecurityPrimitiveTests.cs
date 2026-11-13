using System.Security.Cryptography;
using Backora.Application.Execution;
using Backora.Application.Notifications;
using Backora.Application.Validation;
using Backora.Infrastructure.Identity;
using Backora.Infrastructure.Security;
using Backora.Domain.Notifications;
using FluentAssertions;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace Backora.UnitTests;

public class AesGcmSecretProtectorTests
{
    private static AesGcmSecretProtector Create(string active, params (string Id, string Key)[] keys) =>
        new(Options.Create(new EncryptionOptions { ActiveKeyId = active, Keys = keys.ToDictionary(k => k.Id, k => k.Key) }));

    private static readonly string K1 = AesGcmSecretProtector.GenerateKey();
    private static readonly string K2 = AesGcmSecretProtector.GenerateKey();

    [Fact]
    public void Round_trips_and_uses_random_nonces()
    {
        var p = Create("k1", ("k1", K1));
        var a = p.Protect("-----BEGIN OPENSSH PRIVATE KEY----- secret");
        var b = p.Protect("-----BEGIN OPENSSH PRIVATE KEY----- secret");
        a.Should().StartWith("bk1:k1:").And.NotBe(b);
        p.Unprotect(a).Should().Be("-----BEGIN OPENSSH PRIVATE KEY----- secret");
        a.Should().NotContain("secret");
    }

    [Fact]
    public void Detects_tampering()
    {
        var p = Create("k1", ("k1", K1));
        var c = p.Protect("value");
        var bytes = Convert.FromBase64String(c.Split(':')[2]);
        bytes[^1] ^= 0x01;
        var tampered = $"bk1:k1:{Convert.ToBase64String(bytes)}";
        var act = () => p.Unprotect(tampered);
        act.Should().Throw<CryptographicException>();
    }

    [Fact]
    public void Key_id_is_bound_as_associated_data()
    {
        var p = Create("k1", ("k1", K1), ("k2", K1)); // same key material, different id
        var c = p.Protect("value");
        var relabelled = c.Replace("bk1:k1:", "bk1:k2:", StringComparison.Ordinal);
        var act = () => p.Unprotect(relabelled);
        act.Should().Throw<CryptographicException>();
    }

    [Fact]
    public void Supports_rotation()
    {
        var old = Create("k1", ("k1", K1));
        var c = old.Protect("value");
        var rotated = Create("k2", ("k1", K1), ("k2", K2));
        rotated.NeedsReEncryption(c).Should().BeTrue();
        rotated.Unprotect(c).Should().Be("value");
        rotated.NeedsReEncryption(rotated.Protect("value")).Should().BeFalse();
    }

    [Fact]
    public void Rejects_missing_or_weak_keys()
    {
        var act1 = () => Create("k1");
        act1.Should().Throw<InvalidOperationException>();
        var act2 = () => Create("k1", ("k1", Convert.ToBase64String(new byte[16])));
        act2.Should().Throw<InvalidOperationException>().WithMessage("*32 bytes*");
    }

    [Fact]
    public void Unknown_key_id_fails_closed()
    {
        var c = Create("k1", ("k1", K1)).Protect("v");
        var act = () => Create("k2", ("k2", K2)).Unprotect(c);
        act.Should().Throw<CryptographicException>();
    }
}

public class CompatPasswordHasherTests
{
    private readonly CompatPasswordHasher _hasher = new(Options.Create(new PasswordHasherOptions { IterationCount = 10_000 }));

    [Fact]
    public void Verifies_legacy_laravel_bcrypt_and_requests_rehash()
    {
        var legacy = BCrypt.Net.BCrypt.HashPassword("Correct-Horse-1", 10).Replace("$2a$", "$2y$", StringComparison.Ordinal);
        legacy.Should().StartWith("$2y$");
        _hasher.VerifyHashedPassword(new ApplicationUser(), legacy, "Correct-Horse-1").Should().Be(PasswordVerificationResult.SuccessRehashNeeded);
        _hasher.VerifyHashedPassword(new ApplicationUser(), legacy, "wrong").Should().Be(PasswordVerificationResult.Failed);
    }

    [Fact]
    public void New_hashes_are_identity_v3_and_do_not_need_rehash()
    {
        var hash = _hasher.HashPassword(new ApplicationUser(), "Correct-Horse-1");
        CompatPasswordHasher.IsBcrypt(hash).Should().BeFalse();
        _hasher.VerifyHashedPassword(new ApplicationUser(), hash, "Correct-Horse-1").Should().Be(PasswordVerificationResult.Success);
    }

    [Fact]
    public void Malformed_bcrypt_does_not_throw()
    {
        _hasher.VerifyHashedPassword(new ApplicationUser(), "$2y$10$" + new string('!', 53), "x").Should().Be(PasswordVerificationResult.Failed);
    }
}

public class NotificationRenderingTests
{
    [Fact]
    public void Email_values_are_html_encoded()
    {
        var html = NotificationRenderer.RenderEmail("<p>{{error_message}}</p>", new Dictionary<string, string?> { ["error_message"] = "<script>alert(1)</script>" });
        html.Should().Be("<p>&lt;script&gt;alert(1)&lt;/script&gt;</p>");
    }

    [Fact]
    public void Teams_values_cannot_break_json()
    {
        var (json, error) = NotificationRenderer.RenderTeams("{\"text\":\"{{error_message}}\"}",
            new Dictionary<string, string?> { ["error_message"] = "line1\n\"}, \"injected\": true, \"x\":\"" });
        error.Should().BeNull();
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        doc.RootElement.EnumerateObject().Select(p => p.Name).Should().Equal("text");
    }

    [Fact]
    public void Invalid_teams_template_is_reported()
    {
        NotificationRenderer.RenderTeams("{ not json", new Dictionary<string, string?>()).Error.Should().NotBeNull();
    }

    [Fact]
    public void Default_templates_render_with_sample_values()
    {
        var values = DefaultTemplates.SampleValues("https://backora.example");
        NotificationRenderer.RenderEmail(null, values).Should().Contain("orders-db-nightly");
        NotificationRenderer.RenderTeams(null, values).Error.Should().BeNull();
    }

    [Fact]
    public void Unknown_placeholders_render_empty() =>
        PlaceholderTemplate.Render("[{{nope}}]", new Dictionary<string, string?>(), s => s).Should().Be("[]");
}

public class RcloneParsingTests
{
    [Fact]
    public void Parses_latest_json_stats()
    {
        var log = """
            {"level":"notice","msg":"stats","stats":{"bytes":100,"totalBytes":1000,"transfers":0,"totalTransfers":2,"errors":0}}
            {"level":"notice","msg":"stats","stats":{"bytes":1000,"totalBytes":1000,"transfers":2,"totalTransfers":2,"errors":0}}
            """;
        var s = Rclone.ParseLatestStats(log);
        s.Should().NotBeNull();
        s!.Bytes.Should().Be(1000);
        s.Transfers.Should().Be(2);
    }

    [Fact]
    public void Ignores_partial_lines() => Rclone.ParseLatestStats("{\"stats\":{\"bytes\":1").Should().BeNull();

    [Fact]
    public void Extracts_error_messages() =>
        Rclone.ExtractErrors("{\"level\":\"error\",\"msg\":\"Failed to copy: AccessDenied\"}\n").Should().Contain("AccessDenied");

    [Fact]
    public void Config_rejects_line_breaks_in_credentials()
    {
        var act = () => Rclone.BuildConfig(new Application.Abstractions.S3Connection("http://s3", "us-east-1", "b", "AK\ninjected = x", "sk", true));
        act.Should().Throw<Application.Abstractions.RemoteOperationException>();
    }

    [Fact]
    public void Replica_config_uses_two_named_remotes_and_server_side_detection_is_strict()
    {
        var a = new Application.Abstractions.S3Connection("https://s3.primary/", "us-east-1", "prod", "AK1", "SK1", true);
        var sameAccount = a with { Bucket = "dr" };
        var otherSite = a with { Endpoint = "https://s3.dr-site" };
        Rclone.SameAccount(a, sameAccount).Should().BeTrue();
        Rclone.SameAccount(a, a with { Endpoint = "https://s3.primary" }).Should().BeTrue("a trailing slash is not a different endpoint");
        Rclone.SameAccount(a, otherSite).Should().BeFalse();
        Rclone.SameAccount(a, a with { SecretKey = "SK2" }).Should().BeFalse();

        var config = Rclone.BuildConfig(a, Rclone.ReplicaSourceRemote) + Rclone.BuildConfig(otherSite, Rclone.ReplicaTargetRemote);
        config.Should().Contain("[backora-src]").And.Contain("[backora-dst]").And.Contain("endpoint = https://s3.dr-site");
        Rclone.RemotePath(otherSite, "/dr/job/x/", Rclone.ReplicaTargetRemote).Should().Be("backora-dst:prod/dr/job/x/");
    }
}

public class InputRuleTests
{
    [Theory]
    [InlineData("db01.example.com", true)]
    [InlineData("10.0.0.15", true)]
    [InlineData("fe80::1", true)]
    [InlineData("target", true)]
    [InlineData("host;id", false)]
    [InlineData("host name", false)]
    [InlineData("$(whoami)", false)]
    [InlineData("http://host", false)]
    [InlineData("", false)]
    public void Host_validation(string host, bool ok) => Rules.IsHost(host).Should().Be(ok);

    [Theory]
    [InlineData("root", true)]
    [InlineData("backup-user", true)]
    [InlineData("root;id", false)]
    [InlineData("a b", false)]
    public void Posix_user_validation(string user, bool ok) => Rules.IsPosixUser(user).Should().Be(ok);

    [Theory]
    [InlineData("https://s3.example.com", true)]
    [InlineData("http://minio:9000", true)]
    [InlineData("https://user:pw@s3.example.com", false)]
    [InlineData("ftp://x", false)]
    public void Endpoint_validation(string url, bool ok) => Rules.IsHttpUrl(url).Should().Be(ok);

    [Theory]
    [InlineData("http://169.254.169.254/latest", true)]
    [InlineData("http://metadata.google.internal", true)]
    [InlineData("https://s3.amazonaws.com", false)]
    public void Ssrf_blocklist(string endpoint, bool blocked) => Application.Inventory.StorageService.IsBlockedEndpoint(endpoint).Should().Be(blocked);
}
