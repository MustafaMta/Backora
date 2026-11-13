using Backora.Application.Abstractions;
using Backora.Domain;
using Backora.Domain.Execution;
using Backora.Domain.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Backora.Application.Execution;

/// <summary>Resolved, decrypted configuration needed to execute remote operations.</summary>
public sealed record ExecutionEnvironment(
    SshEndpoint Worker,
    string WorkerWorkDirectory,
    string RclonePath,
    string NfsHost,
    string NfsPath,
    string? DefaultTargetKey)
{
    public const string TargetWorkDirectory = "/var/tmp/backora";

    public SshEndpoint TargetEndpoint(Server server, string? serverKey)
    {
        var key = serverKey ?? DefaultTargetKey
            ?? throw new RemoteOperationException(FailureClass.Configuration,
                $"No SSH private key for server '{server.Name}': set a server key or the global default SSH key.");
        return new SshEndpoint(server.Host, server.SshPort, server.SshUser, key, server.HostKeyFingerprint);
    }

    public string JobDirectory(string jobName) => ShellQuote.Join(NfsPath, jobName);
}

public sealed class ExecutionEnvironmentFactory(IAppDbContext db, ISecretProtector secrets, ExecutionHostKeys keys)
{
    public async Task<ExecutionEnvironment> CreateAsync(CancellationToken ct)
    {
        var worker = await db.WorkerNodes.AsNoTracking().FirstOrDefaultAsync(ct)
            ?? throw new RemoteOperationException(FailureClass.Configuration, "Worker node is not configured (Settings → Worker).");
        if (string.IsNullOrWhiteSpace(worker.Host) || !keys.HasWorkerKey(worker))
        {
            throw new RemoteOperationException(FailureClass.Configuration, "Worker node configuration is incomplete.");
        }
        if (worker.HostKeyFingerprint is null)
        {
            throw new RemoteOperationException(FailureClass.HostKeyMismatch, "Worker host key has not been trusted yet (Settings → Worker → Test & trust).");
        }

        var storage = await db.StorageSettings.AsNoTracking().FirstOrDefaultAsync(ct);
        if (storage is null || string.IsNullOrWhiteSpace(storage.NfsPath) || !ShellQuote.IsSafeAbsolutePath(storage.NfsPath))
        {
            throw new RemoteOperationException(FailureClass.Configuration, "NFS staging path is not configured (Settings → NFS).");
        }

        var ssh = await db.SshSettings.AsNoTracking().FirstOrDefaultAsync(ct);
        var defaultKey = keys.DefaultTargetKey(ssh);

        var endpoint = new SshEndpoint(worker.Host, worker.SshPort, worker.SshUser, keys.WorkerKey(worker), worker.HostKeyFingerprint);
        return new ExecutionEnvironment(endpoint, worker.WorkDirectory, worker.RclonePath, storage.NfsHost, storage.NfsPath, defaultKey);
    }

    /// <summary>Like <see cref="CreateAsync"/> but allows an untrusted worker host key (connection tests report it).</summary>
    public async Task<ExecutionEnvironment> CreateForProbeAsync(CancellationToken ct)
    {
        var worker = await db.WorkerNodes.AsNoTracking().FirstOrDefaultAsync(ct)
            ?? throw new RemoteOperationException(FailureClass.Configuration, "Worker node is not configured (Settings → Worker).");
        if (string.IsNullOrWhiteSpace(worker.Host) || !keys.HasWorkerKey(worker))
        {
            throw new RemoteOperationException(FailureClass.Configuration, "Worker node configuration is incomplete.");
        }
        var storage = await db.StorageSettings.AsNoTracking().FirstOrDefaultAsync(ct);
        var ssh = await db.SshSettings.AsNoTracking().FirstOrDefaultAsync(ct);
        var defaultKey = keys.DefaultTargetKey(ssh);
        var endpoint = new SshEndpoint(worker.Host, worker.SshPort, worker.SshUser, keys.WorkerKey(worker), worker.HostKeyFingerprint);
        return new ExecutionEnvironment(endpoint, worker.WorkDirectory, worker.RclonePath, storage?.NfsHost ?? string.Empty, storage?.NfsPath ?? string.Empty, defaultKey);
    }

    public string? DecryptServerKey(Server server) =>
        server.PrivateKeyCiphertext is { } c ? secrets.Unprotect(c) : null;
}
