using Backora.Contracts.V1;
using Backora.Domain;
using Backora.Domain.Backups;
using Backora.Domain.Infrastructure;

namespace Backora.Application.Common;

public static class Mapping
{
    public static RunStatusDto ToDto(this RunStatus s) => (RunStatusDto)(int)s;
    public static RunTriggerDto ToDto(this RunTrigger s) => (RunTriggerDto)(int)s;
    public static FailureClassDto ToDto(this FailureClass s) => (FailureClassDto)(int)s;
    public static OffloadStatusDto ToDto(this OffloadStatus s) => (OffloadStatusDto)(int)s;
    public static CleanupStatusDto ToDto(this CleanupStatus s) => (CleanupStatusDto)(int)s;
    public static RetentionStateDto ToDto(this RetentionState s) => (RetentionStateDto)(int)s;
    public static RestoreStatusDto ToDto(this RestoreStatus s) => (RestoreStatusDto)(int)s;
    public static VerificationStatusDto ToDto(this VerificationStatus s) => (VerificationStatusDto)(int)s;
    public static JobStatusDto ToDto(this JobStatus s) => (JobStatusDto)(int)s;
    public static MissedRunPolicyDto ToDto(this MissedRunPolicy s) => (MissedRunPolicyDto)(int)s;
    public static AuditOutcomeDto ToDto(this AuditOutcome s) => (AuditOutcomeDto)(int)s;
    public static EncryptionModeDto ToDto(this EncryptionMode s) => (EncryptionModeDto)(int)s;
    public static EncryptionMode ToDomain(this EncryptionModeDto s) => (EncryptionMode)(int)s;

    public static RunStatus ToDomain(this RunStatusDto s) => (RunStatus)(int)s;
    public static RunTrigger ToDomain(this RunTriggerDto s) => (RunTrigger)(int)s;
    public static JobStatus ToDomain(this JobStatusDto s) => (JobStatus)(int)s;
    public static MissedRunPolicy ToDomain(this MissedRunPolicyDto s) => (MissedRunPolicy)(int)s;
    public static AuditOutcome ToDomain(this AuditOutcomeDto s) => (AuditOutcome)(int)s;

    public static RunSummaryDto ToSummary(this BackupRun r, string? serverName = null, OffloadStatus? offloadStatus = null) => new(
        r.Id, r.BackupJobId, r.JobName, r.Category, r.ServerId, serverName ?? r.Server?.Name ?? string.Empty,
        r.Status.ToDto(), r.Trigger.ToDto(), r.FailureClass.ToDto(), r.ErrorMessage, r.CreatedAt, r.StartedAt, r.CompletedAt,
        r.StartedAt is null ? null : ((r.CompletedAt ?? DateTimeOffset.UtcNow) - r.StartedAt.Value).TotalSeconds,
        r.ArtifactCount, r.TotalArtifactBytes, r.VerificationStatus.ToDto(), r.Attempt, r.MaxAttempts, offloadStatus?.ToDto(), r.RequestedByName, r.DismissedAt);

    public static ArtifactDto ToDto(this BackupArtifact a) => new(a.Id, a.RelativePath, a.SizeBytes, a.Sha256, a.ModifiedAt, a.LocalCopyDeleted);

    public static OffloadSummaryDto ToSummary(this OffloadRun o, string? serverName = null, long? supersededById = null) => new(
        o.Id, o.BackupRunId, o.BackupJobId, o.JobName, o.ServerId, serverName ?? o.Server?.Name,
        o.S3DestinationId, o.S3Destination?.Name ?? string.Empty, (o.S3Destination is { Kind: StorageKind.Filesystem } fs ? fs.LocalPath : o.S3Destination?.Bucket) ?? string.Empty, o.RemotePrefix, o.LegacyPrefix,
        o.Status.ToDto(), o.VerificationStatus.ToDto(), o.BytesTotal, o.BytesTransferred, o.FilesTotal, o.FilesTransferred,
        Percent(o.BytesTransferred, o.BytesTotal, o.Status == OffloadStatus.Completed), o.LegacyTotalSize, o.CleanupStatus.ToDto(),
        o.RetentionState.ToDto(), o.IsProtected, o.ProtectionReason, o.ExpiresAt, o.CreatedAt, o.StartedAt, o.CompletedAt,
        o.FailureClass.ToDto(), o.ErrorMessage, o.Attempt, o.MaxAttempts, o.SourceOffloadRunId, o.SourceOffloadRun?.S3Destination?.Name,
        o.Encrypted, o.EncryptionFormat, o.EncryptionKeyFingerprint, supersededById,
        o.Status is OffloadStatus.Running or OffloadStatus.Verifying ? o.ProgressStage : null, o.ProgressBytes, o.ProgressTotalBytes, o.ProgressStageStartedAt, o.DismissedAt);

    public static RestoreSummaryDto ToSummary(this RestoreRun r, string? serverName = null) => new(
        r.Id, r.OffloadRunId, r.JobName, r.ServerId, serverName ?? r.Server?.Name, r.Status.ToDto(), r.VerificationStatus.ToDto(),
        r.TargetPath, r.FilesExpected, r.FilesVerified, r.BytesTotal, r.BytesTransferred,
        Percent(r.BytesTransferred, r.BytesTotal, r.Status == RestoreStatus.Completed), r.RequestedByName, r.Reason,
        r.CreatedAt, r.StartedAt, r.CompletedAt, r.FailureClass.ToDto(), r.ErrorMessage,
        r.RequiresDecryptionKey, r.DecryptionKeyFingerprint, r.Status == RestoreStatus.AwaitingKey ? r.KeyRequestExpiresAt : null);

    public static double? Percent(long? done, long? total, bool complete)
    {
        if (complete) return 100;
        if (done is null || total is null or <= 0) return null;
        return Math.Round(Math.Min(100, done.Value * 100.0 / total.Value), 1);
    }

    public static string MaskKeyId(string keyId) =>
        keyId.Length <= 4 ? new string('•', keyId.Length) : new string('•', Math.Min(12, keyId.Length - 4)) + keyId[^4..];

    public static HealthState ServerHealth(Server s, bool hasFailedLastRun) =>
        s.HostKeyFingerprint is null ? HealthState.Warning
        : s.LastConnectionTestSucceeded == false || hasFailedLastRun ? HealthState.Critical
        : s.LastConnectionTestSucceeded == true ? HealthState.Healthy
        : HealthState.Unknown;
}
