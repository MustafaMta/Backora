using System.Security.Cryptography;
using Backora.Application.Abstractions;
using Backora.Application.Common;
using Backora.Contracts.Security;
using Backora.Contracts.V1;
using Backora.Contracts.V1.Auth;
using Backora.Domain.Platform;
using Backora.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Backora.Infrastructure.Identity;

public sealed class UserAdminService(UserManager<ApplicationUser> users, BackoraDbContext db, ICurrentUser current, IAuditLog audit, TokenService tokens, IClock clock)
{
    public async Task<PagedResult<UserDto>> ListAsync(string? q, PageRequest page, CancellationToken ct)
    {
        var query = db.Users.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(q))
        {
            var term = $"%{q.Trim()}%";
            query = query.Where(u => EF.Functions.ILike(u.Name, term) || EF.Functions.ILike(u.Email!, term));
        }
        query = page.Descending ? query.OrderByDescending(u => u.Name) : query.OrderBy(u => u.Name);
        var total = await query.LongCountAsync(ct);
        var list = await query.Skip(page.Skip).Take(page.SafePageSize).ToListAsync(ct);
        var roles = await RolesByUserAsync(list.Select(u => u.Id).ToList(), ct);
        return new PagedResult<UserDto>(list.Select(u => ToDto(u, roles.GetValueOrDefault(u.Id) ?? [])).ToList(), page.SafePage, page.SafePageSize, total);
    }

    public async Task<UserDto> GetAsync(long id, CancellationToken ct)
    {
        var u = await db.Users.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("User", id);
        var roles = await RolesByUserAsync([id], ct);
        return ToDto(u, roles.GetValueOrDefault(id) ?? []);
    }

    public async Task<UserDto> CreateAsync(CreateUserRequest r, CancellationToken ct)
    {
        GuardRoleGrant(r.Roles);
        if (await users.FindByEmailAsync(r.Email.Trim()) is not null) throw new ConflictException("email_taken", "A user with this email already exists.");
        var user = new ApplicationUser { UserName = r.Email.Trim(), Email = r.Email.Trim(), Name = r.Name.Trim(), EmailConfirmed = true, IsActive = true, MustChangePassword = true };
        var result = await users.CreateAsync(user, r.Password);
        if (!result.Succeeded) throw IdentityErrors.ToValidation(result, "password");
        await users.AddToRolesAsync(user, r.Roles.Distinct());
        audit.Record("user.created", "User", user.Id.ToString(), details: new { user.Email, roles = r.Roles });
        await db.SaveChangesAsync(ct);
        return await GetAsync(user.Id, ct);
    }

    public async Task<UserDto> UpdateAsync(long id, UpdateUserRequest r, CancellationToken ct)
    {
        var user = await users.FindByIdAsync(id.ToString()) ?? throw new NotFoundException("User", id);
        var currentRoles = await users.GetRolesAsync(user);
        var newRoles = r.Roles.Distinct().ToList();
        var rolesChanged = !currentRoles.OrderBy(x => x).SequenceEqual(newRoles.OrderBy(x => x));

        if (id == current.UserId)
        {
            if (rolesChanged) throw new DomainRuleException("self_role_change", "You cannot change your own roles.");
            if (!r.IsActive) throw new DomainRuleException("self_disable", "You cannot disable your own account.");
        }
        if (rolesChanged)
        {
            GuardRoleGrant(newRoles.Except(currentRoles));
            // Removing Platform Administrator from someone also requires being one.
            if (currentRoles.Contains(Roles.PlatformAdministrator) && !newRoles.Contains(Roles.PlatformAdministrator))
            {
                GuardRoleGrant([Roles.PlatformAdministrator]);
                await EnsureNotLastPlatformAdminAsync(id, ct);
            }
        }
        if (!r.IsActive && currentRoles.Contains(Roles.PlatformAdministrator)) await EnsureNotLastPlatformAdminAsync(id, ct);

        user.Name = r.Name.Trim();
        var deactivated = user.IsActive && !r.IsActive;
        user.IsActive = r.IsActive;
        if (deactivated || rolesChanged) await users.UpdateSecurityStampAsync(user);
        await users.UpdateAsync(user);
        if (rolesChanged)
        {
            await users.RemoveFromRolesAsync(user, currentRoles.Except(newRoles));
            await users.AddToRolesAsync(user, newRoles.Except(currentRoles));
        }
        if (deactivated || rolesChanged) await tokens.RevokeAllAsync(user.Id, null, ct);
        audit.Record("user.updated", "User", id.ToString(), details: new { user.Name, user.IsActive, before = currentRoles, after = newRoles });
        await db.SaveChangesAsync(ct);
        return await GetAsync(id, ct);
    }

    public async Task SetPasswordAsync(long id, SetUserPasswordRequest r, CancellationToken ct)
    {
        var user = await users.FindByIdAsync(id.ToString()) ?? throw new NotFoundException("User", id);
        var roles = await users.GetRolesAsync(user);
        if (roles.Contains(Roles.PlatformAdministrator) && id != current.UserId) GuardRoleGrant([Roles.PlatformAdministrator]);
        foreach (var validator in users.PasswordValidators)
        {
            var v = await validator.ValidateAsync(users, user, r.NewPassword);
            if (!v.Succeeded) throw IdentityErrors.ToValidation(v, "newPassword");
        }
        user.PasswordHash = users.PasswordHasher.HashPassword(user, r.NewPassword);
        user.MustChangePassword = r.RequireChangeOnNextLogin;
        await users.UpdateSecurityStampAsync(user);
        await users.UpdateAsync(user);
        await tokens.RevokeAllAsync(user.Id, null, ct);
        audit.Record("user.password_set", "User", id.ToString(), details: new { r.RequireChangeOnNextLogin });
        await db.SaveChangesAsync(ct);
    }

    public async Task UnlockAsync(long id, CancellationToken ct)
    {
        var user = await users.FindByIdAsync(id.ToString()) ?? throw new NotFoundException("User", id);
        await users.SetLockoutEndDateAsync(user, null);
        await users.ResetAccessFailedCountAsync(user);
        audit.Record("user.unlocked", "User", id.ToString());
        await db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<RoleDto>> RolesAsync(CancellationToken ct)
    {
        var counts = await (from ur in db.UserRoles join r in db.Roles on ur.RoleId equals r.Id group ur by r.Name into g select new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Key!, x => x.Count, ct);
        return Roles.All.Select(r => new RoleDto(r, Roles.DisplayName(r), Roles.Describe(r), RolePermissions.Matrix[r].Order(StringComparer.Ordinal).ToList(), counts.GetValueOrDefault(r))).ToList();
    }

    // ───────────── Personal access tokens ─────────────
    public async Task<IReadOnlyList<PersonalAccessTokenDto>> TokensAsync(long userId, CancellationToken ct) =>
        (await db.ApiTokens.AsNoTracking().Where(t => t.UserId == userId).OrderByDescending(t => t.CreatedAt).ToListAsync(ct)).Select(ToDto).ToList();

    public async Task<CreatedPersonalAccessTokenResponse> CreateTokenAsync(long userId, string name, int? expiresInDays, TimeSpan? exactLifetime, CancellationToken ct)
    {
        var plain = "bko_" + TokenService.Base64Url(RandomNumberGenerator.GetBytes(30));
        var token = new ApiToken
        {
            UserId = userId,
            Name = name.Trim(),
            TokenHash = TokenService.Hash(plain),
            Prefix = plain[..10],
            ExpiresAt = exactLifetime is { } lt ? clock.UtcNow.Add(lt) : clock.UtcNow.AddDays(expiresInDays ?? 90),
        };
        db.ApiTokens.Add(token);
        await db.SaveChangesAsync(ct);
        audit.Record("auth.token_created", "ApiToken", token.Id.ToString(), details: new { token.Name, token.Prefix, token.ExpiresAt });
        await db.SaveChangesAsync(ct);
        return new CreatedPersonalAccessTokenResponse(plain, ToDto(token));
    }

    public async Task RevokeTokenAsync(long userId, long tokenId, CancellationToken ct)
    {
        var token = await db.ApiTokens.FirstOrDefaultAsync(t => t.Id == tokenId && t.UserId == userId, ct) ?? throw new NotFoundException("Token", tokenId);
        token.RevokedAt ??= clock.UtcNow;
        audit.Record("auth.token_revoked", "ApiToken", tokenId.ToString(), details: new { token.Name, token.Prefix });
        await db.SaveChangesAsync(ct);
    }

    private static PersonalAccessTokenDto ToDto(ApiToken t) => new(t.Id, t.Name, t.Prefix, t.CreatedAt, t.ExpiresAt, t.LastUsedAt, t.RevokedAt);

    private void GuardRoleGrant(IEnumerable<string> roles)
    {
        if (roles.Contains(Roles.PlatformAdministrator) && !current.Roles.Contains(Roles.PlatformAdministrator))
        {
            throw new ForbiddenException("Only Platform Administrators can grant or revoke the Platform Administrator role.");
        }
    }

    private async Task EnsureNotLastPlatformAdminAsync(long userId, CancellationToken ct)
    {
        var admins = await users.GetUsersInRoleAsync(Roles.PlatformAdministrator);
        if (admins.Count(a => a.IsActive && a.Id != userId) == 0) throw new DomainRuleException("last_platform_admin", "At least one active Platform Administrator must remain.");
    }

    private async Task<Dictionary<long, List<string>>> RolesByUserAsync(List<long> ids, CancellationToken ct) =>
        (await (from ur in db.UserRoles join r in db.Roles on ur.RoleId equals r.Id where ids.Contains(ur.UserId) select new { ur.UserId, r.Name }).ToListAsync(ct))
        .GroupBy(x => x.UserId).ToDictionary(g => g.Key, g => g.Select(x => x.Name!).Order(StringComparer.Ordinal).ToList());

    private static UserDto ToDto(ApplicationUser u, List<string> roles) => new(
        u.Id, u.Email ?? string.Empty, u.Name, roles, u.IsActive, u.TwoFactorEnabled, u.LastLoginAt,
        u.LockoutEnd is { } le && le > DateTimeOffset.UtcNow ? le : null, u.CreatedAt, u.LegacyUserCode);
}
