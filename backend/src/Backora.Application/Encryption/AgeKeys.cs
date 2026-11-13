using System.Security.Cryptography;
using Backora.Contracts.V1;

namespace Backora.Application.Encryption;

/// <summary>A freshly generated age X25519 key pair. <see cref="Identity"/> is private and is never persisted.</summary>
public sealed record AgeKeyPair(string Recipient, string Identity, string Fingerprint);

/// <summary>
/// age (https://age-encryption.org/v1) X25519 key handling: strict parsing of recipients ("age1…") and
/// identities ("AGE-SECRET-KEY-1…"), public-key derivation (X25519 base-point multiplication via
/// BouncyCastle) and key generation. Encryption and decryption themselves are performed exclusively by the
/// official <c>age</c> binary on the worker host — Backora implements no cipher.
///
/// Private material is held in byte arrays that are zeroed after use. Only public data (the recipient and
/// its fingerprint) ever leaves these methods, except <see cref="Generate"/>, which returns the identity
/// exactly once so it can be shown to the operator.
/// </summary>
public static class AgeKeys
{
    public const string RecipientHrp = "age";
    public const string IdentityHrp = "age-secret-key-";
    public const string KeyFormat = "age-x25519";
    public const string FileFormat = "age-v1";
    /// <summary>First line of every age v1 file.</summary>
    public const string HeaderLine = "age-encryption.org/v1";
    /// <summary>Object-key suffix of encrypted artifacts.</summary>
    public const string ObjectSuffix = ".age";
    public const int KeyLength = 32;
    public const int MaxInputLength = 1000;

    /// <summary>Parses an age X25519 recipient; returns the 32 raw public-key bytes.</summary>
    public static bool TryParseRecipient(string? value, out byte[] publicKey, out string? error)
    {
        publicKey = [];
        var v = value?.Trim();
        if (string.IsNullOrEmpty(v)) { error = "Enter an age recipient (public key starting with \"age1\")."; return false; }
        if (v.Length > MaxInputLength) { error = "The recipient is too long."; return false; }
        if (v.StartsWith("AGE-SECRET-KEY-", StringComparison.OrdinalIgnoreCase))
        {
            error = "This is a PRIVATE key. Backora only needs the public recipient (\"age1…\"); keep the private key offline.";
            return false;
        }
        if (v.StartsWith("ssh-", StringComparison.Ordinal) || v.StartsWith("age1pq", StringComparison.OrdinalIgnoreCase) || v.StartsWith("age1yubikey", StringComparison.OrdinalIgnoreCase))
        {
            error = "Only native age X25519 recipients (\"age1…\") are supported.";
            return false;
        }
        if (!Bech32.TryDecode(v, out var hrp, out var data, out var decodeError))
        {
            error = $"The recipient is not valid bech32: {decodeError}.";
            return false;
        }
        if (hrp != RecipientHrp)
        {
            CryptographicOperations.ZeroMemory(data);
            error = "The recipient must start with \"age1\".";
            return false;
        }
        if (data.Length != KeyLength)
        {
            CryptographicOperations.ZeroMemory(data);
            error = $"The recipient must encode a {KeyLength}-byte X25519 public key.";
            return false;
        }
        // age emits lowercase recipients; all-uppercase input is valid bech32 and is normalised on storage.
        publicKey = data;
        error = null;
        return true;
    }

    public static bool IsValidRecipient(string? value)
    {
        var ok = TryParseRecipient(value, out var pk, out _);
        CryptographicOperations.ZeroMemory(pk);
        return ok;
    }

    /// <summary>Canonical (lowercase) form of a valid recipient.</summary>
    public static string NormalizeRecipient(string value)
    {
        if (!TryParseRecipient(value, out var pk, out var error)) throw new ArgumentException(error, nameof(value));
        return Bech32.Encode(RecipientHrp, pk);
    }

    /// <summary>"SHA256:" + unpadded base64 of SHA-256 over the 32 raw public-key bytes. Reveals nothing about the private key.</summary>
    public static string Fingerprint(ReadOnlySpan<byte> publicKey)
    {
        if (publicKey.Length != KeyLength) throw new ArgumentException("A 32-byte public key is required.", nameof(publicKey));
        return "SHA256:" + Convert.ToBase64String(SHA256.HashData(publicKey)).TrimEnd('=');
    }

    public static string RecipientFingerprint(string recipient)
    {
        if (!TryParseRecipient(recipient, out var pk, out var error)) throw new ArgumentException(error, nameof(recipient));
        return Fingerprint(pk);
    }

    public static RecipientInspectionDto Inspect(string? recipient) =>
        TryParseRecipient(recipient, out var pk, out var error)
            ? new RecipientInspectionDto(true, Fingerprint(pk), null, KeyFormat)
            : new RecipientInspectionDto(false, null, error, KeyFormat);

    /// <summary>
    /// Validates the format of an identity ("AGE-SECRET-KEY-1…") and derives its X25519 public key. Accepts a
    /// bare identity or a pasted <c>age-keygen</c> key file (comment lines are ignored). The scalar is zeroed
    /// before returning; only the public key leaves this method.
    /// </summary>
    public static bool TryDerivePublicKey(ReadOnlySpan<char> identity, out byte[] publicKey, out string? error)
    {
        publicKey = [];
        if (!TryLocateIdentity(identity, out var line, out error)) return false;
        if (!Bech32.TryDecode(line, out var hrp, out var scalar, out var decodeError))
        {
            error = $"The private key is not valid bech32: {decodeError}.";
            return false;
        }
        try
        {
            if (hrp != IdentityHrp) { error = "The private key must start with \"AGE-SECRET-KEY-1\"."; return false; }
            if (scalar.Length != KeyLength) { error = $"The private key must encode a {KeyLength}-byte X25519 scalar."; return false; }
            var pk = new byte[KeyLength];
            // X25519(scalar, basepoint) — RFC 7748 (clamping is applied inside, as in age).
            Org.BouncyCastle.Math.EC.Rfc7748.X25519.ScalarMultBase(scalar, 0, pk, 0);
            publicKey = pk;
            error = null;
            return true;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(scalar);
        }
    }

    /// <summary>Describes why <paramref name="identity"/> is not a usable age identity (never echoes the value), or null.</summary>
    public static string? IdentityError(string? identity)
    {
        if (string.IsNullOrWhiteSpace(identity)) return "Enter an age private key starting with \"AGE-SECRET-KEY-1\".";
        if (TryDerivePublicKey(identity, out var pk, out var error))
        {
            CryptographicOperations.ZeroMemory(pk);
            return null;
        }
        return error;
    }

    /// <summary>
    /// The canonical identity line (uppercase ASCII, as <c>age</c> requires) followed by a newline, for
    /// delivery to the worker-host FIFO. The caller owns the buffer and must zero it.
    /// </summary>
    public static byte[] IdentityLineBytes(ReadOnlySpan<char> identity)
    {
        if (!TryLocateIdentity(identity, out var line, out var error)) throw new ArgumentException(error, nameof(identity));
        var bytes = new byte[line.Length + 1];
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (c > 126) { CryptographicOperations.ZeroMemory(bytes); throw new ArgumentException("The private key contains non-ASCII characters.", nameof(identity)); }
            bytes[i] = (byte)char.ToUpperInvariant(c);
        }
        bytes[^1] = (byte)'\n';
        return bytes;
    }

    public static bool IsValidIdentity(string? identity)
    {
        if (identity is null) return false;
        var ok = TryDerivePublicKey(identity, out var pk, out _);
        CryptographicOperations.ZeroMemory(pk);
        return ok;
    }

    /// <summary>
    /// True when <paramref name="identity"/> is the private key of <paramref name="recipient"/>. The public
    /// keys are compared in constant time.
    /// </summary>
    public static bool Matches(ReadOnlySpan<char> identity, string recipient, out string? derivedFingerprint, out string? error)
    {
        derivedFingerprint = null;
        if (!TryDerivePublicKey(identity, out var derived, out error)) return false;
        if (!TryParseRecipient(recipient, out var expected, out var recipientError))
        {
            error = "The recovery point's recorded recipient is invalid: " + recipientError;
            return false;
        }
        derivedFingerprint = Fingerprint(derived);
        return CryptographicOperations.FixedTimeEquals(derived, expected);
    }

    /// <summary>Generates a new X25519 identity from 32 CSPRNG bytes (as <c>age-keygen</c> does).</summary>
    public static AgeKeyPair Generate()
    {
        var scalar = RandomNumberGenerator.GetBytes(KeyLength);
        var pk = new byte[KeyLength];
        try
        {
            Org.BouncyCastle.Math.EC.Rfc7748.X25519.ScalarMultBase(scalar, 0, pk, 0);
            var identity = Bech32.Encode(IdentityHrp, scalar).ToUpperInvariant();
            return new AgeKeyPair(Bech32.Encode(RecipientHrp, pk), identity, Fingerprint(pk));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(scalar);
        }
    }

    /// <summary>Extracts the identity line from a bare key or an age-keygen file without allocating strings.</summary>
    private static bool TryLocateIdentity(ReadOnlySpan<char> input, out ReadOnlySpan<char> line, out string? error)
    {
        line = default;
        if (input.Length > MaxInputLength * 4) { error = "The private key is too long."; return false; }
        var found = 0;
        foreach (var range in input.Split('\n'))
        {
            var l = input[range].Trim();
            if (l.IsEmpty || l[0] == '#') continue;
            if (l.StartsWith("AGE-SECRET-KEY-", StringComparison.OrdinalIgnoreCase))
            {
                line = l;
                found++;
                continue;
            }
            if (l.StartsWith("age1", StringComparison.OrdinalIgnoreCase))
            {
                error = "This is a public recipient (\"age1…\"); the restore needs the matching PRIVATE key (\"AGE-SECRET-KEY-1…\").";
                return false;
            }
            error = "Enter an age private key starting with \"AGE-SECRET-KEY-1\".";
            return false;
        }
        if (found == 1) { error = null; return true; }
        error = found == 0 ? "Enter an age private key starting with \"AGE-SECRET-KEY-1\"." : "Paste exactly one private key.";
        return false;
    }
}
