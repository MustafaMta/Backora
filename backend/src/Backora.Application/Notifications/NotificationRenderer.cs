using System.Net;
using System.Text.Json;
using Backora.Domain.Notifications;

namespace Backora.Application.Notifications;

public static class NotificationRenderer
{
    /// <summary>Renders an email template; every placeholder value is HTML-encoded.</summary>
    public static string RenderEmail(string? template, IReadOnlyDictionary<string, string?> values) =>
        PlaceholderTemplate.Render(string.IsNullOrWhiteSpace(template) ? DefaultTemplates.Email : template, values, v => WebUtility.HtmlEncode(v));

    /// <summary>
    /// Renders a Teams card; values are JSON-string-escaped so they cannot break out of string literals.
    /// The result is parsed to guarantee valid JSON before sending.
    /// </summary>
    public static (string Json, string? Error) RenderTeams(string? template, IReadOnlyDictionary<string, string?> values)
    {
        var rendered = PlaceholderTemplate.Render(string.IsNullOrWhiteSpace(template) ? DefaultTemplates.Teams : template, values, PlaceholderTemplate.JsonEscape);
        try
        {
            using var doc = JsonDocument.Parse(rendered);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return (rendered, "Template must render to a JSON object.");
            return (rendered, null);
        }
        catch (JsonException ex)
        {
            return (rendered, $"Template does not render to valid JSON: {ex.Message}");
        }
    }

    public static IReadOnlyDictionary<string, string?> ParsePayload(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, string?>>(json) ?? [];
        }
        catch (JsonException)
        {
            return new Dictionary<string, string?>();
        }
    }
}
