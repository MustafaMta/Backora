using Backora.Contracts.Security;

namespace Backora.Web.Components.Layout;

/// <summary>
/// Primary navigation. Secondary screens (history, restores, transfers, variables, audit, account) are nested
/// under their area and shown when that area is open, so they stay discoverable without crowding the sidebar.
/// </summary>
public static class Nav
{
    public sealed record Sub(string Label, string Href, string? Permission);
    public sealed record Item(string Group, string Label, string Href, string Icon, string? Permission, Sub[]? Subs = null);

    public const string Workspace = "Protection";

    public static readonly IReadOnlyList<Item> Items =
    [
        new(Workspace, "Overview", "/", "dashboard", Permissions.DashboardRead),
        new(Workspace, "Backup jobs", "/jobs", "jobs", Permissions.JobsRead, [new("Run history", "/runs", Permissions.RunsRead)]),
        new(Workspace, "Recovery", "/recovery", "recovery", Permissions.RecoveryPointsRead, [new("Restores", "/restores", Permissions.RestoresRead)]),
        new("Infrastructure", "Protected servers", "/servers", "server", Permissions.ServersRead),
        new("Infrastructure", "Storage & DR", "/storage", "topology", Permissions.StorageRead, [new("Transfers", "/transfers", Permissions.RecoveryPointsRead), new("Stored files", "/files", Permissions.StorageRead)]),
        new("Infrastructure", "Execution host", "/execution-host", "execution-host", Permissions.SettingsRead),
        new("Management", "Templates", "/templates", "template", Permissions.TemplatesRead, [new("Variables", "/variables", Permissions.TemplatesRead)]),
        new("Management", "Users & access", "/users", "users", Permissions.UsersRead, [new("Audit log", "/audit", Permissions.AuditRead)]),
        new("Management", "Settings", "/settings", "settings", Permissions.SettingsRead, [new("Your account", "/account", null)]),
    ];

    private static bool Under(string path, string href) =>
        href == "/" ? path == "/" : path.Equals(href, StringComparison.OrdinalIgnoreCase) || path.StartsWith(href + "/", StringComparison.OrdinalIgnoreCase);

    private static string Clean(string relativePath) => "/" + relativePath.Split('?', '#')[0].Trim('/');

    /// <summary>The primary item that owns a path (its own route or one of its sub routes).</summary>
    public static Item? Match(string relativePath)
    {
        var path = Clean(relativePath);
        return Items.SelectMany(i => new[] { i.Href }.Concat(i.Subs?.Select(s => s.Href) ?? []).Select(p => (i, p)))
            .Where(x => Under(path, x.p)).OrderByDescending(x => x.p.Length).Select(x => x.i).FirstOrDefault();
    }

    /// <summary>The sub entry the path is on, if any.</summary>
    public static Sub? MatchSub(string relativePath)
    {
        var path = Clean(relativePath);
        return Items.SelectMany(i => i.Subs ?? []).Where(s => Under(path, s.Href)).OrderByDescending(s => s.Href.Length).FirstOrDefault();
    }

    /// <summary>The account page also lives under Settings, but anyone may open it.</summary>
    public static bool IsAccount(string relativePath) => Under(Clean(relativePath), "/account");
}
