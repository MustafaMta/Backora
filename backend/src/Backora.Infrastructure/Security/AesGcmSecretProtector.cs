using System.Security.Cryptography;
using System.Text;
using Backora.Application.Abstractions;
using Microsoft.Extensions.Options;

namespace Backora.Infrastructure.Security;

public sealed class EncryptionOptions
{
    public const string Section = "Encryption";

    /// <summary>Key id used for new encryptions.</summary>
    public string ActiveKeyId { get; set; } = string.Empty;

    /// <summary>Map of key id → base64 256-bit key. Supplied via environment/secret store, never the database.</summary>
    public Dictionary<string, string> Keys { get; set; } = new(StringComparer.Ordinal);
}

/// <summary>
/// AES-256-GCM envelope: <c>bk1:{keyId}:{base64(nonce[12] | ciphertext | tag[16])}</c>.
/// Nonces are 96-bit random values from the OS CSPRNG (never reused under one key in practice at our
/// volumes; NIST SP 800-38D limit is 2^32 messages per key). The key id is bound as associated data so a
/// ciphertext cannot be re-labelled to another key. Rotation: add a new key, switch ActiveKeyId, run
/// <c>Backora.Tools rotate-keys</c>, then retire the old key.
/// </summary>
public sealed class AesGcmSecretProtector : ISecretProtector
{
    private const string Prefix = "bk1";
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private readonly Dictionary<string, byte[]> _keys;
    private readonly string _activeKeyId;

    public AesGcmSecretProtector(IOptions<EncryptionOptions> options)
    {
        var o = options.Value;
        if (string.IsNullOrWhiteSpace(o.ActiveKeyId) || o.Keys.Count == 0)
        {
            throw new InvalidOperationException(
                "Encryption keys are not configured. Set Encryption__ActiveKeyId and Encryption__Keys__<id> (base64, 32 bytes).");
        }

        _keys = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var (id, b64) in o.Keys)
        {
            if (id.Contains(':', StringComparison.Ordinal)) throw new InvalidOperationException($"Key id '{id}' must not contain ':'.");
            byte[] key;
            try { key = Convert.FromBase64String(b64); }
            catch (FormatException) { throw new InvalidOperationException($"Encryption key '{id}' is not valid base64."); }
            if (key.Length != 32) throw new InvalidOperationException($"Encryption key '{id}' must be 32 bytes (AES-256).");
            _keys[id] = key;
        }

        if (!_keys.ContainsKey(o.ActiveKeyId)) throw new InvalidOperationException($"Active key '{o.ActiveKeyId}' is not present in Encryption:Keys.");
        _activeKeyId = o.ActiveKeyId;
    }

    public string Protect(string plaintext)
    {
        ArgumentNullException.ThrowIfNull(plaintext);
        var key = _keys[_activeKeyId];
        var plain = Encoding.UTF8.GetBytes(plaintext);
        var buffer = new byte[NonceSize + plain.Length + TagSize];
        var nonce = buffer.AsSpan(0, NonceSize);
        RandomNumberGenerator.Fill(nonce);
        using var aes = new AesGcm(key, TagSize);
        aes.Encrypt(nonce, plain, buffer.AsSpan(NonceSize, plain.Length), buffer.AsSpan(NonceSize + plain.Length, TagSize), Aad(_activeKeyId));
        CryptographicOperations.ZeroMemory(plain);
        return $"{Prefix}:{_activeKeyId}:{Convert.ToBase64String(buffer)}";
    }

    public string Unprotect(string ciphertext)
    {
        var parts = ciphertext.Split(':', 3);
        if (parts.Length != 3 || parts[0] != Prefix) throw new CryptographicException("Unrecognised secret envelope.");
        if (!_keys.TryGetValue(parts[1], out var key)) throw new CryptographicException($"Encryption key '{parts[1]}' is not available.");
        var buffer = Convert.FromBase64String(parts[2]);
        if (buffer.Length < NonceSize + TagSize) throw new CryptographicException("Ciphertext too short.");
        var plainLength = buffer.Length - NonceSize - TagSize;
        var plain = new byte[plainLength];
        using var aes = new AesGcm(key, TagSize);
        aes.Decrypt(buffer.AsSpan(0, NonceSize), buffer.AsSpan(NonceSize, plainLength), buffer.AsSpan(NonceSize + plainLength, TagSize), plain, Aad(parts[1]));
        var result = Encoding.UTF8.GetString(plain);
        CryptographicOperations.ZeroMemory(plain);
        return result;
    }

    public bool NeedsReEncryption(string ciphertext)
    {
        var parts = ciphertext.Split(':', 3);
        return parts.Length != 3 || parts[0] != Prefix || parts[1] != _activeKeyId;
    }

    private static byte[] Aad(string keyId) => Encoding.UTF8.GetBytes($"{Prefix}:{keyId}");

    public static string GenerateKey() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
}
