using Backora.DesignSystem;
using Backora.Web.Api;
using Backora.Web.Auth;
using Backora.Web.Components;
using Backora.Web.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<BackendOptions>(builder.Configuration.GetSection(BackendOptions.Section));
builder.Services.Configure<FrontendOptions>(builder.Configuration.GetSection(FrontendOptions.Section));
var frontendOptions = builder.Configuration.GetSection(FrontendOptions.Section).Get<FrontendOptions>() ?? new FrontendOptions();
var allowInsecureCookies = builder.Configuration.GetValue("Frontend:AllowInsecureCookies", builder.Environment.IsDevelopment());

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddRazorComponents().AddInteractiveServerComponents(o =>
{
    o.DetailedErrors = builder.Environment.IsDevelopment();
    o.DisconnectedCircuitRetentionPeriod = TimeSpan.FromMinutes(3);
});
builder.Services.AddHttpContextAccessor();
builder.Services.AddCascadingAuthenticationState();

// Data protection keys protect the auth/antiforgery cookies; persist them so restarts and replicas agree.
var dp = builder.Services.AddDataProtection().SetApplicationName("Backora.Web");
var keysPath = builder.Configuration["DataProtection:KeysPath"];
if (!string.IsNullOrWhiteSpace(keysPath))
{
    Directory.CreateDirectory(keysPath);
    dp.PersistKeysToFileSystem(new DirectoryInfo(keysPath));
    // The key ring is encrypted at rest with a key held outside the volume (a Secret), when one is configured.
    if (AesGcmXmlEncryptor.ParseKey(builder.Configuration["DataProtection:KeyEncryptionKey"]) is { } kek)
    {
        builder.Services.Configure<Microsoft.AspNetCore.DataProtection.KeyManagement.KeyManagementOptions>(o => o.XmlEncryptor = new AesGcmXmlEncryptor(kek));
    }
}

// Sessions survive restarts and redeploys when a sessions folder (on a persistent volume) is configured.
var sessionsPath = builder.Configuration["Frontend:SessionsPath"];
if (!string.IsNullOrWhiteSpace(sessionsPath))
{
    builder.Services.AddSingleton<ISessionPersistence>(sp => new FileSessionPersistence(sessionsPath,
        sp.GetRequiredService<Microsoft.AspNetCore.DataProtection.IDataProtectionProvider>(), sp.GetRequiredService<ILogger<FileSessionPersistence>>()));
}
else
{
    builder.Services.AddSingleton<ISessionPersistence, NoSessionPersistence>();
}

// ---- BFF authentication: cookie holds only a session key; tokens stay server-side ----
builder.Services.AddSingleton<SessionStore>();
builder.Services.AddSingleton<MemoryTicketStore>();
builder.Services.AddSingleton<IPostConfigureOptions<CookieAuthenticationOptions>, PostConfigureTicketStore>();
builder.Services.AddHostedService<SessionCleanupService>();
builder.Services.AddAuthentication(BffClaims.Scheme)
    .AddCookie(BffClaims.Scheme, o =>
    {
        o.Cookie.Name = allowInsecureCookies ? "backora.session" : BffClaims.CookieName;
        o.Cookie.HttpOnly = true;
        o.Cookie.SameSite = SameSiteMode.Strict;
        o.Cookie.SecurePolicy = allowInsecureCookies ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
        o.Cookie.Path = "/";
        o.SlidingExpiration = true;
        o.ExpireTimeSpan = TimeSpan.FromMinutes(Math.Max(5, frontendOptions.SessionIdleMinutes));
        o.LoginPath = "/login";
        o.LogoutPath = "/account/logout";
        o.AccessDeniedPath = "/forbidden";
        o.ReturnUrlParameter = "returnUrl";
        o.Events.OnValidatePrincipal = async ctx =>
        {
            var store = ctx.HttpContext.RequestServices.GetRequiredService<SessionStore>();
            var sid = ctx.Principal?.FindFirst(BffClaims.SessionId)?.Value;
            if (store.Get(sid) is null)
            {
                ctx.RejectPrincipal();
                await ctx.HttpContext.SignOutAsync(BffClaims.Scheme);
            }
        };
    });
builder.Services.AddAuthorization();
builder.Services.AddAntiforgery(o =>
{
    o.Cookie.Name = allowInsecureCookies ? "backora.af" : "__Host-backora-af";
    o.Cookie.SameSite = SameSiteMode.Strict;
    o.Cookie.SecurePolicy = allowInsecureCookies ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
});

// ---- Backend API clients ----
builder.Services.AddHttpClient(BffClaims.BackendAnonymousClient, (sp, c) =>
{
    var o = sp.GetRequiredService<IOptions<BackendOptions>>().Value;
    c.BaseAddress = new Uri(o.BaseUrl.TrimEnd('/') + "/");
    c.Timeout = TimeSpan.FromSeconds(o.TimeoutSeconds);
}).AddHttpMessageHandler<ForwardClientIpHandler>();
builder.Services.AddTransient<ForwardClientIpHandler>();
builder.Services.AddSingleton<BackendTransport>();
builder.Services.AddSingleton<ITokenRefresher, TokenRefresher>();
builder.Services.AddScoped<SessionAccessor>();
builder.Services.AddScoped<BackendHttp>();
builder.Services.AddScoped<ApiClient>();
builder.Services.AddScoped<AuthService>();
builder.Services.AddScoped<UserContext>();
builder.Services.AddScoped<MeClient>();
builder.Services.AddScoped<UsersClient>();
builder.Services.AddScoped<ServersClient>();
builder.Services.AddScoped<SettingsClient>();
builder.Services.AddScoped<VariablesClient>();
builder.Services.AddScoped<TemplatesClient>();
builder.Services.AddScoped<StorageClient>();
builder.Services.AddScoped<BackupJobsClient>();
builder.Services.AddScoped<RunsClient>();
builder.Services.AddScoped<OffloadsClient>();
builder.Services.AddScoped<RecoveryPointsClient>();
builder.Services.AddScoped<RestoresClient>();
builder.Services.AddScoped<DashboardClient>();
builder.Services.AddScoped<AuditClient>();
builder.Services.AddScoped<SearchClient>();
builder.Services.AddScoped<SystemClient>();
builder.Services.AddScoped<BackoraApi>();

// ---- UI services ----
builder.Services.AddScoped<BackoraJs>();
builder.Services.AddScoped<ToastService>();
builder.Services.AddScoped<ClientClock>();
builder.Services.AddMemoryCache();
builder.Services.AddSingleton<PreviewStore>();

builder.Services.Configure<ForwardedHeadersOptions>(o =>
{
    o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    // Trust proxies configured by the platform (Kubernetes/OpenShift router); see docs.
    o.KnownIPNetworks.Clear();
    o.KnownProxies.Clear();
});

var app = builder.Build();

app.UseForwardedHeaders();
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/error", createScopeForErrors: true);
    app.UseHsts();
}
app.UseBackoraSecurityHeaders(app.Environment.IsDevelopment());
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);

app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();

app.MapStaticAssets();

app.MapGet("/health", () => Results.Ok(new { status = "ok" })).AllowAnonymous().DisableAntiforgery();
app.MapGet("/health/ready", async (IHttpClientFactory factory, CancellationToken ct) =>
{
    try
    {
        var client = factory.CreateClient(BffClaims.BackendAnonymousClient);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(3));
        using var response = await client.GetAsync("/health/live", cts.Token);
        return response.IsSuccessStatusCode
            ? Results.Ok(new { status = "ready" })
            : Results.Json(new { status = "backend-unhealthy" }, statusCode: 503);
    }
    catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
    {
        return Results.Json(new { status = "backend-unreachable" }, statusCode: 503);
    }
}).AllowAnonymous();

app.MapPost("/account/logout", async (HttpContext ctx, SessionStore store, AuthService auth, Microsoft.AspNetCore.Antiforgery.IAntiforgery antiforgery) =>
{
    if (!await antiforgery.IsRequestValidAsync(ctx)) return Results.BadRequest();
    var sid = ctx.User.FindFirst(BffClaims.SessionId)?.Value;
    var session = store.Get(sid);
    if (session is not null) await auth.LogoutAsync(session, ctx.RequestAborted);
    await ctx.SignOutAsync(BffClaims.Scheme);
    store.Remove(sid);
    return Results.LocalRedirect("/login?signedOut=1");
});

// Sandboxed email template preview: no scripts, no network, framed only by Backora itself.
app.MapGet("/preview/email/{id}", (string id, HttpContext ctx, PreviewStore previews) =>
{
    var html = previews.Get(id, ctx.User.FindFirst(BffClaims.SessionId)?.Value);
    if (html is null) return Results.NotFound();
    ctx.Items[SecurityHeaders.CustomPolicyKey] = true;
    ctx.Response.Headers["Content-Security-Policy"] = "default-src 'none'; style-src 'unsafe-inline'; img-src data:; font-src data:; frame-ancestors 'self'; sandbox";
    ctx.Response.Headers["X-Frame-Options"] = "SAMEORIGIN";
    ctx.Response.Headers["Cache-Control"] = "no-store";
    return Results.Content(html, "text/html; charset=utf-8");
}).RequireAuthorization();

app.MapRazorComponents<App>().AddInteractiveServerRenderMode();

app.Run();

public partial class Program;
