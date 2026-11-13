using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Backora.Domain.Backups;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Backora.Infrastructure.Persistence;

/// <summary>A built-in backup template as shipped (backend/database/templates, with _common.inc inlined).</summary>
public sealed record BuiltInTemplate(string Name, string Category, string? Description, string Script);

/// <summary>
/// The built-in backup templates are embedded in this assembly. Templates live in the database, so a fix to a template
/// file has to reach the stored copy: at startup <see cref="SyncAsync"/> replaces a stored built-in template with the
/// shipped one, but only when the stored script is a version Backora itself shipped earlier
/// (<c>shipped-versions.txt</c>). A template an administrator edited is never overwritten.
/// </summary>
public static partial class BuiltInTemplates
{
    private const string Prefix = "BuiltInTemplates/";

    public static IReadOnlyList<BuiltInTemplate> Load()
    {
        var common = Read("_common.inc");
        var list = new List<BuiltInTemplate>();
        foreach (var resource in Assembly.GetManifestResourceNames().Where(n => n.StartsWith(Prefix, StringComparison.Ordinal) && n.EndsWith(".sh", StringComparison.Ordinal)).Order(StringComparer.Ordinal))
        {
            var script = Inline(Read(resource[Prefix.Length..]), common);
            var head = HeaderRegex().Match(script);
            if (!head.Success) throw new InvalidOperationException($"{resource} has no '# @template name=\"…\" category=\"…\"' line.");
            list.Add(new BuiltInTemplate(head.Groups["name"].Value, head.Groups["cat"].Value, head.Groups["desc"].Success ? head.Groups["desc"].Value : null, script));
        }
        return list;
    }

    /// <summary>SHA-256 of every built-in template version ever shipped.</summary>
    public static IReadOnlySet<string> ShippedVersions() =>
        Read("shipped-versions.txt").Split('\n')
            .Select(l => l.Trim()).Where(l => l.Length >= 64 && !l.StartsWith('#'))
            .Select(l => l[..64].ToLowerInvariant()).ToHashSet(StringComparer.Ordinal);

    /// <summary>Inlines the shared block exactly as the consolidation tool stores templates.</summary>
    public static string Inline(string template, string common) =>
        template.Replace("\r\n", "\n", StringComparison.Ordinal).Replace("#@include _common.inc\n", common, StringComparison.Ordinal);

    public static string Hash(string script) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(script)));

    public enum Decision { UpToDate, Update, Customized }

    public static Decision Decide(string storedScript, BuiltInTemplate shipped, IReadOnlySet<string> shippedVersions) =>
        storedScript == shipped.Script ? Decision.UpToDate
        : shippedVersions.Contains(Hash(storedScript)) ? Decision.Update
        : Decision.Customized;

    /// <summary>Brings stored built-in templates up to date (new revision); returns how many were updated.</summary>
    public static async Task<int> SyncAsync(BackoraDbContext db, ILogger logger, CancellationToken ct, IReadOnlyList<BuiltInTemplate>? templates = null, IReadOnlySet<string>? shippedVersions = null)
    {
        templates ??= Load();
        shippedVersions ??= ShippedVersions();
        var names = templates.Select(t => t.Name).ToList();
        var stored = await db.ScriptTemplates.Where(t => names.Contains(t.Name)).ToListAsync(ct);
        var updated = 0;
        foreach (var t in templates)
        {
            var s = stored.FirstOrDefault(x => x.Name == t.Name);
            if (s is null) continue;   // never re-create a template an administrator removed
            switch (Decide(s.Script, t, shippedVersions))
            {
                case Decision.Update:
                    s.Script = t.Script;
                    s.Revision++;
                    if (t.Description is not null) s.Description = t.Description;
                    updated++;
                    logger.LogInformation("Built-in template {Template} updated to the shipped version (revision {Revision})", t.Name, s.Revision);
                    break;
                case Decision.Customized:
                    logger.LogWarning("Built-in template {Template} was edited in Backora, so the shipped update was not applied", t.Name);
                    break;
            }
        }
        if (updated == 0) return 0;
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateConcurrencyException)
        {
            // Another replica updated them at the same moment; its result is the same.
            logger.LogInformation("Built-in templates were updated concurrently by another instance");
            return 0;
        }
        return updated;
    }

    private static Assembly Assembly => typeof(BuiltInTemplates).Assembly;

    private static string Read(string name)
    {
        using var stream = Assembly.GetManifestResourceStream(Prefix + name) ?? throw new InvalidOperationException($"Embedded template resource {name} is missing.");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    [GeneratedRegex("""^# @template name="(?<name>[^"]+)" category="(?<cat>[^"]+)"(?: description="(?<desc>[^"]*)")?""", RegexOptions.Multiline)]
    private static partial Regex HeaderRegex();
}
