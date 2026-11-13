using Backora.Contracts.V1;

namespace Backora.Web.Services;

/// <summary>
/// One protection verdict computed from the dashboard data with the same rules as the Overview's
/// "Needs your attention" list, so the sidebar and the Overview never disagree.
/// </summary>
public readonly record struct ProtectionSummary(int Problems, int ToReview)
{
    public static ProtectionSummary From(DashboardDto d)
    {
        var s = d.System;
        var problems = 0;
        if (!s.DatabaseHealthy) problems++;
        if (!s.WorkerOnline) problems++;
        if (!s.WorkerNodeConfigured || !s.WorkerNodeTrusted) problems++;
        if (!s.NfsConfigured) problems++;
        if (s.DestinationsFailing > 0) problems++;
        problems += d.Attention.Count(a => a.Severity == HealthState.Critical);

        var review = d.Attention.Count(a => a.Severity != HealthState.Critical);
        if (s.UntrustedServers > 0) review++;
        if (d.Kpis.TotalServers - d.Kpis.ProtectedServers > 0) review++;
        return new ProtectionSummary(problems, review);
    }

    public string Tone => Problems > 0 ? "danger" : ToReview > 0 ? "warning" : "success";

    public string Title => Problems switch
    {
        1 => "1 problem",
        > 1 => $"{Problems} problems",
        _ => ToReview switch { 0 => "All protected", 1 => "1 item to review", _ => $"{ToReview} items to review" },
    };

    public string Subtitle => Problems > 0 ? "Needs your attention" : ToReview > 0 ? "Backups are running" : "Backups are healthy";
}
