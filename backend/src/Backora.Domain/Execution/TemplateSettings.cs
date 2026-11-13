using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Backora.Domain.Execution;

/// <summary>A setting a template declares in a comment line, for example:
/// <c># @setting DB_PASSWORD "Database password" secret</c> or <c># @setting DB_PORT "Port" default=5432</c>.</summary>
public sealed record TemplateSettingSpec(string Name, string Label, bool Required, bool Secret, string? Default, string? Help);

/// <summary>
/// Template-declared, per-job settings. Values are never pasted into the script text: they are prepended as
/// <c>export NAME='value'</c> lines (POSIX-quoted), so any character in a password is safe.
/// </summary>
public static partial class TemplateSettings
{
    public const int MaxNameLength = 64;
    public const int MaxValueLength = 4000;

    public static bool IsValidName(string? name) => name is { Length: > 0 and <= MaxNameLength } && NameRegex().IsMatch(name);

    public static IReadOnlyList<TemplateSettingSpec> Parse(string? script)
    {
        if (string.IsNullOrEmpty(script)) return [];
        var specs = new List<TemplateSettingSpec>();
        foreach (Match m in SpecRegex().Matches(script))
        {
            var name = m.Groups["name"].Value;
            if (specs.Any(s => s.Name == name)) continue;
            var rest = m.Groups["rest"].Value;
            var required = FlagRegex("required").IsMatch(rest);
            var secret = FlagRegex("secret").IsMatch(rest);
            string? def = null;
            var d = DefaultRegex().Match(rest);
            if (d.Success) def = d.Groups["q"].Success ? d.Groups["q"].Value : d.Groups["v"].Value;
            var h = HelpRegex().Match(rest);
            specs.Add(new TemplateSettingSpec(name, m.Groups["label"].Value.Trim(), required, secret, def, h.Success ? h.Groups["h"].Value : null));
        }
        return specs;
    }

    /// <summary>Prepends the exported settings (after the shebang) and recomputes the SHA-256.</summary>
    public static RenderedScript Apply(RenderedScript rendered, IReadOnlyList<TemplateSettingSpec> specs, IReadOnlyDictionary<string, string> values,
        IReadOnlyCollection<string>? fromAllJobs = null)
    {
        if (specs.Count == 0) return rendered;
        var block = new StringBuilder("# --- Backora job settings (generated per run) ---\n");
        foreach (var spec in specs)
        {
            var value = values.TryGetValue(spec.Name, out var v) && !string.IsNullOrEmpty(v) ? v : spec.Default ?? string.Empty;
            block.Append("export ").Append(spec.Name).Append('=').Append(ShellQuote.Quote(value)).Append('\n');
        }
        // Settings that came from an all-jobs variable: the template lets a server's own per-job value override them.
        block.Append("export BACKORA_ALL_JOBS=").Append(ShellQuote.Quote(string.Join(' ', fromAllJobs ?? []))).Append('\n');
        block.Append("# --- end of job settings ---\n");

        var content = rendered.Content;
        string result;
        if (content.StartsWith("#!", StringComparison.Ordinal))
        {
            var nl = content.IndexOf('\n', StringComparison.Ordinal);
            result = nl < 0 ? content + "\n" + block : content[..(nl + 1)] + block + content[(nl + 1)..];
        }
        else
        {
            result = block + content;
        }
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(result)));
        return rendered with { Content = result, Sha256 = hash };
    }

    /// <summary>Job part of a job-specific variable: upper case, anything but letters and digits becomes '_'
    /// (<c>sso-db</c> → <c>SSO_DB</c>), so it is a valid variable name.</summary>
    public static string JobKey(string jobName) => JobKeyRegex().Replace(jobName.ToUpperInvariant(), "_");

    /// <summary>The Backora variable that holds a setting: <c>NAME__JOB</c> for one job, <c>NAME</c> for every job.</summary>
    public static string VariableName(string setting, string? jobName = null) => jobName is null ? setting : $"{setting}__{JobKey(jobName)}";

    /// <summary>
    /// Resolves a job's settings from Backora variables (Templates → Variables), never from the job itself:
    /// <c>NAME__JOB</c> (this job) wins over <c>NAME</c> (all jobs); otherwise the template default applies.
    /// </summary>
    public static Dictionary<string, string> Resolve(IEnumerable<TemplateSettingSpec> specs, string jobName, Func<string, string?> variable) =>
        Resolve(specs, jobName, variable, out _);

    /// <summary>As <see cref="Resolve(IEnumerable{TemplateSettingSpec}, string, Func{string, string?})"/>; <paramref name="fromAllJobs"/>
    /// lists the settings whose value came from the all-jobs variable (a server's own per-job value may override those).</summary>
    public static Dictionary<string, string> Resolve(IEnumerable<TemplateSettingSpec> specs, string jobName, Func<string, string?> variable, out SortedSet<string> fromAllJobs)
    {
        ArgumentNullException.ThrowIfNull(variable);
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        fromAllJobs = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var spec in specs)
        {
            var v = NonEmpty(variable(VariableName(spec.Name, jobName)));
            if (v is null && NonEmpty(variable(spec.Name)) is { } all) { v = all; fromAllJobs.Add(spec.Name); }
            if (v is not null) values[spec.Name] = v;
        }
        return values;
    }

    /// <summary>Splits a variable name into the setting and job key it targets (<c>DB_USER__ZABBIX</c> → DB_USER, ZABBIX).</summary>
    public static (string Setting, string? JobKey) SplitVariableName(string name)
    {
        var cut = name.IndexOf("__", StringComparison.Ordinal);
        return cut <= 0 ? (name, null) : (name[..cut], name[(cut + 2)..]);
    }

    private static string? NonEmpty(string? v) => string.IsNullOrEmpty(v) ? null : v;

    [GeneratedRegex(@"[^A-Z0-9]+", RegexOptions.CultureInvariant)]
    private static partial Regex JobKeyRegex();

    [GeneratedRegex(@"^[A-Z][A-Z0-9_]*$", RegexOptions.CultureInvariant)]
    private static partial Regex NameRegex();

    [GeneratedRegex(@"^[ \t]*#[ \t]*@setting[ \t]+(?<name>[A-Z][A-Z0-9_]{0,63})[ \t]+""(?<label>[^""\r\n]*)""(?<rest>[^\r\n]*)$", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex SpecRegex();

    [GeneratedRegex(@"(?:^|\s)default=(?:""(?<q>[^""]*)""|(?<v>\S+))", RegexOptions.CultureInvariant)]
    private static partial Regex DefaultRegex();

    [GeneratedRegex(@"(?:^|\s)help=""(?<h>[^""]*)""", RegexOptions.CultureInvariant)]
    private static partial Regex HelpRegex();

    private static Regex FlagRegex(string flag) => new(@"(?:^|\s)" + flag + @"(?:\s|$)", RegexOptions.CultureInvariant);
}
