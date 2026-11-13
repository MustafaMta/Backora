using Backora.Application.Common;
using Backora.Contracts.V1;
using Backora.Domain;
using Backora.Domain.Infrastructure;

namespace Backora.Application.Encryption;

/// <summary>Encryption-mode transitions of an S3 destination (enable, rotate, disable) — public key material only.</summary>
public static class DestinationEncryption
{
    public sealed record Change(string Kind, string? OldFingerprint, string? NewFingerprint);

    /// <summary>
    /// Applies the requested encryption mode. Rotation (a new recipient) retires the active key row and adds a
    /// new one; existing recovery points keep the recipient recorded on them (nothing is re-encrypted).
    /// Disabling requires explicit confirmation. Only public keys are handled here.
    /// </summary>
    public static Change? Apply(S3Destination d, S3DestinationUpsertRequest r, bool isNew, DateTimeOffset now)
    {
        var oldMode = isNew ? EncryptionMode.None : d.EncryptionMode;
        var oldFingerprint = isNew ? null : d.EncryptionKeyFingerprint;
        var requested = r.EncryptionMode.ToDomain();

        if (requested == EncryptionMode.None)
        {
            if (oldMode == EncryptionMode.None) return null;
            if (!r.ConfirmDisableEncryption)
            {
                throw new UnprocessableRequestException("encryption_disable_unconfirmed", "confirmDisableEncryption",
                    "Disabling encryption means new backups on this destination are uploaded unencrypted. Set confirmDisableEncryption to true to confirm. Existing encrypted recovery points stay encrypted and still need their private key.");
            }
            RetireActiveKey(d, now);
            d.EncryptionMode = EncryptionMode.None;
            d.EncryptionRecipient = null;
            d.EncryptionKeyFingerprint = null;
            d.EncryptionKeyActivatedAt = null;
            return new Change("disabled", oldFingerprint, null);
        }

        // CustomerKey: a recipient is required; on update an empty value keeps the active recipient.
        string recipient;
        if (string.IsNullOrWhiteSpace(r.EncryptionRecipient))
        {
            if (oldMode == EncryptionMode.CustomerKey && d.EncryptionRecipient is not null) return null;
            throw RequestValidationException.For("encryptionRecipient", "A customer-key destination needs an age recipient (public key starting with \"age1\").");
        }
        if (!AgeKeys.TryParseRecipient(r.EncryptionRecipient, out var publicKey, out var error))
        {
            throw RequestValidationException.For("encryptionRecipient", error ?? "Invalid age recipient.");
        }
        recipient = Bech32.Encode(AgeKeys.RecipientHrp, publicKey);
        var fingerprint = AgeKeys.Fingerprint(publicKey);
        if (oldMode == EncryptionMode.CustomerKey && string.Equals(d.EncryptionRecipient, recipient, StringComparison.Ordinal)) return null;

        RetireActiveKey(d, now);
        d.EncryptionMode = EncryptionMode.CustomerKey;
        d.EncryptionRecipient = recipient;
        d.EncryptionKeyFingerprint = fingerprint;
        d.EncryptionKeyActivatedAt = now;
        d.EncryptionKeys.Add(new S3DestinationEncryptionKey { Recipient = recipient, Fingerprint = fingerprint, ActivatedAt = now });
        return new Change(oldMode == EncryptionMode.CustomerKey ? "rotated" : "enabled", oldFingerprint, fingerprint);
    }

    private static void RetireActiveKey(S3Destination d, DateTimeOffset now)
    {
        foreach (var k in d.EncryptionKeys.Where(k => k.RetiredAt is null)) k.RetiredAt = now;
    }
}
