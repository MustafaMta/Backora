using System.Net;
using System.Net.Http.Headers;

namespace Backora.Web.Auth;

/// <summary>
/// Attaches the current session's access token, refreshes proactively before expiry and reactively on
/// 401 (one retry). When the session cannot be refreshed it is discarded and a 401 is returned so the UI
/// sends the user to the login page.
/// </summary>
public sealed class TokenHandler(SessionAccessor accessor, SessionStore store, ITokenRefresher refresher) : DelegatingHandler
{
    public const string SessionExpiredHeader = "X-Bko-Session-Expired";

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var sid = await accessor.GetSessionIdAsync();
        var session = store.Get(sid);
        if (session is null) return Expired(request);

        if (!await refresher.EnsureFreshAsync(session, force: false, staleAccessToken: null, cancellationToken))
        {
            store.Remove(session.Id);
            return Expired(request);
        }

        // Buffer the body so the request can be replayed after a reactive refresh.
        byte[]? body = null;
        MediaTypeHeaderValue? contentType = null;
        if (request.Content is not null)
        {
            body = await request.Content.ReadAsByteArrayAsync(cancellationToken);
            contentType = request.Content.Headers.ContentType;
            request.Content = new ByteArrayContent(body);
            if (contentType is not null) request.Content.Headers.ContentType = contentType;
        }

        if (!string.IsNullOrEmpty(session.ClientIp))
        {
            request.Headers.Remove(ForwardClientIpHandler.Header);
            request.Headers.TryAddWithoutValidation(ForwardClientIpHandler.Header, session.ClientIp);
        }
        var token = session.AccessToken;
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        session.LastSeenAt = DateTimeOffset.UtcNow;
        var response = await base.SendAsync(request, cancellationToken);
        if (response.StatusCode != HttpStatusCode.Unauthorized) return response;

        response.Dispose();
        if (!await refresher.EnsureFreshAsync(session, force: true, staleAccessToken: token, cancellationToken))
        {
            store.Remove(session.Id);
            return Expired(request);
        }

        using var retry = new HttpRequestMessage(request.Method, request.RequestUri);
        foreach (var h in request.Headers.Where(h => h.Key != "Authorization")) retry.Headers.TryAddWithoutValidation(h.Key, h.Value);
        if (body is not null)
        {
            retry.Content = new ByteArrayContent(body);
            if (contentType is not null) retry.Content.Headers.ContentType = contentType;
        }
        retry.Headers.Authorization = new AuthenticationHeaderValue("Bearer", session.AccessToken);
        return await base.SendAsync(retry, cancellationToken);
    }

    private static HttpResponseMessage Expired(HttpRequestMessage request)
    {
        var r = new HttpResponseMessage(HttpStatusCode.Unauthorized) { RequestMessage = request, Content = new StringContent(string.Empty) };
        r.Headers.TryAddWithoutValidation(SessionExpiredHeader, "1");
        return r;
    }
}

/// <summary>
/// For anonymous calls made inside an HTTP request (login, 2FA, password reset): forwards the end-user IP
/// so the API's audit trail and rate limits see the real client, not the frontend container.
/// </summary>
public sealed class ForwardClientIpHandler(IHttpContextAccessor accessor) : DelegatingHandler
{
    public const string Header = "X-Forwarded-For";

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (accessor.HttpContext?.Connection.RemoteIpAddress is { } ip)
        {
            request.Headers.Remove(Header);
            request.Headers.TryAddWithoutValidation(Header, ip.ToString());
        }
        return base.SendAsync(request, cancellationToken);
    }
}

/// <summary>Shared pooled transport for all backend calls.</summary>
public sealed class BackendTransport : IDisposable
{
    public BackendTransport(Microsoft.Extensions.Options.IOptions<BackendOptions> options)
    {
        Options = options.Value;
        Handler = new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            AutomaticDecompression = DecompressionMethods.All,
        };
    }

    public BackendOptions Options { get; }
    public HttpMessageHandler Handler { get; }
    public void Dispose() => Handler.Dispose();
}

/// <summary>
/// Scoped HttpClient bound to the current user's session. Created per scope (per circuit) instead of via
/// IHttpClientFactory because factory handlers live in a separate DI scope and could not see the circuit's
/// authentication state.
/// </summary>
public sealed class BackendHttp : IDisposable
{
    public BackendHttp(BackendTransport transport, SessionAccessor accessor, SessionStore store, ITokenRefresher refresher)
        : this(new TokenHandler(accessor, store, refresher) { InnerHandler = transport.Handler }, transport.Options) { }

    public BackendHttp(HttpMessageHandler handler, BackendOptions options)
    {
        Client = new HttpClient(handler, disposeHandler: false)
        {
            BaseAddress = new Uri(options.BaseUrl.TrimEnd('/') + "/"),
            Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds),
        };
    }

    public HttpClient Client { get; }
    public void Dispose() => Client.Dispose();
}
