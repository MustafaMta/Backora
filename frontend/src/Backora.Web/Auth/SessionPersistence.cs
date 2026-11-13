using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using Backora.Contracts.V1.Auth;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.XmlEncryption;

namespace Backora.Web.Auth;

/// <summary>
/// Where server-side sessions survive a restart. Without it (development) sessions live in memory only and a restart
/// signs everyone out.
/// </summary>
public interface ISessionPersistence
{
    void SaveSession(UserSession session);
    UserSession? LoadSession(string id);
    void SaveTicket(string id, AuthenticationTicket ticket);
    AuthenticationTicket? LoadTicket(string id);
    void Delete(string id);
    /// <summary>Removes sessions whose refresh token has expired, and files that cannot be read back; returns how many.</summary>
    int PurgeExpired(DateTimeOffset now);
}

public sealed class NoSessionPersistence : ISessionPersistence
{
    public void SaveSession(UserSession session) { }
    public UserSession? LoadSession(string id) => null;
    public void SaveTicket(string id, AuthenticationTicket ticket) { }
    public AuthenticationTicket? LoadTicket(string id) => null;
    public void Delete(string id) { }
    public int PurgeExpired(DateTimeOffset now) => 0;
}

/// <summary>
/// One encrypted file per session on a private volume, so a frontend restart or redeploy keeps users signed in.
/// <list type="bullet">
/// <item>The file name is a hash of the session id: the cookie's value never appears on disk.</item>
/// <item>The contents (backend tokens, the sign-in ticket) are encrypted and authenticated with ASP.NET Data
/// Protection; a file that does not decrypt (tampered, from another key ring) is ignored and deleted.</item>
/// <item>Files are 0600 in a 0700 folder, written atomically (temp file, then rename).</item>
/// </list>
/// </summary>
public sealed class FileSessionPersistence : ISessionPersistence
{
    private readonly string _dir;
    private readonly IDataProtector _protector;
    private readonly ILogger<FileSessionPersistence> _logger;

    public FileSessionPersistence(string directory, IDataProtectionProvider provider, ILogger<FileSessionPersistence> logger)
    {
        _dir = directory;
        _protector = provider.CreateProtector("Backora.Web.Sessions.v1");
        _logger = logger;
        Directory.CreateDirectory(_dir);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(_dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    private sealed record SessionRecord(string Id, string AccessToken, DateTimeOffset AccessTokenExpiresAt, string RefreshToken,
        DateTimeOffset RefreshTokenExpiresAt, CurrentUserDto User, DateTimeOffset CreatedAt, DateTimeOffset LastSeenAt, string? ClientIp);

    private string PathFor(string id, string kind)
    {
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(id)))[..40];
        return Path.Combine(_dir, $"{hash}.{kind}");
    }

    public void SaveSession(UserSession s) => Write(PathFor(s.Id, "session"), JsonSerializer.SerializeToUtf8Bytes(
        new SessionRecord(s.Id, s.AccessToken, s.AccessTokenExpiresAt, s.RefreshToken, s.RefreshTokenExpiresAt, s.User, s.CreatedAt, s.LastSeenAt, s.ClientIp)));

    public UserSession? LoadSession(string id)
    {
        var bytes = Read(PathFor(id, "session"));
        if (bytes is null) return null;
        var r = JsonSerializer.Deserialize<SessionRecord>(bytes);
        if (r is null || !string.Equals(r.Id, id, StringComparison.Ordinal)) return null;
        return new UserSession
        {
            Id = r.Id, User = r.User, CreatedAt = r.CreatedAt, LastSeenAt = r.LastSeenAt, ClientIp = r.ClientIp,
            AccessToken = r.AccessToken, AccessTokenExpiresAt = r.AccessTokenExpiresAt, RefreshToken = r.RefreshToken, RefreshTokenExpiresAt = r.RefreshTokenExpiresAt,
        };
    }

    public void SaveTicket(string id, AuthenticationTicket ticket) => Write(PathFor(id, "ticket"), TicketSerializer.Default.Serialize(ticket));

    public AuthenticationTicket? LoadTicket(string id) => Read(PathFor(id, "ticket")) is { } bytes ? TicketSerializer.Default.Deserialize(bytes) : null;

    public void Delete(string id)
    {
        TryDelete(PathFor(id, "session"));
        TryDelete(PathFor(id, "ticket"));
    }

    public int PurgeExpired(DateTimeOffset now)
    {
        var removed = 0;
        foreach (var file in Directory.EnumerateFiles(_dir, "*.session"))
        {
            SessionRecord? r = null;
            try { r = Read(file) is { } b ? JsonSerializer.Deserialize<SessionRecord>(b) : null; }
            catch (JsonException) { }
            if (r is not null && r.RefreshTokenExpiresAt > now) continue;
            TryDelete(file);
            TryDelete(Path.ChangeExtension(file, ".ticket"));
            removed++;
        }
        // A ticket whose session is gone is useless.
        foreach (var file in Directory.EnumerateFiles(_dir, "*.ticket"))
        {
            if (!File.Exists(Path.ChangeExtension(file, ".session"))) { TryDelete(file); removed++; }
        }
        foreach (var tmp in Directory.EnumerateFiles(_dir, "*.tmp")) TryDelete(tmp);
        return removed;
    }

    private void Write(string path, byte[] plaintext)
    {
        var tmp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var fs = new FileStream(tmp, new FileStreamOptions
            {
                Mode = FileMode.CreateNew, Access = FileAccess.Write,
                UnixCreateMode = OperatingSystem.IsWindows() ? null : UnixFileMode.UserRead | UnixFileMode.UserWrite,
            }))
            {
                fs.Write(_protector.Protect(plaintext));
            }
            File.Move(tmp, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Persistence is best effort: the session still works in memory until the next restart.
            _logger.LogWarning(ex, "Could not persist a session file");
            TryDelete(tmp);
        }
    }

    private byte[]? Read(string path)
    {
        byte[] protectedBytes;
        try { protectedBytes = File.ReadAllBytes(path); }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) { return null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not read a session file");
            return null;
        }
        try { return _protector.Unprotect(protectedBytes); }
        catch (CryptographicException)
        {
            // Tampered with, or written under a key ring that no longer exists: never trust it.
            _logger.LogWarning("Discarded a session file that could not be decrypted");
            TryDelete(path);
            return null;
        }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}

/// <summary>
/// Encrypts the Data Protection key ring at rest (AES-256-GCM) with a key-encryption key held outside the volume (an
/// OpenShift/Kubernetes Secret, <c>DataProtection:KeyEncryptionKey</c>, 32 bytes base64). A copy of the volume alone
/// therefore cannot decrypt the cookies or the session files.
/// </summary>
public sealed class AesGcmXmlEncryptor(byte[] keyEncryptionKey) : IXmlEncryptor
{
    public EncryptedXmlInfo Encrypt(XElement plaintextElement)
    {
        var plaintext = Encoding.UTF8.GetBytes(plaintextElement.ToString(SaveOptions.DisableFormatting));
        var nonce = RandomNumberGenerator.GetBytes(12);
        var tag = new byte[16];
        var ciphertext = new byte[plaintext.Length];
        using (var aes = new AesGcm(keyEncryptionKey, 16)) aes.Encrypt(nonce, plaintext, ciphertext, tag);
        var element = new XElement("encryptedKey", new XAttribute("algorithm", "AES-256-GCM"),
            new XElement("value", Convert.ToBase64String([.. nonce, .. tag, .. ciphertext])));
        return new EncryptedXmlInfo(element, typeof(AesGcmXmlDecryptor));
    }

    public static byte[]? ParseKey(string? base64)
    {
        if (string.IsNullOrWhiteSpace(base64)) return null;
        var key = Convert.FromBase64String(base64.Trim());
        return key.Length == 32 ? key : throw new InvalidOperationException("DataProtection:KeyEncryptionKey must be 32 bytes, base64-encoded.");
    }
}

public sealed class AesGcmXmlDecryptor(IServiceProvider services) : IXmlDecryptor
{
    public XElement Decrypt(XElement encryptedElement)
    {
        var key = AesGcmXmlEncryptor.ParseKey(services.GetRequiredService<IConfiguration>()["DataProtection:KeyEncryptionKey"])
            ?? throw new InvalidOperationException("The Data Protection key ring is encrypted but DataProtection:KeyEncryptionKey is not set.");
        var blob = Convert.FromBase64String(encryptedElement.Element("value")?.Value ?? throw new FormatException("Encrypted key has no value."));
        var nonce = blob.AsSpan(0, 12);
        var tag = blob.AsSpan(12, 16);
        var ciphertext = blob.AsSpan(28);
        var plaintext = new byte[ciphertext.Length];
        using (var aes = new AesGcm(key, 16)) aes.Decrypt(nonce, ciphertext, tag, plaintext);
        return XElement.Parse(Encoding.UTF8.GetString(plaintext));
    }
}
