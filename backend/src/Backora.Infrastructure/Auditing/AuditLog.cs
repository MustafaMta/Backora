using System.Text.Json;
using Backora.Application.Abstractions;
using Backora.Domain;
using Backora.Domain.Platform;
using Backora.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Backora.Infrastructure.Auditing;

public sealed class AuditLog(BackoraDbContext db, ICurrentUser user, IClock clock, IServiceScopeFactory scopes) : IAuditLog
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public void Record(string action, string? resourceType, string? resourceId, AuditOutcome outcome = AuditOutcome.Success, object? details = null) =>
        db.AuditEvents.Add(Create(action, resourceType, resourceId, outcome, details));

    public async Task RecordNowAsync(string action, string? resourceType, string? resourceId, AuditOutcome outcome, object? details = null, CancellationToken ct = default)
    {
        // Separate scope/context so the record survives a rollback of the caller's unit of work.
        await using var scope = scopes.CreateAsyncScope();
        var ctx = scope.ServiceProvider.GetRequiredService<BackoraDbContext>();
        ctx.AuditEvents.Add(Create(action, resourceType, resourceId, outcome, details));
        await ctx.SaveChangesAsync(ct);
    }

    private AuditEvent Create(string action, string? resourceType, string? resourceId, AuditOutcome outcome, object? details) => new()
    {
        OccurredAt = clock.UtcNow,
        ActorUserId = user.UserId,
        ActorName = user.IsAuthenticated ? user.Name : "system",
        ActorIp = user.IpAddress,
        Action = action,
        ResourceType = resourceType,
        ResourceId = resourceId,
        Outcome = outcome,
        CorrelationId = user.CorrelationId,
        DetailsJson = details is null ? null : JsonSerializer.Serialize(details, Json),
    };
}

/// <summary>Identity used by background services.</summary>
public sealed class SystemUser : ICurrentUser
{
    public long? UserId => null;
    public string Name => "system";
    public string? IpAddress => null;
    public string? CorrelationId => System.Diagnostics.Activity.Current?.TraceId.ToString();
    public bool IsAuthenticated => false;
    public bool HasPermission(string permission) => true;
    public IReadOnlyCollection<string> Roles => [];
}
