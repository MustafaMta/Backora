using System.Globalization;
using System.Text;

namespace Backora.Web.Api;

/// <summary>Fluent query-string builder that skips null/empty values.</summary>
public sealed class Query
{
    private readonly List<KeyValuePair<string, string>> _pairs = [];

    public Query Add(string key, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value)) _pairs.Add(new(key, value));
        return this;
    }

    public Query Add(string key, long? value) => value is null ? this : Add(key, value.Value.ToString(CultureInfo.InvariantCulture));
    public Query Add(string key, int? value) => value is null ? this : Add(key, value.Value.ToString(CultureInfo.InvariantCulture));
    public Query Add(string key, bool? value) => value is null ? this : Add(key, value.Value ? "true" : "false");
    public Query Add(string key, DateTimeOffset? value) => value is null ? this : Add(key, value.Value.UtcDateTime.ToString("o", CultureInfo.InvariantCulture));
    public Query Add<TEnum>(string key, TEnum? value) where TEnum : struct, Enum => value is null ? this : Add(key, value.Value.ToString());

    public string Build(string path)
    {
        if (_pairs.Count == 0) return path;
        var sb = new StringBuilder(path).Append('?');
        for (var i = 0; i < _pairs.Count; i++)
        {
            if (i > 0) sb.Append('&');
            sb.Append(Uri.EscapeDataString(_pairs[i].Key)).Append('=').Append(Uri.EscapeDataString(_pairs[i].Value));
        }
        return sb.ToString();
    }
}
