namespace Backora.Web.Services;

public static class TimeZones
{
    private static readonly Lazy<IReadOnlyList<string>> All = new(() =>
    {
        var ids = TimeZoneInfo.GetSystemTimeZones()
            .Select(z => TimeZoneInfo.TryConvertWindowsIdToIanaId(z.Id, out var iana) ? iana : z.Id)
            .Where(id => id.Contains('/') || id == "UTC")
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();
        if (!ids.Contains("UTC")) ids.Insert(0, "UTC");
        else { ids.Remove("UTC"); ids.Insert(0, "UTC"); }
        return ids;
    });

    /// <summary>IANA time zone identifiers known to the server, UTC first.</summary>
    public static IReadOnlyList<string> Iana => All.Value;
}
