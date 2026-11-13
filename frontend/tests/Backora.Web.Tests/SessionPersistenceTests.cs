using System.Security.Claims;
using Backora.Contracts.V1.Auth;
using Backora.Web.Auth;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Backora.Web.Tests;

/// <summary>
/// A frontend restart or redeploy must not sign users out: sessions and sign-in tickets survive on disk — encrypted,
/// never readable without the key ring, never accepted when tampered with — and expire as before.
/// </summary>
public sealed class SessionPersistenceTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("bko-sessions-").FullName;
    private static readonly CurrentUserDto User = new(1, "admin@example.com", "Admin", ["PlatformAdministrator"], ["dashboard.read"], false, false, null);

    // The same key ring before and after the "restart", as the persistent volume provides.
    private IDataProtectionProvider Keys() => DataProtectionProvider.Create(new DirectoryInfo(Path.Combine(_root, "keys")));
    private FileSessionPersistence Disk() => new(Path.Combine(_root, "sessions"), Keys(), NullLogger<FileSessionPersistence>.Instance);
    private static TokenResponse Tokens(string refresh = "refresh-1", int hours = 12) =>
        new("access-1", DateTimeOffset.UtcNow.AddMinutes(15), refresh, DateTimeOffset.UtcNow.AddHours(hours), User);

    [Fact]
    public void A_session_survives_a_restart()
    {
        var before = new SessionStore(TimeProvider.System, Disk());
        var session = before.Create(Tokens());
        session.ClientIp = "203.0.113.7";
        before.Persist(session);

        var after = new SessionStore(TimeProvider.System, Disk());   // a new process
        var restored = after.Get(session.Id);

        Assert.NotNull(restored);
        Assert.Equal("access-1", restored!.AccessToken);
        Assert.Equal("refresh-1", restored.RefreshToken);
        Assert.Equal("admin@example.com", restored.User.Email);
        Assert.Equal("203.0.113.7", restored.ClientIp);
    }

    [Fact]
    public async Task The_sign_in_ticket_survives_a_restart()
    {
        var sessions = new SessionStore(TimeProvider.System, Disk());
        var session = sessions.Create(Tokens());
        var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim(BffClaims.SessionId, session.Id), new Claim(ClaimTypes.Name, "Admin")], BffClaims.Scheme));
        var tickets = new MemoryTicketStore(sessions, TimeProvider.System, Disk());
        var key = await tickets.StoreAsync(new AuthenticationTicket(principal, new AuthenticationProperties { ExpiresUtc = DateTimeOffset.UtcNow.AddHours(1) }, BffClaims.Scheme));

        var restartedSessions = new SessionStore(TimeProvider.System, Disk());
        var restartedTickets = new MemoryTicketStore(restartedSessions, TimeProvider.System, Disk());
        var ticket = await restartedTickets.RetrieveAsync(key);

        Assert.NotNull(ticket);
        Assert.Equal("Admin", ticket!.Principal.Identity!.Name);
    }

    [Fact]
    public void Rotated_tokens_are_what_comes_back()
    {
        var store = new SessionStore(TimeProvider.System, Disk());
        var session = store.Create(Tokens());
        session.Apply(Tokens(refresh: "refresh-2"));
        store.Persist(session);

        Assert.Equal("refresh-2", new SessionStore(TimeProvider.System, Disk()).Get(session.Id)!.RefreshToken);
    }

    [Fact]
    public void Nothing_readable_lands_on_disk_and_a_tampered_file_is_refused()
    {
        var store = new SessionStore(TimeProvider.System, Disk());
        var session = store.Create(Tokens());

        var files = Directory.GetFiles(Path.Combine(_root, "sessions"));
        Assert.Single(files);
        Assert.DoesNotContain(session.Id, Path.GetFileName(files[0]));   // the cookie's value is not the file name
        var raw = File.ReadAllBytes(files[0]);
        Assert.DoesNotContain("refresh-1", System.Text.Encoding.UTF8.GetString(raw));
        Assert.DoesNotContain("admin@example.com", System.Text.Encoding.UTF8.GetString(raw));
        if (!OperatingSystem.IsWindows()) Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(files[0]));

        raw[^5] ^= 0x01;
        File.WriteAllBytes(files[0], raw);
        Assert.Null(new SessionStore(TimeProvider.System, Disk()).Get(session.Id));
        Assert.False(File.Exists(files[0]), "a file that does not decrypt is deleted, never trusted");
    }

    [Fact]
    public void Without_the_key_ring_a_copy_of_the_sessions_is_useless()
    {
        var session = new SessionStore(TimeProvider.System, Disk()).Create(Tokens());
        var otherKeys = DataProtectionProvider.Create(new DirectoryInfo(Path.Combine(_root, "other-keys")));
        var stolen = new FileSessionPersistence(Path.Combine(_root, "sessions"), otherKeys, NullLogger<FileSessionPersistence>.Instance);
        Assert.Null(stolen.LoadSession(session.Id));
    }

    [Fact]
    public void Expired_sessions_are_purged_from_disk_and_sign_out_removes_them()
    {
        var store = new SessionStore(TimeProvider.System, Disk());
        var expired = store.Create(Tokens(hours: -1));
        var live = store.Create(Tokens());
        var gone = store.Create(Tokens());

        store.Remove(gone.Id);
        new SessionStore(TimeProvider.System, Disk()).PurgeExpired();

        var restarted = new SessionStore(TimeProvider.System, Disk());
        Assert.Null(restarted.Get(expired.Id));
        Assert.Null(restarted.Get(gone.Id));
        Assert.NotNull(restarted.Get(live.Id));
        Assert.Single(Directory.GetFiles(Path.Combine(_root, "sessions"), "*.session"));
    }

    [Fact]
    public void The_key_ring_is_encrypted_at_rest_and_only_the_right_key_opens_it()
    {
        var kek = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);
        var element = new System.Xml.Linq.XElement("key", new System.Xml.Linq.XElement("secret", "master-key-material"));
        var encrypted = new AesGcmXmlEncryptor(kek).Encrypt(element);
        Assert.DoesNotContain("master-key-material", encrypted.EncryptedElement.ToString());

        IServiceProvider With(byte[] key) => new ServiceCollection().AddSingleton<IConfiguration>(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["DataProtection:KeyEncryptionKey"] = Convert.ToBase64String(key) }).Build()).BuildServiceProvider();
        Assert.Equal("master-key-material", new AesGcmXmlDecryptor(With(kek)).Decrypt(encrypted.EncryptedElement).Element("secret")!.Value);
        Assert.ThrowsAny<System.Security.Cryptography.CryptographicException>(() =>
            new AesGcmXmlDecryptor(With(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32))).Decrypt(encrypted.EncryptedElement));
        Assert.Throws<InvalidOperationException>(() => AesGcmXmlEncryptor.ParseKey(Convert.ToBase64String(new byte[16])));
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
