using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Backora.Domain.Execution;

public sealed record ScriptRenderContext(
    string JobName,
    string Category,
    string ScriptName,
    decimal RetentionDays,
    string NfsHost,
    string NfsPath,
    IReadOnlyDictionary<string, string> Variables);

public sealed record RenderedScript(string Content, string Sha256, IReadOnlyList<string> UnresolvedPlaceholders);

/// <summary>
/// Renders legacy-compatible templates. Built-in tokens: {JOB_NAME} {CATEGORY} {SCRIPT_NAME}
/// {RETENTION_DAYS} {NFS_HOST} {NFS_PATH}; plus every variable as {name}. Substitution is literal (as in
/// the legacy system) because templates already quote their placeholders. Built-in values are validated
/// elsewhere to be safe path segments.
/// </summary>
public static partial class ScriptRenderer
{
    public static RenderedScript Render(string template, ScriptRenderContext ctx)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["JOB_NAME"] = ctx.JobName,
            ["CATEGORY"] = ctx.Category,
            ["SCRIPT_NAME"] = ctx.ScriptName,
            ["RETENTION_DAYS"] = ctx.RetentionDays.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture),
            ["NFS_HOST"] = ctx.NfsHost,
            ["NFS_PATH"] = ctx.NfsPath,
        };
        foreach (var (k, v) in ctx.Variables)
        {
            map.TryAdd(k, v);
        }

        var unresolved = new SortedSet<string>(StringComparer.Ordinal);
        var content = PlaceholderRegex().Replace(template, m =>
        {
            var key = m.Groups[1].Value;
            if (map.TryGetValue(key, out var value)) return value;
            unresolved.Add(key);
            return m.Value;
        });

        // Normalise line endings: scripts edited in a browser may contain CRLF which breaks bash.
        content = content.Replace("\r\n", "\n", StringComparison.Ordinal);
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(content)));
        return new RenderedScript(content, hash, unresolved.ToList());
    }

    /// <summary>Lists placeholders referenced by a template (used for validation and UI hints).</summary>
    public static IReadOnlyList<string> FindPlaceholders(string template) =>
        PlaceholderRegex().Matches(template).Select(m => m.Groups[1].Value).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();

    // Matches {NAME} but not bash ${NAME} expansions or {{double}} braces.
    [GeneratedRegex(@"(?<![\$\{])\{([A-Za-z_][A-Za-z0-9_]*)\}(?!\})", RegexOptions.CultureInvariant)]
    private static partial Regex PlaceholderRegex();
}

/// <summary>POSIX shell quoting. Every dynamic value placed in a remote command goes through this.</summary>
public static class ShellQuote
{
    public static string Quote(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Contains('\0', StringComparison.Ordinal)) throw new ArgumentException("NUL byte not allowed in shell argument.", nameof(value));
        return "'" + value.Replace("'", "'\"'\"'", StringComparison.Ordinal) + "'";
    }

    /// <summary>Validates an absolute POSIX path without traversal or control characters.</summary>
    public static bool IsSafeAbsolutePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || path[0] != '/' || path.Length > 1024) return false;
        if (path.Any(c => char.IsControl(c))) return false;
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments.All(s => s is not "." and not "..");
    }

    /// <summary>Joins POSIX path segments, rejecting traversal.</summary>
    public static string Join(string basePath, params string[] segments)
    {
        var sb = new StringBuilder(basePath.TrimEnd('/'));
        foreach (var s in segments)
        {
            var seg = s.Trim('/');
            if (seg.Length == 0) continue;
            if (seg.Split('/').Any(x => x is "." or "..")) throw new ArgumentException($"Path traversal in segment '{s}'.");
            sb.Append('/').Append(seg);
        }
        return sb.Length == 0 ? "/" : sb.ToString();
    }
}
