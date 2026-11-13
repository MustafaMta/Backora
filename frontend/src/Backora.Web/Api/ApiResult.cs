using System.Net;
using System.Text.Json;

namespace Backora.Web.Api;

public enum ApiOutcome
{
    Success,
    ValidationFailed,
    Conflict,
    ConcurrencyConflict,
    NotFound,
    Forbidden,
    Unauthorized,
    RateLimited,
    ServerError,
    NetworkError,
}

/// <summary>RFC 9457 problem details as returned by the Backora API.</summary>
public sealed class ApiProblem
{
    public string? Title { get; init; }
    public string? Detail { get; init; }
    public string? Code { get; init; }
    public int? Status { get; init; }
    public string? CorrelationId { get; init; }
    public string? TraceId { get; init; }
    public IReadOnlyDictionary<string, string[]> Errors { get; init; } = new Dictionary<string, string[]>();

    public static ApiProblem? Parse(string? body, string? correlationHeader)
    {
        if (string.IsNullOrWhiteSpace(body)) return correlationHeader is null ? null : new ApiProblem { CorrelationId = correlationHeader };
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return new ApiProblem { CorrelationId = correlationHeader };
            var errors = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
            if (root.TryGetProperty("errors", out var errs) && errs.ValueKind == JsonValueKind.Object)
            {
                foreach (var p in errs.EnumerateObject())
                {
                    errors[p.Name] = p.Value.ValueKind switch
                    {
                        JsonValueKind.Array => p.Value.EnumerateArray().Select(e => e.ValueKind == JsonValueKind.String ? e.GetString() ?? string.Empty : e.ToString()).ToArray(),
                        JsonValueKind.String => [p.Value.GetString() ?? string.Empty],
                        _ => [p.Value.ToString()],
                    };
                }
            }
            return new ApiProblem
            {
                Title = Str(root, "title"),
                Detail = Str(root, "detail") ?? Str(root, "message"),
                Code = Str(root, "code"),
                Status = root.TryGetProperty("status", out var st) && st.TryGetInt32(out var s) ? s : null,
                CorrelationId = Str(root, "correlationId") ?? correlationHeader,
                TraceId = Str(root, "traceId"),
                Errors = errors,
            };
        }
        catch (JsonException)
        {
            return new ApiProblem { CorrelationId = correlationHeader };
        }
    }

    private static string? Str(JsonElement root, string name) =>
        root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}

public class ApiResult
{
    public ApiOutcome Outcome { get; init; }
    public int StatusCode { get; init; }
    public ApiProblem? Problem { get; init; }

    public bool Ok => Outcome == ApiOutcome.Success;
    public bool IsConcurrencyConflict => Outcome == ApiOutcome.ConcurrencyConflict;
    public string? CorrelationId => Problem?.CorrelationId;

    /// <summary>Human-readable message suitable for toasts and error states.</summary>
    public string Message => Outcome switch
    {
        ApiOutcome.Success => "Done.",
        ApiOutcome.Unauthorized => "Your session has expired. Please sign in again.",
        ApiOutcome.Forbidden => Problem?.Detail ?? "You don't have permission to perform this action.",
        ApiOutcome.NotFound => Problem?.Detail ?? "The requested item could not be found. It may have been deleted.",
        ApiOutcome.ConcurrencyConflict => "Someone else changed this item while you were editing. Reload to get the latest version, then reapply your changes.",
        ApiOutcome.Conflict => Problem?.Detail ?? Problem?.Title ?? "This action conflicts with the current state of the item.",
        ApiOutcome.ValidationFailed => Problem?.Detail ?? (Problem?.Errors.Count > 0 ? "Please correct the highlighted fields." : Problem?.Title ?? "The request was not valid."),
        ApiOutcome.RateLimited => "Too many requests. Please wait a moment and try again.",
        ApiOutcome.NetworkError => "The Backora API is unreachable. Check that the backend is running and try again.",
        _ => Problem?.Detail is { Length: > 0 } d && StatusCode < 500 ? d : "The server could not complete the request.",
    };

    public string Title => Outcome switch
    {
        ApiOutcome.ValidationFailed => "Check your input",
        ApiOutcome.Forbidden => "Not allowed",
        ApiOutcome.NotFound => "Not found",
        ApiOutcome.ConcurrencyConflict => "Changed by someone else",
        ApiOutcome.Conflict => "Conflict",
        ApiOutcome.RateLimited => "Slow down",
        ApiOutcome.Unauthorized => "Signed out",
        ApiOutcome.NetworkError => "Connection problem",
        _ => "Request failed",
    };

    /// <summary>Returns the first validation message for a field (case-insensitive, also matches nested "Field.Sub").</summary>
    public string? FieldError(string field)
    {
        if (Problem is null || Problem.Errors.Count == 0) return null;
        if (Problem.Errors.TryGetValue(field, out var direct) && direct.Length > 0) return direct[0];
        var match = Problem.Errors.FirstOrDefault(kv => kv.Key.EndsWith("." + field, StringComparison.OrdinalIgnoreCase) || kv.Key.Equals("$." + field, StringComparison.OrdinalIgnoreCase));
        return match.Value is { Length: > 0 } ? match.Value[0] : null;
    }

    public static ApiOutcome Classify(HttpStatusCode status, ApiProblem? problem)
    {
        var code = (int)status;
        if (code is >= 200 and < 300) return ApiOutcome.Success;
        return code switch
        {
            400 or 422 => ApiOutcome.ValidationFailed,
            401 => ApiOutcome.Unauthorized,
            403 => ApiOutcome.Forbidden,
            404 => ApiOutcome.NotFound,
            409 when string.Equals(problem?.Code, "concurrency_conflict", StringComparison.OrdinalIgnoreCase) => ApiOutcome.ConcurrencyConflict,
            412 => ApiOutcome.ConcurrencyConflict,
            409 => ApiOutcome.Conflict,
            429 => ApiOutcome.RateLimited,
            _ => ApiOutcome.ServerError,
        };
    }

    public static ApiResult Network() => new() { Outcome = ApiOutcome.NetworkError };
}

public sealed class ApiResult<T> : ApiResult
{
    public T? Value { get; init; }

    public static ApiResult<T> From(ApiResult r, T? value = default) =>
        new() { Outcome = r.Outcome, StatusCode = r.StatusCode, Problem = r.Problem, Value = value };
}
