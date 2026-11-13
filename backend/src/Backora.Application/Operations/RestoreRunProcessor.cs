using Backora.Application.Abstractions;
using Backora.Application.Encryption;
using Backora.Application.Execution;
using Backora.Application.Notifications;
using Backora.Application.Telemetry;
using Backora.Domain;
using Backora.Domain.Backups;
using Backora.Domain.Execution;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Backora.Application.Operations;

/// <summary>
/// Restores a recovery point from S3 into a fresh, isolated NFS directory (never over existing data) and
/// validates every restored file against the SHA-256 manifest captured at backup time.
///
/// Encrypted (age) recovery points: the worker prepares the work dir, creates a 0600 FIFO and starts a
/// detached script that blocks reading the operator's private key from it; the restore then waits in
/// AwaitingKey. The key is written into the FIFO by the API's key courier — it never passes through this
/// process, the database or a queue. Once the script reports the key was received, the restore proceeds
/// (rclone cat | age -d) and the plaintext is verified against the SHA-256 manifest as usual.
/// </summary>
public sealed class RestoreRunProcessor(IServiceScopeFactory scopes, IOptions<WorkerOptions> options, IOptions<ClientEncryptionOptions> encryption, ILogger<RestoreRunProcessor> logger)
    : OperationProcessorBase(scopes, options, logger)
{
    private ClientEncryptionOptions Encryption => encryption.Value;

    public override string Kind => OperationKinds.Restore;

    protected override async Task<StepOutcome> StepAsync(IServiceProvider sp, long id, OperationContext ctx, CancellationToken ct)
    {
        var db = sp.GetRequiredService<IAppDbContext>();
        var clock = sp.GetRequiredService<IClock>();
        var restore = await db.RestoreRuns
            .Include(r => r.OffloadRun!).ThenInclude(o => o.S3Destination)
            .Include(r => r.OffloadRun!).ThenInclude(o => o.BackupRun!).ThenInclude(b => b.Artifacts)
            .FirstOrDefaultAsync(r => r.Id == id, ct);
        if (restore is null || restore.IsTerminal) return StepOutcome.Terminal;

        var now = clock.UtcNow;
        restore.ProcessedBy = ctx.Owner;
        restore.HeartbeatAt = now;

        try
        {
            switch (restore.Status)
            {
                case RestoreStatus.Pending:
                    restore.Attempt++;
                    restore.TransitionTo(RestoreStatus.Running, now);
                    var env0 = await sp.GetRequiredService<ExecutionEnvironmentFactory>().CreateAsync(ct);
                    restore.RemoteDirectory = $"{env0.WorkerWorkDirectory.TrimEnd('/')}/backora-restore-{restore.Id}-a{restore.Attempt}";
                    restore.RemotePid = null;
                    restore.LogOffset = 0;
                    restore.KeyRequestedAt = null;
                    restore.KeyRequestExpiresAt = null;
                    restore.AppendLogLine(now, $"Attempt {restore.Attempt}/{restore.MaxAttempts}: restoring {Storage.StorageConnections.Describe(restore.OffloadRun?.S3Destination, restore.OffloadRun?.RemotePrefix ?? "")} → {restore.TargetPath}");
                    if (restore.RequiresDecryptionKey)
                    {
                        restore.AppendLogLine(now, $"Recovery point is encrypted (age, recipient {restore.DecryptionKeyFingerprint}); the private key is required for this attempt and is never stored");
                    }
                    BackoraTelemetry.OperationsStarted.Add(1, new System.Diagnostics.TagList { { "kind", Kind } });
                    await db.SaveChangesAsync(ct);
                    return StepOutcome.Continue;
                case RestoreStatus.Running:
                    return await TransferAsync(sp, db, restore, ctx, now, ct);
                case RestoreStatus.AwaitingKey:
                    return await AwaitKeyAsync(sp, db, restore, ctx, now, ct);
                case RestoreStatus.Verifying:
                    return await VerifyAsync(sp, db, restore, ctx, now, ct);
                case RestoreStatus.Cancelling:
                    return await CancelAsync(sp, db, restore, ctx, now, ct);
                default:
                    return StepOutcome.Terminal;
            }
        }
        catch (RemoteOperationException ex)
        {
            await ctx.ResetSessionAsync();
            return await HandleFailureAsync(sp, db, restore, ex.FailureClass, ex.Message, clock.UtcNow, ct);
        }
        catch (DbUpdateConcurrencyException) { throw; }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Logger.LogError(ex, "Restore {RestoreId} failed unexpectedly", restore.Id);
            await ctx.ResetSessionAsync();
            return await HandleFailureAsync(sp, db, restore, FailureClass.Internal, ex.Message, clock.UtcNow, ct);
        }
    }

    private async Task<StepOutcome> TransferAsync(IServiceProvider sp, IAppDbContext db, RestoreRun restore, OperationContext ctx, DateTimeOffset now, CancellationToken ct)
    {
        var (env, worker) = await WorkerAsync(sp, ctx, ct);
        var dir = restore.RemoteDirectory!;
        var offload = restore.OffloadRun ?? throw new RemoteOperationException(FailureClass.Configuration, "Recovery point no longer exists.");
        var conn = Connection(sp, offload);

        if (restore.RemotePid is null)
        {
            var status0 = await DetachedProcess.GetStatusAsync(worker, dir, ct);
            if (status0.Pid is { } adopted)
            {
                restore.RemotePid = adopted;
            }
            else
            {
                var target = ShellQuote.Quote(restore.TargetPath);
                // Conflict protection: the isolated target directory must not exist or must be empty.
                var conflict = await worker.RunAsync(
                    $"if [ -e {target} ] && [ -n \"$(ls -A {target} 2>/dev/null)\" ]; then echo CONFLICT; else mkdir -p -m 750 {target} && echo READY; fi",
                    TimeSpan.FromSeconds(60), ct);
                if (conflict.StdOut.Contains("CONFLICT", StringComparison.Ordinal))
                {
                    throw new RemoteOperationException(FailureClass.Configuration, $"Target directory {restore.TargetPath} already contains data; refusing to overwrite.");
                }
                if (!conflict.StdOut.Contains("READY", StringComparison.Ordinal))
                {
                    throw new RemoteOperationException(FailureClass.Configuration, $"Cannot create target directory {restore.TargetPath}: {DetachedProcess.Trim(conflict.StdErr)}");
                }

                await DetachedProcess.PrepareDirectoryAsync(worker, dir, ct);
                await worker.WriteFileAsync($"{dir}/rclone.conf", Rclone.BuildConfig(conn), 600, ct);
                if (restore.RequiresDecryptionKey || offload.Encrypted)
                {
                    return await StartEncryptedAsync(db, restore, offload, env, worker, conn, dir, now, ct);
                }
                if (offload.Compression is not null)
                {
                    return await StartDecompressAsync(db, restore, offload, env, worker, conn, dir, now, ct);
                }
                var command = $"{ShellQuote.Quote(env.RclonePath)} copy {ShellQuote.Quote(Rclone.RemotePath(conn, offload.RemotePrefix))} {target} " +
                              $"--checksum {Rclone.CommonFlags("./rclone.conf")}";
                restore.RemotePid = await DetachedProcess.StartAsync(worker, dir, command, ct);
                restore.LastProgressAt = now;
                restore.AppendLogLine(now, $"rclone started (pid {restore.RemotePid})");
            }
            await db.SaveChangesAsync(ct);
            return StepOutcome.Poll;
        }

        var status = await DetachedProcess.GetStatusAsync(worker, dir, ct);
        var chunk = await DetachedProcess.ReadLogAsync(worker, dir, restore.LogOffset, ct);
        if (chunk.NewOffset != restore.LogOffset)
        {
            restore.LogOffset = chunk.NewOffset;
            if (restore.RequiresDecryptionKey || offload.Compression is not null)
            {
                // Progress of an encrypted or compressed restore = restored bytes written to the target directory.
                if (AgeScripts.LatestCounter(chunk.Text, "BKO_PROGRESS") is { } written)
                {
                    if (written > (restore.BytesTransferred ?? 0)) restore.LastProgressAt = now;
                    restore.BytesTransferred = restore.BytesTotal is { } total ? Math.Min(written, total) : written;
                }
            }
            else
            {
                var stats = Rclone.ParseLatestStats(chunk.Text);
                if (stats is not null)
                {
                    if (stats.Bytes > (restore.BytesTransferred ?? 0)) restore.LastProgressAt = now;
                    restore.BytesTransferred = stats.Bytes;
                    if (stats.TotalBytes > 0) restore.BytesTotal = stats.TotalBytes;
                }
            }
            restore.AppendLog(OffloadRunProcessor.Summarize(chunk.Text));
        }

        switch (status.State)
        {
            case RemoteProcessState.Running when now - (restore.LastProgressAt ?? restore.StartedAt ?? now) > TimeSpan.FromSeconds(Options.TransferStallSeconds):
                restore.AppendLogLine(now, $"No transfer progress for {Options.TransferStallSeconds}s; terminating rclone");
                await DetachedProcess.TerminateAsync(worker, dir, ct);
                return await HandleFailureAsync(sp, db, restore, FailureClass.StorageError, $"Transfer stalled: no progress for {Options.TransferStallSeconds} seconds (object storage unreachable or unresponsive).", now, ct);
            case RemoteProcessState.Running:
                await db.SaveChangesAsync(ct);
                return StepOutcome.Poll;
            case RemoteProcessState.Exited when status.ExitCode == 0:
                restore.AppendLogLine(now, "Transfer finished; validating restored files");
                restore.TransitionTo(RestoreStatus.Verifying, now);
                await db.SaveChangesAsync(ct);
                return StepOutcome.Continue;
            case RemoteProcessState.Exited when restore.RequiresDecryptionKey && status.ExitCode == AgeScripts.ExitDecrypt:
                var decryptLog = await DetachedProcess.ReadLogAsync(worker, dir, 0, ct);
                restore.VerificationStatus = VerificationStatus.Failed;
                return await HandleFailureAsync(sp, db, restore, FailureClass.VerificationFailed,
                    $"Decryption failed — the encrypted object could not be authenticated (corrupted or tampered ciphertext, or a key that does not match). " +
                    $"Partially restored files were removed; no unencrypted copy is substituted. {AgeScripts.ExtractErrors(decryptLog.Text)}".Trim(), now, ct, retry: false);
            case RemoteProcessState.Exited when status.ExitCode == TransferScripts.ExitCompress:
                var zLog = await DetachedProcess.ReadLogAsync(worker, dir, 0, ct);
                restore.VerificationStatus = VerificationStatus.Failed;
                return await HandleFailureAsync(sp, db, restore, FailureClass.VerificationFailed,
                    $"A compressed object could not be decompressed (corrupted in storage). Partially restored files were removed. {AgeScripts.ExtractErrors(zLog.Text)}".Trim(), now, ct, retry: false);
            case RemoteProcessState.Exited when offload.Compression is not null && !restore.RequiresDecryptionKey:
                var dLog = await DetachedProcess.ReadLogAsync(worker, dir, 0, ct);
                return await HandleFailureAsync(sp, db, restore, FailureClass.StorageError, $"Restore script exited with code {status.ExitCode}: {AgeScripts.ExtractErrors(dLog.Text)}".Trim(), now, ct);
            case RemoteProcessState.Exited when restore.RequiresDecryptionKey:
                var encLog = await DetachedProcess.ReadLogAsync(worker, dir, 0, ct);
                return await HandleFailureAsync(sp, db, restore, FailureClass.StorageError, $"Encrypted restore script exited with code {status.ExitCode}: {AgeScripts.ExtractErrors(encLog.Text)}".Trim(), now, ct);
            case RemoteProcessState.Exited:
                var full = await DetachedProcess.ReadLogAsync(worker, dir, 0, ct);
                return await HandleFailureAsync(sp, db, restore, FailureClass.StorageError, $"rclone exited with code {status.ExitCode}: {Rclone.ExtractErrors(full.Text)}".Trim(), now, ct);
            default:
                return await HandleFailureAsync(sp, db, restore, FailureClass.ProcessLost, "The rclone process ended without an exit code.", now, ct);
        }
    }

    /// <summary>
    /// Restore of a point the worker compressed before uploading: each object streamed through <c>zstd -dc</c> into the
    /// target, then verified against the SHA-256 manifest recorded at backup time like any other restore.
    /// </summary>
    private async Task<StepOutcome> StartDecompressAsync(IAppDbContext db, RestoreRun restore, OffloadRun offload, ExecutionEnvironment env, IRemoteSession worker,
        S3Connection conn, string dir, DateTimeOffset now, CancellationToken ct)
    {
        var manifest = offload.BackupRun?.Artifacts.OrderBy(a => a.RelativePath, StringComparer.Ordinal).ToList() ?? [];
        if (manifest.Count == 0)
        {
            throw new RemoteOperationException(FailureClass.Configuration, "The compressed recovery point has no artifact manifest; it cannot be restored.");
        }
        AgeScripts.EnsureListable(manifest.Select(a => a.RelativePath));
        var probe = await worker.RunAsync("command -v zstd >/dev/null 2>&1 && echo ZSTD", TimeSpan.FromSeconds(30), ct);
        if (!probe.StdOut.Contains("ZSTD", StringComparison.Ordinal))
        {
            throw new RemoteOperationException(FailureClass.Configuration, "zstd is not installed on the worker host; compressed recovery points cannot be restored until it is (package 'zstd').");
        }
        await worker.WriteFileAsync($"{dir}/{AgeScripts.FilesList}", string.Join('\n', manifest.Select(a => a.RelativePath)) + "\n", 600, ct);
        await worker.WriteFileAsync($"{dir}/{TransferScripts.RestoreList}", RestoreObjects(offload), 600, ct);
        await worker.WriteFileAsync($"{dir}/restore.sh",
            TransferScripts.DecompressRestore(env.RclonePath, Rclone.RemotePath(conn, offload.RemotePrefix), restore.TargetPath), 700, ct);
        restore.RemotePid = await DetachedProcess.StartAsync(worker, dir, "bash ./restore.sh", ct);
        restore.FilesExpected = manifest.Count;
        restore.BytesTotal = manifest.Sum(a => a.SizeBytes);
        restore.BytesTransferred = 0;
        restore.LastProgressAt = now;
        restore.AppendLogLine(now, $"Restoring {manifest.Count} zstd-compressed object(s) (pid {restore.RemotePid}); each is decompressed on the worker host and then checked against the backup's SHA-256");
        await db.SaveChangesAsync(ct);
        return StepOutcome.Poll;
    }

    /// <summary>
    /// What a compressed point's restore downloads: its stored objects (one per backup file, one archive per packed
    /// backup folder), as recorded when the upload was verified.
    /// </summary>
    private static string RestoreObjects(OffloadRun offload)
    {
        var stored = offload.EncryptedObjects;
        if (stored.Count == 0)
            throw new RemoteOperationException(FailureClass.Configuration, "The compressed recovery point has no record of its stored objects; it cannot be restored.");
        return StageItem.RestoreFile(stored.Select(e => (e.RelativePath, offload.ObjectKeyFor(e.RelativePath))));
    }

    /// <summary>Stages an encrypted restore: FIFO + detached script, then waits in AwaitingKey.</summary>
    private async Task<StepOutcome> StartEncryptedAsync(IAppDbContext db, RestoreRun restore, OffloadRun offload, ExecutionEnvironment env, IRemoteSession worker,
        S3Connection conn, string dir, DateTimeOffset now, CancellationToken ct)
    {
        var manifest = offload.BackupRun?.Artifacts.OrderBy(a => a.RelativePath, StringComparer.Ordinal).ToList() ?? [];
        if (manifest.Count == 0)
        {
            throw new RemoteOperationException(FailureClass.Configuration, "The encrypted recovery point has no artifact manifest; it cannot be restored.");
        }
        AgeScripts.EnsureListable(manifest.Select(a => a.RelativePath));
        var age = ShellQuote.Quote(Encryption.AgePath);
        var probe = await worker.RunAsync($"command -v {age} >/dev/null 2>&1 && {age} --version 2>&1 | head -n 1 || echo NOAGE", TimeSpan.FromSeconds(30), ct);
        if (probe.StdOut.Contains("NOAGE", StringComparison.Ordinal))
        {
            throw new RemoteOperationException(FailureClass.Configuration, $"The age tool ('{Encryption.AgePath}') is not installed on the worker host; encrypted recovery points cannot be restored until it is (Alpine/Debian package 'age', version 1.1 or later).");
        }

        await worker.WriteFileAsync($"{dir}/{AgeScripts.FilesList}", string.Join('\n', manifest.Select(a => a.RelativePath)) + "\n", 600, ct);
        if (offload.Compression is not null) await worker.WriteFileAsync($"{dir}/{TransferScripts.RestoreList}", RestoreObjects(offload), 600, ct);
        await worker.WriteFileAsync($"{dir}/restore.sh",
            AgeScripts.DecryptRestore(Encryption.AgePath, env.RclonePath, Rclone.RemotePath(conn, offload.RemotePrefix), restore.TargetPath, decompress: offload.Compression is not null), 700, ct);
        var fifo = await worker.RunAsync($"cd {ShellQuote.Quote(dir)} && rm -f ./{AgeScripts.KeyFifo} ./{AgeScripts.KeyReceivedMarker} && mkfifo -m 600 ./{AgeScripts.KeyFifo} && echo FIFO", TimeSpan.FromSeconds(30), ct);
        if (!fifo.StdOut.Contains("FIFO", StringComparison.Ordinal))
        {
            throw new RemoteOperationException(FailureClass.Configuration, $"Could not create the key FIFO on the worker host: {DetachedProcess.Trim(fifo.StdErr)}");
        }
        restore.RemotePid = await DetachedProcess.StartAsync(worker, dir, "bash ./restore.sh", ct);
        restore.FilesExpected = manifest.Count;
        restore.BytesTotal = manifest.Sum(a => a.SizeBytes);
        restore.BytesTransferred = 0;
        restore.KeyRequestedAt = now;
        restore.KeyRequestExpiresAt = now + Encryption.KeyRequestTimeout;
        restore.LastProgressAt = now;
        restore.TransitionTo(RestoreStatus.AwaitingKey, now);
        restore.AppendLogLine(now, $"Encrypted restore staged on the worker host (pid {restore.RemotePid}); waiting for the private key until {restore.KeyRequestExpiresAt:u}");
        restore.NextAttemptAt = now.AddSeconds(Options.PollIntervalSeconds);
        await db.SaveChangesAsync(ct);
        return StepOutcome.Yield;
    }

    /// <summary>AwaitingKey: proceed once the script received the key; expire otherwise. Never holds a dispatcher slot.</summary>
    private async Task<StepOutcome> AwaitKeyAsync(IServiceProvider sp, IAppDbContext db, RestoreRun restore, OperationContext ctx, DateTimeOffset now, CancellationToken ct)
    {
        var (_, worker) = await WorkerAsync(sp, ctx, ct);
        var dir = restore.RemoteDirectory ?? throw new RemoteOperationException(FailureClass.Internal, "Restore work directory is unknown.");
        var marker = await worker.RunAsync($"test -e {ShellQuote.Quote(dir + "/" + AgeScripts.KeyReceivedMarker)} && echo RECEIVED || echo WAITING", TimeSpan.FromSeconds(30), ct);
        if (marker.StdOut.Contains("RECEIVED", StringComparison.Ordinal))
        {
            restore.TransitionTo(RestoreStatus.Running, now);
            restore.LastProgressAt = now;
            restore.AppendLogLine(now, "Decryption key received on the worker host; streaming and decrypting objects");
            await db.SaveChangesAsync(ct);
            return StepOutcome.Continue;
        }

        if (restore.KeyRequestExpiresAt is { } expires && now > expires)
        {
            await DetachedProcess.TerminateAsync(worker, dir, ct);
            await RemoveWorkDirAsync(worker, dir, ct);
            await worker.RunAsync($"rmdir {ShellQuote.Quote(restore.TargetPath)} 2>/dev/null; true", TimeSpan.FromSeconds(30), ct);
            restore.RemotePid = null;
            return await HandleFailureAsync(sp, db, restore, FailureClass.Timeout,
                $"The decryption key was not supplied in time (the request expired at {expires:u}). Nothing was decrypted; start a new restore and supply the key.", now, ct, retry: false);
        }

        var status = await DetachedProcess.GetStatusAsync(worker, dir, ct);
        if (status.State != RemoteProcessState.Running)
        {
            var log = await DetachedProcess.ReadLogAsync(worker, dir, 0, ct);
            restore.RemotePid = null;
            await RemoveWorkDirAsync(worker, dir, ct);
            return await HandleFailureAsync(sp, db, restore, FailureClass.ProcessLost,
                $"The restore script on the worker host stopped while waiting for the key (exit {status.ExitCode?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "unknown"}). {AgeScripts.ExtractErrors(log.Text)}".Trim(), now, ct, retry: false);
        }

        restore.NextAttemptAt = now.AddSeconds(Options.PollIntervalSeconds);
        await db.SaveChangesAsync(ct);
        return StepOutcome.Yield;
    }

    private async Task<StepOutcome> VerifyAsync(IServiceProvider sp, IAppDbContext db, RestoreRun restore, OperationContext ctx, DateTimeOffset now, CancellationToken ct)
    {
        var (env, worker) = await WorkerAsync(sp, ctx, ct);
        var offload = restore.OffloadRun!;
        var manifest = offload.BackupRun?.Artifacts.ToList() ?? [];

        if (manifest.Count > 0)
        {
            var restored = await RemoteFiles.HashAsync(worker, restore.TargetPath, manifest.Select(a => a.RelativePath), ct);
            var byPath = restored.ToDictionary(f => f.RelativePath, StringComparer.Ordinal);
            var problems = new List<string>();
            foreach (var a in manifest)
            {
                if (!byPath.TryGetValue(a.RelativePath, out var f)) problems.Add($"{a.RelativePath}: missing");
                else if (f.SizeBytes != a.SizeBytes) problems.Add($"{a.RelativePath}: size {f.SizeBytes} ≠ {a.SizeBytes}");
                else if (!string.Equals(f.Sha256, a.Sha256, StringComparison.OrdinalIgnoreCase)) problems.Add($"{a.RelativePath}: SHA-256 mismatch");
            }
            restore.FilesExpected = manifest.Count;
            restore.FilesVerified = manifest.Count - problems.Count;
            if (problems.Count > 0)
            {
                restore.VerificationStatus = VerificationStatus.Failed;
                return await HandleFailureAsync(sp, db, restore, FailureClass.VerificationFailed, "Restored files do not match the backup manifest: " + string.Join("; ", problems.Take(10)), now, ct, retry: false);
            }
            restore.VerificationStatus = VerificationStatus.Verified;
            restore.AppendLogLine(now, $"All {manifest.Count} restored file(s) match the SHA-256 manifest recorded at backup time");
        }
        else
        {
            // Legacy recovery points have no Backora manifest: verify against object-store checksums instead.
            var conn = Connection(sp, offload);
            var check = await worker.RunAsync(
                $"cd {ShellQuote.Quote(restore.RemoteDirectory!)} && {ShellQuote.Quote(env.RclonePath)} check {ShellQuote.Quote(Rclone.RemotePath(conn, offload.RemotePrefix))} {ShellQuote.Quote(restore.TargetPath)} --one-way --config ./rclone.conf > ./check.log 2>&1; echo \"RC=$?\"; tail -n 20 ./check.log",
                TimeSpan.FromHours(6), ct);
            if (!check.StdOut.TrimStart().StartsWith("RC=0", StringComparison.Ordinal))
            {
                restore.VerificationStatus = VerificationStatus.Failed;
                return await HandleFailureAsync(sp, db, restore, FailureClass.VerificationFailed, $"rclone check found differences: {check.StdOut[^Math.Min(600, check.StdOut.Length)..]}", now, ct, retry: false);
            }
            restore.VerificationStatus = VerificationStatus.Partial;
            restore.AppendLogLine(now, "Verified against object storage checksums (legacy recovery point without a SHA-256 manifest)");
        }

        restore.TransitionTo(RestoreStatus.Completed, now);
        restore.AppendLogLine(now, $"Restore completed. Files are available at {restore.TargetPath}");
        await sp.GetRequiredService<NotificationPublisher>().PublishRestoreAsync(restore, ct);
        await db.SaveChangesAsync(ct);
        BackoraTelemetry.RecordTerminal(Kind, "completed", FailureClass.None.ToString(), restore.Duration);
        await RemoveWorkDirAsync(worker, restore.RemoteDirectory!, ct);
        return StepOutcome.Terminal;
    }

    private async Task<StepOutcome> CancelAsync(IServiceProvider sp, IAppDbContext db, RestoreRun restore, OperationContext ctx, DateTimeOffset now, CancellationToken ct)
    {
        if (restore.RemotePid is not null && restore.RemoteDirectory is not null)
        {
            var (_, worker) = await WorkerAsync(sp, ctx, ct);
            await DetachedProcess.TerminateAsync(worker, restore.RemoteDirectory, ct);
            await RemoveWorkDirAsync(worker, restore.RemoteDirectory, ct);
        }
        restore.FailureClass = FailureClass.Cancelled;
        restore.ErrorMessage = "Cancelled by user. Partially restored files may remain in the target directory.";
        restore.AppendLogLine(now, restore.ErrorMessage);
        restore.TransitionTo(RestoreStatus.Cancelled, now);
        await db.SaveChangesAsync(ct);
        BackoraTelemetry.RecordTerminal(Kind, "cancelled", FailureClass.Cancelled.ToString(), restore.Duration);
        return StepOutcome.Terminal;
    }

    private async Task<StepOutcome> HandleFailureAsync(IServiceProvider sp, IAppDbContext db, RestoreRun restore, FailureClass failureClass, string message,
        DateTimeOffset now, CancellationToken ct, bool retry = true)
    {
        Logger.LogWarning("Restore {RestoreId} error ({FailureClass}): {Message}", restore.Id, failureClass, message);
        if (restore.Status is RestoreStatus.Running or RestoreStatus.Verifying or RestoreStatus.AwaitingKey && restore.RemotePid is not null && IsTransient(failureClass))
        {
            restore.AppendLogLine(now, $"Transient error, will retry: {message}");
            await db.SaveChangesAsync(ct);
            return StepOutcome.Poll;
        }

        if (retry && restore.Status == RestoreStatus.Running && IsTransient(failureClass) && restore.TryScheduleRetry(now))
        {
            restore.AppendLogLine(now, $"Attempt {restore.Attempt} failed: {message}. Retrying at {restore.NextAttemptAt:u}");
            restore.RemotePid = null;
            restore.TransitionTo(RestoreStatus.Pending, now);
            await db.SaveChangesAsync(ct);
            return StepOutcome.Yield;
        }

        if (restore.IsTerminal) return StepOutcome.Terminal;
        if (restore.RemoteDirectory is { } workDir && restore.RequiresDecryptionKey)
        {
            // Encrypted restores: never leave the script, FIFO or credentials behind on the worker host.
            try
            {
                var (_, worker) = await WorkerAsync(sp, new OperationContext("cleanup"), ct);
                await DetachedProcess.TerminateAsync(worker, workDir, ct);
                await RemoveWorkDirAsync(worker, workDir, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { Logger.LogDebug(ex, "cleanup after failed restore"); }
        }
        if (restore.Status == RestoreStatus.Cancelling)
        {
            restore.FailureClass = FailureClass.Cancelled;
            restore.TransitionTo(RestoreStatus.Cancelled, now);
        }
        else
        {
            restore.Fail(failureClass, message, now);
        }
        await sp.GetRequiredService<NotificationPublisher>().PublishRestoreAsync(restore, ct);
        await db.SaveChangesAsync(ct);
        BackoraTelemetry.RecordTerminal(Kind, "failed", failureClass.ToString(), restore.Duration);
        return StepOutcome.Terminal;
    }

    private static S3Connection Connection(IServiceProvider sp, OffloadRun offload)
    {
        var dest = offload.S3Destination ?? throw new RemoteOperationException(FailureClass.Configuration, "S3 destination no longer exists.");
        return Storage.StorageConnections.For(dest, sp.GetRequiredService<ISecretProtector>());
    }

    private static async Task<(ExecutionEnvironment Env, IRemoteSession Worker)> WorkerAsync(IServiceProvider sp, OperationContext ctx, CancellationToken ct)
    {
        var env = await sp.GetRequiredService<ExecutionEnvironmentFactory>().CreateAsync(ct);
        var connector = sp.GetRequiredService<ISshConnector>();
        return (env, await ctx.GetSessionAsync("worker", () => connector.ConnectWorkerAsync(env.Worker, ct)));
    }

    private async Task RemoveWorkDirAsync(IRemoteSession worker, string dir, CancellationToken ct)
    {
        try { await DetachedProcess.RemoveDirectoryAsync(worker, dir, ct); }
        catch (Exception ex) when (ex is not OperationCanceledException) { Logger.LogWarning(ex, "Failed to remove restore work dir {Dir}", dir); }
    }
}
