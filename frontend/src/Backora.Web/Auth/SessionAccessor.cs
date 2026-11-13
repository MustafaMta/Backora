using Microsoft.AspNetCore.Components.Authorization;

namespace Backora.Web.Auth;

/// <summary>
/// Resolves the current session id inside both SSR requests and interactive circuits. Circuits have no
/// HttpContext, but the circuit's AuthenticationState is the cookie principal captured at connection time.
/// </summary>
public class SessionAccessor(AuthenticationStateProvider? authState, IHttpContextAccessor httpContext)
{
    private string? _override;

    /// <summary>Used by SSR endpoints that create a session in the same request.</summary>
    public void Set(string sessionId) => _override = sessionId;

    public virtual async Task<string?> GetSessionIdAsync()
    {
        if (_override is not null) return _override;
        var ctxUser = httpContext.HttpContext?.User;
        if (ctxUser?.Identity?.IsAuthenticated == true) return ctxUser.FindFirst(BffClaims.SessionId)?.Value;
        if (authState is null) return null;
        try
        {
            var state = await authState.GetAuthenticationStateAsync();
            return state.User.FindFirst(BffClaims.SessionId)?.Value;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }
}
