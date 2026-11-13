using System.Diagnostics;
using System.Text.RegularExpressions;
using Backora.Application.Common;
using Backora.Contracts.V1;
using Backora.Infrastructure.Identity;
using FluentValidation;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Backora.Api;

/// <summary>Accepts a safe inbound X-Correlation-ID or generates one; echoed on the response and in logs.</summary>
public sealed partial class CorrelationMiddleware(RequestDelegate next, ILogger<CorrelationMiddleware> logger)
{
    public const string ItemKey = "CorrelationId";

    public async Task InvokeAsync(HttpContext context)
    {
        var inbound = context.Request.Headers[ApiHeaders.CorrelationId].ToString();
        var id = !string.IsNullOrEmpty(inbound) && SafeId().IsMatch(inbound) ? inbound : Activity.Current?.TraceId.ToString() ?? Guid.NewGuid().ToString("N");
        context.Items[ItemKey] = id;
        context.Response.Headers[ApiHeaders.CorrelationId] = id;
        Activity.Current?.SetTag("backora.correlation_id", id);
        using (logger.BeginScope(new Dictionary<string, object> { ["CorrelationId"] = id }))
        {
            await next(context);
        }
    }

    [GeneratedRegex("^[A-Za-z0-9._-]{8,64}$")]
    private static partial Regex SafeId();
}

/// <summary>
/// Users flagged MustChangePassword (bootstrap admin, admin-set passwords, migrated accounts on request) may
/// only read their profile, change their password or sign out until they do so.
/// </summary>
public sealed class PasswordChangeGateMiddleware(RequestDelegate next)
{
    private static readonly string[] Allowed = ["/api/v1/me", "/api/v1/auth/"];

    public async Task InvokeAsync(HttpContext context)
    {
        var path = context.Request.Path.Value ?? string.Empty;
        if (context.User.HasClaim(BackoraClaims.PasswordChangeRequired, "1")
            && path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase)
            && !(path.Equals("/api/v1/me", StringComparison.OrdinalIgnoreCase) || path.Equals("/api/v1/me/password", StringComparison.OrdinalIgnoreCase)
                 || path.StartsWith(Allowed[1], StringComparison.OrdinalIgnoreCase)))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsJsonAsync(new ProblemDetails
            {
                Status = 403,
                Title = "Password change required",
                Detail = "You must change your password before using Backora.",
                Type = "https://backora.local/problems/password_change_required",
                Extensions = { ["code"] = "password_change_required", ["correlationId"] = context.Items[CorrelationMiddleware.ItemKey] },
            }, options: null, contentType: "application/problem+json");
            return;
        }
        await next(context);
    }
}

public sealed class SecurityHeadersMiddleware(RequestDelegate next, IWebHostEnvironment env)
{
    public Task InvokeAsync(HttpContext context)
    {
        var h = context.Response.Headers;
        h.XContentTypeOptions = "nosniff";
        h["Referrer-Policy"] = "no-referrer";
        h.XFrameOptions = "DENY";
        h["Cross-Origin-Resource-Policy"] = "same-origin";
        h["Permissions-Policy"] = "camera=(), microphone=(), geolocation=()";
        var isDocs = env.IsDevelopment() && (context.Request.Path.StartsWithSegments("/scalar") || context.Request.Path.StartsWithSegments("/openapi"));
        h.ContentSecurityPolicy = isDocs ? "frame-ancestors 'none'" : "default-src 'none'; frame-ancestors 'none'";
        if (context.Request.Path.StartsWithSegments("/api")) h.CacheControl = "no-store";
        return next(context);
    }
}

/// <summary>Maps exceptions to RFC 9457 problem details. Never leaks stack traces or internals outside Development.</summary>
public sealed class ProblemExceptionHandler(IProblemDetailsService problems, IWebHostEnvironment env, ILogger<ProblemExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext context, Exception exception, CancellationToken ct)
    {
        var (status, code, title, detail, errors) = exception switch
        {
            RequestValidationException v => (400, v.Code, "Validation failed", v.Message, v.Errors),
            ValidationException fv => (400, "validation_failed", "Validation failed", "One or more fields are invalid.",
                (IDictionary<string, string[]>?)fv.Errors.GroupBy(e => Camel(e.PropertyName)).ToDictionary(g => g.Key, g => g.Select(e => e.ErrorMessage).ToArray())),
            NotFoundException nf => (404, nf.Code, "Not found", nf.Message, null),
            ConcurrencyException c => (409, c.Code, "Conflict", c.Message, null),
            DbUpdateConcurrencyException => (409, "concurrency_conflict", "Conflict", "The resource was modified by someone else. Reload it and apply your changes again.", null),
            ConflictException c => (409, c.Code, "Conflict", c.Message, null),
            ForbiddenException f => (403, f.Code, "Forbidden", f.Message, null),
            DomainRuleException d => (422, d.Code, "Request cannot be processed", d.Message, null),
            UnprocessableRequestException u => (422, u.Code, "Request cannot be processed", u.Message, u.Errors),
            AuthenticationFailedException a => (401, "authentication_failed", "Authentication failed", a.Message, null),
            BadHttpRequestException b => (b.StatusCode, "bad_request", "Bad request", "The request could not be read.", null),
            DbUpdateException { InnerException: PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } } => (409, "unique_violation", "Conflict", "A resource with the same unique values already exists.", null),
            DbUpdateException { InnerException: PostgresException { SqlState: PostgresErrorCodes.ForeignKeyViolation } } => (409, "reference_violation", "Conflict", "The operation conflicts with related resources.", null),
            OperationCanceledException when context.RequestAborted.IsCancellationRequested => (499, "client_closed", "Request cancelled", null, null),
            _ => (500, "internal_error", "Unexpected error", "An unexpected error occurred. Quote the correlation id when contacting support.", null),
        };

        if (status >= 500) logger.LogError(exception, "Unhandled exception for {Method} {Path}", context.Request.Method, context.Request.Path);
        else logger.LogInformation("Request rejected with {Status} {Code}: {Detail}", status, code, detail);

        if (status == 499) return true;
        context.Response.StatusCode = status;
        var pd = new ProblemDetails { Status = status, Title = title, Detail = detail, Type = $"https://backora.local/problems/{code}" };
        pd.Extensions["code"] = code;
        pd.Extensions["correlationId"] = context.Items[CorrelationMiddleware.ItemKey];
        if (errors is not null) pd.Extensions["errors"] = errors;
        if (env.IsDevelopment() && status >= 500) pd.Extensions["exception"] = exception.GetType().Name + ": " + exception.Message;
        return await problems.TryWriteAsync(new ProblemDetailsContext { HttpContext = context, ProblemDetails = pd, Exception = exception });
    }

    private static string Camel(string s) => string.IsNullOrEmpty(s) ? s : char.ToLowerInvariant(s[0]) + s[1..];
}

/// <summary>Runs FluentValidation for endpoint arguments of type T.</summary>
public sealed class ValidationFilter<T>(IValidator<T> validator) : IEndpointFilter where T : class
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var arg = context.Arguments.OfType<T>().FirstOrDefault();
        if (arg is null) throw new RequestValidationException(new Dictionary<string, string[]> { ["body"] = ["A request body is required."] });
        var result = await validator.ValidateAsync(arg, context.HttpContext.RequestAborted);
        if (!result.IsValid) throw new ValidationException(result.Errors);
        return await next(context);
    }
}

public static class EndpointExtensions
{
    public static RouteHandlerBuilder Validate<T>(this RouteHandlerBuilder builder) where T : class => builder.AddEndpointFilter<ValidationFilter<T>>();

    public static RouteHandlerBuilder RequirePermission(this RouteHandlerBuilder builder, string permission) => builder.RequireAuthorization(permission);

    public static PageRequest Page(int? page, int? pageSize, string? sort) => new(page ?? 1, pageSize ?? 25, sort);
}
