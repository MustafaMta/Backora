using Backora.Application.Abstractions;
using Backora.Application.Encryption;
using Backora.Contracts.V1;
using Backora.Domain.Backups;
using Backora.Domain.Infrastructure;
using Backora.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Backora.Tools;

/// <summary>
/// Creates or updates an S3 storage site and links jobs to it as a DR copy (S3 → S3 after the primary upload).
/// Keys come only from BACKORA_S3_ACCESS_KEY / BACKORA_S3_SECRET_KEY (never arguments) and are stored encrypted.
/// </summary>
public sealed class S3SiteCommand(BackoraDbContext db, ISecretProtector secrets)
{
    public async Task<int> RunAsync(Dictionary<string, string?> o, CancellationToken ct)
    {
        string Req(string k) => o.GetValueOrDefault(k) is { Length: > 0 } v ? v : throw new ToolsProgram.UsageException($"--{k} is required.");
        var name = Req("name"); var endpoint = Req("endpoint"); var region = Req("region"); var bucket = Req("bucket");
        var prefix = o.GetValueOrDefault("prefix")?.Trim('/');
        var access = Environment.GetEnvironmentVariable("BACKORA_S3_ACCESS_KEY");
        var secret = Environment.GetEnvironmentVariable("BACKORA_S3_SECRET_KEY");
        if (string.IsNullOrEmpty(access) || string.IsNullOrEmpty(secret)) throw new ToolsProgram.UsageException("Set BACKORA_S3_ACCESS_KEY and BACKORA_S3_SECRET_KEY in the environment.");
        var jobs = (o.GetValueOrDefault("link-jobs") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var strategy = db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            var site = await db.S3Destinations.Include(d => d.EncryptionKeys).FirstOrDefaultAsync(d => d.Name == name, ct);
            if (site is null) { site = new S3Destination { Name = name }; db.S3Destinations.Add(site); }
            site.Endpoint = endpoint; site.Region = region; site.Bucket = bucket; site.PathPrefix = string.IsNullOrEmpty(prefix) ? null : prefix;
            site.ForcePathStyle = o.ContainsKey("path-style");
            site.AccessKeyId = access; site.SecretKeyCiphertext = secrets.Protect(secret); site.IsEnabled = true;
            var encryption = "not client-side encrypted";
            if (o.ContainsKey("backora-key"))
            {
                // Backora's own age key: the private key is stored encrypted (app encryption key) so restores decrypt automatically.
                var key = await BackoraKey.GetOrCreateAsync(db, secrets, ct);
                var req = new S3DestinationUpsertRequest(name, endpoint, region, bucket, access, null, site.ForcePathStyle, site.PathPrefix, null,
                    EncryptionModeDto.CustomerKey, key.Recipient);
                DestinationEncryption.Apply(site, req, isNew: site.Id == 0, DateTimeOffset.UtcNow);
                BackoraKey.Attach(site, key.Recipient, key.IdentityCiphertext);
                encryption = $"encrypted with the Backora key {AgeKeys.RecipientFingerprint(key.Recipient)}{(key.Created ? " (new)" : " (shared)")}";
            }
            await db.SaveChangesAsync(ct);
            Console.WriteLine($"site      {site.Name} (#{site.Id}): {endpoint} bucket {bucket}{(site.PathPrefix is null ? "" : "/" + site.PathPrefix)} · keys stored encrypted · {encryption} · enabled");

            var missing = new List<string>();
            foreach (var jn in jobs)
            {
                var job = await db.BackupJobs.Include(j => j.ReplicationTargets).FirstOrDefaultAsync(j => j.DeletedAt == null && j.Name.ToLower() == jn.ToLower(), ct);
                if (job is null) { missing.Add(jn); continue; }
                if (job.S3DestinationId == site.Id) { Console.WriteLine($"skip      {job.Name}: this site is its primary"); continue; }
                if (job.S3DestinationId is null) { Console.WriteLine($"skip      {job.Name}: no primary storage, so it can't have a DR copy"); continue; }
                if (job.ReplicationTargets.Any(t => t.S3DestinationId == site.Id)) { Console.WriteLine($"ok        {job.Name}: already linked"); continue; }
                job.ReplicationTargets.Add(new BackupJobReplicationTarget { S3DestinationId = site.Id });
                Console.WriteLine($"link      {job.Name}: DR copy to {site.Name}");
            }
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            if (missing.Count > 0) Console.WriteLine($"not found: {string.Join(", ", missing)}");
            return missing.Count == 0 ? 0 : 1;
        });
    }
}
