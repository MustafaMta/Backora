using System.Security.Claims;
using System.Text.Encodings.Web;
using Backora.Application.Abstractions;
using Backora.Contracts.Security;
using Backora.Infrastructure.Identity;
using Backora.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Backora.Api.Security;

public static class AuthSchemes
{
    public const string Jwt = "Bearer";
    public const string PersonalAccessToken = "PersonalAccessToken";
    public const string Combined = "BearerOrPat";
}

/// <summary>Authenticates opaque <c>bko_</c> personal access tokens (SHA-256 hashed at rest).</summary>
public sealed class PersonalAccessTokenHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    BackoraDbContext db,
    IClock clock) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var header = Request.Headers.Authorization.ToString();
        if (!header.StartsWith("Bearer bko_", StringComparison.Ordinal)) return AuthenticateResult.NoResult();
        var plain = header["Bearer ".Length..].Trim();
        var hash = TokenService.Hash(plain);
        var now = clock.UtcNow;
        var token = await db.ApiTokens.FirstOrDefaultAsync(t => t.TokenHash == hash, Context.RequestAborted);
        if (token is null || !token.IsActive(now)) return AuthenticateResult.Fail("Invalid or expired token.");
        var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == token.UserId, Context.RequestAborted);
        if (user is null || !user.IsActive || (user.LockoutEnd is { } le && le > now)) return AuthenticateResult.Fail("Token user is disabled.");

        var roles = await (from ur in db.UserRoles join r in db.Roles on ur.RoleId equals r.Id where ur.UserId == user.Id select r.Name!).ToListAsync(Context.RequestAborted);
        if (token.LastUsedAt is null || now - token.LastUsedAt > TimeSpan.FromMinutes(1))
        {
            token.LastUsedAt = now;
            await db.SaveChangesAsync(Context.RequestAborted);
        }

        var claims = new List<Claim>
        {
            new("sub", user.Id.ToString()),
            new("email", user.Email ?? string.Empty),
            new("name", user.Name),
            new(BackoraClaims.AuthMethod, "pat"),
            new("token_id", token.Id.ToString()),
        };
        claims.AddRange(roles.Select(r => new Claim(ClaimTypes.Role, r)));
        claims.AddRange(RolePermissions.For(roles).Select(p => new Claim(BackoraClaims.Permission, p)));
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, Scheme.Name, "name", ClaimTypes.Role));
        return AuthenticateResult.Success(new AuthenticationTicket(principal, Scheme.Name));
    }
}

/// <summary>Requirement satisfied when the principal carries the permission claim.</summary>
public sealed record PermissionRequirement(string Permission) : IAuthorizationRequirement;

public sealed class PermissionHandler : AuthorizationHandler<PermissionRequirement>
{
    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, PermissionRequirement requirement)
    {
        if (context.User.HasClaim(BackoraClaims.Permission, requirement.Permission)) context.Succeed(requirement);
        return Task.CompletedTask;
    }
}

public sealed class HttpCurrentUser(IHttpContextAccessor accessor) : ICurrentUser
{
    private ClaimsPrincipal? Principal => accessor.HttpContext?.User;

    public long? UserId => long.TryParse(Principal?.FindFirst("sub")?.Value, out var id) ? id : null;
    public string Name => Principal?.FindFirst("email")?.Value is { Length: > 0 } e ? e : Principal?.FindFirst("name")?.Value ?? "anonymous";
    public string? IpAddress => accessor.HttpContext?.Connection.RemoteIpAddress?.ToString();
    public string? CorrelationId => accessor.HttpContext?.Items[CorrelationMiddleware.ItemKey] as string;
    public bool IsAuthenticated => Principal?.Identity?.IsAuthenticated == true;
    public bool HasPermission(string permission) => Principal?.HasClaim(BackoraClaims.Permission, permission) == true;
    public IReadOnlyCollection<string> Roles => Principal?.FindAll(ClaimTypes.Role).Select(c => c.Value).ToList() ?? [];
    public string? SessionFamily => Principal?.FindFirst(BackoraClaims.SessionFamily)?.Value;
}
