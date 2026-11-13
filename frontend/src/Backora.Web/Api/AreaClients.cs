using Backora.Contracts.V1;
using Backora.Contracts.V1.Auth;

namespace Backora.Web.Api;

// Strongly typed clients per API area. Paths mirror backend/docs/04-api-reference.md exactly.

public sealed class MeClient(ApiClient api)
{
    public Task<ApiResult<CurrentUserDto>> GetAsync(CancellationToken ct = default) => api.GetAsync<CurrentUserDto>("/me", ct);
    public Task<ApiResult> UpdateProfileAsync(UpdateProfileRequest r) => api.PutAsync("/me/profile", r);
    public Task<ApiResult> ChangePasswordAsync(ChangePasswordRequest r) => api.PostAsync("/me/password", r);
    public Task<ApiResult<TwoFactorSetupResponse>> SetupTwoFactorAsync() => api.PostAsync<TwoFactorSetupResponse>("/me/2fa/setup", new { });
    public Task<ApiResult<TwoFactorEnableResponse>> EnableTwoFactorAsync(TwoFactorEnableRequest r) => api.PostAsync<TwoFactorEnableResponse>("/me/2fa/enable", r);
    public Task<ApiResult> DisableTwoFactorAsync(TwoFactorDisableRequest r) => api.PostAsync("/me/2fa/disable", r);
    public Task<ApiResult<List<PersonalAccessTokenDto>>> TokensAsync() => api.GetAsync<List<PersonalAccessTokenDto>>("/me/tokens");
    public Task<ApiResult<CreatedPersonalAccessTokenResponse>> CreateTokenAsync(CreatePersonalAccessTokenRequest r) => api.PostAsync<CreatedPersonalAccessTokenResponse>("/me/tokens", r);
    public Task<ApiResult> RevokeTokenAsync(long id) => api.DeleteAsync($"/me/tokens/{id}");
    public Task<ApiResult<List<SessionDto>>> SessionsAsync() => api.GetAsync<List<SessionDto>>("/me/sessions");
    public Task<ApiResult> RevokeSessionAsync(long id) => api.DeleteAsync($"/me/sessions/{id}");
}

public sealed class UsersClient(ApiClient api)
{
    public Task<ApiResult<PagedResult<UserDto>>> ListAsync(string? q, int page, int pageSize, string? sort = null) =>
        api.GetAsync<PagedResult<UserDto>>(new Query().Add("q", q).Add("page", page).Add("pageSize", pageSize).Add("sort", sort).Build("/users"));
    public Task<ApiResult<UserDto>> GetAsync(long id) => api.GetAsync<UserDto>($"/users/{id}");
    public Task<ApiResult<UserDto>> CreateAsync(CreateUserRequest r) => api.PostAsync<UserDto>("/users", r);
    public Task<ApiResult<UserDto>> UpdateAsync(long id, UpdateUserRequest r) => api.PutAsync<UserDto>($"/users/{id}", r);
    public Task<ApiResult> SetPasswordAsync(long id, SetUserPasswordRequest r) => api.PostAsync($"/users/{id}/password", r);
    public Task<ApiResult> UnlockAsync(long id) => api.PostAsync($"/users/{id}/unlock", new { });
    public Task<ApiResult<List<RoleDto>>> RolesAsync() => api.GetAsync<List<RoleDto>>("/roles");
}

public sealed class ServersClient(ApiClient api)
{
    public Task<ApiResult<PagedResult<ServerDto>>> ListAsync(string? q, int page, int pageSize, string? sort) =>
        api.GetAsync<PagedResult<ServerDto>>(new Query().Add("q", q).Add("page", page).Add("pageSize", pageSize).Add("sort", sort).Build("/servers"));
    public Task<ApiResult<ServerDto>> GetAsync(long id) => api.GetAsync<ServerDto>($"/servers/{id}");
    public Task<ApiResult<ServerDto>> CreateAsync(ServerUpsertRequest r) => api.PostAsync<ServerDto>("/servers", r);
    public Task<ApiResult<ServerDto>> UpdateAsync(long id, ServerUpsertRequest r) => api.PutAsync<ServerDto>($"/servers/{id}", r);
    public Task<ApiResult> DeleteAsync(long id) => api.DeleteAsync($"/servers/{id}");
    public Task<ApiResult<ConnectionTestResultDto>> TestAsync(long id) => api.PostAsync<ConnectionTestResultDto>($"/servers/{id}/test", new { });
    public Task<ApiResult<ConnectionTestResultDto>> TestAdHocAsync(ConnectionTestRequest r) => api.PostAsync<ConnectionTestResultDto>("/servers/test", r);
    public Task<ApiResult> TrustHostKeyAsync(long id, string fingerprint) => api.PostAsync($"/servers/{id}/trust-host-key", new TrustHostKeyRequest(fingerprint));
}

public sealed class SettingsClient(ApiClient api)
{
    public Task<ApiResult<WorkerNodeDto>> WorkerAsync() => api.GetAsync<WorkerNodeDto>("/settings/worker");
    public Task<ApiResult<WorkerNodeDto>> UpdateWorkerAsync(WorkerNodeUpdateRequest r) => api.PutAsync<WorkerNodeDto>("/settings/worker", r);
    public Task<ApiResult<ConnectionTestResultDto>> TestWorkerAsync() => api.PostAsync<ConnectionTestResultDto>("/settings/worker/test", new { });
    public Task<ApiResult> TrustWorkerHostKeyAsync(string fingerprint) => api.PostAsync("/settings/worker/trust-host-key", new TrustHostKeyRequest(fingerprint));
    /// <summary>Execution host status: node settings, last capability probe, active work and the targets reached through it.</summary>
    public Task<ApiResult<ExecutionHostDto>> ExecutionHostAsync(CancellationToken ct = default) => api.GetAsync<ExecutionHostDto>("/execution-host", ct);
    /// <summary>Runs Backora's fixed, read-only capability probe on the execution host.</summary>
    public Task<ApiResult<ExecutionHostProbeDto>> ProbeExecutionHostAsync() => api.PostAsync<ExecutionHostProbeDto>("/execution-host/probe", new { });

    public Task<ApiResult<NfsSettingsDto>> NfsAsync() => api.GetAsync<NfsSettingsDto>("/settings/nfs");
    public Task<ApiResult<NfsSettingsDto>> UpdateNfsAsync(NfsSettingsUpdateRequest r) => api.PutAsync<NfsSettingsDto>("/settings/nfs", r);

    public Task<ApiResult<SshSettingsDto>> SshAsync() => api.GetAsync<SshSettingsDto>("/settings/ssh");
    public Task<ApiResult<SshSettingsDto>> UpdateSshAsync(SshSettingsUpdateRequest r) => api.PutAsync<SshSettingsDto>("/settings/ssh", r);

    public Task<ApiResult<SmtpSettingsDto>> SmtpAsync() => api.GetAsync<SmtpSettingsDto>("/settings/smtp");
    public Task<ApiResult<SmtpSettingsDto>> UpdateSmtpAsync(SmtpSettingsUpdateRequest r) => api.PutAsync<SmtpSettingsDto>("/settings/smtp", r);
    public Task<ApiResult<TemplatePreviewResponse>> PreviewSmtpAsync(TemplatePreviewRequest r) => api.PostAsync<TemplatePreviewResponse>("/settings/smtp/preview", r);
    public Task<ApiResult<MessageResponse>> TestSmtpAsync(SendTestNotificationRequest r) => api.PostAsync<MessageResponse>("/settings/smtp/test", r);

    public Task<ApiResult<TeamsSettingsDto>> TeamsAsync() => api.GetAsync<TeamsSettingsDto>("/settings/teams");
    public Task<ApiResult<TeamsSettingsDto>> UpdateTeamsAsync(TeamsSettingsUpdateRequest r) => api.PutAsync<TeamsSettingsDto>("/settings/teams", r);
    public Task<ApiResult<TemplatePreviewResponse>> PreviewTeamsAsync(TemplatePreviewRequest r) => api.PostAsync<TemplatePreviewResponse>("/settings/teams/preview", r);
    public Task<ApiResult<MessageResponse>> TestTeamsAsync() => api.PostAsync<MessageResponse>("/settings/teams/test", new SendTestNotificationRequest(null));

    public Task<ApiResult<NotificationSettingsDto>> NotificationsAsync() => api.GetAsync<NotificationSettingsDto>("/settings/notifications");
    public Task<ApiResult<NotificationSettingsDto>> UpdateNotificationsAsync(NotificationSettingsUpdateRequest r) => api.PutAsync<NotificationSettingsDto>("/settings/notifications", r);
}

public sealed class VariablesClient(ApiClient api)
{
    public Task<ApiResult<List<VariableDto>>> ListAsync() => api.GetAsync<List<VariableDto>>("/variables");
    public Task<ApiResult<VariableDto>> CreateAsync(VariableUpsertRequest r) => api.PostAsync<VariableDto>("/variables", r);
    public Task<ApiResult<VariableDto>> UpdateAsync(long id, VariableUpsertRequest r) => api.PutAsync<VariableDto>($"/variables/{id}", r);
    public Task<ApiResult> DeleteAsync(long id) => api.DeleteAsync($"/variables/{id}");
}

public sealed class TemplatesClient(ApiClient api)
{
    public Task<ApiResult<List<TemplateSummaryDto>>> ListAsync(string? q = null, string? category = null) =>
        api.GetAsync<List<TemplateSummaryDto>>(new Query().Add("q", q).Add("category", category).Build("/templates"));
    public Task<ApiResult<List<string>>> CategoriesAsync() => api.GetAsync<List<string>>("/templates/categories");
    public Task<ApiResult<TemplateDto>> GetAsync(long id) => api.GetAsync<TemplateDto>($"/templates/{id}");
    public Task<ApiResult<TemplateDto>> CreateAsync(TemplateUpsertRequest r) => api.PostAsync<TemplateDto>("/templates", r);
    public Task<ApiResult<TemplateDto>> UpdateAsync(long id, TemplateUpsertRequest r) => api.PutAsync<TemplateDto>($"/templates/{id}", r);
    public Task<ApiResult> DeleteAsync(long id) => api.DeleteAsync($"/templates/{id}");
}

public sealed class StorageClient(ApiClient api)
{
    public Task<ApiResult<List<S3DestinationDto>>> ListAsync() => api.GetAsync<List<S3DestinationDto>>("/storage/s3");
    public Task<ApiResult<S3DestinationDto>> GetAsync(long id) => api.GetAsync<S3DestinationDto>($"/storage/s3/{id}");
    public Task<ApiResult<S3DestinationDto>> CreateAsync(S3DestinationUpsertRequest r) => api.PostAsync<S3DestinationDto>("/storage/s3", r);
    public Task<ApiResult<S3DestinationDto>> UpdateAsync(long id, S3DestinationUpsertRequest r) => api.PutAsync<S3DestinationDto>($"/storage/s3/{id}", r);
    public Task<ApiResult> DeleteAsync(long id) => api.DeleteAsync($"/storage/s3/{id}");
    public Task<ApiResult<S3TestResultDto>> TestAsync(S3TestRequest r) => api.PostAsync<S3TestResultDto>("/storage/s3/test", r);
    /// <summary>Every job that copies to site <paramref name="id"/> copies to <paramref name="toId"/> instead.</summary>
    public Task<ApiResult<MoveDrJobsResponse>> MoveDrJobsAsync(long id, long toId) => api.PostAsync<MoveDrJobsResponse>($"/storage/s3/{id}/move-dr-jobs", new MoveDrJobsRequest(toId));
    /// <summary>Lists a site's real contents: one folder level, or (with a term or dates) a search across the folder tree.</summary>
    public Task<ApiResult<S3BrowseResponse>> BrowseAsync(long id, string? prefix, string? q, DateTimeOffset? from, DateTimeOffset? to, string? token, CancellationToken ct = default)
    {
        var qs = new List<string>();
        if (!string.IsNullOrEmpty(prefix)) qs.Add("prefix=" + Uri.EscapeDataString(prefix));
        if (!string.IsNullOrWhiteSpace(q)) qs.Add("q=" + Uri.EscapeDataString(q.Trim()));
        if (from is { } f) qs.Add("from=" + Uri.EscapeDataString(f.ToString("o", System.Globalization.CultureInfo.InvariantCulture)));
        if (to is { } t) qs.Add("to=" + Uri.EscapeDataString(t.ToString("o", System.Globalization.CultureInfo.InvariantCulture)));
        if (!string.IsNullOrEmpty(token)) qs.Add("token=" + Uri.EscapeDataString(token));
        return api.GetAsync<S3BrowseResponse>($"/storage/s3/{id}/objects" + (qs.Count > 0 ? "?" + string.Join('&', qs) : ""), ct);
    }
    /// <summary>Validates an age recipient ("age1…") and returns its fingerprint. Nothing is saved.</summary>
    public Task<ApiResult<RecipientInspectionDto>> InspectRecipientAsync(string recipient, CancellationToken ct = default) =>
        api.PostAsync<RecipientInspectionDto>("/storage/s3/encryption/inspect", new InspectRecipientRequest(recipient), null, ct);
    /// <summary>Generates a key pair once. The private key is returned only in this response and is never stored.</summary>
    public Task<ApiResult<GeneratedKeyPairDto>> GenerateKeyPairAsync() =>
        api.PostAsync<GeneratedKeyPairDto>("/storage/s3/encryption/generate", new { });
}

public sealed record JobFilter(string? Q = null, JobStatusDto? Status = null, string? Category = null, long? ServerId = null, HealthState? Health = null);

public sealed class BackupJobsClient(ApiClient api)
{
    public Task<ApiResult<PagedResult<BackupJobDto>>> ListAsync(JobFilter f, int page, int pageSize, string? sort) =>
        api.GetAsync<PagedResult<BackupJobDto>>(new Query().Add("q", f.Q).Add("status", f.Status).Add("category", f.Category)
            .Add("serverId", f.ServerId).Add("health", f.Health).Add("page", page).Add("pageSize", pageSize).Add("sort", sort).Build("/backup-jobs"));
    public Task<ApiResult<BackupJobDto>> GetAsync(long id) => api.GetAsync<BackupJobDto>($"/backup-jobs/{id}");
    public Task<ApiResult<List<string>>> CategoriesAsync() => api.GetAsync<List<string>>("/backup-jobs/categories");
    public Task<ApiResult<BackupJobDto>> CreateAsync(BackupJobUpsertRequest r) => api.PostAsync<BackupJobDto>("/backup-jobs", r);
    public Task<ApiResult<BackupJobDto>> UpdateAsync(long id, BackupJobUpsertRequest r) => api.PutAsync<BackupJobDto>($"/backup-jobs/{id}", r);
    public Task<ApiResult> DeleteAsync(long id) => api.DeleteAsync($"/backup-jobs/{id}");
    public Task<ApiResult> SetStatusAsync(long id, JobStatusDto status) => api.PostAsync($"/backup-jobs/{id}/status", new SetJobStatusRequest(status));
    public Task<ApiResult<RunCreatedResponse>> RunNowAsync(long id, string idempotencyKey) => api.PostAsync<RunCreatedResponse>($"/backup-jobs/{id}/runs", new { }, idempotencyKey);
    public Task<ApiResult<SchedulePreviewResponse>> PreviewScheduleAsync(SchedulePreviewRequest r, CancellationToken ct = default) =>
        api.PostAsync<SchedulePreviewResponse>("/backup-jobs/schedule-preview", r, null, ct);
    public Task<ApiResult<ScriptPreviewResponse>> ScriptPreviewAsync(long id) => api.GetAsync<ScriptPreviewResponse>($"/backup-jobs/{id}/script-preview");
}

public sealed record RunFilter(
    RunStatusDto? Status = null, long? JobId = null, long? ServerId = null, RunTriggerDto? Trigger = null,
    DateTimeOffset? From = null, DateTimeOffset? To = null, string? Q = null, bool Cleared = false);

public sealed class RunsClient(ApiClient api)
{
    public Task<ApiResult<PagedResult<RunSummaryDto>>> ListAsync(RunFilter f, int page, int pageSize, string? sort, CancellationToken ct = default) =>
        api.GetAsync<PagedResult<RunSummaryDto>>(new Query().Add("status", f.Status).Add("jobId", f.JobId).Add("serverId", f.ServerId)
            .Add("trigger", f.Trigger).Add("from", f.From).Add("to", f.To).Add("q", f.Q).Add("cleared", f.Cleared ? true : null)
            .Add("page", page).Add("pageSize", pageSize).Add("sort", sort).Build("/runs"), ct);
    public Task<ApiResult<RunDetailDto>> GetAsync(long id, CancellationToken ct = default) => api.GetAsync<RunDetailDto>($"/runs/{id}", ct);
    public Task<ApiResult> CancelAsync(long id) => api.PostAsync($"/runs/{id}/cancel", new { });
    public Task<ApiResult<RunCreatedResponse>> RetryAsync(long id) => api.PostAsync<RunCreatedResponse>($"/runs/{id}/retry", new { }, Guid.NewGuid().ToString());
    public Task<ApiResult<DismissedResponse>> DismissAsync(long id) => api.PostAsync<DismissedResponse>($"/runs/{id}/dismiss", new { });
    public Task<ApiResult<DismissedResponse>> DismissAllFailedAsync(long? jobId = null) =>
        api.PostAsync<DismissedResponse>(jobId is { } j ? $"/runs/dismiss-failed?jobId={j}" : "/runs/dismiss-failed", new { });
}

public sealed class OffloadsClient(ApiClient api)
{
    public Task<ApiResult<PagedResult<OffloadSummaryDto>>> ListAsync(OffloadStatusDto? status, long? jobId, int page, int pageSize, bool? replica = null, long? destinationId = null, string? q = null, bool cleared = false) =>
        api.GetAsync<PagedResult<OffloadSummaryDto>>(new Query().Add("status", status).Add("jobId", jobId).Add("replica", replica).Add("destinationId", destinationId).Add("q", q).Add("cleared", cleared ? true : null)
            .Add("page", page).Add("pageSize", pageSize).Build("/offloads"));
    public Task<ApiResult<OffloadDetailDto>> GetAsync(long id) => api.GetAsync<OffloadDetailDto>($"/offloads/{id}");
    public Task<ApiResult<IdResponse>> RetryAsync(long id) => api.PostAsync<IdResponse>($"/offloads/{id}/retry", new { });
    public Task<ApiResult<DismissedResponse>> DismissAsync(long id) => api.PostAsync<DismissedResponse>($"/offloads/{id}/dismiss", new { });
    public Task<ApiResult<DismissedResponse>> DismissAllFailedAsync() => api.PostAsync<DismissedResponse>("/offloads/dismiss-failed", new { });
}

public sealed record RecoveryPointFilter(
    long? JobId = null, long? ServerId = null, long? DestinationId = null, DateTimeOffset? From = null, DateTimeOffset? To = null,
    string? Q = null, bool IncludeDeleted = false);

public sealed class RecoveryPointsClient(ApiClient api)
{
    public Task<ApiResult<PagedResult<OffloadSummaryDto>>> ListAsync(RecoveryPointFilter f, int page, int pageSize, string? sort = null) =>
        api.GetAsync<PagedResult<OffloadSummaryDto>>(new Query().Add("jobId", f.JobId).Add("serverId", f.ServerId).Add("destinationId", f.DestinationId)
            .Add("from", f.From).Add("to", f.To).Add("q", f.Q).Add("includeDeleted", f.IncludeDeleted ? true : null)
            .Add("page", page).Add("pageSize", pageSize).Add("sort", sort).Build("/recovery-points"));
    public Task<ApiResult<List<ServerBackupSummaryDto>>> ServersAsync() => api.GetAsync<List<ServerBackupSummaryDto>>("/recovery-points/servers");
    public Task<ApiResult> ProtectAsync(long id, ProtectRecoveryPointRequest r) => api.PostAsync($"/recovery-points/{id}/protection", r);
    public Task<ApiResult<IdResponse>> ReplicateAsync(long id, long destinationId) => api.PostAsync<IdResponse>($"/recovery-points/{id}/replicate", new ReplicateRecoveryPointRequest(destinationId));
    /// <summary>Checks a private key against the recovery point's recorded recipient. The key is not stored.</summary>
    public Task<ApiResult<KeyVerificationDto>> VerifyKeyAsync(long id, string privateKey) =>
        api.PostAsync<KeyVerificationDto>($"/recovery-points/{id}/verify-key", new VerifyDecryptionKeyRequest(privateKey));
}

public sealed class RestoresClient(ApiClient api)
{
    public Task<ApiResult<RestorePrecheckResponse>> PrecheckAsync(long recoveryPointId) =>
        api.PostAsync<RestorePrecheckResponse>("/restores/precheck", new RestorePrecheckRequest(recoveryPointId));
    public Task<ApiResult<RestoreSummaryDto>> CreateAsync(CreateRestoreRequest r, string idempotencyKey) =>
        api.PostAsync<RestoreSummaryDto>("/restores", r, idempotencyKey);
    public Task<ApiResult<PagedResult<RestoreSummaryDto>>> ListAsync(RestoreStatusDto? status, string? q, int page, int pageSize) =>
        api.GetAsync<PagedResult<RestoreSummaryDto>>(new Query().Add("status", status).Add("q", q).Add("page", page).Add("pageSize", pageSize).Build("/restores"));
    public Task<ApiResult<RestoreDetailDto>> GetAsync(long id, CancellationToken ct = default) => api.GetAsync<RestoreDetailDto>($"/restores/{id}", ct);
    public Task<ApiResult> CancelAsync(long id) => api.PostAsync($"/restores/{id}/cancel", new { });
    /// <summary>Supplies the private key to a restore that is waiting for it. Held in memory only; never stored.</summary>
    public Task<ApiResult<RestoreSummaryDto>> SubmitKeyAsync(long id, string privateKey) =>
        api.PostAsync<RestoreSummaryDto>($"/restores/{id}/decryption-key", new SubmitDecryptionKeyRequest(privateKey));
}

public sealed class DashboardClient(ApiClient api)
{
    public Task<ApiResult<DashboardDto>> GetAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default) =>
        api.GetAsync<DashboardDto>(new Query().Add("from", from).Add("to", to).Build("/dashboard"), ct);
    public Task<ApiResult<List<AlertDto>>> AlertsAsync(CancellationToken ct = default) => api.GetAsync<List<AlertDto>>("/dashboard/alerts", ct);
}

public sealed record AuditFilter(DateTimeOffset? From = null, DateTimeOffset? To = null, string? Action = null, string? Actor = null, string? ResourceType = null, AuditOutcomeDto? Outcome = null);

public sealed class AuditClient(ApiClient api)
{
    public Task<ApiResult<PagedResult<AuditEventDto>>> ListAsync(AuditFilter f, int page, int pageSize) =>
        api.GetAsync<PagedResult<AuditEventDto>>(new Query().Add("from", f.From).Add("to", f.To).Add("action", f.Action).Add("actor", f.Actor)
            .Add("resourceType", f.ResourceType).Add("outcome", f.Outcome).Add("page", page).Add("pageSize", pageSize).Build("/audit"));
}

public sealed class SearchClient(ApiClient api)
{
    public Task<ApiResult<List<SearchResultDto>>> SearchAsync(string q, CancellationToken ct = default) =>
        api.GetAsync<List<SearchResultDto>>(new Query().Add("q", q).Build("/search"), ct);
}

public sealed class SystemClient(ApiClient api)
{
    public Task<ApiResult<SystemInfoDto>> InfoAsync() => api.GetAsync<SystemInfoDto>("/system/info");
}

/// <summary>Facade grouping all area clients for convenient injection in pages.</summary>
public sealed class BackoraApi(
    MeClient me, UsersClient users, ServersClient servers, SettingsClient settings, VariablesClient variables,
    TemplatesClient templates, StorageClient storage, BackupJobsClient jobs, RunsClient runs, OffloadsClient offloads,
    RecoveryPointsClient recoveryPoints, RestoresClient restores, DashboardClient dashboard, AuditClient audit,
    SearchClient search, SystemClient system)
{
    public MeClient Me { get; } = me;
    public UsersClient Users { get; } = users;
    public ServersClient Servers { get; } = servers;
    public SettingsClient Settings { get; } = settings;
    public VariablesClient Variables { get; } = variables;
    public TemplatesClient Templates { get; } = templates;
    public StorageClient Storage { get; } = storage;
    public BackupJobsClient Jobs { get; } = jobs;
    public RunsClient Runs { get; } = runs;
    public OffloadsClient Offloads { get; } = offloads;
    public RecoveryPointsClient RecoveryPoints { get; } = recoveryPoints;
    public RestoresClient Restores { get; } = restores;
    public DashboardClient Dashboard { get; } = dashboard;
    public AuditClient Audit { get; } = audit;
    public SearchClient Search { get; } = search;
    public SystemClient System { get; } = system;
}
