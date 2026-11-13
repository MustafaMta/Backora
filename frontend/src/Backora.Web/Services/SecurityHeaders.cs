namespace Backora.Web.Services;

/// <summary>Strict security headers. Scripts are self-hosted only; inline style attributes are allowed for computed widths.</summary>
public static class SecurityHeaders
{
    /// <summary>Set in HttpContext.Items by endpoints that emit their own, stricter CSP (e.g. sandboxed previews).</summary>
    public const string CustomPolicyKey = "bko:custom-csp";

    public static IApplicationBuilder UseBackoraSecurityHeaders(this IApplicationBuilder app, bool isDevelopment) =>
        app.Use(async (context, next) =>
        {
            context.Response.OnStarting(() =>
            {
                var h = context.Response.Headers;
                if (context.Items.ContainsKey(CustomPolicyKey))
                {
                    h["X-Content-Type-Options"] = "nosniff";
                    h["Referrer-Policy"] = "no-referrer";
                    return Task.CompletedTask;
                }
                var host = context.Request.Host.Value;
                var connect = $"'self' wss://{host}" + (isDevelopment ? $" ws://{host} ws://localhost:* wss://localhost:*" : string.Empty);
                h["Content-Security-Policy"] =
                    "default-src 'self'; " +
                    "script-src 'self'; " +
                    "style-src 'self'; style-src-attr 'unsafe-inline'; " +
                    "img-src 'self' data:; " +
                    "font-src 'self'; " +
                    $"connect-src {connect}; " +
                    "frame-src 'self'; " +
                    "object-src 'none'; base-uri 'self'; form-action 'self'; frame-ancestors 'none'";
                h["X-Content-Type-Options"] = "nosniff";
                h["Referrer-Policy"] = "strict-origin-when-cross-origin";
                h["X-Frame-Options"] = "DENY";
                h["Permissions-Policy"] = "camera=(), microphone=(), geolocation=(), payment=(), usb=(), interest-cohort=()";
                h["Cross-Origin-Opener-Policy"] = "same-origin";
                h["Cross-Origin-Resource-Policy"] = "same-origin";
                return Task.CompletedTask;
            });
            await next();
        });
}
