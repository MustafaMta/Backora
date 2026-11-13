using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Backora.Contracts.V1;
using Backora.Contracts.V1.Auth;
using Backora.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Backora.IntegrationTests.Infrastructure;

/// <summary>One disposable PostgreSQL 18 container per test assembly collection. Each fixture user gets its own database.</summary>
public sealed class PostgresContainer : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:18-alpine")
        .WithUsername("backora_owner")
        .WithPassword("it_" + Convert.ToHexString(RandomNumberGenerator.GetBytes(8)))
        .WithDatabase("postgres")
        .Build();

    public async Task InitializeAsync() => await _container.StartAsync();

    public async Task DisposeAsync() => await _container.DisposeAsync();

    /// <summary>Creates a fresh, empty database and returns its connection string.</summary>
    public async Task<string> CreateDatabaseAsync(string prefix)
    {
        var name = $"{prefix}_{Guid.NewGuid():N}".ToLowerInvariant();
        await using var conn = new NpgsqlConnection(_container.GetConnectionString());
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand($"CREATE DATABASE \"{name}\"", conn);
        await cmd.ExecuteNonQueryAsync();
        return new NpgsqlConnectionStringBuilder(_container.GetConnectionString()) { Database = name, IncludeErrorDetail = true }.ConnectionString;
    }
}

[CollectionDefinition(Name)]
public sealed class PostgresCollection : ICollectionFixture<PostgresContainer>
{
    public const string Name = "postgres";
}

public static class TestSecrets
{
    public const string AdminEmail = "admin@backora.test";
    public const string BootstrapPassword = "Bootstrap-Pass-2026";
    public const string AdminPassword = "Admin-Changed-Pass-2026";

    public static Dictionary<string, string?> Configuration(string connectionString) => new()
    {
        ["ConnectionStrings:Backora"] = connectionString,
        ["Encryption:ActiveKeyId"] = "test1",
        ["Encryption:Keys:test1"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
        ["Jwt:SigningKey"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(48)),
        ["Bootstrap:AdminEmail"] = AdminEmail,
        ["Bootstrap:AdminPassword"] = BootstrapPassword,
        ["Database:MigrateOnStartup"] = "true",
        ["Platform:PublicUrl"] = "http://frontend.test",
        ["RateLimits:LoginPerMinute"] = "1000",
        ["RateLimits:SensitivePerMinute"] = "1000",
        ["RateLimits:GlobalPerMinute"] = "100000",
    };
}

/// <summary>Hosts the real API pipeline in-process against a real PostgreSQL database.</summary>
public sealed class BackoraApiFactory(string connectionString, Dictionary<string, string?>? overrides = null, Action<IWebHostBuilder>? configure = null) : WebApplicationFactory<Program>
{
    // Background execution is hosted separately in E2E tests (BackgroundExecutionHost) so it can be restarted on its own.
    public Dictionary<string, string?> Settings { get; } = Merge(Merge(TestSecrets.Configuration(connectionString), new() { ["Execution:Enabled"] = "false" }), overrides);

    private static Dictionary<string, string?> Merge(Dictionary<string, string?> a, Dictionary<string, string?>? b)
    {
        if (b is not null) foreach (var (k, v) in b) a[k] = v;
        return a;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        // Read at the top of Program.cs before app-configuration overrides apply, so it must be a host setting.
        builder.UseSetting("Execution:Enabled", Settings.TryGetValue("Execution:Enabled", out var exec) ? exec : "false");
        builder.ConfigureAppConfiguration((_, cfg) => cfg.AddInMemoryCollection(Settings));
        configure?.Invoke(builder);
    }

    public BackoraDbContext CreateDbContext() => Services.CreateScope().ServiceProvider.GetRequiredService<BackoraDbContext>();

    public async Task<T> WithDbAsync<T>(Func<BackoraDbContext, Task<T>> action)
    {
        await using var scope = Services.CreateAsyncScope();
        return await action(scope.ServiceProvider.GetRequiredService<BackoraDbContext>());
    }

    public async Task WithDbAsync(Func<BackoraDbContext, Task> action)
    {
        await using var scope = Services.CreateAsyncScope();
        await action(scope.ServiceProvider.GetRequiredService<BackoraDbContext>());
    }

    /// <summary>Logs in as the bootstrap admin, completing the mandatory password change once.</summary>
    public async Task<HttpClient> AdminClientAsync()
    {
        var client = CreateClient();
        var token = await TryLoginAsync(client, TestSecrets.AdminEmail, TestSecrets.AdminPassword);
        if (token is null)
        {
            var bootstrap = await TryLoginAsync(client, TestSecrets.AdminEmail, TestSecrets.BootstrapPassword)
                ?? throw new InvalidOperationException("Bootstrap admin login failed.");
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", bootstrap);
            var change = await client.PostAsJsonAsync("/api/v1/me/password", new ChangePasswordRequest(TestSecrets.BootstrapPassword, TestSecrets.AdminPassword));
            change.EnsureSuccessStatusCode();
            token = await TryLoginAsync(client, TestSecrets.AdminEmail, TestSecrets.AdminPassword);
        }
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    /// <summary>Creates a user with the given roles (via the admin API) and returns an authenticated client.</summary>
    public async Task<HttpClient> ClientForRoleAsync(HttpClient admin, params string[] roles)
    {
        var email = $"{string.Join("-", roles).ToLowerInvariant()}-{Guid.NewGuid():N}"[..Math.Min(40, string.Join("-", roles).Length + 33)] + "@backora.test";
        const string initial = "Initial-Pass-2026x";
        const string final = "Final-Pass-2026xyz";
        var create = await admin.PostAsJsonAsync("/api/v1/users", new CreateUserRequest(email, "Test " + string.Join(",", roles), initial, roles));
        create.EnsureSuccessStatusCode();
        var client = CreateClient();
        var token = await TryLoginAsync(client, email, initial) ?? throw new InvalidOperationException("login failed");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        (await client.PostAsJsonAsync("/api/v1/me/password", new ChangePasswordRequest(initial, final))).EnsureSuccessStatusCode();
        token = await TryLoginAsync(client, email, final) ?? throw new InvalidOperationException("login failed");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    public static async Task<string?> TryLoginAsync(HttpClient client, string email, string password)
    {
        var response = await client.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest(email, password));
        if (!response.IsSuccessStatusCode) return null;
        var body = await response.Content.ReadFromJsonAsync<LoginResponse>(Json.Options);
        return body?.Tokens?.AccessToken;
    }
}

public static class Json
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    public static async Task<T> ReadAsync<T>(this HttpResponseMessage response)
    {
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"{(int)response.StatusCode} {response.ReasonPhrase}: {await response.Content.ReadAsStringAsync()}");
        }
        return (await response.Content.ReadFromJsonAsync<T>(Options))!;
    }
}

public static class Requests
{
    public static ServerUpsertRequest Server(string name, string host = "db01.example.internal", string? key = null, uint? version = null) =>
        new(name, host, 22, "root", key, false, null, version);

    public static TemplateUpsertRequest Template(string name, string script = "#!/bin/bash\necho {JOB_NAME}\n", string category = "PostgreSQL") =>
        new(name, category, script, null, null);

    public static BackupJobUpsertRequest Job(string name, long templateId, long serverId, string? cron = null, long? destination = null,
        JobStatusDto status = JobStatusDto.Active, decimal retention = 7, uint? version = null, int timeout = 60, bool keepLocal = false, IReadOnlyList<long>? replicas = null) =>
        new(name, null, templateId, serverId, status, cron, "UTC", MissedRunPolicyDto.RunOnceWithinGrace, 60, retention, 1, destination, keepLocal, true, timeout, 3, version, replicas);
}
