using System.Text.RegularExpressions;
using Backora.Application.Abstractions;
using Backora.Domain.Execution;
using Backora.Domain.Infrastructure;
using Backora.Domain.Platform;
using Backora.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Backora.Tools;

/// <summary>
/// Stores backup-script settings as Backora variables (Templates → Variables): each <c>BACKORA__NAME[__JOB]='value'</c>
/// line of an env file becomes the variable <c>NAME[__JOB]</c>; passwords and passphrases are stored encrypted.
/// Existing variables with the same name are updated. Values are never printed.
/// </summary>
public sealed partial class ScriptVariablesImport(BackoraDbContext db, ISecretProtector secrets)
{
    public async Task<int> RunAsync(Dictionary<string, string?> o, CancellationToken ct)
    {
        var file = o.GetValueOrDefault("env-file") is { Length: > 0 } f ? f : throw new ToolsProgram.UsageException("--env-file <path> is required.");
        var entries = new List<(string Name, string Value)>();
        foreach (var raw in await File.ReadAllLinesAsync(file, ct))
        {
            var m = LineRegex().Match(raw.Trim());
            if (!m.Success) continue;
            var value = m.Groups["sq"].Success ? m.Groups["sq"].Value : m.Groups["dq"].Success ? Regex.Unescape(m.Groups["dq"].Value).Replace("$$", "$", StringComparison.Ordinal) : m.Groups["raw"].Value;
            if (value.Length > 0) entries.Add((m.Groups["name"].Value, value));
        }
        var jobs = await db.BackupJobs.AsNoTracking().Where(j => j.DeletedAt == null).Select(j => j.Name).ToListAsync(ct);
        var jobByKey = jobs.GroupBy(TemplateSettings.JobKey, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

        var strategy = db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            int created = 0, updated = 0, secret = 0;
            foreach (var (name, value) in entries)
            {
                var (setting, key) = TemplateSettings.SplitVariableName(name);
                var isSecret = SecretRegex().IsMatch(setting);
                var job = key is null ? null : jobByKey.GetValueOrDefault(key);
                var description = key is null ? $"{setting} for every backup job" : $"{setting} for the backup job {job ?? key}";
                var v = await db.Variables.FirstOrDefaultAsync(x => x.Name == name, ct);
                if (v is null) { v = new Variable { Name = name }; db.Variables.Add(v); created++; } else updated++;
                v.IsSecret = isSecret;
                v.Value = isSecret ? null : value;
                v.ValueCiphertext = isSecret ? secrets.Protect(value) : null;
                v.Description ??= description;
                if (isSecret) secret++;
                if (key is not null && job is null) Console.WriteLine($"warning   {name}: no backup job matches '{key}'");
            }
            await db.SaveChangesAsync(ct);
            db.AuditEvents.Add(new AuditEvent
            {
                OccurredAt = DateTimeOffset.UtcNow, ActorName = "Backora.Tools", Action = "variable.imported", ResourceType = "Variable",
                Outcome = Domain.AuditOutcome.Success, DetailsJson = System.Text.Json.JsonSerializer.Serialize(new { created, updated, secret, names = entries.Select(e => e.Name) }),
            });
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            Console.WriteLine($"variables {created} created, {updated} updated ({secret} stored encrypted) from {entries.Count} settings");
            return 0;
        });
    }

    [GeneratedRegex(@"^BACKORA__(?<name>[A-Z][A-Z0-9_]*)=(?:'(?<sq>[^']*)'|""(?<dq>(?:[^""\\]|\\.)*)""|(?<raw>[^\s#]*))$", RegexOptions.CultureInvariant)]
    private static partial Regex LineRegex();

    [GeneratedRegex("PASSWORD|PASSPHRASE|SECRET|TOKEN|KEY", RegexOptions.CultureInvariant)]
    private static partial Regex SecretRegex();
}
