using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace Backora.Infrastructure.Identity;

/// <summary>
/// ASP.NET Core Identity hasher (PBKDF2-HMAC-SHA512, 600k iterations) that also verifies legacy Laravel
/// bcrypt hashes (<c>$2y$</c>/<c>$2a$</c>/<c>$2b$</c>). A successful legacy verification returns
/// <see cref="PasswordVerificationResult.SuccessRehashNeeded"/> so Identity transparently upgrades the hash
/// on first login; users keep their passwords across the migration.
/// </summary>
public sealed class CompatPasswordHasher(IOptions<PasswordHasherOptions> options) : IPasswordHasher<ApplicationUser>
{
    private readonly PasswordHasher<ApplicationUser> _inner = new(options);

    public string HashPassword(ApplicationUser user, string password) => _inner.HashPassword(user, password);

    public PasswordVerificationResult VerifyHashedPassword(ApplicationUser user, string hashedPassword, string providedPassword)
    {
        if (IsBcrypt(hashedPassword))
        {
            try
            {
                return BCrypt.Net.BCrypt.Verify(providedPassword, hashedPassword)
                    ? PasswordVerificationResult.SuccessRehashNeeded
                    : PasswordVerificationResult.Failed;
            }
            catch (BCrypt.Net.SaltParseException)
            {
                return PasswordVerificationResult.Failed;
            }
        }
        return _inner.VerifyHashedPassword(user, hashedPassword, providedPassword);
    }

    public static bool IsBcrypt(string hash) =>
        hash.Length == 60 && (hash.StartsWith("$2y$", StringComparison.Ordinal) || hash.StartsWith("$2a$", StringComparison.Ordinal) || hash.StartsWith("$2b$", StringComparison.Ordinal));
}
