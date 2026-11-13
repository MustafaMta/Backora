using Backora.Domain.Common;
using Backora.Domain.Operations;

namespace Backora.Domain.Backups;

/// <summary>
/// Copy of a backup run's artifacts to S3 (legacy: upload_to_s3_job_executions). A completed offload is a
/// <em>recovery point</em>: the unit that can be restored and that retention operates on.
/// </summary>
public sealed class OffloadRun : LeasedOperation
{
    public long? BackupRunId { get; set; }
    public BackupRun? BackupRun { get; set; }
    public long S3DestinationId { get; set; }
    public Infrastructure.S3Destination? S3Destination { get; set; }
    public long? ServerId { get; set; }
    public Infrastructure.Server? Server { get; set; }
    public long? BackupJobId { get; set; }

    public string JobName { get; set; } = string.Empty;

    /// <summary>
    /// Set for an S3 → S3 copy (replica, e.g. DR site): the recovery point this copy was made from.
    /// Null for the primary upload from NFS.
    /// </summary>
    public long? SourceOffloadRunId { get; set; }
    public OffloadRun? SourceOffloadRun { get; set; }
    public bool IsReplica => SourceOffloadRunId is not null;

    /// <summary>Source directory on the worker (legacy column nfs_path). For replicas: the source object prefix.</summary>
    public string SourcePath { get; set; } = string.Empty;

    /// <summary>Object key prefix inside the bucket (no bucket, trailing slash).</summary>
    public string RemotePrefix { get; set; } = string.Empty;

    /// <summary>True for imported rows whose prefix was derived from the legacy naming convention.</summary>
    public bool LegacyPrefix { get; set; }

    /// <summary>
    /// Set when someone cleared this failure from the Failed list ("seen, nothing more to do"): it stays in the history
    /// and can still be retried, but no longer counts as a failure needing attention (Failed list, alerts, job health).
    /// </summary>
    public DateTimeOffset? DismissedAt { get; set; }
    public string? DismissedBy { get; set; }

    public OffloadStatus Status { get; set; } = OffloadStatus.Pending;
    public VerificationStatus VerificationStatus { get; set; } = VerificationStatus.NotVerified;
    public DateTimeOffset? VerifiedAt { get; set; }

    public long? BytesTotal { get; set; }
    public long? BytesTransferred { get; set; }

    /// <summary>Last time transferred bytes increased (stall detection).</summary>
    public DateTimeOffset? LastProgressAt { get; set; }

    /// <summary>
    /// Live progress of the current step of the copy — encrypt, upload, inspect (ciphertext checksums), check
    /// (rclone check against storage) — with when that step started, so speed and time left are per step.
    /// </summary>
    public string? ProgressStage { get; set; }
    public long? ProgressBytes { get; set; }
    public long? ProgressTotalBytes { get; set; }
    public DateTimeOffset? ProgressStageStartedAt { get; set; }

    /// <summary>Records the copy's current step; the step's start time resets when the step changes.</summary>
    public void SetProgress(string stage, long bytes, long? totalBytes, DateTimeOffset now)
    {
        if (ProgressStage != stage) ProgressStageStartedAt = now;
        ProgressStage = stage;
        ProgressBytes = Math.Max(0, bytes);
        ProgressTotalBytes = totalBytes is > 0 ? totalBytes : null;
    }

    public int? FilesTotal { get; set; }
    public int? FilesTransferred { get; set; }

    /// <summary>Legacy human-readable size ("1.234 GiB"), kept for imported rows.</summary>
    public string? LegacyTotalSize { get; set; }

    public CleanupStatus CleanupStatus { get; set; } = CleanupStatus.NotRequired;
    public string? CleanupMessage { get; set; }

    public RetentionState RetentionState { get; set; } = RetentionState.Active;
    public bool IsProtected { get; set; }
    public string? ProtectionReason { get; set; }
    public long? ProtectedByUserId { get; set; }
    public DateTimeOffset? ExpiresAt { get; set; }
    public DateTimeOffset? DeletedAt { get; set; }
    public string? DeletionReason { get; set; }

    /// <summary>
    /// True when the objects of this recovery point are age ciphertext (object key = relative path + ".age").
    /// Snapshotted when the upload starts; later destination changes never alter it.
    /// </summary>
    public bool Encrypted { get; set; }

    /// <summary>Ciphertext format, e.g. "age-v1". Null for plaintext points.</summary>
    public string? EncryptionFormat { get; set; }

    /// <summary>age recipient (public key) the objects were encrypted to. The private key is never stored.</summary>
    public string? EncryptionRecipient { get; set; }
    public string? EncryptionKeyFingerprint { get; set; }

    /// <summary>
    /// "zstd" when the worker compressed the artifacts before uploading them (object key = relative path + ".zst",
    /// then ".age" when encrypted too). Null for objects stored exactly as the backup wrote them. Snapshotted when the
    /// upload starts; replicas keep their source's.
    /// </summary>
    public string? Compression { get; set; }

    public const string Zstd = "zstd";
    public const string ZstdSuffix = ".zst";

    /// <summary>
    /// JSON array of <see cref="EncryptedObject"/> (stored object size + SHA-256/MD5 per artifact) recorded when the
    /// staged objects (compressed and/or encrypted) were verified; used to verify the uploaded objects and replicas.
    /// </summary>
    public string? EncryptedObjectsJson { get; set; }

    public IReadOnlyList<EncryptedObject> EncryptedObjects =>
        string.IsNullOrEmpty(EncryptedObjectsJson) ? [] : System.Text.Json.JsonSerializer.Deserialize<List<EncryptedObject>>(EncryptedObjectsJson) ?? [];

    /// <summary>Suffix the stored objects carry after the artifact's own name: ".zst", ".age", ".zst.age" or none.</summary>
    public string ObjectSuffix => (Compression == Zstd ? ZstdSuffix : string.Empty) + (Encrypted ? EncryptedObject.Suffix : string.Empty);

    /// <summary>True when the stored objects are not the artifacts byte for byte (compressed and/or encrypted).</summary>
    public bool Transformed => Encrypted || Compression is not null;

    /// <summary>
    /// Object key (relative to <see cref="RemotePrefix"/>) for a stored name: an artifact path, or a folder name ending in
    /// "/" for a backup folder the worker packed into one archive (&lt;folder&gt;.tar + suffix).
    /// </summary>
    public string ObjectKeyFor(string storedName) => (IsPackedFolder(storedName) ? storedName.TrimEnd('/') + ".tar" : storedName) + ObjectSuffix;

    /// <summary>True for the stored name of a backup folder packed into one archive (it ends in "/").</summary>
    public static bool IsPackedFolder(string storedName) => storedName.EndsWith('/');

    /// <summary>Copies the encryption metadata of another point (replica of already-encrypted objects).</summary>
    public void InheritEncryption(OffloadRun source)
    {
        Encrypted = source.Encrypted;
        EncryptionFormat = source.EncryptionFormat;
        EncryptionRecipient = source.EncryptionRecipient;
        EncryptionKeyFingerprint = source.EncryptionKeyFingerprint;
        EncryptedObjectsJson = source.EncryptedObjectsJson;
        Compression = source.Compression;
    }

    public bool IsRecoveryPoint => Status == OffloadStatus.Completed && RetentionState is RetentionState.Active or RetentionState.Expired;
    public bool IsTerminal => Status is OffloadStatus.Completed or OffloadStatus.Failed or OffloadStatus.Cancelled;

    private static readonly Dictionary<OffloadStatus, OffloadStatus[]> Allowed = new()
    {
        [OffloadStatus.Pending] = [OffloadStatus.Running, OffloadStatus.Failed, OffloadStatus.Cancelled],
        [OffloadStatus.Running] = [OffloadStatus.Verifying, OffloadStatus.Pending, OffloadStatus.Failed, OffloadStatus.Cancelled],
        [OffloadStatus.Verifying] = [OffloadStatus.Completed, OffloadStatus.Failed, OffloadStatus.Pending],
        [OffloadStatus.Completed] = [],
        [OffloadStatus.Failed] = [],
        [OffloadStatus.Cancelled] = [],
    };

    public void TransitionTo(OffloadStatus next, DateTimeOffset now)
    {
        if (!Allowed[Status].Contains(next)) throw new InvalidStateTransitionException(nameof(OffloadRun), Status, next);
        Status = next;
        if (next == OffloadStatus.Running && StartedAt is null) StartedAt = now;
        if (next is OffloadStatus.Completed or OffloadStatus.Failed or OffloadStatus.Cancelled)
        {
            CompletedAt = now;
        }
    }

    public void Fail(FailureClass failureClass, string message, DateTimeOffset now)
    {
        FailureClass = failureClass;
        ErrorMessage = message.Length > 4000 ? message[..4000] : message;
        AppendLogLine(now, "ERROR " + ErrorMessage);
        TransitionTo(OffloadStatus.Failed, now);
    }

    public static string BuildPrefix(string? destinationPrefix, string jobName, DateTimeOffset startedUtc, long offloadId)
    {
        var basePrefix = string.IsNullOrWhiteSpace(destinationPrefix) ? string.Empty : destinationPrefix.Trim('/') + "/";
        // Legacy layout was <job>/<Y-m-d-H>/; the run id suffix prevents two runs in one hour colliding.
        return $"{basePrefix}{jobName}/{startedUtc.UtcDateTime:yyyy-MM-dd-HH}-r{offloadId}/";
    }

    /// <summary>
    /// Prefix of a replica: the same layout as the source (minus the source destination's base prefix) under
    /// the target destination's base prefix, so a DR bucket mirrors the primary bucket's structure.
    /// </summary>
    public static string BuildReplicaPrefix(string? targetDestinationPrefix, string? sourceDestinationPrefix, string sourcePrefix)
    {
        var relative = sourcePrefix.TrimStart('/');
        var sourceBase = string.IsNullOrWhiteSpace(sourceDestinationPrefix) ? string.Empty : sourceDestinationPrefix.Trim('/') + "/";
        if (sourceBase.Length > 0 && relative.StartsWith(sourceBase, StringComparison.Ordinal)) relative = relative[sourceBase.Length..];
        if (!relative.EndsWith('/')) relative += "/";
        var targetBase = string.IsNullOrWhiteSpace(targetDestinationPrefix) ? string.Empty : targetDestinationPrefix.Trim('/') + "/";
        return targetBase + relative;
    }
}

/// <summary>One artifact as stored in S3 for a compressed and/or encrypted recovery point.</summary>
/// <param name="RelativePath">Artifact path (the backup's own name, without ".zst"/".age").</param>
/// <param name="CiphertextSize">Exact object size in bytes.</param>
/// <param name="CiphertextSha256">Lowercase hex SHA-256 of the stored object.</param>
public sealed record EncryptedObject(string RelativePath, long CiphertextSize, string CiphertextSha256, string? CiphertextMd5 = null)
{
    public const string Suffix = ".age";
}

/// <summary>Restore of a recovery point from S3 into an isolated NFS staging directory (legacy: restore_to_nfs_job_executions).</summary>
public sealed class RestoreRun : LeasedOperation
{
    public long OffloadRunId { get; set; }
    public OffloadRun? OffloadRun { get; set; }
    public long? ServerId { get; set; }
    public Infrastructure.Server? Server { get; set; }

    public string JobName { get; set; } = string.Empty;
    public RestoreStatus Status { get; set; } = RestoreStatus.Pending;

    /// <summary>Absolute target directory on the worker (NFS), unique per restore.</summary>
    public string TargetPath { get; set; } = string.Empty;

    public string? Reason { get; set; }
    public long? RequestedByUserId { get; set; }
    public string? RequestedByName { get; set; }
    public DateTimeOffset? CancelRequestedAt { get; set; }

    public VerificationStatus VerificationStatus { get; set; } = VerificationStatus.NotVerified;
    public int? FilesExpected { get; set; }
    public int? FilesVerified { get; set; }
    public long? BytesTotal { get; set; }
    public long? BytesTransferred { get; set; }
    public DateTimeOffset? LastProgressAt { get; set; }

    /// <summary>The recovery point is encrypted: the operator must supply the private key for every attempt.</summary>
    public bool RequiresDecryptionKey { get; set; }

    /// <summary>Fingerprint of the recipient the key must match (public). The key itself is never stored.</summary>
    public string? DecryptionKeyFingerprint { get; set; }
    public DateTimeOffset? KeyRequestedAt { get; set; }
    public DateTimeOffset? KeyRequestExpiresAt { get; set; }

    public bool IsTerminal => Status is RestoreStatus.Completed or RestoreStatus.Failed or RestoreStatus.Cancelled;

    private static readonly Dictionary<RestoreStatus, RestoreStatus[]> Allowed = new()
    {
        [RestoreStatus.Pending] = [RestoreStatus.Running, RestoreStatus.Failed, RestoreStatus.Cancelled],
        [RestoreStatus.Running] = [RestoreStatus.Verifying, RestoreStatus.Pending, RestoreStatus.Failed, RestoreStatus.Cancelling, RestoreStatus.AwaitingKey],
        [RestoreStatus.AwaitingKey] = [RestoreStatus.Running, RestoreStatus.Pending, RestoreStatus.Failed, RestoreStatus.Cancelling],
        [RestoreStatus.Verifying] = [RestoreStatus.Completed, RestoreStatus.Failed],
        [RestoreStatus.Cancelling] = [RestoreStatus.Cancelled, RestoreStatus.Failed],
        [RestoreStatus.Completed] = [],
        [RestoreStatus.Failed] = [],
        [RestoreStatus.Cancelled] = [],
    };

    public void TransitionTo(RestoreStatus next, DateTimeOffset now)
    {
        if (!Allowed[Status].Contains(next)) throw new InvalidStateTransitionException(nameof(RestoreRun), Status, next);
        Status = next;
        if (next == RestoreStatus.Running && StartedAt is null) StartedAt = now;
        if (next is RestoreStatus.Completed or RestoreStatus.Failed or RestoreStatus.Cancelled)
        {
            CompletedAt = now;
        }
    }

    public void Fail(FailureClass failureClass, string message, DateTimeOffset now)
    {
        FailureClass = failureClass;
        ErrorMessage = message.Length > 4000 ? message[..4000] : message;
        AppendLogLine(now, "ERROR " + ErrorMessage);
        TransitionTo(RestoreStatus.Failed, now);
    }
}
