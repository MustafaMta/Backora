using Backora.Contracts.V1;

namespace Backora.Web.Services;

/// <summary>One list of things that need attention, used by the Overview and the alerts bell so they never disagree.</summary>
public sealed record ProtectionIssue(string Title, string Detail, string Href, string Tone, string Area);

public static class ProtectionIssues
{
    public static List<ProtectionIssue> From(DashboardDto d)
    {
        var list = new List<ProtectionIssue>();
        var s = d.System;
        if (!s.DatabaseHealthy) list.Add(new("Backora's database is unavailable", "Backups can't be scheduled until it's back.", "/settings", "bad", "System"));
        if (!s.WorkerOnline) list.Add(new("The backup engine is offline", "No backups or restores can run right now.", "/execution-host", "bad", "Execution host"));
        if (!s.WorkerNodeConfigured || !s.WorkerNodeTrusted) list.Add(new(s.WorkerNodeConfigured ? "The execution host isn't trusted yet" : "The execution host isn't set up", "Backups run through it. Finish its setup.", "/execution-host", "bad", "Execution host"));
        if (!s.NfsConfigured) list.Add(new("Shared backup storage isn't configured", "Backups need a place to land before going off-site.", "/settings/storage", "bad", "Settings"));
        if (s.DestinationsFailing > 0) list.Add(new(s.DestinationsFailing == 1 ? "A storage site can't be reached" : $"{s.DestinationsFailing} storage sites can't be reached", "Copies to it are failing until it reconnects.", "/storage", "bad", "Storage & DR"));
        foreach (var a in d.Attention.Where(a => a.Severity == HealthState.Critical)) list.Add(new(a.JobName, a.Reason, $"/jobs/{a.JobId}", "bad", "Backup job"));
        foreach (var a in d.Attention.Where(a => a.Severity != HealthState.Critical)) list.Add(new(a.JobName, a.Reason, $"/jobs/{a.JobId}", "warn", "Backup job"));
        if (s.UntrustedServers > 0) list.Add(new(s.UntrustedServers == 1 ? "A server's identity isn't confirmed" : $"{s.UntrustedServers} servers' identities aren't confirmed", "Trust the host key before backing it up.", "/servers", "warn", "Servers"));
        var unprotected = d.Kpis.TotalServers - d.Kpis.ProtectedServers;
        if (unprotected > 0) list.Add(new(unprotected == 1 ? "A server has no active backup" : $"{unprotected} servers have no active backup", "Create or resume a backup job so it can be recovered.", "/servers", "warn", "Servers"));
        return list;
    }
}
