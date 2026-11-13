using System.Text.RegularExpressions;

namespace Backora.Domain.Notifications;

/// <summary>
/// Renders legacy notification templates with <c>{{placeholder}}</c> tokens. The caller supplies an
/// encoder appropriate to the output (HTML encoding for email, JSON string escaping for Teams cards), so
/// values such as error logs can never inject markup or break the JSON structure.
/// </summary>
public static partial class PlaceholderTemplate
{
    public static readonly IReadOnlyList<string> KnownPlaceholders =
    [
        "job_name", "job_category", "script_name", "server_name", "server_ip", "ssh_user", "ssh_port",
        "execution_id", "started_at", "completed_at", "failure_time", "total_time", "error_message", "output",
        "admin_url", "event", "status", "failure_class",
    ];

    public static string Render(string template, IReadOnlyDictionary<string, string?> values, Func<string, string> encode)
    {
        return TokenRegex().Replace(template, m =>
        {
            var key = m.Groups[1].Value.Trim();
            return values.TryGetValue(key, out var v) ? encode(v ?? string.Empty) : string.Empty;
        });
    }

    public static string JsonEscape(string value)
    {
        var json = System.Text.Json.JsonSerializer.Serialize(value);
        return json[1..^1];
    }

    [GeneratedRegex(@"\{\{\s*([A-Za-z0-9_]+)\s*\}\}", RegexOptions.CultureInvariant)]
    private static partial Regex TokenRegex();
}
