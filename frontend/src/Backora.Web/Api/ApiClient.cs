using System.Net;
using Backora.Web.Auth;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Backora.Web.Api;

public static class ApiJson
{
    public static readonly JsonSerializerOptions Options = Create();

    private static JsonSerializerOptions Create()
    {
        var o = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        };
        o.Converters.Add(new JsonStringEnumConverter());
        return o;
    }
}

/// <summary>Low-level HTTP wrapper turning every response into an <see cref="ApiResult{T}"/>; never throws for HTTP errors.</summary>
public sealed class ApiClient(BackendHttp http, ILogger<ApiClient> logger, Microsoft.Extensions.Options.IOptions<FrontendOptions>? frontend = null)
{
    /// <summary>POST endpoints that only compute or check something and never change state.</summary>
    private static readonly string[] ReadOnlyPosts =
    [
        "/backup-jobs/schedule-preview", "/restores/precheck", "/storage/s3/encryption/inspect",
        "/settings/smtp/preview", "/settings/teams/preview",
    ];

    internal static bool IsAllowedInReview(HttpMethod method, string path) =>
        method == HttpMethod.Get || (method == HttpMethod.Post && ReadOnlyPosts.Contains(path.Split('?')[0], StringComparer.Ordinal));

    public const string Prefix = "/api/v1";

    public Task<ApiResult<T>> GetAsync<T>(string path, CancellationToken ct = default) =>
        SendAsync<T>(HttpMethod.Get, path, null, null, ct);

    public Task<ApiResult<T>> PostAsync<T>(string path, object? body, string? idempotencyKey = null, CancellationToken ct = default) =>
        SendAsync<T>(HttpMethod.Post, path, body, idempotencyKey, ct);

    public async Task<ApiResult> PostAsync(string path, object? body, CancellationToken ct = default) =>
        await SendAsync<object>(HttpMethod.Post, path, body, null, ct);

    public Task<ApiResult<T>> PutAsync<T>(string path, object? body, CancellationToken ct = default) =>
        SendAsync<T>(HttpMethod.Put, path, body, null, ct);

    public async Task<ApiResult> PutAsync(string path, object? body, CancellationToken ct = default) =>
        await SendAsync<object>(HttpMethod.Put, path, body, null, ct);

    public async Task<ApiResult> DeleteAsync(string path, CancellationToken ct = default) =>
        await SendAsync<object>(HttpMethod.Delete, path, null, null, ct);

    public async Task<ApiResult<T>> SendAsync<T>(HttpMethod method, string path, object? body, string? idempotencyKey, CancellationToken ct)
    {
        if (frontend?.Value.ReadOnlyReview == true && !IsAllowedInReview(method, path))
        {
            logger.LogWarning("Read-only review: refused {Method} {Path}", method, path);
            return new ApiResult<T>
            {
                Outcome = ApiOutcome.Forbidden, StatusCode = 403,
                Problem = new ApiProblem { Title = "Read-only review", Detail = "This preview is read-only. Nothing was changed.", Code = "read_only_review" },
            };
        }
        using var request = new HttpRequestMessage(method, Prefix + path);
        request.Headers.Accept.ParseAdd("application/json");
        if (body is not null) request.Content = JsonContent.Create(body, body.GetType(), options: ApiJson.Options);
        if (idempotencyKey is not null) request.Headers.TryAddWithoutValidation("Idempotency-Key", idempotencyKey);

        HttpResponseMessage response;
        try
        {
            response = await http.Client.SendAsync(request, ct);
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning(ex, "Backend request {Method} {Path} failed", method, path);
            return ApiResult<T>.From(ApiResult.Network());
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning("Backend request {Method} {Path} timed out", method, path);
            return ApiResult<T>.From(ApiResult.Network());
        }

        using (response)
        {
            var correlation = response.Headers.TryGetValues("X-Correlation-ID", out var values) ? values.FirstOrDefault() : null;
            if (response.IsSuccessStatusCode)
            {
                T? value = default;
                if (response.StatusCode != HttpStatusCode.NoContent && typeof(T) != typeof(object) && response.Content.Headers.ContentLength != 0)
                {
                    try
                    {
                        value = await response.Content.ReadFromJsonAsync<T>(ApiJson.Options, ct);
                    }
                    catch (JsonException ex)
                    {
                        logger.LogError(ex, "Could not parse response of {Method} {Path}", method, path);
                        return new ApiResult<T> { Outcome = ApiOutcome.ServerError, StatusCode = (int)response.StatusCode, Problem = new ApiProblem { Detail = "The server returned an unexpected response.", CorrelationId = correlation } };
                    }
                }
                return new ApiResult<T> { Outcome = ApiOutcome.Success, StatusCode = (int)response.StatusCode, Value = value };
            }

            var text = await response.Content.ReadAsStringAsync(ct);
            var problem = ApiProblem.Parse(text, correlation);
            var outcome = ApiResult.Classify(response.StatusCode, problem);
            if (outcome == ApiOutcome.ServerError)
            {
                logger.LogWarning("Backend {Method} {Path} returned {Status} (correlation {Correlation})", method, path, (int)response.StatusCode, problem?.CorrelationId);
            }
            return new ApiResult<T> { Outcome = outcome, StatusCode = (int)response.StatusCode, Problem = problem };
        }
    }
}
