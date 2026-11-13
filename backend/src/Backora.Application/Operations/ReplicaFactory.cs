using Backora.Domain;
using Backora.Domain.Backups;

namespace Backora.Application.Operations;

/// <summary>Creates S3 → S3 replica copies (e.g. to a DR site) of a completed recovery point.</summary>
public static class ReplicaFactory
{
    public static OffloadRun Create(OffloadRun source, long targetDestinationId, DateTimeOffset now, string? correlationId = null) => new()
    {
        SourceOffloadRunId = source.Id,
        BackupRunId = source.BackupRunId,
        BackupJobId = source.BackupJobId,
        ServerId = source.ServerId,
        S3DestinationId = targetDestinationId,
        JobName = source.JobName,
        // For replicas the "source path" records where the data is read from (bucket-relative prefix).
        SourcePath = source.RemotePrefix,
        MaxAttempts = 3,
        NextAttemptAt = now,
        CorrelationId = correlationId ?? source.CorrelationId,
        CleanupStatus = CleanupStatus.NotRequired,
    };
}
