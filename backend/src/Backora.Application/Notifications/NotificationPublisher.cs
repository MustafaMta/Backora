using System.Text.Json;
using Backora.Application.Abstractions;
using Backora.Domain;
using Backora.Domain.Backups;
using Backora.Domain.Platform;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Backora.Application.Notifications;

public sealed class PlatformOptions
{
    public const string Section = "Platform";

    /// <summary>Public URL of the Blazor frontend, used for links in notifications and password reset emails.</summary>
    public string PublicUrl { get; set; } = "http://localhost:5200";
}

public static class NotificationEvents
{
    public const string BackupFailed = "backup.failed";
    public const string BackupSucceeded = "backup.succeeded";
    public const string OffloadFailed = "offload.failed";
    public const string RestoreFailed = "restore.failed";
    public const string RestoreCompleted = "restore.completed";
    public const string PasswordReset = "auth.password_reset";
    public const string Test = "test";
}

/// <summary>
/// Writes notification outbox rows in the caller's unit of work, so a notification exists if and only if
/// the state change that triggered it was committed. The worker dispatches them with retries.
/// </summary>
public sealed class NotificationPublisher(IAppDbContext db, IClock clock, IOptions<PlatformOptions> platform)
{
    public async Task PublishBackupAsync(BackupRun run, string eventType, CancellationToken ct)
    {
        var settings = await db.NotificationSettings.AsNoTracking().FirstOrDefaultAsync(ct) ?? new NotificationSettings();
        var enabled = eventType switch
        {
            NotificationEvents.BackupFailed => settings.NotifyOnBackupFailure,
            NotificationEvents.BackupSucceeded => settings.NotifyOnBackupSuccess,
            _ => false,
        };
        if (!enabled) return;

        var server = run.Server ?? await db.Servers.AsNoTracking().FirstOrDefaultAsync(s => s.Id == run.ServerId, ct);
        var values = new Dictionary<string, string?>
        {
            ["event"] = eventType,
            ["status"] = run.Status.ToString(),
            ["job_name"] = run.JobName,
            ["job_category"] = run.Category,
            ["script_name"] = run.TemplateName,
            ["server_name"] = server?.Name,
            ["server_ip"] = server?.Host,
            ["ssh_user"] = server?.SshUser,
            ["ssh_port"] = server?.SshPort.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["execution_id"] = run.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["started_at"] = Format(run.StartedAt),
            ["completed_at"] = Format(run.CompletedAt),
            ["failure_time"] = Format(run.CompletedAt ?? clock.UtcNow),
            ["total_time"] = FormatDuration(run.StartedAt, run.CompletedAt),
            ["error_message"] = run.ErrorMessage,
            ["failure_class"] = run.FailureClass.ToString(),
            ["output"] = Tail(run.Log, 4000),
            ["admin_url"] = $"{platform.Value.PublicUrl.TrimEnd('/')}/runs/{run.Id}",
        };
        var subject = eventType == NotificationEvents.BackupFailed
            ? $"[Backora] Backup failed: {run.JobName} on {server?.Name}"
            : $"[Backora] Backup completed: {run.JobName}";
        Enqueue(settings, eventType, subject, values, nameof(BackupRun), run.Id, run.CorrelationId);
    }

    public async Task PublishOffloadFailedAsync(OffloadRun offload, CancellationToken ct)
    {
        var settings = await db.NotificationSettings.AsNoTracking().FirstOrDefaultAsync(ct) ?? new NotificationSettings();
        if (!settings.NotifyOnOffloadFailure) return;
        var values = new Dictionary<string, string?>
        {
            ["event"] = NotificationEvents.OffloadFailed,
            ["status"] = offload.Status.ToString(),
            ["job_name"] = offload.JobName,
            ["execution_id"] = offload.BackupRunId?.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["started_at"] = Format(offload.StartedAt),
            ["completed_at"] = Format(offload.CompletedAt),
            ["failure_time"] = Format(offload.CompletedAt ?? clock.UtcNow),
            ["total_time"] = FormatDuration(offload.StartedAt, offload.CompletedAt),
            ["error_message"] = offload.ErrorMessage,
            ["failure_class"] = offload.FailureClass.ToString(),
            ["output"] = Tail(offload.Log, 4000),
            ["admin_url"] = $"{platform.Value.PublicUrl.TrimEnd('/')}/recovery-points?offload={offload.Id}",
        };
        Enqueue(settings, NotificationEvents.OffloadFailed, $"[Backora] S3 offload failed: {offload.JobName}", values, nameof(OffloadRun), offload.Id, offload.CorrelationId);
    }

    public async Task PublishRestoreAsync(RestoreRun restore, CancellationToken ct)
    {
        var settings = await db.NotificationSettings.AsNoTracking().FirstOrDefaultAsync(ct) ?? new NotificationSettings();
        var failed = restore.Status == RestoreStatus.Failed;
        if (failed ? !settings.NotifyOnRestoreFailure : !settings.NotifyOnRestoreCompleted) return;
        var eventType = failed ? NotificationEvents.RestoreFailed : NotificationEvents.RestoreCompleted;
        var values = new Dictionary<string, string?>
        {
            ["event"] = eventType,
            ["status"] = restore.Status.ToString(),
            ["job_name"] = restore.JobName,
            ["execution_id"] = restore.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["started_at"] = Format(restore.StartedAt),
            ["completed_at"] = Format(restore.CompletedAt),
            ["failure_time"] = Format(restore.CompletedAt ?? clock.UtcNow),
            ["total_time"] = FormatDuration(restore.StartedAt, restore.CompletedAt),
            ["error_message"] = restore.ErrorMessage ?? (failed ? null : $"Restored to {restore.TargetPath}"),
            ["failure_class"] = restore.FailureClass.ToString(),
            ["output"] = Tail(restore.Log, 4000),
            ["admin_url"] = $"{platform.Value.PublicUrl.TrimEnd('/')}/restores/{restore.Id}",
        };
        var subject = failed ? $"[Backora] Restore failed: {restore.JobName}" : $"[Backora] Restore completed: {restore.JobName}";
        Enqueue(settings, eventType, subject, values, nameof(RestoreRun), restore.Id, restore.CorrelationId);
    }

    private void Enqueue(NotificationSettings settings, string eventType, string subject, Dictionary<string, string?> values,
        string entityType, long entityId, string? correlationId)
    {
        var payload = JsonSerializer.Serialize(values);
        var now = clock.UtcNow;
        if (settings.Recipients.Count > 0)
        {
            db.NotificationMessages.Add(new NotificationMessage
            {
                Channel = NotificationChannel.Email,
                EventType = eventType,
                Subject = subject,
                PayloadJson = payload,
                Recipients = [.. settings.Recipients],
                NextAttemptAt = now,
                RelatedEntityType = entityType,
                RelatedEntityId = entityId,
                CorrelationId = correlationId,
            });
        }
        db.NotificationMessages.Add(new NotificationMessage
        {
            Channel = NotificationChannel.Teams,
            EventType = eventType,
            Subject = subject,
            PayloadJson = payload,
            NextAttemptAt = now,
            RelatedEntityType = entityType,
            RelatedEntityId = entityId,
            CorrelationId = correlationId,
        });
    }

    private static string? Format(DateTimeOffset? t) => t?.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss 'UTC'", System.Globalization.CultureInfo.InvariantCulture);

    public static string FormatDuration(DateTimeOffset? start, DateTimeOffset? end)
    {
        if (start is null || end is null) return "N/A";
        var d = end.Value - start.Value;
        return d.TotalHours >= 1 ? $"{(int)d.TotalHours}h {d.Minutes}m {d.Seconds}s" : d.TotalMinutes >= 1 ? $"{d.Minutes}m {d.Seconds}s" : $"{d.Seconds}s";
    }

    private static string? Tail(string? s, int max) => s is null ? null : s.Length <= max ? s : s[^max..];
}
