using System.Security.Cryptography;
using System.Text;
using Backora.Application.Abstractions;
using Backora.Application.Common;
using Backora.Application.Execution;
using Backora.Application.Notifications;
using Backora.Contracts.Security;
using Backora.Contracts.V1;
using Backora.Domain;
using Backora.Domain.Infrastructure;
using Backora.Domain.Platform;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Backora.Application.Inventory;

public sealed class SettingsService(
    IAppDbContext db,
    ICurrentUser user,
    ISecretProtector secrets,
    IAuditLog audit,
    IClock clock,
    ISshConnector ssh,
    IEmailSender email,
    ITeamsSender teams,
    IOptions<PlatformOptions> platform,
    ExecutionHostKeys keys)
{
    // ───────────── Worker node ─────────────
    public async Task<WorkerNodeDto> GetWorkerAsync(CancellationToken ct)
    {
        var w = await db.WorkerNodes.AsNoTracking().FirstOrDefaultAsync(ct);
        return w is null
            ? new WorkerNodeDto(false, string.Empty, 22, string.Empty, keys.KeyFromFile, null, null, "rclone", "/var/tmp/backora", null, null, null, 0, keys.Source)
            : new WorkerNodeDto(true, w.Host, w.SshPort, w.SshUser, keys.HasWorkerKey(w), w.HostKeyFingerprint, w.HostKeyTrustedAt,
                w.RclonePath, w.WorkDirectory, w.LastConnectionTestAt, w.LastConnectionTestSucceeded, w.LastConnectionTestMessage, w.Version, keys.Source);
    }

    public async Task<WorkerNodeDto> UpdateWorkerAsync(WorkerNodeUpdateRequest r, CancellationToken ct)
    {
        var w = await db.WorkerNodes.FirstOrDefaultAsync(ct);
        if (w is null)
        {
            if (string.IsNullOrWhiteSpace(r.PrivateKey) && !keys.KeyFromFile) throw RequestValidationException.For("privateKey", "A private key is required for the execution host.");
            w = new WorkerNode { Id = 1 };
            db.WorkerNodes.Add(w);
        }
        else
        {
            Concurrency.EnsureVersion(w, r.Version);
        }

        if (w.Host != r.Host.Trim() || w.SshPort != r.SshPort)
        {
            w.HostKeyFingerprint = null;
            w.HostKeyTrustedAt = null;
            w.LastConnectionTestSucceeded = null;
        }
        w.Host = r.Host.Trim();
        w.SshPort = r.SshPort;
        w.SshUser = r.SshUser.Trim();
        w.RclonePath = r.RclonePath.Trim();
        w.WorkDirectory = r.WorkDirectory.TrimEnd('/');
        if (keys.KeyFromFile)
        {
            // The key is a mounted secret file: never accept or keep a copy in PostgreSQL.
            if (!string.IsNullOrWhiteSpace(r.PrivateKey)) throw RequestValidationException.For("privateKey", "The execution host key is managed as a mounted secret file and can't be set here.");
            w.PrivateKeyCiphertext = string.Empty;
        }
        else if (!string.IsNullOrWhiteSpace(r.PrivateKey))
        {
            RequireCredentials();
            w.PrivateKeyCiphertext = secrets.Protect(r.PrivateKey.Trim());
        }
        audit.Record("settings.worker.updated", "WorkerNode", "1", details: new { w.Host, w.SshPort, w.SshUser, keyChanged = !string.IsNullOrWhiteSpace(r.PrivateKey) });
        await db.SaveChangesAsync(ct);
        return await GetWorkerAsync(ct);
    }

    public async Task<ConnectionTestResultDto> TestWorkerAsync(CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var w = await db.WorkerNodes.FirstOrDefaultAsync(ct) ?? throw new DomainRuleException("worker_not_configured", "Save the worker settings first.");
        var endpoint = new SshEndpoint(w.Host, w.SshPort, w.SshUser, keys.WorkerKey(w), w.HostKeyFingerprint);
        var (obs, _, error, stage) = await ssh.ProbeAsync(endpoint, null, ct);

        string message;
        var success = false;
        if (error is null && obs.Trusted)
        {
            // Verify rclone and the NFS mount point from the worker's perspective.
            var checks = new List<string>();
            await using var session = await ssh.ConnectWorkerAsync(endpoint, ct);
            var rclone = await session.RunAsync($"{Domain.Execution.ShellQuote.Quote(w.RclonePath)} version 2>&1 | head -n 1", TimeSpan.FromSeconds(20), ct);
            checks.Add(rclone.Succeeded && rclone.StdOut.Contains("rclone", StringComparison.OrdinalIgnoreCase) ? $"{rclone.StdOut.Trim()} found" : "rclone NOT found (install it on the worker; Backora never auto-installs software)");
            var storage = await db.StorageSettings.AsNoTracking().FirstOrDefaultAsync(ct);
            if (storage is not null && !string.IsNullOrEmpty(storage.NfsPath))
            {
                var nfs = await session.RunAsync($"test -d {Domain.Execution.ShellQuote.Quote(storage.NfsPath)} && test -w {Domain.Execution.ShellQuote.Quote(storage.NfsPath)} && echo ok", TimeSpan.FromSeconds(20), ct);
                checks.Add(nfs.StdOut.Contains("ok", StringComparison.Ordinal) ? $"NFS path {storage.NfsPath} writable" : $"NFS path {storage.NfsPath} missing or not writable");
            }
            var work = await session.RunAsync($"mkdir -p -m 700 {Domain.Execution.ShellQuote.Quote(w.WorkDirectory)} && test -w {Domain.Execution.ShellQuote.Quote(w.WorkDirectory)} && echo ok", TimeSpan.FromSeconds(20), ct);
            checks.Add(work.StdOut.Contains("ok", StringComparison.Ordinal) ? $"work directory {w.WorkDirectory} writable" : $"work directory {w.WorkDirectory} not writable");
            success = checks.All(c => !c.Contains("NOT", StringComparison.Ordinal) && !c.Contains("missing", StringComparison.Ordinal) && !c.Contains("not writable", StringComparison.Ordinal));
            message = (success ? "Worker reachable. " : "Worker reachable with problems: ") + string.Join("; ", checks);
        }
        else
        {
            message = error ?? "Worker host key is not trusted yet. Review and trust the fingerprint.";
        }

        w.LastConnectionTestAt = clock.UtcNow;
        w.LastConnectionTestSucceeded = success;
        w.LastConnectionTestMessage = message.Length > 2000 ? message[..2000] : message;
        audit.Record("settings.worker.tested", "WorkerNode", "1", success ? AuditOutcome.Success : AuditOutcome.Failure, new { stage, success });
        await db.SaveChangesAsync(ct);
        return new ConnectionTestResultDto(success, message, stage, obs.Fingerprint, obs.Trusted, null, false, sw.ElapsedMilliseconds);
    }

    public async Task<WorkerNodeDto> TrustWorkerHostKeyAsync(string fingerprint, CancellationToken ct)
    {
        if (!fingerprint.StartsWith("SHA256:", StringComparison.Ordinal)) throw RequestValidationException.For("fingerprint", "Expected an OpenSSH SHA256 fingerprint.");
        var w = await db.WorkerNodes.FirstOrDefaultAsync(ct) ?? throw new DomainRuleException("worker_not_configured", "Save the worker settings first.");
        var previous = w.HostKeyFingerprint;
        w.HostKeyFingerprint = fingerprint;
        w.HostKeyTrustedAt = clock.UtcNow;
        w.HostKeyTrustedByUserId = user.UserId;
        audit.Record("settings.worker.host_key_trusted", "WorkerNode", "1", details: new { fingerprint, previous });
        await db.SaveChangesAsync(ct);
        return await GetWorkerAsync(ct);
    }

    // ───────────── NFS ─────────────
    public async Task<NfsSettingsDto> GetNfsAsync(CancellationToken ct)
    {
        var s = await db.StorageSettings.AsNoTracking().FirstOrDefaultAsync(ct);
        return new NfsSettingsDto(s?.NfsHost ?? string.Empty, s?.NfsPath ?? string.Empty, s?.Version ?? 0);
    }

    public async Task<NfsSettingsDto> UpdateNfsAsync(NfsSettingsUpdateRequest r, CancellationToken ct)
    {
        var s = await db.StorageSettings.FirstOrDefaultAsync(ct);
        if (s is null) { s = new StorageSettings { Id = 1 }; db.StorageSettings.Add(s); }
        else Concurrency.EnsureVersion(s, r.Version);
        var old = new { s.NfsHost, s.NfsPath };
        s.NfsHost = r.NfsHost.Trim();
        s.NfsPath = "/" + r.NfsPath.Trim().Trim('/');
        audit.Record("settings.nfs.updated", "StorageSettings", "1", details: new { old, s.NfsHost, s.NfsPath });
        await db.SaveChangesAsync(ct);
        return await GetNfsAsync(ct);
    }

    // ───────────── Default SSH key ─────────────
    public async Task<SshSettingsDto> GetSshAsync(CancellationToken ct)
    {
        var s = await db.SshSettings.AsNoTracking().FirstOrDefaultAsync(ct);
        return new SshSettingsDto(s?.DefaultPrivateKeyCiphertext is not null, s?.DefaultPublicKey, PublicKeyFingerprint(s?.DefaultPublicKey), s?.UpdatedAt);
    }

    public async Task<SshSettingsDto> UpdateSshAsync(SshSettingsUpdateRequest r, CancellationToken ct)
    {
        RequireCredentials();
        var s = await db.SshSettings.FirstOrDefaultAsync(ct);
        if (s is null) { s = new SshSettings { Id = 1 }; db.SshSettings.Add(s); }
        s.DefaultPrivateKeyCiphertext = secrets.Protect(r.PrivateKey.Trim());
        s.DefaultPublicKey = string.IsNullOrWhiteSpace(r.PublicKey) ? null : r.PublicKey.Trim();
        audit.Record("settings.ssh.updated", "SshSettings", "1", details: new { fingerprint = PublicKeyFingerprint(s.DefaultPublicKey) });
        await db.SaveChangesAsync(ct);
        return await GetSshAsync(ct);
    }

    public static string? PublicKeyFingerprint(string? publicKey)
    {
        if (string.IsNullOrWhiteSpace(publicKey)) return null;
        var parts = publicKey.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2) return null;
        try
        {
            var blob = Convert.FromBase64String(parts[1]);
            return "SHA256:" + Convert.ToBase64String(SHA256.HashData(blob)).TrimEnd('=');
        }
        catch (FormatException) { return null; }
    }

    // ───────────── SMTP ─────────────
    public async Task<SmtpSettingsDto> GetSmtpAsync(CancellationToken ct)
    {
        var s = await db.SmtpSettings.AsNoTracking().FirstOrDefaultAsync(ct);
        return s is null
            ? new SmtpSettingsDto(false, string.Empty, 587, null, false, string.Empty, "Backora", "StartTls", null, DefaultTemplates.Email, false, 0)
            : new SmtpSettingsDto(true, s.Host, s.Port, s.Username, s.PasswordCiphertext is not null, s.FromAddress, s.FromName, s.Security, s.Template, DefaultTemplates.Email, s.Enabled, s.Version);
    }

    public async Task<SmtpSettingsDto> UpdateSmtpAsync(SmtpSettingsUpdateRequest r, CancellationToken ct)
    {
        var s = await db.SmtpSettings.FirstOrDefaultAsync(ct);
        if (s is null) { s = new SmtpSettings { Id = 1 }; db.SmtpSettings.Add(s); }
        else Concurrency.EnsureVersion(s, r.Version);
        s.Host = r.Host.Trim();
        s.Port = r.Port;
        s.Username = string.IsNullOrWhiteSpace(r.Username) ? null : r.Username.Trim();
        if (!string.IsNullOrEmpty(r.Password))
        {
            RequireCredentials();
            s.PasswordCiphertext = secrets.Protect(r.Password);
        }
        s.FromAddress = r.FromAddress.Trim();
        s.FromName = r.FromName?.Trim();
        s.Security = r.Security;
        s.Template = string.IsNullOrWhiteSpace(r.Template) ? null : r.Template;
        s.Enabled = r.Enabled;
        audit.Record("settings.smtp.updated", "SmtpSettings", "1", details: new { s.Host, s.Port, s.Username, s.FromAddress, s.Security, s.Enabled, passwordChanged = !string.IsNullOrEmpty(r.Password) });
        await db.SaveChangesAsync(ct);
        return await GetSmtpAsync(ct);
    }

    public TemplatePreviewResponse PreviewEmail(string? template) =>
        new(NotificationRenderer.RenderEmail(template, DefaultTemplates.SampleValues(platform.Value.PublicUrl)), true, null);

    public async Task<MessageResponse> SendTestEmailAsync(string? recipient, CancellationToken ct)
    {
        var s = await db.SmtpSettings.AsNoTracking().FirstOrDefaultAsync(ct) ?? throw new DomainRuleException("smtp_not_configured", "Save the SMTP settings first.");
        var to = recipient;
        if (string.IsNullOrWhiteSpace(to))
        {
            var n = await db.NotificationSettings.AsNoTracking().FirstOrDefaultAsync(ct);
            to = n?.Recipients.FirstOrDefault();
        }
        if (string.IsNullOrWhiteSpace(to) || !new System.ComponentModel.DataAnnotations.EmailAddressAttribute().IsValid(to))
        {
            throw RequestValidationException.For("recipient", "Enter a valid recipient email address.");
        }
        var html = NotificationRenderer.RenderEmail(s.Template, DefaultTemplates.SampleValues(platform.Value.PublicUrl));
        try
        {
            await email.SendAsync(s, s.PasswordCiphertext is { } p ? secrets.Unprotect(p) : null, [to], "[Backora] Test notification", html, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await audit.RecordNowAsync("settings.smtp.test", "SmtpSettings", "1", AuditOutcome.Failure, new { to, error = ex.Message }, ct);
            throw new DomainRuleException("smtp_send_failed", $"Sending failed: {ex.Message}");
        }
        audit.Record("settings.smtp.test", "SmtpSettings", "1", details: new { to });
        await db.SaveChangesAsync(ct);
        return new MessageResponse($"Test email sent to {to}.");
    }

    // ───────────── Teams ─────────────
    public async Task<TeamsSettingsDto> GetTeamsAsync(CancellationToken ct)
    {
        var s = await db.TeamsSettings.AsNoTracking().FirstOrDefaultAsync(ct);
        string? host = null;
        if (s?.WebhookUrlCiphertext is { } c && Uri.TryCreate(secrets.Unprotect(c), UriKind.Absolute, out var uri)) host = uri.Host;
        return new TeamsSettingsDto(s?.WebhookUrlCiphertext is not null, host, s?.Template, DefaultTemplates.Teams, s?.Enabled ?? false, s?.Version ?? 0);
    }

    public async Task<TeamsSettingsDto> UpdateTeamsAsync(TeamsSettingsUpdateRequest r, CancellationToken ct)
    {
        var s = await db.TeamsSettings.FirstOrDefaultAsync(ct);
        if (s is null) { s = new TeamsSettings { Id = 1 }; db.TeamsSettings.Add(s); }
        else Concurrency.EnsureVersion(s, r.Version);
        if (!string.IsNullOrWhiteSpace(r.WebhookUrl))
        {
            RequireCredentials();
            s.WebhookUrlCiphertext = secrets.Protect(r.WebhookUrl.Trim());
        }
        if (!string.IsNullOrWhiteSpace(r.Template))
        {
            var (_, error) = NotificationRenderer.RenderTeams(r.Template, DefaultTemplates.SampleValues(platform.Value.PublicUrl));
            if (error is not null) throw RequestValidationException.For("template", error);
        }
        s.Template = string.IsNullOrWhiteSpace(r.Template) ? null : r.Template;
        s.Enabled = r.Enabled;
        audit.Record("settings.teams.updated", "TeamsSettings", "1", details: new { s.Enabled, webhookChanged = !string.IsNullOrWhiteSpace(r.WebhookUrl) });
        await db.SaveChangesAsync(ct);
        return await GetTeamsAsync(ct);
    }

    public TemplatePreviewResponse PreviewTeams(string? template)
    {
        var (json, error) = NotificationRenderer.RenderTeams(template, DefaultTemplates.SampleValues(platform.Value.PublicUrl));
        return new TemplatePreviewResponse(json, error is null, error);
    }

    public async Task<MessageResponse> SendTestTeamsAsync(CancellationToken ct)
    {
        var s = await db.TeamsSettings.AsNoTracking().FirstOrDefaultAsync(ct);
        if (s?.WebhookUrlCiphertext is null) throw new DomainRuleException("teams_not_configured", "Save a Teams webhook URL first.");
        var (json, error) = NotificationRenderer.RenderTeams(s.Template, DefaultTemplates.SampleValues(platform.Value.PublicUrl));
        if (error is not null) throw new DomainRuleException("teams_template_invalid", error);
        try
        {
            await teams.SendAsync(secrets.Unprotect(s.WebhookUrlCiphertext), json, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await audit.RecordNowAsync("settings.teams.test", "TeamsSettings", "1", AuditOutcome.Failure, new { error = ex.Message }, ct);
            throw new DomainRuleException("teams_send_failed", $"Webhook call failed: {ex.Message}");
        }
        audit.Record("settings.teams.test", "TeamsSettings", "1");
        await db.SaveChangesAsync(ct);
        return new MessageResponse("Test card delivered to Microsoft Teams.");
    }

    // ───────────── Notification routing ─────────────
    public async Task<NotificationSettingsDto> GetNotificationsAsync(CancellationToken ct)
    {
        var s = await db.NotificationSettings.AsNoTracking().FirstOrDefaultAsync(ct) ?? new NotificationSettings();
        return new NotificationSettingsDto(s.Recipients, s.NotifyOnBackupFailure, s.NotifyOnOffloadFailure, s.NotifyOnRestoreFailure, s.NotifyOnRestoreCompleted, s.NotifyOnBackupSuccess, s.Version);
    }

    public async Task<NotificationSettingsDto> UpdateNotificationsAsync(NotificationSettingsUpdateRequest r, CancellationToken ct)
    {
        var s = await db.NotificationSettings.FirstOrDefaultAsync(ct);
        if (s is null) { s = new NotificationSettings { Id = 1 }; db.NotificationSettings.Add(s); }
        else Concurrency.EnsureVersion(s, r.Version);
        s.Recipients = r.Recipients.Select(x => x.Trim()).Where(x => x.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        s.NotifyOnBackupFailure = r.NotifyOnBackupFailure;
        s.NotifyOnOffloadFailure = r.NotifyOnOffloadFailure;
        s.NotifyOnRestoreFailure = r.NotifyOnRestoreFailure;
        s.NotifyOnRestoreCompleted = r.NotifyOnRestoreCompleted;
        s.NotifyOnBackupSuccess = r.NotifyOnBackupSuccess;
        audit.Record("settings.notifications.updated", "NotificationSettings", "1", details: new { recipients = s.Recipients.Count });
        await db.SaveChangesAsync(ct);
        return await GetNotificationsAsync(ct);
    }

    private void RequireCredentials()
    {
        if (!user.HasPermission(Permissions.CredentialsManage)) throw new ForbiddenException("Changing credentials requires the credentials.manage permission.");
    }

    internal static string Sha(string s) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(s)));
}
