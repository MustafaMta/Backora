using System.Text.RegularExpressions;
using Backora.Application.Abstractions;
using Backora.Domain;
using Backora.Domain.Execution;
using Backora.Domain.Infrastructure;

namespace Backora.Application.Storage;

/// <summary>Builds the connection for a storage site, S3 bucket or folder on the execution host.</summary>
public static partial class StorageConnections
{
    public static S3Connection For(S3Destination d, ISecretProtector secrets) => d.Kind == StorageKind.Filesystem
        ? new S3Connection(string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, false,
            d.LocalPath is { Length: > 0 } p ? p.TrimEnd('/') : throw new RemoteOperationException(FailureClass.Configuration, $"Storage site '{d.Name}' has no folder configured."))
        : new S3Connection(d.Endpoint, d.Region, d.Bucket, d.AccessKeyId, secrets.Unprotect(d.SecretKeyCiphertext), d.ForcePathStyle);

    /// <summary>Where a key prefix lives, for logs: s3://bucket/prefix, or the folder path on the execution host.</summary>
    public static string Describe(S3Destination? d, string prefix) =>
        d is { Kind: StorageKind.Filesystem, LocalPath: { } path } ? $"{path.TrimEnd('/')}/{prefix.TrimStart('/')}" : $"s3://{d?.Bucket}/{prefix}";

    /// <summary>Endpoint recorded for a filesystem site, so "same target" comparisons treat two sites on one folder as one.</summary>
    public static string LocalEndpoint(string path) => "file://" + path.TrimEnd('/');

    // Top-level folders of the execution host that are never a storage site.
    private static readonly HashSet<string> SystemRoots = new(StringComparer.Ordinal)
        { "bin", "boot", "dev", "etc", "lib", "lib64", "proc", "root", "run", "sbin", "sys", "usr", "var", "tmp" };

    [GeneratedRegex(@"^/[A-Za-z0-9._/-]+$", RegexOptions.CultureInvariant)]
    private static partial Regex SafePath();

    /// <summary>
    /// Why a folder can't be a storage site, or null when it can: an absolute, plain path (letters, digits, . _ - /), not a
    /// system folder, and not the staging share the backups are written to (a copy there would be no copy at all).
    /// </summary>
    public static string? LocalPathProblem(string? path, string? stagingPath)
    {
        if (string.IsNullOrWhiteSpace(path)) return "Enter the folder on the execution host, for example /mnt/dr-site.";
        var p = path.Trim().TrimEnd('/');
        if (p.Length == 0 || !SafePath().IsMatch(p) || !ShellQuote.IsSafeAbsolutePath(p) || p.Contains("//", StringComparison.Ordinal))
            return "Use an absolute path with letters, digits, '.', '_', '-' and '/' only, for example /mnt/dr-site.";
        var segments = p.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0 || SystemRoots.Contains(segments[0])) return "That is a system folder of the execution host; use the folder the storage is mounted on.";
        if (stagingPath is { Length: > 0 } s)
        {
            var staging = s.TrimEnd('/');
            if (p == staging || p.StartsWith(staging + "/", StringComparison.Ordinal) || staging.StartsWith(p + "/", StringComparison.Ordinal))
                return $"That folder overlaps the staging share ({staging}) the backups are written to; a copy there would not be a separate copy.";
        }
        return null;
    }
}
