using Backora.Application.Abstractions;
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
/// Drives a backup run: Pending → Starting → Running → Verifying → Completed | Failed, with Cancelling.
/// Success criteria: the script exits 0 AND (when RequireArtifacts) at least one non-empty file was
/// produced in the job's NFS directory during the run; every artifact is SHA-256 fingerprinted.
/// </summary>
public sealed class BackupRunProcessor(IServiceScopeFactory scopes, IOptions<WorkerOptions> options, ILogger<BackupRunProcessor> logger)
    : OperationProcessorBase(scopes, options, logger)
{
    public override string Kind => OperationKinds.Backup;

    protected override async Task<StepOutcome> StepAsync(IServiceProvider sp, long id, OperationContext ctx, CancellationToken ct)
    {
        var db = sp.GetRequiredService<IAppDbContext>();
        var clock = sp.GetRequiredService<IClock>();
        var run = await db.BackupRuns
            .Include(r => r.Server)
            .Include(r => r.BackupJob!).ThenInclude(j => j.ScriptTemplate)
            .AsSplitQuery()
            .FirstOrDefaultAsync(r => r.Id == id, ct);
        if (run is null || run.IsTerminal) return StepOutcome.Terminal;

        var now = clock.UtcNow;
        run.ProcessedBy = ctx.Owner;
        run.HeartbeatAt = now;

        try
        {
            switch (run.Status)
            {
                case RunStatus.Pending:
                    run.TransitionTo(RunStatus.Starting, now);
                    run.TimeoutAt = now.AddMinutes(run.BackupJob?.TimeoutMinutes ?? 360);
                    run.AppendLogLine(now, $"Attempt {run.Attempt}/{run.MaxAttempts} claimed by {ctx.Owner}");
                    BackoraTelemetry.OperationsStarted.Add(1, new System.Diagnostics.TagList { { "kind", Kind } });
                    await db.SaveChangesAsync(ct);
                    return StepOutcome.Continue;

                case RunStatus.Starting:
                    return await StartAsync(sp, db, run, ctx, now, ct);

                case RunStatus.Running:
                    return await MonitorAsync(sp, db, run, ctx, now, ct);

                case RunStatus.Verifying:
                    return await VerifyAsync(sp, db, run, ctx, now, ct);

                case RunStatus.Cancelling:
                    return await CancelAsync(sp, db, run, ctx, now, ct);

                default:
                    return StepOutcome.Terminal;
            }
        }
        catch (RemoteOperationException ex)
        {
            await ctx.ResetSessionAsync();
            return await HandleRemoteFailureAsync(sp, db, run, ex, clock.UtcNow, ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Logger.LogError(ex, "Backup run {RunId} failed with an unexpected error", run.Id);
            await ctx.ResetSessionAsync();
            await FailAsync(sp, db, run, FailureClass.Internal, $"Internal error: {ex.Message}", clock.UtcNow, ct);
            return StepOutcome.Terminal;
        }
    }

    private async Task<StepOutcome> StartAsync(IServiceProvider sp, IAppDbContext db, BackupRun run, OperationContext ctx, DateTimeOffset now, CancellationToken ct)
    {
        if (run.CancelRequestedAt is not null && run.RemotePid is null)
        {
            run.FailureClass = FailureClass.Cancelled;
            run.TransitionTo(RunStatus.Cancelling, now);
            await db.SaveChangesAsync(ct);
            return StepOutcome.Continue;
        }

        var job = run.BackupJob ?? throw new RemoteOperationException(FailureClass.Configuration, "Backup job no longer exists.");
        var server = run.Server ?? throw new RemoteOperationException(FailureClass.Configuration, "Server no longer exists.");
        var template = job.ScriptTemplate ?? throw new RemoteOperationException(FailureClass.Configuration, "Script template no longer exists.");
        if (server.HostKeyFingerprint is null)
        {
            throw new RemoteOperationException(FailureClass.HostKeyMismatch, $"Host key for server '{server.Name}' has not been trusted. Test the connection and trust the fingerprint first.");
        }

        var envFactory = sp.GetRequiredService<ExecutionEnvironmentFactory>();
        var env = await envFactory.CreateAsync(ct);
        var secrets = sp.GetRequiredService<ISecretProtector>();
        var variables = await db.Variables.AsNoTracking().ToListAsync(ct);
        var varMap = variables.ToDictionary(v => v.Name, v => v.IsSecret ? (v.ValueCiphertext is null ? string.Empty : secrets.Unprotect(v.ValueCiphertext)) : v.Value ?? string.Empty, StringComparer.Ordinal);

        var rendered = ScriptRenderer.Render(template.Script, new ScriptRenderContext(
            job.Name, job.Category, template.Name, job.RetentionDays, env.NfsHost, env.NfsPath, varMap));
        if (rendered.UnresolvedPlaceholders.Count > 0)
        {
            throw new RemoteOperationException(FailureClass.Configuration,
                $"Template '{template.Name}' references undefined variables: {string.Join(", ", rendered.UnresolvedPlaceholders.Select(p => "{" + p + "}"))}.");
        }

        // Connection settings come from Backora variables (NAME__JOB, then NAME; secrets decrypted only here) and are
        // exported as quoted shell variables; they are never stored with the job or the run.
        var specs = TemplateSettings.Parse(template.Script);
        var settingValues = TemplateSettings.Resolve(specs, job.Name, k => varMap.GetValueOrDefault(k), out var fromAllJobs);
        var missing = specs.Where(s => s.Required && !settingValues.ContainsKey(s.Name) && string.IsNullOrEmpty(s.Default)).Select(s => s.Name).ToList();
        if (missing.Count > 0)
        {
            throw new RemoteOperationException(FailureClass.Configuration,
                $"Job '{job.Name}' is missing required settings: add the variable {string.Join(", ", missing.Select(m => $"{TemplateSettings.VariableName(m, job.Name)} (or {m} for all jobs)"))} under Templates → Variables.");
        }
        rendered = TemplateSettings.Apply(rendered, specs, settingValues, fromAllJobs);

        run.TemplateName = template.Name;
        run.TemplateRevision = template.Revision;
        run.ScriptSha256 = rendered.Sha256;

        // Record the worker's clock: artifact discovery compares NFS mtimes against it.
        await using (var workerSession = await sp.GetRequiredService<ISshConnector>().ConnectWorkerAsync(env.Worker, ct))
        {
            run.WorkerStartEpoch ??= await RemoteFiles.EpochAsync(workerSession, ct);
            // The database server mounts <share>/<job>, and NFS cannot mount a folder that does not exist yet (a new job,
            // or one whose folder was removed). The worker mounts the whole share, so it makes sure the folder is there.
            var jobDir = env.JobDirectory(job.Name);
            var made = await workerSession.RunAsync($"mkdir -p -m 0770 -- {ShellQuote.Quote(jobDir)}", TimeSpan.FromMinutes(2), ct);
            if (!made.Succeeded)
            {
                throw new RemoteOperationException(FailureClass.StorageError,
                    $"Could not create the backup folder {jobDir} on the NFS share: {made.StdErr.Trim()}");
            }
        }

        var dir = $"{ExecutionEnvironment.TargetWorkDirectory}/backora-run-{run.Id}";
        var connector = sp.GetRequiredService<ISshConnector>();
        var target = env.TargetEndpoint(server, envFactory.DecryptServerKey(server));
        var session = await ctx.GetSessionAsync($"target:{server.Id}", () => connector.ConnectTargetAsync(env.Worker, target, ct));

        // Idempotent start: if a previous owner crashed after launching but before recording the PID, adopt
        // the running process instead of launching a duplicate (and never rewrite a script bash is reading).
        var existing = await DetachedProcess.GetStatusAsync(session, dir, ct);
        int pid;
        if (existing.Pid is { } adoptedPid)
        {
            pid = adoptedPid;
            run.AppendLogLine(now, $"Adopted already-started remote process pid {pid}");
        }
        else
        {
            await DetachedProcess.PrepareDirectoryAsync(session, dir, ct);
            await session.WriteFileAsync($"{dir}/script.sh", rendered.Content, 700, ct);
            pid = await DetachedProcess.StartAsync(session, dir, "bash ./script.sh", ct);
        }

        run.RemoteDirectory = dir;
        run.RemotePid = pid;
        run.TransitionTo(RunStatus.Running, now);
        run.AppendLogLine(now, $"Started '{template.Name}' (rev {template.Revision}, sha256 {rendered.Sha256[..12]}…) on {server.Name} [{server.Host}] as pid {pid}");
        await db.SaveChangesAsync(ct);
        Logger.LogInformation("Backup run {RunId} started remote pid {Pid} on server {ServerId}", run.Id, pid, server.Id);
        return StepOutcome.Poll;
    }

    private async Task<StepOutcome> MonitorAsync(IServiceProvider sp, IAppDbContext db, BackupRun run, OperationContext ctx, DateTimeOffset now, CancellationToken ct)
    {
        var session = await TargetSessionAsync(sp, run, ctx, ct);
        var dir = run.RemoteDirectory!;
        var status = await DetachedProcess.GetStatusAsync(session, dir, ct);
        await DrainLogAsync(session, run, dir, all: status.State != RemoteProcessState.Running, ct);
        if (status.State == RemoteProcessState.Running && status.Progress is { } progress) RunProgress.Apply(run, progress, now);

        switch (status.State)
        {
            case RemoteProcessState.Running when run.TimeoutAt is { } timeout && now > timeout:
            {
                var minutes = run.BackupJob?.TimeoutMinutes ?? 360;
                var started = run.StartedAt ?? now;
                var sample = await DetachedProcess.SampleIoAsync(session, dir, ct);
                var verdict = RunTimeout.Decide(sample, now, started, minutes);
                if (verdict is RunTimeout.Verdict.Measure or RunTimeout.Verdict.Extend)
                {
                    run.TimeoutAt = RunTimeout.NextDeadline(verdict, now, started, minutes);
                    var limit = RunTimeout.HardLimit(started, minutes);
                    run.AppendLogLine(now, verdict == RunTimeout.Verdict.Measure
                        ? $"Timeout of {minutes} minutes reached; checking whether the backup is still reading and writing data before stopping it"
                        : $"Still working past the {minutes}-minute timeout ({ProgressText(sample!)}); deadline extended to {run.TimeoutAt:yyyy-MM-dd HH:mm} UTC, hard limit {limit:yyyy-MM-dd HH:mm} UTC");
                    await db.SaveChangesAsync(ct);
                    return StepOutcome.Poll;
                }

                var reason = verdict switch
                {
                    RunTimeout.Verdict.HardLimit => $"Backup was still running after {RunTimeout.HardLimitFactor}× its timeout of {minutes} minutes and was terminated. Raise the job's timeout if it really needs longer.",
                    RunTimeout.Verdict.Stalled => $"Backup exceeded its timeout of {minutes} minutes and was terminated: it read and wrote almost nothing in the last {Math.Max(1, (sample!.Epoch - sample.PreviousEpoch!.Value) / 60)} minutes ({ProgressText(sample)}).",
                    _ => $"Backup exceeded its timeout of {minutes} minutes and was terminated.",
                };
                run.AppendLogLine(now, $"{reason} Terminating process group {run.RemotePid}");
                await DetachedProcess.TerminateAsync(session, dir, ct);
                await FailAsync(sp, db, run, FailureClass.Timeout, reason, now, ct);
                await RemovePartialOutputAsync(session, run, dir, now, ct);
                await CleanupTargetAsync(session, dir, ct);
                return StepOutcome.Terminal;
            }

            case RemoteProcessState.Running:
                await db.SaveChangesAsync(ct);
                return StepOutcome.Poll;

            case RemoteProcessState.Exited:
                run.ExitCode = status.ExitCode;
                // The script (which may contain rendered secrets) is removed as soon as it has finished.
                await session.RunAsync($"rm -f -- {ShellQuote.Quote(dir + "/script.sh")}", TimeSpan.FromSeconds(30), ct);
                if (status.ExitCode == 0)
                {
                    await HandOverOutputsAsync(sp, session, run, dir, ct);
                    run.AppendLogLine(now, "Script exited with code 0; verifying artifacts");
                    run.TransitionTo(RunStatus.Verifying, now);
                    await db.SaveChangesAsync(ct);
                    return StepOutcome.Continue;
                }
                await FailAsync(sp, db, run, FailureClass.ScriptError, $"Backup script exited with code {status.ExitCode}. {LastErrorLine(run.Log)}".Trim(), now, ct);
                await RemovePartialOutputAsync(session, run, dir, now, ct);
                await CleanupTargetAsync(session, dir, ct);
                return StepOutcome.Terminal;

            default:
                await FailAsync(sp, db, run, FailureClass.ProcessLost,
                    "The remote backup process ended without recording an exit code (killed, OOM or host reboot).", now, ct);
                await RemovePartialOutputAsync(session, run, dir, now, ct);
                await CleanupTargetAsync(session, dir, ct);
                return StepOutcome.Terminal;
        }
    }

    private async Task<StepOutcome> VerifyAsync(IServiceProvider sp, IAppDbContext db, BackupRun run, OperationContext ctx, DateTimeOffset now, CancellationToken ct)
    {
        var job = run.BackupJob!;
        var env = await sp.GetRequiredService<ExecutionEnvironmentFactory>().CreateAsync(ct);
        var connector = sp.GetRequiredService<ISshConnector>();
        // Verification polls for as long as the checksums take: reuse one worker session for the whole operation.
        var worker = await ctx.GetSessionAsync("worker", () => connector.ConnectWorkerAsync(env.Worker, ct));
        var jobDir = env.JobDirectory(run.JobName);
        var workerNow = await RemoteFiles.EpochAsync(worker, ct);
        var startEpoch = run.WorkerStartEpoch ?? (run.StartedAt ?? now).ToUnixTimeSeconds();
        var sinceMinutes = (int)Math.Ceiling((workerNow - startEpoch) / 60.0) + Options.ArtifactDiscoverySkewMinutes;

        // Checksums run detached on the worker (a large backup takes a long while to hash) and report live progress.
        var scanDir = $"{env.WorkerWorkDirectory.TrimEnd('/')}/backora-verify-{run.Id}";
        var scan = await DetachedProcess.GetStatusAsync(worker, scanDir, ct);
        switch (scan.State)
        {
            case RemoteProcessState.NotStarted:
            {
                var compress = await CompressesAsync(db, job.Name, ct);
                await DetachedProcess.PrepareDirectoryAsync(worker, scanDir, ct);
                await worker.WriteFileAsync($"{scanDir}/hasher.py", ChecksumScripts.Hasher, 600, ct);
                if (compress)
                {
                    // Files earlier backups of this job recorded as their own (still on the share) are never compressed by this run.
                    var owned = await db.BackupArtifacts.AsNoTracking()
                        .Where(a => a.BackupRun!.BackupJobId == run.BackupJobId && a.BackupRunId != run.Id && !a.LocalCopyDeleted)
                        .Select(a => a.RelativePath).Distinct().ToListAsync(ct);
                    await worker.WriteFileAsync($"{scanDir}/exclude.txt", owned.Count == 0 ? "" : string.Join('\n', owned) + "\n", 600, ct);
                }
                // Not before the run started (a minute's leeway for clocks): never the previous backup's files.
                await worker.WriteFileAsync($"{scanDir}/scan.sh", ChecksumScripts.BackupFiles(jobDir, sinceMinutes, compress, startEpoch - 60), 700, ct);
                await DetachedProcess.StartAsync(worker, scanDir, "bash ./scan.sh", ct);
                run.AppendLogLine(now, compress
                    ? $"Compressing the backup on the worker into one file per output (zstd), then computing its checksums, in {jobDir}"
                    : $"Computing checksums of the files in {jobDir}");
                await db.SaveChangesAsync(ct);
                return StepOutcome.Poll;
            }
            case RemoteProcessState.Running:
                if (scan.Progress is { } p) RunProgress.Apply(run, p, now);
                await db.SaveChangesAsync(ct);
                return StepOutcome.Poll;
            case RemoteProcessState.Lost:
                run.AppendLogLine(now, "The checksum process ended without a result; starting it again");
                await DetachedProcess.RemoveDirectoryAsync(worker, scanDir, ct);
                await db.SaveChangesAsync(ct);
                return StepOutcome.Poll;
        }
        await ReportCompressionAsync(worker, run, scanDir, now, ct);
        if (scan.ExitCode != 0)
        {
            await RemoveUnfinishedCompressionAsync(worker, jobDir, ct);
            await DetachedProcess.RemoveDirectoryAsync(worker, scanDir, ct);
            throw new RemoteOperationException(FailureClass.VerificationFailed, $"Computing checksums failed (exit code {scan.ExitCode}).");
        }
        var output = (await worker.RunAsync($"cat {ShellQuote.Quote(scanDir + "/result")} 2>/dev/null", TimeSpan.FromMinutes(5), ct)).StdOut;
        if (!output.Contains("NODIR", StringComparison.Ordinal) && !output.Contains("END", StringComparison.Ordinal))
            throw new RemoteOperationException(FailureClass.VerificationFailed, "The checksum results are incomplete.");
        // One entry per file: a file listed twice would make saving its artifacts fail on every attempt.
        var files = output.Contains("NODIR", StringComparison.Ordinal) ? new List<ScannedFile>()
            : RemoteFiles.Parse(output).DistinctBy(f => f.RelativePath, StringComparer.Ordinal).ToList();
        // The scan folder (results, compression record) stays until the outcome is saved: a restart in between (a deploy)
        // then reads the same finished results again instead of starting the step over.
        // Files modified slightly before start (skew window) but not after are excluded unless newer than start - skew.
        var cutoff = DateTimeOffset.FromUnixTimeSeconds(startEpoch).AddMinutes(-Options.ArtifactDiscoverySkewMinutes);
        files = files.Where(f => f.ModifiedAt >= cutoff).ToList();
        // Never attribute a file to two runs: exclude artifacts already recorded for earlier runs of this job
        // (relevant for frequent schedules where the skew window overlaps a previous run).
        var paths = files.Select(f => f.RelativePath).ToList();
        var known = await db.BackupArtifacts.AsNoTracking()
            .Where(a => a.BackupRun!.BackupJobId == run.BackupJobId && a.BackupRunId != run.Id && paths.Contains(a.RelativePath))
            .Select(a => new { a.RelativePath, a.Sha256 }).ToListAsync(ct);
        files = files.Where(f => !known.Any(k => k.RelativePath == f.RelativePath && k.Sha256 == f.Sha256)).ToList();

        var empty = files.Where(f => f.SizeBytes == 0).Select(f => f.RelativePath).ToList();
        if (job.RequireArtifacts && files.Count == 0)
        {
            await FailAsync(sp, db, run, FailureClass.NoArtifacts,
                $"The script exited successfully but produced no files in {jobDir}. A backup without artifacts is not considered successful.", now, ct);
            // Worker first: opening the session to the database server closes the worker's (one session per operation).
            await RemoveScanFolderAsync(worker, scanDir, run, ct);
            await RemoveRunFolderAsync(sp, run, ctx, ct);
            return StepOutcome.Terminal;
        }
        if (empty.Count > 0)
        {
            await FailAsync(sp, db, run, FailureClass.VerificationFailed, $"Empty artifact(s) produced: {string.Join(", ", empty.Take(10))}.", now, ct);
            // Worker first: opening the session to the database server closes the worker's (one session per operation).
            await RemoveScanFolderAsync(worker, scanDir, run, ct);
            await RemoveRunFolderAsync(sp, run, ctx, ct);
            return StepOutcome.Terminal;
        }

        // A run that is a sliver of the job's recent good backups dumped the wrong thing (database, user, privileges):
        // refuse it rather than letting it rotate good recovery points out.
        var produced = files.Sum(f => f.SizeBytes);
        // Baseline: the larger of the last five completed runs and the last five primary uploads (imported history has
        // no run sizes, but its uploads do).
        // A compressed backup is only compared with earlier compressed ones: against uncompressed history a database with
        // much free space (which compresses far more than 100x) would look like a sliver.
        long? baseline;
        if (files.Any(f => BackupSizeGuard.IsCompressed(f.RelativePath)))
        {
            baseline = await db.BackupRuns.AsNoTracking()
                .Where(r => r.BackupJobId == run.BackupJobId && r.Id != run.Id && r.Status == RunStatus.Completed && r.TotalArtifactBytes > 0
                            && r.Artifacts.Any(a => a.RelativePath.EndsWith(".zst") || a.RelativePath.EndsWith(".gz") || a.RelativePath.EndsWith(".zst.gpg") || a.RelativePath.EndsWith(".gz.gpg")))
                .OrderByDescending(r => r.CompletedAt).Take(5).MaxAsync(r => r.TotalArtifactBytes, ct);
            if (baseline is null) run.AppendLogLine(now, "First compressed backup of this job: the size check against recent backups starts with the next one");
        }
        else
        {
            var recentRuns = await db.BackupRuns.AsNoTracking()
                .Where(r => r.BackupJobId == run.BackupJobId && r.Id != run.Id && r.Status == RunStatus.Completed && r.TotalArtifactBytes > 0)
                .OrderByDescending(r => r.CompletedAt).Take(5).MaxAsync(r => r.TotalArtifactBytes, ct);
            var recentUploads = await db.OffloadRuns.AsNoTracking()
                .Where(o => o.BackupJobId == run.BackupJobId && o.SourceOffloadRunId == null && o.Status == OffloadStatus.Completed && o.BytesTotal > 0)
                .OrderByDescending(o => o.CompletedAt).Take(5).MaxAsync(o => o.BytesTotal, ct);
            baseline = recentRuns is null && recentUploads is null ? null : Math.Max(recentRuns ?? 0, recentUploads ?? 0);
        }
        var skipVar = TemplateSettings.VariableName(BackupSizeGuard.SkipVariable, job.Name);
        if (BackupSizeGuard.IsSuspicious(produced, baseline) && !await db.Variables.AnyAsync(v => v.Name == skipVar, ct))
        {
            await FailAsync(sp, db, run, FailureClass.VerificationFailed,
                $"The backup is {produced:N0} bytes, under 1% of this job's recent backups (up to {baseline:N0} bytes). " +
                "The script most likely dumped the wrong database or ran as a user without privileges; check the job's settings under Templates → Variables. " +
                $"If the data really shrank, add the variable {skipVar} to accept it.", now, ct);
            // Worker first: opening the session to the database server closes the worker's (one session per operation).
            await RemoveScanFolderAsync(worker, scanDir, run, ct);
            await RemoveRunFolderAsync(sp, run, ctx, ct);
            return StepOutcome.Terminal;
        }

        foreach (var f in files)
        {
            run.Artifacts.Add(new BackupArtifact { RelativePath = f.RelativePath, SizeBytes = f.SizeBytes, Sha256 = f.Sha256, Md5 = f.Md5, ModifiedAt = f.ModifiedAt });
            run.AppendLogLine(now, $"Artifact {f.RelativePath} {f.SizeBytes:N0} bytes sha256 {f.Sha256}");
        }
        run.ArtifactCount = files.Count;
        run.TotalArtifactBytes = files.Sum(f => f.SizeBytes);
        run.VerificationStatus = files.Count > 0 ? VerificationStatus.Verified : VerificationStatus.NotVerified;
        run.TransitionTo(RunStatus.Completed, now);
        run.AppendLogLine(now, $"Backup completed: {files.Count} artifact(s), {run.TotalArtifactBytes:N0} bytes");

        var primaryEnabled = job.S3DestinationId is { } pid && await db.S3Destinations.Where(d => d.Id == pid).Select(d => d.IsEnabled).FirstOrDefaultAsync(ct);
        if (files.Count > 0 && job.S3DestinationId is not null && !primaryEnabled)
        {
            run.AppendLogLine(now, "Off-site upload skipped: the destination is disabled. The backup stays on the staging share.");
        }
        if (files.Count > 0 && primaryEnabled && job.S3DestinationId is { } destId && job.S3DestinationAssignedAt is { } assigned && assigned <= run.CreatedAt)
        {
            db.OffloadRuns.Add(new OffloadRun
            {
                BackupRun = run,
                BackupJobId = job.Id,
                S3DestinationId = destId,
                ServerId = run.ServerId,
                JobName = run.JobName,
                SourcePath = jobDir,
                MaxAttempts = 3,
                NextAttemptAt = now,
                CorrelationId = run.CorrelationId,
                CleanupStatus = job.KeepLocalCopy ? CleanupStatus.Skipped : CleanupStatus.Pending,
            });
            run.AppendLogLine(now, "Queued offload to S3 destination");
        }

        await sp.GetRequiredService<NotificationPublisher>().PublishBackupAsync(run, NotificationEvents.BackupSucceeded, ct);
        await db.SaveChangesAsync(ct);
        BackoraTelemetry.RecordTerminal(Kind, "completed", FailureClass.None.ToString(), run.Duration);

        // Worker first: opening the session to the database server closes the worker's (one session per operation).
        await RemoveScanFolderAsync(worker, scanDir, run, ct);
        await RemoveRunFolderAsync(sp, run, ctx, ct);
        return StepOutcome.Terminal;
    }

    private async Task RemoveScanFolderAsync(IRemoteSession worker, string scanDir, BackupRun run, CancellationToken ct)
    {
        try { await DetachedProcess.RemoveDirectoryAsync(worker, scanDir, ct); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Logger.LogWarning(ex, "Could not remove the verification folder of run {RunId} on the worker", run.Id);
        }
    }

    /// <summary>The run's folder on the database server (its log, exit code) is no longer needed once the run is over.</summary>
    private async Task RemoveRunFolderAsync(IServiceProvider sp, BackupRun run, OperationContext ctx, CancellationToken ct)
    {
        if (run.RemoteDirectory is null) return;
        try
        {
            var session = await TargetSessionAsync(sp, run, ctx, ct);
            await CleanupTargetAsync(session, run.RemoteDirectory, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Logger.LogWarning(ex, "Could not remove working directory for run {RunId}", run.Id);
        }
    }

    private async Task<StepOutcome> CancelAsync(IServiceProvider sp, IAppDbContext db, BackupRun run, OperationContext ctx, DateTimeOffset now, CancellationToken ct)
    {
        // A run cancelled while its checksums were being computed: stop that work on the worker too.
        try
        {
            var env = await sp.GetRequiredService<ExecutionEnvironmentFactory>().CreateAsync(ct);
            await using var worker = await sp.GetRequiredService<ISshConnector>().ConnectWorkerAsync(env.Worker, ct);
            var scanDir = $"{env.WorkerWorkDirectory.TrimEnd('/')}/backora-verify-{run.Id}";
            if ((await DetachedProcess.GetStatusAsync(worker, scanDir, ct)).State != RemoteProcessState.NotStarted)
            {
                await DetachedProcess.TerminateAsync(worker, scanDir, ct);
                await RemoveUnfinishedCompressionAsync(worker, env.JobDirectory(run.JobName), ct);
                await DetachedProcess.RemoveDirectoryAsync(worker, scanDir, ct);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Logger.LogWarning(ex, "Could not stop the checksum work of cancelled run {RunId} on the worker", run.Id);
        }

        if (run.RemoteDirectory is not null)
        {
            var session = await TargetSessionAsync(sp, run, ctx, ct);
            var dead = await DetachedProcess.TerminateAsync(session, run.RemoteDirectory, ct);
            await DrainLogAsync(session, run, run.RemoteDirectory, all: true, ct);
            if (!dead)
            {
                run.AppendLogLine(now, "Process group did not terminate yet; retrying");
                await db.SaveChangesAsync(ct);
                return StepOutcome.Poll;
            }
            await RemovePartialOutputAsync(session, run, run.RemoteDirectory, now, ct);
            await CleanupTargetAsync(session, run.RemoteDirectory, ct);
        }

        run.FailureClass = FailureClass.Cancelled;
        run.ErrorMessage = "Cancelled by user.";
        run.AppendLogLine(now, "Run cancelled; remote process group terminated");
        run.TransitionTo(RunStatus.Cancelled, now);
        await db.SaveChangesAsync(ct);
        BackoraTelemetry.RecordTerminal(Kind, "cancelled", FailureClass.Cancelled.ToString(), run.Duration);
        return StepOutcome.Terminal;
    }

    private async Task<StepOutcome> HandleRemoteFailureAsync(IServiceProvider sp, IAppDbContext db, BackupRun run, RemoteOperationException ex, DateTimeOffset now, CancellationToken ct)
    {
        Logger.LogWarning("Backup run {RunId} remote error ({FailureClass}): {Message}", run.Id, ex.FailureClass, ex.Message);

        // While running, connectivity loss to the target does not mean the backup failed: the detached
        // process keeps going. Keep polling until the timeout.
        if (run.Status is RunStatus.Running or RunStatus.Verifying or RunStatus.Cancelling && IsTransient(ex.FailureClass))
        {
            if (run.TimeoutAt is { } t && now > t.AddMinutes(30))
            {
                await FailAsync(sp, db, run, ex.FailureClass, $"Lost contact with the server beyond the timeout: {ex.Message}", now, ct);
                return StepOutcome.Terminal;
            }
            run.AppendLogLine(now, $"Transient error, will retry: {ex.Message}");
            await db.SaveChangesAsync(ct);
            return StepOutcome.Poll;
        }

        if (run.Status == RunStatus.Starting && run.RemotePid is null && IsTransient(ex.FailureClass) && run.TryScheduleRetry(now))
        {
            run.AppendLogLine(now, $"Attempt {run.Attempt} failed to start ({ex.Message}); next attempt at {run.NextAttemptAt:u}");
            run.TransitionTo(RunStatus.Pending, now);
            await db.SaveChangesAsync(ct);
            return StepOutcome.Yield;
        }

        await FailAsync(sp, db, run, ex.FailureClass, ex.Message, now, ct);
        return StepOutcome.Terminal;
    }

    /// <summary>
    /// The template records the paths of its outputs (./outputs in the run folder on the server). They are handed to the
    /// worker, whose compress step then compresses exactly those and nothing else in the job folder (another run's
    /// files, or another system writing there). Templates that record nothing fall back to "written since the run started".
    /// </summary>
    private async Task HandOverOutputsAsync(IServiceProvider sp, IRemoteSession target, BackupRun run, string dir, CancellationToken ct)
    {
        try
        {
            var listed = (await target.RunAsync($"cat {ShellQuote.Quote(dir + "/outputs")} 2>/dev/null", TimeSpan.FromSeconds(30), ct)).StdOut;
            var names = OutputNames(listed, run.JobName);
            if (names.Count == 0) return;
            var env = await sp.GetRequiredService<ExecutionEnvironmentFactory>().CreateAsync(ct);
            await using var worker = await sp.GetRequiredService<ISshConnector>().ConnectWorkerAsync(env.Worker, ct);
            var scanDir = $"{env.WorkerWorkDirectory.TrimEnd('/')}/backora-verify-{run.Id}";
            await DetachedProcess.PrepareDirectoryAsync(worker, scanDir, ct);
            await worker.WriteFileAsync($"{scanDir}/outputs.txt", string.Join('\n', names) + "\n", 600, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The compress step then falls back to "written since the run started".
            Logger.LogWarning(ex, "Could not hand over the outputs of run {RunId}", run.Id);
        }
    }

    /// <summary>The job folder entries named by the template's output record (absolute paths of this job's own outputs).</summary>
    public static IReadOnlyList<string> OutputNames(string record, string jobName) =>
        record.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(p => IsOwnOutput(p, jobName))
            .Select(p => p.TrimEnd('/')[(p.TrimEnd('/').LastIndexOf('/') + 1)..])
            .Distinct(StringComparer.Ordinal).ToList();

    /// <summary>Whether a finished backup is compressed on the worker: on unless the job (COMPRESSION__job) or all jobs (COMPRESSION) say none.</summary>
    private async Task<bool> CompressesAsync(IAppDbContext db, string jobName, CancellationToken ct)
    {
        var perJob = TemplateSettings.VariableName("COMPRESSION", jobName);
        var values = await db.Variables.AsNoTracking().Where(v => !v.IsSecret && (v.Name == perJob || v.Name == "COMPRESSION"))
            .Select(v => new { v.Name, v.Value }).ToListAsync(ct);
        var value = values.FirstOrDefault(v => v.Name == perJob)?.Value ?? values.FirstOrDefault(v => v.Name == "COMPRESSION")?.Value ?? Options.BackupCompression;
        return !IsOff(value);
    }

    public static bool IsOff(string? value) =>
        (value ?? string.Empty).Trim().ToLowerInvariant() is "none" or "off" or "no" or "false" or "0";

    /// <summary>Turns the compress step's record into run log lines (what was compressed, how much smaller, what was left as it is).</summary>
    private static async Task ReportCompressionAsync(IRemoteSession worker, BackupRun run, string scanDir, DateTimeOffset now, CancellationToken ct)
    {
        var log = (await worker.RunAsync($"cat {ShellQuote.Quote(scanDir + "/" + ChecksumScripts.CompressLog)} 2>/dev/null", TimeSpan.FromSeconds(30), ct)).StdOut;
        foreach (var line in CompressionLogLines(log)) run.AppendLogLine(now, line);
    }

    /// <summary>Readable lines for the compress step's record (see <see cref="ChecksumScripts.CompressLog"/>).</summary>
    public static IEnumerable<string> CompressionLogLines(string log)
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        foreach (var raw in log.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var p = raw.TrimEnd('\r').Split('\t');
            if (p[0] == "COMPRESSED" && p.Length == 6 && long.TryParse(p[3], inv, out var before) && long.TryParse(p[4], inv, out var after) && long.TryParse(p[5], inv, out var seconds))
            {
                var ratio = after > 0 ? (double)before / after : 0;
                var rate = seconds > 0 ? before / seconds : before;
                yield return $"Compressed {p[1]} into {p[2]} on the worker: {Bytes(before)} → {Bytes(after)} ({ratio.ToString("0.0", inv)}× smaller) in {Duration(seconds)} ({Bytes(rate)}/s); checksums taken of the compressed file, original removed";
            }
            else if (p[0] == "SKIPPED" && p.Length >= 3)
            {
                yield return p[1] == "*" ? $"Not compressed: {p[2]}" : $"{p[1]} left as it is: {p[2]}";
            }
            else if (p[0] == "FAILED" && p.Length >= 3)
            {
                yield return $"Warning: {p[1]} could not be compressed: {p[2]}";
            }
        }
    }

    private static string Bytes(long b)
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        string[] units = ["B", "KiB", "MiB", "GiB", "TiB"];
        double v = b; var u = 0;
        while (v >= 1024 && u < units.Length - 1) { v /= 1024; u++; }
        return u == 0 ? $"{b} B" : $"{v.ToString(v >= 100 ? "0" : "0.0", inv)} {units[u]}";
    }

    private static string Duration(long seconds) =>
        seconds < 60 ? $"{seconds}s" : seconds < 3600 ? $"{seconds / 60}m {seconds % 60}s" : $"{seconds / 3600}h {seconds % 3600 / 60}m";

    /// <summary>A compressed file that was still being written when the step stopped is not a backup: remove it (the raw output stays).</summary>
    private async Task RemoveUnfinishedCompressionAsync(IRemoteSession worker, string jobDir, CancellationToken ct)
    {
        try
        {
            await worker.RunAsync($"find {ShellQuote.Quote(jobDir)} -maxdepth 1 -name {ShellQuote.Quote("*" + ChecksumScripts.CompressPartialSuffix)} -exec rm -f -- {{}} + 2>/dev/null; true",
                TimeSpan.FromMinutes(2), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Logger.LogWarning(ex, "Could not remove an unfinished compressed file in {JobDirectory}", jobDir);
        }
    }

    private static string ProgressText(IoSample s)
    {
        var min = Math.Max(1, (s.Epoch - (s.PreviousEpoch ?? s.Epoch)) / 60);
        if (s.Bytes < s.PreviousBytes) return $"one step finished and the next started in the last {min} min";
        var mib = (s.Bytes - (s.PreviousBytes ?? s.Bytes)) / 1048576.0;
        return $"{mib:N0} MiB read and written in {min} min";
    }

    private async Task FailAsync(IServiceProvider sp, IAppDbContext db, BackupRun run, FailureClass failureClass, string message, DateTimeOffset now, CancellationToken ct)
    {
        if (run.IsTerminal) return;
        if (run.Status == RunStatus.Cancelling && failureClass != FailureClass.Cancelled)
        {
            run.AppendLogLine(now, "Cancellation requested; error during termination: " + message);
        }
        run.Fail(failureClass, message, now);
        await sp.GetRequiredService<NotificationPublisher>().PublishBackupAsync(run, NotificationEvents.BackupFailed, ct);
        await db.SaveChangesAsync(ct);
        BackoraTelemetry.RecordTerminal(Kind, "failed", failureClass.ToString(), run.Duration);
    }

    private async Task<IRemoteSession> TargetSessionAsync(IServiceProvider sp, BackupRun run, OperationContext ctx, CancellationToken ct)
    {
        var server = run.Server ?? throw new RemoteOperationException(FailureClass.Configuration, "Server no longer exists.");
        var envFactory = sp.GetRequiredService<ExecutionEnvironmentFactory>();
        var connector = sp.GetRequiredService<ISshConnector>();
        return await ctx.GetSessionAsync($"target:{server.Id}", async () =>
        {
            var env = await envFactory.CreateAsync(ct);
            return await connector.ConnectTargetAsync(env.Worker, env.TargetEndpoint(server, envFactory.DecryptServerKey(server)), ct);
        });
    }

    private static async Task DrainLogAsync(IRemoteSession session, BackupRun run, string dir, bool all, CancellationToken ct)
    {
        for (var i = 0; i < (all ? 40 : 1); i++)
        {
            var chunk = await DetachedProcess.ReadLogAsync(session, dir, run.LogOffset, ct);
            if (chunk.NewOffset == run.LogOffset) break;
            run.AppendLog(chunk.Text);
            run.LogOffset = chunk.NewOffset;
        }
    }

    /// <summary>
    /// A failed, stopped or lost run can leave output on the share: a timeout or cancel ends in SIGKILL before the
    /// script's own cleanup finishes deleting a large folder over NFS, and a lost process cleans nothing. The templates
    /// record every output path in the run folder (./partial; cleared when the run succeeds or its cleanup completed);
    /// remove exactly those, in the background on the server, after checking each path is this job's own output.
    /// </summary>
    private async Task RemovePartialOutputAsync(IRemoteSession session, BackupRun run, string dir, DateTimeOffset now, CancellationToken ct)
    {
        try
        {
            var listed = await session.RunAsync($"cat {ShellQuote.Quote(dir + "/partial")} 2>/dev/null", TimeSpan.FromSeconds(30), ct);
            var paths = listed.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Distinct(StringComparer.Ordinal).Where(p => IsOwnOutput(p, run.JobName)).ToList();
            if (paths.Count == 0) return;
            var targets = string.Join(' ', paths.Select(ShellQuote.Quote));
            await session.RunAsync($"(command -v setsid >/dev/null 2>&1 && S=setsid || S=; nohup $S rm -rf -- {targets} </dev/null >/dev/null 2>&1 &)", TimeSpan.FromSeconds(30), ct);
            run.AppendLogLine(now, $"Removing the incomplete output the stopped run left on the share: {string.Join(", ", paths)}");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Logger.LogWarning(ex, "Could not remove the partial output of run {RunId}", run.Id);
        }
    }

    /// <summary>An absolute path, no "..", whose name is this job's output (&lt;job&gt;-&lt;time&gt;…), as the templates name it.</summary>
    public static bool IsOwnOutput(string path, string jobName)
    {
        if (path.Length < 3 || path[0] != '/' || path.Contains('\0') || path.Split('/').Any(s => s is ".." or ".")) return false;
        var name = path.TrimEnd('/');
        name = name[(name.LastIndexOf('/') + 1)..];
        return path.Count(c => c == '/') >= 2 && name.StartsWith(jobName + "-", StringComparison.Ordinal);
    }

    private async Task CleanupTargetAsync(IRemoteSession session, string dir, CancellationToken ct)
    {
        try { await DetachedProcess.RemoveDirectoryAsync(session, dir, ct); }
        catch (Exception ex) when (ex is not OperationCanceledException) { Logger.LogWarning(ex, "Failed to remove remote working directory {Dir}", dir); }
    }

    private static string LastErrorLine(string? log)
    {
        if (string.IsNullOrEmpty(log)) return string.Empty;
        var line = log.Split('\n').Reverse().FirstOrDefault(l =>
            l.Contains("error", StringComparison.OrdinalIgnoreCase) || l.Contains("failed", StringComparison.OrdinalIgnoreCase) || l.Contains("fatal", StringComparison.OrdinalIgnoreCase));
        return line is null ? string.Empty : "Last error: " + (line.Length > 400 ? line[..400] : line.Trim());
    }
}
