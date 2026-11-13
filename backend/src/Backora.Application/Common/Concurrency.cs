using Backora.Domain.Common;

namespace Backora.Application.Common;

public static class Concurrency
{
    /// <summary>Rejects updates made against a stale version (when the client supplied one).</summary>
    public static void EnsureVersion(Entity entity, uint? expected)
    {
        if (expected is { } v && v != entity.Version) throw new ConcurrencyException();
    }
}
