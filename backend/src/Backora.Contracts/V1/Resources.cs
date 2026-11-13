namespace Backora.Contracts.V1;

// ───────────────────────────── Users ─────────────────────────────
public sealed record UserDto(
    long Id,
    string Email,
    string Name,
    IReadOnlyList<string> Roles,
    bool IsActive,
    bool TwoFactorEnabled,
    DateTimeOffset? LastLoginAt,
    DateTimeOffset? LockedOutUntil,
    DateTimeOffset CreatedAt,
    string? LegacyUserCode);

public sealed record CreateUserRequest(string Email, string Name, string Password, IReadOnlyList<string> Roles);

public sealed record UpdateUserRequest(string Name, IReadOnlyList<string> Roles, bool IsActive);

public sealed record SetUserPasswordRequest(string NewPassword, bool RequireChangeOnNextLogin = true);

public sealed record RoleDto(string Name, string DisplayName, string Description, IReadOnlyList<string> Permissions, int UserCount);

// ───────────────────────────── Servers ─────────────────────────────
public sealed record ServerDto(
    long Id,
    string Name,
    string Host,
    int SshPort,
    string SshUser,
    bool HasPrivateKey,
    string? HostKeyFingerprint,
    DateTimeOffset? HostKeyTrustedAt,
    DateTimeOffset? LastConnectionTestAt,
    bool? LastConnectionTestSucceeded,
    string? LastConnectionTestMessage,
    string? Description,
    int JobCount,
    DateTimeOffset? LastBackupAt,
    HealthState Health,
    uint Version,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

/// <summary>On update, a null <see cref="PrivateKey"/> keeps the stored key; set <see cref="ClearPrivateKey"/> to fall back to the default key.</summary>
public sealed record ServerUpsertRequest(
    string Name,
    string Host,
    int SshPort,
    string SshUser,
    string? PrivateKey,
    bool ClearPrivateKey,
    string? Description,
    uint? Version);

public sealed record ConnectionTestRequest(string Host, int SshPort, string SshUser, string? PrivateKey, long? ServerId);

public sealed record ConnectionTestResultDto(
    bool Success,
    string Message,
    string Stage,
    string? WorkerFingerprint,
    bool WorkerFingerprintTrusted,
    string? TargetFingerprint,
    bool TargetFingerprintTrusted,
    long DurationMs);

public sealed record TrustHostKeyRequest(string Fingerprint);

// ───────────────────────────── Settings ─────────────────────────────
public sealed record WorkerNodeDto(
    bool Configured,
    string Host,
    int SshPort,
    string SshUser,
    bool HasPrivateKey,
    string? HostKeyFingerprint,
    DateTimeOffset? HostKeyTrustedAt,
    string RclonePath,
    string WorkDirectory,
    DateTimeOffset? LastConnectionTestAt,
    bool? LastConnectionTestSucceeded,
    string? LastConnectionTestMessage,
    uint Version,
    string PrivateKeySource = "database");

public sealed record WorkerNodeUpdateRequest(
    string Host,
    int SshPort,
    string SshUser,
    string? PrivateKey,
    string RclonePath,
    string WorkDirectory,
    uint? Version);

public sealed record NfsSettingsDto(string NfsHost, string NfsPath, uint Version);

public sealed record NfsSettingsUpdateRequest(string NfsHost, string NfsPath, uint? Version);

public sealed record SshSettingsDto(bool HasDefaultKey, string? DefaultPublicKey, string? KeyFingerprint, DateTimeOffset? UpdatedAt);

public sealed record SshSettingsUpdateRequest(string PrivateKey, string? PublicKey);

/// <summary>
/// A Backora variable. Template placeholders use it as {NAME}; backup settings use NAME (all jobs) or NAME__JOB (one job):
/// then <see cref="Setting"/> is the setting and <see cref="AppliesTo"/> is "*" or the job name.
/// </summary>
public sealed record VariableDto(long Id, string Name, bool IsSecret, string? Value, bool HasValue, string? Description, int UsedByTemplates, DateTimeOffset UpdatedAt,
    string? Setting = null, string? AppliesTo = null, bool JobFound = true);

/// <summary>For secret variables a null <see cref="Value"/> on update keeps the stored value.</summary>
public sealed record VariableUpsertRequest(string Name, bool IsSecret, string? Value, string? Description);

public sealed record SmtpSettingsDto(
    bool Configured,
    string Host,
    int Port,
    string? Username,
    bool HasPassword,
    string FromAddress,
    string? FromName,
    string Security,
    string? Template,
    string DefaultTemplate,
    bool Enabled,
    uint Version);

public sealed record SmtpSettingsUpdateRequest(
    string Host,
    int Port,
    string? Username,
    string? Password,
    string FromAddress,
    string? FromName,
    string Security,
    string? Template,
    bool Enabled,
    uint? Version);

public sealed record TeamsSettingsDto(bool Configured, string? WebhookHost, string? Template, string DefaultTemplate, bool Enabled, uint Version);

public sealed record TeamsSettingsUpdateRequest(string? WebhookUrl, string? Template, bool Enabled, uint? Version);

public sealed record NotificationSettingsDto(
    IReadOnlyList<string> Recipients,
    bool NotifyOnBackupFailure,
    bool NotifyOnOffloadFailure,
    bool NotifyOnRestoreFailure,
    bool NotifyOnRestoreCompleted,
    bool NotifyOnBackupSuccess,
    uint Version);

public sealed record NotificationSettingsUpdateRequest(
    IReadOnlyList<string> Recipients,
    bool NotifyOnBackupFailure,
    bool NotifyOnOffloadFailure,
    bool NotifyOnRestoreFailure,
    bool NotifyOnRestoreCompleted,
    bool NotifyOnBackupSuccess,
    uint? Version);

public sealed record TemplatePreviewRequest(string? Template);

public sealed record TemplatePreviewResponse(string Rendered, bool IsValid, string? Error);

public sealed record SendTestNotificationRequest(string? Recipient);

// ───────────────────────────── Templates ─────────────────────────────
public sealed record TemplateDto(
    long Id,
    string Name,
    string Category,
    string Script,
    string? Description,
    int Revision,
    IReadOnlyList<string> Placeholders,
    IReadOnlyList<string> UnknownPlaceholders,
    int JobCount,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    uint Version,
    IReadOnlyList<TemplateSettingDto>? Settings = null);

public sealed record TemplateSummaryDto(long Id, string Name, string Category, string? Description, int Revision, int JobCount, DateTimeOffset UpdatedAt,
    IReadOnlyList<TemplateSettingDto>? Settings = null);

/// <summary>A per-job setting a template declares (<c># @setting NAME "Label" [required] [secret] [default=…] [help="…"]</c>).</summary>
public sealed record TemplateSettingDto(string Name, string Label, bool Required, bool Secret, string? Default, string? Help);

public sealed record TemplateUpsertRequest(string Name, string Category, string Script, string? Description, uint? Version);

// ───────────────────────────── S3 destinations ─────────────────────────────
public sealed record S3DestinationDto(
    long Id,
    string Name,
    string Endpoint,
    string Region,
    string Bucket,
    string AccessKeyIdMasked,
    bool ForcePathStyle,
    string? PathPrefix,
    DateTimeOffset? LastConnectionTestAt,
    bool? LastConnectionTestSucceeded,
    string? LastConnectionTestMessage,
    int JobCount,
    int RecoveryPointCount,
    long StoredBytes,
    uint Version,
    DateTimeOffset CreatedAt,
    EncryptionModeDto EncryptionMode = EncryptionModeDto.None,
    string? EncryptionRecipient = null,
    string? EncryptionKeyFingerprint = null,
    DateTimeOffset? EncryptionKeyActivatedAt = null,
    IReadOnlyList<EncryptionKeyDto>? EncryptionKeyHistory = null,
    DateTimeOffset? LastEncryptedUploadAt = null,
    bool IsEnabled = true,
    bool BackoraHoldsKey = false,
    StorageKindDto Kind = StorageKindDto.S3,
    /// <summary>For a filesystem site: the folder on the execution host.</summary>
    string? LocalPath = null);

/// <summary>
/// A recipient public key that was (or is) used for a destination. Only public material is ever stored:
/// the matching private key stays with the customer and is required for every restore.
/// </summary>
public sealed record EncryptionKeyDto(string Fingerprint, string Recipient, DateTimeOffset ActivatedAt, DateTimeOffset? RetiredAt, int RecoveryPointCount);

/// <summary>Checks an age X25519 recipient ("age1…") without saving it.</summary>
public sealed record InspectRecipientRequest(string Recipient);

public sealed record RecipientInspectionDto(bool Valid, string? Fingerprint, string? Error, string Format);

/// <summary>
/// A freshly generated age X25519 key pair. Returned exactly once; Backora never stores the private key and
/// cannot show it again.
/// </summary>
public sealed record GeneratedKeyPairDto(string Recipient, string PrivateKey, string Fingerprint);

/// <summary>On update, a null <see cref="SecretKey"/> keeps the stored secret.</summary>
public sealed record S3DestinationUpsertRequest(
    string Name,
    string Endpoint,
    string Region,
    string Bucket,
    string AccessKeyId,
    string? SecretKey,
    bool ForcePathStyle,
    string? PathPrefix,
    uint? Version,
    EncryptionModeDto EncryptionMode = EncryptionModeDto.None,
    string? EncryptionRecipient = null,
    bool ConfirmDisableEncryption = false,
    bool IsEnabled = true,
    bool UseBackoraKey = false,
    StorageKindDto Kind = StorageKindDto.S3,
    /// <summary>For a filesystem site: the folder on the execution host (absolute path, e.g. a mounted NFS share).</summary>
    string? LocalPath = null);

public sealed record S3TestRequest(
    long? DestinationId,
    string Endpoint,
    string Region,
    string Bucket,
    string AccessKeyId,
    string? SecretKey,
    bool ForcePathStyle,
    string? PathPrefix,
    StorageKindDto Kind = StorageKindDto.S3,
    string? LocalPath = null);

/// <summary>A folder (common prefix) or an object in a storage site.</summary>
/// For a folder, <see cref="Size"/>, <see cref="Files"/> and <see cref="LastModified"/> cover everything inside it (all levels).
public sealed record S3BrowseItemDto(string Key, string Name, bool IsFolder, long Size, DateTimeOffset? LastModified, long Files = 0);

/// <summary>Totals for everything under the current folder (all levels) or, in search mode, for every match.
/// <see cref="Complete"/> is false when the listing stopped at the scan limit (the numbers are then lower bounds).</summary>
public sealed record S3BrowseTotals(long Files, long Bytes, int Folders, DateTimeOffset? Newest, DateTimeOffset? Oldest, bool Complete);

/// <summary>One page of a storage site's contents. In search mode (<see cref="Searched"/>) items come from the whole folder tree.</summary>
public sealed record S3BrowseResponse(long DestinationId, string DestinationName, string Bucket, string Prefix, IReadOnlyList<S3BrowseItemDto> Items,
    string? NextToken, bool Searched, bool Truncated, int Scanned, S3BrowseTotals? Totals = null);

public sealed record S3TestResultDto(bool Success, string Message, IReadOnlyList<CheckResultDto> Checks);

// ───────────────────────────── Backup jobs ─────────────────────────────
public sealed record BackupJobDto(
    long Id,
    string Name,
    string Category,
    string? Description,
    long TemplateId,
    string TemplateName,
    long ServerId,
    string ServerName,
    string ServerHost,
    JobStatusDto Status,
    string? CronExpression,
    string TimeZone,
    string ScheduleDescription,
    MissedRunPolicyDto MissedRunPolicy,
    int MissedRunGraceMinutes,
    decimal RetentionDays,
    int MinRecoveryPointsToKeep,
    long? S3DestinationId,
    string? S3DestinationName,
    DateTimeOffset? S3DestinationAssignedAt,
    bool KeepLocalCopy,
    bool RequireArtifacts,
    int TimeoutMinutes,
    int MaxAttempts,
    DateTimeOffset? NextRunAt,
    RunSummaryDto? LastRun,
    DateTimeOffset? LastSuccessAt,
    HealthState Health,
    string? HealthReason,
    uint Version,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    IReadOnlyList<ReplicationTargetDto>? ReplicationTargets = null,
    IReadOnlyList<JobSettingDto>? Settings = null);

/// <summary>A job's value for a template setting. Secret values are never returned (<see cref="HasValue"/> says one is stored).</summary>
public sealed record JobSettingDto(string Name, bool IsSecret, string? Value, bool HasValue);

/// <summary>Set a template setting on a job. For secret settings, a null or empty value keeps the stored secret.</summary>
public sealed record JobSettingInput(string Name, string? Value);

/// <summary>Secondary (e.g. DR) destination every verified recovery point of a job is copied to, S3 → S3.</summary>
public sealed record ReplicationTargetDto(long DestinationId, string Name, string Bucket);

public sealed record BackupJobUpsertRequest(
    string Name,
    string? Description,
    long TemplateId,
    long ServerId,
    JobStatusDto Status,
    string? CronExpression,
    string? TimeZone,
    MissedRunPolicyDto MissedRunPolicy,
    int MissedRunGraceMinutes,
    decimal RetentionDays,
    int MinRecoveryPointsToKeep,
    long? S3DestinationId,
    bool KeepLocalCopy,
    bool RequireArtifacts,
    int TimeoutMinutes,
    int MaxAttempts,
    uint? Version,
    IReadOnlyList<long>? ReplicaDestinationIds = null,
    IReadOnlyList<JobSettingInput>? Settings = null);

public sealed record SetJobStatusRequest(JobStatusDto Status);

public sealed record SchedulePreviewRequest(string? CronExpression, string? TimeZone);

public sealed record SchedulePreviewResponse(bool Valid, string? Error, string Description, IReadOnlyList<DateTimeOffset> NextOccurrences);

public sealed record ScriptPreviewResponse(string Script, string Sha256, IReadOnlyList<string> UnresolvedPlaceholders, IReadOnlyList<string> SecretVariablesMasked);

// ───────────────────────────── Runs ─────────────────────────────
public sealed record RunSummaryDto(
    long Id,
    long BackupJobId,
    string JobName,
    string Category,
    long ServerId,
    string ServerName,
    RunStatusDto Status,
    RunTriggerDto Trigger,
    FailureClassDto FailureClass,
    string? ErrorMessage,
    DateTimeOffset CreatedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt,
    double? DurationSeconds,
    int ArtifactCount,
    long? TotalArtifactBytes,
    VerificationStatusDto VerificationStatus,
    int Attempt,
    int MaxAttempts,
    OffloadStatusDto? OffloadStatus,
    string? RequestedBy,
    /// <summary>Set when someone cleared this failed run from the Failed list; it stays in the history.</summary>
    DateTimeOffset? DismissedAt = null);

public sealed record ArtifactDto(long Id, string RelativePath, long SizeBytes, string Sha256, DateTimeOffset ModifiedAt, bool LocalCopyDeleted);

public sealed record RunDetailDto(
    RunSummaryDto Summary,
    string TemplateName,
    int? TemplateRevision,
    string? ScriptSha256,
    int? ExitCode,
    string? Log,
    bool LogVisible,
    DateTimeOffset? ScheduledFor,
    DateTimeOffset? HeartbeatAt,
    string? LeaseOwner,
    DateTimeOffset? NextAttemptAt,
    DateTimeOffset? CancelRequestedAt,
    string? RemoteDirectory,
    int? RemotePid,
    DateTimeOffset? TimeoutAt,
    string? CorrelationId,
    IReadOnlyList<ArtifactDto> Artifacts,
    IReadOnlyList<OffloadSummaryDto> Offloads,
    IReadOnlyList<TimelineEntryDto> Timeline,
    RunProgressDto? Progress = null);

/// <summary>
/// Live progress of a running backup. <see cref="Percent"/> and <see cref="SecondsRemaining"/> are null when the
/// template cannot tell how much it will write; <see cref="Measured"/> is false when the bytes come from the run's
/// processes rather than from the template (older or custom templates).
/// </summary>
public sealed record RunProgressDto(
    long Bytes,
    long? TotalBytes,
    double? Percent,
    long? BytesPerSecond,
    double? SecondsRemaining,
    string? Stage,
    bool Measured,
    DateTimeOffset UpdatedAt);

public sealed record TimelineEntryDto(DateTimeOffset At, string Title, string? Detail, string Tone);

public sealed record RunCreatedResponse(long RunId, bool AlreadyExisted);

// ───────────────────────────── Offloads / recovery points ─────────────────────────────
public sealed record OffloadSummaryDto(
    long Id,
    long? BackupRunId,
    long? BackupJobId,
    string JobName,
    long? ServerId,
    string? ServerName,
    long DestinationId,
    string DestinationName,
    string Bucket,
    string RemotePrefix,
    bool LegacyPrefix,
    OffloadStatusDto Status,
    VerificationStatusDto VerificationStatus,
    long? BytesTotal,
    long? BytesTransferred,
    int? FilesTotal,
    int? FilesTransferred,
    double? ProgressPercent,
    string? LegacyTotalSize,
    CleanupStatusDto CleanupStatus,
    RetentionStateDto RetentionState,
    bool IsProtected,
    string? ProtectionReason,
    DateTimeOffset? ExpiresAt,
    DateTimeOffset CreatedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt,
    FailureClassDto FailureClass,
    string? ErrorMessage,
    int Attempt,
    int MaxAttempts,
    long? SourceOffloadId = null,
    string? SourceDestinationName = null,
    bool Encrypted = false,
    string? EncryptionFormat = null,
    string? EncryptionKeyFingerprint = null,
    /// <summary>Set when a later attempt for the same backup run and site replaced this one (a retry). The newer attempt's id.</summary>
    long? SupersededById = null,
    /// <summary>Current step of a copy in progress: encrypt, upload, inspect or check; with its bytes and start time.</summary>
    string? ProgressStage = null,
    long? ProgressBytes = null,
    long? ProgressTotalBytes = null,
    DateTimeOffset? ProgressStageStartedAt = null,
    /// <summary>Set when someone cleared this failure from the Failed list; it stays in the history.</summary>
    DateTimeOffset? DismissedAt = null)
{
    /// <summary>True for an S3 → S3 copy (e.g. DR site) of another recovery point; false for the upload from NFS.</summary>
    public bool IsReplica => SourceOffloadId is not null;
}

/// <summary>Moves every job that copies to one DR site so it copies to another instead.</summary>
public sealed record MoveDrJobsRequest(long ToDestinationId);

/// <summary>How many jobs now copy to the new DR site.</summary>
public sealed record MoveDrJobsResponse(int Jobs, string ToDestinationName);

/// <summary>How many failed copies were cleared from the Failed list.</summary>
public sealed record DismissedResponse(int Count);

/// <summary>Copy a recovery point to another S3 destination (e.g. a DR site).</summary>
public sealed record ReplicateRecoveryPointRequest(long DestinationId);

public sealed record OffloadDetailDto(
    OffloadSummaryDto Summary,
    string? Log,
    string? CleanupMessage,
    DateTimeOffset? VerifiedAt,
    DateTimeOffset? DeletedAt,
    string? DeletionReason,
    IReadOnlyList<ArtifactDto> Artifacts,
    IReadOnlyList<RestoreSummaryDto> Restores);

public sealed record ProtectRecoveryPointRequest(bool Protected, string? Reason);

public sealed record ServerBackupSummaryDto(
    long ServerId,
    string ServerName,
    string ServerHost,
    int RecoveryPoints,
    int CompletedOffloads,
    int FailedOffloads,
    long StoredBytes,
    DateTimeOffset? LatestRecoveryPointAt);

// ───────────────────────────── Restores ─────────────────────────────
public sealed record RestorePrecheckRequest(long RecoveryPointId);

public sealed record RestorePrecheckResponse(
    bool CanRestore,
    IReadOnlyList<CheckResultDto> Checks,
    string TargetPath,
    int ExpectedFiles,
    long ExpectedBytes,
    string ConfirmationPhrase,
    OffloadSummaryDto RecoveryPoint);

/// <summary><see cref="DecryptionKey"/> is required for an encrypted recovery point. It is held only in memory and never stored.</summary>
public sealed record CreateRestoreRequest(long RecoveryPointId, string Reason, string ConfirmationPhrase, string? DecryptionKey = null);

/// <summary>Supplies the private key ("AGE-SECRET-KEY-1…") to a restore that is waiting for it. Never stored or logged.</summary>
public sealed record SubmitDecryptionKeyRequest(string PrivateKey);

/// <summary>Checks a private key against a recovery point's recorded recipient without starting a restore.</summary>
public sealed record VerifyDecryptionKeyRequest(string PrivateKey);

public sealed record KeyVerificationDto(bool Matches, string? Fingerprint, string Message);

public sealed record RestoreSummaryDto(
    long Id,
    long RecoveryPointId,
    string JobName,
    long? ServerId,
    string? ServerName,
    RestoreStatusDto Status,
    VerificationStatusDto VerificationStatus,
    string TargetPath,
    int? FilesExpected,
    int? FilesVerified,
    long? BytesTotal,
    long? BytesTransferred,
    double? ProgressPercent,
    string? RequestedBy,
    string? Reason,
    DateTimeOffset CreatedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt,
    FailureClassDto FailureClass,
    string? ErrorMessage,
    bool RequiresDecryptionKey = false,
    string? DecryptionKeyFingerprint = null,
    DateTimeOffset? KeyRequestExpiresAt = null);

public sealed record RestoreDetailDto(RestoreSummaryDto Summary, string? Log, OffloadSummaryDto RecoveryPoint, IReadOnlyList<TimelineEntryDto> Timeline);

// ───────────────────────────── Dashboard ─────────────────────────────
public sealed record DashboardDto(
    DateTimeOffset From,
    DateTimeOffset To,
    DashboardKpisDto Kpis,
    IReadOnlyList<DailyRunPointDto> RunTrend,
    IReadOnlyList<DailyDurationPointDto> DurationTrend,
    IReadOnlyList<StoragePointDto> StorageGrowth,
    IReadOnlyList<FailureSliceDto> FailureDistribution,
    IReadOnlyList<UpcomingRunDto> Upcoming,
    IReadOnlyList<AttentionItemDto> Attention,
    IReadOnlyList<RunSummaryDto> RecentRuns,
    IReadOnlyList<RestoreSummaryDto> RecentRestores,
    SystemHealthDto System);

/// <summary>Dashboard KPIs. <c>SuccessRate</c> is a percentage in the range 0–100 (null when no runs finished).</summary>
public sealed record DashboardKpisDto(
    int ProtectedServers,
    int TotalServers,
    int ActiveJobs,
    int ScheduledJobs,
    int RunsTotal,
    int RunsSucceeded,
    int RunsFailed,
    int RunsRunning,
    double? SuccessRate,
    DateTimeOffset? LastSuccessfulBackupAt,
    long StoredBytes,
    int RecoveryPoints,
    int RestoresInRange,
    int JobsNeedingAttention);

public sealed record DailyRunPointDto(DateOnly Date, int Succeeded, int Failed, int Cancelled);

public sealed record DailyDurationPointDto(DateOnly Date, double? AverageSeconds, double? MaxSeconds);

public sealed record StoragePointDto(DateOnly Date, long Bytes);

public sealed record FailureSliceDto(FailureClassDto FailureClass, int Count);

public sealed record UpcomingRunDto(long JobId, string JobName, string ServerName, DateTimeOffset NextRunAt, string ScheduleDescription);

public sealed record AttentionItemDto(long JobId, string JobName, string ServerName, HealthState Severity, string Reason, DateTimeOffset? LastRunAt);

public sealed record SystemHealthDto(
    bool DatabaseHealthy,
    bool WorkerOnline,
    DateTimeOffset? WorkerLastSeenAt,
    int WorkerInstances,
    bool WorkerNodeConfigured,
    bool WorkerNodeTrusted,
    bool NfsConfigured,
    int UntrustedServers,
    int DestinationsFailing);

public sealed record AlertDto(string Id, HealthState Severity, string Title, string Message, DateTimeOffset OccurredAt, string? Link);

// ───────────────────────────── Audit & search ─────────────────────────────
public sealed record AuditEventDto(
    long Id,
    DateTimeOffset OccurredAt,
    long? ActorUserId,
    string ActorName,
    string? ActorIp,
    string Action,
    string? ResourceType,
    string? ResourceId,
    AuditOutcomeDto Outcome,
    string? CorrelationId,
    string? DetailsJson);

public sealed record SearchResultDto(string Type, long Id, string Title, string? Subtitle, string Url);

public sealed record SystemInfoDto(string Version, string Environment, IReadOnlyList<WorkerInstanceDto> Workers, DateTimeOffset ServerTime);

public sealed record WorkerInstanceDto(string WorkerId, string HostName, string Version, DateTimeOffset StartedAt, DateTimeOffset LastSeenAt, int ActiveOperations, bool Online);

// ───────────────────────────── Execution host ─────────────────────────────
/// <summary>A tool detected on the execution host. <see cref="Version"/> is null when the binary is not installed.</summary>
public sealed record ToolCapabilityDto(string Name, string Category, string? Version);

public sealed record HostDiskDto(string Path, string FileSystem, long SizeBytes, long AvailableBytes);

public sealed record HostServiceDto(string Name, string State);

/// <summary>Result of the fixed, read-only capability probe run over SSH on the execution host.</summary>
public sealed record ExecutionHostProbeDto(
    DateTimeOffset ProbedAt,
    string Hostname,
    string OperatingSystem,
    string Kernel,
    int Cpus,
    long MemoryBytes,
    long MemoryAvailableBytes,
    long UptimeSeconds,
    IReadOnlyList<ToolCapabilityDto> Tools,
    IReadOnlyList<HostDiskDto> Disks,
    IReadOnlyList<HostServiceDto> Services);

/// <summary>A managed server reached through the execution host (never directly from the backend).</summary>
public sealed record ExecutionTargetDto(long ServerId, string Name, string Host, int Port, bool HostKeyTrusted, bool? LastTestSucceeded, DateTimeOffset? LastTestAt, int Jobs);

public sealed record ExecutionHostDto(
    WorkerNodeDto Node,
    ExecutionHostProbeDto? Probe,
    int RunningBackups,
    int RunningTransfers,
    int RunningRestores,
    int CompletedLast24h,
    int FailedLast24h,
    IReadOnlyList<ExecutionTargetDto> Targets);
