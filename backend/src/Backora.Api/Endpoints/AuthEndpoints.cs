using Backora.Api.Security;
using Backora.Application.Common;
using Backora.Contracts.V1;
using Backora.Contracts.V1.Auth;
using Backora.Infrastructure.Identity;
using Microsoft.AspNetCore.Identity;

namespace Backora.Api.Endpoints;

public static class AuthEndpoints
{
    public static void MapAuth(this IEndpointRouteBuilder app)
    {
        var auth = app.MapGroup("/api/v1/auth").WithTags("Authentication");

        auth.MapPost("/login", async (LoginRequest r, AuthService svc, HttpContext http, CancellationToken ct) =>
                TypedResults.Ok(await svc.LoginAsync(r, Ip(http), UserAgent(http), ct)))
            .Validate<LoginRequest>().RequireRateLimiting(RateLimits.Login).AllowAnonymous();

        auth.MapPost("/login/2fa", async (TwoFactorLoginRequest r, AuthService svc, HttpContext http, CancellationToken ct) =>
                TypedResults.Ok(await svc.LoginTwoFactorAsync(r, Ip(http), UserAgent(http), ct)))
            .RequireRateLimiting(RateLimits.Login).AllowAnonymous();

        auth.MapPost("/refresh", async (RefreshRequest r, AuthService svc, HttpContext http, CancellationToken ct) =>
                TypedResults.Ok(await svc.RefreshAsync(r.RefreshToken, Ip(http), UserAgent(http), ct)))
            .RequireRateLimiting(RateLimits.Refresh).AllowAnonymous();

        auth.MapPost("/logout", async (LogoutRequest r, AuthService svc, HttpCurrentUser user, CancellationToken ct) =>
            {
                await svc.LogoutAsync(user.UserId!.Value, user.SessionFamily, r.RefreshToken, ct);
                return TypedResults.NoContent();
            }).RequireAuthorization();

        auth.MapPost("/password/forgot", async (ForgotPasswordRequest r, AuthService svc, CancellationToken ct) =>
            {
                if (!string.IsNullOrWhiteSpace(r.Email) && r.Email.Length <= 256) await svc.ForgotPasswordAsync(r.Email, ct);
                return TypedResults.Accepted((string?)null, new MessageResponse("If an account exists for this address, a reset link has been sent."));
            }).RequireRateLimiting(RateLimits.Login).AllowAnonymous();

        auth.MapPost("/password/reset", async (ResetPasswordRequest r, AuthService svc, CancellationToken ct) =>
            {
                await svc.ResetPasswordAsync(r, ct);
                return TypedResults.NoContent();
            }).RequireRateLimiting(RateLimits.Login).AllowAnonymous();

        var me = app.MapGroup("/api/v1/me").WithTags("Profile").RequireAuthorization();

        me.MapGet("", async (HttpCurrentUser user, UserManager<ApplicationUser> users) =>
        {
            var u = await users.FindByIdAsync(user.UserId!.Value.ToString()) ?? throw new NotFoundException("User", user.UserId!);
            var roles = await users.GetRolesAsync(u);
            var perms = Contracts.Security.RolePermissions.For(roles).Order(StringComparer.Ordinal).ToList();
            return TypedResults.Ok(new CurrentUserDto(u.Id, u.Email ?? string.Empty, u.Name, roles.ToList(), perms, u.TwoFactorEnabled, u.MustChangePassword, u.LastLoginAt));
        });

        me.MapPut("/profile", async (UpdateProfileRequest r, HttpCurrentUser user, UserManager<ApplicationUser> users) =>
        {
            if (string.IsNullOrWhiteSpace(r.Name) || r.Name.Length > 200) throw RequestValidationException.For("name", "Enter a name (max 200 characters).");
            var u = await users.FindByIdAsync(user.UserId!.Value.ToString()) ?? throw new NotFoundException("User", user.UserId!);
            u.Name = r.Name.Trim();
            await users.UpdateAsync(u);
            return TypedResults.NoContent();
        });

        me.MapPost("/password", async (ChangePasswordRequest r, AuthService svc, HttpCurrentUser user, CancellationToken ct) =>
        {
            await svc.ChangePasswordAsync(user.UserId!.Value, user.SessionFamily, r, ct);
            return TypedResults.NoContent();
        }).RequireRateLimiting(RateLimits.Sensitive);

        me.MapPost("/2fa/setup", async (AuthService svc, HttpCurrentUser user) => TypedResults.Ok(await svc.BeginTwoFactorSetupAsync(user.UserId!.Value)));
        me.MapPost("/2fa/enable", async (TwoFactorEnableRequest r, AuthService svc, HttpCurrentUser user, CancellationToken ct) =>
            TypedResults.Ok(await svc.EnableTwoFactorAsync(user.UserId!.Value, r.Code, ct))).RequireRateLimiting(RateLimits.Sensitive);
        me.MapPost("/2fa/disable", async (TwoFactorDisableRequest r, AuthService svc, HttpCurrentUser user, CancellationToken ct) =>
        {
            await svc.DisableTwoFactorAsync(user.UserId!.Value, r.Password, ct);
            return TypedResults.NoContent();
        }).RequireRateLimiting(RateLimits.Sensitive);

        me.MapGet("/tokens", async (UserAdminService svc, HttpCurrentUser user, CancellationToken ct) => TypedResults.Ok(await svc.TokensAsync(user.UserId!.Value, ct)));
        me.MapPost("/tokens", async (CreatePersonalAccessTokenRequest r, UserAdminService svc, HttpCurrentUser user, CancellationToken ct) =>
            TypedResults.Ok(await svc.CreateTokenAsync(user.UserId!.Value, r.Name, r.ExpiresInDays, null, ct))).Validate<CreatePersonalAccessTokenRequest>();
        me.MapDelete("/tokens/{id:long}", async (long id, UserAdminService svc, HttpCurrentUser user, CancellationToken ct) =>
        {
            await svc.RevokeTokenAsync(user.UserId!.Value, id, ct);
            return TypedResults.NoContent();
        });

        me.MapGet("/sessions", async (AuthService svc, HttpCurrentUser user, CancellationToken ct) => TypedResults.Ok(await svc.SessionsAsync(user.UserId!.Value, user.SessionFamily, ct)));
        me.MapDelete("/sessions/{id:long}", async (long id, AuthService svc, HttpCurrentUser user, CancellationToken ct) =>
        {
            await svc.RevokeSessionAsync(user.UserId!.Value, id, ct);
            return TypedResults.NoContent();
        });
    }

    internal static string? Ip(HttpContext http) => http.Connection.RemoteIpAddress?.ToString();

    internal static string? UserAgent(HttpContext http) => http.Request.Headers.UserAgent.ToString() is { Length: > 0 } ua ? ua : null;
}
