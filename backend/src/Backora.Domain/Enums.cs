namespace Backora.Domain;

public enum JobStatus { Inactive = 0, Active = 1 }

/// <summary>Lifecycle of a backup run (legacy: backup_job_executions.status).</summary>
public enum RunStatus
{
    Pending = 0,
    Starting = 1,
    Running = 2,
    Verifying = 3,
    Completed = 4,
    Failed = 5,
    Cancelling = 6,
    Cancelled = 7,
}

public enum RunTrigger { Manual = 0, Scheduled = 1, Api = 2, Retry = 3, Imported = 4 }

/// <summary>Classifies why an operation failed so operators know where to look.</summary>
public enum FailureClass
{
    None = 0,
    Configuration = 1,
    Connectivity = 2,
    Authentication = 3,
    HostKeyMismatch = 4,
    ScriptError = 5,
    NoArtifacts = 6,
    VerificationFailed = 7,
    Timeout = 8,
    ProcessLost = 9,
    StorageError = 10,
    Cancelled = 11,
    Internal = 12,
}

public enum OffloadStatus { Pending = 0, Running = 1, Verifying = 2, Completed = 3, Failed = 4, Cancelled = 5 }

public enum CleanupStatus { NotRequired = 0, Pending = 1, Completed = 2, Failed = 3, Skipped = 4 }

public enum RetentionState { Active = 0, Expired = 1, Deleting = 2, Deleted = 3, DeleteFailed = 4 }

/// <summary>
/// Lifecycle of a restore. <see cref="AwaitingKey"/>: an encrypted recovery point is staged on the worker host
/// and the restore waits for the operator-supplied private key (delivered only in memory, never stored).
/// </summary>
public enum RestoreStatus { Pending = 0, Running = 1, Verifying = 2, Completed = 3, Failed = 4, Cancelling = 5, Cancelled = 6, AwaitingKey = 7 }

/// <summary>What a storage site is: an S3-compatible bucket, or a folder on the execution host (e.g. a mounted NFS share).</summary>
public enum StorageKind { S3 = 0, Filesystem = 1 }

/// <summary>Client-side encryption applied on the worker host before objects reach an S3 destination.</summary>
public enum EncryptionMode
{
    None = 0,
    /// <summary>age (X25519) encryption to a customer-held recipient; Backora stores only the public key.</summary>
    CustomerKey = 1,
}

public enum VerificationStatus { NotVerified = 0, Verified = 1, Failed = 2, Partial = 3 }

public enum NotificationChannel { Email = 0, Teams = 1 }

public enum NotificationStatus { Pending = 0, Sent = 1, Failed = 2, Skipped = 3 }

public enum AuditOutcome { Success = 0, Failure = 1, Denied = 2 }

public enum MissedRunPolicy
{
    /// <summary>Run once if the missed occurrence is within the grace window, otherwise skip.</summary>
    RunOnceWithinGrace = 0,
    /// <summary>Never catch up; wait for the next occurrence.</summary>
    Skip = 1,
}
