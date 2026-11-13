using System.Net.Http;
using System.Text.Json;
using Backora.Application.Abstractions;
using Backora.Application.Backups;
using Backora.Application.Notifications;
using Backora.Application.Telemetry;
using Backora.Domain;
using Backora.Domain.Backups;
using Backora.Domain.Platform;
using Backora.Domain.Retention;
using Backora.Domain.Scheduling;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Backora.Application.Operations;

/// <summary>
/// Cron scheduler. Runs under a cluster lock so only one instance schedules at a time; additionally the
/// unique index on (job, scheduled_for) makes double scheduling impossible even across lock handover.
/// Concurrency policy (legacy-compatible): if the previous run is still active the occurrence is skipped.
/// </summary>
public sealed class SchedulerService(IAppDbContext db, IClock clock, ILogger<SchedulerService> logger)
{
    public async Task<int> TickAsync(CancellationToken ct)
    {
        var now = clock.UtcNow;
        var jobs = await db.BackupJobs
            .Include(j => j.ScriptTemplate)
            .Where(j => j.Status == JobStatus.Active && j.CronExpression != null && j.CronExpression != "" && j.DeletedAt == null)
            .ToListAsync(ct);

        var created = 0;
        foreach (var job in jobs)
        {
            var decision = SchedulePlanner.Evaluate(job.CronExpression!, job.TimeZone, job.NextRunAt, now, job.MissedRunPolicy, job.MissedRunGraceMinutes);
            if (decision.ShouldRun && decision.ScheduledFor is { } occurrence)
            {
                if (await RunFactory.HasActiveRunAsync(db, job.Id, ct))
                {
                    logger.LogWarning("Skipping scheduled occurrence {Occurrence} of job {JobId}: previous run still active", occurrence, job.Id);
                    BackoraTelemetry.ScheduledRuns.Add(1, new System.Diagnostics.TagList { { "decision", "skipped_active" } });
                }
                else
                {
                    db.BackupRuns.Add(RunFactory.Create(job, RunTrigger.Scheduled, now, null, "scheduler", null, occurrence));
                    created++;
                    BackoraTelemetry.ScheduledRuns.Add(1, new System.Diagnostics.TagList { { "decision", "run" } });
                }
                job.LastScheduledFor = occurrence;
            }
            else if (decision.ScheduledFor is { } missed)
            {
                logger.LogWarning("Job {JobId} missed occurrence {Occurrence} ({Reason}); waiting for next", job.Id, missed, decision.Reason);
                job.LastScheduledFor = missed;
                BackoraTelemetry.ScheduledRuns.Add(1, new System.Diagnostics.TagList { { "decision", "missed" } });
            }

            job.NextRunAt = decision.NextRunAt;
            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException ex) when (RunFactory.IsActiveRunViolation(ex) || RunFactory.IsOccurrenceViolation(ex))
            {
                logger.LogInformation("Occurrence for job {JobId} already scheduled by another instance", job.Id);
                Detach(job);
            }
            catch (DbUpdateConcurrencyException)
            {
                Detach(job);
            }
        }
        return created;
    }

    private void Detach(BackupJob job)
    {
        if (db is DbContext ctx)
        {
            foreach (var entry in ctx.ChangeTracker.Entries().Where(e => e.State != EntityState.Unchanged).ToList()) entry.State = EntityState.Detached;
        }
        _ = job;
    }
}

/// <summary>Applies retention policies to recovery points (completed offloads) using the pure planner.</summary>
public sealed class RetentionService(IAppDbContext db, IClock clock, ISecretProtector secrets, IObjectStorage storage, IAuditLog audit, ILogger<RetentionService> logger)
{
    public async Task<int> TickAsync(CancellationToken ct)
    {
        var now = clock.UtcNow;
        var points = await db.OffloadRuns
            .Include(o => o.S3Destination)
            .Include(o => o.SourceOffloadRun)
            .Where(o => o.Status == OffloadStatus.Completed && o.BackupJobId != null
                        && (o.RetentionState == RetentionState.Active || o.RetentionState == RetentionState.Expired || o.RetentionState == RetentionState.DeleteFailed))
            .ToListAsync(ct);
        if (points.Count == 0) return 0;

        var activeRestorePoints = await db.RestoreRuns
            .Where(r => r.Status == RestoreStatus.Pending || r.Status == RestoreStatus.Running || r.Status == RestoreStatus.Verifying || r.Status == RestoreStatus.Cancelling || r.Status == RestoreStatus.AwaitingKey)
            .Select(r => r.OffloadRunId).Distinct().ToListAsync(ct);
        // A point that an in-flight DR copy is reading from must not be deleted underneath it.
        var activeCopySources = await db.OffloadRuns
            .Where(o => o.SourceOffloadRunId != null && (o.Status == OffloadStatus.Pending || o.Status == OffloadStatus.Running || o.Status == OffloadStatus.Verifying))
            .GroupBy(o => o.SourceOffloadRunId!.Value).Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Count, ct);
        var jobIds = points.Select(p => p.BackupJobId!.Value).Distinct().ToList();
        var jobs = await db.BackupJobs.AsNoTracking().Where(j => jobIds.Contains(j.Id)).ToDictionaryAsync(j => j.Id, ct);

        // Imported legacy points can share one hour-granular prefix; a prefix is only deleted when every
        // live point that uses it is deletable.
        var deletable = new HashSet<long>();
        var deleted = 0;
        // Policy is applied per job AND per destination: the primary bucket and each DR bucket each keep
        // their own minimum of verified points. A copy ages from the moment its source point was taken.
        foreach (var group in points.GroupBy(p => (Job: p.BackupJobId!.Value, p.S3DestinationId)))
        {
            if (!jobs.TryGetValue(group.Key.Job, out var job)) continue;
            var infos = group.Select(p => new RecoveryPointInfo(p.Id, p.BackupJobId, AgeBasis(p),
                p.VerificationStatus == VerificationStatus.Verified || p.LegacyPrefix, p.IsProtected, activeRestorePoints.Contains(p.Id), p.RetentionState));
            var decisions = RetentionPlanner.Plan(infos, job.RetentionDays, job.MinRecoveryPointsToKeep, now, activeCopySources);
            var cutoff = now - TimeSpan.FromSeconds((double)(job.RetentionDays * 86400m));

            foreach (var d in decisions)
            {
                var point = group.First(p => p.Id == d.RecoveryPointId);
                if (d.Action == RetentionAction.Keep)
                {
                    var newState = job.RetentionDays > 0 && AgeBasis(point) <= cutoff ? RetentionState.Expired : RetentionState.Active;
                    if (point.RetentionState != RetentionState.DeleteFailed && point.RetentionState != newState) point.RetentionState = newState;
                    continue;
                }

                deletable.Add(point.Id);
                point.DeletionReason = d.Reason;
            }
        }

        foreach (var shared in points.Where(p => deletable.Contains(p.Id)).GroupBy(p => (p.S3DestinationId, p.RemotePrefix)))
        {
            var users = points.Where(p => p.S3DestinationId == shared.Key.S3DestinationId && p.RemotePrefix == shared.Key.RemotePrefix).ToList();
            if (users.Any(u => !deletable.Contains(u.Id)))
            {
                foreach (var p in shared)
                {
                    p.RetentionState = RetentionState.Expired;
                    p.DeletionReason = $"Deletion deferred: prefix shared with live recovery point(s) {string.Join(", ", users.Where(u => !deletable.Contains(u.Id)).Select(u => u.Id))}.";
                }
                continue;
            }
            var first = shared.First();
            if (await DeleteAsync(first, first.DeletionReason ?? "expired", now, ct))
            {
                deleted++;
                foreach (var other in shared.Skip(1))
                {
                    other.RetentionState = RetentionState.Deleted;
                    other.DeletedAt = now;
                    other.DeletionReason = $"Retention: shared prefix deleted together with recovery point {first.Id}";
                    deleted++;
                }
            }
        }
        await db.SaveChangesAsync(ct);
        return deleted;
    }

    private static DateTimeOffset AgeBasis(OffloadRun p) =>
        p.SourceOffloadRun is { } src ? src.CompletedAt ?? src.CreatedAt : p.CompletedAt ?? p.CreatedAt;

    private async Task<bool> DeleteAsync(OffloadRun point, string reason, DateTimeOffset now, CancellationToken ct)
    {
        var dest = point.S3Destination;
        if (dest is null || string.IsNullOrWhiteSpace(point.RemotePrefix) || point.RemotePrefix.Trim('/').Length == 0 || !point.RemotePrefix.Contains('/', StringComparison.Ordinal))
        {
            point.RetentionState = RetentionState.DeleteFailed;
            point.DeletionReason = "Unsafe or missing prefix; manual review required.";
            return false;
        }

        point.RetentionState = RetentionState.Deleting;
        await db.SaveChangesAsync(ct);
        var conn = Storage.StorageConnections.For(dest, secrets);
        try
        {
            var count = await storage.DeletePrefixAsync(conn, point.RemotePrefix, ct);
            var remaining = await storage.ListAsync(conn, point.RemotePrefix, ct);
            if (remaining.Count > 0) throw new InvalidOperationException($"{remaining.Count} object(s) remain after deletion.");
            point.RetentionState = RetentionState.Deleted;
            point.DeletedAt = now;
            point.DeletionReason = $"Retention: {reason}";
            audit.Record("recovery_point.deleted", "RecoveryPoint", point.Id.ToString(System.Globalization.CultureInfo.InvariantCulture), AuditOutcome.Success,
                new { point.JobName, dest.Bucket, point.RemotePrefix, objects = count, reason });
            BackoraTelemetry.RetentionDeleted.Add(1);
            logger.LogInformation("Retention deleted recovery point {PointId} ({Objects} objects)", point.Id, count);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            point.RetentionState = RetentionState.DeleteFailed;
            point.DeletionReason = $"Deletion failed: {ex.Message}";
            audit.Record("recovery_point.delete_failed", "RecoveryPoint", point.Id.ToString(System.Globalization.CultureInfo.InvariantCulture), AuditOutcome.Failure, new { error = ex.Message });
            logger.LogError(ex, "Retention failed to delete recovery point {PointId}", point.Id);
            return false;
        }
    }
}

/// <summary>Dispatches the notification outbox (email via SMTP, Microsoft Teams webhooks) with retries.</summary>
public sealed class NotificationDispatcher(
    IAppDbContext db,
    IClock clock,
    ISecretProtector secrets,
    IEmailSender email,
    ITeamsSender teams,
    ILogger<NotificationDispatcher> logger)
{
    public const int MaxAttempts = 5;

    public async Task<int> TickAsync(CancellationToken ct)
    {
        var now = clock.UtcNow;
        var batch = await db.NotificationMessages
            .Where(m => m.Status == NotificationStatus.Pending && m.NextAttemptAt <= now)
            .OrderBy(m => m.Id).Take(25).ToListAsync(ct);
        if (batch.Count == 0) return 0;

        var smtp = await db.SmtpSettings.AsNoTracking().FirstOrDefaultAsync(ct);
        var teamsSettings = await db.TeamsSettings.AsNoTracking().FirstOrDefaultAsync(ct);
        var sent = 0;
        foreach (var message in batch)
        {
            var values = NotificationRenderer.ParsePayload(message.PayloadJson);
            message.Attempts++;
            try
            {
                if (message.Channel == NotificationChannel.Email)
                {
                    if (smtp is null || !smtp.Enabled || string.IsNullOrWhiteSpace(smtp.Host))
                    {
                        Skip(message, "SMTP is not configured or disabled.");
                        continue;
                    }
                    if (message.Recipients.Count == 0)
                    {
                        Skip(message, "No recipients configured.");
                        continue;
                    }
                    var template = message.EventType == NotificationEvents.PasswordReset ? DefaultTemplates.PasswordResetEmail : smtp.Template;
                    var html = NotificationRenderer.RenderEmail(template, values);
                    var password = smtp.PasswordCiphertext is { } pc ? secrets.Unprotect(pc) : null;
                    await email.SendAsync(smtp, password, message.Recipients, message.Subject ?? "Backora notification", html, ct);
                }
                else
                {
                    if (teamsSettings is null || !teamsSettings.Enabled || teamsSettings.WebhookUrlCiphertext is null)
                    {
                        Skip(message, "Teams webhook is not configured or disabled.");
                        continue;
                    }
                    var (json, error) = NotificationRenderer.RenderTeams(teamsSettings.Template, values);
                    if (error is not null)
                    {
                        message.Status = NotificationStatus.Failed;
                        message.LastError = error;
                        continue;
                    }
                    await teams.SendAsync(secrets.Unprotect(teamsSettings.WebhookUrlCiphertext), json, ct);
                }
                message.Status = NotificationStatus.Sent;
                message.SentAt = now;
                message.LastError = null;
                sent++;
                BackoraTelemetry.NotificationsSent.Add(1, new System.Diagnostics.TagList { { "channel", message.Channel.ToString() }, { "outcome", "sent" } });
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                message.LastError = ex.Message.Length > 1000 ? ex.Message[..1000] : ex.Message;
                if (message.Attempts >= MaxAttempts)
                {
                    message.Status = NotificationStatus.Failed;
                    logger.LogError(ex, "Notification {MessageId} via {Channel} failed permanently", message.Id, message.Channel);
                }
                else
                {
                    message.NextAttemptAt = now.AddSeconds(Math.Min(3600, 30 * Math.Pow(2, message.Attempts)));
                    logger.LogWarning("Notification {MessageId} via {Channel} failed (attempt {Attempt}): {Error}", message.Id, message.Channel, message.Attempts, ex.Message);
                }
                BackoraTelemetry.NotificationsSent.Add(1, new System.Diagnostics.TagList { { "channel", message.Channel.ToString() }, { "outcome", "error" } });
            }
            finally
            {
                // Saved after every message, even on shutdown: saving only at the end of the batch re-sent every email
                // of the batch whenever the process stopped or the save failed before it.
                await db.SaveChangesAsync(CancellationToken.None);
            }
        }
        return sent;
    }

    private static void Skip(NotificationMessage message, string reason)
    {
        message.Status = NotificationStatus.Skipped;
        message.LastError = reason;
    }
}
