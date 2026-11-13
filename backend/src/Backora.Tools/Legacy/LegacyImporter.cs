using System.Data.Common;
using System.Text.Json;
using Backora.Application.Abstractions;
using Backora.Contracts.Security;
using Backora.Domain;
using Backora.Domain.Backups;
using Backora.Domain.Common;
using Backora.Domain.Infrastructure;
using Backora.Domain.Platform;
using Backora.Domain.Scheduling;
using Backora.Infrastructure.Identity;
using Backora.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Backora.Tools.Legacy;

/// <param name="PreserveJobStatus">Keep legacy active/inactive flags. Default false: every job is imported Inactive so
/// nothing executes before host keys are trusted and the legacy scheduler is stopped (gradual enablement).</param>
public sealed record ImportOptions(bool DryRun, bool AllowNonEmpty, bool PreserveJobStatus = false);

/// <summary>
/// Imports a legacy Laravel/MariaDB Backora database into the PostgreSQL schema. Legacy primary keys are
/// preserved. The whole import runs in ONE PostgreSQL transaction (rolled back on any error or in dry-run
/// mode) and is idempotent: re-running upserts the same rows by id. Secrets are encrypted with the active
/// key while writing and are never logged or reported.
/// </summary>
public sealed class LegacyImporter(BackoraDbContext db, ISecretProtector secrets, IClock clock)
{
    private const int MaxLog = 512 * 1024;
    private const int MaxLegacyOutput = 1024 * 1024;

    private static readonly string[] SequenceTables =
    [
        "users", "servers", "worker_nodes", "storage_settings", "ssh_settings", "variables", "script_templates", "s3_destinations",
        "smtp_settings", "teams_settings", "notification_settings", "backup_jobs", "backup_runs", "offload_runs", "restore_runs", "audit_events",
    ];

    // ───────────────────────────── Plan ─────────────────────────────

    /// <summary>Everything the import would write, computed purely from the snapshot. Secret fields hold plaintext until sealed.</summary>
    public sealed class Plan
    {
        public List<(ApplicationUser User, string Role)> Users { get; } = [];
        public List<Server> Servers { get; } = [];
        public WorkerNode? Worker { get; set; }
        public SshSettings? Ssh { get; set; }
        public StorageSettings? Storage { get; set; }
        public List<Variable> Variables { get; } = [];
        public List<ScriptTemplate> Templates { get; } = [];
        public List<S3Destination> Destinations { get; } = [];
        public SmtpSettings? Smtp { get; set; }
        public TeamsSettings? Teams { get; set; }
        public NotificationSettings? Notifications { get; set; }
        public List<(BackupJob Job, (string Name, string Category)? MissingTemplate)> Jobs { get; } = [];
        public List<BackupRun> Runs { get; } = [];
        public List<OffloadRun> Offloads { get; } = [];
        public List<RestoreRun> Restores { get; } = [];
        public Dictionary<string, HashSet<long>> ExpectedIds { get; } = new(StringComparer.Ordinal);
    }

    public static Plan BuildPlan(LegacySnapshot s, ImportReport report, DateTimeOffset now, bool preserveJobStatus = false)
    {
        var plan = new Plan();
        void Expect(string table, long id)
        {
            if (!plan.ExpectedIds.TryGetValue(table, out var set)) plan.ExpectedIds[table] = set = [];
            set.Add(id);
        }
        foreach (var table in LegacySnapshot.DomainTables) report.Table(table).Source = s[table].Count;

        // users
        foreach (var r in s["users"])
        {
            var email = r.Str("email")?.Trim();
            if (string.IsNullOrEmpty(email)) { report.Skip("users", r.Id, "missing email"); continue; }
            var (role, defaulted) = LegacyMapping.MapRole(r.Str("role_name"));
            if (defaulted) report.Note("users", $"user {r.Id}: role '{r.Str("role_name") ?? "(null)"}' mapped to least-privilege {role}");
            var active = LegacyMapping.IsActiveUserStatus(r.Str("status"));
            if (!active) report.Note("users", $"user {r.Id}: legacy status '{r.Str("status")}' → disabled");
            plan.Users.Add((new ApplicationUser
            {
                Id = r.Id,
                LegacyId = r.Id,
                UserName = email,
                NormalizedUserName = email.ToUpperInvariant(),
                Email = email,
                NormalizedEmail = email.ToUpperInvariant(),
                EmailConfirmed = true,
                Name = LegacyMapping.Truncate(r.Str("name")?.Trim(), 200) is { Length: > 0 } n ? n : email,
                PasswordHash = r.Str("password"),
                SecurityStamp = Guid.NewGuid().ToString("N").ToUpperInvariant(),
                ConcurrencyStamp = Guid.NewGuid().ToString(),
                LockoutEnabled = true,
                IsActive = active,
                MustChangePassword = false,
                LegacyUserCode = LegacyMapping.Truncate(r.Str("user_id"), 100),
                CreatedAt = r.Ts("created_at") ?? now,
                UpdatedAt = r.Ts("updated_at") ?? now,
            }, role));
            if (!CompatPasswordHasher.IsBcrypt(r.Str("password") ?? string.Empty)) report.Note("users", $"user {r.Id}: password hash is not bcrypt; the user must reset the password");
            Expect("users", r.Id);
        }

        // servers (deterministic de-duplication of names, ordered by id)
        var serverNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var r in s["servers"])
        {
            var name = (r.Str("server_name") ?? $"server-{r.Id}").Trim();
            if (!serverNames.Add(name))
            {
                report.Note("servers", $"server {r.Id}: duplicate name '{name}' renamed to '{name} (#{r.Id})'");
                name = $"{name} (#{r.Id})";
                serverNames.Add(name);
            }
            plan.Servers.Add(new Server
            {
                Id = r.Id,
                Name = LegacyMapping.Truncate(name, 200)!,
                Host = (r.Str("server_ip") ?? string.Empty).Trim(),
                SshPort = r.Int("ssh_port") is { } p and >= 1 and <= 65535 ? p : 22,
                SshUser = (r.Str("ssh_user") ?? "root").Trim() is { Length: > 0 } u ? u : "root",
                PrivateKeyCiphertext = string.IsNullOrWhiteSpace(r.Str("private_key")) ? null : r.Str("private_key")!.Trim(),
                HostKeyFingerprint = null,
                CreatedAt = r.Ts("created_at") ?? now,
            });
            Expect("servers", r.Id);
        }
        report.Note("servers", $"{plan.Servers.Count} server(s) imported with UNTRUSTED host keys — verify and trust each fingerprint before enabling backups");
        report.Note("servers", $"{plan.Servers.Count(x => x.PrivateKeyCiphertext is not null)} server(s) carry a server-specific private key (encrypted)");

        // worker / ssh / nfs singletons
        if (s["worker_settings"].FirstOrDefault() is { } w)
        {
            plan.Worker = new WorkerNode
            {
                Id = 1, Host = (w.Str("server_ip") ?? string.Empty).Trim(), SshPort = 22, SshUser = (w.Str("user") ?? string.Empty).Trim(),
                PrivateKeyCiphertext = w.Str("worker_ssh_private_key")?.Trim() ?? string.Empty, HostKeyFingerprint = null,
                RclonePath = "rclone", WorkDirectory = "/var/tmp/backora", CreatedAt = w.Ts("created_at") ?? now,
            };
            if (s["worker_settings"].Count > 1) report.Note("worker_settings", $"{s["worker_settings"].Count - 1} extra row(s) ignored (legacy used the first row)");
            Expect("worker_settings", w.Id);
        }
        if (s["ssh_settings"].FirstOrDefault() is { } ssh)
        {
            var priv = ssh.Str("private_key");
            var sshKey = ssh.Str("ssh_key");
            string? publicKey = null;
            if (LegacyMapping.LooksLikePublicKey(sshKey))
            {
                publicKey = sshKey!.Trim();
                report.Note("ssh_settings", "ssh_key column recognised as a public key");
            }
            else if (string.IsNullOrWhiteSpace(priv) && LegacyMapping.LooksLikePrivateKey(sshKey))
            {
                priv = sshKey;
                report.Note("ssh_settings", "ssh_key column contained a private key and private_key was empty: used as the default private key");
            }
            else if (!string.IsNullOrWhiteSpace(sshKey))
            {
                report.Note("ssh_settings", "ssh_key column content not recognised as a key; ignored");
            }
            plan.Ssh = new SshSettings
            {
                Id = 1, DefaultPrivateKeyCiphertext = string.IsNullOrWhiteSpace(priv) ? null : priv.Trim(), DefaultPublicKey = publicKey, CreatedAt = ssh.Ts("created_at") ?? now,
            };
            report.Note("ssh_settings", plan.Ssh.DefaultPrivateKeyCiphertext is null ? "no default private key" : "default private key imported (encrypted)");
            Expect("ssh_settings", ssh.Id);
        }
        if (s["nfs_settings"].FirstOrDefault() is { } nfs)
        {
            plan.Storage = new StorageSettings { Id = 1, NfsHost = (nfs.Str("nfs_host") ?? string.Empty).Trim(), NfsPath = (nfs.Str("nfs_path") ?? string.Empty).Trim(), CreatedAt = nfs.Ts("created_at") ?? now };
            Expect("nfs_settings", nfs.Id);
        }

        // variables
        foreach (var r in s["variables"])
        {
            var name = (r.Str("name") ?? string.Empty).Trim();
            if (name.Length == 0) { report.Skip("variables", r.Id, "empty name"); continue; }
            var secret = LegacyMapping.IsSecretVariableName(name);
            if (secret) report.Note("variables", $"variable '{name}' imported as an encrypted secret");
            plan.Variables.Add(new Variable
            {
                Id = r.Id, Name = LegacyMapping.Truncate(name, 100)!, IsSecret = secret,
                Value = secret ? null : r.Str("value") ?? string.Empty,
                ValueCiphertext = secret ? r.Str("value") ?? string.Empty : null,
                CreatedAt = r.Ts("created_at") ?? now,
            });
            Expect("variables", r.Id);
        }

        // templates
        var templateKeys = new HashSet<(string, string)>();
        foreach (var r in s["templates"])
        {
            var name = (r.Str("name") ?? $"template-{r.Id}").Trim();
            var category = string.IsNullOrWhiteSpace(r.Str("category")) ? "Uncategorized" : r.Str("category")!.Trim();
            if (category == "Uncategorized") report.Note("templates", $"template {r.Id}: missing category → 'Uncategorized'");
            if (!templateKeys.Add((name, category)))
            {
                report.Note("templates", $"template {r.Id}: duplicate name '{name}' in '{category}' renamed to '{name} (#{r.Id})'");
                name = $"{name} (#{r.Id})";
                templateKeys.Add((name, category));
            }
            plan.Templates.Add(new ScriptTemplate
            {
                Id = r.Id, Name = LegacyMapping.Truncate(name, 200)!, Category = LegacyMapping.Truncate(category, 100)!,
                Script = (r.Str("script") ?? string.Empty).Replace("\r\n", "\n", StringComparison.Ordinal), Revision = 1,
                CreatedAt = r.Ts("created_at") ?? now,
            });
            Expect("templates", r.Id);
        }

        // s3 destinations
        var destNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var r in s["s3_settings"])
        {
            var name = (r.Str("name") ?? $"s3-{r.Id}").Trim();
            if (!destNames.Add(name)) { report.Note("s3_settings", $"destination {r.Id}: duplicate name renamed to '{name} (#{r.Id})'"); name = $"{name} (#{r.Id})"; destNames.Add(name); }
            plan.Destinations.Add(new S3Destination
            {
                Id = r.Id, Name = LegacyMapping.Truncate(name, 200)!, Endpoint = (r.Str("endpoint") ?? string.Empty).Trim().TrimEnd('/'),
                Region = (r.Str("region") ?? "us-east-1").Trim(), Bucket = (r.Str("bucket") ?? string.Empty).Trim(), AccessKeyId = (r.Str("key") ?? string.Empty).Trim(),
                SecretKeyCiphertext = r.Str("secret") ?? string.Empty, ForcePathStyle = true, CreatedAt = r.Ts("created_at") ?? now,
            });
            Expect("s3_settings", r.Id);
        }

        // smtp / teams / notification singletons
        if (s["smtp_settings"].FirstOrDefault() is { } smtp)
        {
            var port = int.TryParse(smtp.Str("port"), out var p) && p is >= 1 and <= 65535 ? p : 587;
            var user = string.IsNullOrWhiteSpace(smtp.Str("username")) || smtp.Str("username") == "null" ? null : smtp.Str("username")!.Trim();
            var pass = string.IsNullOrEmpty(smtp.Str("password")) || smtp.Str("password") == "null" ? null : smtp.Str("password");
            plan.Smtp = new SmtpSettings
            {
                Id = 1, Host = (smtp.Str("host") ?? string.Empty).Trim(), Port = port, Username = user, PasswordCiphertext = pass,
                FromAddress = (smtp.Str("from_address") ?? string.Empty).Trim(), FromName = "Backora", Security = LegacyMapping.SmtpSecurity(port, user is not null),
                Template = string.IsNullOrWhiteSpace(smtp.Str("template")) ? null : smtp.Str("template"), Enabled = true, CreatedAt = smtp.Ts("created_at") ?? now,
            };
            report.Note("smtp_settings", $"security mode derived from port {port}: {plan.Smtp.Security}; custom template {(plan.Smtp.Template is null ? "not set" : "kept")}");
            Expect("smtp_settings", smtp.Id);
        }
        if (s["teams_settings"].FirstOrDefault() is { } teams)
        {
            plan.Teams = new TeamsSettings
            {
                Id = 1, WebhookUrlCiphertext = string.IsNullOrWhiteSpace(teams.Str("webhook_url")) ? null : teams.Str("webhook_url")!.Trim(),
                Template = string.IsNullOrWhiteSpace(teams.Str("template")) ? null : teams.Str("template"), Enabled = true, CreatedAt = teams.Ts("created_at") ?? now,
            };
            if (plan.Teams.WebhookUrlCiphertext is { } url && !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                plan.Teams.Enabled = false;
                report.Note("teams_settings", "webhook URL is not https: imported but channel disabled");
            }
            if (plan.Teams.Template is { } tpl && !IsJsonAfterPlaceholderStrip(tpl))
            {
                report.Note("teams_settings", "custom template does not render to valid JSON; kept but notifications will fail until it is fixed (or cleared to use the default card)");
            }
            Expect("teams_settings", teams.Id);
        }
        if (s["notification_settings"].FirstOrDefault() is { } ns)
        {
            var recipients = LegacyMapping.ParseRecipients(ns.Str("notification_emails"));
            plan.Notifications = new NotificationSettings { Id = 1, Recipients = recipients.ToList(), CreatedAt = ns.Ts("created_at") ?? now };
            report.Note("notification_settings", $"{recipients.Count} recipient(s) imported");
            Expect("notification_settings", ns.Id);
        }

        // backup jobs
        var templatesByKey = plan.Templates.GroupBy(t => (t.Name, t.Category)).ToDictionary(g => g.Key, g => g.First());
        var legacyTemplateByKey = s["templates"].Select(r => (r, key: ((r.Str("name") ?? string.Empty).Trim(), string.IsNullOrWhiteSpace(r.Str("category")) ? "Uncategorized" : r.Str("category")!.Trim())))
            .GroupBy(x => x.key).ToDictionary(g => g.Key, g => g.First().r.Id);
        var serverIds = plan.Servers.Select(x => x.Id).ToHashSet();
        var destIds = plan.Destinations.Select(x => x.Id).ToHashSet();
        var jobNames = new HashSet<string>(StringComparer.Ordinal);
        var legacyActive = 0;
        foreach (var r in s["backup_jobs"])
        {
            var name = (r.Str("name") ?? string.Empty).Trim();
            var serverId = r.Long("server_id");
            if (serverId is null || !serverIds.Contains(serverId.Value)) { report.Skip("backup_jobs", r.Id, $"server {serverId} does not exist"); continue; }
            if (name.Length == 0 || name.Length > BackupJob.MaxNameLength) { report.Skip("backup_jobs", r.Id, "name empty or longer than 100 characters"); continue; }

            var scriptName = (r.Str("script_name") ?? string.Empty).Trim();
            var category = string.IsNullOrWhiteSpace(r.Str("category")) ? "Uncategorized" : r.Str("category")!.Trim();
            long? templateId = legacyTemplateByKey.TryGetValue((scriptName, category), out var tid) ? tid : null;
            if (templateId is null)
            {
                var byName = s["templates"].Where(t => (t.Str("name") ?? string.Empty).Trim() == scriptName).ToList();
                if (byName.Count == 1)
                {
                    templateId = byName[0].Id;
                    report.Note("backup_jobs", $"job {r.Id}: template resolved by name only ('{scriptName}'; category '{category}' differs)");
                }
            }
            (string, string)? missing = templateId is null ? ($"{scriptName} (missing legacy template)", category) : null;
            if (missing is not null) report.Note("backup_jobs", $"job {r.Id} '{name}': template '{scriptName}' in '{category}' not found — linked to a failing placeholder template and set Inactive");

            var cron = LegacyMapping.MapCron(r.Str("schedule_at"));
            if (!cron.Valid) report.Note("backup_jobs", $"job {r.Id} '{name}': invalid cron '{r.Str("schedule_at")}' ({cron.Error}) → manual only, Inactive");
            var status = LegacyMapping.MapJobStatus(r.Str("status"), name, cron.Valid, templateId is not null);
            if (!preserveJobStatus && status.Status == JobStatus.Active)
            {
                status = status with { Status = JobStatus.Inactive };
                legacyActive++;
            }
            foreach (var reason in status.Reasons.Where(x => x.StartsWith("rename", StringComparison.Ordinal))) report.Note("backup_jobs", $"job {r.Id} '{name}': {reason} — set Inactive");

            DateTimeOffset? deletedAt = null;
            if (!jobNames.Add(name))
            {
                deletedAt = now;
                report.Note("backup_jobs", $"job {r.Id}: duplicate name '{name}' — imported as deleted (history preserved)");
            }

            long? destId = r.Long("s3_config_id") is { } d && destIds.Contains(d) ? d : null;
            if (r.Long("s3_config_id") is { } dd && destId is null) report.Note("backup_jobs", $"job {r.Id}: S3 destination {dd} not found → none");
            DateTimeOffset? assigned = destId is null ? null : r.Ts("s3_config_added_at");
            if (destId is not null && assigned is null)
            {
                assigned = now;
                report.Note("backup_jobs", $"job {r.Id}: no s3_config_added_at in legacy data → offload enabled from import time");
            }

            var job = new BackupJob
            {
                Id = r.Id,
                Name = name,
                Category = templateId is { } t2 && plan.Templates.FirstOrDefault(x => x.Id == t2) is { } tpl ? tpl.Category : LegacyMapping.Truncate(category, 100)!,
                ScriptTemplateId = templateId ?? 0,
                ServerId = serverId.Value,
                Status = status.Status,
                CronExpression = cron.Cron,
                TimeZone = "UTC",
                RetentionDays = Math.Max(0m, r.Dec("retention_days") ?? 7m),
                MinRecoveryPointsToKeep = 1,
                S3DestinationId = destId,
                S3DestinationAssignedAt = assigned,
                KeepLocalCopy = false,
                RequireArtifacts = true,
                TimeoutMinutes = 360,
                MaxAttempts = 3,
                DeletedAt = deletedAt,
                CreatedAt = r.Ts("created_at") ?? now,
            };
            if (job.IsScheduled) job.NextRunAt = SchedulePlanner.NextOccurrence(job.CronExpression!, job.TimeZone, now);
            plan.Jobs.Add((job, missing));
            Expect("backup_jobs", r.Id);
        }
        _ = templatesByKey;
        if (legacyActive > 0) report.Note("backup_jobs", $"{legacyActive} job(s) active in the legacy system imported as Inactive (enable them after trusting host keys; use --preserve-job-status to keep legacy flags)");

        // runs
        var jobsById = plan.Jobs.ToDictionary(j => j.Job.Id, j => j.Job);
        foreach (var r in s["backup_job_executions"])
        {
            var jobId = r.Long("backup_job_id");
            if (jobId is null || !jobsById.ContainsKey(jobId.Value)) { report.Skip("backup_job_executions", r.Id, $"backup job {jobId} not imported"); continue; }
            var serverId = r.Long("server_id") is { } sid && serverIds.Contains(sid) ? sid : jobsById[jobId.Value].ServerId;
            var map = LegacyMapping.MapRunStatus(r.Str("status"));
            var created = r.Ts("created_at") ?? r.Ts("started_at") ?? now;
            var output = r.Str("output");
            var run = new BackupRun
            {
                Id = r.Id,
                BackupJobId = jobId.Value,
                ServerId = serverId,
                JobName = LegacyMapping.Truncate(r.Str("job_name") ?? jobsById[jobId.Value].Name, 200)!,
                Category = LegacyMapping.Truncate(string.IsNullOrWhiteSpace(r.Str("category")) ? "Uncategorized" : r.Str("category")!, 100)!,
                TemplateName = LegacyMapping.Truncate(r.Str("script_name") ?? string.Empty, 200)!,
                Status = map.Status,
                FailureClass = map.FailureClass,
                Trigger = RunTrigger.Imported,
                RequestedByName = "legacy",
                StartedAt = r.Ts("started_at"),
                CompletedAt = r.Ts("completed_at") ?? r.Ts("updated_at") ?? r.Ts("started_at") ?? created,
                ErrorMessage = LegacyMapping.Truncate(map.Message ?? r.Str("error_message"), 4000),
                LegacyOutput = LegacyMapping.Truncate(output, MaxLegacyOutput),
                Log = LegacyMapping.ExtractLog(output) is { } log ? LegacyMapping.Tail(log, MaxLog) : r.Has("log") && r.Str("log") is { } l2 ? LegacyMapping.Tail(l2, MaxLog) : null,
                LegacyQueueJobId = r.Long("queue_job_id"),
                VerificationStatus = VerificationStatus.NotVerified,
                Attempt = 1,
                MaxAttempts = 1,
                NextAttemptAt = created,
                CreatedAt = created,
            };
            if (map.Message is not null) report.Note("backup_job_executions", $"run {r.Id}: legacy status '{r.Str("status")}' → Failed/{map.FailureClass}");
            plan.Runs.Add(run);
            Expect("backup_job_executions", r.Id);
        }

        // offloads
        var runsById = plan.Runs.ToDictionary(r => r.Id);
        foreach (var r in s["upload_to_s3_job_executions"])
        {
            var destId = r.Long("s3_config_id");
            if (destId is null || !destIds.Contains(destId.Value)) { report.Skip("upload_to_s3_job_executions", r.Id, $"S3 destination {destId} not imported"); continue; }
            var runId = r.Long("backup_job_execution_id") is { } rid && runsById.ContainsKey(rid) ? rid : (long?)null;
            if (r.Long("backup_job_execution_id") is { } missingRun && runId is null) report.Note("upload_to_s3_job_executions", $"upload {r.Id}: referenced run {missingRun} not imported → no run link");
            var run = runId is { } x ? runsById[x] : null;
            var jobName = run?.JobName ?? LegacyMapping.LastSegment(r.Str("nfs_path"));
            var started = r.Ts("started_at") ?? r.Ts("created_at") ?? now;
            var (status, message) = LegacyMapping.MapOffloadStatus(r.Str("status"));
            var created = r.Ts("created_at") ?? started;
            var totalSize = r.Str("total_size");
            var job = run is not null ? jobsById[run.BackupJobId] : null;
            var offload = new OffloadRun
            {
                Id = r.Id,
                BackupRunId = runId,
                BackupJobId = run?.BackupJobId,
                S3DestinationId = destId.Value,
                ServerId = r.Long("server_id") is { } sid && serverIds.Contains(sid) ? sid : run?.ServerId,
                JobName = LegacyMapping.Truncate(jobName, 200)!,
                SourcePath = LegacyMapping.Truncate(r.Str("nfs_path") ?? string.Empty, 1024)!,
                RemotePrefix = LegacyMapping.LegacyPrefix(jobName, started),
                LegacyPrefix = true,
                Status = status,
                VerificationStatus = VerificationStatus.NotVerified,
                LegacyTotalSize = LegacyMapping.Truncate(totalSize, 50),
                BytesTotal = LegacyMapping.ParseSize(totalSize),
                CleanupStatus = status == OffloadStatus.Completed ? CleanupStatus.Completed : CleanupStatus.NotRequired,
                RetentionState = RetentionState.Active,
                StartedAt = r.Ts("started_at"),
                CompletedAt = r.Ts("completed_at") ?? (status == OffloadStatus.Completed ? r.Ts("updated_at") ?? started : r.Ts("updated_at") ?? created),
                Log = r.Str("log") is { } log ? LegacyMapping.Tail(log, MaxLog) : null,
                ErrorMessage = LegacyMapping.Truncate(message ?? r.Str("error_message"), 4000),
                FailureClass = status == OffloadStatus.Failed ? (message is null ? FailureClass.StorageError : FailureClass.ProcessLost) : FailureClass.None,
                Attempt = 1,
                MaxAttempts = 1,
                NextAttemptAt = created,
                CreatedAt = created,
            };
            if (offload.Status == OffloadStatus.Completed && job is not null && job.RetentionDays > 0 && offload.CompletedAt is { } ca)
            {
                offload.ExpiresAt = ca + TimeSpan.FromSeconds((double)(job.RetentionDays * 86400m));
            }
            if (totalSize is not null && offload.BytesTotal is null) report.Note("upload_to_s3_job_executions", $"upload {r.Id}: size '{totalSize}' could not be parsed");
            if (message is not null) report.Note("upload_to_s3_job_executions", $"upload {r.Id}: legacy status '{r.Str("status")}' → Failed");
            plan.Offloads.Add(offload);
            Expect("upload_to_s3_job_executions", r.Id);
        }

        // restores
        var offloadIds = plan.Offloads.Select(o => o.Id).ToHashSet();
        var targetPaths = new HashSet<string>(StringComparer.Ordinal);
        foreach (var r in s["restore_to_nfs_job_executions"])
        {
            var oid = r.Long("upload_to_s3_job_execution_id");
            if (oid is null || !offloadIds.Contains(oid.Value)) { report.Skip("restore_to_nfs_job_executions", r.Id, $"recovery point {oid} not imported"); continue; }
            var offload = plan.Offloads.First(o => o.Id == oid.Value);
            var path = string.IsNullOrWhiteSpace(r.Str("restored_path")) ? $"<legacy-unknown>/restore-{r.Id}" : r.Str("restored_path")!.Trim();
            if (!targetPaths.Add(path))
            {
                path = $"{path}#legacy-restore-{r.Id}";
                targetPaths.Add(path);
                report.Note("restore_to_nfs_job_executions", $"restore {r.Id}: shared legacy target path disambiguated");
            }
            var (status, message) = LegacyMapping.MapRestoreStatus(r.Str("status"));
            var created = r.Ts("created_at") ?? r.Ts("started_at") ?? now;
            plan.Restores.Add(new RestoreRun
            {
                Id = r.Id,
                OffloadRunId = oid.Value,
                ServerId = r.Long("server_id") is { } sid && serverIds.Contains(sid) ? sid : offload.ServerId,
                JobName = offload.JobName,
                Status = status,
                TargetPath = LegacyMapping.Truncate(path, 1024)!,
                Reason = "Imported from legacy system",
                RequestedByName = "legacy",
                StartedAt = r.Ts("started_at"),
                CompletedAt = r.Ts("completed_at") ?? r.Ts("updated_at") ?? created,
                Log = r.Str("log") is { } log ? LegacyMapping.Tail(log, MaxLog) : null,
                ErrorMessage = LegacyMapping.Truncate(message ?? r.Str("error_message"), 4000),
                FailureClass = status == RestoreStatus.Failed ? (message is null ? FailureClass.StorageError : FailureClass.ProcessLost) : status == RestoreStatus.Cancelled ? FailureClass.Cancelled : FailureClass.None,
                VerificationStatus = VerificationStatus.NotVerified,
                Attempt = 1,
                MaxAttempts = 1,
                NextAttemptAt = created,
                CreatedAt = created,
            });
            Expect("restore_to_nfs_job_executions", r.Id);
        }

        foreach (var t in s["api_tokens"]) report.Skip("api_tokens", t.Id, "legacy API tokens are not migrated; users create new personal access tokens");
        return plan;
    }

    private static bool IsJsonAfterPlaceholderStrip(string template)
    {
        var rendered = System.Text.RegularExpressions.Regex.Replace(template, @"\{\{\s*[A-Za-z0-9_]+\s*\}\}", "x");
        try { using var _ = JsonDocument.Parse(rendered); return true; }
        catch (JsonException) { return false; }
    }

    // ───────────────────────────── Apply ─────────────────────────────

    public async Task<ImportReport> ImportAsync(LegacySnapshot snapshot, ImportOptions options, CancellationToken ct)
    {
        var now = clock.UtcNow;
        ImportReport report = null!;

        if (!options.AllowNonEmpty)
        {
            var nonEmpty = await db.Servers.AnyAsync(ct) || await db.ScriptTemplates.AnyAsync(ct) || await db.BackupJobs.AnyAsync(ct)
                           || await db.BackupRuns.AnyAsync(ct) || await db.Users.AnyAsync(ct);
            if (nonEmpty)
            {
                throw new InvalidOperationException("The target database already contains data. Re-run with --allow-non-empty to upsert into it (re-running an import is safe).");
            }
        }

        var strategy = db.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            // Plan is rebuilt per attempt: sealing mutates entities, so a retried transaction must start fresh.
            db.ChangeTracker.Clear();
            report = new ImportReport { Mode = "import", DryRun = options.DryRun, StartedAt = now, LegacyDatabase = snapshot.Database, LegacyServerVersion = snapshot.ServerVersion };
            var plan = BuildPlan(snapshot, report, now, options.PreserveJobStatus);
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            await ApplyAsync(plan, report, now, ct);
            await RunChecksAsync(plan, report, tx, ct);
            if (options.DryRun || !report.Passed)
            {
                await tx.RollbackAsync(ct);
                if (!options.DryRun) report.Warnings.Add("Integrity checks failed: the import transaction was rolled back; nothing was written.");
            }
            else
            {
                await tx.CommitAsync(ct);
            }
        });
        report.FinishedAt = clock.UtcNow;
        return report;
    }

    private async Task ApplyAsync(Plan plan, ImportReport report, DateTimeOffset now, CancellationToken ct)
    {
        // Roles must exist (normally created by `migrate`).
        var roleIds = await db.Roles.ToDictionaryAsync(r => r.Name!, r => r.Id, ct);
        foreach (var role in Roles.All.Where(r => !roleIds.ContainsKey(r)))
        {
            var entity = new ApplicationRole(role) { ConcurrencyStamp = Guid.NewGuid().ToString() };
            db.Roles.Add(entity);
            await db.SaveChangesAsync(ct);
            roleIds[role] = entity.Id;
        }

        // Users (+ role assignment).
        var userIds = plan.Users.Select(u => u.User.Id).ToList();
        var existingUsers = await db.Users.Where(u => userIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id, ct);
        foreach (var (user, role) in plan.Users)
        {
            if (existingUsers.TryGetValue(user.Id, out var current))
            {
                if (current.LegacyId != user.LegacyId)
                {
                    report.Skip("users", user.Id, "target already has a different (non-legacy) user with this id");
                    continue;
                }
                // Keep the upgraded hash / security stamp if the user has logged in since a previous import.
                current.Name = user.Name;
                current.Email = user.Email;
                current.NormalizedEmail = user.NormalizedEmail;
                current.UserName = user.UserName;
                current.NormalizedUserName = user.NormalizedUserName;
                current.IsActive = user.IsActive;
                current.LegacyUserCode = user.LegacyUserCode;
                current.CreatedAt = user.CreatedAt;
            }
            else
            {
                var emailOwner = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.NormalizedEmail == user.NormalizedEmail, ct);
                if (emailOwner is not null)
                {
                    report.Skip("users", user.Id, $"email already used by target user {emailOwner.Id}");
                    continue;
                }
                db.Users.Add(user);
            }
            var assigned = await db.UserRoles.Where(ur => ur.UserId == user.Id).ToListAsync(ct);
            db.UserRoles.RemoveRange(assigned.Where(a => a.RoleId != roleIds[role]));
            if (assigned.All(a => a.RoleId != roleIds[role])) db.UserRoles.Add(new IdentityUserRole<long> { UserId = user.Id, RoleId = roleIds[role] });
            report.Table("users").Imported++;
        }
        await db.SaveChangesAsync(ct);

        await UpsertAsync(db.Servers, plan.Servers, s => s.PrivateKeyCiphertext = Seal(s.PrivateKeyCiphertext), report.Table("servers"), ct);
        if (plan.Worker is { } w) await UpsertAsync(db.WorkerNodes, [w], x => x.PrivateKeyCiphertext = Seal(x.PrivateKeyCiphertext) ?? string.Empty, report.Table("worker_settings"), ct);
        if (plan.Ssh is { } ssh) await UpsertAsync(db.SshSettings, [ssh], x => x.DefaultPrivateKeyCiphertext = Seal(x.DefaultPrivateKeyCiphertext), report.Table("ssh_settings"), ct);
        if (plan.Storage is { } st) await UpsertAsync(db.StorageSettings, [st], null, report.Table("nfs_settings"), ct);
        await UpsertAsync(db.Variables, plan.Variables, v => v.ValueCiphertext = v.IsSecret ? Seal(v.ValueCiphertext ?? string.Empty) : null, report.Table("variables"), ct);
        await UpsertAsync(db.ScriptTemplates, plan.Templates, null, report.Table("templates"), ct, preserve: (existing, planned) =>
        {
            // A template edited after a previous import keeps its newer revision.
            if (existing.Script != planned.Script) planned.Revision = existing.Revision + 1;
            else planned.Revision = existing.Revision;
        });
        await UpsertAsync(db.S3Destinations, plan.Destinations, d => d.SecretKeyCiphertext = Seal(d.SecretKeyCiphertext) ?? string.Empty, report.Table("s3_settings"), ct);
        if (plan.Smtp is { } smtp) await UpsertAsync(db.SmtpSettings, [smtp], x => x.PasswordCiphertext = Seal(x.PasswordCiphertext), report.Table("smtp_settings"), ct);
        if (plan.Teams is { } teams) await UpsertAsync(db.TeamsSettings, [teams], x => x.WebhookUrlCiphertext = Seal(x.WebhookUrlCiphertext), report.Table("teams_settings"), ct);
        if (plan.Notifications is { } ns) await UpsertAsync(db.NotificationSettings, [ns], null, report.Table("notification_settings"), ct);

        // Identity sequences must be moved past preserved ids before generating any new id.
        await ResetSequencesAsync(ct);

        // Placeholder templates for jobs whose legacy template vanished.
        foreach (var (job, missing) in plan.Jobs.Where(j => j.MissingTemplate is not null))
        {
            var (name, category) = missing!.Value;
            var tpl = await db.ScriptTemplates.FirstOrDefaultAsync(t => t.Name == name && t.Category == category, ct);
            if (tpl is null)
            {
                tpl = new ScriptTemplate
                {
                    Name = LegacyMapping.Truncate(name, 200)!, Category = LegacyMapping.Truncate(category, 100)!, Script = LegacyMapping.MissingTemplateScript, Revision = 1,
                    Description = "Created by the legacy import because the referenced template no longer existed. Replace the job's template.",
                };
                db.ScriptTemplates.Add(tpl);
                await db.SaveChangesAsync(ct);
            }
            job.ScriptTemplateId = tpl.Id;
            job.Category = tpl.Category;
        }

        await UpsertAsync(db.BackupJobs, plan.Jobs.Select(j => j.Job).ToList(), null, report.Table("backup_jobs"), ct, preserve: (existing, planned) =>
        {
            // Never undo decisions an operator made in Backora after a previous import.
            if (existing.DeletedAt is not null && planned.DeletedAt is null) planned.DeletedAt = existing.DeletedAt;
            // A re-import never re-activates a job that is inactive in Backora (paused by an operator or by a previous import).
            if (existing.Status == JobStatus.Inactive) planned.Status = JobStatus.Inactive;
            if (planned.Status != JobStatus.Active || planned.DeletedAt is not null) planned.NextRunAt = null;
        });
        await UpsertAsync(db.BackupRuns, plan.Runs, null, report.Table("backup_job_executions"), ct, preserve: (existing, planned) =>
        {
            if (existing.Trigger != RunTrigger.Imported) throw new InvalidOperationException($"backup_runs id {existing.Id} exists and was not created by the legacy import.");
        });
        await UpsertAsync(db.OffloadRuns, plan.Offloads, null, report.Table("upload_to_s3_job_executions"), ct, preserve: (existing, planned) =>
        {
            // Retention/protection state is owned by Backora once imported.
            planned.RetentionState = existing.RetentionState;
            planned.IsProtected = existing.IsProtected;
            planned.ProtectionReason = existing.ProtectionReason;
            planned.ProtectedByUserId = existing.ProtectedByUserId;
            planned.DeletedAt = existing.DeletedAt;
            planned.DeletionReason = existing.DeletionReason;
        });
        await UpsertAsync(db.RestoreRuns, plan.Restores, null, report.Table("restore_to_nfs_job_executions"), ct);

        await ResetSequencesAsync(ct);

        db.AuditEvents.Add(new AuditEvent
        {
            OccurredAt = now,
            ActorName = "legacy-import",
            Action = "legacy.import",
            ResourceType = "Database",
            ResourceId = report.LegacyDatabase,
            Outcome = AuditOutcome.Success,
            DetailsJson = JsonSerializer.Serialize(report.Tables.ToDictionary(t => t.Key, t => new { source = t.Value.Source, imported = t.Value.Imported, skipped = t.Value.Skipped.Count })),
        });
        await db.SaveChangesAsync(ct);
    }

    private string? Seal(string? plaintext) => plaintext is null ? null : secrets.Protect(plaintext);

    /// <summary>Upsert by preserved legacy id. New rows are inserted with their explicit id.</summary>
    private async Task UpsertAsync<T>(DbSet<T> set, IReadOnlyList<T> planned, Action<T>? seal, TableReport table, CancellationToken ct, Action<T, T>? preserve = null)
        where T : Entity
    {
        foreach (var chunk in planned.Chunk(500))
        {
            var ids = chunk.Select(e => e.Id).ToList();
            var existing = await set.Where(e => ids.Contains(e.Id)).ToDictionaryAsync(e => e.Id, ct);
            foreach (var entity in chunk)
            {
                seal?.Invoke(entity);
                if (existing.TryGetValue(entity.Id, out var current))
                {
                    preserve?.Invoke(current, entity);
                    var createdAt = entity.CreatedAt;
                    var entry = db.Entry(current);
                    entry.CurrentValues.SetValues(entity);
                    entry.Property(nameof(Entity.Version)).IsModified = false;
                    current.CreatedAt = createdAt;
                }
                else
                {
                    set.Add(entity);
                }
                table.Imported++;
            }
            await db.SaveChangesAsync(ct);
            db.ChangeTracker.Clear();
        }
    }

    private async Task ResetSequencesAsync(CancellationToken ct)
    {
        foreach (var table in SequenceTables)
        {
            // Fixed list of table names; setval(…, max, true) or (…, 1, false) for empty tables.
            await db.Database.ExecuteSqlRawAsync(
                $"SELECT setval(pg_get_serial_sequence('{table}', 'id'), COALESCE((SELECT MAX(id) FROM {table}), 1), (SELECT MAX(id) FROM {table}) IS NOT NULL)", ct);
        }
    }

    // ───────────────────────────── Checks / verify ─────────────────────────────

    private static readonly (string Legacy, string Target)[] TableMap =
    [
        ("users", "users"), ("servers", "servers"), ("worker_settings", "worker_nodes"), ("ssh_settings", "ssh_settings"),
        ("nfs_settings", "storage_settings"), ("variables", "variables"), ("templates", "script_templates"), ("s3_settings", "s3_destinations"),
        ("smtp_settings", "smtp_settings"), ("teams_settings", "teams_settings"), ("notification_settings", "notification_settings"),
        ("backup_jobs", "backup_jobs"), ("backup_job_executions", "backup_runs"), ("upload_to_s3_job_executions", "offload_runs"),
        ("restore_to_nfs_job_executions", "restore_runs"),
    ];

    private static readonly HashSet<string> Singletons = new(StringComparer.Ordinal) { "worker_settings", "ssh_settings", "nfs_settings", "smtp_settings", "teams_settings", "notification_settings" };

    private async Task RunChecksAsync(Plan plan, ImportReport report, IDbContextTransaction? tx, CancellationToken ct)
    {
        var conn = db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open) await db.Database.OpenConnectionAsync(ct);
        var dbTx = tx?.GetDbTransaction();

        foreach (var (legacy, target) in TableMap)
        {
            var t = report.Table(legacy);
            var expected = plan.ExpectedIds.TryGetValue(legacy, out var set) ? set : [];
            var targetIds = Singletons.Contains(legacy)
                ? await ScalarAsync(conn, dbTx, $"SELECT COUNT(*) FROM {target} WHERE id = 1", ct) > 0 ? (expected.Count > 0 ? new HashSet<long>(expected) : []) : []
                : await IdsAsync(conn, dbTx, target, expected, ct);
            t.Target = targetIds.Count;
            var missing = expected.Except(targetIds).ToList();
            var accounted = t.Source == expected.Count + t.Skipped.Count(s => !expected.Contains(s.Id)) || Singletons.Contains(legacy) && expected.Count <= 1;
            report.Check($"{legacy} → {target} completeness", missing.Count == 0 && accounted,
                missing.Count == 0
                    ? $"source {t.Source}, expected {expected.Count}, present {targetIds.Count}, skipped {t.Skipped.Count}"
                    : $"{missing.Count} expected row(s) missing in target: {string.Join(", ", missing.Take(20))}");
        }

        await CheckZero(conn, dbTx, report, "runs reference existing jobs", "SELECT COUNT(*) FROM backup_runs r LEFT JOIN backup_jobs j ON j.id = r.backup_job_id WHERE j.id IS NULL", ct);
        await CheckZero(conn, dbTx, report, "runs reference existing servers", "SELECT COUNT(*) FROM backup_runs r LEFT JOIN servers s ON s.id = r.server_id WHERE s.id IS NULL", ct);
        await CheckZero(conn, dbTx, report, "jobs reference existing templates and servers", "SELECT COUNT(*) FROM backup_jobs j LEFT JOIN script_templates t ON t.id = j.script_template_id LEFT JOIN servers s ON s.id = j.server_id WHERE t.id IS NULL OR s.id IS NULL", ct);
        await CheckZero(conn, dbTx, report, "offloads reference existing runs (or none)", "SELECT COUNT(*) FROM offload_runs o LEFT JOIN backup_runs r ON r.id = o.backup_run_id WHERE o.backup_run_id IS NOT NULL AND r.id IS NULL", ct);
        await CheckZero(conn, dbTx, report, "offloads reference existing destinations", "SELECT COUNT(*) FROM offload_runs o LEFT JOIN s3_destinations d ON d.id = o.s3_destination_id WHERE d.id IS NULL", ct);
        await CheckZero(conn, dbTx, report, "restores reference existing recovery points", "SELECT COUNT(*) FROM restore_runs x LEFT JOIN offload_runs o ON o.id = x.offload_run_id WHERE o.id IS NULL", ct);
        await CheckZero(conn, dbTx, report, "no imported operation is active (nothing will auto-execute)",
            "SELECT (SELECT COUNT(*) FROM backup_runs WHERE trigger = 'Imported' AND status IN ('Pending','Starting','Running','Verifying','Cancelling'))" +
            " + (SELECT COUNT(*) FROM offload_runs WHERE legacy_prefix AND status IN ('Pending','Running','Verifying'))" +
            " + (SELECT COUNT(*) FROM restore_runs WHERE reason = 'Imported from legacy system' AND status IN ('Pending','Running','Verifying','Cancelling'))", ct);
        await CheckZero(conn, dbTx, report, "all stored secrets are encrypted (bk1 envelope)",
            "SELECT (SELECT COUNT(*) FROM servers WHERE private_key_ciphertext IS NOT NULL AND private_key_ciphertext NOT LIKE 'bk1:%')" +
            " + (SELECT COUNT(*) FROM worker_nodes WHERE private_key_ciphertext NOT LIKE 'bk1:%')" +
            " + (SELECT COUNT(*) FROM ssh_settings WHERE default_private_key_ciphertext IS NOT NULL AND default_private_key_ciphertext NOT LIKE 'bk1:%')" +
            " + (SELECT COUNT(*) FROM variables WHERE value_ciphertext IS NOT NULL AND value_ciphertext NOT LIKE 'bk1:%')" +
            " + (SELECT COUNT(*) FROM s3_destinations WHERE secret_key_ciphertext NOT LIKE 'bk1:%')" +
            " + (SELECT COUNT(*) FROM smtp_settings WHERE password_ciphertext IS NOT NULL AND password_ciphertext NOT LIKE 'bk1:%')" +
            " + (SELECT COUNT(*) FROM teams_settings WHERE webhook_url_ciphertext IS NOT NULL AND webhook_url_ciphertext NOT LIKE 'bk1:%')", ct);
        await CheckZero(conn, dbTx, report, "every imported user has a role", "SELECT COUNT(*) FROM users u WHERE u.legacy_id IS NOT NULL AND NOT EXISTS (SELECT 1 FROM user_roles ur WHERE ur.user_id = u.id)", ct);

        var untrusted = await ScalarAsync(conn, dbTx, "SELECT COUNT(*) FROM servers WHERE host_key_fingerprint IS NULL", ct);
        report.Check("host keys require verification", true, $"{untrusted} server(s) untrusted (expected after import; trust fingerprints in the UI)");
    }

    /// <summary>Recomputes the plan and compares it with the target without writing anything.</summary>
    public async Task<ImportReport> VerifyAsync(LegacySnapshot snapshot, CancellationToken ct)
    {
        var now = clock.UtcNow;
        var report = new ImportReport { Mode = "verification", StartedAt = now, LegacyDatabase = snapshot.Database, LegacyServerVersion = snapshot.ServerVersion };
        var plan = BuildPlan(snapshot, report, now);
        foreach (var (_, t) in report.Tables) t.Imported = 0;
        await RunChecksAsync(plan, report, null, ct);
        foreach (var (legacy, _) in TableMap) report.Table(legacy).Imported = report.Table(legacy).Target;
        report.FinishedAt = clock.UtcNow;
        return report;
    }

    private static async Task CheckZero(DbConnection conn, DbTransaction? tx, ImportReport report, string name, string sql, CancellationToken ct)
    {
        var n = await ScalarAsync(conn, tx, sql, ct);
        report.Check(name, n == 0, n == 0 ? "ok" : $"{n} violation(s)");
    }

    private static async Task<long> ScalarAsync(DbConnection conn, DbTransaction? tx, string sql, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        return Convert.ToInt64(await cmd.ExecuteScalarAsync(ct), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task<HashSet<long>> IdsAsync(DbConnection conn, DbTransaction? tx, string table, HashSet<long> ids, CancellationToken ct)
    {
        var found = new HashSet<long>();
        if (ids.Count == 0) return found;
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = $"SELECT id FROM {table} WHERE id = ANY(@ids)";
        var p = cmd.CreateParameter();
        p.ParameterName = "ids";
        p.Value = ids.ToArray();
        cmd.Parameters.Add(p);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) found.Add(r.GetInt64(0));
        return found;
    }
}
