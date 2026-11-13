namespace Backora.Web.Auth;

public sealed class BackendOptions
{
    public const string Section = "Backend";
    /// <summary>Base URL of the Backora API (no trailing /api/v1).</summary>
    public string BaseUrl { get; set; } = "http://localhost:5080";
    public int TimeoutSeconds { get; set; } = 30;
}

public sealed class FrontendOptions
{
    public const string Section = "Frontend";
    /// <summary>Public URL users reach the frontend at; used to build password reset links.</summary>
    public string PublicUrl { get; set; } = "http://localhost:8444";
    /// <summary>Idle timeout of the browser session (sliding).</summary>
    public int SessionIdleMinutes { get; set; } = 60;
    /// <summary>
    /// Read-only review mode for automated visual checks: every state-changing API call is refused inside the
    /// frontend before it reaches the backend (only side-effect-free previews and checks are allowed).
    /// </summary>
    public bool ReadOnlyReview { get; set; }
}

public static class BffClaims
{
    public const string SessionId = "bko:sid";
    public const string Scheme = "BackoraBff";
    public const string CookieName = "__Host-backora";
    public const string TwoFactorCookie = "__Host-backora-2fa";
    public const string BackendAnonymousClient = "backend-anon";
}
