namespace Backora.Application.Common;

/// <summary>Base for expected application errors mapped to RFC 9457 problem responses by the API.</summary>
public abstract class AppException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

public sealed class NotFoundException(string resource, object id)
    : AppException("not_found", $"{resource} {id} was not found.");

public sealed class ConflictException(string code, string message) : AppException(code, message);

/// <summary>The supplied version does not match the stored version (optimistic concurrency).</summary>
public sealed class ConcurrencyException()
    : AppException("concurrency_conflict", "The resource was modified by someone else. Reload it and apply your changes again.");

public sealed class ForbiddenException(string message = "You do not have permission to perform this action.")
    : AppException("forbidden", message);

public sealed class DomainRuleException(string code, string message) : AppException(code, message);

/// <summary>A well-formed request that cannot be processed (HTTP 422) with field-level errors, e.g. a decryption key that does not match.</summary>
public sealed class UnprocessableRequestException(string code, string field, string message) : AppException(code, message)
{
    public IDictionary<string, string[]> Errors { get; } = new Dictionary<string, string[]> { [field] = [message] };
}

public sealed class RequestValidationException(IDictionary<string, string[]> errors)
    : AppException("validation_failed", "One or more fields are invalid.")
{
    public IDictionary<string, string[]> Errors { get; } = errors;

    public static RequestValidationException For(string field, string message) => new(new Dictionary<string, string[]> { [field] = [message] });
}

public sealed record PageRequest(int Page = 1, int PageSize = 25, string? Sort = null)
{
    public int SafePage => Math.Max(1, Page);
    public int SafePageSize => Math.Clamp(PageSize, 1, 200);
    public int Skip => (SafePage - 1) * SafePageSize;
    public bool Descending => Sort?.StartsWith('-') == true;
    public string? SortField => Sort?.TrimStart('-', '+').ToLowerInvariant();
}
