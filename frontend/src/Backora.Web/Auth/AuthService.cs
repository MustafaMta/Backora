using System.Net.Http.Json;
using System.Security.Claims;
using Backora.Contracts.V1.Auth;
using Backora.Web.Api;
using Microsoft.AspNetCore.Authentication;

namespace Backora.Web.Auth;

/// <summary>Anonymous backend calls used by the BFF authentication endpoints and pages.</summary>
public sealed class AuthService(IHttpClientFactory factory, SessionStore sessions, ILogger<AuthService> logger)
{
    private HttpClient Client => factory.CreateClient(BffClaims.BackendAnonymousClient);

    public Task<ApiResult<LoginResponse>> LoginAsync(string email, string password, CancellationToken ct = default) =>
        PostAsync<LoginResponse>("/api/v1/auth/login", new LoginRequest(email, password), ct);

    public Task<ApiResult<TokenResponse>> TwoFactorAsync(string twoFactorToken, string code, bool recovery, CancellationToken ct = default) =>
        PostAsync<TokenResponse>("/api/v1/auth/login/2fa", new TwoFactorLoginRequest(twoFactorToken, code, recovery), ct);

    public async Task<ApiResult> ForgotPasswordAsync(string email, CancellationToken ct = default) =>
        await PostAsync<object>("/api/v1/auth/password/forgot", new ForgotPasswordRequest(email), ct);

    public async Task<ApiResult> ResetPasswordAsync(string email, string token, string password, CancellationToken ct = default) =>
        await PostAsync<object>("/api/v1/auth/password/reset", new ResetPasswordRequest(email, token, password), ct);

    /// <summary>Revokes the backend refresh-token family. Best effort: local sign-out happens regardless.</summary>
    public async Task LogoutAsync(UserSession session, CancellationToken ct = default)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/logout")
            {
                Content = JsonContent.Create(new LogoutRequest(session.RefreshToken), options: ApiJson.Options),
            };
            request.Headers.Authorization = new("Bearer", session.AccessToken);
            using var response = await Client.SendAsync(request, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            logger.LogWarning(ex, "Backend logout call failed; continuing with local sign-out");
        }
    }

    /// <summary>Creates a server-side session for the tokens and issues the session cookie.</summary>
    public async Task<UserSession> SignInAsync(HttpContext http, TokenResponse tokens)
    {
        var session = sessions.Create(tokens);
        session.ClientIp = http.Connection.RemoteIpAddress?.ToString();
        var identity = new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, tokens.User.Id.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            new Claim(ClaimTypes.Name, tokens.User.Name),
            new Claim(ClaimTypes.Email, tokens.User.Email),
            new Claim(BffClaims.SessionId, session.Id),
        ], BffClaims.Scheme, ClaimTypes.Name, ClaimTypes.Role);
        await http.SignInAsync(BffClaims.Scheme, new ClaimsPrincipal(identity), new AuthenticationProperties { IsPersistent = false, AllowRefresh = true });
        return session;
    }

    private async Task<ApiResult<T>> PostAsync<T>(string path, object body, CancellationToken ct)
    {
        try
        {
            using var response = await Client.PostAsJsonAsync(path, body, ApiJson.Options, ct);
            var correlation = response.Headers.TryGetValues("X-Correlation-ID", out var v) ? v.FirstOrDefault() : null;
            if (response.IsSuccessStatusCode)
            {
                T? value = default;
                if (typeof(T) != typeof(object) && response.StatusCode != System.Net.HttpStatusCode.NoContent)
                {
                    value = await response.Content.ReadFromJsonAsync<T>(ApiJson.Options, ct);
                }
                return new ApiResult<T> { Outcome = ApiOutcome.Success, StatusCode = (int)response.StatusCode, Value = value };
            }
            var problem = ApiProblem.Parse(await response.Content.ReadAsStringAsync(ct), correlation);
            return new ApiResult<T> { Outcome = ApiResult.Classify(response.StatusCode, problem), StatusCode = (int)response.StatusCode, Problem = problem };
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            logger.LogWarning(ex, "Backend auth call {Path} failed", path);
            return ApiResult<T>.From(ApiResult.Network());
        }
    }
}

public static class ReturnUrl
{
    /// <summary>Accepts only local, rooted paths to prevent open redirects.</summary>
    public static string Sanitize(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return "/";
        url = url.Trim();
        if (!url.StartsWith('/') || url.StartsWith("//", StringComparison.Ordinal) || url.StartsWith("/\\", StringComparison.Ordinal)) return "/";
        if (url.Any(char.IsControl)) return "/";
        if (url.StartsWith("/login", StringComparison.OrdinalIgnoreCase) || url.StartsWith("/account/", StringComparison.OrdinalIgnoreCase)) return "/";
        return url;
    }
}
