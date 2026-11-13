namespace Backora.Domain.Common;

/// <summary>Base type for persistent aggregates. Identifiers are 64-bit to preserve legacy Laravel ids.</summary>
public abstract class Entity
{
    public long Id { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>PostgreSQL <c>xmin</c> system column used as an optimistic concurrency token.</summary>
    public uint Version { get; set; }
}

public sealed class DomainException : Exception
{
    public DomainException(string code, string message) : base(message) => Code = code;
    public string Code { get; }
}

public sealed class InvalidStateTransitionException : Exception
{
    public InvalidStateTransitionException(string entity, object from, object to)
        : base($"{entity} cannot transition from {from} to {to}.")
    {
        From = from.ToString() ?? string.Empty;
        To = to.ToString() ?? string.Empty;
    }

    public string From { get; }
    public string To { get; }
}
