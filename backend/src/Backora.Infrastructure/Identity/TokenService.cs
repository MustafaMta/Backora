using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Backora.Application.Abstractions;
using Backora.Contracts.Security;
using Backora.Contracts.V1.Auth;
using Backora.Domain.Platform;
using Backora.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Backora.Infrastructure.Identity;

public sealed class JwtOptions
{
    public const string Section = "Jwt";
    public string Issuer { get; set; } = "backora";
    public string Audience { get; set; } = "backora-api";

    /// <summary>HMAC-SHA256 signing key, base64, at least 32 bytes. Supplied via secret store/environment.</summary>
    public string SigningKey { get; set; } = string.Empty;
    public int AccessTokenMinutes { get; set; } = 15;
    public int RefreshTokenDays { get; set; } = 14;

    public SymmetricSecurityKey GetKey()
    {
        byte[] bytes;
        try { bytes = Convert.FromBase64String(SigningKey); }
        catch (FormatException) { throw new InvalidOperationException("Jwt:SigningKey must be base64."); }
        if (bytes.Length < 32) throw new InvalidOperationException("Jwt:SigningKey must be at least 256 bits.");
        return new SymmetricSecurityKey(bytes) { KeyId = "backora-hs256" };
    }
}

public static class BackoraClaims
{
    public const string Permission = "perm";
    public const string SessionFamily = "sfam";
    public const string AuthMethod = "amr";
    public const string PasswordChangeRequired = "pwchg";
}

public sealed class TokenService(BackoraDbContext db, IOptions<JwtOptions> options, IClock clock)
{
    private readonly JwtOptions _o = options.Value;

    public async Task<TokenResponse> IssueAsync(ApplicationUser user, IList<string> roles, string? familyId, string? ip, string? userAgent, CancellationToken ct)
    {
        var now = clock.UtcNow;
        familyId ??= Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
        var permissions = RolePermissions.For(roles);
        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
            new(JwtRegisteredClaimNames.Email, user.Email ?? string.Empty),
            new(JwtRegisteredClaimNames.Name, user.Name),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString("N")),
            new(BackoraClaims.SessionFamily, familyId),
            new("sstamp", user.SecurityStamp ?? string.Empty),
        };
        if (user.MustChangePassword) claims.Add(new Claim(BackoraClaims.PasswordChangeRequired, "1"));
        claims.AddRange(roles.Select(r => new Claim(ClaimTypes.Role, r)));
        claims.AddRange(permissions.Select(p => new Claim(BackoraClaims.Permission, p)));

        var expires = now.AddMinutes(_o.AccessTokenMinutes);
        var accessToken = new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = _o.Issuer,
            Audience = _o.Audience,
            Subject = new ClaimsIdentity(claims),
            NotBefore = now.UtcDateTime,
            IssuedAt = now.UtcDateTime,
            Expires = expires.UtcDateTime,
            SigningCredentials = new SigningCredentials(_o.GetKey(), SecurityAlgorithms.HmacSha256),
        });

        var refreshPlain = Base64Url(RandomNumberGenerator.GetBytes(48));
        var refreshExpires = now.AddDays(_o.RefreshTokenDays);
        db.RefreshTokens.Add(new RefreshToken
        {
            UserId = user.Id,
            TokenHash = Hash(refreshPlain),
            FamilyId = familyId,
            CreatedAt = now,
            ExpiresAt = refreshExpires,
            CreatedByIp = ip,
            UserAgent = userAgent is { Length: > 500 } ua ? ua[..500] : userAgent,
        });
        await db.SaveChangesAsync(ct);

        var dto = new CurrentUserDto(user.Id, user.Email ?? string.Empty, user.Name, roles.ToList(), permissions.Order(StringComparer.Ordinal).ToList(),
            user.TwoFactorEnabled, user.MustChangePassword, user.LastLoginAt);
        return new TokenResponse(accessToken, expires, refreshPlain, refreshExpires, dto);
    }

    /// <summary>
    /// Rotates a refresh token. Presenting an already-rotated token is treated as theft: the entire token
    /// family (session) is revoked (OAuth 2.0 Security BCP refresh token reuse detection).
    /// </summary>
    public async Task<(RefreshToken? Token, string? Error)> ValidateForRotationAsync(string refreshToken, CancellationToken ct)
    {
        var hash = Hash(refreshToken);
        var token = await db.RefreshTokens.FirstOrDefaultAsync(t => t.TokenHash == hash, ct);
        var now = clock.UtcNow;
        if (token is null) return (null, "invalid");
        if (token.RevokedAt is not null)
        {
            if (token.ReplacedByHash is not null) await RevokeFamilyAsync(token.UserId, token.FamilyId, ct);
            return (null, "reused");
        }
        if (token.ExpiresAt <= now) return (null, "expired");
        return (token, null);
    }

    public async Task MarkRotatedAsync(RefreshToken token, string newPlain, CancellationToken ct)
    {
        token.RevokedAt = clock.UtcNow;
        token.ReplacedByHash = Hash(newPlain);
        await db.SaveChangesAsync(ct);
    }

    public async Task RevokeFamilyAsync(long userId, string familyId, CancellationToken ct)
    {
        var now = clock.UtcNow;
        await db.RefreshTokens.Where(t => t.UserId == userId && t.FamilyId == familyId && t.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAt, now), ct);
    }

    public async Task RevokeAllAsync(long userId, string? exceptFamily, CancellationToken ct)
    {
        var now = clock.UtcNow;
        await db.RefreshTokens.Where(t => t.UserId == userId && t.RevokedAt == null && t.FamilyId != exceptFamily)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAt, now), ct);
    }

    public static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    public static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
