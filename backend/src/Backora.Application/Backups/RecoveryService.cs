using Backora.Application.Abstractions;
using Backora.Application.Common;
using Backora.Application.Encryption;
using Backora.Application.Execution;
using Backora.Application.Operations;
using Backora.Contracts.V1;
using Backora.Domain;
using Backora.Domain.Backups;
using Backora.Domain.Execution;
using Backora.Domain.Platform;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Backora.Application.Backups;

public sealed record RecoveryPointQuery(long? JobId, long? ServerId, long? DestinationId, DateTimeOffset? From, DateTimeOffset? To, string? Q, bool IncludeDeleted, OffloadStatusDto? Status,
    bool? Replica = null, bool? Encrypted = null, bool Cleared = false);

public sealed class RecoveryService(
    IAppDbContext db,
    Execution.ExecutionHostKeys keys,
    ICurrentUser user,
    IAuditLog audit,
    IClock clock,
    ISecretProtector secrets,
    IObjectStorage storage,
    ISshConnector ssh,
    DecryptionKeyCourier courier,
    IOptions<ClientEncryptionOptions> encryption)
{
    // ───────────── Offloads & recovery points ─────────────
    public async Task<PagedResult<OffloadSummaryDto>> ListOffloadsAsync(RecoveryPointQuery f, PageRequest page, bool recoveryPointsOnly, CancellationToken ct)
    {
        var query = db.OffloadRuns.AsNoTracking().Include(o => o.S3Destination).Include(o => o.Server)
            .Include(o => o.SourceOffloadRun!).ThenInclude(s => s.S3Destination).AsQueryable();
        if (recoveryPointsOnly)
        {
            query = query.Where(o => o.Status == OffloadStatus.Completed);
            if (!f.IncludeDeleted) query = query.Where(o => o.RetentionState != RetentionState.Deleted);
        }
        else
        {
            // A cleared failure is out of the way everywhere (it is kept, under "Cleared").
            query = f.Cleared ? query.Where(o => o.DismissedAt != null) : query.Where(o => o.DismissedAt == null);
        }
        if (!recoveryPointsOnly && f.Status is { } st)
        {
            var s = (OffloadStatus)(int)st;
            query = query.Where(o => o.Status == s);
            // "Failed" lists failures that still need attention; a failure that was retried is resolved, and a v1
            // record is history that cannot be retried.
            if (s == OffloadStatus.Failed) query = query.WhereNeedsAttention(db.OffloadRuns);
        }
        if (f.JobId is { } j) query = query.Where(o => o.BackupJobId == j);
        if (f.ServerId is { } sid) query = query.Where(o => o.ServerId == sid);
        if (f.DestinationId is { } did) query = query.Where(o => o.S3DestinationId == did);
        if (f.Encrypted is { } encrypted) query = query.Where(o => o.Encrypted == encrypted);
        if (f.Replica is { } replica) query = replica ? query.Where(o => o.SourceOffloadRunId != null) : query.Where(o => o.SourceOffloadRunId == null);
        if (f.From is { } from) query = query.Where(o => (o.CompletedAt ?? o.CreatedAt) >= from);
        if (f.To is { } to) query = query.Where(o => (o.CompletedAt ?? o.CreatedAt) <= to);
        if (!string.IsNullOrWhiteSpace(f.Q))
        {
            var term = $"%{f.Q.Trim()}%";
            query = query.Where(o => EF.Functions.ILike(o.JobName, term) || EF.Functions.ILike(o.RemotePrefix, term) || EF.Functions.ILike(o.Server!.Name, term));
        }
        query = (page.SortField, page.Descending) switch
        {
            ("size", true) => query.OrderByDescending(o => o.BytesTotal),
            ("size", false) => query.OrderBy(o => o.BytesTotal),
            ("completedat", false) => query.OrderBy(o => o.CompletedAt),
            _ => query.OrderByDescending(o => o.CompletedAt ?? o.CreatedAt).ThenByDescending(o => o.Id),
        };
        var total = await query.LongCountAsync(ct);
        var items = await query.Skip(page.Skip).Take(page.SafePageSize).ToListAsync(ct);
        var next = await CopyAttempts.NextAttemptsAsync(db, items, ct);
        return new PagedResult<OffloadSummaryDto>(items.Select(o => o.ToSummary(null, next.TryGetValue(o.Id, out var n) ? n : null)).ToList(), page.SafePage, page.SafePageSize, total);
    }

    public async Task<OffloadDetailDto> GetOffloadAsync(long id, CancellationToken ct)
    {
        var o = await db.OffloadRuns.AsNoTracking().Include(x => x.S3Destination).Include(x => x.Server)
            .Include(x => x.BackupRun!).ThenInclude(r => r.Artifacts)
            .Include(x => x.SourceOffloadRun!).ThenInclude(s => s.S3Destination)
            .FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("Offload", id);
        var restores = await db.RestoreRuns.AsNoTracking().Include(r => r.Server).Where(r => r.OffloadRunId == id).OrderByDescending(r => r.CreatedAt).ToListAsync(ct);
        var next = await CopyAttempts.NextAttemptsAsync(db, [o], ct);
        return new OffloadDetailDto(o.ToSummary(null, next.TryGetValue(o.Id, out var n) ? n : null), o.Log, o.CleanupMessage, o.VerifiedAt, o.DeletedAt, o.DeletionReason,
            o.BackupRun?.Artifacts.OrderBy(a => a.RelativePath, StringComparer.Ordinal).Select(a => a.ToDto()).ToList() ?? [],
            restores.Select(r => r.ToSummary()).ToList());
    }

    public async Task<IdResponse> RetryOffloadAsync(long id, CancellationToken ct)
    {
        var o = await db.OffloadRuns.Include(x => x.BackupRun).FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("Offload", id);
        if (o.Status != OffloadStatus.Failed) throw new ConflictException("offload_not_failed", "Only failed offloads can be retried.");
        if (o.LegacyPrefix) throw new ConflictException("offload_legacy", "This copy was recorded by Backora v1 and cannot be retried: v1 did not record its files. A new backup creates a new copy.");
        if (o.BackupRunId is { } runId && await db.OffloadRuns.AnyAsync(x => x.BackupRunId == runId && x.S3DestinationId == o.S3DestinationId && x.Id > o.Id, ct))
        {
            throw new ConflictException("offload_retried", "This copy was already retried. Open the newer attempt for this site.");
        }
        if (o.SourceOffloadRunId is { } sourceId)
        {
            return await QueueReplicaAsync(sourceId, o.S3DestinationId, "offload.retried", new { previous = id, o.JobName }, ct);
        }
        if (o.BackupRunId is null) throw new ConflictException("offload_no_source", "This offload has no source backup run (legacy record).");
        if (await db.OffloadRuns.AnyAsync(x => x.BackupRunId == o.BackupRunId && x.SourceOffloadRunId == null
                && (x.Status == OffloadStatus.Pending || x.Status == OffloadStatus.Running || x.Status == OffloadStatus.Verifying || x.Status == OffloadStatus.Completed), ct))
        {
            throw new ConflictException("offload_exists", "An active or completed offload already exists for this backup run.");
        }
        if (await db.S3Destinations.AsNoTracking().Where(d => d.Id == o.S3DestinationId).Select(d => (bool?)d.IsEnabled).FirstOrDefaultAsync(ct) == false)
        {
            throw new ConflictException("destination_disabled", "This destination is disabled. Enable it before retrying the upload.");
        }
        var job = o.BackupJobId is { } jid ? await db.BackupJobs.AsNoTracking().FirstOrDefaultAsync(j => j.Id == jid, ct) : null;
        var retry = new OffloadRun
        {
            BackupRunId = o.BackupRunId, BackupJobId = o.BackupJobId, S3DestinationId = o.S3DestinationId, ServerId = o.ServerId, JobName = o.JobName,
            SourcePath = o.SourcePath, MaxAttempts = 3, NextAttemptAt = clock.UtcNow, CorrelationId = user.CorrelationId,
            CleanupStatus = job?.KeepLocalCopy == true ? CleanupStatus.Skipped : CleanupStatus.Pending,
        };
        db.OffloadRuns.Add(retry);
        await db.SaveChangesAsync(ct);
        audit.Record("offload.retried", "Offload", retry.Id.ToString(), details: new { previous = id, o.JobName });
        await db.SaveChangesAsync(ct);
        return new IdResponse(retry.Id);
    }

    /// <summary>
    /// Clears failed copies from the Failed list (one, or every failure that still needs attention): they stay in the
    /// history, can still be retried, and stop counting in alerts and job health. Nothing is deleted.
    /// </summary>
    public async Task<DismissedResponse> DismissFailedAsync(long? id, CancellationToken ct)
    {
        var query = db.OffloadRuns.Where(o => o.Status == OffloadStatus.Failed).WhereNeedsAttention(db.OffloadRuns);
        if (id is { } one)
        {
            var o = await db.OffloadRuns.FirstOrDefaultAsync(x => x.Id == one, ct) ?? throw new NotFoundException("Offload", one);
            if (o.Status != OffloadStatus.Failed) throw new ConflictException("offload_not_failed", "Only failed copies can be cleared.");
            query = query.Where(x => x.Id == one);
        }
        var failed = await query.ToListAsync(ct);
        var now = clock.UtcNow;
        foreach (var f in failed)
        {
            f.DismissedAt = now;
            f.DismissedBy = user.Name;
        }
        audit.Record("offload.failures_cleared", "Offload", id?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "all",
            details: new { count = failed.Count, ids = failed.Select(f => f.Id).Take(500).ToList() });
        await db.SaveChangesAsync(ct);
        return new DismissedResponse(failed.Count);
    }

    /// <summary>Copies a recovery point to another S3 destination (S3 → S3), e.g. to seed or repair a DR site.</summary>
    public Task<IdResponse> ReplicateAsync(long recoveryPointId, ReplicateRecoveryPointRequest r, CancellationToken ct) =>
        QueueReplicaAsync(recoveryPointId, r.DestinationId, "recovery_point.replication_requested", null, ct);

    private async Task<IdResponse> QueueReplicaAsync(long sourceId, long destinationId, string auditAction, object? extra, CancellationToken ct)
    {
        var source = await db.OffloadRuns.AsNoTracking().Include(x => x.S3Destination).FirstOrDefaultAsync(x => x.Id == sourceId, ct)
            ?? throw new NotFoundException("Recovery point", sourceId);
        if (source.Status != OffloadStatus.Completed || source.RetentionState is RetentionState.Deleted or RetentionState.Deleting)
        {
            throw new ConflictException("recovery_point_unavailable", "Only completed recovery points that still exist can be copied.");
        }
        var target = await db.S3Destinations.AsNoTracking().FirstOrDefaultAsync(d => d.Id == destinationId, ct)
            ?? throw RequestValidationException.For("destinationId", "S3 destination not found.");
        if (target.Id == source.S3DestinationId) throw RequestValidationException.For("destinationId", "Choose a destination other than the one the recovery point is stored on.");
        if (!target.IsEnabled) throw new ConflictException("destination_disabled", $"{target.Name} is disabled. Enable it before copying recovery points to it.");
        var targetPrefix = OffloadRun.BuildReplicaPrefix(target.PathPrefix, source.S3Destination?.PathPrefix, source.RemotePrefix);
        if (source.S3Destination is { } sd && string.Equals(sd.Endpoint.TrimEnd('/'), target.Endpoint.TrimEnd('/'), StringComparison.OrdinalIgnoreCase)
            && sd.Bucket == target.Bucket && targetPrefix == source.RemotePrefix)
        {
            throw RequestValidationException.For("destinationId", "That destination points at the same bucket and prefix as the source; the copy would overwrite itself.");
        }
        // One copy per (source point, destination): an active or completed copy already covers it.
        if (await db.OffloadRuns.AnyAsync(x => x.SourceOffloadRunId == sourceId && x.S3DestinationId == destinationId
                && (x.Status == OffloadStatus.Pending || x.Status == OffloadStatus.Running || x.Status == OffloadStatus.Verifying
                    || (x.Status == OffloadStatus.Completed && x.RetentionState != RetentionState.Deleted)), ct))
        {
            throw new ConflictException("replica_exists", $"This recovery point already has an active or completed copy on {target.Name}.");
        }
        if (source.BackupRunId is { } brid && await db.OffloadRuns.AnyAsync(x => x.BackupRunId == brid && x.S3DestinationId == destinationId
                && (x.Status == OffloadStatus.Pending || x.Status == OffloadStatus.Running || x.Status == OffloadStatus.Verifying), ct))
        {
            throw new ConflictException("replica_exists", $"A transfer of this backup to {target.Name} is already in progress.");
        }

        var replica = ReplicaFactory.Create(source, destinationId, clock.UtcNow, user.CorrelationId);
        db.OffloadRuns.Add(replica);
        await db.SaveChangesAsync(ct);
        audit.Record(auditAction, "Offload", replica.Id.ToString(), details: new { source = sourceId, destination = target.Name, source.JobName, extra });
        await db.SaveChangesAsync(ct);
        return new IdResponse(replica.Id);
    }

    public async Task<OffloadSummaryDto> SetProtectionAsync(long id, ProtectRecoveryPointRequest r, CancellationToken ct)
    {
        var o = await db.OffloadRuns.Include(x => x.S3Destination).Include(x => x.Server).FirstOrDefaultAsync(x => x.Id == id && x.Status == OffloadStatus.Completed, ct)
            ?? throw new NotFoundException("Recovery point", id);
        if (o.RetentionState == RetentionState.Deleted) throw new ConflictException("recovery_point_deleted", "The recovery point has already been deleted.");
        o.IsProtected = r.Protected;
        o.ProtectionReason = r.Protected ? r.Reason?.Trim() : null;
        o.ProtectedByUserId = r.Protected ? user.UserId : null;
        audit.Record(r.Protected ? "recovery_point.protected" : "recovery_point.unprotected", "RecoveryPoint", id.ToString(), details: new { o.JobName, r.Reason });
        await db.SaveChangesAsync(ct);
        return o.ToSummary();
    }

    public async Task<IReadOnlyList<ServerBackupSummaryDto>> ServerSummaryAsync(CancellationToken ct)
    {
        var rows = await db.OffloadRuns.AsNoTracking().Where(o => o.ServerId != null)
            .GroupBy(o => o.ServerId!.Value)
            .Select(g => new
            {
                ServerId = g.Key,
                Points = g.Count(o => o.Status == OffloadStatus.Completed && o.RetentionState != RetentionState.Deleted),
                Completed = g.Count(o => o.Status == OffloadStatus.Completed),
                Failed = g.Count(o => o.Status == OffloadStatus.Failed),
                Bytes = g.Where(o => o.Status == OffloadStatus.Completed && o.RetentionState != RetentionState.Deleted).Sum(o => o.BytesTotal ?? 0),
                Latest = g.Where(o => o.Status == OffloadStatus.Completed && o.RetentionState != RetentionState.Deleted).Max(o => (DateTimeOffset?)o.CompletedAt),
            }).ToListAsync(ct);
        var servers = await db.Servers.AsNoTracking().ToDictionaryAsync(s => s.Id, ct);
        return rows.Where(r => servers.ContainsKey(r.ServerId))
            .Select(r => new ServerBackupSummaryDto(r.ServerId, servers[r.ServerId].Name, servers[r.ServerId].Host, r.Points, r.Completed, r.Failed, r.Bytes, r.Latest))
            .OrderBy(r => r.ServerName).ToList();
    }

    // ───────────── Restores ─────────────
    public static string ConfirmationPhraseFor(OffloadRun o) => $"restore {o.JobName}";

    public async Task<RestorePrecheckResponse> PrecheckAsync(long recoveryPointId, CancellationToken ct)
    {
        var o = await db.OffloadRuns.AsNoTracking().Include(x => x.S3Destination).Include(x => x.Server)
            .Include(x => x.BackupRun!).ThenInclude(r => r.Artifacts)
            .FirstOrDefaultAsync(x => x.Id == recoveryPointId, ct) ?? throw new NotFoundException("Recovery point", recoveryPointId);

        var checks = new List<CheckResultDto>();
        var manifest = o.BackupRun?.Artifacts.ToList() ?? [];
        var expectedBytes = manifest.Count > 0 ? manifest.Sum(a => a.SizeBytes) : o.BytesTotal ?? 0;

        checks.Add(o.Status == OffloadStatus.Completed && o.RetentionState != RetentionState.Deleted
            ? new CheckResultDto("Recovery point state", CheckState.Passed, $"Completed {o.CompletedAt:yyyy-MM-dd HH:mm} UTC, retention {o.RetentionState}.")
            : new CheckResultDto("Recovery point state", CheckState.Failed, $"Recovery point is {o.Status}/{o.RetentionState} and cannot be restored."));

        checks.Add(o.VerificationStatus == VerificationStatus.Verified
            ? new CheckResultDto("Integrity at backup time", CheckState.Passed, $"Offload was verified by checksum; {manifest.Count} file(s) in the SHA-256 manifest.")
            : new CheckResultDto("Integrity at backup time", CheckState.Warning, o.LegacyPrefix ? "Legacy recovery point without a Backora manifest; restored files are verified against object storage checksums only." : "Offload verification is not recorded."));

        var activeRestore = await db.RestoreRuns.AnyAsync(r => r.OffloadRunId == o.Id && (r.Status == RestoreStatus.Pending || r.Status == RestoreStatus.Running || r.Status == RestoreStatus.Verifying
            || r.Status == RestoreStatus.Cancelling || r.Status == RestoreStatus.AwaitingKey), ct);
        checks.Add(activeRestore
            ? new CheckResultDto("Concurrent restores", CheckState.Failed, "A restore of this recovery point is already in progress.")
            : new CheckResultDto("Concurrent restores", CheckState.Passed, "No other restore of this recovery point is running."));

        if (o.Encrypted && await BackoraKey.HoldsAsync(db, o.EncryptionRecipient, ct))
        {
            checks.Add(new CheckResultDto("Decryption key", CheckState.Passed,
                $"Encrypted with age to {o.EncryptionKeyFingerprint}, Backora's own key: it decrypts automatically during the restore."));
        }
        else if (o.Encrypted)
        {
            checks.Add(new CheckResultDto("Decryption key required", CheckState.Warning,
                $"This recovery point is encrypted with age to the recipient {o.EncryptionKeyFingerprint}. The matching private key (\"AGE-SECRET-KEY-1…\") must be supplied for the restore; Backora does not have it and cannot restore without it."));
        }

        // Object storage availability: every object present with expected size.
        if (o.S3Destination is { } dest)
        {
            try
            {
                var objects = await storage.ListAsync(Storage.StorageConnections.For(dest, secrets), o.RemotePrefix, ct);
                if (objects.Count == 0)
                {
                    checks.Add(new CheckResultDto("Object storage availability", CheckState.Failed, $"No objects found under s3://{dest.Bucket}/{o.RemotePrefix}."));
                }
                else if (manifest.Count > 0)
                {
                    var byKey = objects.ToDictionary(x => x.Key[o.RemotePrefix.Length..], x => x.Size, StringComparer.Ordinal);
                    // Compressed/encrypted points store <rel>.zst / .age with the object size recorded at upload time.
                    var cipherSizes = o.EncryptedObjects.ToDictionary(e => e.RelativePath, e => e.CiphertextSize, StringComparer.Ordinal);
                    var bad = manifest.Where(a => !byKey.TryGetValue(o.ObjectKeyFor(a.RelativePath), out var sz)
                            || sz != (o.Transformed ? cipherSizes.GetValueOrDefault(a.RelativePath, sz) : a.SizeBytes))
                        .Select(a => o.ObjectKeyFor(a.RelativePath)).ToList();
                    checks.Add(bad.Count == 0
                        ? new CheckResultDto("Object storage availability", CheckState.Passed, $"All {manifest.Count} object(s) present with expected sizes ({FormatBytes(expectedBytes)}).")
                        : new CheckResultDto("Object storage availability", CheckState.Failed, $"Missing or size-mismatched objects: {string.Join(", ", bad.Take(5))}."));
                }
                else
                {
                    expectedBytes = objects.Sum(x => x.Size);
                    checks.Add(new CheckResultDto("Object storage availability", CheckState.Passed, $"{objects.Count} object(s), {FormatBytes(expectedBytes)} found."));
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                checks.Add(new CheckResultDto("Object storage availability", CheckState.Failed, $"Could not list objects: {ex.Message}"));
            }
        }
        else
        {
            checks.Add(new CheckResultDto("Object storage availability", CheckState.Failed, "Destination no longer exists."));
        }

        // Target directory and worker reachability.
        var targetPath = string.Empty;
        try
        {
            var env = await new ExecutionEnvironmentFactory(db, secrets, keys).CreateAsync(ct);
            targetPath = ShellQuote.Join(env.NfsPath, o.JobName, $"restore-rp{o.Id}-{clock.UtcNow:yyyyMMddHHmmss}");
            await using var worker = await ssh.ConnectWorkerAsync(env.Worker, ct);
            var probe = await worker.RunAsync(
                $"test -d {ShellQuote.Quote(env.NfsPath)} && test -w {ShellQuote.Quote(env.NfsPath)} && echo WRITABLE; df -Pk {ShellQuote.Quote(env.NfsPath)} 2>/dev/null | tail -n 1 | awk '{{print \"FREE \" $4*1024}}'; " +
                $"command -v {ShellQuote.Quote(env.RclonePath)} >/dev/null 2>&1 && echo RCLONE; command -v {ShellQuote.Quote(encryption.Value.AgePath)} >/dev/null 2>&1 && echo AGETOOL",
                TimeSpan.FromSeconds(30), ct);
            checks.Add(probe.StdOut.Contains("WRITABLE", StringComparison.Ordinal)
                ? new CheckResultDto("NFS staging area", CheckState.Passed, $"Worker can write to {env.NfsPath}; files will be restored to a new isolated directory.")
                : new CheckResultDto("NFS staging area", CheckState.Failed, $"{env.NfsPath} is not writable on the worker."));
            var freeLine = probe.StdOut.Split('\n').FirstOrDefault(l => l.StartsWith("FREE ", StringComparison.Ordinal));
            if (freeLine is not null && long.TryParse(freeLine[5..].Trim(), out var free))
            {
                checks.Add(free > expectedBytes * 1.05
                    ? new CheckResultDto("Free space", CheckState.Passed, $"{FormatBytes(free)} free, {FormatBytes(expectedBytes)} required.")
                    : new CheckResultDto("Free space", CheckState.Failed, $"Only {FormatBytes(free)} free but {FormatBytes(expectedBytes)} are required."));
            }
            checks.Add(probe.StdOut.Contains("RCLONE", StringComparison.Ordinal)
                ? new CheckResultDto("Transfer tool", CheckState.Passed, "rclone is available on the worker.")
                : new CheckResultDto("Transfer tool", CheckState.Failed, "rclone is not installed on the worker."));
            if (o.Encrypted)
            {
                checks.Add(probe.StdOut.Contains("AGETOOL", StringComparison.Ordinal)
                    ? new CheckResultDto("Decryption tool", CheckState.Passed, "age is available on the worker host.")
                    : new CheckResultDto("Decryption tool", CheckState.Failed, "age is not installed on the worker host; encrypted recovery points cannot be decrypted."));
            }
        }
        catch (RemoteOperationException ex)
        {
            checks.Add(new CheckResultDto("Worker connectivity", CheckState.Failed, ex.Message));
        }

        var can = checks.All(c => c.State != CheckState.Failed);
        return new RestorePrecheckResponse(can, checks, targetPath, manifest.Count, expectedBytes, ConfirmationPhraseFor(o), o.ToSummary());
    }

    public async Task<RestoreSummaryDto> CreateRestoreAsync(CreateRestoreRequest r, string? idempotencyKey, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(idempotencyKey) && user.UserId is { } uid)
        {
            var existing = await db.IdempotencyRecords.AsNoTracking().FirstOrDefaultAsync(x => x.UserId == uid && x.Scope == "restore" && x.Key == idempotencyKey, ct);
            if (existing?.ResourceId is { } rid)
            {
                var prior = await db.RestoreRuns.AsNoTracking().Include(x => x.Server).FirstAsync(x => x.Id == rid, ct);
                return prior.ToSummary();
            }
        }

        var o = await db.OffloadRuns.Include(x => x.S3Destination).FirstOrDefaultAsync(x => x.Id == r.RecoveryPointId, ct)
            ?? throw new NotFoundException("Recovery point", r.RecoveryPointId);
        if (!string.Equals(r.ConfirmationPhrase.Trim(), ConfirmationPhraseFor(o), StringComparison.Ordinal))
        {
            await audit.RecordNowAsync("restore.confirmation_mismatch", "RecoveryPoint", o.Id.ToString(), AuditOutcome.Denied, new { o.JobName }, ct);
            throw RequestValidationException.For("confirmationPhrase", $"Type exactly: {ConfirmationPhraseFor(o)}");
        }
        if (o.Status != OffloadStatus.Completed || o.RetentionState == RetentionState.Deleted || o.RetentionState == RetentionState.Deleting)
        {
            throw new ConflictException("recovery_point_unavailable", "The recovery point is not available for restore.");
        }

        // Encrypted point: the key is checked here, in the API process, before anything is created.
        byte[]? keyLine = null;
        if (o.Encrypted)
        {
            // Backora-held key: decrypted from the database with the app's encryption key; nobody has to paste it.
            keyLine = string.IsNullOrWhiteSpace(r.DecryptionKey) ? await BackoraKey.KeyLineForAsync(db, secrets, o.EncryptionRecipient, ct) : null;
            if (keyLine is not null) audit.Record("restore.decryption_key_from_backora", "RecoveryPoint", o.Id.ToString(), details: new { o.JobName, keyFingerprint = o.EncryptionKeyFingerprint });
            else keyLine = await AcceptKeyAsync(o, r.DecryptionKey, "decryptionKey", "RecoveryPoint", o.Id.ToString(), ct);
        }

        var storageSettings = await db.StorageSettings.AsNoTracking().FirstOrDefaultAsync(ct)
            ?? throw new DomainRuleException("nfs_not_configured", "NFS settings are not configured.");
        var now = clock.UtcNow;
        var restore = new RestoreRun
        {
            OffloadRunId = o.Id,
            ServerId = o.ServerId,
            JobName = o.JobName,
            TargetPath = ShellQuote.Join(storageSettings.NfsPath, o.JobName, $"restore-rp{o.Id}-{now:yyyyMMddHHmmss}"),
            Reason = r.Reason.Trim(),
            RequestedByUserId = user.UserId,
            RequestedByName = user.Name,
            MaxAttempts = 2,
            NextAttemptAt = now,
            CorrelationId = user.CorrelationId,
            FilesExpected = null,
            RequiresDecryptionKey = o.Encrypted,
            DecryptionKeyFingerprint = o.Encrypted ? o.EncryptionKeyFingerprint : null,
        };
        restore.AppendLogLine(now, $"Restore requested by {user.Name}: {restore.Reason}");
        db.RestoreRuns.Add(restore);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException?.Message.Contains("ux_restore_runs_one_active_per_point", StringComparison.Ordinal) == true)
        {
            if (keyLine is not null) System.Security.Cryptography.CryptographicOperations.ZeroMemory(keyLine);
            throw new ConflictException("restore_already_active", "A restore of this recovery point is already in progress.");
        }
        catch
        {
            if (keyLine is not null) System.Security.Cryptography.CryptographicOperations.ZeroMemory(keyLine);
            throw;
        }
        if (keyLine is not null)
        {
            // Ownership of the buffer moves to the courier (memory only; zeroed on delivery or expiry).
            courier.Submit(restore.Id, keyLine, now + encryption.Value.KeyRequestTimeout);
        }
        if (!string.IsNullOrWhiteSpace(idempotencyKey) && user.UserId is { } u)
        {
            db.IdempotencyRecords.Add(new IdempotencyRecord { UserId = u, Scope = "restore", Key = idempotencyKey, ResourceType = nameof(RestoreRun), ResourceId = restore.Id, CreatedAt = now });
        }
        audit.Record("restore.requested", "Restore", restore.Id.ToString(), details: new { recoveryPoint = o.Id, o.JobName, restore.TargetPath, restore.Reason, encrypted = o.Encrypted, keyFingerprint = restore.DecryptionKeyFingerprint });
        await db.SaveChangesAsync(ct);
        return restore.ToSummary();
    }

    public async Task<PagedResult<RestoreSummaryDto>> ListRestoresAsync(RestoreStatusDto? status, string? q, PageRequest page, CancellationToken ct)
    {
        var query = db.RestoreRuns.AsNoTracking().Include(r => r.Server).AsQueryable();
        if (status is { } s) query = query.Where(r => r.Status == (RestoreStatus)(int)s);
        if (!string.IsNullOrWhiteSpace(q))
        {
            var term = $"%{q.Trim()}%";
            query = query.Where(r => EF.Functions.ILike(r.JobName, term) || EF.Functions.ILike(r.TargetPath, term) || (r.RequestedByName != null && EF.Functions.ILike(r.RequestedByName, term)));
        }
        query = query.OrderByDescending(r => r.CreatedAt).ThenByDescending(r => r.Id);
        var total = await query.LongCountAsync(ct);
        var items = await query.Skip(page.Skip).Take(page.SafePageSize).ToListAsync(ct);
        return new PagedResult<RestoreSummaryDto>(items.Select(x => x.ToSummary()).ToList(), page.SafePage, page.SafePageSize, total);
    }

    public async Task<RestoreDetailDto> GetRestoreAsync(long id, CancellationToken ct)
    {
        var r = await db.RestoreRuns.AsNoTracking().Include(x => x.Server)
            .Include(x => x.OffloadRun!).ThenInclude(o => o.S3Destination)
            .FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("Restore", id);
        var timeline = new List<TimelineEntryDto> { new(r.CreatedAt, "Requested", $"by {r.RequestedByName}: {r.Reason}", "neutral") };
        if (r.StartedAt is { } st) timeline.Add(new(st, "Transfer started", r.TargetPath, "info"));
        if (r.KeyRequestedAt is { } kr)
        {
            timeline.Add(new(kr, "Waiting for decryption key", $"Key for recipient {r.DecryptionKeyFingerprint} requested; expires {r.KeyRequestExpiresAt:yyyy-MM-dd HH:mm} UTC", "warning"));
        }
        if (r.CancelRequestedAt is { } cr) timeline.Add(new(cr, "Cancellation requested", null, "warning"));
        if (r.CompletedAt is { } c)
        {
            timeline.Add(r.Status switch
            {
                RestoreStatus.Completed => new(c, "Completed and validated", r.VerificationStatus == VerificationStatus.Verified ? "SHA-256 manifest matched" : "Verified against object storage checksums", "success"),
                RestoreStatus.Failed => new(c, $"Failed · {r.FailureClass}", r.ErrorMessage, "danger"),
                _ => new(c, r.Status.ToString(), r.ErrorMessage, "muted"),
            });
        }
        return new RestoreDetailDto(r.ToSummary(), r.Log, r.OffloadRun!.ToSummary(), timeline);
    }

    public async Task<RestoreSummaryDto> CancelRestoreAsync(long id, CancellationToken ct)
    {
        var r = await db.RestoreRuns.Include(x => x.Server).FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("Restore", id);
        var now = clock.UtcNow;
        switch (r.Status)
        {
            case RestoreStatus.Pending:
                r.CancelRequestedAt = now;
                r.FailureClass = FailureClass.Cancelled;
                r.ErrorMessage = "Cancelled before start.";
                r.TransitionTo(RestoreStatus.Cancelled, now);
                break;
            case RestoreStatus.Running:
            case RestoreStatus.AwaitingKey:
                r.CancelRequestedAt = now;
                r.TransitionTo(RestoreStatus.Cancelling, now);
                break;
            default:
                throw new ConflictException("restore_not_cancellable", $"The restore is {r.Status} and cannot be cancelled.");
        }
        courier.Discard(id);
        audit.Record("restore.cancel_requested", "Restore", id.ToString(), details: new { r.JobName });
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateConcurrencyException) { throw new ConflictException("restore_state_changed", "The restore changed state. Refresh and try again."); }
        return r.ToSummary();
    }

    // ───────────── Customer-key decryption ─────────────

    /// <summary>
    /// Supplies the private key to a restore that waits for it (e.g. after an API restart lost the in-memory
    /// key, or for a retry attempt). Same validation as at creation; the key is held in memory only.
    /// </summary>
    public async Task<RestoreSummaryDto> SubmitDecryptionKeyAsync(long id, SubmitDecryptionKeyRequest request, CancellationToken ct)
    {
        var r = await db.RestoreRuns.AsNoTracking().Include(x => x.Server).Include(x => x.OffloadRun).FirstOrDefaultAsync(x => x.Id == id, ct)
            ?? throw new NotFoundException("Restore", id);
        if (!r.RequiresDecryptionKey || r.OffloadRun is not { Encrypted: true } point)
        {
            throw new ConflictException("restore_key_not_required", "This restore is not of an encrypted recovery point; no key is needed.");
        }
        // Waiting for a key: AwaitingKey, or an attempt that has not reached it yet (the courier waits).
        var waiting = r.Status == RestoreStatus.AwaitingKey || r.Status == RestoreStatus.Pending || (r.Status == RestoreStatus.Running && r.KeyRequestedAt is null);
        if (!waiting)
        {
            throw new ConflictException("restore_not_awaiting_key", $"The restore is {r.Status} and is not waiting for a decryption key.");
        }
        var keyLine = await AcceptKeyAsync(point, request.PrivateKey, "privateKey", "Restore", id.ToString(), ct);
        var deadline = r.KeyRequestExpiresAt is { } exp && exp > clock.UtcNow ? exp : clock.UtcNow + encryption.Value.KeyRequestTimeout;
        courier.Submit(id, keyLine, deadline);
        audit.Record("restore.decryption_key_submitted", "Restore", id.ToString(), details: new { r.JobName, keyFingerprint = point.EncryptionKeyFingerprint });
        await db.SaveChangesAsync(ct);
        return r.ToSummary();
    }

    /// <summary>Checks a private key against a recovery point's recorded recipient (nothing is started or stored).</summary>
    public async Task<KeyVerificationDto> VerifyDecryptionKeyAsync(long recoveryPointId, VerifyDecryptionKeyRequest request, CancellationToken ct)
    {
        var o = await db.OffloadRuns.AsNoTracking().FirstOrDefaultAsync(x => x.Id == recoveryPointId, ct) ?? throw new NotFoundException("Recovery point", recoveryPointId);
        if (!o.Encrypted || o.EncryptionRecipient is null)
        {
            return new KeyVerificationDto(false, null, "This recovery point is not encrypted; no key is required to restore it.");
        }
        var matches = AgeKeys.Matches(request.PrivateKey, o.EncryptionRecipient, out var derived, out var error);
        await audit.RecordNowAsync("recovery_point.decryption_key_checked", "RecoveryPoint", o.Id.ToString(), matches ? AuditOutcome.Success : AuditOutcome.Failure,
            new { o.JobName, expected = o.EncryptionKeyFingerprint, matches }, ct);
        if (error is not null) return new KeyVerificationDto(false, null, error);
        return matches
            ? new KeyVerificationDto(true, derived, $"The key matches recipient {o.EncryptionKeyFingerprint}.")
            : new KeyVerificationDto(false, derived, $"This key belongs to {derived}, but the recovery point was encrypted to {o.EncryptionKeyFingerprint}.");
    }

    /// <summary>Validates a supplied identity against the point's recipient; returns the canonical key line (caller owns/zeroes it).</summary>
    private async Task<byte[]> AcceptKeyAsync(OffloadRun point, string? key, string field, string resourceType, string resourceId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            throw new UnprocessableRequestException("decryption_key_required", field,
                $"A decryption key is required: this recovery point is encrypted (age, recipient {point.EncryptionKeyFingerprint}). Supply the matching private key (\"AGE-SECRET-KEY-1…\").");
        }
        if (!AgeKeys.Matches(key, point.EncryptionRecipient ?? string.Empty, out var derived, out var error))
        {
            await audit.RecordNowAsync("restore.decryption_key_rejected", resourceType, resourceId, AuditOutcome.Denied,
                new { point.JobName, expected = point.EncryptionKeyFingerprint, supplied = derived }, ct);
            throw new UnprocessableRequestException("decryption_key_mismatch", field, error ?? (derived is null
                ? "The decryption key is not a valid age private key."
                : $"The decryption key does not match this recovery point: it belongs to {derived}, the point was encrypted to {point.EncryptionKeyFingerprint}."));
        }
        return AgeKeys.IdentityLineBytes(key);
    }

    public static string FormatBytes(long bytes)
    {
        string[] u = ["B", "KiB", "MiB", "GiB", "TiB", "PiB"];
        double v = bytes;
        var i = 0;
        while (v >= 1024 && i < u.Length - 1) { v /= 1024; i++; }
        return $"{v:0.##} {u[i]}";
    }
}
