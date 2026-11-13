using Backora.Application.Abstractions;
using Backora.Application.Common;
using Backora.Domain;
using Backora.Domain.Backups;
using Microsoft.EntityFrameworkCore;

namespace Backora.Application.Backups;

/// <summary>Creates backup runs. Duplicate active runs are prevented by a partial unique index.</summary>
public static class RunFactory
{
    public static BackupRun Create(BackupJob job, RunTrigger trigger, DateTimeOffset now, long? userId, string? userName, string? correlationId, DateTimeOffset? scheduledFor = null)
    {
        if (job.IsDeleted) throw new DomainRuleException("job_deleted", "The backup job has been deleted.");
        return new BackupRun
        {
            BackupJobId = job.Id,
            ServerId = job.ServerId,
            JobName = job.Name,
            Category = job.Category,
            TemplateName = job.ScriptTemplate?.Name ?? string.Empty,
            TemplateRevision = job.ScriptTemplate?.Revision,
            Status = RunStatus.Pending,
            Trigger = trigger,
            ScheduledFor = scheduledFor,
            RequestedByUserId = userId,
            RequestedByName = userName,
            MaxAttempts = job.MaxAttempts,
            NextAttemptAt = now,
            CorrelationId = correlationId,
        };
    }

    public static bool IsActiveRunViolation(DbUpdateException ex) =>
        ex.InnerException?.Message.Contains("ux_backup_runs_one_active_per_job", StringComparison.Ordinal) == true;

    public static bool IsOccurrenceViolation(DbUpdateException ex) =>
        ex.InnerException?.Message.Contains("ux_backup_runs_job_occurrence", StringComparison.Ordinal) == true;

    public static Task<bool> HasActiveRunAsync(IAppDbContext db, long jobId, CancellationToken ct) =>
        db.BackupRuns.AnyAsync(r => r.BackupJobId == jobId && BackupRun.ActiveStatuses.Contains(r.Status), ct);
}
