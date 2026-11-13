using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Backora.Contracts.Security;
using Backora.Domain;
using Backora.Domain.Backups;
using Backora.Domain.Scheduling;

namespace Backora.Tools.Legacy;

/// <summary>Pure, unit-tested translation rules from the Laravel schema to the Backora 2 domain.</summary>
public static partial class LegacyMapping
{
    public const string IncompleteRunMessage = "Imported: never completed in legacy system";
    public const string IncompleteTransferMessage = "Imported: incomplete in legacy system";
    public const string MissingTemplateScript = "#!/bin/bash\necho \"Legacy template missing\" >&2\nexit 1\n";

    /// <summary>Legacy role_name → Backora role. Unknown/null roles get least privilege (Viewer).</summary>
    public static (string Role, bool Defaulted) MapRole(string? legacyRole) => legacyRole?.Trim().ToLowerInvariant() switch
    {
        "super admin" => (Roles.PlatformAdministrator, false),
        "admin" => (Roles.BackupAdministrator, false),
        "viewer" => (Roles.Viewer, false),
        _ => (Roles.Viewer, true),
    };

    public static bool IsActiveUserStatus(string? status) =>
        status is null || !(status.Trim().Equals("Inactive", StringComparison.OrdinalIgnoreCase)
                            || status.Trim().Equals("Disable", StringComparison.OrdinalIgnoreCase)
                            || status.Trim().Equals("Disabled", StringComparison.OrdinalIgnoreCase));

    /// <summary>Variables whose name suggests a credential are imported as encrypted secrets.</summary>
    public static bool IsSecretVariableName(string name) => SecretNameRegex().IsMatch(name);

    public sealed record RunStatusMapping(RunStatus Status, FailureClass FailureClass, string? Message);

    /// <summary>
    /// Active legacy states are never imported as active (that would make the new worker pick them up);
    /// they become failed/ProcessLost with an explanatory message.
    /// </summary>
    public static RunStatusMapping MapRunStatus(string? legacyStatus) => legacyStatus?.Trim().ToLowerInvariant() switch
    {
        "completed" => new(RunStatus.Completed, FailureClass.None, null),
        "failed" => new(RunStatus.Failed, FailureClass.ScriptError, null),
        "cancelled" => new(RunStatus.Cancelled, FailureClass.Cancelled, null),
        _ => new(RunStatus.Failed, FailureClass.ProcessLost, IncompleteRunMessage),
    };

    public static (OffloadStatus Status, string? Message) MapOffloadStatus(string? legacyStatus) => legacyStatus?.Trim().ToLowerInvariant() switch
    {
        "completed" => (OffloadStatus.Completed, null),
        "failed" => (OffloadStatus.Failed, null),
        _ => (OffloadStatus.Failed, IncompleteTransferMessage),
    };

    public static (RestoreStatus Status, string? Message) MapRestoreStatus(string? legacyStatus) => legacyStatus?.Trim().ToLowerInvariant() switch
    {
        "completed" => (RestoreStatus.Completed, null),
        "failed" => (RestoreStatus.Failed, null),
        "cancelled" => (RestoreStatus.Cancelled, null),
        _ => (RestoreStatus.Failed, IncompleteTransferMessage),
    };

    /// <summary>
    /// Parses legacy sizes: rclone ("1.234 GiB", "500 MiB", "12 KB", "100 B") and du -h ("55M", "5.6G", "0").
    /// Binary multiples for *iB and bare K/M/G/T (du), decimal for KB/MB/GB/TB.
    /// </summary>
    public static long? ParseSize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var m = SizeRegex().Match(text.Trim());
        if (!m.Success) return null;
        if (!decimal.TryParse(m.Groups["n"].Value, NumberStyles.Number, CultureInfo.InvariantCulture, out var n)) return null;
        var unit = m.Groups["u"].Value.ToUpperInvariant();
        decimal multiplier = unit switch
        {
            "" or "B" or "BYTES" => 1m,
            "K" or "KI" or "KIB" => 1024m,
            "M" or "MI" or "MIB" => 1024m * 1024,
            "G" or "GI" or "GIB" => 1024m * 1024 * 1024,
            "T" or "TI" or "TIB" => 1024m * 1024 * 1024 * 1024,
            "P" or "PI" or "PIB" => 1024m * 1024 * 1024 * 1024 * 1024,
            "KB" => 1000m,
            "MB" => 1000m * 1000,
            "GB" => 1000m * 1000 * 1000,
            "TB" => 1000m * 1000 * 1000 * 1000,
            "PB" => 1000m * 1000 * 1000 * 1000 * 1000,
            _ => -1m,
        };
        if (multiplier < 0) return null;
        return (long)Math.Round(n * multiplier);
    }

    public sealed record CronMapping(string? Cron, bool Valid, string? Error);

    public static CronMapping MapCron(string? scheduleAt)
    {
        if (string.IsNullOrWhiteSpace(scheduleAt)) return new(null, true, null);
        var normalized = string.Join(' ', scheduleAt.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        return SchedulePlanner.TryParse(normalized, out _, out var error) ? new(normalized, true, null) : new(null, false, error);
    }

    public sealed record JobStatusMapping(JobStatus Status, IReadOnlyList<string> Reasons);

    /// <summary>A job keeps its legacy status unless something prevents safe execution (then it is imported Inactive).</summary>
    public static JobStatusMapping MapJobStatus(string? legacyStatus, string name, bool cronValid, bool templateResolved)
    {
        var reasons = new List<string>();
        if (!BackupJob.IsValidName(name)) reasons.Add("rename required: name is not a safe path segment");
        if (!cronValid) reasons.Add("invalid cron expression");
        if (!templateResolved) reasons.Add("legacy template missing");
        var wanted = string.Equals(legacyStatus, "active", StringComparison.OrdinalIgnoreCase) ? JobStatus.Active : JobStatus.Inactive;
        return new(reasons.Count > 0 ? JobStatus.Inactive : wanted, reasons);
    }

    /// <summary>Legacy S3 prefix convention: &lt;job&gt;/&lt;Y-m-d-H&gt;/ derived from the upload start time.</summary>
    public static string LegacyPrefix(string jobName, DateTimeOffset startedUtc) =>
        $"{jobName.Trim('/')}/{startedUtc.UtcDateTime:yyyy-MM-dd-HH}/";

    public static string SmtpSecurity(int port, bool hasCredentials) => port switch
    {
        465 => "SslOnConnect",
        25 when !hasCredentials => "None",
        _ => "StartTls",
    };

    public static bool LooksLikePublicKey(string? value) =>
        value is not null && PublicKeyRegex().IsMatch(value.Trim());

    public static bool LooksLikePrivateKey(string? value) =>
        value is not null && value.Contains("PRIVATE KEY-----", StringComparison.Ordinal);

    public static IReadOnlyList<string> ParseRecipients(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind == JsonValueKind.Array)
            {
                return doc.RootElement.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String)
                    .Select(e => e.GetString()!.Trim()).Where(e => e.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            }
            if (doc.RootElement.ValueKind == JsonValueKind.String)
            {
                return SplitList(doc.RootElement.GetString());
            }
        }
        catch (JsonException)
        {
            return SplitList(json);
        }
        return [];
    }

    /// <summary>Extracts the persisted live log from the legacy "output" JSON blob, if any.</summary>
    public static string? ExtractLog(string? output)
    {
        if (string.IsNullOrWhiteSpace(output) || !output.TrimStart().StartsWith('{')) return null;
        try
        {
            using var doc = JsonDocument.Parse(output);
            foreach (var key in new[] { "final_log", "log_content", "live_log" })
            {
                if (doc.RootElement.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(v.GetString())) return v.GetString();
            }
        }
        catch (JsonException) { }
        return null;
    }

    public static string Tail(string? s, int max) => s is null ? string.Empty : s.Length <= max ? s : s[^max..];

    public static string? Truncate(string? s, int max) => s is null ? null : s.Length <= max ? s : s[..max];

    /// <summary>Last path segment of an NFS path (used when an upload lost its run reference).</summary>
    public static string LastSegment(string? path) =>
        string.IsNullOrWhiteSpace(path) ? "unknown" : path.TrimEnd('/').Split('/').LastOrDefault(s => s.Length > 0) ?? "unknown";

    private static List<string> SplitList(string? s) =>
        (s ?? string.Empty).Split([',', ';', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    [GeneratedRegex("(pass|secret|token|key|pwd)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SecretNameRegex();

    [GeneratedRegex(@"^(?<n>\d+(?:\.\d+)?)\s*(?<u>[A-Za-z]*)$", RegexOptions.CultureInvariant)]
    private static partial Regex SizeRegex();

    [GeneratedRegex(@"^(ssh-(rsa|ed25519|dss)|ecdsa-sha2-nistp\d+|sk-\S+)\s+[A-Za-z0-9+/=]+(\s.*)?$", RegexOptions.CultureInvariant)]
    private static partial Regex PublicKeyRegex();
}
