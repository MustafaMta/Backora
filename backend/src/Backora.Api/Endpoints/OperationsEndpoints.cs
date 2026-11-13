using Backora.Application.Backups;
using Backora.Application.Insights;
using Backora.Contracts.Security;
using Backora.Contracts.V1;
using Backora.Domain;
using Backora.Infrastructure.Identity;
using Microsoft.AspNetCore.Mvc;

namespace Backora.Api.Endpoints;

public static class OperationsEndpoints
{
    public static void MapOperations(this IEndpointRouteBuilder app)
    {
        // ───────────── Backup jobs ─────────────
        var jobs = app.MapGroup("/api/v1/backup-jobs").WithTags("Backup jobs").RequireAuthorization();
        jobs.MapGet("", async (string? q, JobStatusDto? status, string? category, long? serverId, HealthState? health, int? page, int? pageSize, string? sort,
                BackupJobService s, CancellationToken ct) =>
            TypedResults.Ok(await s.ListAsync(q, status, category, serverId, health, EndpointExtensions.Page(page, pageSize, sort), ct))).RequirePermission(Permissions.JobsRead);
        jobs.MapGet("/categories", async (BackupJobService s, CancellationToken ct) => TypedResults.Ok(await s.CategoriesAsync(ct))).RequirePermission(Permissions.JobsRead);
        jobs.MapGet("/{id:long}", async (long id, BackupJobService s, CancellationToken ct) => TypedResults.Ok(await s.GetAsync(id, ct))).RequirePermission(Permissions.JobsRead);
        jobs.MapPost("", async (BackupJobUpsertRequest r, BackupJobService s, CancellationToken ct) =>
        {
            var job = await s.CreateAsync(r, ct);
            return TypedResults.Created($"/api/v1/backup-jobs/{job.Id}", job);
        }).Validate<BackupJobUpsertRequest>().RequirePermission(Permissions.JobsWrite);
        jobs.MapPut("/{id:long}", async (long id, BackupJobUpsertRequest r, BackupJobService s, CancellationToken ct) => TypedResults.Ok(await s.UpdateAsync(id, r, ct)))
            .Validate<BackupJobUpsertRequest>().RequirePermission(Permissions.JobsWrite);
        jobs.MapDelete("/{id:long}", async (long id, BackupJobService s, CancellationToken ct) => { await s.DeleteAsync(id, ct); return TypedResults.NoContent(); })
            .RequirePermission(Permissions.JobsWrite);
        jobs.MapPost("/{id:long}/status", async (long id, SetJobStatusRequest r, BackupJobService s, CancellationToken ct) => TypedResults.Ok(await s.SetStatusAsync(id, r.Status, ct)))
            .RequirePermission(Permissions.JobsWrite);
        jobs.MapPost("/{id:long}/runs", async (long id, [FromHeader(Name = ApiHeaders.IdempotencyKey)] string? key, BackupJobService s, CancellationToken ct) =>
        {
            var result = await s.RunNowAsync(id, Clean(key), RunTrigger.Manual, ct);
            return result.AlreadyExisted ? Results.Ok(result) : Results.Created($"/api/v1/runs/{result.RunId}", result);
        }).RequirePermission(Permissions.JobsRun).RequireRateLimiting(RateLimits.Sensitive);
        jobs.MapPost("/schedule-preview", (SchedulePreviewRequest r, Backora.Application.Abstractions.IClock clock) => TypedResults.Ok(BackupJobService.Preview(r, clock.UtcNow)))
            .Validate<SchedulePreviewRequest>().RequirePermission(Permissions.JobsRead);
        jobs.MapGet("/{id:long}/script-preview", async (long id, BackupJobService s, CancellationToken ct) => TypedResults.Ok(await s.ScriptPreviewAsync(id, ct)))
            .RequirePermission(Permissions.TemplatesRead);

        // ───────────── Runs ─────────────
        var runs = app.MapGroup("/api/v1/runs").WithTags("Runs").RequireAuthorization();
        runs.MapGet("", async (RunStatusDto? status, long? jobId, long? serverId, RunTriggerDto? trigger, DateTimeOffset? from, DateTimeOffset? to, string? q, bool? cleared,
                int? page, int? pageSize, string? sort, RunService s, CancellationToken ct) =>
            TypedResults.Ok(await s.ListAsync(new RunQuery(status, jobId, serverId, trigger, from, to, q, cleared == true), EndpointExtensions.Page(page, pageSize, sort), ct)))
            .RequirePermission(Permissions.RunsRead);
        runs.MapGet("/{id:long}", async (long id, RunService s, CancellationToken ct) => TypedResults.Ok(await s.GetAsync(id, ct))).RequirePermission(Permissions.RunsRead);
        runs.MapPost("/{id:long}/cancel", async (long id, RunService s, CancellationToken ct) => TypedResults.Ok(await s.CancelAsync(id, ct))).RequirePermission(Permissions.RunsCancel);
        runs.MapPost("/{id:long}/retry", async (long id, [FromHeader(Name = ApiHeaders.IdempotencyKey)] string? key, RunService s, CancellationToken ct) =>
            TypedResults.Ok(await s.RetryAsync(id, Clean(key), ct))).RequirePermission(Permissions.JobsRun);
        // Clear failed runs from the Failed list (they stay in the history): one, or every failed run not cleared yet.
        runs.MapPost("/{id:long}/dismiss", async (long id, RunService s, CancellationToken ct) => TypedResults.Ok(await s.DismissFailedAsync(id, ct))).RequirePermission(Permissions.JobsRun);
        runs.MapPost("/dismiss-failed", async (long? jobId, RunService s, CancellationToken ct) => TypedResults.Ok(await s.DismissFailedAsync(null, ct, jobId))).RequirePermission(Permissions.JobsRun);

        // ───────────── Offloads & recovery points ─────────────
        var offloads = app.MapGroup("/api/v1/offloads").WithTags("Offloads").RequireAuthorization();
        offloads.MapGet("", async (OffloadStatusDto? status, long? jobId, long? serverId, long? destinationId, DateTimeOffset? from, DateTimeOffset? to, string? q, bool? replica, bool? cleared,
                int? page, int? pageSize, string? sort, RecoveryService s, CancellationToken ct) =>
            TypedResults.Ok(await s.ListOffloadsAsync(new RecoveryPointQuery(jobId, serverId, destinationId, from, to, q, true, status, replica, Cleared: cleared == true), EndpointExtensions.Page(page, pageSize, sort), false, ct)))
            .RequirePermission(Permissions.RecoveryPointsRead);
        offloads.MapGet("/{id:long}", async (long id, RecoveryService s, CancellationToken ct) => TypedResults.Ok(await s.GetOffloadAsync(id, ct))).RequirePermission(Permissions.RecoveryPointsRead);
        offloads.MapPost("/{id:long}/retry", async (long id, RecoveryService s, CancellationToken ct) => TypedResults.Ok(await s.RetryOffloadAsync(id, ct))).RequirePermission(Permissions.JobsRun);
        // Clear failures from the Failed list (they stay in the history): one, or every failure still needing attention.
        offloads.MapPost("/{id:long}/dismiss", async (long id, RecoveryService s, CancellationToken ct) => TypedResults.Ok(await s.DismissFailedAsync(id, ct))).RequirePermission(Permissions.JobsRun);
        offloads.MapPost("/dismiss-failed", async (RecoveryService s, CancellationToken ct) => TypedResults.Ok(await s.DismissFailedAsync(null, ct))).RequirePermission(Permissions.JobsRun);

        var points = app.MapGroup("/api/v1/recovery-points").WithTags("Recovery points").RequireAuthorization();
        points.MapGet("", async (long? jobId, long? serverId, long? destinationId, DateTimeOffset? from, DateTimeOffset? to, string? q, bool? includeDeleted, bool? encrypted,
                int? page, int? pageSize, string? sort, RecoveryService s, CancellationToken ct) =>
            TypedResults.Ok(await s.ListOffloadsAsync(new RecoveryPointQuery(jobId, serverId, destinationId, from, to, q, includeDeleted ?? false, null, Encrypted: encrypted), EndpointExtensions.Page(page, pageSize, sort), true, ct)))
            .RequirePermission(Permissions.RecoveryPointsRead);
        points.MapGet("/servers", async (RecoveryService s, CancellationToken ct) => TypedResults.Ok(await s.ServerSummaryAsync(ct))).RequirePermission(Permissions.RecoveryPointsRead);
        points.MapPost("/{id:long}/protection", async (long id, ProtectRecoveryPointRequest r, RecoveryService s, CancellationToken ct) => TypedResults.Ok(await s.SetProtectionAsync(id, r, ct)))
            .Validate<ProtectRecoveryPointRequest>().RequirePermission(Permissions.RecoveryPointsProtect);
        points.MapPost("/{id:long}/verify-key", async (long id, VerifyDecryptionKeyRequest r, RecoveryService s, CancellationToken ct) => TypedResults.Ok(await s.VerifyDecryptionKeyAsync(id, r, ct)))
            .Validate<VerifyDecryptionKeyRequest>().RequirePermission(Permissions.RestoresExecute).RequireRateLimiting(RateLimits.Sensitive);
        points.MapPost("/{id:long}/replicate", async (long id, ReplicateRecoveryPointRequest r, RecoveryService s, CancellationToken ct) => TypedResults.Ok(await s.ReplicateAsync(id, r, ct)))
            .RequirePermission(Permissions.JobsRun);

        // ───────────── Restores ─────────────
        var restores = app.MapGroup("/api/v1/restores").WithTags("Restores").RequireAuthorization();
        restores.MapPost("/precheck", async (RestorePrecheckRequest r, RecoveryService s, CancellationToken ct) => TypedResults.Ok(await s.PrecheckAsync(r.RecoveryPointId, ct)))
            .RequirePermission(Permissions.RestoresExecute).RequireRateLimiting(RateLimits.Sensitive);
        restores.MapPost("", async (CreateRestoreRequest r, [FromHeader(Name = ApiHeaders.IdempotencyKey)] string? key, RecoveryService s, CancellationToken ct) =>
        {
            var created = await s.CreateRestoreAsync(r, Clean(key), ct);
            return TypedResults.Created($"/api/v1/restores/{created.Id}", created);
        }).Validate<CreateRestoreRequest>().RequirePermission(Permissions.RestoresExecute).RequireRateLimiting(RateLimits.Sensitive);
        restores.MapGet("", async (RestoreStatusDto? status, string? q, int? page, int? pageSize, RecoveryService s, CancellationToken ct) =>
            TypedResults.Ok(await s.ListRestoresAsync(status, q, EndpointExtensions.Page(page, pageSize, null), ct))).RequirePermission(Permissions.RestoresRead);
        restores.MapGet("/{id:long}", async (long id, RecoveryService s, CancellationToken ct) => TypedResults.Ok(await s.GetRestoreAsync(id, ct))).RequirePermission(Permissions.RestoresRead);
        restores.MapPost("/{id:long}/decryption-key", async (long id, SubmitDecryptionKeyRequest r, RecoveryService s, CancellationToken ct) =>
                TypedResults.Accepted($"/api/v1/restores/{id}", await s.SubmitDecryptionKeyAsync(id, r, ct)))
            .Validate<SubmitDecryptionKeyRequest>().RequirePermission(Permissions.RestoresExecute).RequireRateLimiting(RateLimits.Sensitive);
        restores.MapPost("/{id:long}/cancel", async (long id, RecoveryService s, CancellationToken ct) => TypedResults.Ok(await s.CancelRestoreAsync(id, ct))).RequirePermission(Permissions.RestoresExecute);

        // ───────────── Dashboard, audit, search, system ─────────────
        app.MapGet("/api/v1/dashboard", async (DateTimeOffset? from, DateTimeOffset? to, DashboardService s, CancellationToken ct) => TypedResults.Ok(await s.GetAsync(from, to, ct)))
            .WithTags("Dashboard").RequireAuthorization(Permissions.DashboardRead);
        app.MapGet("/api/v1/dashboard/alerts", async (DashboardService s, CancellationToken ct) => TypedResults.Ok(await s.AlertsAsync(ct)))
            .WithTags("Dashboard").RequireAuthorization(Permissions.DashboardRead);
        app.MapGet("/api/v1/audit", async (DateTimeOffset? from, DateTimeOffset? to, string? action, string? actor, string? resourceType, AuditOutcomeDto? outcome,
                int? page, int? pageSize, string? sort, AuditQueryService s, CancellationToken ct) =>
            TypedResults.Ok(await s.ListAsync(new AuditQuery(from, to, action, actor, resourceType, outcome), EndpointExtensions.Page(page, pageSize, sort), ct)))
            .WithTags("Audit").RequireAuthorization(Permissions.AuditRead);
        app.MapGet("/api/v1/search", async (string? q, SearchService s, CancellationToken ct) => TypedResults.Ok(await s.SearchAsync(q, ct)))
            .WithTags("Search").RequireAuthorization();
        app.MapGet("/api/v1/system/info", async (SystemService s, IWebHostEnvironment env, CancellationToken ct) => TypedResults.Ok(await s.InfoAsync(env.EnvironmentName, ct)))
            .WithTags("System").RequireAuthorization(Permissions.DashboardRead);

        // ───────────── Users & roles ─────────────
        var users = app.MapGroup("/api/v1/users").WithTags("Users").RequireAuthorization();
        users.MapGet("", async (string? q, int? page, int? pageSize, string? sort, UserAdminService s, CancellationToken ct) =>
            TypedResults.Ok(await s.ListAsync(q, EndpointExtensions.Page(page, pageSize, sort), ct))).RequirePermission(Permissions.UsersRead);
        users.MapGet("/{id:long}", async (long id, UserAdminService s, CancellationToken ct) => TypedResults.Ok(await s.GetAsync(id, ct))).RequirePermission(Permissions.UsersRead);
        users.MapPost("", async (CreateUserRequest r, UserAdminService s, CancellationToken ct) =>
        {
            var u = await s.CreateAsync(r, ct);
            return TypedResults.Created($"/api/v1/users/{u.Id}", u);
        }).Validate<CreateUserRequest>().RequirePermission(Permissions.UsersManage);
        users.MapPut("/{id:long}", async (long id, UpdateUserRequest r, UserAdminService s, CancellationToken ct) => TypedResults.Ok(await s.UpdateAsync(id, r, ct)))
            .Validate<UpdateUserRequest>().RequirePermission(Permissions.UsersManage);
        users.MapPost("/{id:long}/password", async (long id, SetUserPasswordRequest r, UserAdminService s, CancellationToken ct) => { await s.SetPasswordAsync(id, r, ct); return TypedResults.NoContent(); })
            .RequirePermission(Permissions.UsersManage);
        users.MapPost("/{id:long}/unlock", async (long id, UserAdminService s, CancellationToken ct) => { await s.UnlockAsync(id, ct); return TypedResults.NoContent(); })
            .RequirePermission(Permissions.UsersManage);
        app.MapGet("/api/v1/roles", async (UserAdminService s, CancellationToken ct) => TypedResults.Ok(await s.RolesAsync(ct)))
            .WithTags("Users").RequireAuthorization(Permissions.UsersRead);
    }

    /// <summary>Idempotency keys are bounded and restricted to a safe alphabet.</summary>
    private static string? Clean(string? key) =>
        string.IsNullOrWhiteSpace(key) ? null : key.Length <= 200 && key.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or ':' or '.') ? key : throw new Application.Common.RequestValidationException(
            new Dictionary<string, string[]> { [ApiHeaders.IdempotencyKey] = ["Idempotency-Key must be ≤200 characters of [A-Za-z0-9-_:.]."] });
}
