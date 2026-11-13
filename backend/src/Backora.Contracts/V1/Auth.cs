namespace Backora.Contracts.V1.Auth;

public sealed record LoginRequest(string Email, string Password);

/// <summary>Either tokens are issued, or a second factor is required and <see cref="TwoFactorToken"/> must be exchanged.</summary>
public sealed record LoginResponse(bool RequiresTwoFactor, string? TwoFactorToken, TokenResponse? Tokens);

public sealed record TwoFactorLoginRequest(string TwoFactorToken, string Code, bool IsRecoveryCode = false);

public sealed record TokenResponse(
    string AccessToken,
    DateTimeOffset AccessTokenExpiresAt,
    string RefreshToken,
    DateTimeOffset RefreshTokenExpiresAt,
    CurrentUserDto User);

public sealed record RefreshRequest(string RefreshToken);

public sealed record LogoutRequest(string? RefreshToken);

public sealed record CurrentUserDto(
    long Id,
    string Email,
    string Name,
    IReadOnlyList<string> Roles,
    IReadOnlyList<string> Permissions,
    bool TwoFactorEnabled,
    bool MustChangePassword,
    DateTimeOffset? LastLoginAt);

public sealed record ChangePasswordRequest(string CurrentPassword, string NewPassword);

public sealed record UpdateProfileRequest(string Name);

public sealed record ForgotPasswordRequest(string Email);

public sealed record ResetPasswordRequest(string Email, string Token, string NewPassword);

public sealed record TwoFactorSetupResponse(string SharedKey, string AuthenticatorUri);

public sealed record TwoFactorEnableRequest(string Code);

public sealed record TwoFactorEnableResponse(IReadOnlyList<string> RecoveryCodes);

public sealed record TwoFactorDisableRequest(string Password);

public sealed record PersonalAccessTokenDto(
    long Id,
    string Name,
    string Prefix,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ExpiresAt,
    DateTimeOffset? LastUsedAt,
    DateTimeOffset? RevokedAt);

public sealed record CreatePersonalAccessTokenRequest(string Name, int? ExpiresInDays);

/// <summary>The plain token is returned exactly once.</summary>
public sealed record CreatedPersonalAccessTokenResponse(string Token, PersonalAccessTokenDto Details);

public sealed record SessionDto(long Id, DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt, string? Ip, string? UserAgent, bool Current);
