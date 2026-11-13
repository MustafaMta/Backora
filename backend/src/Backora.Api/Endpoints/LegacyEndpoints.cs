using System.Text.Json.Serialization;
using Backora.Api.Security;
using Backora.Application.Abstractions;
using Backora.Application.Backups;
using Backora.Application.Inventory;
using Backora.Application.Validation;
using Backora.Contracts.Security;
using Backora.Contracts.V1;
using Backora.Domain;
using Backora.Infrastructure.Identity;
using Backora.Infrastructure.Persistence;
using FluentValidation;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Backora.Api.Endpoints;

/// <summary>
/// Backwards-compatible automation API (paths and payloads from the Laravel <c>routes/api.php</c> and
/// <c>API_CURL.md</c>). Requests are translated to the v1 use cases, so validation, authorization and
/// auditing are identical.
/// </summary>
public static class LegacyEndpoints
{
    public sealed record TokenRequest(
        [property: JsonPropertyName("email")] string? Email,
        [property: JsonPropertyName("password")] string? Password,
        [property: JsonPropertyName("token_name")] string? TokenName,
        [property: JsonPropertyName("expires_in_minutes")] int? ExpiresInMinutes);

    public sealed record ServerRequest(
        [property: JsonPropertyName("server_name")] string? ServerName,
        [property: JsonPropertyName("server_ip")] string? ServerIp,
        [property: JsonPropertyName("ssh_user")] string? SshUser,
        [property: JsonPropertyName("ssh_port")] int? SshPort,
        [property: JsonPropertyName("private_key")] string? PrivateKey);

    public sealed record JobRequest(
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("category")] string? Category,
        [property: JsonPropertyName("script_name")] string? ScriptName,
        [property: JsonPropertyName("status")] string? Status,
        [property: JsonPropertyName("schedule_at")] string? ScheduleAt,
        [property: JsonPropertyName("server_id")] long? ServerId,
        [property: JsonPropertyName("retention_days")] decimal? RetentionDays,
        [property: JsonPropertyName("s3_config_id")] long? S3ConfigId);

    public static void MapLegacy(this IEndpointRouteBuilder app)
    {
        var legacy = app.MapGroup("/api").WithTags("Legacy compatibility");

        legacy.MapPost("/token", async (TokenRequest r, UserManager<ApplicationUser> users, UserAdminService tokens, IAuditLog audit, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(r.Email) || string.IsNullOrWhiteSpace(r.Password))
                return Results.Json(new { success = false, message = "The email and password fields are required." }, statusCode: 422);
            if (r.ExpiresInMinutes is < 1 or > 525600)
                return Results.Json(new { success = false, message = "expires_in_minutes must be between 1 and 525600." }, statusCode: 422);

            var user = await users.FindByEmailAsync(r.Email.Trim());
            if (user is null || !user.IsActive || await users.IsLockedOutAsync(user) || !await users.CheckPasswordAsync(user, r.Password))
            {
                if (user is not null && user.IsActive) await users.AccessFailedAsync(user);
                await audit.RecordNowAsync("auth.legacy_token", "User", user?.Id.ToString(), AuditOutcome.Failure, null, ct);
                return Results.Json(new { success = false, message = "Invalid email or password." }, statusCode: 401);
            }
            if (user.TwoFactorEnabled)
            {
                return Results.Json(new { success = false, message = "This account uses two-factor authentication. Create a personal access token in Backora (Account → API tokens)." }, statusCode: 403);
            }
            await users.ResetAccessFailedCountAsync(user);

            // Legacy semantics: token lifetime in minutes (default 60).
            var lifetime = TimeSpan.FromMinutes(r.ExpiresInMinutes ?? 60);
            var created = await tokens.CreateTokenAsync(user.Id, string.IsNullOrWhiteSpace(r.TokenName) ? "curl" : r.TokenName!, null, lifetime, ct);
            await audit.RecordNowAsync("auth.legacy_token", "User", user.Id.ToString(), AuditOutcome.Success, new { user.Email, created.Details.Prefix, created.Details.ExpiresAt }, ct);
            return Results.Json(new
            {
                success = true,
                token_type = "Bearer",
                access_token = created.Token,
                expires_at = created.Details.ExpiresAt,
            });
        }).RequireRateLimiting(RateLimits.Login).AllowAnonymous();

        legacy.MapDelete("/token", async (HttpContext http, BackoraDbContext db, IClock clock, CancellationToken ct) =>
        {
            var tokenId = http.User.FindFirst("token_id")?.Value;
            if (tokenId is null || !long.TryParse(tokenId, out var id))
                return Results.Json(new { success = false, message = "Only personal access tokens can be revoked here." }, statusCode: 400);
            var token = await db.ApiTokens.FirstAsync(t => t.Id == id, ct);
            token.RevokedAt = clock.UtcNow;
            await db.SaveChangesAsync(ct);
            return Results.Json(new { success = true, message = "Token revoked." });
        }).RequireAuthorization(new Microsoft.AspNetCore.Authorization.AuthorizeAttribute { AuthenticationSchemes = AuthSchemes.PersonalAccessToken });

        legacy.MapPost("/servers", async (ServerRequest r, ServerService svc, IValidator<ServerUpsertRequest> validator, CancellationToken ct) =>
        {
            var req = new ServerUpsertRequest(r.ServerName ?? string.Empty, r.ServerIp ?? string.Empty, r.SshPort ?? 0, r.SshUser ?? string.Empty, r.PrivateKey, false, null, null);
            await validator.ValidateAndThrowAsync(req, ct);
            var s = await svc.CreateAsync(req, ct);
            return Results.Json(new { success = true, message = "Server created successfully.", server = LegacyServer(s) }, statusCode: 201);
        }).RequireAuthorization(Permissions.ServersWrite);

        legacy.MapPost("/servers/test-ssh", async (ServerRequest r, ServerService svc, IValidator<ConnectionTestRequest> validator, CancellationToken ct) =>
        {
            var req = new ConnectionTestRequest(r.ServerIp ?? string.Empty, r.SshPort ?? 0, r.SshUser ?? string.Empty, r.PrivateKey, null);
            await validator.ValidateAndThrowAsync(req, ct);
            var result = await svc.TestAsync(req, ct);
            return Results.Json(new { success = result.Success, message = result.Message, target_fingerprint = result.TargetFingerprint, worker_fingerprint = result.WorkerFingerprint });
        }).RequireAuthorization(Permissions.ServersTest).RequireRateLimiting(RateLimits.Sensitive);

        legacy.MapPost("/backup-jobs", async (JobRequest r, BackupJobService jobs, BackoraDbContext db, IValidator<BackupJobUpsertRequest> validator, CancellationToken ct) =>
        {
            var template = await db.ScriptTemplates.AsNoTracking().FirstOrDefaultAsync(t => t.Name == r.ScriptName && t.Category == r.Category, ct);
            if (template is null)
            {
                return Results.Json(new { success = false, message = "The selected script_name is invalid for this category.", errors = new { script_name = new[] { "No template with this name exists in the given category." } } }, statusCode: 422);
            }
            if (r.Status is not ("active" or "inactive"))
            {
                return Results.Json(new { success = false, message = "The selected status is invalid.", errors = new { status = new[] { "Use active or inactive." } } }, statusCode: 422);
            }
            var req = new BackupJobUpsertRequest(r.Name ?? string.Empty, null, template.Id, r.ServerId ?? 0, r.Status == "active" ? JobStatusDto.Active : JobStatusDto.Inactive,
                r.ScheduleAt, "UTC", MissedRunPolicyDto.RunOnceWithinGrace, 60, r.RetentionDays ?? 7, 1, r.S3ConfigId, false, true, 360, 3, null);
            await validator.ValidateAndThrowAsync(req, ct);
            var job = await jobs.CreateAsync(req, ct);
            return Results.Json(new
            {
                success = true,
                message = "Backup job created successfully.",
                backup_job = new
                {
                    id = job.Id, name = job.Name, category = job.Category, script_name = job.TemplateName, status = job.Status == JobStatusDto.Active ? "active" : "inactive",
                    schedule_at = job.CronExpression, server_id = job.ServerId, retention_days = job.RetentionDays, s3_config_id = job.S3DestinationId,
                    s3_config_added_at = job.S3DestinationAssignedAt, created_at = job.CreatedAt, updated_at = job.UpdatedAt,
                },
            }, statusCode: 201);
        }).RequireAuthorization(Permissions.JobsWrite);
    }

    private static object LegacyServer(ServerDto s) => new
    {
        id = s.Id, server_name = s.Name, server_ip = s.Host, ssh_user = s.SshUser, ssh_port = s.SshPort, has_private_key = s.HasPrivateKey,
        host_key_fingerprint = s.HostKeyFingerprint, created_at = s.CreatedAt, updated_at = s.UpdatedAt,
    };
}
