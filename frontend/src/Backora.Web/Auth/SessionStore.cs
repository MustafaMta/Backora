using System.Collections.Concurrent;
using System.Security.Cryptography;
using Backora.Contracts.V1.Auth;

namespace Backora.Web.Auth;

/// <summary>Server-side token session. Backend tokens never leave the frontend server.</summary>
public sealed class UserSession
{
    public required string Id { get; init; }
    public string AccessToken { get; set; } = string.Empty;
    public DateTimeOffset AccessTokenExpiresAt { get; set; }
    public string RefreshToken { get; set; } = string.Empty;
    public DateTimeOffset RefreshTokenExpiresAt { get; set; }
    public required CurrentUserDto User { get; set; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset LastSeenAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>End-user IP captured at sign-in; forwarded to the API as X-Forwarded-For for auditing.</summary>
    public string? ClientIp { get; set; }

    /// <summary>Serializes refreshes so concurrent requests trigger a single rotation.</summary>
    public SemaphoreSlim RefreshLock { get; } = new(1, 1);

    public void Apply(TokenResponse tokens)
    {
        AccessToken = tokens.AccessToken;
        AccessTokenExpiresAt = tokens.AccessTokenExpiresAt;
        RefreshToken = tokens.RefreshToken;
        RefreshTokenExpiresAt = tokens.RefreshTokenExpiresAt;
        User = tokens.User;
    }
}

/// <summary>
/// Session store (singleton): in memory for speed, written through to <see cref="ISessionPersistence"/> so a restart
/// or redeploy keeps users signed in (with persistence configured); a session missing from memory is read back.
/// </summary>
public sealed class SessionStore(TimeProvider clock, ISessionPersistence persistence)
{
    public SessionStore(TimeProvider clock) : this(clock, new NoSessionPersistence()) { }

    private readonly ConcurrentDictionary<string, UserSession> _sessions = new(StringComparer.Ordinal);

    public int Count => _sessions.Count;

    public UserSession Create(TokenResponse tokens)
    {
        var id = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var session = new UserSession { Id = id, User = tokens.User };
        session.Apply(tokens);
        _sessions[id] = session;
        persistence.SaveSession(session);
        return session;
    }

    /// <summary>Records a change to the session (rotated tokens) so it survives a restart.</summary>
    public void Persist(UserSession session) => persistence.SaveSession(session);

    public UserSession? Get(string? id)
    {
        if (id is null) return null;
        if (!_sessions.TryGetValue(id, out var s))
        {
            // After a restart the session is on disk only.
            if (persistence.LoadSession(id) is not { } loaded) return null;
            s = _sessions.GetOrAdd(id, loaded);
        }
        if (s.RefreshTokenExpiresAt <= clock.GetUtcNow())
        {
            Remove(id);
            return null;
        }
        return s;
    }

    public void Remove(string? id)
    {
        if (id is null) return;
        if (_sessions.TryRemove(id, out var s)) s.RefreshLock.Dispose();
        persistence.Delete(id);
    }

    public int PurgeExpired()
    {
        var now = clock.GetUtcNow();
        var removed = 0;
        foreach (var (id, s) in _sessions)
        {
            if (s.RefreshTokenExpiresAt <= now) { Remove(id); removed++; }
        }
        return removed + persistence.PurgeExpired(now);
    }
}

public sealed class SessionCleanupService(SessionStore store, MemoryTicketStore tickets) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(5));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            store.PurgeExpired();
            tickets.PurgeExpired();
        }
    }
}
