using Backora.Contracts.V1.Auth;
using Backora.Web.Api;

namespace Backora.Web.Auth;

/// <summary>Current user for UI gating (per circuit). The backend still enforces every permission.</summary>
public class UserContext(SessionAccessor accessor, SessionStore store, IServiceProvider services)
{
    private CurrentUserDto? _user;
    private HashSet<string> _permissions = new(StringComparer.Ordinal);

    public CurrentUserDto? User => _user;
    public bool IsAuthenticated => _user is not null;
    public event Action? Changed;

    public virtual async Task<CurrentUserDto?> EnsureLoadedAsync()
    {
        if (_user is not null) return _user;
        var session = store.Get(await accessor.GetSessionIdAsync());
        if (session is not null) Set(session.User);
        return _user;
    }

    public virtual bool Can(string permission) => _permissions.Contains(permission);

    public bool CanAny(params string[] permissions) => permissions.Any(Can);

    /// <summary>Reloads the profile from the API (after profile or role changes).</summary>
    public async Task RefreshAsync()
    {
        var me = services.GetRequiredService<MeClient>();
        var result = await me.GetAsync();
        if (result is { Ok: true, Value: not null })
        {
            var session = store.Get(await accessor.GetSessionIdAsync());
            if (session is not null) session.User = result.Value;
            Set(result.Value);
        }
    }

    /// <summary>
    /// Forces the next API call to rotate tokens so claims baked into the access token (roles, password-change
    /// gate) reflect the latest server state, e.g. right after a password change.
    /// </summary>
    public async Task InvalidateAccessTokenAsync()
    {
        var session = store.Get(await accessor.GetSessionIdAsync());
        if (session is not null) session.AccessTokenExpiresAt = DateTimeOffset.MinValue;
    }

    public void Set(CurrentUserDto user)
    {
        _user = user;
        _permissions = new HashSet<string>(user.Permissions, StringComparer.Ordinal);
        Changed?.Invoke();
    }
}
