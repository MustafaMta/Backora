namespace Backora.DesignSystem;

/// <summary>Semantic tone shared by pills, timeline dots, health indicators and stat tiles.</summary>
public enum StatusTone
{
    Neutral,
    Success,
    Danger,
    Running,
    Warning,
    Unknown,
    Brand,
}

public static class StatusToneExtensions
{
    public static string ToCss(this StatusTone tone) => tone switch
    {
        StatusTone.Success => "success",
        StatusTone.Danger => "danger",
        StatusTone.Running => "running",
        StatusTone.Warning => "warning",
        StatusTone.Unknown => "unknown",
        StatusTone.Brand => "brand",
        _ => "neutral",
    };

    /// <summary>CSS class for small status dots (.dot--*).</summary>
    public static string ToDot(this StatusTone tone) => tone switch
    {
        StatusTone.Brand => "dot--running",
        _ => "dot--" + tone.ToCss(),
    };
}
