using Backora.Application.Inventory;
using Backora.Contracts.Security;
using Backora.Contracts.V1;

namespace Backora.Api.Endpoints;

public static class InventoryEndpoints
{
    public static void MapInventory(this IEndpointRouteBuilder app)
    {
        // ───────────── Servers ─────────────
        var servers = app.MapGroup("/api/v1/servers").WithTags("Servers").RequireAuthorization();
        servers.MapGet("", async (string? q, int? page, int? pageSize, string? sort, ServerService svc, CancellationToken ct) =>
            TypedResults.Ok(await svc.ListAsync(q, EndpointExtensions.Page(page, pageSize, sort), ct))).RequirePermission(Permissions.ServersRead);
        servers.MapGet("/{id:long}", async (long id, ServerService svc, CancellationToken ct) => TypedResults.Ok(await svc.GetAsync(id, ct))).RequirePermission(Permissions.ServersRead);
        servers.MapPost("", async (ServerUpsertRequest r, ServerService svc, CancellationToken ct) =>
        {
            var created = await svc.CreateAsync(r, ct);
            return TypedResults.Created($"/api/v1/servers/{created.Id}", created);
        }).Validate<ServerUpsertRequest>().RequirePermission(Permissions.ServersWrite);
        servers.MapPut("/{id:long}", async (long id, ServerUpsertRequest r, ServerService svc, CancellationToken ct) => TypedResults.Ok(await svc.UpdateAsync(id, r, ct)))
            .Validate<ServerUpsertRequest>().RequirePermission(Permissions.ServersWrite);
        servers.MapDelete("/{id:long}", async (long id, ServerService svc, CancellationToken ct) => { await svc.DeleteAsync(id, ct); return TypedResults.NoContent(); })
            .RequirePermission(Permissions.ServersWrite);
        servers.MapPost("/test", async (ConnectionTestRequest r, ServerService svc, CancellationToken ct) => TypedResults.Ok(await svc.TestAsync(r, ct)))
            .Validate<ConnectionTestRequest>().RequirePermission(Permissions.ServersTest).RequireRateLimiting(RateLimits.Sensitive);
        servers.MapPost("/{id:long}/test", async (long id, ServerService svc, CancellationToken ct) =>
        {
            var s = await svc.GetAsync(id, ct);
            return TypedResults.Ok(await svc.TestAsync(new ConnectionTestRequest(s.Host, s.SshPort, s.SshUser, null, id), ct));
        }).RequirePermission(Permissions.ServersTest).RequireRateLimiting(RateLimits.Sensitive);
        servers.MapPost("/{id:long}/trust-host-key", async (long id, TrustHostKeyRequest r, ServerService svc, CancellationToken ct) =>
            TypedResults.Ok(await svc.TrustHostKeyAsync(id, r.Fingerprint, ct))).RequirePermission(Permissions.ServersWrite);

        // ───────────── Settings ─────────────
        // ───────────── Execution host (the only SSH destination of the backend) ─────────────
        var exec = app.MapGroup("/api/v1/execution-host").WithTags("Execution host").RequireAuthorization();
        exec.MapGet("", async (ExecutionHostService s, CancellationToken ct) => TypedResults.Ok(await s.GetAsync(ct))).RequirePermission(Permissions.SettingsRead);
        exec.MapPost("/probe", async (ExecutionHostService s, CancellationToken ct) => TypedResults.Ok(await s.ProbeAsync(ct)))
            .RequirePermission(Permissions.SettingsWrite).RequireRateLimiting(RateLimits.Sensitive);

        var settings = app.MapGroup("/api/v1/settings").WithTags("Settings").RequireAuthorization();
        settings.MapGet("/worker", async (SettingsService s, CancellationToken ct) => TypedResults.Ok(await s.GetWorkerAsync(ct))).RequirePermission(Permissions.SettingsRead);
        settings.MapPut("/worker", async (WorkerNodeUpdateRequest r, SettingsService s, CancellationToken ct) => TypedResults.Ok(await s.UpdateWorkerAsync(r, ct)))
            .Validate<WorkerNodeUpdateRequest>().RequirePermission(Permissions.SettingsWrite);
        settings.MapPost("/worker/test", async (SettingsService s, CancellationToken ct) => TypedResults.Ok(await s.TestWorkerAsync(ct)))
            .RequirePermission(Permissions.SettingsWrite).RequireRateLimiting(RateLimits.Sensitive);
        settings.MapPost("/worker/trust-host-key", async (TrustHostKeyRequest r, SettingsService s, CancellationToken ct) => TypedResults.Ok(await s.TrustWorkerHostKeyAsync(r.Fingerprint, ct)))
            .RequirePermission(Permissions.SettingsWrite);
        settings.MapGet("/nfs", async (SettingsService s, CancellationToken ct) => TypedResults.Ok(await s.GetNfsAsync(ct))).RequirePermission(Permissions.SettingsRead);
        settings.MapPut("/nfs", async (NfsSettingsUpdateRequest r, SettingsService s, CancellationToken ct) => TypedResults.Ok(await s.UpdateNfsAsync(r, ct)))
            .Validate<NfsSettingsUpdateRequest>().RequirePermission(Permissions.SettingsWrite);
        settings.MapGet("/ssh", async (SettingsService s, CancellationToken ct) => TypedResults.Ok(await s.GetSshAsync(ct))).RequirePermission(Permissions.SettingsRead);
        settings.MapPut("/ssh", async (SshSettingsUpdateRequest r, SettingsService s, CancellationToken ct) => TypedResults.Ok(await s.UpdateSshAsync(r, ct)))
            .Validate<SshSettingsUpdateRequest>().RequirePermission(Permissions.SettingsWrite);
        settings.MapGet("/smtp", async (SettingsService s, CancellationToken ct) => TypedResults.Ok(await s.GetSmtpAsync(ct))).RequirePermission(Permissions.SettingsRead);
        settings.MapPut("/smtp", async (SmtpSettingsUpdateRequest r, SettingsService s, CancellationToken ct) => TypedResults.Ok(await s.UpdateSmtpAsync(r, ct)))
            .Validate<SmtpSettingsUpdateRequest>().RequirePermission(Permissions.SettingsWrite);
        settings.MapPost("/smtp/preview", (TemplatePreviewRequest r, SettingsService s) => TypedResults.Ok(s.PreviewEmail(r.Template))).RequirePermission(Permissions.SettingsWrite);
        settings.MapPost("/smtp/test", async (SendTestNotificationRequest r, SettingsService s, CancellationToken ct) => TypedResults.Ok(await s.SendTestEmailAsync(r.Recipient, ct)))
            .RequirePermission(Permissions.SettingsWrite).RequireRateLimiting(RateLimits.Sensitive);
        settings.MapGet("/teams", async (SettingsService s, CancellationToken ct) => TypedResults.Ok(await s.GetTeamsAsync(ct))).RequirePermission(Permissions.SettingsRead);
        settings.MapPut("/teams", async (TeamsSettingsUpdateRequest r, SettingsService s, CancellationToken ct) => TypedResults.Ok(await s.UpdateTeamsAsync(r, ct)))
            .Validate<TeamsSettingsUpdateRequest>().RequirePermission(Permissions.SettingsWrite);
        settings.MapPost("/teams/preview", (TemplatePreviewRequest r, SettingsService s) => TypedResults.Ok(s.PreviewTeams(r.Template))).RequirePermission(Permissions.SettingsWrite);
        settings.MapPost("/teams/test", async (SettingsService s, CancellationToken ct) => TypedResults.Ok(await s.SendTestTeamsAsync(ct)))
            .RequirePermission(Permissions.SettingsWrite).RequireRateLimiting(RateLimits.Sensitive);
        settings.MapGet("/notifications", async (SettingsService s, CancellationToken ct) => TypedResults.Ok(await s.GetNotificationsAsync(ct))).RequirePermission(Permissions.SettingsRead);
        settings.MapPut("/notifications", async (NotificationSettingsUpdateRequest r, SettingsService s, CancellationToken ct) => TypedResults.Ok(await s.UpdateNotificationsAsync(r, ct)))
            .Validate<NotificationSettingsUpdateRequest>().RequirePermission(Permissions.SettingsWrite);

        // ───────────── Variables ─────────────
        var vars = app.MapGroup("/api/v1/variables").WithTags("Variables").RequireAuthorization();
        vars.MapGet("", async (VariableService s, CancellationToken ct) => TypedResults.Ok(await s.ListAsync(ct))).RequirePermission(Permissions.TemplatesRead);
        vars.MapPost("", async (VariableUpsertRequest r, VariableService s, CancellationToken ct) => TypedResults.Ok(await s.CreateAsync(r, ct)))
            .Validate<VariableUpsertRequest>().RequirePermission(Permissions.TemplatesWrite);
        vars.MapPut("/{id:long}", async (long id, VariableUpsertRequest r, VariableService s, CancellationToken ct) => TypedResults.Ok(await s.UpdateAsync(id, r, ct)))
            .Validate<VariableUpsertRequest>().RequirePermission(Permissions.TemplatesWrite);
        vars.MapDelete("/{id:long}", async (long id, VariableService s, CancellationToken ct) => { await s.DeleteAsync(id, ct); return TypedResults.NoContent(); })
            .RequirePermission(Permissions.TemplatesWrite);

        // ───────────── Templates ─────────────
        var templates = app.MapGroup("/api/v1/templates").WithTags("Templates").RequireAuthorization();
        templates.MapGet("", async (string? q, string? category, TemplateService s, CancellationToken ct) => TypedResults.Ok(await s.ListAsync(q, category, ct)))
            .RequirePermission(Permissions.TemplatesRead);
        templates.MapGet("/categories", async (TemplateService s, CancellationToken ct) => TypedResults.Ok(await s.CategoriesAsync(ct))).RequirePermission(Permissions.TemplatesRead);
        templates.MapGet("/{id:long}", async (long id, TemplateService s, CancellationToken ct) => TypedResults.Ok(await s.GetAsync(id, ct))).RequirePermission(Permissions.TemplatesRead);
        templates.MapPost("", async (TemplateUpsertRequest r, TemplateService s, CancellationToken ct) =>
        {
            var t = await s.CreateAsync(r, ct);
            return TypedResults.Created($"/api/v1/templates/{t.Id}", t);
        }).Validate<TemplateUpsertRequest>().RequirePermission(Permissions.TemplatesWrite);
        templates.MapPut("/{id:long}", async (long id, TemplateUpsertRequest r, TemplateService s, CancellationToken ct) => TypedResults.Ok(await s.UpdateAsync(id, r, ct)))
            .Validate<TemplateUpsertRequest>().RequirePermission(Permissions.TemplatesWrite);
        templates.MapDelete("/{id:long}", async (long id, TemplateService s, CancellationToken ct) => { await s.DeleteAsync(id, ct); return TypedResults.NoContent(); })
            .RequirePermission(Permissions.TemplatesWrite);

        // ───────────── S3 storage ─────────────
        var storage = app.MapGroup("/api/v1/storage/s3").WithTags("Storage").RequireAuthorization();
        storage.MapGet("", async (StorageService s, CancellationToken ct) => TypedResults.Ok(await s.ListAsync(ct))).RequirePermission(Permissions.StorageRead);
        storage.MapGet("/{id:long}", async (long id, StorageService s, CancellationToken ct) => TypedResults.Ok(await s.GetAsync(id, ct))).RequirePermission(Permissions.StorageRead);
        storage.MapGet("/{id:long}/objects", async (long id, string? prefix, string? q, DateTimeOffset? from, DateTimeOffset? to, string? token, StorageService s, CancellationToken ct) =>
            TypedResults.Ok(await s.BrowseAsync(id, prefix, q, from, to, token, ct))).RequirePermission(Permissions.StorageRead);
        storage.MapPost("", async (S3DestinationUpsertRequest r, StorageService s, CancellationToken ct) =>
        {
            var d = await s.CreateAsync(r, ct);
            return TypedResults.Created($"/api/v1/storage/s3/{d.Id}", d);
        }).Validate<S3DestinationUpsertRequest>().RequirePermission(Permissions.StorageWrite).RequirePermission(Permissions.CredentialsManage);
        storage.MapPut("/{id:long}", async (long id, S3DestinationUpsertRequest r, StorageService s, CancellationToken ct) => TypedResults.Ok(await s.UpdateAsync(id, r, ct)))
            .Validate<S3DestinationUpsertRequest>().RequirePermission(Permissions.StorageWrite).RequirePermission(Permissions.CredentialsManage);
        storage.MapDelete("/{id:long}", async (long id, StorageService s, CancellationToken ct) => { await s.DeleteAsync(id, ct); return TypedResults.NoContent(); })
            .RequirePermission(Permissions.StorageWrite);
        storage.MapPost("/{id:long}/move-dr-jobs", async (long id, MoveDrJobsRequest r, StorageService s, CancellationToken ct) => TypedResults.Ok(await s.MoveDrJobsAsync(id, r.ToDestinationId, ct)))
            .RequirePermission(Permissions.StorageWrite).RequirePermission(Permissions.JobsWrite);
        storage.MapPost("/test", async (S3TestRequest r, StorageService s, CancellationToken ct) => TypedResults.Ok(await s.TestAsync(r, ct)))
            .Validate<S3TestRequest>().RequirePermission(Permissions.StorageWrite).RequireRateLimiting(RateLimits.Sensitive);

        // Client-side encryption (age). Only public recipients are ever stored.
        storage.MapPost("/encryption/inspect", (InspectRecipientRequest r) => TypedResults.Ok(StorageService.Inspect(r)))
            .Validate<InspectRecipientRequest>().RequirePermission(Permissions.StorageWrite);
        storage.MapPost("/encryption/generate", async (HttpContext http, StorageService s, CancellationToken ct) =>
        {
            // The private key is in this response exactly once: never cache it anywhere.
            http.Response.Headers.CacheControl = "no-store";
            http.Response.Headers.Pragma = "no-cache";
            return TypedResults.Ok(await s.GenerateKeyPairAsync(ct));
        }).RequirePermission(Permissions.StorageWrite).RequirePermission(Permissions.CredentialsManage).RequireRateLimiting(RateLimits.Sensitive);
    }
}
