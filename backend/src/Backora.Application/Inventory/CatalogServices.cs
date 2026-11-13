using Backora.Application.Abstractions;
using Backora.Application.Common;
using Backora.Application.Encryption;
using Backora.Application.Validation;
using Backora.Contracts.Security;
using Backora.Contracts.V1;
using Backora.Domain;
using Backora.Domain.Backups;
using Backora.Domain.Execution;
using Backora.Domain.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Backora.Application.Inventory;

public sealed class VariableService(IAppDbContext db, ICurrentUser user, ISecretProtector secrets, IAuditLog audit)
{
    public async Task<IReadOnlyList<VariableDto>> ListAsync(CancellationToken ct)
    {
        var vars = await db.Variables.AsNoTracking().OrderBy(v => v.Name).ToListAsync(ct);
        var scripts = await db.ScriptTemplates.AsNoTracking().Select(t => t.Script).ToListAsync(ct);
        var used = scripts.SelectMany(ScriptRenderer.FindPlaceholders).GroupBy(p => p).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        // Template settings (DB_USER, DB_USER__<JOB> …): counted by the templates that declare the setting.
        var settings = scripts.SelectMany(sc => TemplateSettings.Parse(sc).Select(x => x.Name)).GroupBy(n => n).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        var jobs = await db.BackupJobs.AsNoTracking().Where(j => j.DeletedAt == null).Select(j => j.Name).ToListAsync(ct);
        var jobByKey = jobs.GroupBy(TemplateSettings.JobKey, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        return vars.Select(v =>
        {
            var (setting, key) = TemplateSettings.SplitVariableName(v.Name);
            if (!settings.TryGetValue(setting, out var count)) return ToDto(v, used.GetValueOrDefault(v.Name));
            var job = key is null ? null : jobByKey.GetValueOrDefault(key);
            return ToDto(v, count) with { Setting = setting, AppliesTo = key is null ? "*" : job ?? key, JobFound = key is null || job is not null };
        }).ToList();
    }

    public async Task<VariableDto> CreateAsync(VariableUpsertRequest r, CancellationToken ct)
    {
        if (await db.Variables.AnyAsync(v => v.Name == r.Name, ct)) throw new ConflictException("variable_exists", $"Variable '{r.Name}' already exists.");
        var v = new Variable { Name = r.Name };
        Apply(v, r, isNew: true);
        db.Variables.Add(v);
        await db.SaveChangesAsync(ct);
        audit.Record("variable.created", "Variable", v.Id.ToString(), details: new { v.Name, v.IsSecret });
        await db.SaveChangesAsync(ct);
        return ToDto(v, 0);
    }

    public async Task<VariableDto> UpdateAsync(long id, VariableUpsertRequest r, CancellationToken ct)
    {
        var v = await db.Variables.FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("Variable", id);
        if (v.Name != r.Name && await db.Variables.AnyAsync(x => x.Name == r.Name && x.Id != id, ct))
        {
            throw new ConflictException("variable_exists", $"Variable '{r.Name}' already exists.");
        }
        v.Name = r.Name;
        Apply(v, r, isNew: false);
        audit.Record("variable.updated", "Variable", id.ToString(), details: new { v.Name, v.IsSecret, valueChanged = r.Value is not null });
        await db.SaveChangesAsync(ct);
        return ToDto(v, 0);
    }

    public async Task DeleteAsync(long id, CancellationToken ct)
    {
        var v = await db.Variables.FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("Variable", id);
        var scripts = await db.ScriptTemplates.AsNoTracking().Select(t => new { t.Name, t.Script }).ToListAsync(ct);
        var users = scripts.Where(s => ScriptRenderer.FindPlaceholders(s.Script).Contains(v.Name)).Select(s => s.Name).ToList();
        if (users.Count > 0) throw new ConflictException("variable_in_use", $"Variable is referenced by template(s): {string.Join(", ", users)}.");
        db.Variables.Remove(v);
        audit.Record("variable.deleted", "Variable", id.ToString(), details: new { v.Name });
        await db.SaveChangesAsync(ct);
    }

    private void Apply(Variable v, VariableUpsertRequest r, bool isNew)
    {
        if (r.IsSecret)
        {
            if (!user.HasPermission(Permissions.CredentialsManage)) throw new ForbiddenException("Managing secret variables requires the credentials.manage permission.");
            if (r.Value is not null) v.ValueCiphertext = secrets.Protect(r.Value);
            else if (!v.IsSecret && v.Value is not null) v.ValueCiphertext = secrets.Protect(v.Value); // converting plain → secret keeps value
            else if (isNew) v.ValueCiphertext = secrets.Protect(string.Empty);
            v.Value = null;
        }
        else
        {
            if (v.IsSecret && r.Value is null) throw RequestValidationException.For("value", "Provide the value when converting a secret variable to a plain one.");
            v.Value = r.Value ?? v.Value ?? string.Empty;
            v.ValueCiphertext = null;
        }
        v.IsSecret = r.IsSecret;
        v.Description = r.Description?.Trim();
    }

    private static VariableDto ToDto(Variable v, int used) =>
        new(v.Id, v.Name, v.IsSecret, v.IsSecret ? null : v.Value, v.IsSecret ? v.ValueCiphertext is not null : !string.IsNullOrEmpty(v.Value), v.Description, used, v.UpdatedAt);
}

public sealed class TemplateService(IAppDbContext db, ICurrentUser user, IAuditLog audit)
{
    internal static IReadOnlyList<TemplateSettingDto> SettingsOf(string script) =>
        Backora.Domain.Execution.TemplateSettings.Parse(script).Select(x => new TemplateSettingDto(x.Name, x.Label, x.Required, x.Secret, x.Default, x.Help)).ToList();

    public async Task<IReadOnlyList<TemplateSummaryDto>> ListAsync(string? q, string? category, CancellationToken ct)
    {
        var query = db.ScriptTemplates.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(category)) query = query.Where(t => t.Category == category);
        if (!string.IsNullOrWhiteSpace(q))
        {
            var term = $"%{q.Trim()}%";
            query = query.Where(t => EF.Functions.ILike(t.Name, term) || EF.Functions.ILike(t.Category, term));
        }
        var templates = await query.OrderBy(t => t.Category).ThenBy(t => t.Name).ToListAsync(ct);
        var counts = await JobCountsAsync(ct);
        return templates.Select(t => new TemplateSummaryDto(t.Id, t.Name, t.Category, t.Description, t.Revision, counts.GetValueOrDefault(t.Id), t.UpdatedAt, SettingsOf(t.Script))).ToList();
    }

    public async Task<IReadOnlyList<string>> CategoriesAsync(CancellationToken ct) =>
        await db.ScriptTemplates.AsNoTracking().Select(t => t.Category).Distinct().OrderBy(c => c).ToListAsync(ct);

    public async Task<TemplateDto> GetAsync(long id, CancellationToken ct)
    {
        var t = await db.ScriptTemplates.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("Template", id);
        var counts = await JobCountsAsync(ct);
        var variableNames = await db.Variables.AsNoTracking().Select(v => v.Name).ToListAsync(ct);
        var placeholders = ScriptRenderer.FindPlaceholders(t.Script);
        var unknown = placeholders.Where(p => !Rules.ReservedPlaceholders.Contains(p) && !variableNames.Contains(p)).ToList();
        return new TemplateDto(t.Id, t.Name, t.Category, t.Script, t.Description, t.Revision, placeholders, unknown, counts.GetValueOrDefault(t.Id), t.CreatedAt, t.UpdatedAt, t.Version, SettingsOf(t.Script));
    }

    public async Task<TemplateDto> CreateAsync(TemplateUpsertRequest r, CancellationToken ct)
    {
        await EnsureUniqueAsync(r.Name, r.Category, null, ct);
        var t = new ScriptTemplate { Name = r.Name.Trim(), Category = r.Category.Trim(), Script = Normalize(r.Script), Description = r.Description?.Trim(), UpdatedByUserId = user.UserId };
        db.ScriptTemplates.Add(t);
        await db.SaveChangesAsync(ct);
        audit.Record("template.created", "Template", t.Id.ToString(), details: new { t.Name, t.Category, scriptSha256 = SettingsService.Sha(t.Script) });
        await db.SaveChangesAsync(ct);
        return await GetAsync(t.Id, ct);
    }

    public async Task<TemplateDto> UpdateAsync(long id, TemplateUpsertRequest r, CancellationToken ct)
    {
        var t = await db.ScriptTemplates.FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("Template", id);
        Concurrency.EnsureVersion(t, r.Version);
        await EnsureUniqueAsync(r.Name, r.Category, id, ct);
        var script = Normalize(r.Script);
        var changed = script != t.Script;
        var oldHash = SettingsService.Sha(t.Script);
        t.Name = r.Name.Trim();
        t.Category = r.Category.Trim();
        t.Description = r.Description?.Trim();
        if (changed)
        {
            t.Script = script;
            t.Revision++;
        }
        t.UpdatedByUserId = user.UserId;

        // Keep the denormalised category on jobs aligned (legacy jobs carried the category of their template).
        var jobs = await db.BackupJobs.Where(j => j.ScriptTemplateId == id).ToListAsync(ct);
        foreach (var j in jobs) j.Category = t.Category;

        audit.Record("template.updated", "Template", id.ToString(), details: new { t.Name, t.Category, t.Revision, scriptChanged = changed, oldSha256 = oldHash, newSha256 = SettingsService.Sha(t.Script) });
        await db.SaveChangesAsync(ct);
        return await GetAsync(id, ct);
    }

    public async Task DeleteAsync(long id, CancellationToken ct)
    {
        var t = await db.ScriptTemplates.FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("Template", id);
        var jobs = await db.BackupJobs.CountAsync(j => j.ScriptTemplateId == id, ct);
        if (jobs > 0) throw new ConflictException("template_in_use", $"The template is used by {jobs} backup job(s) (including deleted jobs kept for history).");
        db.ScriptTemplates.Remove(t);
        audit.Record("template.deleted", "Template", id.ToString(), details: new { t.Name, t.Category });
        await db.SaveChangesAsync(ct);
    }

    private async Task<Dictionary<long, int>> JobCountsAsync(CancellationToken ct) =>
        await db.BackupJobs.Where(j => j.DeletedAt == null).GroupBy(j => j.ScriptTemplateId)
            .Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count, ct);

    private async Task EnsureUniqueAsync(string name, string category, long? exceptId, CancellationToken ct)
    {
        var n = name.Trim();
        var c = category.Trim();
        if (await db.ScriptTemplates.AnyAsync(t => t.Name == n && t.Category == c && t.Id != exceptId, ct))
        {
            throw new ConflictException("template_exists", $"A template named '{n}' already exists in category '{c}'.");
        }
    }

    private static string Normalize(string script) => script.Replace("\r\n", "\n", StringComparison.Ordinal);
}

public sealed class StorageService(IAppDbContext db, ISecretProtector secrets, IAuditLog audit, IClock clock, IObjectStorage storage)
{
    public async Task<IReadOnlyList<S3DestinationDto>> ListAsync(CancellationToken ct)
    {
        var dests = await db.S3Destinations.AsNoTracking().OrderBy(d => d.Name).ToListAsync(ct);
        return await DecorateAsync(dests, ct);
    }

    public async Task<S3DestinationDto> GetAsync(long id, CancellationToken ct)
    {
        var d = await db.S3Destinations.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("S3 destination", id);
        return (await DecorateAsync([d], ct))[0];
    }

    public async Task<S3DestinationDto> CreateAsync(S3DestinationUpsertRequest r, CancellationToken ct)
    {
        if (r.Kind == StorageKindDto.S3)
        {
            if (string.IsNullOrWhiteSpace(r.SecretKey)) throw RequestValidationException.For("secretKey", "The secret key is required.");
            if (string.IsNullOrWhiteSpace(r.AccessKeyId)) throw RequestValidationException.For("accessKeyId", "The access key id is required.");
        }
        await EnsureUniqueAsync(r.Name, null, ct);
        var d = new S3Destination();
        await ApplyAsync(d, r, ct);
        (string Recipient, string Cipher)? held = null;
        if (r.UseBackoraKey && r.EncryptionMode == EncryptionModeDto.CustomerKey)
        {
            var k = await BackoraKey.GetOrCreateAsync(db, secrets, ct);
            held = (k.Recipient, k.IdentityCiphertext);
            r = r with { EncryptionRecipient = k.Recipient };
        }
        var change = DestinationEncryption.Apply(d, r, isNew: true, clock.UtcNow);
        if (held is { } h) BackoraKey.Attach(d, h.Recipient, h.Cipher);
        db.S3Destinations.Add(d);
        await db.SaveChangesAsync(ct);
        audit.Record("storage.s3.created", "S3Destination", d.Id.ToString(), details: new { d.Name, d.Endpoint, d.Bucket, d.Region, encryption = d.EncryptionMode.ToString() });
        RecordEncryptionAudit(d, change);
        await db.SaveChangesAsync(ct);
        return await GetAsync(d.Id, ct);
    }

    public async Task<S3DestinationDto> UpdateAsync(long id, S3DestinationUpsertRequest r, CancellationToken ct)
    {
        var d = await db.S3Destinations.Include(x => x.EncryptionKeys).FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("S3 destination", id);
        Concurrency.EnsureVersion(d, r.Version);
        await EnsureUniqueAsync(r.Name, id, ct);
        var kind = r.Kind == StorageKindDto.Filesystem ? StorageKind.Filesystem : StorageKind.S3;
        var bucketChanged = kind != d.Kind || (kind == StorageKind.S3
            ? d.Bucket != r.Bucket || d.Endpoint != r.Endpoint
            : (d.LocalPath ?? "").TrimEnd('/') != (r.LocalPath ?? "").Trim().TrimEnd('/'));
        if (bucketChanged && await db.OffloadRuns.AnyAsync(o => o.S3DestinationId == id && o.Status == OffloadStatus.Completed && o.RetentionState != RetentionState.Deleted, ct))
        {
            throw new ConflictException("destination_has_recovery_points",
                "This destination holds recovery points; changing its type, endpoint, bucket or folder would orphan them. Create a new destination instead.");
        }
        await ApplyAsync(d, r, ct);
        (string Recipient, string Cipher)? held = null;
        if (r.UseBackoraKey && r.EncryptionMode == EncryptionModeDto.CustomerKey)
        {
            var k = await BackoraKey.GetOrCreateAsync(db, secrets, ct);
            held = (k.Recipient, k.IdentityCiphertext);
            r = r with { EncryptionRecipient = k.Recipient };
        }
        var change = DestinationEncryption.Apply(d, r, isNew: false, clock.UtcNow);
        if (held is { } h) BackoraKey.Attach(d, h.Recipient, h.Cipher);
        audit.Record("storage.s3.updated", "S3Destination", id.ToString(), details: new { d.Name, d.Endpoint, d.Bucket, secretChanged = r.SecretKey is not null, encryption = d.EncryptionMode.ToString() });
        RecordEncryptionAudit(d, change);
        await db.SaveChangesAsync(ct);
        return await GetAsync(id, ct);
    }

    /// <summary>
    /// Every job that copies its recovery points to DR site <paramref name="fromId"/> copies them to
    /// <paramref name="toId"/> instead, from the next backup on. Copies already stored on the old site stay there,
    /// restorable, until retention removes them.
    /// </summary>
    public async Task<MoveDrJobsResponse> MoveDrJobsAsync(long fromId, long toId, CancellationToken ct)
    {
        var from = await db.S3Destinations.AsNoTracking().FirstOrDefaultAsync(d => d.Id == fromId, ct) ?? throw new NotFoundException("S3 destination", fromId);
        var to = await db.S3Destinations.AsNoTracking().FirstOrDefaultAsync(d => d.Id == toId, ct)
            ?? throw RequestValidationException.For("toDestinationId", "Choose the site the jobs should copy to.");
        if (fromId == toId) throw RequestValidationException.For("toDestinationId", "Choose a different site.");
        if (!to.IsEnabled) throw new ConflictException("destination_disabled", $"{to.Name} is paused. Enable it before moving jobs to it.");
        var jobs = await db.BackupJobs.Include(j => j.ReplicationTargets)
            .Where(j => j.DeletedAt == null && j.ReplicationTargets.Any(t => t.S3DestinationId == fromId)).ToListAsync(ct);
        var moved = new List<string>();
        foreach (var job in jobs)
        {
            job.ReplicationTargets.RemoveAll(t => t.S3DestinationId == fromId);
            // A job whose primary storage is the new site, or that already copies there, just stops copying to the old one.
            if (job.S3DestinationId != toId && job.ReplicationTargets.All(t => t.S3DestinationId != toId))
            {
                job.ReplicationTargets.Add(new BackupJobReplicationTarget { S3DestinationId = toId });
            }
            moved.Add(job.Name);
        }
        audit.Record("storage.dr_jobs_moved", "S3Destination", fromId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            details: new { from = from.Name, to = to.Name, jobs = moved });
        await db.SaveChangesAsync(ct);
        return new MoveDrJobsResponse(moved.Count, to.Name);
    }

    public async Task DeleteAsync(long id, CancellationToken ct)
    {
        var d = await db.S3Destinations.FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("S3 destination", id);
        if (await db.BackupJobs.AnyAsync(j => j.S3DestinationId == id && j.DeletedAt == null, ct))
            throw new ConflictException("destination_in_use", "Backup jobs still offload to this destination.");
        if (await db.BackupJobReplicationTargets.AnyAsync(t => t.S3DestinationId == id, ct))
            throw new ConflictException("destination_in_use", "Backup jobs still copy recovery points to this destination (DR copies). Remove it from those jobs first.");
        if (await db.OffloadRuns.AnyAsync(o => o.S3DestinationId == id, ct))
            throw new ConflictException("destination_has_history", "The destination has offload history and recovery points; it cannot be deleted.");
        db.S3Destinations.Remove(d);
        audit.Record("storage.s3.deleted", "S3Destination", id.ToString(), details: new { d.Name, d.Bucket });
        await db.SaveChangesAsync(ct);
    }

    private const int BrowsePageSize = 300;
    private const int SearchScanLimit = 20000;
    private const int SearchResultLimit = 500;
    private const int SummaryScanLimit = 200_000;
    /// <summary>Entries one scan of a folder site reads (within 20 s); beyond that its totals are lower bounds.</summary>
    private const int FolderScanLimit = 20_000;
    private static readonly TimeSpan SummaryTtl = TimeSpan.FromMinutes(3);
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<(long Site, string Prefix), (DateTimeOffset At, FolderSummary Summary)> Summaries = new();

    /// <summary>Recursive totals under a prefix, per direct child folder and overall.</summary>
    private sealed record FolderSummary(Dictionary<string, (long Files, long Bytes, DateTimeOffset? Newest)> Children, S3BrowseTotals Totals);

    private async Task<FolderSummary> SummarizeAsync(long site, S3Connection conn, string p, CancellationToken ct)
    {
        if (Summaries.TryGetValue((site, p), out var hit) && clock.UtcNow - hit.At < SummaryTtl) return hit.Summary;
        var children = new Dictionary<string, (long Files, long Bytes, DateTimeOffset? Newest)>(StringComparer.Ordinal);
        long files = 0, bytes = 0; DateTimeOffset? newest = null, oldest = null;
        string? next = null;
        do
        {
            // A folder site is scanned in one capped pass on the execution host (paging would repeat the scan).
            var page = await storage.BrowseAsync(conn, p, next, conn.IsLocal ? FolderScanLimit : 1000, recursive: true, ct);
            foreach (var o in page.Objects)
            {
                files++; bytes += o.Size;
                if (newest is null || o.LastModified > newest) newest = o.LastModified;
                if (oldest is null || o.LastModified < oldest) oldest = o.LastModified;
                var rest = o.Key[p.Length..];
                var slash = rest.IndexOf('/');
                if (slash < 0) continue;
                var folder = p + rest[..(slash + 1)];
                var c = children.GetValueOrDefault(folder);
                children[folder] = (c.Files + 1, c.Bytes + o.Size, c.Newest is null || o.LastModified > c.Newest ? o.LastModified : c.Newest);
            }
            next = page.NextToken;
        } while (next is not null && files < SummaryScanLimit && !conn.IsLocal);
        var summary = new FolderSummary(children, new S3BrowseTotals(files, bytes, children.Count, newest, oldest, next is null));
        Summaries[(site, p)] = (clock.UtcNow, summary);
        if (Summaries.Count > 500) foreach (var k in Summaries.Where(x => clock.UtcNow - x.Value.At >= SummaryTtl).Select(x => x.Key).ToList()) Summaries.TryRemove(k, out _);
        return summary;
    }

    /// <summary>
    /// Lists a storage site's real contents. Without filters: one folder level, paged. With a search term or a date range:
    /// scans the folder tree (up to 20,000 objects) and returns the newest 500 matches. Read-only; secrets stay server-side.
    /// </summary>
    public async Task<S3BrowseResponse> BrowseAsync(long id, string? prefix, string? q, DateTimeOffset? from, DateTimeOffset? to, string? token, CancellationToken ct)
    {
        var d = await db.S3Destinations.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("S3 destination", id);
        if (d.Kind == StorageKind.S3 && (string.IsNullOrEmpty(d.AccessKeyId) || string.IsNullOrEmpty(d.SecretKeyCiphertext)))
        {
            throw new ConflictException("destination_missing_credentials", $"'{d.Name}' has no access keys yet. Add them on the site's page first.");
        }
        var conn = Storage.StorageConnections.For(d, secrets);
        var p = (prefix ?? string.Empty).TrimStart('/');
        if (p.Length > 0 && !p.EndsWith('/')) p += "/";
        var term = q?.Trim();
        var search = !string.IsNullOrEmpty(term) || from is not null || to is not null;
        try
        {
            if (!search)
            {
                var page = await storage.BrowseAsync(conn, p, token, BrowsePageSize, recursive: false, ct);
                var summary = await SummarizeAsync(d.Id, conn, p, ct);
                var items = page.Folders.Select(f =>
                    {
                        var c = summary.Children.GetValueOrDefault(f);
                        return new S3BrowseItemDto(f, LastSegment(f), true, c.Bytes, c.Newest, c.Files);
                    })
                    .Concat(page.Objects.Select(o => new S3BrowseItemDto(o.Key, LastSegment(o.Key), false, o.Size, o.LastModified, 1))).ToList();
                return new S3BrowseResponse(d.Id, d.Name, d.Kind == StorageKind.Filesystem ? d.LocalPath ?? "" : d.Bucket, p, items, page.NextToken, false, page.NextToken is not null, items.Count,
                    summary.Totals with { Folders = Math.Max(summary.Totals.Folders, page.Folders.Count) });
            }

            var matches = new List<StoredObject>();
            string? next = null; var scanned = 0;
            do
            {
                var page = await storage.BrowseAsync(conn, p, next, conn.IsLocal ? FolderScanLimit : 1000, recursive: true, ct);
                scanned += page.Objects.Count;
                matches.AddRange(page.Objects.Where(o =>
                    (string.IsNullOrEmpty(term) || o.Key[p.Length..].Contains(term, StringComparison.OrdinalIgnoreCase)) &&
                    (from is null || o.LastModified >= from) && (to is null || o.LastModified < to)));
                next = page.NextToken;
            } while (next is not null && scanned < SearchScanLimit && !conn.IsLocal);
            var result = matches.OrderByDescending(o => o.LastModified).Take(SearchResultLimit)
                .Select(o => new S3BrowseItemDto(o.Key, LastSegment(o.Key), false, o.Size, o.LastModified, 1)).ToList();
            var totals = new S3BrowseTotals(matches.Count, matches.Sum(o => o.Size), 0,
                matches.Count == 0 ? null : matches.Max(o => o.LastModified), matches.Count == 0 ? null : matches.Min(o => o.LastModified), next is null);
            return new S3BrowseResponse(d.Id, d.Name, d.Kind == StorageKind.Filesystem ? d.LocalPath ?? "" : d.Bucket, p, result, null, true, next is not null || matches.Count > SearchResultLimit, scanned, totals);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not NotFoundException and not ConflictException)
        {
            throw new ConflictException("storage_unreachable", $"Couldn't list '{d.Name}': {ex.Message}");
        }

        static string LastSegment(string key) { var k = key.TrimEnd('/'); var i = k.LastIndexOf('/'); return i < 0 ? k : k[(i + 1)..]; }
    }

    public async Task<S3TestResultDto> TestAsync(S3TestRequest r, CancellationToken ct)
    {
        if (r.Kind == StorageKindDto.Filesystem) return await TestFolderAsync(r, ct);
        string secret;
        S3Destination? stored = null;
        if (!string.IsNullOrEmpty(r.SecretKey)) secret = r.SecretKey;
        else if (r.DestinationId is { } did)
        {
            stored = await db.S3Destinations.FirstOrDefaultAsync(x => x.Id == did, ct) ?? throw new NotFoundException("S3 destination", did);
            secret = secrets.Unprotect(stored.SecretKeyCiphertext);
        }
        else throw RequestValidationException.For("secretKey", "The secret key is required.");

        if (IsBlockedEndpoint(r.Endpoint)) throw RequestValidationException.For("endpoint", "This endpoint address is not allowed (link-local/metadata).");
        var accessKey = r.AccessKeyId;
        if (string.IsNullOrWhiteSpace(accessKey))
        {
            stored ??= r.DestinationId is { } sid ? await db.S3Destinations.FirstOrDefaultAsync(x => x.Id == sid, ct) : null;
            accessKey = stored?.AccessKeyId ?? throw RequestValidationException.For("accessKeyId", "The access key id is required.");
        }
        var checks = await storage.TestAsync(new S3Connection(r.Endpoint, r.Region, r.Bucket, accessKey, secret, r.ForcePathStyle), ct);
        var success = checks.All(c => c.State != CheckState.Failed);
        var message = success ? "Connection, permissions and write/read/delete probe succeeded." : checks.First(c => c.State == CheckState.Failed).Message;
        if (stored is not null && stored.Endpoint == r.Endpoint && stored.Bucket == r.Bucket)
        {
            stored.LastConnectionTestAt = clock.UtcNow;
            stored.LastConnectionTestSucceeded = success;
            stored.LastConnectionTestMessage = message.Length > 2000 ? message[..2000] : message;
        }
        audit.Record("storage.s3.tested", "S3Destination", r.DestinationId?.ToString(), success ? AuditOutcome.Success : AuditOutcome.Failure, new { r.Endpoint, r.Bucket, success });
        await db.SaveChangesAsync(ct);
        return new S3TestResultDto(success, message, checks);
    }

    /// <summary>Folder site: exists on the execution host, is a mount, can be written, read and deleted, and has free space.</summary>
    private async Task<S3TestResultDto> TestFolderAsync(S3TestRequest r, CancellationToken ct)
    {
        var staging = (await db.StorageSettings.AsNoTracking().FirstOrDefaultAsync(ct))?.NfsPath;
        if (Storage.StorageConnections.LocalPathProblem(r.LocalPath, staging) is { } problem) throw RequestValidationException.For("localPath", problem);
        var path = r.LocalPath!.Trim().TrimEnd('/');
        var checks = await storage.TestAsync(new S3Connection(string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, false, path), ct);
        var success = checks.All(c => c.State != CheckState.Failed);
        var message = success ? $"The folder {path} is reachable on the execution host and can be written, read and deleted." : checks.First(c => c.State == CheckState.Failed).Message;
        if (r.DestinationId is { } did && await db.S3Destinations.FirstOrDefaultAsync(x => x.Id == did, ct) is { } stored
            && stored.Kind == StorageKind.Filesystem && (stored.LocalPath ?? "").TrimEnd('/') == path)
        {
            stored.LastConnectionTestAt = clock.UtcNow;
            stored.LastConnectionTestSucceeded = success;
            stored.LastConnectionTestMessage = message.Length > 2000 ? message[..2000] : message;
        }
        audit.Record("storage.s3.tested", "S3Destination", r.DestinationId?.ToString(), success ? AuditOutcome.Success : AuditOutcome.Failure, new { localPath = path, success });
        await db.SaveChangesAsync(ct);
        return new S3TestResultDto(success, message, checks);
    }

    /// <summary>SSRF guard: cloud metadata and link-local endpoints are never contacted.</summary>
    public static bool IsBlockedEndpoint(string endpoint)
    {
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri)) return true;
        if (System.Net.IPAddress.TryParse(uri.Host, out var ip))
        {
            var b = ip.GetAddressBytes();
            if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork && b[0] == 169 && b[1] == 254) return true;
            if (ip.IsIPv6LinkLocal) return true;
        }
        return uri.Host.Equals("metadata.google.internal", StringComparison.OrdinalIgnoreCase);
    }

    private async Task ApplyAsync(S3Destination d, S3DestinationUpsertRequest r, CancellationToken ct)
    {
        if (r.Kind == StorageKindDto.Filesystem)
        {
            // A folder on the execution host: no endpoint, keys or bucket.
            var staging = (await db.StorageSettings.AsNoTracking().FirstOrDefaultAsync(ct))?.NfsPath;
            if (Storage.StorageConnections.LocalPathProblem(r.LocalPath, staging) is { } problem) throw RequestValidationException.For("localPath", problem);
            var path = r.LocalPath!.Trim().TrimEnd('/');
            d.Kind = StorageKind.Filesystem;
            d.LocalPath = path;
            d.Name = r.Name.Trim();
            d.Endpoint = Storage.StorageConnections.LocalEndpoint(path);
            d.Region = string.Empty;
            d.Bucket = string.Empty;
            d.AccessKeyId = string.Empty;
            d.SecretKeyCiphertext = string.Empty;
            d.ForcePathStyle = false;
            d.PathPrefix = string.IsNullOrWhiteSpace(r.PathPrefix) ? null : r.PathPrefix.Trim('/');
            d.IsEnabled = r.IsEnabled;
            return;
        }
        d.Kind = StorageKind.S3;
        d.LocalPath = null;
        if (IsBlockedEndpoint(r.Endpoint)) throw RequestValidationException.For("endpoint", "This endpoint address is not allowed (link-local/metadata).");
        d.Name = r.Name.Trim();
        d.Endpoint = r.Endpoint.Trim().TrimEnd('/');
        d.Region = r.Region.Trim();
        d.Bucket = r.Bucket.Trim();
        if (!string.IsNullOrWhiteSpace(r.AccessKeyId)) d.AccessKeyId = r.AccessKeyId.Trim();
        d.ForcePathStyle = r.ForcePathStyle;
        d.PathPrefix = string.IsNullOrWhiteSpace(r.PathPrefix) ? null : r.PathPrefix.Trim('/');
        d.IsEnabled = r.IsEnabled;
        if (!string.IsNullOrEmpty(r.SecretKey)) d.SecretKeyCiphertext = secrets.Protect(r.SecretKey);
    }

    private async Task EnsureUniqueAsync(string name, long? exceptId, CancellationToken ct)
    {
        var n = name.Trim();
        if (await db.S3Destinations.AnyAsync(d => d.Name == n && d.Id != exceptId, ct)) throw new ConflictException("destination_exists", $"A destination named '{n}' already exists.");
    }

    private void RecordEncryptionAudit(S3Destination d, DestinationEncryption.Change? change)
    {
        if (change is null) return;
        audit.Record($"storage.s3.encryption_{change.Kind}", "S3Destination", d.Id.ToString(),
            details: new { d.Name, previousFingerprint = change.OldFingerprint, fingerprint = change.NewFingerprint, format = AgeKeys.KeyFormat });
    }

    /// <summary>Validates a recipient without saving it.</summary>
    public static RecipientInspectionDto Inspect(InspectRecipientRequest r) => AgeKeys.Inspect(r.Recipient);

    /// <summary>
    /// Generates an age X25519 key pair for operators without their own tooling. The private key is returned
    /// exactly once and is not stored, logged or audited (the audit records only the public fingerprint).
    /// </summary>
    public async Task<GeneratedKeyPairDto> GenerateKeyPairAsync(CancellationToken ct)
    {
        var pair = AgeKeys.Generate();
        await audit.RecordNowAsync("storage.s3.encryption_key_pair_generated", "S3Destination", null, AuditOutcome.Success,
            new { fingerprint = pair.Fingerprint, format = AgeKeys.KeyFormat, note = "encryption key pair generated; private key returned once and not stored" }, ct);
        return new GeneratedKeyPairDto(pair.Recipient, pair.Identity, pair.Fingerprint);
    }

    private async Task<List<S3DestinationDto>> DecorateAsync(List<S3Destination> dests, CancellationToken ct)
    {
        var ids = dests.Select(d => d.Id).ToList();
        var jobCounts = await db.BackupJobs.Where(j => j.S3DestinationId != null && ids.Contains(j.S3DestinationId!.Value) && j.DeletedAt == null)
            .GroupBy(j => j.S3DestinationId!.Value).Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count, ct);
        var points = await db.OffloadRuns.Where(o => ids.Contains(o.S3DestinationId) && o.Status == OffloadStatus.Completed && o.RetentionState != RetentionState.Deleted)
            .GroupBy(o => o.S3DestinationId).Select(g => new { g.Key, Count = g.Count(), Bytes = g.Sum(o => o.BytesTotal ?? 0) })
            .ToDictionaryAsync(x => x.Key, ct);
        var keys = await db.S3DestinationEncryptionKeys.AsNoTracking().Where(k => ids.Contains(k.S3DestinationId))
            .OrderByDescending(k => k.ActivatedAt).ToListAsync(ct);
        var perFingerprint = await db.OffloadRuns.Where(o => ids.Contains(o.S3DestinationId) && o.Encrypted && o.Status == OffloadStatus.Completed && o.RetentionState != RetentionState.Deleted)
            .GroupBy(o => new { o.S3DestinationId, o.EncryptionKeyFingerprint }).Select(g => new { g.Key.S3DestinationId, g.Key.EncryptionKeyFingerprint, Count = g.Count() })
            .ToListAsync(ct);
        var lastEncrypted = await db.OffloadRuns.Where(o => ids.Contains(o.S3DestinationId) && o.Encrypted && o.Status == OffloadStatus.Completed)
            .GroupBy(o => o.S3DestinationId).Select(g => new { g.Key, Last = g.Max(o => o.CompletedAt) })
            .ToDictionaryAsync(x => x.Key, x => x.Last, ct);
        return dests.Select(d =>
        {
            points.TryGetValue(d.Id, out var p);
            var history = keys.Where(k => k.S3DestinationId == d.Id)
                .Select(k => new EncryptionKeyDto(k.Fingerprint, k.Recipient, k.ActivatedAt, k.RetiredAt,
                    perFingerprint.Where(x => x.S3DestinationId == d.Id && x.EncryptionKeyFingerprint == k.Fingerprint).Sum(x => x.Count)))
                .ToList();
            return new S3DestinationDto(d.Id, d.Name, d.Endpoint, d.Region, d.Bucket, Mapping.MaskKeyId(d.AccessKeyId), d.ForcePathStyle, d.PathPrefix,
                d.LastConnectionTestAt, d.LastConnectionTestSucceeded, d.LastConnectionTestMessage, jobCounts.GetValueOrDefault(d.Id), p?.Count ?? 0, p?.Bytes ?? 0, d.Version, d.CreatedAt,
                d.EncryptionMode.ToDto(), d.EncryptionRecipient, d.EncryptionKeyFingerprint, d.EncryptionKeyActivatedAt, history, lastEncrypted.GetValueOrDefault(d.Id), d.IsEnabled,
                d.EncryptionMode == EncryptionMode.CustomerKey && keys.Any(k => k.S3DestinationId == d.Id && k.RetiredAt is null && k.IdentityCiphertext != null),
                d.Kind == StorageKind.Filesystem ? StorageKindDto.Filesystem : StorageKindDto.S3, d.LocalPath);
        }).ToList();
    }
}
