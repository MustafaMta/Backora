using System.Globalization;
using System.Text.Json;
using Backora.Application.Abstractions;
using Backora.Application.Common;
using Backora.Application.Execution;
using Backora.Contracts.V1;
using Backora.Domain;
using Backora.Domain.Execution;
using Microsoft.EntityFrameworkCore;

namespace Backora.Application.Inventory;

/// <summary>
/// The execution host (e.g. 10.0.0.10) is the only machine the backend opens SSH sessions to. Every backup,
/// transfer, encryption and restore command runs there; managed servers are reached from it (SSH jump host).
/// This service reports its state and runs a fixed, read-only capability probe — never user-supplied commands.
/// </summary>
public sealed class ExecutionHostService(IAppDbContext db, ExecutionHostKeys keys, ISshConnector ssh, IClock clock, SettingsService settings, IAuditLog audit)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Binaries Backora can use, with the category shown in the UI. Detection only — nothing is installed.</summary>
    internal static readonly (string Name, string Category)[] Tools =
    [
        ("pg_dump", "PostgreSQL"), ("pg_restore", "PostgreSQL"), ("pg_basebackup", "PostgreSQL"), ("psql", "PostgreSQL"),
        ("mariadb-backup", "MariaDB"), ("mariadb-dump", "MariaDB"), ("mariadb", "MariaDB"), ("mysqldump", "MariaDB"),
        ("rclone", "Transfer"), ("rsync", "Transfer"),
        ("age", "Encryption"), ("gpg", "Encryption"), ("sha256sum", "Integrity"),
        ("tar", "Archive"), ("gzip", "Archive"), ("pigz", "Archive"), ("zstd", "Archive"),
    ];

    internal static readonly string[] Services = ["postgresql", "mariadb", "mysqld", "docker", "sshd"];

    public async Task<ExecutionHostDto> GetAsync(CancellationToken ct)
    {
        var node = await settings.GetWorkerAsync(ct);
        var w = await db.WorkerNodes.AsNoTracking().FirstOrDefaultAsync(ct);
        var probe = w?.CapabilitiesJson is { Length: > 0 } json ? JsonSerializer.Deserialize<ExecutionHostProbeDto>(json, Json) : null;

        var since = clock.UtcNow.AddHours(-24);
        var runningBackups = await db.BackupRuns.CountAsync(r => r.Status == RunStatus.Starting || r.Status == RunStatus.Running || r.Status == RunStatus.Verifying, ct);
        var runningTransfers = await db.OffloadRuns.CountAsync(o => o.Status == OffloadStatus.Running || o.Status == OffloadStatus.Verifying, ct);
        var runningRestores = await db.RestoreRuns.CountAsync(r => r.Status == RestoreStatus.Running || r.Status == RestoreStatus.Verifying || r.Status == RestoreStatus.AwaitingKey, ct);
        var completed = await db.BackupRuns.CountAsync(r => r.Status == RunStatus.Completed && r.CompletedAt >= since, ct);
        var failed = await db.BackupRuns.CountAsync(r => r.Status == RunStatus.Failed && r.CompletedAt >= since, ct);

        var jobCounts = await db.BackupJobs.AsNoTracking().GroupBy(j => j.ServerId).Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count, ct);
        var targets = await db.Servers.AsNoTracking().OrderBy(s => s.Name)
            .Select(s => new { s.Id, s.Name, s.Host, s.SshPort, s.HostKeyFingerprint, s.LastConnectionTestSucceeded, s.LastConnectionTestAt })
            .ToListAsync(ct);

        return new ExecutionHostDto(node, probe, runningBackups, runningTransfers, runningRestores, completed, failed,
            targets.Select(t => new ExecutionTargetDto(t.Id, t.Name, t.Host, t.SshPort, t.HostKeyFingerprint is not null, t.LastConnectionTestSucceeded, t.LastConnectionTestAt,
                jobCounts.TryGetValue(t.Id, out var n) ? n : 0)).ToList());
    }

    /// <summary>Runs the read-only discovery script on the execution host (host key must already be trusted) and stores the result.</summary>
    public async Task<ExecutionHostProbeDto> ProbeAsync(CancellationToken ct)
    {
        var w = await db.WorkerNodes.FirstOrDefaultAsync(ct) ?? throw new DomainRuleException("worker_not_configured", "Configure the execution host first (Settings → Execution host).");
        if (w.HostKeyFingerprint is null) throw new DomainRuleException("host_key_untrusted", "Trust the execution host's SSH host key before probing it.");
        var storage = await db.StorageSettings.AsNoTracking().FirstOrDefaultAsync(ct);

        var endpoint = new SshEndpoint(w.Host, w.SshPort, w.SshUser, keys.WorkerKey(w), w.HostKeyFingerprint);
        await using var session = await ssh.ConnectWorkerAsync(endpoint, ct);
        var result = await session.RunAsync(BuildScript(w.WorkDirectory, storage?.NfsPath), TimeSpan.FromSeconds(60), ct);
        if (!result.Succeeded && string.IsNullOrWhiteSpace(result.StdOut))
        {
            throw new RemoteOperationException(FailureClass.Connectivity, "The capability probe did not return any output.");
        }

        var probe = Parse(result.StdOut, clock.UtcNow);
        w.CapabilitiesJson = JsonSerializer.Serialize(probe, Json);
        w.CapabilitiesProbedAt = probe.ProbedAt;
        audit.Record("execution_host.probe", "WorkerNode", w.Id.ToString(CultureInfo.InvariantCulture), AuditOutcome.Success,
            new { probe.Hostname, Tools = probe.Tools.Count(t => t.Version is not null) });
        await db.SaveChangesAsync(ct);
        return probe;
    }

    /// <summary>Fixed script: only paths from admin settings are interpolated, and they are shell-quoted.</summary>
    public static string BuildScript(string workDirectory, string? nfsPath)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append("echo \"hostname=$(hostname)\"; ");
        sb.Append("( . /etc/os-release 2>/dev/null && echo \"os=$PRETTY_NAME\" ) || echo \"os=$(uname -s)\"; ");
        sb.Append("echo \"kernel=$(uname -r)\"; echo \"cpus=$(nproc 2>/dev/null || echo 0)\"; ");
        sb.Append("awk '/^MemTotal:/{print \"mem_kb=\"$2} /^MemAvailable:/{print \"mem_avail_kb=\"$2}' /proc/meminfo 2>/dev/null; ");
        sb.Append("echo \"uptime_s=$(cut -d. -f1 /proc/uptime 2>/dev/null || echo 0)\"; ");
        foreach (var (name, _) in Tools)
        {
            sb.Append(CultureInfo.InvariantCulture, $"if command -v {name} >/dev/null 2>&1; then echo \"tool.{name}=$({name} --version 2>&1 | head -n 1 | tr -d '\\r')\"; else echo \"tool.{name}=\"; fi; ");
        }
        foreach (var svc in Services)
        {
            sb.Append(CultureInfo.InvariantCulture, $"echo \"svc.{svc}=$(systemctl is-active {svc} 2>/dev/null || true)\"; ");
        }
        var paths = new List<string> { "/" };
        if (IsPlainPath(workDirectory)) paths.Add(workDirectory);
        if (IsPlainPath(nfsPath)) paths.Add(nfsPath!);
        foreach (var p in paths.Distinct(StringComparer.Ordinal))
        {
            var q = ShellQuote.Quote(p);
            // Probe the nearest existing ancestor so a missing work directory still reports its filesystem.
            sb.Append(CultureInfo.InvariantCulture, $"d={q}; while [ ! -e \"$d\" ] && [ \"$d\" != / ]; do d=$(dirname \"$d\"); done; ");
            sb.Append(CultureInfo.InvariantCulture, $"df -PkT \"$d\" 2>/dev/null | awk -v p={q} 'NR==2{{print \"disk=\"p\"|\"$2\"|\"$3*1024\"|\"$5*1024}}'; ");
        }
        return sb.ToString();
    }

    /// <summary>Defence in depth on top of quoting: the probe only ever touches plain absolute paths.</summary>
    private static bool IsPlainPath(string? path) =>
        !string.IsNullOrWhiteSpace(path) && ShellQuote.IsSafeAbsolutePath(path) &&
        System.Text.RegularExpressions.Regex.IsMatch(path, "^/[A-Za-z0-9._/-]*$");

    public static ExecutionHostProbeDto Parse(string output, DateTimeOffset now)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        var disks = new List<HostDiskDto>();
        foreach (var raw in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var line = raw.Trim();
            var eq = line.IndexOf('=', StringComparison.Ordinal);
            if (eq <= 0) continue;
            var key = line[..eq];
            var value = line[(eq + 1)..].Trim();
            if (key == "disk")
            {
                var parts = value.Split('|');
                if (parts.Length == 4 && long.TryParse(parts[2], out var size) && long.TryParse(parts[3], out var avail))
                {
                    disks.Add(new HostDiskDto(parts[0], parts[1], size, avail));
                }
                continue;
            }
            values[key] = value.Length > 200 ? value[..200] : value;
        }

        string V(string k) => values.TryGetValue(k, out var v) ? v : string.Empty;
        long L(string k) => long.TryParse(V(k), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : 0;

        var tools = Tools.Select(t => new ToolCapabilityDto(t.Name, t.Category, V($"tool.{t.Name}") is { Length: > 0 } ver ? ver : null)).ToList();
        var services = Services.Select(s => new HostServiceDto(s, V($"svc.{s}") is { Length: > 0 } st ? st : "unknown")).Where(s => s.State != "unknown" && s.State != "inactive" || s.Name is "postgresql" or "mariadb" or "sshd").ToList();
        return new ExecutionHostProbeDto(now, V("hostname"), V("os"), V("kernel"), (int)L("cpus"), L("mem_kb") * 1024, L("mem_avail_kb") * 1024, L("uptime_s"), tools, disks, services);
    }
}
