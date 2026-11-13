using System.Text.RegularExpressions;
using Backora.Application.Abstractions;
using Backora.Domain.Backups;
using Backora.Domain.Execution;
using Backora.Domain.Infrastructure;
using Backora.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Backora.Tools;

/// <summary>
/// One-off consolidation of the imported legacy templates into a small, parameterised set
/// (<c>backend/database/templates</c>). Runs in one transaction; <c>--dry-run</c> rolls back.
/// Credentials found in old scripts move into encrypted per-job settings and are never printed.
/// S3 uploads are removed from scripts: an S3 target a script used becomes a (paused) DR site linked to its jobs.
/// </summary>
public sealed partial class TemplateConsolidation(BackoraDbContext db, ISecretProtector secrets)
{
    private const string MariaPhysical = "MariaDB · Physical";
    private const string MariaLogical = "MariaDB · Logical";
    private const string PgPhysical = "PostgreSQL · Physical";
    private const string PgLogical = "PostgreSQL · Logical";

    private sealed record Setting(string Value, bool Secret);
    private sealed record Plan(string Target, Func<string, BackupJob, Dictionary<string, string>, Dictionary<string, Setting>> Settings);

    public async Task<int> RunAsync(string templatesDir, bool dryRun, CancellationToken ct, bool templatesOnly = false)
    {
        var files = LoadTemplateFiles(templatesDir);
        // The connection retries transient failures, so the whole transaction runs as one retryable unit.
        var strategy = db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();
            originalNames.Clear();
            return await RunOnceAsync(files, dryRun, templatesOnly, ct);
        });
    }

    private async Task<int> RunOnceAsync(List<TemplateFile> files, bool dryRun, bool templatesOnly, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var now = DateTimeOffset.UtcNow;

        // 1) Install / update the unified templates.
        var all = await db.ScriptTemplates.ToListAsync(ct);
        foreach (var f in files)
        {
            var t = all.FirstOrDefault(x => x.Name == f.Name);
            if (t is null) { t = new ScriptTemplate { Name = f.Name, Revision = 1 }; db.ScriptTemplates.Add(t); all.Add(t); }
            else if (t.Script != f.Script) { t.Revision++; Console.WriteLine($"update    {f.Name}: revision {t.Revision}"); }
            t.Category = f.Category; t.Description = f.Description; t.Script = f.Script;
            Console.WriteLine($"template  {f.Name}: {TemplateSettings.Parse(f.Script).Count} settings");
        }
        await db.SaveChangesAsync(ct);
        if (templatesOnly)
        {
            // Script update only: jobs, settings, sites and variables are left as they are.
            if (dryRun) { await tx.RollbackAsync(ct); Console.WriteLine("dry run: nothing written"); }
            else await tx.CommitAsync(ct);
            return 0;
        }
        var byName = all.ToDictionary(t => t.Name, StringComparer.Ordinal);

        var variables = await db.Variables.AsNoTracking().ToListAsync(ct);
        var vars = variables.ToDictionary(v => v.Name, v => v.IsSecret ? (v.ValueCiphertext is null ? "" : secrets.Unprotect(v.ValueCiphertext)) : v.Value ?? "", StringComparer.Ordinal);

        Dictionary<string, Setting> None(string _, BackupJob __, Dictionary<string, string> ___) => [];
        var plans = new Dictionary<string, Plan>(StringComparer.Ordinal)
        {
            ["mariabackup"] = new(MariaPhysical, (s, _, v) => Put(("DB_USER", Var(v, "mysql_user"), false), ("DB_PASSWORD", Match(MariaPasswordRegex(), s), true))),
            ["MariaBackup-ENVs"] = new(MariaPhysical, None),
            ["MariaBackup_S3"] = new(MariaPhysical, (_, _, v) => Put(("DB_USER", Var(v, "mysql_user"), false))),
            ["mysqldump-DR"] = new(MariaLogical, (s, j, v) => Put(("DB_USER", Var(v, "mysql_user"), false), ("DATABASES", s.Contains("\"{JOB_NAME}\"", StringComparison.Ordinal) ? j.Name : "", false))),
            ["Monitoring_Dump"] = new(MariaLogical, (s, _, _) => MysqldumpBlock(s)),
            ["pg_basebackup"] = new(PgPhysical, None),
            ["PG_Service_ENVs"] = new(PgPhysical, None),
            ["PG_Basebackup_S3"] = new(PgPhysical, None),
            ["PG_Basebackup_Docker"] = new(PgPhysical, (s, _, _) => Put(("DOCKER_CONTAINER", Match(DockerContainerRegex(), s), false), ("OUTPUT_OWNER", Match(ChownRegex(), s), false))),
            ["Crm_Physical"] = new(PgPhysical, (s, _, _) => Put(("PG_BIN_DIR", Match(PgBinRegex(), s), false), ("DB_HOST", Match(HostFlagRegex(), s), false),
                ("DB_USER", Match(UserFlagRegex(), s), false), ("DB_PASSWORD", Match(PgPasswordRegex(), s), true))),
            ["Pg_Dump"] = new(PgLogical, None),
            ["App_Dump"] = new(PgLogical, None),
            ["PG_Dump_S3"] = new(PgLogical, None),
            ["Encrypted_Dump"] = new(PgLogical, (s, _, _) => Put(("ENCRYPT_PASSPHRASE", Match(GpgPassphraseRegex(), s), true))),
        };

        // 2) S3 targets hard-coded in scripts become DR sites (paused until keys are entered).
        var destinations = await db.S3Destinations.ToListAsync(ct);
        var drSites = new Dictionary<string, S3Destination>(StringComparer.Ordinal);
        foreach (var t in all.Where(t => !files.Any(f => f.Name == t.Name)))
        {
            var ep = Match(EndpointRegex(), t.Script);
            var bucketPath = Match(S3UriRegex(), t.Script);
            if (ep.Length == 0 || bucketPath.Length == 0) continue;
            var bucket = bucketPath.Split('/', 2)[0];
            var prefix = bucketPath.Contains('/') ? bucketPath.Split('/', 2)[1].Trim('/') : null;
            var site = destinations.FirstOrDefault(d => d.Endpoint.TrimEnd('/') == ep.TrimEnd('/') && d.Bucket == bucket);
            if (site is null)
            {
                var region = Match(RegionRegex(), ep);
                site = new S3Destination
                {
                    Name = ep.Contains("wasabisys", StringComparison.OrdinalIgnoreCase) ? "Wasabi DR (VM vault)" : $"DR ({bucket})",
                    Endpoint = ep, Region = region.Length > 0 ? region : "us-east-1", Bucket = bucket, PathPrefix = prefix,
                    AccessKeyId = "", SecretKeyCiphertext = "", ForcePathStyle = true, IsEnabled = false,
                };
                db.S3Destinations.Add(site); destinations.Add(site);
                Console.WriteLine($"dr-site   {site.Name}: {ep} bucket {bucket}{(prefix is null ? "" : "/" + prefix)} (paused: enter access keys in Storage & DR)");
            }
            drSites[t.Name] = site;
        }
        await db.SaveChangesAsync(ct);
        var primaryFromVars = destinations.FirstOrDefault(d => vars.TryGetValue("s3_url", out var u) && d.Endpoint.TrimEnd('/') == u.TrimEnd('/') && vars.GetValueOrDefault("s3_bucket") == d.Bucket)
            ?? destinations.Where(d => d.IsEnabled).OrderBy(d => d.Id).FirstOrDefault();

        // 3) Keep the two genuinely different scripts, renamed; strip S3 uploads from them.
        Rename(byName, "Patroni", "PostgreSQL · Patroni (server scripts)", "PostgreSQL", "Runs /opt/pg_basebackup.sh and /opt/etcd_backup.sh on the server into the NFS folder.");
        Rename(byName, "ANS1", "KVM · VM snapshot", "Virtual machine", "Live snapshot of a KVM virtual machine, archived and encrypted (password file on the host).");
        foreach (var t in all)
        {
            var stripped = StripS3Upload(t.Script);
            if (stripped != t.Script) { t.Script = stripped; t.Revision++; Console.WriteLine($"strip     {t.Name}: removed direct S3 upload lines"); }
        }

        // 4) Move jobs (including deleted ones, so old templates can be removed) and their settings.
        var jobs = await db.BackupJobs.Include(j => j.ReplicationTargets).AsSplitQuery().ToListAsync(ct);
        var existingVars = (await db.Variables.ToListAsync(ct)).ToDictionary(v => v.Name, StringComparer.Ordinal);
        int moved = 0, secretCount = 0, plainCount = 0;
        foreach (var job in jobs)
        {
            var old = all.First(t => t.Id == job.ScriptTemplateId);
            if (drSites.TryGetValue(old.Name, out var dr) || drSites.TryGetValue(OriginalName(old), out dr))
            {
                if (job.S3DestinationId is null && primaryFromVars is not null && primaryFromVars.Id != dr.Id) job.AssignDestination(primaryFromVars.Id, now);
                if (job.S3DestinationId != dr.Id && job.ReplicationTargets.All(r => r.S3DestinationId != dr.Id))
                {
                    job.ReplicationTargets.Add(new BackupJobReplicationTarget { S3DestinationId = dr.Id });
                    Console.WriteLine($"link      job {job.Name}: DR copy to {dr.Name}");
                }
            }
            if (!plans.TryGetValue(old.Name, out var plan)) continue;
            var target = byName[plan.Target];
            var specs = TemplateSettings.Parse(target.Script);
            var values = plan.Settings(old.Script, job, vars);
            job.ScriptTemplateId = target.Id;
            job.Category = target.Category;
            // Runs read settings only from variables (NAME__JOB), so that is where they go, under the template's names.
            foreach (var (legacyName, setting) in values)
            {
                var name = EngineSettingName(legacyName, target.Category);
                if (string.IsNullOrEmpty(setting.Value) || specs.All(s => s.Name != name)) continue;
                var secret = setting.Secret || specs.First(s => s.Name == name).Secret;
                var allJobs = existingVars.GetValueOrDefault(name);
                if (!secret && allJobs is { IsSecret: false } && allJobs.Value == setting.Value) continue;   // same as the all-jobs value
                var varName = TemplateSettings.VariableName(name, job.Name);
                if (existingVars.ContainsKey(varName)) continue;   // never overwrite a variable someone set
                var v = new Variable { Name = varName, IsSecret = secret, Description = $"Imported from the legacy script of job {job.Name}" };
                if (secret) v.ValueCiphertext = secrets.Protect(setting.Value); else v.Value = setting.Value;
                db.Variables.Add(v); existingVars[varName] = v;
                if (secret) secretCount++; else plainCount++;
            }
            moved++;
        }
        await db.SaveChangesAsync(ct);

        // 5) Remove templates no job uses any more, and variables no template references.
        var used = jobs.Select(j => j.ScriptTemplateId).ToHashSet();
        var removedTemplates = all.Where(t => !used.Contains(t.Id) && !files.Any(f => f.Name == t.Name)).ToList();
        db.ScriptTemplates.RemoveRange(removedTemplates);
        var remainingScripts = all.Except(removedTemplates).Select(t => t.Script).ToList();
        var referenced = remainingScripts.SelectMany(ScriptRenderer.FindPlaceholders).ToHashSet(StringComparer.Ordinal);
        var settingNames = remainingScripts.SelectMany(sc => TemplateSettings.Parse(sc).Select(spec => spec.Name)).ToHashSet(StringComparer.Ordinal);
        var removedVars = (await db.Variables.ToListAsync(ct))
            .Where(v => !referenced.Contains(v.Name) && !settingNames.Contains(TemplateSettings.SplitVariableName(v.Name).Setting)).ToList();
        db.Variables.RemoveRange(removedVars);
        await db.SaveChangesAsync(ct);

        var active = await db.BackupJobs.CountAsync(j => j.DeletedAt == null && j.Status == Domain.JobStatus.Active, ct);
        Console.WriteLine($"jobs moved: {moved} · setting variables: {plainCount} plain, {secretCount} secret (encrypted) · active jobs: {active}");
        Console.WriteLine($"templates removed ({removedTemplates.Count}): {string.Join(", ", removedTemplates.Select(t => t.Name))}");
        Console.WriteLine($"variables removed ({removedVars.Count}): {string.Join(", ", removedVars.Select(v => v.Name))}");
        Console.WriteLine($"templates now ({all.Count - removedTemplates.Count}): {string.Join(", ", all.Except(removedTemplates).Select(t => t.Name).Order())}");

        if (dryRun) { await tx.RollbackAsync(ct); Console.WriteLine("DRY RUN: rolled back, nothing changed."); }
        else { await tx.CommitAsync(ct); Console.WriteLine("Committed."); }
        return 0;
    }

    private sealed record TemplateFile(string Name, string Category, string? Description, string Script);

    private static List<TemplateFile> LoadTemplateFiles(string dir)
    {
        if (!Directory.Exists(dir)) throw new InvalidOperationException($"Template directory not found: {dir}");
        var common = File.ReadAllText(Path.Combine(dir, "_common.inc"));
        var list = new List<TemplateFile>();
        foreach (var path in Directory.GetFiles(dir, "*.sh").Order(StringComparer.Ordinal))
        {
            var script = File.ReadAllText(path).Replace("\r\n", "\n", StringComparison.Ordinal).Replace("#@include _common.inc\n", common, StringComparison.Ordinal);
            var head = TemplateHeaderRegex().Match(script);
            if (!head.Success) throw new InvalidOperationException($"{Path.GetFileName(path)} has no '# @template name=\"…\" category=\"…\"' line.");
            list.Add(new TemplateFile(head.Groups["name"].Value, head.Groups["cat"].Value, head.Groups["desc"].Success ? head.Groups["desc"].Value : null, script));
        }
        return list;
    }

    private readonly Dictionary<long, string> originalNames = [];
    private string OriginalName(ScriptTemplate t) => originalNames.GetValueOrDefault(t.Id, t.Name);

    private void Rename(Dictionary<string, ScriptTemplate> byName, string from, string to, string category, string description)
    {
        if (!byName.TryGetValue(from, out var t)) return;
        originalNames[t.Id] = from;
        t.Name = to; t.Category = category; t.Description = description;
        Console.WriteLine($"template  {from} → {to}");
    }

    /// <summary>Removes direct S3 uploads (aws s3 / rclone / s3cmd and their target lines): Backora uploads and copies to DR itself.</summary>
    private static string StripS3Upload(string script)
    {
        var lines = script.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var kept = lines.Where(l => !S3LineRegex().IsMatch(l)).ToArray();
        return kept.Length == lines.Length ? script : string.Join('\n', kept);
    }

    private const string LongPasswordFlag = "--password=";

    private static Dictionary<string, Setting> MysqldumpBlock(string script)
    {
        var block = MysqldumpRegex().Match(script);
        if (!block.Success) return [];
        var tokens = block.Groups["args"].Value.Replace("\\\n", " ", StringComparison.Ordinal).Split([' ', '\t', '\n'], StringSplitOptions.RemoveEmptyEntries).ToList();
        string user = "", password = "";
        var options = new List<string>();
        var databases = new List<string>();
        for (var i = 0; i < tokens.Count; i++)
        {
            var tok = tokens[i];
            if (tok == "-u" && i + 1 < tokens.Count) user = tokens[++i].Trim('\'', '"');
            else if (tok.StartsWith("-u", StringComparison.Ordinal) && tok.Length > 2) user = tok[2..].Trim('\'', '"');
            else if (tok.StartsWith("-p", StringComparison.Ordinal)) password = tok[2..].Trim('\'', '"');
            else if (tok.StartsWith(LongPasswordFlag, StringComparison.Ordinal)) password = tok[LongPasswordFlag.Length..].Trim('\'', '"');
            else if (tok.StartsWith("--", StringComparison.Ordinal)) options.Add(tok);
            else if (!tok.StartsWith('-') && !tok.StartsWith('"') && !tok.StartsWith('$')) databases.Add(tok);
        }
        return Put(("DB_USER", user, false), ("DB_PASSWORD", password, true), ("DUMP_OPTIONS", string.Join(' ', options), false), ("DATABASES", string.Join(' ', databases), false));
    }

    /// <summary>The legacy scripts' DB_* settings are per engine in the templates: MYSQL_* for MariaDB, PG_* for PostgreSQL.</summary>
    public static string EngineSettingName(string name, string category) => name switch
    {
        "DB_HOST" or "DB_PORT" or "DB_USER" or "DB_PASSWORD" =>
            (category.StartsWith("Postgre", StringComparison.OrdinalIgnoreCase) ? "PG_" : "MYSQL_") + name[3..],
        _ => name,
    };

    private static Dictionary<string, Setting> Put(params (string Name, string Value, bool Secret)[] items) =>
        items.Where(i => !string.IsNullOrEmpty(i.Value)).ToDictionary(i => i.Name, i => new Setting(i.Value, i.Secret), StringComparer.Ordinal);

    private static string Var(Dictionary<string, string> vars, string name) => vars.GetValueOrDefault(name) ?? "";

    private static string Match(Regex r, string s)
    {
        var m = r.Match(s);
        if (!m.Success) return "";
        var g = m.Groups.Cast<Group>().Skip(1).FirstOrDefault(x => x.Success && x.Value.Length > 0);
        return g?.Value.Trim() ?? "";
    }

    [GeneratedRegex(@"^#\s*@template\s+name=""(?<name>[^""]+)""\s+category=""(?<cat>[^""]+)""(?:\s+description=""(?<desc>[^""]*)"")?", RegexOptions.Multiline)]
    private static partial Regex TemplateHeaderRegex();
    [GeneratedRegex(@"--password=(?:""([^""]*)""|'([^']*)'|([^\s\\]+))")]
    private static partial Regex MariaPasswordRegex();
    [GeneratedRegex(@"PGPASSWORD=(?:""([^""]*)""|'([^']*)'|(\S+))")]
    private static partial Regex PgPasswordRegex();
    [GeneratedRegex(@"(/\S+?)/pg_basebackup\b")]
    private static partial Regex PgBinRegex();
    [GeneratedRegex(@"\s-h\s+(\S+)")]
    private static partial Regex HostFlagRegex();
    [GeneratedRegex(@"\s-U\s+(\S+)")]
    private static partial Regex UserFlagRegex();
    [GeneratedRegex(@"--passphrase\s+(?:""([^""]*)""|'([^']*)'|(\S+))")]
    private static partial Regex GpgPassphraseRegex();
    [GeneratedRegex(@"docker exec\s+-u\s+\S+\s+(\S+)")]
    private static partial Regex DockerContainerRegex();
    [GeneratedRegex(@"chown -R\s+(\d+:\d+)")]
    private static partial Regex ChownRegex();
    [GeneratedRegex(@"ENDPOINT=""?(https?://[^""\s]+)")]
    private static partial Regex EndpointRegex();
    [GeneratedRegex(@"s3://([^""\s]+)")]
    private static partial Regex S3UriRegex();
    [GeneratedRegex(@"s3\.([a-z0-9-]+)\.wasabisys\.com")]
    private static partial Regex RegionRegex();
    [GeneratedRegex(@"!\s*mysqldump\s*\\?\n?(?<args>(?:[^>\n]*\\\n)*[^>\n]*)>", RegexOptions.Multiline)]
    private static partial Regex MysqldumpRegex();
    [GeneratedRegex(@"\baws s3\b|\brclone\b|\bs3cmd\b|^\s*S3_BUCKET=|^\s*ENDPOINT=""?https?://|Uploading backup to |echo ""Uploaded: |/opt/s3_backup\.sh")]
    private static partial Regex S3LineRegex();
}
