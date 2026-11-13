using Backora.Application.Abstractions;
using Backora.Application.Common;
using Backora.Contracts.V1;
using Backora.Domain;
using Backora.Domain.Backups;
using Backora.Domain.Execution;
using Backora.Domain.Platform;
using Backora.Domain.Scheduling;
using Microsoft.EntityFrameworkCore;

namespace Backora.Application.Backups;

public sealed class BackupJobService(IAppDbContext db, ICurrentUser user, IAuditLog audit, IClock clock, ISecretProtector secrets)
{
    public async Task<PagedResult<BackupJobDto>> ListAsync(string? q, JobStatusDto? status, string? category, long? serverId, HealthState? health, PageRequest page, CancellationToken ct)
    {
        var query = db.BackupJobs.AsNoTracking().Include(j => j.Server).Include(j => j.ScriptTemplate).Include(j => j.S3Destination)
            .Include(j => j.ReplicationTargets).ThenInclude(t => t.S3Destination).AsSplitQuery().Where(j => j.DeletedAt == null);
        if (status is { } st) query = query.Where(j => j.Status == st.ToDomain());
        if (!string.IsNullOrWhiteSpace(category)) query = query.Where(j => j.Category == category);
        if (serverId is { } sid) query = query.Where(j => j.ServerId == sid);
        if (!string.IsNullOrWhiteSpace(q))
        {
            var term = $"%{q.Trim()}%";
            query = query.Where(j => EF.Functions.ILike(j.Name, term) || EF.Functions.ILike(j.Category, term) || EF.Functions.ILike(j.Server!.Name, term) || EF.Functions.ILike(j.ScriptTemplate!.Name, term));
        }
        query = (page.SortField, page.Descending) switch
        {
            ("nextrunat", false) => query.OrderBy(j => j.NextRunAt == null).ThenBy(j => j.NextRunAt),
            ("nextrunat", true) => query.OrderByDescending(j => j.NextRunAt),
            ("createdat", false) => query.OrderBy(j => j.CreatedAt),
            ("createdat", true) => query.OrderByDescending(j => j.CreatedAt),
            (_, true) => query.OrderByDescending(j => j.Name),
            _ => query.OrderBy(j => j.Name),
        };

        if (health is not null)
        {
            // Health is derived; filter in memory over the (bounded) job set.
            var all = await DecorateAsync(await query.ToListAsync(ct), ct);
            var filtered = all.Where(j => j.Health == health).ToList();
            return new PagedResult<BackupJobDto>(filtered.Skip(page.Skip).Take(page.SafePageSize).ToList(), page.SafePage, page.SafePageSize, filtered.Count);
        }

        var total = await query.LongCountAsync(ct);
        var jobs = await query.Skip(page.Skip).Take(page.SafePageSize).ToListAsync(ct);
        return new PagedResult<BackupJobDto>(await DecorateAsync(jobs, ct), page.SafePage, page.SafePageSize, total);
    }

    public async Task<IReadOnlyList<string>> CategoriesAsync(CancellationToken ct)
    {
        var fromJobs = await db.BackupJobs.AsNoTracking().Where(j => j.DeletedAt == null).Select(j => j.Category).Distinct().ToListAsync(ct);
        var fromTemplates = await db.ScriptTemplates.AsNoTracking().Select(t => t.Category).Distinct().ToListAsync(ct);
        return fromJobs.Union(fromTemplates).Where(c => !string.IsNullOrEmpty(c)).Order(StringComparer.OrdinalIgnoreCase).ToList();
    }

    public async Task<BackupJobDto> GetAsync(long id, CancellationToken ct)
    {
        var job = await db.BackupJobs.AsNoTracking().Include(j => j.Server).Include(j => j.ScriptTemplate).Include(j => j.S3Destination)
            .Include(j => j.ReplicationTargets).ThenInclude(t => t.S3Destination).AsSplitQuery()
            .FirstOrDefaultAsync(j => j.Id == id && j.DeletedAt == null, ct) ?? throw new NotFoundException("Backup job", id);
        return (await DecorateAsync([job], ct))[0];
    }

    public async Task<BackupJobDto> CreateAsync(BackupJobUpsertRequest r, CancellationToken ct)
    {
        var job = new BackupJob { CreatedByUserId = user.UserId };
        await ApplyAsync(job, r, ct);
        db.BackupJobs.Add(job);
        await db.SaveChangesAsync(ct);
        audit.Record("backup_job.created", "BackupJob", job.Id.ToString(), details: Snapshot(job));
        await db.SaveChangesAsync(ct);
        return await GetAsync(job.Id, ct);
    }

    public async Task<BackupJobDto> UpdateAsync(long id, BackupJobUpsertRequest r, CancellationToken ct)
    {
        var job = await db.BackupJobs.Include(j => j.ReplicationTargets).AsSplitQuery().FirstOrDefaultAsync(j => j.Id == id && j.DeletedAt == null, ct) ?? throw new NotFoundException("Backup job", id);
        Concurrency.EnsureVersion(job, r.Version);
        var before = Snapshot(job);
        if (job.Name != r.Name && await db.BackupRuns.AnyAsync(x => x.BackupJobId == id, ct))
        {
            throw new ConflictException("job_rename_with_history",
                "Jobs with history cannot be renamed: the name is the NFS directory and S3 prefix of existing recovery points. Create a new job instead.");
        }
        await ApplyAsync(job, r, ct);
        audit.Record("backup_job.updated", "BackupJob", id.ToString(), details: new { before, after = Snapshot(job) });
        await db.SaveChangesAsync(ct);
        return await GetAsync(id, ct);
    }

    public async Task<BackupJobDto> SetStatusAsync(long id, JobStatusDto status, CancellationToken ct)
    {
        var job = await db.BackupJobs.FirstOrDefaultAsync(j => j.Id == id && j.DeletedAt == null, ct) ?? throw new NotFoundException("Backup job", id);
        job.Status = status.ToDomain();
        job.NextRunAt = job.IsScheduled ? SchedulePlanner.NextOccurrence(job.CronExpression!, job.TimeZone, clock.UtcNow) : null;
        audit.Record(status == JobStatusDto.Active ? "backup_job.resumed" : "backup_job.paused", "BackupJob", id.ToString(), details: new { job.Name });
        await db.SaveChangesAsync(ct);
        return await GetAsync(id, ct);
    }

    public async Task DeleteAsync(long id, CancellationToken ct)
    {
        var job = await db.BackupJobs.FirstOrDefaultAsync(j => j.Id == id && j.DeletedAt == null, ct) ?? throw new NotFoundException("Backup job", id);
        if (await RunFactory.HasActiveRunAsync(db, id, ct)) throw new ConflictException("job_has_active_run", "Cancel or wait for the active run before deleting the job.");
        // Soft delete: history and recovery points remain restorable; retention keeps applying the policy.
        job.DeletedAt = clock.UtcNow;
        job.Status = JobStatus.Inactive;
        job.NextRunAt = null;
        audit.Record("backup_job.deleted", "BackupJob", id.ToString(), details: new { job.Name });
        await db.SaveChangesAsync(ct);
    }

    public async Task<RunCreatedResponse> RunNowAsync(long id, string? idempotencyKey, RunTrigger trigger, CancellationToken ct)
    {
        var job = await db.BackupJobs.Include(j => j.ScriptTemplate).FirstOrDefaultAsync(j => j.Id == id && j.DeletedAt == null, ct)
            ?? throw new NotFoundException("Backup job", id);

        if (!string.IsNullOrWhiteSpace(idempotencyKey) && user.UserId is { } uid)
        {
            var existing = await db.IdempotencyRecords.AsNoTracking().FirstOrDefaultAsync(r => r.UserId == uid && r.Scope == "run:" + id && r.Key == idempotencyKey, ct);
            if (existing?.ResourceId is { } rid) return new RunCreatedResponse(rid, true);
        }

        if (await RunFactory.HasActiveRunAsync(db, id, ct))
        {
            throw new ConflictException("run_already_active", "A run of this job is already in progress.");
        }

        var run = RunFactory.Create(job, trigger, clock.UtcNow, user.UserId, user.Name, user.CorrelationId);
        db.BackupRuns.Add(run);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (RunFactory.IsActiveRunViolation(ex))
        {
            throw new ConflictException("run_already_active", "A run of this job is already in progress.");
        }

        if (!string.IsNullOrWhiteSpace(idempotencyKey) && user.UserId is { } u)
        {
            db.IdempotencyRecords.Add(new IdempotencyRecord { UserId = u, Scope = "run:" + id, Key = idempotencyKey, ResourceType = nameof(BackupRun), ResourceId = run.Id, CreatedAt = clock.UtcNow });
        }
        audit.Record("backup_run.requested", "BackupRun", run.Id.ToString(), details: new { jobId = id, job.Name, trigger = trigger.ToString() });
        await db.SaveChangesAsync(ct);
        return new RunCreatedResponse(run.Id, false);
    }

    public static SchedulePreviewResponse Preview(SchedulePreviewRequest r, DateTimeOffset now)
    {
        var tz = string.IsNullOrWhiteSpace(r.TimeZone) ? "UTC" : r.TimeZone;
        if (string.IsNullOrWhiteSpace(r.CronExpression)) return new SchedulePreviewResponse(true, null, "Manual only", []);
        if (!SchedulePlanner.TryGetTimeZone(tz, out _)) return new SchedulePreviewResponse(false, $"Unknown time zone '{tz}'.", string.Empty, []);
        if (!SchedulePlanner.TryParse(r.CronExpression, out _, out var error)) return new SchedulePreviewResponse(false, error, string.Empty, []);
        return new SchedulePreviewResponse(true, null, SchedulePlanner.Describe(r.CronExpression, tz), SchedulePlanner.NextOccurrences(r.CronExpression, tz, now, 5));
    }

    public async Task<ScriptPreviewResponse> ScriptPreviewAsync(long id, CancellationToken ct)
    {
        var job = await db.BackupJobs.AsNoTracking().Include(j => j.ScriptTemplate).AsSplitQuery().FirstOrDefaultAsync(j => j.Id == id && j.DeletedAt == null, ct)
            ?? throw new NotFoundException("Backup job", id);
        var storage = await db.StorageSettings.AsNoTracking().FirstOrDefaultAsync(ct);
        var variables = await db.Variables.AsNoTracking().ToListAsync(ct);
        var masked = variables.Where(v => v.IsSecret).Select(v => v.Name).ToList();
        var map = variables.ToDictionary(v => v.Name, v => v.IsSecret ? "••••••••" : v.Value ?? string.Empty, StringComparer.Ordinal);
        var rendered = ScriptRenderer.Render(job.ScriptTemplate!.Script, new ScriptRenderContext(job.Name, job.Category, job.ScriptTemplate.Name, job.RetentionDays,
            storage?.NfsHost ?? string.Empty, storage?.NfsPath ?? string.Empty, map));
        _ = secrets; // secrets are intentionally never decrypted for previews
        var specs = TemplateSettings.Parse(job.ScriptTemplate.Script);
        var settingValues = TemplateSettings.Resolve(specs, job.Name, k => map.GetValueOrDefault(k), out var fromAllJobs);   // secret variables are already masked
        foreach (var sp in specs.Where(x => x.Secret && settingValues.ContainsKey(x.Name))) { settingValues[sp.Name] = "••••••••"; masked.Add(sp.Name); }
        rendered = TemplateSettings.Apply(rendered, specs, settingValues, fromAllJobs);
        return new ScriptPreviewResponse(rendered.Content, rendered.Sha256, rendered.UnresolvedPlaceholders, masked);
    }

    private async Task ApplyAsync(BackupJob job, BackupJobUpsertRequest r, CancellationToken ct)
    {
        var name = r.Name.Trim();
        if (await db.BackupJobs.AnyAsync(j => j.Name == name && j.Id != job.Id && j.DeletedAt == null, ct))
        {
            throw new ConflictException("job_name_taken", $"A backup job named '{name}' already exists (the name must be unique: it is the NFS directory and S3 prefix).");
        }
        var template = await db.ScriptTemplates.FirstOrDefaultAsync(t => t.Id == r.TemplateId, ct) ?? throw RequestValidationException.For("templateId", "Template not found.");
        if (!await db.Servers.AnyAsync(s => s.Id == r.ServerId, ct)) throw RequestValidationException.For("serverId", "Server not found.");
        if (r.S3DestinationId is { } did && !await db.S3Destinations.AnyAsync(d => d.Id == did, ct)) throw RequestValidationException.For("s3DestinationId", "S3 destination not found.");
        var replicaIds = (r.ReplicaDestinationIds ?? []).Distinct().ToList();
        if (replicaIds.Count > 0)
        {
            if (r.S3DestinationId is null) throw RequestValidationException.For("replicaDestinationIds", "Select a primary S3 destination before adding DR copies.");
            if (replicaIds.Contains(r.S3DestinationId.Value)) throw RequestValidationException.For("replicaDestinationIds", "A DR copy destination must differ from the primary destination.");
            var found = await db.S3Destinations.CountAsync(d => replicaIds.Contains(d.Id), ct);
            if (found != replicaIds.Count) throw RequestValidationException.For("replicaDestinationIds", "One or more DR copy destinations were not found.");
        }

        var now = clock.UtcNow;
        job.Name = name;
        job.Description = r.Description?.Trim();
        job.ScriptTemplateId = template.Id;
        job.Category = template.Category;
        job.ServerId = r.ServerId;
        job.Status = r.Status.ToDomain();
        job.CronExpression = string.IsNullOrWhiteSpace(r.CronExpression) ? null : string.Join(' ', r.CronExpression.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        job.TimeZone = string.IsNullOrWhiteSpace(r.TimeZone) ? "UTC" : r.TimeZone.Trim();
        job.MissedRunPolicy = r.MissedRunPolicy.ToDomain();
        job.MissedRunGraceMinutes = r.MissedRunGraceMinutes;
        job.RetentionDays = r.RetentionDays;
        job.MinRecoveryPointsToKeep = r.MinRecoveryPointsToKeep;
        job.AssignDestination(r.S3DestinationId, now);
        job.ReplicationTargets.RemoveAll(t => !replicaIds.Contains(t.S3DestinationId));
        foreach (var rid in replicaIds.Where(rid => job.ReplicationTargets.All(t => t.S3DestinationId != rid)))
        {
            job.ReplicationTargets.Add(new BackupJobReplicationTarget { S3DestinationId = rid });
        }
        job.KeepLocalCopy = r.KeepLocalCopy;
        job.RequireArtifacts = r.RequireArtifacts;
        job.TimeoutMinutes = r.TimeoutMinutes;
        job.MaxAttempts = r.MaxAttempts;
        job.NextRunAt = job.IsScheduled ? SchedulePlanner.NextOccurrence(job.CronExpression!, job.TimeZone, now) : null;
    }

    private static object Snapshot(BackupJob j) => new
    {
        j.Name, j.ScriptTemplateId, j.ServerId, status = j.Status.ToString(), j.CronExpression, j.TimeZone, j.RetentionDays,
        j.MinRecoveryPointsToKeep, j.S3DestinationId, replicaDestinationIds = j.ReplicationTargets.Select(t => t.S3DestinationId).Order().ToArray(),
        j.KeepLocalCopy, j.RequireArtifacts, j.TimeoutMinutes, j.MaxAttempts,
    };

    /// <summary>The connection settings a job gets from Backora variables (secret values are never returned).</summary>
    private static List<JobSettingDto> VariableSettings(BackupJob j, IReadOnlyDictionary<string, (bool Secret, string? Value)> vars)
    {
        var specs = TemplateSettings.Parse(j.ScriptTemplate?.Script);
        var values = TemplateSettings.Resolve(specs, j.Name, k => vars.TryGetValue(k, out var v) ? (v.Secret ? "secret" : v.Value) : null);
        return specs.Where(sp => values.ContainsKey(sp.Name))
            .Select(sp =>
            {
                var secret = sp.Secret || (vars.TryGetValue(TemplateSettings.VariableName(sp.Name, j.Name), out var a) ? a.Secret : vars.TryGetValue(sp.Name, out var b) && b.Secret);
                return new JobSettingDto(sp.Name, secret, secret ? null : values[sp.Name], true);
            }).ToList();
    }

    private async Task<List<BackupJobDto>> DecorateAsync(List<BackupJob> jobs, CancellationToken ct)
    {
        var ids = jobs.Select(j => j.Id).ToList();
        var lastRuns = await db.BackupRuns.AsNoTracking().Include(r => r.Server)
            .Where(r => ids.Contains(r.BackupJobId))
            .GroupBy(r => r.BackupJobId)
            .Select(g => g.OrderByDescending(r => r.CreatedAt).First())
            .ToListAsync(ct);
        var lastRunByJob = lastRuns.ToDictionary(r => r.BackupJobId);
        var lastSuccess = await db.BackupRuns.Where(r => ids.Contains(r.BackupJobId) && r.Status == RunStatus.Completed)
            .GroupBy(r => r.BackupJobId).Select(g => new { g.Key, At = g.Max(r => r.CompletedAt) }).ToDictionaryAsync(x => x.Key, x => x.At, ct);
        var lastOffloadFailed = await db.OffloadRuns.Where(o => o.BackupJobId != null && ids.Contains(o.BackupJobId!.Value) && o.SourceOffloadRunId == null)
            .GroupBy(o => o.BackupJobId!.Value).Select(g => new { g.Key, Failed = g.OrderByDescending(o => o.CreatedAt).Select(o => o.Status == OffloadStatus.Failed && o.DismissedAt == null).First() })
            .ToDictionaryAsync(x => x.Key, x => x.Failed, ct);
        var now = clock.UtcNow;
        var vars = (await db.Variables.AsNoTracking().Select(v => new { v.Name, v.IsSecret, v.Value }).ToListAsync(ct))
            .ToDictionary(v => v.Name, v => (v.IsSecret, v.Value), StringComparer.Ordinal);

        return jobs.Select(j =>
        {
            lastRunByJob.TryGetValue(j.Id, out var last);
            var success = lastSuccess.GetValueOrDefault(j.Id);
            var (health, reason) = JobHealth.Evaluate(j, last, success, lastOffloadFailed.GetValueOrDefault(j.Id), now);
            return new BackupJobDto(j.Id, j.Name, j.Category, j.Description, j.ScriptTemplateId, j.ScriptTemplate?.Name ?? string.Empty, j.ServerId,
                j.Server?.Name ?? string.Empty, j.Server?.Host ?? string.Empty, j.Status.ToDto(), j.CronExpression, j.TimeZone,
                SchedulePlanner.Describe(j.CronExpression, j.TimeZone), j.MissedRunPolicy.ToDto(), j.MissedRunGraceMinutes, j.RetentionDays,
                j.MinRecoveryPointsToKeep, j.S3DestinationId, j.S3Destination?.Name, j.S3DestinationAssignedAt, j.KeepLocalCopy, j.RequireArtifacts,
                j.TimeoutMinutes, j.MaxAttempts, j.NextRunAt, last?.ToSummary(), success, health, reason, j.Version, j.CreatedAt, j.UpdatedAt,
                j.ReplicationTargets.OrderBy(t => t.S3Destination?.Name, StringComparer.OrdinalIgnoreCase)
                    .Select(t => new ReplicationTargetDto(t.S3DestinationId, t.S3Destination?.Name ?? $"#{t.S3DestinationId}", t.S3Destination?.Bucket ?? string.Empty)).ToList(),
                VariableSettings(j, vars));
        }).ToList();
    }
}

public static class JobHealth
{
    /// <summary>Healthy / Warning / Critical / Unknown derived from the latest outcomes and expected cadence.</summary>
    public static (HealthState, string?) Evaluate(BackupJob job, BackupRun? last, DateTimeOffset? lastSuccess, bool lastOffloadFailed, DateTimeOffset now)
    {
        if (last is null) return (HealthState.Unknown, job.Status == JobStatus.Active ? "No runs yet" : "Paused, never run");
        if (last is { Status: RunStatus.Failed, DismissedAt: null }) return (HealthState.Critical, $"Last run failed ({last.FailureClass})");
        if (lastOffloadFailed) return (HealthState.Critical, "Last S3 offload failed");
        if (job.IsScheduled && lastSuccess is { } ls)
        {
            var next = SchedulePlanner.NextOccurrences(job.CronExpression!, job.TimeZone, ls, 2);
            if (next.Count == 2)
            {
                var interval = next[1] - next[0];
                if (now - ls > interval * 2 + TimeSpan.FromHours(1)) return (HealthState.Warning, $"No successful backup since {ls:yyyy-MM-dd HH:mm} UTC");
            }
        }
        if (job.IsScheduled && lastSuccess is null && last.IsTerminal) return (HealthState.Warning, "No successful backup yet");
        if (last.Status == RunStatus.Cancelled) return (HealthState.Warning, "Last run was cancelled");
        if (job.Status == JobStatus.Inactive) return (HealthState.Unknown, "Paused");
        return (HealthState.Healthy, null);
    }
}
