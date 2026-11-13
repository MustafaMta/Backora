namespace Backora.Contracts.Security;

/// <summary>Built-in roles. Legacy mapping: "super admin" → PlatformAdministrator, "admin" → BackupAdministrator, "viewer" → Viewer.</summary>
public static class Roles
{
    public const string PlatformAdministrator = "PlatformAdministrator";
    public const string BackupAdministrator = "BackupAdministrator";
    public const string BackupOperator = "BackupOperator";
    public const string RestoreOperator = "RestoreOperator";
    public const string Auditor = "Auditor";
    public const string Viewer = "Viewer";

    public static readonly IReadOnlyList<string> All =
        [PlatformAdministrator, BackupAdministrator, BackupOperator, RestoreOperator, Auditor, Viewer];

    public static string DisplayName(string role) => role switch
    {
        PlatformAdministrator => "Platform Administrator",
        BackupAdministrator => "Backup Administrator",
        BackupOperator => "Backup Operator",
        RestoreOperator => "Restore Operator",
        Auditor => "Auditor",
        Viewer => "Read-only Viewer",
        _ => role,
    };

    public static string Describe(string role) => role switch
    {
        PlatformAdministrator => "Full control including users, roles, security and platform settings.",
        BackupAdministrator => "Manages servers, credentials, templates, jobs, storage and retention.",
        BackupOperator => "Runs and cancels backups and monitors operations.",
        RestoreOperator => "Executes restores from recovery points and monitors operations.",
        Auditor => "Read-only access to operations plus the security audit trail.",
        Viewer => "Read-only access to operational status and history.",
        _ => string.Empty,
    };
}

/// <summary>Fine-grained permissions enforced by backend authorization policies.</summary>
public static class Permissions
{
    public const string DashboardRead = "dashboard.read";
    public const string ServersRead = "servers.read";
    public const string ServersWrite = "servers.write";
    public const string ServersTest = "servers.test";
    public const string CredentialsManage = "credentials.manage";
    public const string TemplatesRead = "templates.read";
    public const string TemplatesWrite = "templates.write";
    public const string JobsRead = "jobs.read";
    public const string JobsWrite = "jobs.write";
    public const string JobsRun = "jobs.run";
    public const string RunsRead = "runs.read";
    public const string RunsCancel = "runs.cancel";
    public const string RunsReadLogs = "runs.logs";
    public const string RecoveryPointsRead = "recovery.read";
    public const string RecoveryPointsProtect = "recovery.protect";
    public const string RestoresRead = "restores.read";
    public const string RestoresExecute = "restores.execute";
    public const string StorageRead = "storage.read";
    public const string StorageWrite = "storage.write";
    public const string SettingsRead = "settings.read";
    public const string SettingsWrite = "settings.write";
    public const string UsersRead = "users.read";
    public const string UsersManage = "users.manage";
    public const string AuditRead = "audit.read";

    public static readonly IReadOnlyList<string> All =
    [
        DashboardRead, ServersRead, ServersWrite, ServersTest, CredentialsManage, TemplatesRead, TemplatesWrite,
        JobsRead, JobsWrite, JobsRun, RunsRead, RunsCancel, RunsReadLogs, RecoveryPointsRead, RecoveryPointsProtect,
        RestoresRead, RestoresExecute, StorageRead, StorageWrite, SettingsRead, SettingsWrite, UsersRead, UsersManage, AuditRead,
    ];
}

public static class RolePermissions
{
    private static readonly string[] ReadOnly =
    [
        Permissions.DashboardRead, Permissions.ServersRead, Permissions.TemplatesRead, Permissions.JobsRead,
        Permissions.RunsRead, Permissions.RecoveryPointsRead, Permissions.RestoresRead, Permissions.StorageRead,
    ];

    public static readonly IReadOnlyDictionary<string, IReadOnlySet<string>> Matrix = new Dictionary<string, IReadOnlySet<string>>
    {
        [Roles.PlatformAdministrator] = new HashSet<string>(Permissions.All),
        [Roles.BackupAdministrator] = new HashSet<string>(ReadOnly.Concat(
        [
            Permissions.ServersWrite, Permissions.ServersTest, Permissions.CredentialsManage, Permissions.TemplatesWrite,
            Permissions.JobsWrite, Permissions.JobsRun, Permissions.RunsCancel, Permissions.RunsReadLogs,
            Permissions.RecoveryPointsProtect, Permissions.RestoresExecute, Permissions.StorageWrite, Permissions.SettingsRead,
            Permissions.SettingsWrite, Permissions.UsersRead,
        ])),
        [Roles.BackupOperator] = new HashSet<string>(ReadOnly.Concat(
        [
            Permissions.JobsRun, Permissions.RunsCancel, Permissions.RunsReadLogs, Permissions.ServersTest,
        ])),
        [Roles.RestoreOperator] = new HashSet<string>(ReadOnly.Concat(
        [
            Permissions.RestoresExecute, Permissions.RunsReadLogs,
        ])),
        [Roles.Auditor] = new HashSet<string>(ReadOnly.Concat(
        [
            Permissions.AuditRead, Permissions.RunsReadLogs, Permissions.SettingsRead, Permissions.UsersRead,
        ])),
        [Roles.Viewer] = new HashSet<string>(ReadOnly),
    };

    public static IReadOnlySet<string> For(IEnumerable<string> roles)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var r in roles)
        {
            if (Matrix.TryGetValue(r, out var perms)) set.UnionWith(perms);
        }
        return set;
    }
}
