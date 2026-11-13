using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using Backora.Contracts.V1.Auth;
using Backora.DesignSystem;
using Backora.Web.Api;
using Backora.Web.Auth;
using Backora.Web.Services;
using Bunit;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Backora.Web.Tests;

/// <summary>Programmable fake for the Backora API used by component and client tests.</summary>
public sealed class FakeBackend : HttpMessageHandler
{
    private readonly List<(HttpMethod Method, string Path, Func<HttpRequestMessage, Task<HttpResponseMessage>> Handler)> _routes = [];
    public ConcurrentQueue<(HttpRequestMessage Request, string? Body)> Requests { get; } = new();

    public FakeBackend On(HttpMethod method, string pathPrefix, Func<HttpRequestMessage, Task<HttpResponseMessage>> handler)
    {
        _routes.Insert(0, (method, pathPrefix, handler));
        return this;
    }

    public FakeBackend OnJson(HttpMethod method, string pathPrefix, object body, HttpStatusCode status = HttpStatusCode.OK) =>
        On(method, pathPrefix, _ => Task.FromResult(Json(body, status)));

    public FakeBackend OnProblem(HttpMethod method, string pathPrefix, HttpStatusCode status, object problem) =>
        On(method, pathPrefix, _ => Task.FromResult(Problem(status, problem)));

    public static HttpResponseMessage Json(object body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(JsonSerializer.Serialize(body, ApiJson.Options), Encoding.UTF8, "application/json") };

    public static HttpResponseMessage Problem(HttpStatusCode status, object problem) =>
        new(status) { Content = new StringContent(JsonSerializer.Serialize(problem), Encoding.UTF8, "application/problem+json") };

    public IEnumerable<HttpRequestMessage> Calls(HttpMethod method, string pathPrefix) =>
        Requests.Select(r => r.Request).Where(r => r.Method == method && r.RequestUri!.AbsolutePath.StartsWith(pathPrefix, StringComparison.Ordinal));

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        Requests.Enqueue((request, body));
        var path = request.RequestUri!.AbsolutePath;
        // Exact path wins; otherwise the longest matching prefix (most recent registration breaks ties).
        var match = _routes.Where(r => r.Method == request.Method && path == r.Path).Select(r => r.Handler).FirstOrDefault()
            ?? _routes.Where(r => r.Method == request.Method && path.StartsWith(r.Path, StringComparison.Ordinal))
                      .OrderByDescending(r => r.Path.Length).Select(r => r.Handler).FirstOrDefault();
        if (match is not null) return await match(request);
        return new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("{\"title\":\"Not found\"}") };
    }
}

/// <summary>UserContext with a fixed set of permissions (no session store involved).</summary>
public sealed class FakeUserContext : UserContext
{
    private readonly HashSet<string> _perms;

    public FakeUserContext(params string[] permissions)
        : base(new SessionAccessor(null, new HttpContextAccessor()), new SessionStore(TimeProvider.System), new ServiceCollection().BuildServiceProvider())
    {
        _perms = new HashSet<string>(permissions, StringComparer.Ordinal);
        Set(new CurrentUserDto(1, "op@example.com", "Test Operator", ["BackupOperator"], permissions, false, false, null));
    }

    public override Task<CurrentUserDto?> EnsureLoadedAsync() => Task.FromResult(User);
    public override bool Can(string permission) => _perms.Contains(permission);
}

public static class TestSetup
{
    /// <summary>Registers API clients wired to the fake backend plus UI services required by pages.</summary>
    public static FakeBackend AddBackora(this BunitContext ctx, params string[] permissions)
    {
        var backend = new FakeBackend();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddLogging();
        ctx.Services.AddSingleton(new BackendHttp(backend, new BackendOptions { BaseUrl = "http://api.test" }));
        ctx.Services.AddScoped<ApiClient>();
        ctx.Services.AddScoped<MeClient>();
        ctx.Services.AddScoped<UsersClient>();
        ctx.Services.AddScoped<ServersClient>();
        ctx.Services.AddScoped<SettingsClient>();
        ctx.Services.AddScoped<VariablesClient>();
        ctx.Services.AddScoped<TemplatesClient>();
        ctx.Services.AddScoped<StorageClient>();
        ctx.Services.AddScoped<BackupJobsClient>();
        ctx.Services.AddScoped<RunsClient>();
        ctx.Services.AddScoped<OffloadsClient>();
        ctx.Services.AddScoped<RecoveryPointsClient>();
        ctx.Services.AddScoped<RestoresClient>();
        ctx.Services.AddScoped<DashboardClient>();
        ctx.Services.AddScoped<AuditClient>();
        ctx.Services.AddScoped<SearchClient>();
        ctx.Services.AddScoped<SystemClient>();
        ctx.Services.AddScoped<BackoraApi>();
        ctx.Services.AddScoped<BackoraJs>();
        ctx.Services.AddScoped<ToastService>();
        ctx.Services.AddScoped<ClientClock>();
        ctx.Services.AddSingleton<UserContext>(new FakeUserContext(permissions));
        return backend;
    }
}
