using System.Globalization;
using Backora.Contracts.V1;
using Backora.DesignSystem;

namespace Backora.Web.Components.Shared;

/// <summary>V2 presentation vocabulary: tones, engine glyphs and plain-language wording.</summary>
public static class Ui
{
    public static string Tone(StatusTone t) => t switch
    {
        StatusTone.Success => "ok",
        StatusTone.Danger => "bad",
        StatusTone.Warning => "warn",
        StatusTone.Running => "info",
        StatusTone.Brand => "accent",
        _ => "muted",
    };

    /// <summary>Status glyph shape for a status view (see StatusIcon).</summary>
    public static string Glyph(Backora.Web.Services.StatusView v) => v.Icon == "pause" ? "paused" : v.Tone switch
    {
        StatusTone.Success or StatusTone.Brand => "ok",
        StatusTone.Danger => "bad",
        StatusTone.Warning => "warn",
        StatusTone.Running => "running",
        _ => "idle",
    };

    public static string Engine(string? category)
    {
        var c = (category ?? string.Empty).ToLowerInvariant();
        if (c.Contains("postgres", StringComparison.Ordinal) || c.StartsWith("pg", StringComparison.Ordinal)) return "postgresql";
        if (c.Contains("maria", StringComparison.Ordinal) || c.Contains("mysql", StringComparison.Ordinal)) return "mariadb";
        if (c.Contains("file", StringComparison.Ordinal) || c.Contains("tar", StringComparison.Ordinal) || c.Contains("dir", StringComparison.Ordinal)) return "folder";
        return "database";
    }

    public static string EngineGlyph(string? category) => Engine(category) switch { "postgresql" => "ui-glyph--pg", "mariadb" => "ui-glyph--maria", _ => "" };

    public static string EngineLabel(string? category) => Engine(category) switch
    {
        "postgresql" => "PostgreSQL",
        "mariadb" => "MariaDB / MySQL",
        "folder" => "Files",
        _ => string.IsNullOrWhiteSpace(category) ? "Database" : category!,
    };

    public static string Keep(decimal days) => days switch
    {
        <= 0 => "until deleted",
        < 1 => string.Create(CultureInfo.InvariantCulture, $"{days * 24:0.##} hours"),
        1 => "1 day",
        _ => string.Create(CultureInfo.InvariantCulture, $"{days:0.##} days"),
    };

    /// <summary>
    /// A copy attempt replaced by a later attempt for the same backup run and site (retrying a copy creates a new one).
    /// The API marks it (<see cref="OffloadSummaryDto.SupersededById"/>); <paramref name="all"/> is only a fallback for older APIs.
    /// </summary>
    public static bool IsSuperseded(OffloadSummaryDto o, IEnumerable<OffloadSummaryDto> all) =>
        o.SupersededById is not null
        || (o.BackupRunId is { } run && all.Any(x => x.Id > o.Id && x.BackupRunId == run && x.DestinationId == o.DestinationId));

    /// <summary>The latest copy attempt for each site, so a successful retry hides the failure it replaced.</summary>
    public static IReadOnlyList<OffloadSummaryDto> LatestPerSite(IReadOnlyList<OffloadSummaryDto> offloads) =>
        offloads.Where(o => !IsSuperseded(o, offloads)).ToList();

    public static string Plural(long n, string one, string many) => $"{n.ToString("N0", CultureInfo.InvariantCulture)} {(n == 1 ? one : many)}";

    public static bool Encrypted(S3DestinationDto d) => d.EncryptionMode != EncryptionModeDto.None;
}
