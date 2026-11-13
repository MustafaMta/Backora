using System.Globalization;

namespace Backora.DesignSystem;

/// <summary>Formatting helpers. All inputs are UTC; the caller supplies the viewer's time zone.</summary>
public static class Fmt
{
    private static readonly string[] ByteUnits = ["B", "KB", "MB", "GB", "TB", "PB"];

    public static string Bytes(long? bytes)
    {
        if (bytes is null) return "—";
        double value = bytes.Value;
        var unit = 0;
        while (Math.Abs(value) >= 1024 && unit < ByteUnits.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return unit == 0
            ? $"{bytes.Value:N0} B"
            : value.ToString(value >= 100 ? "N0" : value >= 10 ? "N1" : "N2", CultureInfo.InvariantCulture) + " " + ByteUnits[unit];
    }

    public static string Duration(double? seconds)
    {
        if (seconds is null) return "—";
        var ts = TimeSpan.FromSeconds(Math.Max(0, seconds.Value));
        return Duration(ts);
    }

    public static string Duration(TimeSpan ts)
    {
        if (ts.TotalSeconds < 1) return "<1s";
        if (ts.TotalMinutes < 1) return $"{(int)ts.TotalSeconds}s";
        if (ts.TotalHours < 1) return $"{ts.Minutes}m {ts.Seconds:00}s";
        if (ts.TotalDays < 1) return $"{(int)ts.TotalHours}h {ts.Minutes:00}m";
        return $"{(int)ts.TotalDays}d {ts.Hours}h";
    }

    public static string Relative(DateTimeOffset? value, DateTimeOffset? now = null)
    {
        if (value is null) return "—";
        var reference = now ?? DateTimeOffset.UtcNow;
        var delta = reference - value.Value;
        var future = delta < TimeSpan.Zero;
        var abs = future ? -delta : delta;
        string text;
        if (abs.TotalSeconds < 45) text = "just now";
        else if (abs.TotalMinutes < 1.5) text = "1 min";
        else if (abs.TotalMinutes < 60) text = $"{(int)Math.Round(abs.TotalMinutes)} min";
        else if (abs.TotalHours < 1.5) text = "1 hour";
        else if (abs.TotalHours < 24) text = $"{(int)Math.Round(abs.TotalHours)} hours";
        else if (abs.TotalDays < 1.5) text = "1 day";
        else if (abs.TotalDays < 30) text = $"{(int)Math.Round(abs.TotalDays)} days";
        else if (abs.TotalDays < 365) text = $"{(int)Math.Round(abs.TotalDays / 30)} months";
        else text = $"{abs.TotalDays / 365:0.#} years";
        if (text == "just now") return text;
        return future ? $"in {text}" : $"{text} ago";
    }

    public static string DateTime(DateTimeOffset? value, TimeZoneInfo zone, bool seconds = false)
    {
        if (value is null) return "—";
        var local = TimeZoneInfo.ConvertTime(value.Value, zone);
        return local.ToString(seconds ? "yyyy-MM-dd HH:mm:ss" : "yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
    }

    public static string Date(DateTimeOffset? value, TimeZoneInfo zone)
    {
        if (value is null) return "—";
        return TimeZoneInfo.ConvertTime(value.Value, zone).ToString("MMM d, yyyy", CultureInfo.InvariantCulture);
    }

    public static string Number(long value) => value.ToString("N0", CultureInfo.InvariantCulture);

    public static string Percent(double? fraction, int decimals = 1)
    {
        if (fraction is null) return "—";
        return (fraction.Value * 100).ToString("N" + decimals, CultureInfo.InvariantCulture) + "%";
    }

    public static string Initials(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "?";
        var parts = name.Trim().Split([' ', '.', '@', '_', '-'], StringSplitOptions.RemoveEmptyEntries);
        return parts.Length switch
        {
            0 => "?",
            1 => parts[0][..Math.Min(2, parts[0].Length)].ToUpperInvariant(),
            _ => string.Concat(char.ToUpperInvariant(parts[0][0]), char.ToUpperInvariant(parts[1][0])),
        };
    }

    public static string ShortHash(string? hash, int length = 12) =>
        string.IsNullOrEmpty(hash) ? "—" : hash.Length <= length ? hash : hash[..length] + "…";

    /// <summary>Formats a TOTP shared key into readable 4-character groups.</summary>
    public static string GroupKey(string? key)
    {
        if (string.IsNullOrEmpty(key)) return string.Empty;
        var clean = key.Replace(" ", string.Empty, StringComparison.Ordinal);
        var groups = Enumerable.Range(0, (clean.Length + 3) / 4).Select(i => clean.Substring(i * 4, Math.Min(4, clean.Length - i * 4)));
        return string.Join(' ', groups).ToLowerInvariant();
    }
}
