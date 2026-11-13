using Microsoft.Extensions.Caching.Memory;

namespace Backora.Web.Services;

/// <summary>
/// Holds rendered email previews briefly so they can be shown in a sandboxed iframe served by a dedicated
/// endpoint with its own restrictive CSP (no scripts), instead of injecting untrusted HTML into the app DOM.
/// </summary>
public sealed class PreviewStore(IMemoryCache cache)
{
    public string Put(string ownerSessionId, string html)
    {
        var id = Guid.NewGuid().ToString("N");
        cache.Set("preview:" + id, (ownerSessionId, html), TimeSpan.FromMinutes(10));
        return id;
    }

    public string? Get(string id, string? ownerSessionId)
    {
        if (ownerSessionId is null || !cache.TryGetValue("preview:" + id, out (string Owner, string Html) entry)) return null;
        return entry.Owner == ownerSessionId ? entry.Html : null;
    }
}
