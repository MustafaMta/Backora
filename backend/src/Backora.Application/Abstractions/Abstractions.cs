using Backora.Domain;
using Backora.Domain.Backups;
using Backora.Domain.Infrastructure;
using Backora.Domain.Platform;
using Microsoft.EntityFrameworkCore;

namespace Backora.Application.Abstractions;

public interface IAppDbContext
{
    DbSet<Server> Servers { get; }
    DbSet<WorkerNode> WorkerNodes { get; }
    DbSet<StorageSettings> StorageSettings { get; }
    DbSet<SshSettings> SshSettings { get; }
    DbSet<Variable> Variables { get; }
    DbSet<S3Destination> S3Destinations { get; }
    DbSet<S3DestinationEncryptionKey> S3DestinationEncryptionKeys { get; }
    DbSet<ScriptTemplate> ScriptTemplates { get; }
    DbSet<BackupJob> BackupJobs { get; }
    DbSet<BackupRun> BackupRuns { get; }
    DbSet<BackupArtifact> BackupArtifacts { get; }
    DbSet<OffloadRun> OffloadRuns { get; }
    DbSet<BackupJobReplicationTarget> BackupJobReplicationTargets { get; }
    DbSet<RestoreRun> RestoreRuns { get; }
    DbSet<SmtpSettings> SmtpSettings { get; }
    DbSet<TeamsSettings> TeamsSettings { get; }
    DbSet<NotificationSettings> NotificationSettings { get; }
    DbSet<NotificationMessage> NotificationMessages { get; }
    DbSet<AuditEvent> AuditEvents { get; }
    DbSet<ApiToken> ApiTokens { get; }
    DbSet<IdempotencyRecord> IdempotencyRecords { get; }
    DbSet<WorkerHeartbeat> WorkerHeartbeats { get; }
    DbSet<ClusterLock> ClusterLocks { get; }
    DbSet<OperationLease> OperationLeases { get; }

    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}

public interface IClock
{
    DateTimeOffset UtcNow { get; }
}

public sealed class SystemClock : IClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}

/// <summary>Identity of the caller for authorization, auditing and attribution.</summary>
public interface ICurrentUser
{
    long? UserId { get; }
    string Name { get; }
    string? IpAddress { get; }
    string? CorrelationId { get; }
    bool IsAuthenticated { get; }
    bool HasPermission(string permission);
    IReadOnlyCollection<string> Roles { get; }
}

/// <summary>Authenticated envelope encryption for secrets at rest. Keys never live in the database.</summary>
public interface ISecretProtector
{
    string Protect(string plaintext);
    string Unprotect(string ciphertext);
    bool NeedsReEncryption(string ciphertext);
}

public interface IAuditLog
{
    /// <summary>Adds an audit event to the current unit of work (persisted with SaveChanges).</summary>
    void Record(string action, string? resourceType, string? resourceId, AuditOutcome outcome = AuditOutcome.Success, object? details = null);

    /// <summary>Writes an audit event immediately in its own transaction (used for failures/denials).</summary>
    Task RecordNowAsync(string action, string? resourceType, string? resourceId, AuditOutcome outcome, object? details = null, CancellationToken ct = default);
}

public static class OperationKinds
{
    public const string Backup = "backup";
    public const string Offload = "offload";
    public const string Restore = "restore";
}

/// <summary>Cluster-safe ownership of background operations and singleton tasks.</summary>
public interface ILeaseStore
{
    /// <summary>Claims the next due operation of <paramref name="kind"/> not owned by a live worker.</summary>
    Task<long?> ClaimNextAsync(string kind, string owner, TimeSpan duration, CancellationToken ct);

    /// <summary>Extends the lease; returns false if ownership was lost.</summary>
    Task<bool> RenewAsync(string kind, long operationId, string owner, TimeSpan duration, CancellationToken ct);

    Task ReleaseAsync(string kind, long operationId, string owner, CancellationToken ct);

    /// <summary>Acquires/renews a named cluster lock (scheduler, retention). Returns true when held.</summary>
    Task<bool> TryAcquireClusterLockAsync(string name, string owner, TimeSpan duration, CancellationToken ct);
}

// ───────────────────────────── Remote execution ─────────────────────────────

public sealed record SshEndpoint(string Host, int Port, string User, string PrivateKeyPem, string? TrustedFingerprint);

public sealed record HostKeyObservation(string Host, int Port, string Fingerprint, bool Trusted);

public sealed record CommandResult(int ExitCode, string StdOut, string StdErr)
{
    public bool Succeeded => ExitCode == 0;
}

public class RemoteOperationException : Exception
{
    public RemoteOperationException(FailureClass failureClass, string message, Exception? inner = null)
        : base(message, inner) => FailureClass = failureClass;

    public FailureClass FailureClass { get; }
}

public sealed class HostKeyNotTrustedException : RemoteOperationException
{
    public HostKeyNotTrustedException(string host, string presented, string? expected)
        : base(FailureClass.HostKeyMismatch,
            expected is null
                ? $"Host key for {host} is not trusted yet (presented {presented}). Verify and trust the fingerprint first."
                : $"HOST KEY MISMATCH for {host}: expected {expected} but server presented {presented}. Connection refused.")
    {
        Presented = presented;
        Expected = expected;
    }

    public string Presented { get; }
    public string? Expected { get; }
}

/// <summary>A live SSH session able to run commands and write files.</summary>
public interface IRemoteSession : IAsyncDisposable
{
    Task<CommandResult> RunAsync(string command, TimeSpan timeout, CancellationToken ct);
    /// <summary>Writes a file via SFTP. <paramref name="mode"/> uses octal digits written as decimal (e.g. 600, 700).</summary>
    Task WriteFileAsync(string path, string content, short mode, CancellationToken ct);

    /// <summary>
    /// Runs <paramref name="command"/> and writes <paramref name="stdin"/> to its standard input, then closes it.
    /// Used to hand secrets to a remote process without putting them on a command line or in a file.
    /// The caller owns (and zeroes) the buffer.
    /// </summary>
    Task<CommandResult> RunWithInputAsync(string command, ReadOnlyMemory<byte> stdin, TimeSpan timeout, CancellationToken ct);

    /// <summary>String convenience overload of <see cref="RunWithInputAsync(string, ReadOnlyMemory{byte}, TimeSpan, CancellationToken)"/>. Prefer the byte overload for secrets.</summary>
    async Task<CommandResult> RunWithInputAsync(string command, string stdin, TimeSpan timeout, CancellationToken ct)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(stdin);
        try { return await RunWithInputAsync(command, bytes, timeout, ct); }
        finally { System.Security.Cryptography.CryptographicOperations.ZeroMemory(bytes); }
    }
}

public interface ISshConnector
{
    /// <summary>Connects to the worker node.</summary>
    Task<IRemoteSession> ConnectWorkerAsync(SshEndpoint worker, CancellationToken ct);

    /// <summary>Connects to a target through an SSH tunnel opened on the worker (jump host).</summary>
    Task<IRemoteSession> ConnectTargetAsync(SshEndpoint worker, SshEndpoint target, CancellationToken ct);

    /// <summary>Probes host keys without requiring trust (used by connection tests).</summary>
    Task<(HostKeyObservation Worker, HostKeyObservation? Target, string? Error, string Stage)> ProbeAsync(SshEndpoint worker, SshEndpoint? target, CancellationToken ct);
}

// ───────────────────────────── Object storage ─────────────────────────────

/// <summary>How to reach a storage site. <see cref="LocalRoot"/> set: a folder on the execution host (filesystem site).</summary>
public sealed record S3Connection(string Endpoint, string Region, string Bucket, string AccessKeyId, string SecretKey, bool ForcePathStyle, string? LocalRoot = null)
{
    public bool IsLocal => LocalRoot is not null;
}

public sealed record StoredObject(string Key, long Size, DateTimeOffset LastModified);

public sealed record S3BrowsePage(IReadOnlyList<string> Folders, IReadOnlyList<StoredObject> Objects, string? NextToken);

public interface IObjectStorage
{
    Task<IReadOnlyList<StoredObject>> ListAsync(S3Connection connection, string prefix, CancellationToken ct);
    /// <summary>One page of objects under <paramref name="prefix"/>; with <paramref name="recursive"/> false, sub-folders come back as <see cref="S3BrowsePage.Folders"/>.</summary>
    Task<S3BrowsePage> BrowseAsync(S3Connection connection, string prefix, string? continuationToken, int maxKeys, bool recursive, CancellationToken ct);
    Task<int> DeletePrefixAsync(S3Connection connection, string prefix, CancellationToken ct);
    Task<IReadOnlyList<Contracts.V1.CheckResultDto>> TestAsync(S3Connection connection, CancellationToken ct);
}

// ───────────────────────────── Notifications ─────────────────────────────

public interface IEmailSender
{
    Task SendAsync(SmtpSettings settings, string? password, IReadOnlyList<string> recipients, string subject, string htmlBody, CancellationToken ct);
}

public interface ITeamsSender
{
    Task SendAsync(string webhookUrl, string jsonPayload, CancellationToken ct);
}
