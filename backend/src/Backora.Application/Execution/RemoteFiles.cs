using System.Globalization;
using System.Text;
using Backora.Application.Abstractions;
using Backora.Domain;
using Backora.Domain.Execution;

namespace Backora.Application.Execution;

public sealed record ScannedFile(string RelativePath, long SizeBytes, string Sha256, DateTimeOffset ModifiedAt, string? Md5 = null);

/// <summary>Remote file inventory and integrity operations executed on the worker (which mounts the NFS share).</summary>
public static class RemoteFiles
{
    /// <summary>
    /// Lists files under <paramref name="directory"/> modified within the last <paramref name="sinceMinutes"/>
    /// minutes (excluding <c>restore-*</c> staging folders), with size, mtime and SHA-256. Paths are base64
    /// encoded remotely so names containing tabs/newlines are handled safely.
    /// </summary>
    public static async Task<IReadOnlyList<ScannedFile>?> ScanAsync(IRemoteSession worker, string directory, int sinceMinutes, CancellationToken ct)
    {
        var q = ShellQuote.Quote(directory);
        var script =
            $"cd {q} 2>/dev/null || {{ echo NODIR; exit 0; }}; " +
            $"find . -path './restore-*' -prune -o -type f -mmin -{Math.Max(1, sinceMinutes)} -print | while IFS= read -r f; do " +
            "s=$(stat -c '%s %Y' \"$f\") || continue; h=$(sha256sum \"$f\" | cut -d' ' -f1) || continue; " +
            "n=$(printf '%s' \"${f#./}\" | base64 | tr -d '\\n'); printf '%s\\t%s\\t%s\\n' \"$h\" \"$s\" \"$n\"; done; echo END";
        var result = await worker.RunAsync(script, TimeSpan.FromHours(6), ct);
        if (result.StdOut.Contains("NODIR", StringComparison.Ordinal)) return null;
        if (!result.StdOut.Contains("END", StringComparison.Ordinal))
        {
            throw new RemoteOperationException(FailureClass.VerificationFailed, $"Artifact scan did not complete: {DetachedProcess.Trim(result.StdErr)}");
        }
        return Parse(result.StdOut);
    }

    /// <summary>Computes SHA-256 for explicit relative paths (used to verify restored files).</summary>
    public static async Task<IReadOnlyList<ScannedFile>> HashAsync(IRemoteSession worker, string directory, IEnumerable<string> relativePaths, CancellationToken ct)
    {
        var list = relativePaths.ToList();
        if (list.Count == 0) return [];
        var q = ShellQuote.Quote(directory);
        var sb = new StringBuilder($"cd {q} || exit 3; for f in");
        foreach (var p in list) sb.Append(' ').Append(ShellQuote.Quote(p));
        sb.Append("; do if [ -f \"$f\" ]; then s=$(stat -c '%s %Y' \"$f\"); h=$(sha256sum \"$f\" | cut -d' ' -f1); " +
                  "n=$(printf '%s' \"$f\" | base64 | tr -d '\\n'); printf '%s\\t%s\\t%s\\n' \"$h\" \"$s\" \"$n\"; fi; done; echo END");
        var result = await worker.RunAsync(sb.ToString(), TimeSpan.FromHours(6), ct);
        if (result.ExitCode == 3) throw new RemoteOperationException(FailureClass.VerificationFailed, $"Directory {directory} does not exist.");
        return Parse(result.StdOut);
    }

    /// <summary>
    /// Deletes the listed files (relative to <paramref name="directory"/>) — never globs, never the directory itself.
    /// Sub-folders that held them (a backup kept as a folder, e.g. pg_basebackup base.tar + pg_wal.tar) are removed
    /// only when they are empty afterwards (rmdir never removes a folder with anything left in it).
    /// </summary>
    public static async Task<CommandResult> DeleteFilesAsync(IRemoteSession worker, string directory, IEnumerable<string> relativePaths, CancellationToken ct)
    {
        var sb = new StringBuilder($"cd {ShellQuote.Quote(directory)} || exit 3; rm -f --");
        var folders = new SortedSet<string>(Comparer<string>.Create((a, b) => b.Length != a.Length ? b.Length.CompareTo(a.Length) : string.CompareOrdinal(a, b)));
        foreach (var p in relativePaths)
        {
            if (p.StartsWith('/') || p.Split('/').Any(s => s is ".." or ".")) throw new ArgumentException($"Unsafe relative path '{p}'.");
            sb.Append(' ').Append(ShellQuote.Quote(p));
            for (var i = p.LastIndexOf('/'); i > 0; i = p.LastIndexOf('/', i - 1)) folders.Add(p[..i]);
        }
        if (folders.Count > 0)
        {
            // Deepest first, so a parent becomes empty before it is tried; keep rm's exit code.
            sb.Append("; rc=$?; rmdir --");
            foreach (var f in folders) sb.Append(' ').Append(ShellQuote.Quote(f));
            sb.Append(" 2>/dev/null; exit $rc");
        }
        return await worker.RunAsync(sb.ToString(), TimeSpan.FromMinutes(10), ct);
    }

    public static async Task<long> EpochAsync(IRemoteSession session, CancellationToken ct)
    {
        var r = await session.RunAsync("date +%s", TimeSpan.FromSeconds(20), ct);
        return long.TryParse(r.StdOut.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var e) ? e : DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    }

    public static List<ScannedFile> Parse(string output)
    {
        var files = new List<ScannedFile>();
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Split('\t');
            if (parts.Length is < 3 or > 4 || parts[0].Length != 64) continue;
            var sizeMtime = parts[1].Split(' ');
            if (sizeMtime.Length != 2 || !long.TryParse(sizeMtime[0], out var size) || !long.TryParse(sizeMtime[1], out var mtime)) continue;
            string path;
            try { path = Encoding.UTF8.GetString(Convert.FromBase64String(parts[2])); }
            catch (FormatException) { continue; }
            var md5 = parts.Length == 4 && parts[3].Length == 32 && parts[3].All(Uri.IsHexDigit) ? parts[3].ToLowerInvariant() : null;
            files.Add(new ScannedFile(path, size, parts[0].ToLowerInvariant(), DateTimeOffset.FromUnixTimeSeconds(mtime), md5));
        }
        return files;
    }
}

/// <summary>rclone invocation helpers. Credentials go into a 0600 config file inside a 0700 directory, never on the command line.</summary>
public static class Rclone
{
    public const string RemoteName = "backora";
    public const string ReplicaSourceRemote = "backora-src";
    public const string ReplicaTargetRemote = "backora-dst";

    /// <summary>True when two connections reach the same storage account, so rclone can copy server-side.</summary>
    public static bool SameAccount(S3Connection a, S3Connection b) =>
        a.IsLocal || b.IsLocal ? a.IsLocal && b.IsLocal   // two folders on the execution host: one local remote serves both
        : string.Equals(a.Endpoint.TrimEnd('/'), b.Endpoint.TrimEnd('/'), StringComparison.OrdinalIgnoreCase)
        && string.Equals(a.Region, b.Region, StringComparison.Ordinal)
        && string.Equals(a.AccessKeyId, b.AccessKeyId, StringComparison.Ordinal)
        && string.Equals(a.SecretKey, b.SecretKey, StringComparison.Ordinal)
        && a.ForcePathStyle == b.ForcePathStyle;

    public static string BuildConfig(S3Connection c, string remoteName = RemoteName)
    {
        if (c.IsLocal)
        {
            // A folder on the execution host (a mounted NFS share): rclone's local backend, no credentials.
            return $"""
                [{remoteName}]
                type = local

                """;
        }
        static string Clean(string v, string field) =>
            v.Contains('\n', StringComparison.Ordinal) || v.Contains('\r', StringComparison.Ordinal)
                ? throw new RemoteOperationException(FailureClass.Configuration, $"S3 {field} contains a line break.")
                : v.Trim();
        return $"""
            [{remoteName}]
            type = s3
            provider = Other
            access_key_id = {Clean(c.AccessKeyId, "access key")}
            secret_access_key = {Clean(c.SecretKey, "secret key")}
            region = {Clean(c.Region, "region")}
            endpoint = {Clean(c.Endpoint, "endpoint")}
            force_path_style = {(c.ForcePathStyle ? "true" : "false")}
            location_constraint =
            no_check_bucket = true

            """;
    }

    public static string RemotePath(S3Connection c, string prefix, string remoteName = RemoteName) =>
        c.IsLocal ? $"{remoteName}:{ShellQuote.Join(c.LocalRoot!, prefix)}{(prefix.EndsWith('/') ? "/" : "")}" : $"{remoteName}:{c.Bucket}/{prefix.TrimStart('/')}";

    /// <summary>Common flags: JSON stats for real progress, checksums for integrity, bounded retries.</summary>
    public static string CommonFlags(string configPath) =>
        $"--config {ShellQuote.Quote(configPath)} --use-json-log --stats 5s --stats-log-level NOTICE -v --retries 3 --low-level-retries 10 --contimeout 30s --timeout 300s";

    public sealed record Stats(long Bytes, long TotalBytes, int Transfers, int TotalTransfers, int Errors);

    /// <summary>Parses the last rclone JSON stats line from a log chunk.</summary>
    public static Stats? ParseLatestStats(string log)
    {
        Stats? latest = null;
        foreach (var line in log.Split('\n'))
        {
            if (!line.Contains("\"stats\"", StringComparison.Ordinal)) continue;
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(line);
                if (!doc.RootElement.TryGetProperty("stats", out var s)) continue;
                long L(string n) => s.TryGetProperty(n, out var v) && v.TryGetInt64(out var x) ? x : 0;
                latest = new Stats(L("bytes"), L("totalBytes"), (int)L("transfers"), (int)L("totalTransfers"), (int)L("errors"));
            }
            catch (System.Text.Json.JsonException) { /* partial line */ }
        }
        return latest;
    }

    /// <summary>Extracts human-readable error lines from rclone JSON logs for failure messages.</summary>
    public static string ExtractErrors(string log, int max = 5)
    {
        var errors = new List<string>();
        foreach (var line in log.Split('\n'))
        {
            if (!line.Contains("\"level\":\"error\"", StringComparison.Ordinal) && !line.Contains("ERROR", StringComparison.Ordinal)) continue;
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(line);
                errors.Add(doc.RootElement.TryGetProperty("msg", out var m) ? m.GetString() ?? line : line);
            }
            catch (System.Text.Json.JsonException) { errors.Add(line.Trim()); }
        }
        return string.Join(" | ", errors.TakeLast(max));
    }
}
