using System.Text.Json.Serialization;

namespace Backora.Contracts.V1;

/// <summary>Contract version. Breaking changes require a new namespace (V2) and route prefix (/api/v2).</summary>
public static class ContractVersion
{
    public const string Current = "1.0";
    public const string RoutePrefix = "/api/v1";
}

public sealed record PagedResult<T>(IReadOnlyList<T> Items, int Page, int PageSize, long Total)
{
    public int TotalPages => PageSize <= 0 ? 0 : (int)Math.Ceiling(Total / (double)PageSize);
}

public sealed record CheckResultDto(string Name, CheckState State, string Message);

[JsonConverter(typeof(JsonStringEnumConverter<CheckState>))]
public enum CheckState { Passed, Warning, Failed, Skipped }

public sealed record IdResponse(long Id);

public sealed record MessageResponse(string Message);

[JsonConverter(typeof(JsonStringEnumConverter<HealthState>))]
public enum HealthState { Healthy, Warning, Critical, Unknown }

[JsonConverter(typeof(JsonStringEnumConverter<JobStatusDto>))]
public enum JobStatusDto { Inactive, Active }

[JsonConverter(typeof(JsonStringEnumConverter<RunStatusDto>))]
public enum RunStatusDto { Pending, Starting, Running, Verifying, Completed, Failed, Cancelling, Cancelled }

[JsonConverter(typeof(JsonStringEnumConverter<RunTriggerDto>))]
public enum RunTriggerDto { Manual, Scheduled, Api, Retry, Imported }

[JsonConverter(typeof(JsonStringEnumConverter<FailureClassDto>))]
public enum FailureClassDto
{
    None, Configuration, Connectivity, Authentication, HostKeyMismatch, ScriptError, NoArtifacts,
    VerificationFailed, Timeout, ProcessLost, StorageError, Cancelled, Internal,
}

[JsonConverter(typeof(JsonStringEnumConverter<OffloadStatusDto>))]
public enum OffloadStatusDto { Pending, Running, Verifying, Completed, Failed, Cancelled }

[JsonConverter(typeof(JsonStringEnumConverter<CleanupStatusDto>))]
public enum CleanupStatusDto { NotRequired, Pending, Completed, Failed, Skipped }

[JsonConverter(typeof(JsonStringEnumConverter<RetentionStateDto>))]
public enum RetentionStateDto { Active, Expired, Deleting, Deleted, DeleteFailed }

[JsonConverter(typeof(JsonStringEnumConverter<RestoreStatusDto>))]
public enum RestoreStatusDto { Pending, Running, Verifying, Completed, Failed, Cancelling, Cancelled, AwaitingKey }

/// <summary>Client-side encryption applied before objects reach an S3 destination.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<StorageKindDto>))]
public enum StorageKindDto { S3, Filesystem }

[JsonConverter(typeof(JsonStringEnumConverter<EncryptionModeDto>))]
public enum EncryptionModeDto { None, CustomerKey }

[JsonConverter(typeof(JsonStringEnumConverter<VerificationStatusDto>))]
public enum VerificationStatusDto { NotVerified, Verified, Failed, Partial }

[JsonConverter(typeof(JsonStringEnumConverter<MissedRunPolicyDto>))]
public enum MissedRunPolicyDto { RunOnceWithinGrace, Skip }

[JsonConverter(typeof(JsonStringEnumConverter<AuditOutcomeDto>))]
public enum AuditOutcomeDto { Success, Failure, Denied }

public static class ApiHeaders
{
    public const string CorrelationId = "X-Correlation-ID";
    public const string IdempotencyKey = "Idempotency-Key";
}
