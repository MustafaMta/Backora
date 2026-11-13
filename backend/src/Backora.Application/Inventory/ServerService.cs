using System.Diagnostics;
using Backora.Application.Abstractions;
using Backora.Application.Common;
using Backora.Application.Execution;
using Backora.Contracts.Security;
using Backora.Contracts.V1;
using Backora.Domain;
using Backora.Domain.Backups;
using Backora.Domain.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Backora.Application.Inventory;

public sealed class ServerService(IAppDbContext db, ICurrentUser user, ISecretProtector secrets, IAuditLog audit, IClock clock, ISshConnector ssh, Execution.ExecutionHostKeys keys)
{
    public async Task<PagedResult<ServerDto>> ListAsync(string? q, PageRequest page, CancellationToken ct)
    {
        var query = db.Servers.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(q))
        {
            var term = $"%{q.Trim()}%";
            query = query.Where(s => EF.Functions.ILike(s.Name, term) || EF.Functions.ILike(s.Host, term));
        }
        query = (page.SortField, page.Descending) switch
        {
            ("host", false) => query.OrderBy(s => s.Host),
            ("host", true) => query.OrderByDescending(s => s.Host),
            ("createdat", false) => query.OrderBy(s => s.CreatedAt),
            ("createdat", true) => query.OrderByDescending(s => s.CreatedAt),
            (_, true) => query.OrderByDescending(s => s.Name),
            _ => query.OrderBy(s => s.Name),
        };
        var total = await query.LongCountAsync(ct);
        var servers = await query.Skip(page.Skip).Take(page.SafePageSize).ToListAsync(ct);
        var items = await DecorateAsync(servers, ct);
        return new PagedResult<ServerDto>(items, page.SafePage, page.SafePageSize, total);
    }

    public async Task<ServerDto> GetAsync(long id, CancellationToken ct)
    {
        var server = await db.Servers.AsNoTracking().FirstOrDefaultAsync(s => s.Id == id, ct) ?? throw new NotFoundException("Server", id);
        return (await DecorateAsync([server], ct))[0];
    }

    public async Task<ServerDto> CreateAsync(ServerUpsertRequest request, CancellationToken ct)
    {
        await EnsureUniqueNameAsync(request.Name, null, ct);
        var server = new Server { Name = request.Name.Trim() };
        Apply(server, request);
        db.Servers.Add(server);
        await db.SaveChangesAsync(ct);
        audit.Record("server.created", "Server", server.Id.ToString(), details: new { server.Name, server.Host, server.SshPort, server.SshUser, customKey = server.PrivateKeyCiphertext is not null });
        await db.SaveChangesAsync(ct);
        return await GetAsync(server.Id, ct);
    }

    public async Task<ServerDto> UpdateAsync(long id, ServerUpsertRequest request, CancellationToken ct)
    {
        var server = await db.Servers.FirstOrDefaultAsync(s => s.Id == id, ct) ?? throw new NotFoundException("Server", id);
        Concurrency.EnsureVersion(server, request.Version);
        await EnsureUniqueNameAsync(request.Name, id, ct);
        var endpointChanged = server.Host != request.Host.Trim() || server.SshPort != request.SshPort;
        server.Name = request.Name.Trim();
        Apply(server, request);
        if (endpointChanged)
        {
            // A different host/port may present a different key: require re-verification.
            server.HostKeyFingerprint = null;
            server.HostKeyTrustedAt = null;
            server.HostKeyTrustedByUserId = null;
            server.LastConnectionTestSucceeded = null;
        }
        audit.Record("server.updated", "Server", id.ToString(), details: new { server.Name, server.Host, server.SshPort, server.SshUser, keyChanged = request.PrivateKey is not null || request.ClearPrivateKey, endpointChanged });
        await db.SaveChangesAsync(ct);
        return await GetAsync(id, ct);
    }

    public async Task DeleteAsync(long id, CancellationToken ct)
    {
        var server = await db.Servers.FirstOrDefaultAsync(s => s.Id == id, ct) ?? throw new NotFoundException("Server", id);
        var jobs = await db.BackupJobs.CountAsync(j => j.ServerId == id && j.DeletedAt == null, ct);
        if (jobs > 0) throw new ConflictException("server_in_use", $"The server is used by {jobs} backup job(s). Reassign or delete those jobs first.");
        var hasHistory = await db.BackupRuns.AnyAsync(r => r.ServerId == id, ct) || await db.BackupJobs.AnyAsync(j => j.ServerId == id, ct);
        if (hasHistory) throw new ConflictException("server_has_history", "The server has backup history. Servers with history are kept to preserve the audit trail and recovery points.");
        db.Servers.Remove(server);
        audit.Record("server.deleted", "Server", id.ToString(), details: new { server.Name, server.Host });
        await db.SaveChangesAsync(ct);
    }

    public async Task<ConnectionTestResultDto> TestAsync(ConnectionTestRequest request, CancellationToken ct)
    {
        Server? stored = null;
        if (request.ServerId is { } sid)
        {
            stored = await db.Servers.FirstOrDefaultAsync(s => s.Id == sid, ct) ?? throw new NotFoundException("Server", sid);
        }
        var sw = Stopwatch.StartNew();
        ExecutionEnvironment env;
        try
        {
            env = await new ExecutionEnvironmentFactory(db, secrets, keys).CreateForProbeAsync(ct);
        }
        catch (RemoteOperationException ex)
        {
            return new ConnectionTestResultDto(false, ex.Message, "Configuration", null, false, null, false, sw.ElapsedMilliseconds);
        }

        string? key = !string.IsNullOrWhiteSpace(request.PrivateKey) ? request.PrivateKey
            : stored?.PrivateKeyCiphertext is { } c ? secrets.Unprotect(c) : env.DefaultTargetKey;
        if (key is null)
        {
            return new ConnectionTestResultDto(false, "No SSH private key available. Provide a key or configure the global default key.", "Configuration", null, false, null, false, sw.ElapsedMilliseconds);
        }

        // Only reuse the stored trusted fingerprint when testing the stored endpoint unchanged.
        var sameEndpoint = stored is not null && stored.Host == request.Host && stored.SshPort == request.SshPort;
        var target = new SshEndpoint(request.Host, request.SshPort, request.SshUser, key, sameEndpoint ? stored!.HostKeyFingerprint : null);
        var (worker, targetObs, error, stage) = await ssh.ProbeAsync(env.Worker, target, ct);
        var success = error is null && targetObs is { Trusted: true };
        var message = error ?? "SSH connection through the worker succeeded and both host keys are trusted.";
        if (error is null && targetObs is { Trusted: false }) message = "Connected. Review and trust the target host key fingerprint to enable backups.";

        if (stored is not null && sameEndpoint)
        {
            stored.LastConnectionTestAt = clock.UtcNow;
            stored.LastConnectionTestSucceeded = success;
            stored.LastConnectionTestMessage = message.Length > 2000 ? message[..2000] : message;
        }
        audit.Record("server.connection_tested", "Server", request.ServerId?.ToString(), success ? AuditOutcome.Success : AuditOutcome.Failure,
            new { request.Host, request.SshPort, request.SshUser, stage, success });
        await db.SaveChangesAsync(ct);
        return new ConnectionTestResultDto(success, message, stage, worker.Fingerprint, worker.Trusted, targetObs?.Fingerprint, targetObs?.Trusted ?? false, sw.ElapsedMilliseconds);
    }

    public async Task<ServerDto> TrustHostKeyAsync(long id, string fingerprint, CancellationToken ct)
    {
        if (!fingerprint.StartsWith("SHA256:", StringComparison.Ordinal) || fingerprint.Length is < 20 or > 100)
        {
            throw RequestValidationException.For("fingerprint", "Expected an OpenSSH SHA256 fingerprint (SHA256:…).");
        }
        var server = await db.Servers.FirstOrDefaultAsync(s => s.Id == id, ct) ?? throw new NotFoundException("Server", id);
        var previous = server.HostKeyFingerprint;
        server.HostKeyFingerprint = fingerprint;
        server.HostKeyTrustedAt = clock.UtcNow;
        server.HostKeyTrustedByUserId = user.UserId;
        audit.Record("server.host_key_trusted", "Server", id.ToString(), details: new { fingerprint, previous });
        await db.SaveChangesAsync(ct);
        return await GetAsync(id, ct);
    }

    private void Apply(Server server, ServerUpsertRequest request)
    {
        server.Host = request.Host.Trim();
        server.SshPort = request.SshPort;
        server.SshUser = request.SshUser.Trim();
        server.Description = request.Description?.Trim();
        if (!string.IsNullOrWhiteSpace(request.PrivateKey))
        {
            if (!user.HasPermission(Permissions.CredentialsManage)) throw new ForbiddenException("Managing SSH keys requires the credentials.manage permission.");
            server.PrivateKeyCiphertext = secrets.Protect(request.PrivateKey.Trim());
        }
        else if (request.ClearPrivateKey)
        {
            if (!user.HasPermission(Permissions.CredentialsManage)) throw new ForbiddenException("Managing SSH keys requires the credentials.manage permission.");
            server.PrivateKeyCiphertext = null;
        }
    }

    private async Task EnsureUniqueNameAsync(string name, long? exceptId, CancellationToken ct)
    {
        var trimmed = name.Trim();
        if (await db.Servers.AnyAsync(s => s.Name == trimmed && s.Id != exceptId, ct))
        {
            throw new ConflictException("server_name_taken", $"A server named '{trimmed}' already exists.");
        }
    }

    private async Task<List<ServerDto>> DecorateAsync(List<Server> servers, CancellationToken ct)
    {
        var ids = servers.Select(s => s.Id).ToList();
        var jobCounts = await db.BackupJobs.Where(j => ids.Contains(j.ServerId) && j.DeletedAt == null)
            .GroupBy(j => j.ServerId).Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count, ct);
        var lastRuns = await db.BackupRuns.Where(r => ids.Contains(r.ServerId) && BackupRun.TerminalStatuses.Contains(r.Status))
            .GroupBy(r => r.ServerId)
            .Select(g => new { g.Key, Last = g.OrderByDescending(r => r.CreatedAt).Select(r => new { r.Status, r.CompletedAt }).First(),
                LastSuccess = g.Where(r => r.Status == RunStatus.Completed).Max(r => (DateTimeOffset?)r.CompletedAt) })
            .ToDictionaryAsync(x => x.Key, ct);

        return servers.Select(s =>
        {
            lastRuns.TryGetValue(s.Id, out var lr);
            var failed = lr?.Last.Status == RunStatus.Failed;
            return new ServerDto(s.Id, s.Name, s.Host, s.SshPort, s.SshUser, s.PrivateKeyCiphertext is not null, s.HostKeyFingerprint, s.HostKeyTrustedAt,
                s.LastConnectionTestAt, s.LastConnectionTestSucceeded, s.LastConnectionTestMessage, s.Description,
                jobCounts.GetValueOrDefault(s.Id), lr?.LastSuccess, Mapping.ServerHealth(s, failed), s.Version, s.CreatedAt, s.UpdatedAt);
        }).ToList();
    }
}
