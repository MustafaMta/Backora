using System.Collections.Concurrent;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;

namespace Backora.Web.Auth;

/// <summary>
/// Server-side ticket store: the auth cookie carries only an opaque key (the session id). Removing the
/// ticket (sign-out) also discards the backend tokens.
/// </summary>
public sealed class MemoryTicketStore(SessionStore sessions, TimeProvider clock, ISessionPersistence persistence) : ITicketStore
{
    public MemoryTicketStore(SessionStore sessions, TimeProvider clock) : this(sessions, clock, new NoSessionPersistence()) { }

    private readonly ConcurrentDictionary<string, AuthenticationTicket> _tickets = new(StringComparer.Ordinal);

    public Task<string> StoreAsync(AuthenticationTicket ticket)
    {
        var key = ticket.Principal.FindFirst(BffClaims.SessionId)?.Value
            ?? throw new InvalidOperationException("Ticket has no session id claim.");
        _tickets[key] = ticket;
        persistence.SaveTicket(key, ticket);
        return Task.FromResult(key);
    }

    public Task RenewAsync(string key, AuthenticationTicket ticket)
    {
        _tickets[key] = ticket;
        persistence.SaveTicket(key, ticket);
        return Task.CompletedTask;
    }

    public Task<AuthenticationTicket?> RetrieveAsync(string key)
    {
        if (!_tickets.TryGetValue(key, out var ticket))
        {
            // After a restart the ticket is on disk only.
            if (persistence.LoadTicket(key) is not { } loaded) return Task.FromResult<AuthenticationTicket?>(null);
            ticket = _tickets.GetOrAdd(key, loaded);
        }
        if (ticket.Properties.ExpiresUtc is { } exp && exp <= clock.GetUtcNow() || sessions.Get(key) is null)
        {
            _tickets.TryRemove(key, out _);
            return Task.FromResult<AuthenticationTicket?>(null);
        }
        return Task.FromResult<AuthenticationTicket?>(ticket);
    }

    public Task RemoveAsync(string key)
    {
        _tickets.TryRemove(key, out _);
        sessions.Remove(key);
        return Task.CompletedTask;
    }

    public void PurgeExpired()
    {
        var now = clock.GetUtcNow();
        foreach (var (key, t) in _tickets)
        {
            if (t.Properties.ExpiresUtc is { } exp && exp <= now || sessions.Get(key) is null) _tickets.TryRemove(key, out _);
        }
    }
}

public sealed class PostConfigureTicketStore(MemoryTicketStore store) : Microsoft.Extensions.Options.IPostConfigureOptions<CookieAuthenticationOptions>
{
    public void PostConfigure(string? name, CookieAuthenticationOptions options) => options.SessionStore = store;
}
