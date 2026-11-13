using Backora.Contracts.V1;
using Backora.DesignSystem;

namespace Backora.Web.Services;

public readonly record struct StatusView(string Label, StatusTone Tone, string? Icon = null, string? Description = null);

/// <summary>Single source of truth for how operational states are labelled and coloured.</summary>
public static class StatusMap
{
    public static StatusView Run(RunStatusDto s) => s switch
    {
        RunStatusDto.Pending => new("Queued", StatusTone.Neutral, "clock", "Waiting for a worker to pick it up."),
        RunStatusDto.Starting => new("Starting", StatusTone.Running, null, "Connecting to the server and launching the script."),
        RunStatusDto.Running => new("Running", StatusTone.Running, null, "The backup script is executing on the target."),
        RunStatusDto.Verifying => new("Verifying", StatusTone.Running, null, "Checking artifacts and checksums."),
        RunStatusDto.Completed => new("Succeeded", StatusTone.Success, null, "Completed and verified."),
        RunStatusDto.Failed => new("Failed", StatusTone.Danger, null, "The run did not complete successfully."),
        RunStatusDto.Cancelling => new("Cancelling", StatusTone.Warning, null, "Stopping the remote process."),
        RunStatusDto.Cancelled => new("Cancelled", StatusTone.Neutral, null, "Stopped by a user."),
        _ => new(s.ToString(), StatusTone.Unknown),
    };

    public static bool IsTerminal(RunStatusDto s) => s is RunStatusDto.Completed or RunStatusDto.Failed or RunStatusDto.Cancelled;
    public static bool IsTerminal(RestoreStatusDto s) => s is RestoreStatusDto.Completed or RestoreStatusDto.Failed or RestoreStatusDto.Cancelled;
    public static bool IsTerminal(OffloadStatusDto s) => s is OffloadStatusDto.Completed or OffloadStatusDto.Failed or OffloadStatusDto.Cancelled;

    public static StatusView Offload(OffloadStatusDto s) => s switch
    {
        OffloadStatusDto.Pending => new("Upload queued", StatusTone.Neutral),
        OffloadStatusDto.Running => new("Uploading", StatusTone.Running),
        OffloadStatusDto.Verifying => new("Verifying upload", StatusTone.Running),
        OffloadStatusDto.Completed => new("Stored", StatusTone.Success),
        OffloadStatusDto.Failed => new("Upload failed", StatusTone.Danger),
        OffloadStatusDto.Cancelled => new("Upload cancelled", StatusTone.Neutral),
        _ => new(s.ToString(), StatusTone.Unknown),
    };

    /// <summary>Like <see cref="Offload(OffloadStatusDto)"/>, worded as a copy for S3 → S3 DR replicas.</summary>
    public static StatusView Offload(OffloadSummaryDto o) => !o.IsReplica ? Offload(o.Status) : o.Status switch
    {
        OffloadStatusDto.Pending => new("Copy queued", StatusTone.Neutral),
        OffloadStatusDto.Running => new("Copying", StatusTone.Running),
        OffloadStatusDto.Verifying => new("Verifying copy", StatusTone.Running),
        OffloadStatusDto.Completed => new("Stored", StatusTone.Success),
        OffloadStatusDto.Failed => new("Copy failed", StatusTone.Danger),
        OffloadStatusDto.Cancelled => new("Copy cancelled", StatusTone.Neutral),
        _ => Offload(o.Status),
    };

    public static StatusView Restore(RestoreStatusDto s) => s switch
    {
        RestoreStatusDto.Pending => new("Queued", StatusTone.Neutral),
        RestoreStatusDto.Running => new("Restoring", StatusTone.Running),
        RestoreStatusDto.Verifying => new("Verifying", StatusTone.Running),
        RestoreStatusDto.AwaitingKey => new("Waiting for key", StatusTone.Warning),
        RestoreStatusDto.Completed => new("Restored", StatusTone.Success),
        RestoreStatusDto.Failed => new("Failed", StatusTone.Danger),
        RestoreStatusDto.Cancelling => new("Cancelling", StatusTone.Warning),
        RestoreStatusDto.Cancelled => new("Cancelled", StatusTone.Neutral),
        _ => new(s.ToString(), StatusTone.Unknown),
    };

    public static StatusView Health(HealthState s) => s switch
    {
        HealthState.Healthy => new("Healthy", StatusTone.Success),
        HealthState.Warning => new("Warning", StatusTone.Warning),
        HealthState.Critical => new("Critical", StatusTone.Danger),
        _ => new("Unknown", StatusTone.Unknown),
    };

    public static StatusView Verification(VerificationStatusDto s) => s switch
    {
        VerificationStatusDto.Verified => new("Verified", StatusTone.Success, "check"),
        VerificationStatusDto.Failed => new("Verification failed", StatusTone.Danger, "x"),
        VerificationStatusDto.Partial => new("Partially verified", StatusTone.Warning, "alert"),
        _ => new("Not verified", StatusTone.Neutral),
    };

    public static StatusView Job(JobStatusDto s) => s == JobStatusDto.Active
        ? new("Active", StatusTone.Success)
        : new("Paused", StatusTone.Neutral, "pause");

    public static StatusView Audit(AuditOutcomeDto s) => s switch
    {
        AuditOutcomeDto.Success => new("Success", StatusTone.Success),
        AuditOutcomeDto.Failure => new("Failure", StatusTone.Danger),
        AuditOutcomeDto.Denied => new("Denied", StatusTone.Warning),
        _ => new(s.ToString(), StatusTone.Unknown),
    };

    public static StatusView Cleanup(CleanupStatusDto s) => s switch
    {
        CleanupStatusDto.Completed => new("Local copy removed", StatusTone.Success),
        CleanupStatusDto.Failed => new("Local cleanup failed", StatusTone.Warning),
        CleanupStatusDto.Pending => new("Cleanup pending", StatusTone.Neutral),
        CleanupStatusDto.Skipped => new("Local copy kept", StatusTone.Neutral),
        _ => new("Not required", StatusTone.Neutral),
    };

    public static StatusView Retention(RetentionStateDto s) => s switch
    {
        RetentionStateDto.Active => new("Retained", StatusTone.Success),
        RetentionStateDto.Expired => new("Expired", StatusTone.Warning),
        RetentionStateDto.Deleting => new("Deleting", StatusTone.Running),
        RetentionStateDto.Deleted => new("Deleted", StatusTone.Neutral),
        RetentionStateDto.DeleteFailed => new("Delete failed", StatusTone.Danger),
        _ => new(s.ToString(), StatusTone.Unknown),
    };

    public static StatusView Check(CheckState s) => s switch
    {
        CheckState.Passed => new("Passed", StatusTone.Success, "check-circle"),
        CheckState.Warning => new("Warning", StatusTone.Warning, "alert"),
        CheckState.Failed => new("Failed", StatusTone.Danger, "x-circle"),
        _ => new("Skipped", StatusTone.Neutral, "circle-dashed"),
    };

    public static string Trigger(RunTriggerDto t) => t switch
    {
        RunTriggerDto.Manual => "Manual",
        RunTriggerDto.Scheduled => "Scheduled",
        RunTriggerDto.Api => "API",
        RunTriggerDto.Retry => "Retry",
        RunTriggerDto.Imported => "Imported",
        _ => t.ToString(),
    };

    public static string Failure(FailureClassDto f) => f switch
    {
        FailureClassDto.None => "—",
        FailureClassDto.Configuration => "Configuration",
        FailureClassDto.Connectivity => "Connectivity",
        FailureClassDto.Authentication => "Authentication",
        FailureClassDto.HostKeyMismatch => "Host key mismatch",
        FailureClassDto.ScriptError => "Script error",
        FailureClassDto.NoArtifacts => "No artifacts produced",
        FailureClassDto.VerificationFailed => "Verification failed",
        FailureClassDto.Timeout => "Timeout",
        FailureClassDto.ProcessLost => "Process lost",
        FailureClassDto.StorageError => "Storage error",
        FailureClassDto.Cancelled => "Cancelled",
        FailureClassDto.Internal => "Internal error",
        _ => f.ToString(),
    };

    public static string FailureHint(FailureClassDto f) => f switch
    {
        FailureClassDto.Configuration => "Check the job, template variables and NFS/worker settings.",
        FailureClassDto.Connectivity => "The worker or target server could not be reached over SSH.",
        FailureClassDto.Authentication => "SSH key authentication was rejected. Verify the key installed on the server.",
        FailureClassDto.HostKeyMismatch => "The server presented a different host key than the trusted fingerprint. Investigate before re-trusting.",
        FailureClassDto.ScriptError => "The backup script exited with a non-zero status. Inspect the log output.",
        FailureClassDto.NoArtifacts => "The script finished but no backup files appeared in the job's NFS directory.",
        FailureClassDto.VerificationFailed => "Checksums or sizes did not match the manifest.",
        FailureClassDto.Timeout => "The run exceeded the job's timeout and was stopped.",
        FailureClassDto.ProcessLost => "The remote process disappeared without reporting an exit code.",
        FailureClassDto.StorageError => "The S3 destination rejected or failed the operation.",
        _ => string.Empty,
    };

    public static string FailureColor(FailureClassDto f) => f switch
    {
        FailureClassDto.ScriptError => "var(--chart-5)",
        FailureClassDto.Connectivity => "var(--chart-2)",
        FailureClassDto.Authentication or FailureClassDto.HostKeyMismatch => "var(--chart-4)",
        FailureClassDto.Timeout => "var(--chart-3)",
        FailureClassDto.NoArtifacts or FailureClassDto.VerificationFailed => "var(--chart-7)",
        FailureClassDto.StorageError => "var(--chart-1)",
        FailureClassDto.Configuration => "var(--chart-6)",
        _ => "var(--chart-8)",
    };
}
