using Backora.Domain.Common;

namespace Backora.Domain.Backups;

/// <summary>Bash backup script with {PLACEHOLDER} tokens (legacy: templates).</summary>
public sealed class ScriptTemplate : Entity
{
    public string Name { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public string Script { get; set; } = string.Empty;
    public string? Description { get; set; }

    /// <summary>Incremented on every script change, recorded on each run for traceability.</summary>
    public int Revision { get; set; } = 1;
    public long? UpdatedByUserId { get; set; }
}
