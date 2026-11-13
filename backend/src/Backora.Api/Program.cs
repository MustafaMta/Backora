using System.Security.Claims;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using Backora.Api;
using Backora.Api.Endpoints;
using Backora.Api.Security;
using Backora.Application.Abstractions;
using Backora.Application.Telemetry;
using Backora.Application.Validation;
using Backora.Contracts.Security;
using Backora.Infrastructure;
using Backora.Infrastructure.Identity;
using Backora.Infrastructure.Persistence;
using FluentValidation;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Npgsql;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Scalar.AspNetCore;

var builder = WebApplication.CreateBuilder(args);

builder.Logging.ClearProviders();
builder.Logging.AddJsonConsole(o =>
{
    o.IncludeScopes = true;
    o.UseUtcTimestamp = true;
    o.TimestampFormat = "yyyy-MM-ddTHH:mm:ss.fffZ ";
});

builder.Services.AddBackoraCore(builder.Configuration);
builder.Services.AddBackoraIdentity();

// Data Protection keys (2FA challenge tokens, password reset tokens). Persist to a mounted volume in production.
var dp = builder.Services.AddDataProtection().SetApplicationName("Backora.Api");
if (builder.Configuration["DataProtection:KeysPath"] is { Length: > 0 } keysPath)
{
    dp.PersistKeysToFileSystem(new DirectoryInfo(keysPath));
}

builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<HttpCurrentUser>();
// Background execution (scheduler, dispatchers) runs in this process without an HTTP request: it acts as "system".
builder.Services.AddScoped<ICurrentUser>(sp => sp.GetRequiredService<IHttpContextAccessor>().HttpContext is null
    ? new Backora.Infrastructure.Auditing.SystemUser()
    : sp.GetRequiredService<HttpCurrentUser>());
if (builder.Configuration.GetValue("Execution:Enabled", true))
{
    Backora.Api.Execution.ExecutionRegistration.AddBackgroundExecution(builder.Services, builder.Configuration);
}
builder.Services.AddValidatorsFromAssemblyContaining<BackupJobValidator>();

builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
    o.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.Never;
});

builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<ProblemExceptionHandler>();

// ───────────── Authentication & authorization ─────────────
builder.Services.AddAuthentication(o =>
    {
        o.DefaultScheme = AuthSchemes.Combined;
        o.DefaultChallengeScheme = AuthSchemes.Combined;
    })
    .AddPolicyScheme(AuthSchemes.Combined, "JWT or personal access token", o =>
    {
        o.ForwardDefaultSelector = ctx =>
            ctx.Request.Headers.Authorization.ToString().StartsWith("Bearer bko_", StringComparison.Ordinal)
                ? AuthSchemes.PersonalAccessToken
                : AuthSchemes.Jwt;
    })
    .AddJwtBearer(AuthSchemes.Jwt, _ => { })
    .AddScheme<AuthenticationSchemeOptions, PersonalAccessTokenHandler>(AuthSchemes.PersonalAccessToken, _ => { });

builder.Services.AddOptions<JwtBearerOptions>(AuthSchemes.Jwt).Configure<IOptions<JwtOptions>>((o, jwt) =>
{
    o.MapInboundClaims = false;
    o.TokenValidationParameters = new TokenValidationParameters
    {
        ValidIssuer = jwt.Value.Issuer,
        ValidAudience = jwt.Value.Audience,
        IssuerSigningKey = jwt.Value.GetKey(),
        ValidAlgorithms = [SecurityAlgorithms.HmacSha256],
        ClockSkew = TimeSpan.FromSeconds(30),
        NameClaimType = "name",
        RoleClaimType = ClaimTypes.Role,
    };
    o.Events = new JwtBearerEvents
    {
        // Reject tokens of users that were disabled / had credentials changed (security stamp) since issuance.
        OnTokenValidated = async ctx =>
        {
            var sub = ctx.Principal?.FindFirst("sub")?.Value;
            var stamp = ctx.Principal?.FindFirst("sstamp")?.Value;
            var db = ctx.HttpContext.RequestServices.GetRequiredService<BackoraDbContext>();
            var user = long.TryParse(sub, out var id) ? await db.Users.FindAsync([id], ctx.HttpContext.RequestAborted) : null;
            if (user is null || !user.IsActive || user.SecurityStamp != stamp) ctx.Fail("Session is no longer valid.");
        },
    };
});

builder.Services.AddSingleton<IAuthorizationHandler, PermissionHandler>();
builder.Services.AddAuthorization(o =>
{
    o.FallbackPolicy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build();
    foreach (var permission in Permissions.All)
    {
        o.AddPolicy(permission, p => p.RequireAuthenticatedUser().AddRequirements(new PermissionRequirement(permission)));
    }
});

// ───────────── Rate limiting ─────────────
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    o.OnRejected = async (ctx, ct) =>
    {
        ctx.HttpContext.Response.ContentType = "application/problem+json";
        await ctx.HttpContext.Response.WriteAsJsonAsync(new { status = 429, title = "Too many requests", code = "rate_limited", detail = "Slow down and retry shortly." }, ct);
    };
    var limits = builder.Configuration.GetSection("RateLimits");
    o.AddPolicy(RateLimits.Login, ctx => RateLimitPartition.GetFixedWindowLimiter(
        $"login:{ctx.Connection.RemoteIpAddress}", _ => new FixedWindowRateLimiterOptions { PermitLimit = limits.GetValue("LoginPerMinute", 10), Window = TimeSpan.FromMinutes(1) }));
    o.AddPolicy(RateLimits.Refresh, ctx => RateLimitPartition.GetFixedWindowLimiter(
        $"refresh:{ctx.Connection.RemoteIpAddress}", _ => new FixedWindowRateLimiterOptions { PermitLimit = 60, Window = TimeSpan.FromMinutes(1) }));
    o.AddPolicy(RateLimits.Sensitive, ctx => RateLimitPartition.GetFixedWindowLimiter(
        $"sensitive:{ctx.User.FindFirst("sub")?.Value ?? ctx.Connection.RemoteIpAddress?.ToString()}", _ => new FixedWindowRateLimiterOptions { PermitLimit = limits.GetValue("SensitivePerMinute", 30), Window = TimeSpan.FromMinutes(1) }));
    o.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(ctx => RateLimitPartition.GetFixedWindowLimiter(
        $"global:{ctx.User.FindFirst("sub")?.Value ?? ctx.Connection.RemoteIpAddress?.ToString()}", _ => new FixedWindowRateLimiterOptions { PermitLimit = limits.GetValue("GlobalPerMinute", 600), Window = TimeSpan.FromMinutes(1) }));
});

builder.Services.Configure<ForwardedHeadersOptions>(o =>
{
    o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    o.KnownIPNetworks.Clear();
    o.KnownProxies.Clear();
    foreach (var cidr in builder.Configuration.GetSection("ForwardedHeaders:KnownNetworks").Get<string[]>() ?? [])
    {
        o.KnownIPNetworks.Add(System.Net.IPNetwork.Parse(cidr));
    }
});

builder.Services.Configure<Microsoft.AspNetCore.Server.Kestrel.Core.KestrelServerOptions>(o =>
{
    o.Limits.MaxRequestBodySize = 2 * 1024 * 1024;
    o.AddServerHeader = false;
});

// ───────────── Health & telemetry ─────────────
builder.Services.AddHealthChecks()
    .AddCheck("self", () => HealthCheckResult.Healthy(), tags: ["live"])
    .AddCheck<DatabaseHealthCheck>("postgresql", tags: ["ready"]);

builder.Services.AddOpenTelemetry()
    .ConfigureResource(r => r.AddService("backora-api", serviceVersion: "2.0.0"))
    .WithTracing(t =>
    {
        t.AddSource(BackoraTelemetry.ActivitySource.Name).AddAspNetCoreInstrumentation(o => o.Filter = ctx => !ctx.Request.Path.StartsWithSegments("/health"))
            .AddHttpClientInstrumentation().AddNpgsql();
        if (builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"] is { Length: > 0 }) t.AddOtlpExporter();
    })
    .WithMetrics(m =>
    {
        m.AddMeter(BackoraTelemetry.Meter.Name).AddAspNetCoreInstrumentation().AddHttpClientInstrumentation().AddRuntimeInstrumentation();
        if (builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"] is { Length: > 0 }) m.AddOtlpExporter();
    });

builder.Services.AddOpenApi(o => o.AddDocumentTransformer((doc, _, _) =>
{
    doc.Info.Title = "Backora API";
    doc.Info.Version = "v1";
    doc.Info.Description = "Enterprise backup management API. Authenticate with a JWT (frontend) or a personal access token (bko_…).";
    return Task.CompletedTask;
}));

var app = builder.Build();

// Fail fast on missing/invalid secrets instead of on first use.
_ = app.Services.GetRequiredService<ISecretProtector>();
_ = app.Services.GetRequiredService<IOptions<JwtOptions>>().Value.GetKey();

// Explicit operational commands: `dotnet Backora.Api.dll migrate` applies migrations and seeds roles, then exits.
if (args.Contains("migrate"))
{
    await using var scope = app.Services.CreateAsyncScope();
    var init = scope.ServiceProvider.GetRequiredService<DatabaseInitializer>();
    await init.MigrateAsync(CancellationToken.None);
    await init.SeedAsync(CancellationToken.None);
    return;
}

if (app.Configuration.GetValue("Database:MigrateOnStartup", false))
{
    await using var scope = app.Services.CreateAsyncScope();
    var init = scope.ServiceProvider.GetRequiredService<DatabaseInitializer>();
    await init.MigrateAsync(CancellationToken.None);
    await init.SeedAsync(CancellationToken.None);
}

app.UseForwardedHeaders();
app.UseMiddleware<CorrelationMiddleware>();
app.UseMiddleware<SecurityHeadersMiddleware>();
app.UseExceptionHandler();
app.UseStatusCodePages();
if (!app.Environment.IsDevelopment()) app.UseHsts();
app.UseAuthentication();
app.UseMiddleware<PasswordChangeGateMiddleware>();
app.UseRateLimiter();
app.UseAuthorization();

app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = c => c.Tags.Contains("live") }).AllowAnonymous().DisableRateLimiting();
app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = c => c.Tags.Contains("ready") }).AllowAnonymous().DisableRateLimiting();

if (app.Environment.IsDevelopment() || app.Configuration.GetValue("OpenApi:Enabled", false))
{
    app.MapOpenApi().AllowAnonymous();
    if (app.Environment.IsDevelopment()) app.MapScalarApiReference().AllowAnonymous();
}

app.MapAuth();
app.MapInventory();
app.MapOperations();
app.MapLegacy();

app.Run();

public static class RateLimits
{
    public const string Login = "login";
    public const string Refresh = "refresh";
    public const string Sensitive = "sensitive";
}

public sealed class DatabaseHealthCheck(NpgsqlDataSource dataSource) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken ct = default)
    {
        try
        {
            await using var cmd = dataSource.CreateCommand("SELECT 1");
            await cmd.ExecuteScalarAsync(ct);
            return HealthCheckResult.Healthy();
        }
        catch (Exception ex) when (ex is NpgsqlException or TimeoutException or InvalidOperationException)
        {
            return HealthCheckResult.Unhealthy("PostgreSQL unreachable");
        }
    }
}

public partial class Program;
