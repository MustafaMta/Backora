using Backora.Application.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace Backora.Application.Encryption;

/// <summary>
/// Backora's own age key pair, for storage sites set to "Backora key". The private key is stored in the database
/// encrypted with the app's encryption key (Encryption:Keys), so restores and listings can decrypt without anyone
/// pasting a key. One key is shared by every such site (the same key encrypts and decrypts everywhere).
/// </summary>
public static class BackoraKey
{
    /// <summary>The existing Backora key (newest), or a new one when none exists yet.</summary>
    public static async Task<(string Recipient, string IdentityCiphertext, bool Created)> GetOrCreateAsync(IAppDbContext db, ISecretProtector secrets, CancellationToken ct)
    {
        var existing = await db.S3DestinationEncryptionKeys.AsNoTracking().Where(k => k.IdentityCiphertext != null)
            .OrderByDescending(k => k.ActivatedAt).Select(k => new { k.Recipient, k.IdentityCiphertext }).FirstOrDefaultAsync(ct);
        if (existing is not null) return (existing.Recipient, existing.IdentityCiphertext!, false);
        var pair = AgeKeys.Generate();
        return (pair.Recipient, secrets.Protect(pair.Identity), true);
    }

    /// <summary>Marks the destination's rows for <paramref name="recipient"/> as Backora-held.</summary>
    public static void Attach(Backora.Domain.Infrastructure.S3Destination d, string recipient, string identityCiphertext)
    {
        foreach (var k in d.EncryptionKeys.Where(k => k.Recipient == recipient && k.IdentityCiphertext is null)) k.IdentityCiphertext = identityCiphertext;
    }

    /// <summary>The key line for a recipient when Backora holds its private key; null otherwise. The caller owns (and zeroes) the buffer.</summary>
    public static async Task<byte[]?> KeyLineForAsync(IAppDbContext db, ISecretProtector secrets, string? recipient, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(recipient)) return null;
        var cipher = await db.S3DestinationEncryptionKeys.AsNoTracking().Where(k => k.Recipient == recipient && k.IdentityCiphertext != null)
            .Select(k => k.IdentityCiphertext).FirstOrDefaultAsync(ct);
        if (cipher is null) return null;
        var identity = secrets.Unprotect(cipher);
        return AgeKeys.Matches(identity, recipient, out _, out _) ? AgeKeys.IdentityLineBytes(identity) : null;
    }

    public static Task<bool> HoldsAsync(IAppDbContext db, string? recipient, CancellationToken ct) =>
        string.IsNullOrEmpty(recipient) ? Task.FromResult(false)
            : db.S3DestinationEncryptionKeys.AnyAsync(k => k.Recipient == recipient && k.IdentityCiphertext != null, ct);
}
