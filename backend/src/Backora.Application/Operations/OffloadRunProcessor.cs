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
/// Copies a run's verified artifacts to S3 with rclone on the worker, then verifies them twice:
/// <c>rclone check</c> (size + hash) and an independent object listing via the S3 API. Only after both
/// succeed is the NFS copy removed — and only the files in this run's manifest. A verified primary upload
/// then queues one S3 → S3 replica per DR destination configured on the job; replicas go through the same
/// transfer → double verification pipeline, reading from the source recovery point instead of NFS.
///
/// Customer-key destinations (age): the artifacts are encrypted on the worker host into a 0700 stage
/// directory inside the attempt's work dir (ciphertext only), the staged ciphertext is uploaded as
/// <c>&lt;rel&gt;.age</c> and verified the same way (rclone check against the stage + S3 listing with ciphertext
/// sizes, plus the age header of every staged file). Plaintext is never uploaded to such a destination:
/// a missing or failing <c>age</c> fails the offload.
/// </summary>
public sealed class OffloadRunProcessor(IServiceScopeFactory scopes, IOptions<WorkerOptions> options, IOptions<ClientEncryptionOptions> encryption, ILogger<OffloadRunProcessor> logger)
    : OperationProcessorBase(scopes, options, logger)
{
    public override string Kind => OperationKinds.Offload;

    private ClientEncryptionOptions Encryption => encryption.Value;

    /// <summary>How the objects of this transfer are produced.</summary>
    public enum TransferMode
    {
        /// <summary>Copy the files as they are (compression off, unencrypted destination, unencrypted source).</summary>
        Plain,
        /// <summary>Primary upload, compressed (zstd) and/or encrypted (age) on the worker into a staging folder on the share, then uploaded.</summary>
        StageFiles,
        /// <summary>Replica of an unencrypted point to a customer-key destination: rclone cat | age into the stage, upload ciphertext.</summary>
        EncryptStream,
        /// <summary>Replica of a compressed and/or encrypted point: copy the stored objects unchanged.</summary>
        CopyStored,
    }

    public static TransferMode ModeOf(OffloadRun o) => o.SourceOffloadRun is { } src
        ? src.Encrypted ? TransferMode.CopyStored : o.Encrypted ? TransferMode.EncryptStream : src.Compression is not null ? TransferMode.CopyStored : TransferMode.Plain
        : o.Transformed ? TransferMode.StageFiles : TransferMode.Plain;

    /// <summary>
    /// Decides encryption once, when the first attempt starts, from the destination's settings at that moment.
    /// Later changes to the destination never alter an offload that is already under way.
    /// </summary>
    public static void SnapshotEncryption(OffloadRun offload, string? uploadCompression = null)
    {
        var dest = offload.S3Destination ?? throw new RemoteOperationException(FailureClass.Configuration, "S3 destination no longer exists.");
        // Compression is decided once too: a primary upload follows the worker setting; a replica keeps its source's.
        var alreadyCompressed = offload.BackupRun?.Artifacts is { Count: > 0 } arts && arts.All(a => BackupSizeGuard.IsCompressed(a.RelativePath));
        offload.Compression = offload.SourceOffloadRun is { } origin ? origin.Compression
            : !alreadyCompressed && string.Equals(uploadCompression, OffloadRun.Zstd, StringComparison.OrdinalIgnoreCase) ? OffloadRun.Zstd : null;
        var wantsKey = dest.EncryptionMode == EncryptionMode.CustomerKey;
        if (wantsKey && string.IsNullOrWhiteSpace(dest.EncryptionRecipient))
        {
            throw new RemoteOperationException(FailureClass.Configuration, $"Destination {dest.Name} requires customer-key encryption but has no recipient configured; refusing to upload.");
        }
        if (offload.SourceOffloadRun is { } src)
        {
            if (src.Encrypted)
            {
                if (wantsKey && !string.Equals(src.EncryptionRecipient, dest.EncryptionRecipient, StringComparison.Ordinal))
                {
                    throw new RemoteOperationException(FailureClass.Configuration,
                        $"The source recovery point is encrypted to a different key ({src.EncryptionKeyFingerprint}) than destination {dest.Name} ({dest.EncryptionKeyFingerprint}). " +
                        "Re-encrypting requires the private key and is not supported; copy it to a destination that uses the same recipient or no encryption.");
                }
                offload.InheritEncryption(src); // ciphertext is copied unchanged and keeps the source's key metadata
                return;
            }
            if (!wantsKey) return;
        }
        else if (!wantsKey) return;

        offload.Encrypted = true;
        offload.EncryptionFormat = AgeKeys.FileFormat;
        offload.EncryptionRecipient = dest.EncryptionRecipient;
        offload.EncryptionKeyFingerprint = dest.EncryptionKeyFingerprint;
        offload.EncryptedObjectsJson = null;
    }

    protected override async Task<StepOutcome> StepAsync(IServiceProvider sp, long id, OperationContext ctx, CancellationToken ct)
    {
        var db = sp.GetRequiredService<IAppDbContext>();
        var clock = sp.GetRequiredService<IClock>();
        var offload = await db.OffloadRuns
            .Include(o => o.S3Destination)
            .Include(o => o.BackupRun!).ThenInclude(r => r.Artifacts)
            .Include(o => o.SourceOffloadRun!).ThenInclude(s => s.S3Destination)
            .FirstOrDefaultAsync(o => o.Id == id, ct);
        if (offload is null || offload.IsTerminal) return StepOutcome.Terminal;

        var now = clock.UtcNow;
        offload.ProcessedBy = ctx.Owner;
        offload.HeartbeatAt = now;

        try
        {
            switch (offload.Status)
            {
                case OffloadStatus.Pending:
                    offload.Attempt++;
                    if (offload.Attempt == 1) SnapshotEncryption(offload, Options.UploadCompression);
                    offload.TransitionTo(OffloadStatus.Running, now);
                    if (string.IsNullOrEmpty(offload.RemotePrefix))
                    {
                        offload.RemotePrefix = offload.SourceOffloadRun is { } src
                            ? OffloadRun.BuildReplicaPrefix(offload.S3Destination?.PathPrefix, src.S3Destination?.PathPrefix, src.RemotePrefix)
                            : OffloadRun.BuildPrefix(offload.S3Destination?.PathPrefix, offload.JobName, now, offload.Id);
                    }
                    var env0 = await sp.GetRequiredService<ExecutionEnvironmentFactory>().CreateAsync(ct);
                    offload.RemoteDirectory = $"{env0.WorkerWorkDirectory.TrimEnd('/')}/backora-offload-{offload.Id}-a{offload.Attempt}";
                    offload.RemotePid = null;
                    offload.LogOffset = 0;
                    offload.AppendLogLine(now, offload.SourceOffloadRun is { } source
                        ? $"Attempt {offload.Attempt}/{offload.MaxAttempts}: copying {Storage.StorageConnections.Describe(source.S3Destination, source.RemotePrefix)} ({source.S3Destination?.Name}) → {Storage.StorageConnections.Describe(offload.S3Destination, offload.RemotePrefix)} ({offload.S3Destination?.Name})"
                        : $"Attempt {offload.Attempt}/{offload.MaxAttempts}: offloading to {Storage.StorageConnections.Describe(offload.S3Destination, offload.RemotePrefix)}");
                    if (offload.Encrypted)
                    {
                        offload.AppendLogLine(now, ModeOf(offload) == TransferMode.CopyStored
                            ? $"Source objects are age ciphertext (key {offload.EncryptionKeyFingerprint}); copying them unchanged"
                            : $"Client-side encryption: age X25519 to recipient {offload.EncryptionKeyFingerprint}; only ciphertext (.age) is uploaded");
                    }
                    if (offload.Compression is not null && offload.SourceOffloadRun is null)
                        offload.AppendLogLine(now, "Compressed with zstd on the worker host before the upload (the backup on the share is not changed)");
                    BackoraTelemetry.OperationsStarted.Add(1, new System.Diagnostics.TagList { { "kind", Kind } });
                    await db.SaveChangesAsync(ct);
                    return StepOutcome.Continue;

                case OffloadStatus.Running:
                    return await TransferAsync(sp, db, offload, ctx, now, ct);

                case OffloadStatus.Verifying:
                    return await VerifyAsync(sp, db, offload, ctx, now, ct);

                default:
                    return StepOutcome.Terminal;
            }
        }
        catch (RemoteOperationException ex)
        {
            await ctx.ResetSessionAsync();
            return await HandleFailureAsync(sp, db, offload, ex.FailureClass, ex.Message, clock.UtcNow, ct);
        }
        catch (DbUpdateConcurrencyException) { throw; }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Logger.LogError(ex, "Offload {OffloadId} failed unexpectedly", offload.Id);
            await ctx.ResetSessionAsync();
            return await HandleFailureAsync(sp, db, offload, FailureClass.Internal, ex.Message, clock.UtcNow, ct);
        }
    }

    private async Task<StepOutcome> TransferAsync(IServiceProvider sp, IAppDbContext db, OffloadRun offload, OperationContext ctx, DateTimeOffset now, CancellationToken ct)
    {
        var (env, worker) = await WorkerAsync(sp, ctx, ct);
        var dir = offload.RemoteDirectory!;
        var manifest = await ManifestAsync(sp, offload, ct);
        if (manifest.Count == 0) throw new RemoteOperationException(FailureClass.Configuration, offload.IsReplica ? "The source recovery point contains no objects to copy." : "The backup run has no artifacts to offload.");

        if (offload.RemotePid is null)
        {
            var status0 = await DetachedProcess.GetStatusAsync(worker, dir, ct);
            if (status0.Pid is { } adopted)
            {
                offload.RemotePid = adopted;
            }
            else
            {
                var paths = Paths(sp, offload);
                var mode = ModeOf(offload);
                await DetachedProcess.PrepareDirectoryAsync(worker, dir, ct);
                await worker.WriteFileAsync($"{dir}/rclone.conf", paths.Config, 600, ct);
                await worker.WriteFileAsync($"{dir}/files.txt", string.Join('\n', manifest.Select(a => a.RelativePath)) + "\n", 600, ct);
                string command;
                if (mode == TransferMode.StageFiles && offload.Compression is not null && !await HasZstdAsync(worker, ct))
                {
                    // Nothing is uploaded yet, so the copy can still go uncompressed rather than fail.
                    offload.Compression = null;
                    offload.AppendLogLine(now, "zstd is not installed on the worker host; uploading uncompressed");
                    mode = ModeOf(offload);
                }
                if (mode == TransferMode.StageFiles && !offload.Encrypted
                    && await FreeBytesAsync(worker, env.NfsPath, ct) is { } shareFree && shareFree < StagingNeed(manifest.Sum(a => a.SizeBytes)))
                {
                    // A full share must not block the uploads that free it: send the files as they are instead.
                    offload.Compression = null;
                    offload.AppendLogLine(now, $"Only {Bytes(shareFree)} free on the share, not enough to stage a compressed copy; uploading uncompressed (verified files are removed from the share afterwards)");
                    mode = ModeOf(offload);
                }
                if (mode == TransferMode.StageFiles)
                {
                    AgeScripts.EnsureListable(manifest.Select(a => a.RelativePath));
                    if (offload.Encrypted)
                    {
                        await EnsureAgeAsync(worker, offload, ct);
                        await worker.WriteFileAsync($"{dir}/{AgeScripts.RecipientsFile}", offload.EncryptionRecipient + "\n", 600, ct);
                    }
                    var stagingDir = StagingDirectory(env, offload);
                    // Staged on the share (the worker's own disk is small); worst case the data does not compress at all.
                    await EnsureStagingSpaceAsync(worker, env.NfsPath, manifest.Sum(a => a.SizeBytes), ct);
                    await worker.WriteFileAsync($"{dir}/hasher.py", ChecksumScripts.Hasher, 600, ct);
                    // One object per backup: a backup folder (PostgreSQL base.tar + pg_wal.tar + manifest) becomes one archive.
                    var items = StageItem.For(offload, manifest.Select(a => (a.RelativePath, a.SizeBytes)));
                    await worker.RunAsync($"mkdir -p -m 700 {ShellQuote.Quote(dir + "/members")}", TimeSpan.FromSeconds(30), ct);
                    for (var n = 0; n < items.Count; n++)
                    {
                        if (items[n].Packed) await worker.WriteFileAsync($"{dir}/members/{n}.txt", string.Join('\n', items[n].Members) + "\n", 600, ct);
                    }
                    await worker.WriteFileAsync($"{dir}/{TransferScripts.ItemsList}", StageItem.ItemsFile(items), 600, ct);
                    await worker.WriteFileAsync($"{dir}/{AgeScripts.EncryptedFilesList}", string.Join('\n', items.Select(it => it.Key)) + "\n", 600, ct);
                    var script = TransferScripts.StageAndUpload(paths.Source, stagingDir, offload.Compression is not null, offload.Encrypted ? Encryption.AgePath : null,
                        env.RclonePath, paths.Target, Rclone.CommonFlags("./rclone.conf"), manifest.Sum(a => a.SizeBytes));
                    await worker.WriteFileAsync($"{dir}/offload.sh", script, 700, ct);
                    command = "bash ./offload.sh";
                }
                else if (mode == TransferMode.EncryptStream)
                {
                    AgeScripts.EnsureListable(manifest.Select(a => a.RelativePath));
                    await EnsureStagingSpaceAsync(worker, dir, manifest.Sum(a => a.SizeBytes), ct);
                    await EnsureAgeAsync(worker, offload, ct);
                    // Recipients are public; the file only spares the command line.
                    await worker.WriteFileAsync($"{dir}/{AgeScripts.RecipientsFile}", offload.EncryptionRecipient + "\n", 600, ct);
                    await worker.WriteFileAsync($"{dir}/{AgeScripts.EncryptedFilesList}", string.Join('\n', manifest.Select(a => a.RelativePath + AgeKeys.ObjectSuffix)) + "\n", 600, ct);
                    var script = AgeScripts.StreamEncryptAndUpload(Encryption.AgePath, env.RclonePath, paths.Source, paths.Target, Rclone.CommonFlags("./rclone.conf"));
                    await worker.WriteFileAsync($"{dir}/offload.sh", script, 700, ct);
                    command = "bash ./offload.sh";
                }
                else
                {
                    command = $"{ShellQuote.Quote(env.RclonePath)} copy {ShellQuote.Quote(paths.Source)} {ShellQuote.Quote(paths.Target)} " +
                              $"--files-from ./files.txt --checksum {Rclone.CommonFlags("./rclone.conf")}";
                }
                offload.RemotePid = await DetachedProcess.StartAsync(worker, dir, command, ct);
                offload.LastProgressAt = now;
                offload.BytesTotal = manifest.Sum(a => a.SizeBytes);
                offload.FilesTotal = manifest.Count;
                offload.AppendLogLine(now, mode == TransferMode.StageFiles
                    ? $"{(offload.Compression is not null ? offload.Encrypted ? "Compressing and encrypting" : "Compressing" : "Encrypting")} {manifest.Count} file(s), {offload.BytesTotal:N0} bytes on the worker host (pid {offload.RemotePid}) into the staging folder on the share, then uploading"
                    : mode == TransferMode.EncryptStream
                    ? $"Encrypting {manifest.Count} file(s), {offload.BytesTotal:N0} bytes on the worker host (pid {offload.RemotePid}), then uploading the ciphertext"
                    : $"rclone started (pid {offload.RemotePid}) for {manifest.Count} file(s), {offload.BytesTotal:N0} bytes{(paths.ServerSide ? " (server-side copy within the same storage account)" : "")}");
            }
            await db.SaveChangesAsync(ct);
            return StepOutcome.Poll;
        }

        var status = await DetachedProcess.GetStatusAsync(worker, dir, ct);
        var chunk = await DetachedProcess.ReadLogAsync(worker, dir, offload.LogOffset, ct);
        if (chunk.NewOffset != offload.LogOffset)
        {
            offload.LogOffset = chunk.NewOffset;
            // Encryption phase: the staged ciphertext growing (or a file finishing) is progress too.
            if (chunk.Text.Contains("BKO_STAGE ", StringComparison.Ordinal) || chunk.Text.Contains("BKO_ENCRYPTED ", StringComparison.Ordinal)) offload.LastProgressAt = now;
            // Encrypting before the upload: the staged ciphertext against the plaintext size.
            if (AgeScripts.LatestCounter(chunk.Text, "BKO_STAGE") is { } staged && (offload.BytesTransferred ?? 0) == 0)
                offload.SetProgress("encrypt", staged, offload.BytesTotal, now);
            if (chunk.Text.Contains("BKO_STAGED ", StringComparison.Ordinal)) offload.LastProgressAt = now;
            if (chunk.Text.Contains("BKO_UPLOAD", StringComparison.Ordinal) && offload.ProgressStage is not ("upload" or "prepare")) offload.SetProgress("upload", 0, null, now);
            var stats = Rclone.ParseLatestStats(chunk.Text);
            if (stats is not null)
            {
                if (stats.Bytes > (offload.BytesTransferred ?? 0)) offload.LastProgressAt = now;
                offload.BytesTransferred = stats.Bytes;
                if (stats.TotalBytes > 0) offload.BytesTotal = stats.TotalBytes;
                offload.FilesTransferred = stats.Transfers;
                // rclone reports 0 bytes while it checksums a large file first: that is the "prepare" step, not the upload.
                if (stats.Bytes > 0 || offload.ProgressStage is not "prepare") offload.SetProgress("upload", stats.Bytes, offload.BytesTotal, now);
            }
            offload.AppendLog(Summarize(chunk.Text));
        }

        // Compressing/encrypting into the staging folder: the uncompressed bytes read so far, against the backup's size.
        if (status.Progress is { FromTemplate: true } staging && offload.ProgressStage is not ("upload" or "prepare"))
        {
            if (staging.Bytes > (offload.ProgressBytes ?? -1)) offload.LastProgressAt = now;
            offload.SetProgress(staging.Stage ?? "compress", staging.Bytes, staging.TotalBytes, now);
        }

        // Before the first byte of a large file is uploaded, rclone reads the whole file to compute its MD5 (stored with
        // the object; the copy is verified against it). That phase transfers nothing but is not a stall: the transfer's
        // reads count as progress, and the phase is shown as its own step.
        if (status.State == RemoteProcessState.Running && (offload.BytesTransferred ?? 0) == 0 && offload.ProgressStage is null or "upload" or "prepare")
        {
            var io = await DetachedProcess.SampleIoAsync(worker, dir, ct);
            if (io is { PreviousBytes: { } before, PreviousEpoch: { } since }
                && io.Bytes - before >= RunTimeout.MinBytesPerMinute * Math.Max(1.0, (io.Epoch - since) / 60.0))
            {
                offload.LastProgressAt = now;
            }
            if (io is not null)
            {
                var total = offload.ProgressStage == "prepare" ? offload.ProgressTotalBytes : await UploadBytesAsync(worker, offload, ct);
                offload.SetProgress("prepare", total is > 0 ? Math.Min(io.Bytes, total.Value) : io.Bytes, total, now);
            }
        }

        switch (status.State)
        {
            case RemoteProcessState.Running when now - (offload.LastProgressAt ?? offload.StartedAt ?? now) > TimeSpan.FromSeconds(Options.TransferStallSeconds):
                offload.AppendLogLine(now, $"No transfer progress for {Options.TransferStallSeconds}s; terminating rclone");
                await DetachedProcess.TerminateAsync(worker, dir, ct);
                return await HandleFailureAsync(sp, db, offload, FailureClass.StorageError, $"Transfer stalled: no progress for {Options.TransferStallSeconds} seconds (object storage unreachable or unresponsive).", now, ct);
            case RemoteProcessState.Running:
                await db.SaveChangesAsync(ct);
                return StepOutcome.Poll;
            case RemoteProcessState.Exited when status.ExitCode == 0:
                offload.AppendLogLine(now, "Transfer finished; verifying");
                offload.TransitionTo(OffloadStatus.Verifying, now);
                await db.SaveChangesAsync(ct);
                return StepOutcome.Continue;
            case RemoteProcessState.Exited when status.ExitCode == TransferScripts.ExitCompress:
                var zLog = await DetachedProcess.ReadLogAsync(worker, dir, 0, ct);
                return await HandleFailureAsync(sp, db, offload, FailureClass.StorageError,
                    $"Compression with zstd failed on the worker host; nothing was uploaded. {AgeScripts.ExtractErrors(zLog.Text)}".Trim(), now, ct);
            case RemoteProcessState.Exited when status.ExitCode == AgeScripts.ExitEncrypt && offload.Encrypted:
                var encLog = await DetachedProcess.ReadLogAsync(worker, dir, 0, ct);
                return await HandleFailureAsync(sp, db, offload, FailureClass.Configuration,
                    $"Encryption with age failed on the worker host; nothing unencrypted was uploaded. {AgeScripts.ExtractErrors(encLog.Text)}".Trim(), now, ct);
            case RemoteProcessState.Exited:
                var full = await DetachedProcess.ReadLogAsync(worker, dir, 0, ct);
                var errors = offload.Encrypted ? $"{AgeScripts.ExtractErrors(full.Text)} {Rclone.ExtractErrors(full.Text)}".Trim() : Rclone.ExtractErrors(full.Text);
                return await HandleFailureAsync(sp, db, offload, FailureClass.StorageError,
                    $"{(offload.Encrypted ? "Encrypted upload" : "rclone")} exited with code {status.ExitCode}: {errors}".Trim(), now, ct);
            default:
                // The wrapper writes the exit code last; a full work disk is the usual reason it never could.
                var free = await FreeBytesAsync(worker, dir, ct);
                if (free is { } f && f < LowSpaceBytes)
                {
                    return await HandleFailureAsync(sp, db, offload, FailureClass.Configuration,
                        $"The transfer stopped without an exit code and the execution host's work folder {dir} has only {Bytes(f)} free: it ran out of space. " +
                        "Point the work folder at a larger disk (Execution host → Connection settings) or free space, then retry.", now, ct);
                }
                return await HandleFailureAsync(sp, db, offload, FailureClass.ProcessLost, "The rclone process ended without an exit code.", now, ct);
        }
    }

    private async Task<StepOutcome> VerifyAsync(IServiceProvider sp, IAppDbContext db, OffloadRun offload, OperationContext ctx, DateTimeOffset now, CancellationToken ct)
    {
        var (env, worker) = await WorkerAsync(sp, ctx, ct);
        var dir = offload.RemoteDirectory!;
        var conn = await ConnectionAsync(sp, offload);
        var manifest = await ManifestAsync(sp, offload, ct);
        var sourceFiles = manifest.Select(m => m.RelativePath).ToList(); // NFS files to clean up (plaintext names)
        var paths = Paths(sp, offload);
        var mode = ModeOf(offload);
        var staged = mode is TransferMode.StageFiles or TransferMode.EncryptStream;
        List<EncryptedObject>? encryptedObjects = null;
        var (venv, _) = await WorkerAsync(sp, ctx, ct);
        var checkSource = mode == TransferMode.StageFiles ? StagingDirectory(venv, offload) : staged ? $"{dir}/{AgeScripts.StageDirectory}" : paths.Source;
        var checkList = staged ? $"./{AgeScripts.EncryptedFilesList}" : "./files.txt";
        var objectSuffix = mode == TransferMode.StageFiles ? offload.ObjectSuffix : AgeKeys.ObjectSuffix;

        // 1) Detached on the worker with live progress: the staged ciphertext (age header + SHA-256 per file), then
        //    rclone check, which compares sizes and hashes (MD5/ETag or rclone's stored checksum metadata).
        var checkDir = $"{dir}/check";
        var cs = await DetachedProcess.GetStatusAsync(worker, checkDir, ct);
        switch (cs.State)
        {
            case RemoteProcessState.NotStarted:
                // Local files are hashed by rclone; between two buckets the hashes come from the listings.
                var checkBytes = mode != TransferMode.StageFiles && checkSource.StartsWith('/') ? manifest.Sum(m => m.SizeBytes) : 0;
                await DetachedProcess.PrepareDirectoryAsync(worker, checkDir, ct);
                await worker.WriteFileAsync($"{checkDir}/hasher.py", ChecksumScripts.Hasher, 600, ct);
                // A plain upload whose files all have the MD5 from verification is checked against storage's own hashes.
                if (!staged && checkSource.StartsWith('/') && manifest.Count > 0 && manifest.All(m => m.Md5 is { Length: 32 }))
                    await worker.WriteFileAsync($"{checkDir}/expected.md5", string.Concat(manifest.Select(m => $"{m.Md5}  {m.RelativePath}\n")), 600, ct);
                // The staging script already recorded the staged objects' checksums; a streamed replica's stage is inspected here.
                await worker.WriteFileAsync($"{checkDir}/check.sh", ChecksumScripts.CopyCheck(env.RclonePath, checkSource, paths.Target, checkList,
                    staged ? checkSource : null, staged ? AgeScripts.FilesList : null, offload.Encrypted ? AgeKeys.HeaderLine : null, objectSuffix, checkBytes), 700, ct);
                await DetachedProcess.StartAsync(worker, checkDir, "bash ./check.sh", ct);
                offload.SetProgress(mode == TransferMode.EncryptStream ? "inspect" : "check", 0, staged ? null : checkBytes, now);
                offload.AppendLogLine(now, mode == TransferMode.EncryptStream ? "Checking the staged ciphertext, then the uploaded objects" : "Checking the uploaded objects against their checksums");
                await db.SaveChangesAsync(ct);
                return StepOutcome.Poll;
            case RemoteProcessState.Running:
                if (cs.Progress is { } p) offload.SetProgress(p.Stage ?? "check", p.Bytes, p.TotalBytes, now);
                await db.SaveChangesAsync(ct);
                return StepOutcome.Poll;
            case RemoteProcessState.Lost:
                offload.AppendLogLine(now, "The check process ended without a result; starting it again");
                await DetachedProcess.RemoveDirectoryAsync(worker, checkDir, ct);
                await db.SaveChangesAsync(ct);
                return StepOutcome.Poll;
        }

        if (staged)
        {
            // 0) Every staged file must be an age v1 file; record ciphertext size and SHA-256.
            var stageTsv = (await worker.RunAsync($"cat {ShellQuote.Quote(checkDir + "/stage.tsv")} 2>/dev/null", TimeSpan.FromMinutes(5), ct)).StdOut;
            if (!stageTsv.Contains("END", StringComparison.Ordinal))
                throw new RemoteOperationException(FailureClass.VerificationFailed, "Could not inspect the staged ciphertext.");
            // The stored names: for a primary upload one per backup file or packed backup folder; for a streamed replica
            // the source's objects, recorded under the source's stored names (so a packed folder stays a packed folder).
            var expectedNames = mode == TransferMode.StageFiles
                ? StageItem.For(offload, manifest.Select(a => (a.RelativePath, a.SizeBytes))).Select(it => it.Name).ToList()
                : manifest.Select(m => m.RelativePath).ToList();
            encryptedObjects = ParseStage(stageTsv, expectedNames);
            if (mode == TransferMode.EncryptStream && offload.SourceOffloadRun is { Transformed: true } src0)
            {
                var sourceNames = src0.EncryptedObjects.ToDictionary(e => src0.ObjectKeyFor(e.RelativePath), e => e.RelativePath, StringComparer.Ordinal);
                encryptedObjects = encryptedObjects.Select(e => sourceNames.TryGetValue(e.RelativePath, out var name) ? e with { RelativePath = name } : e).ToList();
            }
            var bad = encryptedObjects.Where(e => e.CiphertextSha256.Length != 64).Select(e => e.RelativePath).ToList();
            if (bad.Count > 0 || encryptedObjects.Count != expectedNames.Count)
            {
                offload.VerificationStatus = VerificationStatus.Failed;
                return await HandleFailureAsync(sp, db, offload, FailureClass.VerificationFailed,
                    $"Staged objects are missing or invalid: {string.Join(", ", bad.Concat(expectedNames.Except(encryptedObjects.Select(e => e.RelativePath))).Take(10))}", now, ct);
            }
            // Verify the uploaded objects against the staged ciphertext (object names = rel + ".age").
            manifest = encryptedObjects.Select(e => new ManifestEntry(offload.ObjectKeyFor(e.RelativePath), e.CiphertextSize)).ToList();
        }

        var tail = await worker.RunAsync($"tail -n 20 {ShellQuote.Quote(dir + "/check.log")} 2>/dev/null", TimeSpan.FromSeconds(30), ct);
        var checkOutput = $"RC={cs.ExitCode}\n{tail.StdOut}".Trim();
        var rcloneOk = cs.ExitCode == 0;

        // 2) Independent check through the S3 API: every manifest file present with the exact size.
        var storage = sp.GetRequiredService<IObjectStorage>();
        var objects = await storage.ListAsync(conn, offload.RemotePrefix, ct);
        var byKey = objects.ToDictionary(o => o.Key[offload.RemotePrefix.Length..], o => o.Size, StringComparer.Ordinal);
        var missing = manifest.Where(a => !byKey.TryGetValue(a.RelativePath, out var size) || size != a.SizeBytes).Select(a => a.RelativePath).ToList();

        if (!rcloneOk || missing.Count > 0)
        {
            var reason = !rcloneOk ? $"rclone check reported differences: {Tail(checkOutput, 600)}" : $"Objects missing or size mismatch in S3: {string.Join(", ", missing.Take(10))}";
            offload.VerificationStatus = VerificationStatus.Failed;
            return await HandleFailureAsync(sp, db, offload, FailureClass.VerificationFailed, reason, now, ct);
        }

        offload.VerificationStatus = VerificationStatus.Verified;
        offload.VerifiedAt = now;
        if (encryptedObjects is not null)
        {
            offload.EncryptedObjectsJson = System.Text.Json.JsonSerializer.Serialize(encryptedObjects);
            offload.BytesTotal = encryptedObjects.Sum(e => e.CiphertextSize);
        }
        else if (mode == TransferMode.CopyStored)
        {
            offload.BytesTotal = manifest.Sum(m => m.SizeBytes);
        }
        offload.BytesTransferred = offload.BytesTotal;
        offload.FilesTransferred = manifest.Count;
        var method = tail.StdOut.Contains("against the MD5 recorded", StringComparison.Ordinal)
            ? "by checksum (each object's MD5 in storage against the one recorded when the backup was verified)"
            : "by checksum (rclone check)";
        offload.AppendLogLine(now, $"Verified {manifest.Count} object(s) {method} and by size (S3 API listing){(offload.IsReplica ? $" on {offload.S3Destination?.Name}" : "")}" +
            (mode == TransferMode.StageFiles && offload.Compression is not null && encryptedObjects is not null
                ? $"; zstd-compressed on the worker from {Bytes(offload.BackupRun?.Artifacts.Sum(a => a.SizeBytes) ?? 0)} to {Bytes(encryptedObjects.Sum(e => e.CiphertextSize))}" : "") +
            (staged && offload.Encrypted ? "; every object is age v1 ciphertext (header checked before upload)" : mode == TransferMode.CopyStored ? "; the source's stored objects, copied unchanged" : ""));
        BackoraTelemetry.BytesTransferred.Add(offload.BytesTotal ?? 0);

        var job = offload.BackupJobId is { } jid
            ? await db.BackupJobs.AsNoTracking().Include(j => j.ReplicationTargets).ThenInclude(t => t.S3Destination).FirstOrDefaultAsync(j => j.Id == jid, ct)
            : null;
        if (job is not null && job.RetentionDays > 0)
        {
            // Copies expire with the recovery point they were made from.
            var basis = offload.SourceOffloadRun?.CompletedAt ?? now;
            offload.ExpiresAt = offload.SourceOffloadRun?.ExpiresAt ?? basis + TimeSpan.FromSeconds((double)(job.RetentionDays * 86400m));
        }

        // Local cleanup: only the verified manifest files, never a wildcard (primary uploads only).
        if (offload.CleanupStatus == CleanupStatus.Pending && !offload.IsReplica)
        {
            var delete = await RemoteFiles.DeleteFilesAsync(worker, offload.SourcePath, sourceFiles, ct);
            if (delete.Succeeded)
            {
                foreach (var a in offload.BackupRun?.Artifacts ?? []) a.LocalCopyDeleted = true;
                offload.CleanupStatus = CleanupStatus.Completed;
                offload.CleanupMessage = $"Removed {sourceFiles.Count} verified file(s) from NFS staging.";
            }
            else
            {
                offload.CleanupStatus = CleanupStatus.Failed;
                offload.CleanupMessage = $"Offload verified but NFS cleanup failed: {Tail(delete.StdErr, 500)}";
            }
            offload.AppendLogLine(now, offload.CleanupMessage);
        }

        if (staged)
        {
            // The staged objects are no longer needed once the upload is verified.
            await worker.RunAsync($"rm -rf -- {ShellQuote.Quote(checkSource)}", TimeSpan.FromMinutes(30), ct);
            offload.AppendLogLine(now, mode == TransferMode.StageFiles ? "Removed the staged copy from the share" : "Removed the staged ciphertext from the worker host");
        }

        offload.TransitionTo(OffloadStatus.Completed, now);

        // DR replication: a verified primary upload fans out to every secondary destination of the job.
        if (!offload.IsReplica && job is not null)
        {
            foreach (var target in job.ReplicationTargets.Where(t => t.S3DestinationId != offload.S3DestinationId))
            {
                var name = target.S3Destination?.Name ?? $"#{target.S3DestinationId}";
                if (target.S3Destination is { IsEnabled: false })
                {
                    // Disabled sites get no new copies; every other site is still copied independently.
                    offload.AppendLogLine(now, $"DR copy to {name} skipped: the destination is disabled");
                    continue;
                }
                db.OffloadRuns.Add(ReplicaFactory.Create(offload, target.S3DestinationId, now));
                offload.AppendLogLine(now, $"Queued DR copy to {name}");
            }
        }

        await db.SaveChangesAsync(ct);
        BackoraTelemetry.RecordTerminal(Kind, "completed", FailureClass.None.ToString(), offload.Duration);
        await RemoveWorkDirAsync(worker, dir, ct);
        return StepOutcome.Terminal;
    }

    private async Task<StepOutcome> HandleFailureAsync(IServiceProvider sp, IAppDbContext db, OffloadRun offload, FailureClass failureClass, string message, DateTimeOffset now, CancellationToken ct)
    {
        Logger.LogWarning("Offload {OffloadId} error ({FailureClass}): {Message}", offload.Id, failureClass, message);
        var retryable = failureClass is not FailureClass.Configuration and not FailureClass.HostKeyMismatch and not FailureClass.Authentication;
        if (offload.Status is OffloadStatus.Running or OffloadStatus.Verifying && offload.RemotePid is not null && IsTransient(failureClass))
        {
            // Lost the worker connection while rclone keeps running detached: keep polling.
            offload.AppendLogLine(now, $"Transient error, will retry: {message}");
            await db.SaveChangesAsync(ct);
            return StepOutcome.Poll;
        }

        if (retryable && offload.TryScheduleRetry(now))
        {
            offload.AppendLogLine(now, $"Attempt {offload.Attempt} failed: {message}. Retrying at {offload.NextAttemptAt:u}");
            if (offload.RemoteDirectory is { } dir && offload.RemotePid is not null)
            {
                try
                {
                    var (cenv, worker) = await WorkerAsync(sp, new OperationContext("cleanup"), ct);
                    await DetachedProcess.TerminateAsync(worker, dir, ct);
                    await RemoveWorkDirAsync(worker, dir, ct);
                    if (ModeOf(offload) == TransferMode.StageFiles) await RemoveWorkDirAsync(worker, StagingDirectory(cenv, offload), ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException) { Logger.LogDebug(ex, "cleanup after failed attempt"); }
            }
            offload.RemotePid = null;
            offload.TransitionTo(OffloadStatus.Pending, now);
            await db.SaveChangesAsync(ct);
            return StepOutcome.Yield;
        }

        offload.Fail(failureClass, message, now);
        if (offload.CleanupStatus == CleanupStatus.Pending) offload.CleanupStatus = CleanupStatus.Skipped;
        if (offload.Transformed && offload.RemoteDirectory is { } failedDir && offload.RemotePid is not null)
        {
            // Do not leave staged objects (or the rclone config) behind after the final attempt.
            try
            {
                var (fenv, worker) = await WorkerAsync(sp, new OperationContext("cleanup"), ct);
                await DetachedProcess.TerminateAsync(worker, failedDir, ct);
                await RemoveWorkDirAsync(worker, failedDir, ct);
                if (ModeOf(offload) == TransferMode.StageFiles) await RemoveWorkDirAsync(worker, StagingDirectory(fenv, offload), ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { Logger.LogDebug(ex, "cleanup after failed encrypted offload"); }
        }
        await sp.GetRequiredService<NotificationPublisher>().PublishOffloadFailedAsync(offload, ct);
        await db.SaveChangesAsync(ct);
        BackoraTelemetry.RecordTerminal(Kind, "failed", failureClass.ToString(), offload.Duration);
        return StepOutcome.Terminal;
    }

    private sealed record ManifestEntry(string RelativePath, long SizeBytes, string? Md5 = null);

    /// <summary>
    /// Files to transfer. Primary uploads use the run's SHA-256 manifest. Replicas use the same manifest when
    /// available, otherwise (legacy recovery points) the source prefix as listed through the S3 API.
    /// </summary>
    private static async Task<List<ManifestEntry>> ManifestAsync(IServiceProvider sp, OffloadRun offload, CancellationToken ct)
    {
        // A compressed and/or encrypted source: its stored objects, by their stored names and sizes.
        if (offload.SourceOffloadRun is { Transformed: true } stored && stored.EncryptedObjects is { Count: > 0 } objects0)
        {
            return objects0.Select(e => new ManifestEntry(stored.ObjectKeyFor(e.RelativePath), e.CiphertextSize, e.CiphertextMd5))
                .OrderBy(m => m.RelativePath, StringComparer.Ordinal).ToList();
        }
        var artifacts = offload.SourceOffloadRun is { Transformed: true } ? []
            : offload.BackupRun?.Artifacts.OrderBy(a => a.RelativePath, StringComparer.Ordinal).Select(a => new ManifestEntry(a.RelativePath, a.SizeBytes, a.Md5)).ToList() ?? [];
        if (artifacts.Count > 0 || offload.SourceOffloadRun is not { } source) return artifacts;
        var conn = SourceConnection(sp, offload);
        var objects = await sp.GetRequiredService<IObjectStorage>().ListAsync(conn, source.RemotePrefix, ct);
        return objects.Where(o => o.Key.Length > source.RemotePrefix.Length)
            .Select(o => new ManifestEntry(o.Key[source.RemotePrefix.Length..], o.Size))
            .OrderBy(m => m.RelativePath, StringComparer.Ordinal).ToList();
    }

    private sealed record TransferPaths(string Config, string Source, string Target, bool ServerSide);

    private static TransferPaths Paths(IServiceProvider sp, OffloadRun offload)
    {
        var target = Connection(sp, offload);
        if (offload.SourceOffloadRun is not { } source)
        {
            return new TransferPaths(Rclone.BuildConfig(target), offload.SourcePath, Rclone.RemotePath(target, offload.RemotePrefix), false);
        }
        var src = SourceConnection(sp, offload);
        if (Rclone.SameAccount(src, target))
        {
            // One remote for both sides lets rclone use S3 server-side copy (no data through the worker).
            return new TransferPaths(Rclone.BuildConfig(target), Rclone.RemotePath(src, source.RemotePrefix), Rclone.RemotePath(target, offload.RemotePrefix), true);
        }
        var config = Rclone.BuildConfig(src, Rclone.ReplicaSourceRemote) + "\n" + Rclone.BuildConfig(target, Rclone.ReplicaTargetRemote);
        return new TransferPaths(config, Rclone.RemotePath(src, source.RemotePrefix, Rclone.ReplicaSourceRemote),
            Rclone.RemotePath(target, offload.RemotePrefix, Rclone.ReplicaTargetRemote), false);
    }

    private static S3Connection SourceConnection(IServiceProvider sp, OffloadRun offload)
    {
        var dest = offload.SourceOffloadRun?.S3Destination ?? throw new RemoteOperationException(FailureClass.Configuration, "The source recovery point's S3 destination no longer exists.");
        return Storage.StorageConnections.For(dest, sp.GetRequiredService<ISecretProtector>());
    }

    private static S3Connection Connection(IServiceProvider sp, OffloadRun offload)
    {
        var dest = offload.S3Destination ?? throw new RemoteOperationException(FailureClass.Configuration, "S3 destination no longer exists.");
        return Storage.StorageConnections.For(dest, sp.GetRequiredService<ISecretProtector>());
    }

    private static Task<S3Connection> ConnectionAsync(IServiceProvider sp, OffloadRun offload) => Task.FromResult(Connection(sp, offload));

    private static async Task<(ExecutionEnvironment Env, IRemoteSession Worker)> WorkerAsync(IServiceProvider sp, OperationContext ctx, CancellationToken ct)
    {
        var env = await sp.GetRequiredService<ExecutionEnvironmentFactory>().CreateAsync(ct);
        var connector = sp.GetRequiredService<ISshConnector>();
        var session = await ctx.GetSessionAsync("worker", () => connector.ConnectWorkerAsync(env.Worker, ct));
        return (env, session);
    }

    private async Task RemoveWorkDirAsync(IRemoteSession worker, string dir, CancellationToken ct)
    {
        try { await DetachedProcess.RemoveDirectoryAsync(worker, dir, ct); }
        catch (Exception ex) when (ex is not OperationCanceledException) { Logger.LogWarning(ex, "Failed to remove offload work dir {Dir}", dir); }
    }

    private const long LowSpaceBytes = 512L * 1024 * 1024;

    /// <summary>Space a staged copy may take in the worst case (data that does not compress; age adds well under 1%).</summary>
    private static long StagingNeed(long plaintextBytes) => plaintextBytes + plaintextBytes / 100 + LowSpaceBytes;

    /// <summary>Staging folder on the NFS share (beside the job folders, never inside one) for this upload attempt.</summary>
    internal static string StagingDirectory(ExecutionEnvironment env, OffloadRun offload) =>
        $"{env.NfsPath.TrimEnd('/')}/{TransferScripts.StagingRoot}/offload-{offload.Id}-a{offload.Attempt}";

    /// <summary>Bytes rclone uploads: the staged copy when the worker compressed/encrypted it, else the files themselves.</summary>
    private static async Task<long?> UploadBytesAsync(IRemoteSession worker, OffloadRun offload, CancellationToken ct)
    {
        if (ModeOf(offload) != TransferMode.StageFiles) return offload.BytesTotal;
        var r = await worker.RunAsync($"cat {ShellQuote.Quote(offload.RemoteDirectory + "/" + TransferScripts.StageManifest)} 2>/dev/null", TimeSpan.FromSeconds(30), ct);
        long sum = 0;
        foreach (var line in r.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Split('\t');
            if (parts.Length >= 2 && long.TryParse(parts[1].Split(' ')[0], out var size)) sum += size;
        }
        return sum > 0 ? sum : offload.BytesTotal;
    }

    private static async Task<bool> HasZstdAsync(IRemoteSession worker, CancellationToken ct) =>
        (await worker.RunAsync("command -v zstd >/dev/null 2>&1 && command -v python3 >/dev/null 2>&1 && echo YES", TimeSpan.FromSeconds(30), ct)).StdOut.Contains("YES", StringComparison.Ordinal);

    /// <summary>
    /// Encrypted copies stage their ciphertext in the work folder before uploading. Refuse to start when it can't fit:
    /// filling the disk kills the transfer before it can even record an exit code.
    /// </summary>
    private static async Task EnsureStagingSpaceAsync(IRemoteSession worker, string dir, long plaintextBytes, CancellationToken ct)
    {
        var free = await FreeBytesAsync(worker, dir, ct);
        if (free is null) return;   // df unavailable: don't block the copy on an unknown
        var needed = StagingNeed(plaintextBytes);
        if (free < needed)
        {
            throw new RemoteOperationException(FailureClass.Configuration,
                $"Not enough free space for the staged copy in {dir}: {Bytes(free.Value)} free, {Bytes(needed)} needed in the worst case. " +
                "Free space there (or point the folder at a larger disk under Execution host → Connection settings), then retry.");
        }
    }

    /// <summary>Free bytes on the filesystem holding <paramref name="dir"/> (POSIX df), or null when it can't be read.</summary>
    internal static async Task<long?> FreeBytesAsync(IRemoteSession worker, string dir, CancellationToken ct)
    {
        var r = await worker.RunAsync($"df -Pk {ShellQuote.Quote(dir)} 2>/dev/null | awk 'NR==2 {{print $4}}'", TimeSpan.FromSeconds(30), ct);
        return long.TryParse(r.StdOut.Trim(), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var kb) ? kb * 1024 : null;
    }

    private static string Bytes(long b) => b >= 1L << 30 ? $"{b / (double)(1L << 30):0.#} GB" : $"{b / (double)(1L << 20):0} MB";

    private async Task EnsureAgeAsync(IRemoteSession worker, OffloadRun offload, CancellationToken ct)
    {
        var age = ShellQuote.Quote(Encryption.AgePath);
        var probe = await worker.RunAsync($"command -v {age} >/dev/null 2>&1 && {age} --version 2>&1 | head -n 1 || echo NOAGE", TimeSpan.FromSeconds(30), ct);
        if (probe.StdOut.Contains("NOAGE", StringComparison.Ordinal) || !probe.Succeeded)
        {
            throw new RemoteOperationException(FailureClass.Configuration,
                $"Destination {offload.S3Destination?.Name} requires customer-key encryption, but the age tool ('{Encryption.AgePath}') is not installed on the worker host " +
                "(Alpine/Debian package 'age', version 1.1 or later). Nothing was uploaded; Backora never falls back to plaintext.");
        }
    }

    /// <summary>Size, SHA-256 and header check of every staged ciphertext file (paths base64-encoded for safety).</summary>
    /// <summary>Parses the staged-ciphertext inspection ("sha256|BADHEADER TAB size TAB base64(rel)" lines, then END).</summary>
    private static List<EncryptedObject> ParseStage(string output, IReadOnlyList<string> expectedNames)
    {
        var known = expectedNames.ToHashSet(StringComparer.Ordinal);
        var list = new List<EncryptedObject>();
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Split('\t');
            if (parts.Length is < 3 or > 4 || !long.TryParse(parts[1].Split(' ')[0], out var size)) continue;
            string rel;
            try { rel = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(parts[2])); }
            catch (FormatException) { continue; }
            if (!known.Contains(rel)) continue;
            var md5 = parts.Length == 4 && parts[3].Length == 32 ? parts[3].ToLowerInvariant() : null;
            list.Add(new EncryptedObject(rel, size, parts[0] == "BADHEADER" ? string.Empty : parts[0].ToLowerInvariant(), md5));
        }
        return list.OrderBy(e => e.RelativePath, StringComparer.Ordinal).ToList();
    }

    /// <summary>Keeps the persisted log readable: rclone JSON lines are reduced to their message.</summary>
    public static string Summarize(string chunk)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var line in chunk.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.StartsWith("BKO_", StringComparison.Ordinal))
            {
                // Markers from Backora's encryption scripts: counters are noise, the rest is made readable.
                var space = line.IndexOf(' ', StringComparison.Ordinal);
                var tag = space < 0 ? line : line[..space];
                var rest = space < 0 ? string.Empty : line[(space + 1)..].Trim();
                var text = tag switch
                {
                    "BKO_STAGE" or "BKO_PROGRESS" => null,
                    "BKO_ENCRYPTED" => $"Encrypted {rest} → {rest}{AgeKeys.ObjectSuffix} (age)",
                    "BKO_UPLOAD" => "Uploading staged ciphertext",
                    "BKO_KEY_RECEIVED" => "Decryption key received by the restore script",
                    "BKO_FILE_DONE" => $"Decrypted and restored {rest}",
                    "BKO_ERROR" => "ERROR " + rest,
                    _ => line,
                };
                if (text is not null) sb.Append(text).Append('\n');
                continue;
            }
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(line);
                var root = doc.RootElement;
                var level = root.TryGetProperty("level", out var l) ? l.GetString() : "info";
                var msg = root.TryGetProperty("msg", out var m) ? m.GetString() : line;
                var time = root.TryGetProperty("time", out var t) ? t.GetString() : null;
                sb.Append('[').Append(time is { Length: >= 19 } ? time[..19].Replace('T', ' ') : "").Append("] ")
                  .Append(level?.ToUpperInvariant()).Append(' ').Append(msg?.Replace("\n", " ", StringComparison.Ordinal).Trim()).Append('\n');
            }
            catch (System.Text.Json.JsonException)
            {
                sb.Append(line).Append('\n');
            }
        }
        return sb.ToString();
    }

    private static string Tail(string s, int max) => s.Length <= max ? s : "…" + s[^max..];
}
