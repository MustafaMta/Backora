using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Backora.Tools.Legacy;

public sealed class TableReport
{
    public long Source { get; set; }
    public long Imported { get; set; }
    public long Target { get; set; }
    public List<SkipEntry> Skipped { get; set; } = [];
    public List<string> Transformations { get; set; } = [];
}

public sealed record SkipEntry(long Id, string Reason);

public sealed record CheckEntry(string Name, bool Passed, string Detail);

/// <summary>Discrepancy report. Contains only ids, names, counts and booleans — never secret values.</summary>
public sealed class ImportReport
{
    public string Mode { get; set; } = "import";
    public bool DryRun { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
    public string LegacyDatabase { get; set; } = string.Empty;
    public string? LegacyServerVersion { get; set; }
    public SortedDictionary<string, TableReport> Tables { get; } = new(StringComparer.Ordinal);
    public List<CheckEntry> Checks { get; } = [];
    public List<string> Warnings { get; } = [];
    public bool Passed => Checks.All(c => c.Passed);
    public string Outcome => Passed ? "PASS" : "FAIL";

    public TableReport Table(string name)
    {
        if (!Tables.TryGetValue(name, out var t)) Tables[name] = t = new TableReport();
        return t;
    }

    public void Skip(string table, long id, string reason) => Table(table).Skipped.Add(new SkipEntry(id, reason));

    public void Note(string table, string message) => Table(table).Transformations.Add(message);

    public void Check(string name, bool passed, string detail) => Checks.Add(new CheckEntry(name, passed, detail));

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public string ToJson() => JsonSerializer.Serialize(new
    {
        outcome = Outcome, Mode, DryRun, StartedAt, FinishedAt, LegacyDatabase, LegacyServerVersion, Tables, Checks, Warnings,
    }, Json);

    public string ToMarkdown()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# Backora legacy {Mode} report — {Outcome}{(DryRun ? " (dry run, rolled back)" : string.Empty)}");
        sb.AppendLine();
        sb.AppendLine($"- Legacy database: `{LegacyDatabase}` (MariaDB {LegacyServerVersion})");
        sb.AppendLine($"- Started: {StartedAt:u} · Finished: {FinishedAt:u}");
        sb.AppendLine();
        sb.AppendLine("| Table | Source | Imported | Skipped | Target (legacy ids) |");
        sb.AppendLine("|---|---:|---:|---:|---:|");
        foreach (var (name, t) in Tables) sb.AppendLine($"| {name} | {t.Source} | {t.Imported} | {t.Skipped.Count} | {t.Target} |");
        sb.AppendLine();
        sb.AppendLine("## Checks");
        foreach (var c in Checks) sb.AppendLine($"- {(c.Passed ? "✅" : "❌")} **{c.Name}** — {c.Detail}");
        var skips = Tables.Where(t => t.Value.Skipped.Count > 0).ToList();
        if (skips.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("## Skipped rows");
            foreach (var (name, t) in skips)
            {
                foreach (var g in t.Skipped.GroupBy(s => s.Reason))
                {
                    sb.AppendLine($"- `{name}` ({g.Count()}): {g.Key} — ids {string.Join(", ", g.Select(s => s.Id).Take(50))}{(g.Count() > 50 ? ", …" : string.Empty)}");
                }
            }
        }
        var notes = Tables.Where(t => t.Value.Transformations.Count > 0).ToList();
        if (notes.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("## Transformations");
            foreach (var (name, t) in notes)
            {
                foreach (var n in t.Transformations.Take(200)) sb.AppendLine($"- `{name}`: {n}");
                if (t.Transformations.Count > 200) sb.AppendLine($"- `{name}`: … {t.Transformations.Count - 200} more");
            }
        }
        if (Warnings.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("## Warnings");
            foreach (var w in Warnings) sb.AppendLine($"- {w}");
        }
        return sb.ToString();
    }
}
