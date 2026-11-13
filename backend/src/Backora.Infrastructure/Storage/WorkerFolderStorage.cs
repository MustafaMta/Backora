using System.Globalization;
using Backora.Application.Abstractions;
using Backora.Application.Execution;
using Backora.Contracts.V1;
using Backora.Domain.Execution;
using Microsoft.Extensions.DependencyInjection;

namespace Backora.Infrastructure.Storage;

/// <summary>
/// A storage site that is a folder on the execution host (typically a mounted NFS share). Listing, deleting and testing
/// run there over SSH; copies, checks and restores use rclone's local backend like any other site. Keys are paths
/// relative to the folder, exactly like object keys in a bucket. Every path is validated and quoted, and deletes never
/// leave the folder.
/// </summary>
public sealed class WorkerFolderStorage(IServiceScopeFactory scopes, ISshConnector ssh)
{
    private static readonly TimeSpan ListTimeout = TimeSpan.FromMinutes(10);

    private async Task<T> OnWorkerAsync<T>(Func<IRemoteSession, Task<T>> action, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var env = await scope.ServiceProvider.GetRequiredService<ExecutionEnvironmentFactory>().CreateAsync(ct);
        await using var session = await ssh.ConnectWorkerAsync(env.Worker, ct);
        return await action(session);
    }

    private static string Root(S3Connection c) =>
        c.LocalRoot is { Length: > 1 } r && ShellQuote.IsSafeAbsolutePath(r) ? r.TrimEnd('/') : throw new InvalidOperationException("The storage folder is not a safe absolute path.");

    /// <summary>A key prefix as a relative path: no leading slash, no traversal.</summary>
    private static string Rel(string prefix)
    {
        var p = (prefix ?? string.Empty).TrimStart('/');
        if (p.Contains('\0', StringComparison.Ordinal) || p.Split('/').Any(s => s is "." or "..")) throw new ArgumentException("Path traversal in the key prefix.", nameof(prefix));
        return p;
    }

    /// <summary>
    /// Every file below ROOT/DIR as "relative path TAB size TAB mtime" (nothing when the folder doesn't exist). With a
    /// <paramref name="limit"/>, at most that many entries within <paramref name="seconds"/>: a share that also holds a
    /// large tree of other data must not stall a browse (the caller then reports the totals as incomplete).
    /// </summary>
    public static string FilesCommand(string root, string dir, int? limit = null, int seconds = 20) =>
        $"cd {ShellQuote.Quote(ShellQuote.Join(root, dir))} 2>/dev/null || exit 0; " +
        (limit is { } n
            ? $"timeout {seconds} find . -type f -printf '%P\\t%s\\t%T@\\n' 2>/dev/null | head -n {n.ToString(CultureInfo.InvariantCulture)}; true"
            : "find . -type f -printf '%P\\t%s\\t%T@\\n'");

    /// <summary>The direct sub-folders ("D TAB name") and files ("F TAB name TAB size TAB mtime") of ROOT/DIR.</summary>
    public static string ChildrenCommand(string root, string dir) =>
        $"cd {ShellQuote.Quote(ShellQuote.Join(root, dir))} 2>/dev/null || exit 0; find . -mindepth 1 -maxdepth 1 \\( -type d -printf 'D\\t%f\\n' \\) -o \\( -type f -printf 'F\\t%f\\t%s\\t%T@\\n' \\)";

    /// <summary>Deletes ROOT/REL (a folder or a file) and prints how many files went. REL is never empty.</summary>
    public static string DeleteCommand(string root, string rel)
    {
        if (rel.Trim('/').Length == 0) throw new ArgumentException("Refusing to delete the whole storage folder.", nameof(rel));
        var target = ShellQuote.Quote(ShellQuote.Join(root, rel));
        return $"if [ -d {target} ]; then n=$(find {target} -type f | wc -l); rm -rf -- {target} || exit 1; echo \"$n\"; " +
               $"elif [ -f {target} ]; then rm -f -- {target} || exit 1; echo 1; else echo 0; fi";
    }

    /// <summary>Exists? mount source, mount point?, write/read/delete probe, size and free bytes.</summary>
    public static string TestCommand(string root)
    {
        var q = ShellQuote.Quote(root);
        return $"[ -d {q} ] || {{ echo NODIR; exit 0; }}; " +
               $"echo \"MOUNT $(findmnt -n -o SOURCE,FSTYPE --target {q} 2>/dev/null | head -n 1)\"; " +
               $"mountpoint -q {q} && echo ISMOUNT || echo NOTMOUNT; " +
               $"f={q}/.backora-probe.$$; if printf backora > \"$f\" 2>/dev/null && [ \"$(cat \"$f\" 2>/dev/null)\" = backora ] && rm -f -- \"$f\"; then echo RW; else rm -f -- \"$f\" 2>/dev/null; echo NORW; fi; " +
               $"df -P -B1 {q} 2>/dev/null | awk 'NR == 2 {{ print \"DF \" $2 \" \" $4 }}'";
    }

    public static List<StoredObject> ParseFiles(string output, string keyPrefix)
    {
        var result = new List<StoredObject>();
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var p = line.Split('\t');
            if (p.Length < 3 || !long.TryParse(p[1], NumberStyles.None, CultureInfo.InvariantCulture, out var size)) continue;
            var seconds = double.TryParse(p[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var s) ? s : 0;
            result.Add(new StoredObject(keyPrefix + p[0], size, DateTimeOffset.FromUnixTimeMilliseconds((long)(seconds * 1000))));
        }
        return result;
    }

    /// <summary>Every file whose key starts with <paramref name="prefix"/> (recursive).</summary>
    public Task<IReadOnlyList<StoredObject>> ListAsync(S3Connection c, string prefix, CancellationToken ct) => OnWorkerAsync<IReadOnlyList<StoredObject>>(async w =>
    {
        var rel = Rel(prefix);
        var dir = rel.Contains('/') ? rel[..(rel.LastIndexOf('/') + 1)] : string.Empty;   // the folder the prefix lives in
        var r = await w.RunAsync(FilesCommand(Root(c), dir), ListTimeout, ct);
        if (!r.Succeeded) throw new InvalidOperationException($"Listing {Root(c)} failed: {r.StdErr.Trim()}");
        return ParseFiles(r.StdOut, dir).Where(o => o.Key.StartsWith(rel, StringComparison.Ordinal)).OrderBy(o => o.Key, StringComparer.Ordinal).ToList();
    }, ct);

    /// <summary>One page of a folder: its sub-folders and files, or (recursive) every file below it. The token is an offset.</summary>
    public Task<S3BrowsePage> BrowseAsync(S3Connection c, string prefix, string? token, int maxKeys, bool recursive, CancellationToken ct) => OnWorkerAsync(async w =>
    {
        var rel = Rel(prefix);
        if (rel.Length > 0 && !rel.EndsWith('/')) rel += "/";
        var offset0 = int.TryParse(token, NumberStyles.None, CultureInfo.InvariantCulture, out var o0) ? o0 : 0;
        // Recursive (totals, search): only as far as this page needs, plus one entry to know there is more.
        var r = await w.RunAsync(recursive ? FilesCommand(Root(c), rel, offset0 + Math.Max(1, maxKeys) + 1) : ChildrenCommand(Root(c), rel), ListTimeout, ct);
        if (!r.Succeeded) throw new InvalidOperationException($"Listing {Root(c)} failed: {r.StdErr.Trim()}");
        var folders = new List<string>();
        List<StoredObject> files;
        if (recursive) files = ParseFiles(r.StdOut, rel);
        else
        {
            files = [];
            foreach (var line in r.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                if (line.StartsWith("D\t", StringComparison.Ordinal)) folders.Add(rel + line[2..] + "/");
                else if (line.StartsWith("F\t", StringComparison.Ordinal)) files.AddRange(ParseFiles(line[2..], rel));
            }
        }
        folders.Sort(StringComparer.Ordinal);
        files.Sort((a, b) => string.CompareOrdinal(a.Key, b.Key));
        var offset = int.TryParse(token, NumberStyles.None, CultureInfo.InvariantCulture, out var o) ? o : 0;
        var entries = folders.Select(f => (Folder: (string?)f, File: (StoredObject?)null)).Concat(files.Select(f => (Folder: (string?)null, File: (StoredObject?)f))).ToList();
        var page = entries.Skip(offset).Take(Math.Max(1, maxKeys)).ToList();
        var next = offset + page.Count < entries.Count ? (offset + page.Count).ToString(CultureInfo.InvariantCulture) : null;
        return new S3BrowsePage(page.Where(e => e.Folder is not null).Select(e => e.Folder!).ToList(), page.Where(e => e.File is not null).Select(e => e.File!).ToList(), next);
    }, ct);

    /// <summary>Deletes everything under <paramref name="prefix"/> (a recovery point's folder). Never the site's root, never outside it.</summary>
    public Task<int> DeletePrefixAsync(S3Connection c, string prefix, CancellationToken ct) => OnWorkerAsync(async w =>
    {
        var rel = Rel(prefix).TrimEnd('/');
        var r = await w.RunAsync(DeleteCommand(Root(c), rel), ListTimeout, ct);
        if (!r.Succeeded) throw new InvalidOperationException($"Deleting {rel} in {Root(c)} failed: {r.StdErr.Trim()}");
        return int.TryParse(r.StdOut.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : 0;
    }, ct);

    /// <summary>The folder exists, is a mount (not the execution host's own disk), can be written, read and deleted, and has room.</summary>
    public async Task<IReadOnlyList<CheckResultDto>> TestAsync(S3Connection c, CancellationToken ct)
    {
        var root = Root(c);
        try
        {
            return await OnWorkerAsync<IReadOnlyList<CheckResultDto>>(async w =>
            {
                var r = await w.RunAsync(TestCommand(root), TimeSpan.FromMinutes(1), ct);
                return ParseTest(r.StdOut, root);
            }, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return [new CheckResultDto("Execution host", CheckState.Failed, $"Couldn't reach the execution host: {ex.Message}")];
        }
    }

    /// <summary>Checks from <see cref="TestCommand"/>'s output.</summary>
    public static IReadOnlyList<CheckResultDto> ParseTest(string output, string root)
    {
        var checks = new List<CheckResultDto>();
        var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (lines.Contains("NODIR"))
        {
            checks.Add(new CheckResultDto("Folder", CheckState.Failed, $"{root} does not exist on the execution host. Mount the storage there first."));
            return checks;
        }
        checks.Add(new CheckResultDto("Folder", CheckState.Passed, $"{root} exists on the execution host."));
        var mount = lines.FirstOrDefault(l => l.StartsWith("MOUNT ", StringComparison.Ordinal))?[6..].Trim() ?? string.Empty;
        checks.Add(lines.Contains("ISMOUNT")
            ? new CheckResultDto("Mounted", CheckState.Passed, $"Mounted from {mount}.")
            : new CheckResultDto("Mounted", CheckState.Warning,
                $"{root} is not a mount point{(mount.Length > 0 ? $" (it is on {mount})" : "")}: copies would land on the execution host's own disk. Mount the storage on it."));
        checks.Add(lines.Contains("RW")
            ? new CheckResultDto("Write, read, delete", CheckState.Passed, "A test file was written, read back and deleted.")
            : new CheckResultDto("Write, read, delete", CheckState.Failed, $"Cannot write to {root} (read-only mount or permissions)."));
        if (lines.FirstOrDefault(l => l.StartsWith("DF ", StringComparison.Ordinal))?.Split(' ') is [_, var sizeText, var freeText]
            && long.TryParse(sizeText, out var size) && long.TryParse(freeText, out var free) && size > 0)
        {
            var pct = free * 100.0 / size;
            checks.Add(new CheckResultDto("Free space", pct < 10 ? CheckState.Warning : CheckState.Passed,
                $"{Gb(free)} free of {Gb(size)} ({pct.ToString("0", CultureInfo.InvariantCulture)}%)."));
        }
        return checks;

        static string Gb(long b) => b >= 1L << 40 ? $"{(b / (double)(1L << 40)).ToString("0.0", CultureInfo.InvariantCulture)} TB" : $"{(b / (double)(1L << 30)).ToString("0.0", CultureInfo.InvariantCulture)} GB";
    }
}

/// <summary>Sends each storage call to the site's kind: the S3 API for buckets, the execution host for folders.</summary>
public sealed class RoutingObjectStorage(S3ObjectStorage s3, WorkerFolderStorage folders) : IObjectStorage
{
    public Task<IReadOnlyList<StoredObject>> ListAsync(S3Connection c, string prefix, CancellationToken ct) =>
        c.IsLocal ? folders.ListAsync(c, prefix, ct) : s3.ListAsync(c, prefix, ct);

    public Task<S3BrowsePage> BrowseAsync(S3Connection c, string prefix, string? token, int maxKeys, bool recursive, CancellationToken ct) =>
        c.IsLocal ? folders.BrowseAsync(c, prefix, token, maxKeys, recursive, ct) : s3.BrowseAsync(c, prefix, token, maxKeys, recursive, ct);

    public Task<int> DeletePrefixAsync(S3Connection c, string prefix, CancellationToken ct) =>
        c.IsLocal ? folders.DeletePrefixAsync(c, prefix, ct) : s3.DeletePrefixAsync(c, prefix, ct);

    public Task<IReadOnlyList<CheckResultDto>> TestAsync(S3Connection c, CancellationToken ct) =>
        c.IsLocal ? folders.TestAsync(c, ct) : s3.TestAsync(c, ct);
}
