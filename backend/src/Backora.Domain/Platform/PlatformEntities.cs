using Backora.Domain.Common;

namespace Backora.Domain.Platform;

public sealed class SmtpSettings : Entity
{
    public string Host { get; set; } = string.Empty;
    public int Port { get; set; } = 587;
    public string? Username { get; set; }
    public string? PasswordCiphertext { get; set; }
    public string FromAddress { get; set; } = string.Empty;
    public string? FromName { get; set; } = "Backora";

    /// <summary>None | StartTls | SslOnConnect. Certificate validation is never disabled.</summary>
    public string Security { get; set; } = "StartTls";

    /// <summary>HTML template with {{placeholders}}; null uses the built-in template.</summary>
    public string? Template { get; set; }
    public bool Enabled { get; set; } = true;
}

public sealed class TeamsSettings : Entity
{
    public string? WebhookUrlCiphertext { get; set; }

    /// <summary>JSON card template with {{placeholders}}; null uses the built-in card.</summary>
    public string? Template { get; set; }
    public bool Enabled { get; set; } = true;
}

public sealed class NotificationSettings : Entity
{
    public List<string> Recipients { get; set; } = [];
    public bool NotifyOnBackupFailure { get; set; } = true;
    public bool NotifyOnOffloadFailure { get; set; } = true;
    public bool NotifyOnRestoreFailure { get; set; } = true;
    public bool NotifyOnRestoreCompleted { get; set; } = true;
    public bool NotifyOnBackupSuccess { get; set; }
}

/// <summary>Notification outbox entry; dispatched with retries by the worker.</summary>
public sealed class NotificationMessage : Entity
{
    public NotificationChannel Channel { get; set; }
    public string EventType { get; set; } = string.Empty;
    public string? Subject { get; set; }

    /// <summary>Placeholder values captured at event time (JSON object).</summary>
    public string PayloadJson { get; set; } = "{}";
    public List<string> Recipients { get; set; } = [];

    public NotificationStatus Status { get; set; } = NotificationStatus.Pending;
    public int Attempts { get; set; }
    public DateTimeOffset NextAttemptAt { get; set; }
    public string? LastError { get; set; }
    public DateTimeOffset? SentAt { get; set; }
    public string? RelatedEntityType { get; set; }
    public long? RelatedEntityId { get; set; }
    public string? CorrelationId { get; set; }
}

/// <summary>Append-only audit record. The application DB role cannot UPDATE/DELETE this table.</summary>
public sealed class AuditEvent
{
    public long Id { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
    public long? ActorUserId { get; set; }
    public string ActorName { get; set; } = "system";
    public string? ActorIp { get; set; }
    public string Action { get; set; } = string.Empty;
    public string? ResourceType { get; set; }
    public string? ResourceId { get; set; }
    public AuditOutcome Outcome { get; set; }
    public string? CorrelationId { get; set; }
    public string? DetailsJson { get; set; }
}

public sealed class ApiToken : Entity
{
    public long UserId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string TokenHash { get; set; } = string.Empty;
    public string Prefix { get; set; } = string.Empty;
    public DateTimeOffset? ExpiresAt { get; set; }
    public DateTimeOffset? LastUsedAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }

    public bool IsActive(DateTimeOffset now) => RevokedAt is null && (ExpiresAt is null || ExpiresAt > now);
}

public sealed class RefreshToken
{
    public long Id { get; set; }
    public long UserId { get; set; }
    public string TokenHash { get; set; } = string.Empty;
    public string FamilyId { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
    public string? ReplacedByHash { get; set; }
    public string? CreatedByIp { get; set; }
    public string? UserAgent { get; set; }
}

public sealed class IdempotencyRecord
{
    public long Id { get; set; }
    public long UserId { get; set; }
    public string Scope { get; set; } = string.Empty;
    public string Key { get; set; } = string.Empty;
    public string? ResourceType { get; set; }
    public long? ResourceId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class WorkerHeartbeat
{
    public string WorkerId { get; set; } = string.Empty;
    public string HostName { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset LastSeenAt { get; set; }
    public int ActiveOperations { get; set; }
}

/// <summary>Singleton coordination lock for cluster-wide periodic tasks (scheduler, retention).</summary>
public sealed class ClusterLock
{
    public string Name { get; set; } = string.Empty;
    public string? Owner { get; set; }
    public DateTimeOffset? ExpiresAt { get; set; }
    public DateTimeOffset? LastRunAt { get; set; }
}

/// <summary>Ownership lease of a background operation (kind = backup | offload | restore).</summary>
public sealed class OperationLease
{
    public string Kind { get; set; } = string.Empty;
    public long OperationId { get; set; }
    public string Owner { get; set; } = string.Empty;
    public DateTimeOffset AcquiredAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset HeartbeatAt { get; set; }
}
