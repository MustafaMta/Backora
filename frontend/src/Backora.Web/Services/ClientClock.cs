using Backora.DesignSystem;

namespace Backora.Web.Services;

/// <summary>Viewer's time zone (from the browser) so timestamps render in local time; UTC until known.</summary>
public sealed class ClientClock(BackoraJs js, ILogger<ClientClock> logger)
{
    public TimeZoneInfo Zone { get; private set; } = TimeZoneInfo.Utc;
    public bool Initialized { get; private set; }
    public event Action? Changed;

    public string ZoneLabel => Zone == TimeZoneInfo.Utc ? "UTC" : Zone.Id;

    public async Task InitializeAsync()
    {
        if (Initialized) return;
        var id = await js.InvokeAsync<string>("timeZone");
        Initialized = true;
        if (string.IsNullOrWhiteSpace(id)) return;
        try
        {
            Zone = TimeZoneInfo.FindSystemTimeZoneById(id);
            Changed?.Invoke();
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            logger.LogDebug("Unknown browser time zone {Zone}", id);
        }
    }

    public string Format(DateTimeOffset? value, bool seconds = false) => Fmt.DateTime(value, Zone, seconds);
}
