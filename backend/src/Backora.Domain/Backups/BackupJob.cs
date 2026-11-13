using System.Text.RegularExpressions;
using Backora.Domain.Common;

namespace Backora.Domain.Backups;

/// <summary>Backup policy binding a template, a server, a schedule, retention and a destination (legacy: backup_jobs).</summary>
public sealed partial class BackupJob : Entity
{
    public const int MaxNameLength = 100;

    /// <summary>Unique name. It is also the NFS sub-directory and the S3 prefix, so it must be a safe path segment.</summary>
    public string Name { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public string? Description { get; set; }

    public long ScriptTemplateId { get; set; }
    public ScriptTemplate? ScriptTemplate { get; set; }

    public long ServerId { get; set; }
    public Infrastructure.Server? Server { get; set; }

    public JobStatus Status { get; set; } = JobStatus.Inactive;

    /// <summary>Five-field cron expression; null/empty means manual only.</summary>
    public string? CronExpression { get; set; }

    /// <summary>IANA time zone the cron expression is evaluated in (DST aware).</summary>
    public string TimeZone { get; set; } = "UTC";
    public MissedRunPolicy MissedRunPolicy { get; set; } = MissedRunPolicy.RunOnceWithinGrace;
    public int MissedRunGraceMinutes { get; set; } = 60;

    /// <summary>Retention in (fractional) days, preserved from legacy decimal(10,4).</summary>
    public decimal RetentionDays { get; set; } = 7m;

    /// <summary>Always keep at least this many verified recovery points regardless of age.</summary>
    public int MinRecoveryPointsToKeep { get; set; } = 1;

    public long? S3DestinationId { get; set; }
    public Infrastructure.S3Destination? S3Destination { get; set; }

    /// <summary>Legacy semantic: only runs created after this moment are offloaded to S3.</summary>
    public DateTimeOffset? S3DestinationAssignedAt { get; set; }

    /// <summary>
    /// Additional destinations (for example a DR site) that every verified recovery point is copied to,
    /// S3 → S3, after the primary upload completes. Each copy is itself a restorable recovery point.
    /// </summary>
    public List<BackupJobReplicationTarget> ReplicationTargets { get; set; } = [];

    /// <summary>
    /// Per-job values for the settings the template declares (<c># @setting NAME ...</c>), for example the database host,
    /// user and password. Secret values are stored encrypted. They reach the script as exported, shell-quoted variables.
    /// </summary>
    public List<BackupJobSetting> Settings { get; set; } = [];

    /// <summary>Keep the NFS copy after a verified offload (legacy behaviour was to delete).</summary>
    public bool KeepLocalCopy { get; set; }

    /// <summary>Require at least one artifact produced in the job's NFS directory for success.</summary>
    public bool RequireArtifacts { get; set; } = true;

    public int TimeoutMinutes { get; set; } = 360;

    /// <summary>Attempts for transient (connectivity) failures while starting a run.</summary>
    public int MaxAttempts { get; set; } = 3;

    public DateTimeOffset? NextRunAt { get; set; }
    public DateTimeOffset? LastScheduledFor { get; set; }

    public long? CreatedByUserId { get; set; }
    public DateTimeOffset? DeletedAt { get; set; }
    public bool IsDeleted => DeletedAt is not null;

    public bool IsScheduled => Status == JobStatus.Active && !string.IsNullOrWhiteSpace(CronExpression) && !IsDeleted;

    public void AssignDestination(long? destinationId, DateTimeOffset now)
    {
        if (destinationId == S3DestinationId) return;
        // Legacy semantics: the timestamp is recorded when a destination is first selected, so older
        // runs are not retroactively offloaded. Changing to another destination resets it.
        S3DestinationAssignedAt = destinationId is null ? null : now;
        S3DestinationId = destinationId;
    }

    public static bool IsValidName(string? name) =>
        !string.IsNullOrWhiteSpace(name) && name.Length <= MaxNameLength && SafeNameRegex().IsMatch(name)
        && name is not "." and not "..";

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._-]*$", RegexOptions.CultureInvariant)]
    private static partial Regex SafeNameRegex();
}

/// <summary>A secondary (e.g. disaster-recovery) S3 destination a job's recovery points are replicated to.</summary>
public sealed class BackupJobReplicationTarget : Entity
{
    public long BackupJobId { get; set; }
    public long S3DestinationId { get; set; }
    public Infrastructure.S3Destination? S3Destination { get; set; }
}

/// <summary>A per-job value for a template setting. Secret values are encrypted (<see cref="ValueCiphertext"/>).</summary>
public sealed class BackupJobSetting : Entity
{
    public long BackupJobId { get; set; }
    public string Name { get; set; } = string.Empty;
    public bool IsSecret { get; set; }
    public string? Value { get; set; }
    public string? ValueCiphertext { get; set; }
}
