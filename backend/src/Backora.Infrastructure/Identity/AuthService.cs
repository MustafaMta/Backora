using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Backora.Application.Abstractions;
using Backora.Application.Common;
using Backora.Application.Notifications;
using Backora.Contracts.V1.Auth;
using Backora.Domain;
using Backora.Domain.Platform;
using Backora.Infrastructure.Persistence;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Backora.Infrastructure.Identity;

public sealed class AuthService(
    UserManager<ApplicationUser> users,
    BackoraDbContext db,
    TokenService tokens,
    IDataProtectionProvider dataProtection,
    IAuditLog audit,
    IClock clock,
    IOptions<PlatformOptions> platform,
    ILogger<AuthService> logger)
{
    private const string GenericLoginError = "Invalid email or password.";
    private readonly ITimeLimitedDataProtector _twoFactorProtector =
        dataProtection.CreateProtector("Backora.TwoFactorChallenge.v1").ToTimeLimitedDataProtector();

    public async Task<LoginResponse> LoginAsync(LoginRequest request, string? ip, string? userAgent, CancellationToken ct)
    {
        var user = await users.FindByEmailAsync(request.Email.Trim());
        if (user is null)
        {
            // Burn comparable time so response timing does not reveal account existence.
            users.PasswordHasher.HashPassword(new ApplicationUser(), request.Password);
            await audit.RecordNowAsync("auth.login", "User", null, AuditOutcome.Failure, new { email = Mask(request.Email), reason = "unknown_user" }, ct);
            throw new AuthenticationFailedException(GenericLoginError);
        }

        if (await users.IsLockedOutAsync(user))
        {
            await audit.RecordNowAsync("auth.login", "User", user.Id.ToString(), AuditOutcome.Denied, new { reason = "locked_out" }, ct);
            throw new AuthenticationFailedException("The account is temporarily locked after repeated failures. Try again later or contact an administrator.");
        }

        if (!await users.CheckPasswordAsync(user, request.Password))
        {
            await users.AccessFailedAsync(user);
            await audit.RecordNowAsync("auth.login", "User", user.Id.ToString(), AuditOutcome.Failure, new { reason = "bad_password" }, ct);
            throw new AuthenticationFailedException(GenericLoginError);
        }

        if (!user.IsActive)
        {
            await audit.RecordNowAsync("auth.login", "User", user.Id.ToString(), AuditOutcome.Denied, new { reason = "disabled" }, ct);
            throw new AuthenticationFailedException(GenericLoginError);
        }

        await users.ResetAccessFailedCountAsync(user);

        if (user.TwoFactorEnabled)
        {
            var payload = $"{user.Id}|{user.SecurityStamp}";
            var challenge = _twoFactorProtector.Protect(payload, TimeSpan.FromMinutes(5));
            return new LoginResponse(true, challenge, null);
        }

        return new LoginResponse(false, null, await CompleteLoginAsync(user, ip, userAgent, "pwd", ct));
    }

    public async Task<TokenResponse> LoginTwoFactorAsync(TwoFactorLoginRequest request, string? ip, string? userAgent, CancellationToken ct)
    {
        string payload;
        try
        {
            payload = _twoFactorProtector.Unprotect(request.TwoFactorToken);
        }
        catch (CryptographicException)
        {
            throw new AuthenticationFailedException("The sign-in challenge expired. Sign in again.");
        }

        var parts = payload.Split('|');
        var user = parts.Length == 2 && long.TryParse(parts[0], out var id) ? await users.FindByIdAsync(id.ToString()) : null;
        if (user is null || user.SecurityStamp != parts[1] || !user.IsActive || await users.IsLockedOutAsync(user))
        {
            throw new AuthenticationFailedException("The sign-in challenge is no longer valid. Sign in again.");
        }

        var code = request.Code.Replace(" ", string.Empty, StringComparison.Ordinal).Replace("-", string.Empty, StringComparison.Ordinal);
        var ok = request.IsRecoveryCode
            ? (await users.RedeemTwoFactorRecoveryCodeAsync(user, request.Code.Trim())).Succeeded
            : await users.VerifyTwoFactorTokenAsync(user, users.Options.Tokens.AuthenticatorTokenProvider, code);
        if (!ok)
        {
            await users.AccessFailedAsync(user);
            await audit.RecordNowAsync("auth.login_2fa", "User", user.Id.ToString(), AuditOutcome.Failure, new { recovery = request.IsRecoveryCode }, ct);
            throw new AuthenticationFailedException("The verification code is not valid.");
        }
        await users.ResetAccessFailedCountAsync(user);
        return await CompleteLoginAsync(user, ip, userAgent, request.IsRecoveryCode ? "recovery" : "mfa", ct);
    }

    private async Task<TokenResponse> CompleteLoginAsync(ApplicationUser user, string? ip, string? userAgent, string method, CancellationToken ct)
    {
        var previousLogin = user.LastLoginAt;
        user.LastLoginAt = clock.UtcNow;
        await users.UpdateAsync(user);
        var roles = await users.GetRolesAsync(user);
        var response = await tokens.IssueAsync(user, roles, null, ip, userAgent, ct);
        db.AuditEvents.Add(new AuditEvent
        {
            OccurredAt = clock.UtcNow, ActorUserId = user.Id, ActorName = user.Email ?? user.Name, ActorIp = ip, Action = "auth.login",
            ResourceType = "User", ResourceId = user.Id.ToString(), Outcome = AuditOutcome.Success,
            DetailsJson = JsonSerializer.Serialize(new { method }),
        });
        await db.SaveChangesAsync(ct);
        return response with { User = response.User with { LastLoginAt = previousLogin } };
    }

    public async Task<TokenResponse> RefreshAsync(string refreshToken, string? ip, string? userAgent, CancellationToken ct)
    {
        var (token, error) = await tokens.ValidateForRotationAsync(refreshToken, ct);
        if (token is null)
        {
            if (error == "reused") logger.LogWarning("Refresh token reuse detected; session family revoked");
            throw new AuthenticationFailedException("Session expired. Sign in again.");
        }
        var user = await users.FindByIdAsync(token.UserId.ToString());
        if (user is null || !user.IsActive || await users.IsLockedOutAsync(user))
        {
            await tokens.RevokeFamilyAsync(token.UserId, token.FamilyId, ct);
            throw new AuthenticationFailedException("Session expired. Sign in again.");
        }
        var roles = await users.GetRolesAsync(user);
        var response = await tokens.IssueAsync(user, roles, token.FamilyId, ip, userAgent, ct);
        await tokens.MarkRotatedAsync(token, response.RefreshToken, ct);
        return response;
    }

    public async Task LogoutAsync(long userId, string? familyId, string? refreshToken, CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(refreshToken))
        {
            var hash = TokenService.Hash(refreshToken);
            var t = await db.RefreshTokens.AsNoTracking().FirstOrDefaultAsync(x => x.TokenHash == hash && x.UserId == userId, ct);
            if (t is not null) familyId = t.FamilyId;
        }
        if (familyId is not null) await tokens.RevokeFamilyAsync(userId, familyId, ct);
        audit.Record("auth.logout", "User", userId.ToString());
        await db.SaveChangesAsync(ct);
    }

    public async Task ForgotPasswordAsync(string email, CancellationToken ct)
    {
        var user = await users.FindByEmailAsync(email.Trim());
        if (user is null || !user.IsActive)
        {
            await audit.RecordNowAsync("auth.password_reset_requested", "User", null, AuditOutcome.Failure, new { email = Mask(email) }, ct);
            return; // identical response either way
        }
        var token = await users.GeneratePasswordResetTokenAsync(user);
        var url = $"{platform.Value.PublicUrl.TrimEnd('/')}/reset-password?email={UrlEncoder.Default.Encode(user.Email!)}&token={UrlEncoder.Default.Encode(token)}";
        db.NotificationMessages.Add(new NotificationMessage
        {
            Channel = NotificationChannel.Email,
            EventType = NotificationEvents.PasswordReset,
            Subject = "Reset your Backora password",
            PayloadJson = JsonSerializer.Serialize(new Dictionary<string, string?> { ["name"] = user.Name, ["reset_url"] = url }),
            Recipients = [user.Email!],
            NextAttemptAt = clock.UtcNow,
            RelatedEntityType = "User",
            RelatedEntityId = user.Id,
        });
        audit.Record("auth.password_reset_requested", "User", user.Id.ToString());
        await db.SaveChangesAsync(ct);
    }

    public async Task ResetPasswordAsync(ResetPasswordRequest request, CancellationToken ct)
    {
        var user = await users.FindByEmailAsync(request.Email.Trim());
        if (user is null) throw new DomainRuleException("reset_invalid", "The reset link is invalid or has expired.");
        var result = await users.ResetPasswordAsync(user, request.Token, request.NewPassword);
        if (!result.Succeeded)
        {
            if (result.Errors.Any(e => e.Code == "InvalidToken")) throw new DomainRuleException("reset_invalid", "The reset link is invalid or has expired.");
            throw IdentityErrors.ToValidation(result, "newPassword");
        }
        user.MustChangePassword = false;
        await users.UpdateAsync(user);
        await users.ResetAccessFailedCountAsync(user);
        await users.SetLockoutEndDateAsync(user, null);
        await tokens.RevokeAllAsync(user.Id, null, ct);
        audit.Record("auth.password_reset", "User", user.Id.ToString());
        await db.SaveChangesAsync(ct);
    }

    public async Task ChangePasswordAsync(long userId, string? currentFamily, ChangePasswordRequest request, CancellationToken ct)
    {
        var user = await users.FindByIdAsync(userId.ToString()) ?? throw new NotFoundException("User", userId);
        var result = await users.ChangePasswordAsync(user, request.CurrentPassword, request.NewPassword);
        if (!result.Succeeded)
        {
            if (result.Errors.Any(e => e.Code == "PasswordMismatch")) throw RequestValidationException.For("currentPassword", "The current password is incorrect.");
            throw IdentityErrors.ToValidation(result, "newPassword");
        }
        user.MustChangePassword = false;
        await users.UpdateAsync(user);
        await tokens.RevokeAllAsync(user.Id, currentFamily, ct);
        audit.Record("auth.password_changed", "User", user.Id.ToString());
        await db.SaveChangesAsync(ct);
    }

    public async Task<TwoFactorSetupResponse> BeginTwoFactorSetupAsync(long userId)
    {
        var user = await users.FindByIdAsync(userId.ToString()) ?? throw new NotFoundException("User", userId);
        await users.ResetAuthenticatorKeyAsync(user);
        var key = await users.GetAuthenticatorKeyAsync(user) ?? throw new InvalidOperationException("Authenticator key unavailable.");
        var label = UrlEncoder.Default.Encode($"Backora:{user.Email}");
        var uri = $"otpauth://totp/{label}?secret={key}&issuer=Backora&digits=6&period=30";
        return new TwoFactorSetupResponse(FormatKey(key), uri);
    }

    public async Task<TwoFactorEnableResponse> EnableTwoFactorAsync(long userId, string code, CancellationToken ct)
    {
        var user = await users.FindByIdAsync(userId.ToString()) ?? throw new NotFoundException("User", userId);
        var valid = await users.VerifyTwoFactorTokenAsync(user, users.Options.Tokens.AuthenticatorTokenProvider, code.Replace(" ", string.Empty, StringComparison.Ordinal));
        if (!valid) throw RequestValidationException.For("code", "The verification code is not valid. Check the time on your device.");
        await users.SetTwoFactorEnabledAsync(user, true);
        var codes = await users.GenerateNewTwoFactorRecoveryCodesAsync(user, 10) ?? [];
        audit.Record("auth.mfa_enabled", "User", userId.ToString());
        await db.SaveChangesAsync(ct);
        return new TwoFactorEnableResponse(codes.ToList());
    }

    public async Task DisableTwoFactorAsync(long userId, string password, CancellationToken ct)
    {
        var user = await users.FindByIdAsync(userId.ToString()) ?? throw new NotFoundException("User", userId);
        if (!await users.CheckPasswordAsync(user, password)) throw RequestValidationException.For("password", "The password is incorrect.");
        await users.SetTwoFactorEnabledAsync(user, false);
        await users.ResetAuthenticatorKeyAsync(user);
        audit.Record("auth.mfa_disabled", "User", userId.ToString());
        await db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<SessionDto>> SessionsAsync(long userId, string? currentFamily, CancellationToken ct)
    {
        var now = clock.UtcNow;
        var active = await db.RefreshTokens.AsNoTracking().Where(t => t.UserId == userId && t.RevokedAt == null && t.ExpiresAt > now)
            .OrderByDescending(t => t.CreatedAt).ToListAsync(ct);
        return active.GroupBy(t => t.FamilyId).Select(g => g.First())
            .Select(t => new SessionDto(t.Id, t.CreatedAt, t.ExpiresAt, t.CreatedByIp, t.UserAgent, t.FamilyId == currentFamily)).ToList();
    }

    public async Task RevokeSessionAsync(long userId, long sessionId, CancellationToken ct)
    {
        var t = await db.RefreshTokens.AsNoTracking().FirstOrDefaultAsync(x => x.Id == sessionId && x.UserId == userId, ct) ?? throw new NotFoundException("Session", sessionId);
        await tokens.RevokeFamilyAsync(userId, t.FamilyId, ct);
        audit.Record("auth.session_revoked", "User", userId.ToString());
        await db.SaveChangesAsync(ct);
    }

    private static string FormatKey(string key)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < key.Length; i += 4) sb.Append(key.AsSpan(i, Math.Min(4, key.Length - i))).Append(' ');
        return sb.ToString().TrimEnd().ToLowerInvariant();
    }

    internal static string Mask(string email)
    {
        var at = email.IndexOf('@', StringComparison.Ordinal);
        return at <= 1 ? "***" : email[0] + "***" + email[at..];
    }
}

public sealed class AuthenticationFailedException(string message) : Exception(message);

public static class IdentityErrors
{
    public static RequestValidationException ToValidation(IdentityResult result, string field) =>
        new(new Dictionary<string, string[]> { [field] = result.Errors.Select(e => e.Description).ToArray() });
}
