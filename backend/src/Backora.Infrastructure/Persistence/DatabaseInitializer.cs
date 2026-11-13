using Backora.Contracts.Security;
using Backora.Infrastructure.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Backora.Infrastructure.Persistence;

/// <summary>Applies migrations (explicitly, never implicitly at app start in production) and seeds roles / bootstrap admin.</summary>
public sealed class DatabaseInitializer(BackoraDbContext db, RoleManager<ApplicationRole> roles, UserManager<ApplicationUser> users, IConfiguration config, ILogger<DatabaseInitializer> logger)
{
    public async Task MigrateAsync(CancellationToken ct)
    {
        var pending = (await db.Database.GetPendingMigrationsAsync(ct)).ToList();
        logger.LogInformation("Applying {Count} pending migration(s): {Migrations}", pending.Count, string.Join(", ", pending));
        await db.Database.MigrateAsync(ct);
        // Fixes to the built-in backup templates reach the copies stored in the database (never hand-edited ones).
        await BuiltInTemplates.SyncAsync(db, logger, ct);
    }

    public async Task SeedAsync(CancellationToken ct)
    {
        foreach (var role in Roles.All)
        {
            if (!await roles.RoleExistsAsync(role))
            {
                var result = await roles.CreateAsync(new ApplicationRole(role));
                if (!result.Succeeded) throw new InvalidOperationException($"Failed to create role {role}: {string.Join("; ", result.Errors.Select(e => e.Description))}");
            }
        }

        // Bootstrap: only when NO users exist, using credentials supplied out-of-band (env/secret).
        if (!await db.Users.AnyAsync(ct))
        {
            var email = config["Bootstrap:AdminEmail"];
            var password = config["Bootstrap:AdminPassword"];
            if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password))
            {
                logger.LogWarning("No users exist. Set Bootstrap__AdminEmail and Bootstrap__AdminPassword (or run `Backora.Tools create-admin`) to create the first Platform Administrator.");
                return;
            }
            var admin = new ApplicationUser { UserName = email, Email = email, Name = config["Bootstrap:AdminName"] ?? "Platform Administrator", EmailConfirmed = true, IsActive = true, MustChangePassword = true };
            var created = await users.CreateAsync(admin, password);
            if (!created.Succeeded) throw new InvalidOperationException("Bootstrap admin creation failed: " + string.Join("; ", created.Errors.Select(e => e.Description)));
            await users.AddToRoleAsync(admin, Roles.PlatformAdministrator);
            logger.LogInformation("Bootstrap Platform Administrator {Email} created (password change required at first sign-in).", email);
        }
    }
}
