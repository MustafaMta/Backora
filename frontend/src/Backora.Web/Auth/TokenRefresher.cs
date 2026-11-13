using System.Net.Http.Json;
using Backora.Contracts.V1.Auth;
using Backora.Web.Api;

namespace Backora.Web.Auth;

public interface ITokenRefresher
{
    /// <summary>
    /// Ensures the session has a usable access token. When <paramref name="force"/> is set (after a 401),
    /// a refresh happens unless another request already rotated the token past <paramref name="staleAccessToken"/>.
    /// Returns false when the session can no longer be refreshed.
    /// </summary>
    Task<bool> EnsureFreshAsync(UserSession session, bool force, string? staleAccessToken, CancellationToken ct);
}

public sealed class TokenRefresher(IHttpClientFactory factory, TimeProvider clock, ILogger<TokenRefresher> logger, ISessionPersistence? persistence = null) : ITokenRefresher
{
    public static readonly TimeSpan Skew = TimeSpan.FromSeconds(30);

    public async Task<bool> EnsureFreshAsync(UserSession session, bool force, string? staleAccessToken, CancellationToken ct)
    {
        if (!force && session.AccessTokenExpiresAt - Skew > clock.GetUtcNow()) return true;

        try
        {
            await session.RefreshLock.WaitAsync(ct);
        }
        catch (ObjectDisposedException)
        {
            return false;
        }

        try
        {
            // Another request may have refreshed while we waited.
            if (force ? session.AccessToken != staleAccessToken : session.AccessTokenExpiresAt - Skew > clock.GetUtcNow())
            {
                return true;
            }
            if (session.RefreshTokenExpiresAt <= clock.GetUtcNow()) return false;

            var client = factory.CreateClient(BffClaims.BackendAnonymousClient);
            using var response = await client.PostAsJsonAsync("/api/v1/auth/refresh", new RefreshRequest(session.RefreshToken), ApiJson.Options, ct);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogInformation("Token refresh rejected with {Status}; session ends", (int)response.StatusCode);
                return false;
            }
            var tokens = await response.Content.ReadFromJsonAsync<TokenResponse>(ApiJson.Options, ct);
            if (tokens is null) return false;
            session.Apply(tokens);
            // The rotated refresh token replaces the old one: persist it, or a restart would bring back a dead token.
            persistence?.SaveSession(session);
            return true;
        }
        catch (HttpRequestException ex)
        {
            // Backend unreachable: keep the session; the caller surfaces a network error.
            logger.LogWarning(ex, "Token refresh failed: backend unreachable");
            throw;
        }
        finally
        {
            try { session.RefreshLock.Release(); } catch (ObjectDisposedException) { }
        }
    }
}
