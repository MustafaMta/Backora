using Backora.Domain.Common;
using Backora.Domain.Operations;

namespace Backora.Domain.Backups;

/// <summary>One execution of a backup job (legacy: backup_job_executions).</summary>
public sealed class BackupRun : LeasedOperation
{
    public long BackupJobId { get; set; }
    public BackupJob? BackupJob { get; set; }
    public long ServerId { get; set; }
    public Infrastructure.Server? Server { get; set; }

    // Snapshots taken at creation so history stays accurate when definitions change.
    public string JobName { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public string TemplateName { get; set; } = string.Empty;
    public int? TemplateRevision { get; set; }
    public string? ScriptSha256 { get; set; }

    public RunStatus Status { get; set; } = RunStatus.Pending;
    public RunTrigger Trigger { get; set; } = RunTrigger.Manual;
    public DateTimeOffset? ScheduledFor { get; set; }
    public long? RequestedByUserId { get; set; }
    public string? RequestedByName { get; set; }

    /// <summary>
    /// Set when someone cleared this failed run from the Failed list ("seen, nothing more to do"): it stays in the history,
    /// but no longer counts as a failure needing attention (Failed list, alerts, "last run failed").
    /// </summary>
    public DateTimeOffset? DismissedAt { get; set; }
    public string? DismissedBy { get; set; }

    public int? ExitCode { get; set; }
    public DateTimeOffset? CancelRequestedAt { get; set; }
    public long? CancelRequestedByUserId { get; set; }
    public DateTimeOffset? TimeoutAt { get; set; }

    /// <summary>Worker clock (unix seconds) when the run started; artifact discovery uses it to find files this run produced.</summary>
    public long? WorkerStartEpoch { get; set; }

    /// <summary>
    /// Live progress while the script runs: bytes written so far and, when the template can tell, the bytes expected
    /// (0/null = unknown). Reported by the template through <c>{dir}/progress</c>; for templates that report nothing,
    /// the bytes written by the run's processes. The rate is smoothed over successive samples.
    /// </summary>
    public long? ProgressBytes { get; set; }
    public long? ProgressTotalBytes { get; set; }
    public string? ProgressStage { get; set; }
    public long? ProgressBytesPerSecond { get; set; }
    public DateTimeOffset? ProgressAt { get; set; }

    public VerificationStatus VerificationStatus { get; set; } = VerificationStatus.NotVerified;
    public long? TotalArtifactBytes { get; set; }
    public int ArtifactCount { get; set; }

    /// <summary>Legacy free-form JSON "output" column, kept for imported rows.</summary>
    public string? LegacyOutput { get; set; }
    public long? LegacyQueueJobId { get; set; }

    public List<BackupArtifact> Artifacts { get; set; } = [];
    public List<OffloadRun> Offloads { get; set; } = [];

    public static readonly RunStatus[] ActiveStatuses =
        [RunStatus.Pending, RunStatus.Starting, RunStatus.Running, RunStatus.Verifying, RunStatus.Cancelling];

    public static readonly RunStatus[] TerminalStatuses = [RunStatus.Completed, RunStatus.Failed, RunStatus.Cancelled];

    public bool IsTerminal => TerminalStatuses.Contains(Status);

    private static readonly Dictionary<RunStatus, RunStatus[]> Allowed = new()
    {
        [RunStatus.Pending] = [RunStatus.Starting, RunStatus.Cancelled, RunStatus.Failed],
        [RunStatus.Starting] = [RunStatus.Running, RunStatus.Pending, RunStatus.Failed, RunStatus.Cancelling, RunStatus.Cancelled],
        [RunStatus.Running] = [RunStatus.Verifying, RunStatus.Failed, RunStatus.Cancelling],
        [RunStatus.Verifying] = [RunStatus.Completed, RunStatus.Failed, RunStatus.Cancelling],
        [RunStatus.Cancelling] = [RunStatus.Cancelled, RunStatus.Failed, RunStatus.Completed],
        [RunStatus.Completed] = [],
        [RunStatus.Failed] = [],
        [RunStatus.Cancelled] = [],
    };

    public static bool CanTransition(RunStatus from, RunStatus to) => Allowed[from].Contains(to);

    public void TransitionTo(RunStatus next, DateTimeOffset now)
    {
        if (!CanTransition(Status, next)) throw new InvalidStateTransitionException(nameof(BackupRun), Status, next);
        Status = next;
        switch (next)
        {
            case RunStatus.Starting:
                Attempt++;
                StartedAt ??= now;
                break;
            case RunStatus.Completed or RunStatus.Failed or RunStatus.Cancelled:
                CompletedAt = now;
                break;
        }
    }

    public void Fail(FailureClass failureClass, string message, DateTimeOffset now)
    {
        FailureClass = failureClass;
        ErrorMessage = Truncate(message, 4000);
        AppendLogLine(now, "ERROR " + ErrorMessage);
        TransitionTo(RunStatus.Failed, now);
    }

    /// <summary>
    /// Requests cancellation. A run that has not yet started a remote process is cancelled immediately;
    /// otherwise it moves to <see cref="RunStatus.Cancelling"/> and the worker terminates the process.
    /// Returns false when the run is already terminal.
    /// </summary>
    public bool RequestCancel(long? userId, DateTimeOffset now)
    {
        if (IsTerminal || Status == RunStatus.Cancelling) return false;
        CancelRequestedAt = now;
        CancelRequestedByUserId = userId;
        if (Status == RunStatus.Pending)
        {
            FailureClass = FailureClass.Cancelled;
            ErrorMessage = "Cancelled before the remote process started.";
            TransitionTo(RunStatus.Cancelled, now);
        }
        else
        {
            TransitionTo(RunStatus.Cancelling, now);
        }
        return true;
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];
}

/// <summary>A file produced by a backup run, with integrity metadata (new; legacy had no manifest).</summary>
public sealed class BackupArtifact : Entity
{
    public long BackupRunId { get; set; }
    public BackupRun? BackupRun { get; set; }

    /// <summary>Path relative to the job's NFS directory.</summary>
    public string RelativePath { get; set; } = string.Empty;
    public long SizeBytes { get; set; }
    public string Sha256 { get; set; } = string.Empty;

    /// <summary>
    /// MD5 computed in the same read as the SHA-256 when the backup was verified. Copies are checked against the hash
    /// object storage keeps for each object, so the source does not have to be read a second time. Null for older rows.
    /// </summary>
    public string? Md5 { get; set; }
    public DateTimeOffset ModifiedAt { get; set; }
    public bool LocalCopyDeleted { get; set; }
}
