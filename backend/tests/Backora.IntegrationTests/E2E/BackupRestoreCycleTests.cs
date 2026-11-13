using System.Net;
using System.Net.Http.Json;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Backora.Application.Operations;
using Backora.Contracts.V1;
using Backora.Domain.Backups;
using Backora.IntegrationTests.Infrastructure;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Backora.IntegrationTests.E2E;

/// <summary>
/// Real backup → offload → restore cycles over SSH (via the worker jump host), NFS and S3, with content
/// comparison against the original data. Requires Docker. Run with: dotnet test --filter Category=E2E
/// </summary>
[Trait("Category", "E2E")]
public sealed class BackupRestoreCycleTests(E2EEnvironment env) : IClassFixture<E2EEnvironment>
{
    private const string BackupScript = """
        #!/bin/bash
        set -euo pipefail
        OUT="{NFS_PATH}/{JOB_NAME}"
        mkdir -p "$OUT"
        echo "Starting {CATEGORY} file backup for {JOB_NAME} (secret length: $(printf %s '{e2e_secret}' | wc -c))"
        tar -czf "$OUT/{JOB_NAME}-$(date +%Y%m%d%H%M%S)-$RANDOM.tar.gz" -C /srv/data .
        echo "Backup completed successfully"
        """;

    private const string SecretValue = "S3cr3t-Value-That-Must-Not-Leak";

    private async Task<long> CreateTemplateAsync(string name, string script)
    {
        var vars = await (await env.Admin.GetAsync("/api/v1/variables")).ReadAsync<List<VariableDto>>();
        if (!vars.Any(v => v.Name == "e2e_secret"))
        {
            (await env.Admin.PostAsJsonAsync("/api/v1/variables", new VariableUpsertRequest("e2e_secret", true, SecretValue, "E2E secret"))).EnsureSuccessStatusCode();
        }
        var t = await (await env.Admin.PostAsJsonAsync("/api/v1/templates", Requests.Template(name, script, "Files"))).ReadAsync<TemplateDto>();
        return t.Id;
    }

    private async Task<BackupJobDto> CreateJobAsync(string script, bool offload = true, decimal retention = 7, bool keepLocal = false, int timeout = 30)
    {
        var name = "e2e-" + Guid.NewGuid().ToString("N")[..10];
        var templateId = await CreateTemplateAsync("tpl-" + name, script);
        return await (await env.Admin.PostAsJsonAsync("/api/v1/backup-jobs",
            Requests.Job(name, templateId, env.ServerId, destination: offload ? env.DestinationId : null, retention: retention, timeout: timeout, keepLocal: keepLocal)))
            .ReadAsync<BackupJobDto>();
    }

    private async Task<long> RunNowAsync(long jobId)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/backup-jobs/{jobId}/runs");
        req.Headers.Add(ApiHeaders.IdempotencyKey, Guid.NewGuid().ToString());
        var created = await (await env.Admin.SendAsync(req)).ReadAsync<RunCreatedResponse>();
        return created.RunId;
    }

    private async Task<RunDetailDto> WaitRunAsync(long runId, Func<RunDetailDto, bool> until, int seconds = 120)
    {
        RunDetailDto? last = null;
        for (var i = 0; i < seconds * 2; i++)
        {
            last = await (await env.Admin.GetAsync($"/api/v1/runs/{runId}")).ReadAsync<RunDetailDto>();
            if (until(last)) return last;
            await Task.Delay(500);
        }
        throw new TimeoutException($"Run {runId} stuck in {last?.Summary.Status}: {last?.Summary.ErrorMessage}\n{last?.Log}");
    }

    private async Task<OffloadDetailDto> WaitOffloadAsync(long offloadId, Func<OffloadDetailDto, bool> until, int seconds = 120)
    {
        OffloadDetailDto? last = null;
        for (var i = 0; i < seconds * 2; i++)
        {
            last = await (await env.Admin.GetAsync($"/api/v1/offloads/{offloadId}")).ReadAsync<OffloadDetailDto>();
            if (until(last)) return last;
            await Task.Delay(500);
        }
        throw new TimeoutException($"Offload {offloadId} stuck in {last?.Summary.Status}: {last?.Summary.ErrorMessage}\n{last?.Log}");
    }

    private async Task<RestoreDetailDto> RestoreAsync(long recoveryPointId)
    {
        var pre = await (await env.Admin.PostAsJsonAsync("/api/v1/restores/precheck", new RestorePrecheckRequest(recoveryPointId))).ReadAsync<RestorePrecheckResponse>();
        pre.CanRestore.Should().BeTrue(string.Join("; ", pre.Checks.Select(c => $"{c.Name}:{c.State}:{c.Message}")));

        var wrong = await env.Admin.PostAsJsonAsync("/api/v1/restores", new CreateRestoreRequest(recoveryPointId, "E2E verification restore", "restore something-else"));
        wrong.StatusCode.Should().Be(HttpStatusCode.BadRequest, "the typed confirmation phrase must match exactly");

        using var req = new HttpRequestMessage(HttpMethod.Post, "/api/v1/restores") { Content = JsonContent.Create(new CreateRestoreRequest(recoveryPointId, "E2E verification restore", pre.ConfirmationPhrase)) };
        req.Headers.Add(ApiHeaders.IdempotencyKey, Guid.NewGuid().ToString());
        var restore = await (await env.Admin.SendAsync(req)).ReadAsync<RestoreSummaryDto>();
        RestoreDetailDto? last = null;
        for (var i = 0; i < 240; i++)
        {
            last = await (await env.Admin.GetAsync($"/api/v1/restores/{restore.Id}")).ReadAsync<RestoreDetailDto>();
            if (last.Summary.Status is RestoreStatusDto.Completed or RestoreStatusDto.Failed or RestoreStatusDto.Cancelled) return last;
            await Task.Delay(500);
        }
        throw new TimeoutException($"Restore stuck: {last?.Summary.Status}\n{last?.Log}");
    }

    private Task<string> DataFingerprintOnTargetAsync() =>
        env.ExecAsync(env.Target, "cd /srv/data && find . -type f -exec sha256sum {} + | sort -k2");

    [Fact]
    public async Task Full_cycle_backup_offload_restore_restores_identical_content()
    {
        var job = await CreateJobAsync(BackupScript);
        var runId = await RunNowAsync(job.Id);
        var run = await WaitRunAsync(runId, r => r.Summary.Status is RunStatusDto.Completed or RunStatusDto.Failed);

        run.Summary.Status.Should().Be(RunStatusDto.Completed, run.Log);
        run.Summary.VerificationStatus.Should().Be(VerificationStatusDto.Verified);
        run.ExitCode.Should().Be(0);
        run.Artifacts.Should().ContainSingle().Which.Sha256.Should().HaveLength(64);
        run.ScriptSha256.Should().HaveLength(64);
        run.Log.Should().Contain("Backup completed successfully").And.Contain($"secret length: {SecretValue.Length}").And.NotContain(SecretValue);
        var artifact = run.Artifacts[0];

        // The detached process was cleaned up on the target (script with rendered secrets removed).
        (await env.ExecAsync(env.Target, $"ls /var/tmp/backora/backora-run-{runId} 2>&1 || true")).Should().Contain("No such file");

        // Offload: transferred, verified twice, and only then the NFS copy is removed.
        var offloadId = run.Offloads.Should().ContainSingle().Subject.Id;
        var offload = await WaitOffloadAsync(offloadId, o => o.Summary.Status is OffloadStatusDto.Completed or OffloadStatusDto.Failed);
        offload.Summary.Status.Should().Be(OffloadStatusDto.Completed, offload.Log);
        offload.Summary.VerificationStatus.Should().Be(VerificationStatusDto.Verified);
        offload.Summary.CleanupStatus.Should().Be(CleanupStatusDto.Completed);
        offload.Summary.ProgressPercent.Should().Be(100);
        offload.Summary.BytesTotal.Should().Be(artifact.SizeBytes);
        (await env.ExecAsync(env.Worker, $"ls /BackoraDB/{job.Name}/")).Should().NotContain(artifact.RelativePath, "the verified local copy is removed");

        using (var s3 = new AmazonS3Client(new BasicAWSCredentials(E2EEnvironment.S3AccessKey, E2EEnvironment.S3SecretKey), new AmazonS3Config { ServiceURL = env.S3Endpoint, ForcePathStyle = true, AuthenticationRegion = "us-east-1" }))
        {
            var list = await s3.ListObjectsV2Async(new ListObjectsV2Request { BucketName = E2EEnvironment.Bucket, Prefix = offload.Summary.RemotePrefix });
            list.S3Objects.Should().ContainSingle(o => o.Key == offload.Summary.RemotePrefix + artifact.RelativePath && o.Size == artifact.SizeBytes);
        }

        // Restore into an isolated staging directory and compare with the original data byte-for-byte.
        var restore = await RestoreAsync(offloadId);
        restore.Summary.Status.Should().Be(RestoreStatusDto.Completed, restore.Log);
        restore.Summary.VerificationStatus.Should().Be(VerificationStatusDto.Verified);
        restore.Summary.FilesVerified.Should().Be(1);
        restore.Summary.TargetPath.Should().StartWith($"/BackoraDB/{job.Name}/restore-");

        var restoredFingerprint = await env.ExecAsync(env.Worker,
            $"rm -rf /tmp/verify && mkdir -p /tmp/verify && tar -xzf '{restore.Summary.TargetPath}/{artifact.RelativePath}' -C /tmp/verify && cd /tmp/verify && find . -type f -exec sha256sum {{}} + | sort -k2");
        var original = await DataFingerprintOnTargetAsync();
        original.Should().Contain("orders.csv").And.Contain("blob.bin");
        restoredFingerprint.Should().Be(original, "restored content must be identical to the source data");

        // The dashboard reflects real operations.
        var dash = await (await env.Admin.GetAsync("/api/v1/dashboard")).ReadAsync<DashboardDto>();
        dash.Kpis.RunsSucceeded.Should().BeGreaterThan(0);
        dash.Kpis.RecoveryPoints.Should().BeGreaterThan(0);
        dash.System.WorkerOnline.Should().BeTrue();
    }

    [Fact]
    public async Task Dr_replication_copies_to_secondary_s3_and_restores_from_the_dr_copy()
    {
        // Two DR destinations on purpose:
        //  • "dr-remote" uses a different endpoint name → rclone streams through the worker (two remotes);
        //  • "dr-same" shares endpoint and credentials → rclone performs an S3 server-side copy.
        const string drBucket = "backora-e2e-dr", sameBucket = "backora-e2e-dr2";
        using var s3 = new AmazonS3Client(new BasicAWSCredentials(E2EEnvironment.S3AccessKey, E2EEnvironment.S3SecretKey), new AmazonS3Config { ServiceURL = env.S3Endpoint, ForcePathStyle = true, AuthenticationRegion = "us-east-1" });
        foreach (var b in new[] { drBucket, sameBucket })
        {
            try { await s3.PutBucketAsync(b); } catch (AmazonS3Exception e) when (e.ErrorCode is "BucketAlreadyOwnedByYou" or "BucketAlreadyExists") { }
        }
        var suffix = Guid.NewGuid().ToString("N")[..6];
        var drRemote = await (await env.Admin.PostAsJsonAsync("/api/v1/storage/s3", new S3DestinationUpsertRequest("dr-remote-" + suffix, env.S3Endpoint.Replace("127.0.0.1", "localhost", StringComparison.Ordinal),
            "us-east-1", drBucket, E2EEnvironment.S3AccessKey, E2EEnvironment.S3SecretKey, true, "dr-site", null))).ReadAsync<S3DestinationDto>();
        var drSame = await (await env.Admin.PostAsJsonAsync("/api/v1/storage/s3", new S3DestinationUpsertRequest("dr-same-" + suffix, env.S3Endpoint,
            "us-east-1", sameBucket, E2EEnvironment.S3AccessKey, E2EEnvironment.S3SecretKey, true, null, null))).ReadAsync<S3DestinationDto>();

        // Validation: the primary destination cannot also be a DR copy target.
        var templateId = await CreateTemplateAsync("tpl-dr-" + suffix, BackupScript);
        var name = "e2e-dr-" + suffix;
        var invalid = await env.Admin.PostAsJsonAsync("/api/v1/backup-jobs", Requests.Job(name, templateId, env.ServerId, destination: env.DestinationId, replicas: [env.DestinationId]));
        invalid.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var job = await (await env.Admin.PostAsJsonAsync("/api/v1/backup-jobs",
            Requests.Job(name, templateId, env.ServerId, destination: env.DestinationId, replicas: [drRemote.Id, drSame.Id]))).ReadAsync<BackupJobDto>();
        job.ReplicationTargets.Should().NotBeNull().And.HaveCount(2);

        var run = await WaitRunAsync(await RunNowAsync(job.Id), r => r.Summary.Status is RunStatusDto.Completed or RunStatusDto.Failed);
        run.Summary.Status.Should().Be(RunStatusDto.Completed, run.Log);
        var artifact = run.Artifacts.Should().ContainSingle().Subject;

        // Primary upload first; on verification it fans out to both DR destinations.
        var primaryId = run.Offloads.Should().ContainSingle().Subject.Id;
        var primary = await WaitOffloadAsync(primaryId, o => o.Summary.Status is OffloadStatusDto.Completed or OffloadStatusDto.Failed);
        primary.Summary.Status.Should().Be(OffloadStatusDto.Completed, primary.Log);
        primary.Summary.IsReplica.Should().BeFalse();

        run = await WaitRunAsync(run.Summary.Id, r => r.Offloads.Count(o => o.IsReplica) == 2 && r.Offloads.Where(o => o.IsReplica).All(o => o.Status is OffloadStatusDto.Completed or OffloadStatusDto.Failed));
        var copies = run.Offloads.Where(o => o.IsReplica).ToList();
        foreach (var c in copies)
        {
            var detail = await WaitOffloadAsync(c.Id, o => o.Summary.Status is OffloadStatusDto.Completed or OffloadStatusDto.Failed);
            detail.Summary.Status.Should().Be(OffloadStatusDto.Completed, detail.Log);
            detail.Summary.VerificationStatus.Should().Be(VerificationStatusDto.Verified);
            detail.Summary.SourceOffloadId.Should().Be(primaryId);
            detail.Summary.CleanupStatus.Should().Be(CleanupStatusDto.NotRequired);
            detail.Summary.BytesTotal.Should().Be(artifact.SizeBytes);
        }
        var remoteCopy = copies.Single(c => c.DestinationId == drRemote.Id);
        var sameCopy = copies.Single(c => c.DestinationId == drSame.Id);
        remoteCopy.RemotePrefix.Should().Be("dr-site/" + primary.Summary.RemotePrefix, "the DR bucket mirrors the primary layout under its own base prefix");
        sameCopy.RemotePrefix.Should().Be(primary.Summary.RemotePrefix);
        (await WaitOffloadAsync(sameCopy.Id, _ => true)).Log.Should().Contain("server-side copy");

        // The bytes in each DR bucket are identical to the artifact recorded at backup time.
        foreach (var (bucket, prefix) in new[] { (drBucket, remoteCopy.RemotePrefix), (sameBucket, sameCopy.RemotePrefix) })
        {
            using var obj = await s3.GetObjectAsync(bucket, prefix + artifact.RelativePath);
            using var ms = new MemoryStream();
            await obj.ResponseStream.CopyToAsync(ms);
            Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(ms.ToArray())).Should().Be(artifact.Sha256, $"copy in {bucket} must be byte-identical");
        }

        // A second copy of the same point to the same destination is refused (idempotency).
        var dup = await env.Admin.PostAsJsonAsync($"/api/v1/recovery-points/{primaryId}/replicate", new ReplicateRecoveryPointRequest(drRemote.Id));
        dup.StatusCode.Should().Be(HttpStatusCode.Conflict);

        // DR scenario: restore from the DR copy (not the primary) and compare with the original data.
        var restore = await RestoreAsync(remoteCopy.Id);
        restore.Summary.Status.Should().Be(RestoreStatusDto.Completed, restore.Log);
        restore.Summary.VerificationStatus.Should().Be(VerificationStatusDto.Verified);
        restore.Log.Should().Contain(drBucket);
        var restoredFingerprint = await env.ExecAsync(env.Worker,
            $"rm -rf /tmp/verify-dr && mkdir -p /tmp/verify-dr && tar -xzf '{restore.Summary.TargetPath}/{artifact.RelativePath}' -C /tmp/verify-dr && cd /tmp/verify-dr && find . -type f -exec sha256sum {{}} + | sort -k2");
        restoredFingerprint.Should().Be(await DataFingerprintOnTargetAsync(), "content restored from the DR site must be identical to the source data");

        // Manual copy of an existing point to a destination the job does not replicate to.
        var extraBucket = "backora-e2e-dr3";
        try { await s3.PutBucketAsync(extraBucket); } catch (AmazonS3Exception e) when (e.ErrorCode is "BucketAlreadyOwnedByYou" or "BucketAlreadyExists") { }
        var extra = await (await env.Admin.PostAsJsonAsync("/api/v1/storage/s3", new S3DestinationUpsertRequest("dr-extra-" + suffix, env.S3Endpoint,
            "us-east-1", extraBucket, E2EEnvironment.S3AccessKey, E2EEnvironment.S3SecretKey, true, null, null))).ReadAsync<S3DestinationDto>();
        var manual = await (await env.Admin.PostAsJsonAsync($"/api/v1/recovery-points/{primaryId}/replicate", new ReplicateRecoveryPointRequest(extra.Id))).ReadAsync<IdResponse>();
        var manualDone = await WaitOffloadAsync(manual.Id, o => o.Summary.Status is OffloadStatusDto.Completed or OffloadStatusDto.Failed);
        manualDone.Summary.Status.Should().Be(OffloadStatusDto.Completed, manualDone.Log);

        // A destination used as a DR target cannot be deleted while jobs still reference it.
        (await env.Admin.DeleteAsync($"/api/v1/storage/s3/{drRemote.Id}")).StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Multi_site_dr_tracks_each_site_independently_retries_one_site_and_skips_disabled_sites()
    {
        // Three DR sites on one job: one healthy, one whose bucket does not exist yet (fails), one disabled.
        using var s3 = new AmazonS3Client(new BasicAWSCredentials(E2EEnvironment.S3AccessKey, E2EEnvironment.S3SecretKey), new AmazonS3Config { ServiceURL = env.S3Endpoint, ForcePathStyle = true, AuthenticationRegion = "us-east-1" });
        var suffix = Guid.NewGuid().ToString("N")[..6];
        string okBucket = "backora-e2e-site-ok", lateBucket = "backora-e2e-late-" + suffix, offBucket = "backora-e2e-site-off";
        foreach (var b in new[] { okBucket, offBucket })
        {
            try { await s3.PutBucketAsync(b); } catch (AmazonS3Exception e) when (e.ErrorCode is "BucketAlreadyOwnedByYou" or "BucketAlreadyExists") { }
        }
        async Task<S3DestinationDto> Dest(string name, string bucket, bool enabled = true) =>
            await (await env.Admin.PostAsJsonAsync("/api/v1/storage/s3", new S3DestinationUpsertRequest(name + "-" + suffix, env.S3Endpoint, "us-east-1", bucket,
                E2EEnvironment.S3AccessKey, E2EEnvironment.S3SecretKey, true, null, null, IsEnabled: enabled))).ReadAsync<S3DestinationDto>();
        var siteOk = await Dest("site-ok", okBucket);
        var siteLate = await Dest("site-late", lateBucket);
        var siteOff = await Dest("site-off", offBucket, enabled: false);
        siteOff.IsEnabled.Should().BeFalse();
        siteOk.IsEnabled.Should().BeTrue();

        var templateId = await CreateTemplateAsync("tpl-msdr-" + suffix, BackupScript);
        var job = await (await env.Admin.PostAsJsonAsync("/api/v1/backup-jobs",
            Requests.Job("e2e-msdr-" + suffix, templateId, env.ServerId, destination: env.DestinationId, replicas: [siteOk.Id, siteLate.Id, siteOff.Id]))).ReadAsync<BackupJobDto>();
        job.ReplicationTargets.Should().HaveCount(3, "one job can copy to any number of DR sites");

        var run = await WaitRunAsync(await RunNowAsync(job.Id), r => r.Summary.Status is RunStatusDto.Completed or RunStatusDto.Failed);
        run.Summary.Status.Should().Be(RunStatusDto.Completed, run.Log);
        var primaryId = run.Offloads.Single(o => !o.IsReplica).Id;
        var primary = await WaitOffloadAsync(primaryId, o => o.Summary.Status is OffloadStatusDto.Completed or OffloadStatusDto.Failed);
        primary.Summary.Status.Should().Be(OffloadStatusDto.Completed, primary.Log);
        primary.Log.Should().Contain($"DR copy to {siteOff.Name} skipped: the destination is disabled");

        // Each enabled site gets its own transfer; the disabled site gets none.
        run = await WaitRunAsync(run.Summary.Id, r => r.Offloads.Count(o => o.IsReplica) == 2
            && r.Offloads.Where(o => o.IsReplica).All(o => o.Status is OffloadStatusDto.Completed or OffloadStatusDto.Failed), seconds: 300);
        var copies = run.Offloads.Where(o => o.IsReplica).ToList();
        copies.Should().NotContain(c => c.DestinationId == siteOff.Id);
        copies.Single(c => c.DestinationId == siteOk.Id).Status.Should().Be(OffloadStatusDto.Completed, "a failure at one site must not affect another");
        var failed = copies.Single(c => c.DestinationId == siteLate.Id);
        failed.Status.Should().Be(OffloadStatusDto.Failed, "the missing bucket must be reported as a failed copy, never as stored");
        failed.ErrorMessage.Should().NotBeNullOrWhiteSpace();

        // Fix the site, then retry only that copy: no new backup run, the same verified source point is reused.
        await s3.PutBucketAsync(lateBucket);
        var runsBefore = (await (await env.Admin.GetAsync($"/api/v1/runs?jobId={job.Id}")).ReadAsync<PagedResult<RunSummaryDto>>()).Total;
        var retry = await (await env.Admin.PostAsync($"/api/v1/offloads/{failed.Id}/retry", null)).ReadAsync<IdResponse>();
        var retried = await WaitOffloadAsync(retry.Id, o => o.Summary.Status is OffloadStatusDto.Completed or OffloadStatusDto.Failed);
        retried.Summary.Status.Should().Be(OffloadStatusDto.Completed, retried.Log);
        retried.Summary.SourceOffloadId.Should().Be(primaryId);
        retried.Summary.DestinationId.Should().Be(siteLate.Id);
        retried.Summary.VerificationStatus.Should().Be(VerificationStatusDto.Verified);
        (await (await env.Admin.GetAsync($"/api/v1/runs?jobId={job.Id}")).ReadAsync<PagedResult<RunSummaryDto>>()).Total.Should().Be(runsBefore, "retrying a DR copy must not re-run the database backup");

        // Disabled sites refuse new copies until they are enabled again.
        var refused = await env.Admin.PostAsJsonAsync($"/api/v1/recovery-points/{primaryId}/replicate", new ReplicateRecoveryPointRequest(siteOff.Id));
        refused.StatusCode.Should().Be(HttpStatusCode.Conflict);

        // Any healthy copy is a valid restore source: restore from the retried site.
        var restore = await RestoreAsync(retry.Id);
        restore.Summary.Status.Should().Be(RestoreStatusDto.Completed, restore.Log);
        restore.Summary.VerificationStatus.Should().Be(VerificationStatusDto.Verified);
    }

    [Fact]
    public async Task Failing_script_is_classified_and_notification_is_queued()
    {
        var job = await CreateJobAsync("#!/bin/bash\necho 'connecting to database'\necho 'ERROR: authentication failed for user backup' >&2\nexit 7\n");
        var run = await WaitRunAsync(await RunNowAsync(job.Id), r => r.Summary.Status is RunStatusDto.Completed or RunStatusDto.Failed);
        run.Summary.Status.Should().Be(RunStatusDto.Failed);
        run.Summary.FailureClass.Should().Be(FailureClassDto.ScriptError);
        run.ExitCode.Should().Be(7);
        run.Summary.ErrorMessage.Should().Contain("exited with code 7").And.Contain("authentication failed");
        run.Offloads.Should().BeEmpty();
        var queued = await env.Api.WithDbAsync(db => db.NotificationMessages.CountAsync(m => m.RelatedEntityId == run.Summary.Id && m.EventType == "backup.failed"));
        queued.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task Successful_exit_without_artifacts_is_not_a_successful_backup()
    {
        var job = await CreateJobAsync("#!/bin/bash\necho 'pretending to back up'\nexit 0\n");
        var run = await WaitRunAsync(await RunNowAsync(job.Id), r => r.Summary.Status is RunStatusDto.Completed or RunStatusDto.Failed);
        run.Summary.Status.Should().Be(RunStatusDto.Failed);
        run.Summary.FailureClass.Should().Be(FailureClassDto.NoArtifacts);
    }

    [Fact]
    public async Task Cancelling_a_running_backup_terminates_the_remote_process_tree()
    {
        var job = await CreateJobAsync("#!/bin/bash\necho started\nsleep 300 & wait\n");
        var runId = await RunNowAsync(job.Id);
        await WaitRunAsync(runId, r => r.Summary.Status == RunStatusDto.Running);
        (await env.ExecAsync(env.Target, "pgrep -x sleep || true")).Trim().Should().NotBeEmpty();

        var dup = await env.Admin.PostAsync($"/api/v1/backup-jobs/{job.Id}/runs", null);
        dup.StatusCode.Should().Be(HttpStatusCode.Conflict, "a second concurrent run of the same job is refused");

        (await env.Admin.PostAsync($"/api/v1/runs/{runId}/cancel", null)).EnsureSuccessStatusCode();
        var run = await WaitRunAsync(runId, r => r.Summary.Status is RunStatusDto.Cancelled or RunStatusDto.Failed);
        run.Summary.Status.Should().Be(RunStatusDto.Cancelled);
        (await env.ExecAsync(env.Target, "pgrep -x sleep || true")).Trim().Should().BeEmpty("the whole process group must be gone");
    }

    [Fact]
    public async Task Worker_restart_during_a_backup_adopts_the_running_process()
    {
        var job = await CreateJobAsync(BackupScript.Replace("tar -czf", "sleep 12; tar -czf", StringComparison.Ordinal), offload: false);
        var runId = await RunNowAsync(job.Id);
        var running = await WaitRunAsync(runId, r => r.Summary.Status == RunStatusDto.Running);
        var pid = running.RemotePid;

        await env.StopWorkerAsync(); // graceful shutdown: leases released, remote process keeps running
        try
        {
            (await env.ExecAsync(env.Target, $"kill -0 {pid} && echo alive")).Should().Contain("alive");
            var stillRunning = await (await env.Admin.GetAsync($"/api/v1/runs/{runId}")).ReadAsync<RunDetailDto>();
            stillRunning.Summary.Status.Should().Be(RunStatusDto.Running);
        }
        finally
        {
            await env.StartWorkerAsync();
        }

        var done = await WaitRunAsync(runId, r => r.Summary.Status is RunStatusDto.Completed or RunStatusDto.Failed, 90);
        done.Summary.Status.Should().Be(RunStatusDto.Completed, done.Log);
        done.RemotePid.Should().Be(pid, "the new worker adopted the existing process rather than starting a duplicate");
        done.Artifacts.Should().ContainSingle();
    }

    [Fact]
    public async Task Corrupted_object_in_storage_is_detected_on_restore()
    {
        var job = await CreateJobAsync(BackupScript);
        var run = await WaitRunAsync(await RunNowAsync(job.Id), r => r.Summary.Status is RunStatusDto.Completed or RunStatusDto.Failed);
        run.Summary.Status.Should().Be(RunStatusDto.Completed, run.Log);
        var offload = await WaitOffloadAsync(run.Offloads[0].Id, o => o.Summary.Status is OffloadStatusDto.Completed or OffloadStatusDto.Failed);
        offload.Summary.Status.Should().Be(OffloadStatusDto.Completed, offload.Log);

        // Silent corruption: same size, different bytes.
        var artifact = run.Artifacts[0];
        using (var s3 = new AmazonS3Client(new BasicAWSCredentials(E2EEnvironment.S3AccessKey, E2EEnvironment.S3SecretKey), new AmazonS3Config { ServiceURL = env.S3Endpoint, ForcePathStyle = true, AuthenticationRegion = "us-east-1" }))
        {
            var garbage = new byte[artifact.SizeBytes];
            Random.Shared.NextBytes(garbage);
            await s3.PutObjectAsync(new PutObjectRequest { BucketName = E2EEnvironment.Bucket, Key = offload.Summary.RemotePrefix + artifact.RelativePath, InputStream = new MemoryStream(garbage) });
        }

        var restore = await RestoreAsync(offload.Summary.Id);
        restore.Summary.Status.Should().Be(RestoreStatusDto.Failed);
        restore.Summary.FailureClass.Should().Be(FailureClassDto.VerificationFailed);
        restore.Summary.ErrorMessage.Should().Contain("SHA-256 mismatch");
    }

    [Fact]
    public async Task Changed_host_key_blocks_execution()
    {
        var job = await CreateJobAsync(BackupScript, offload: false);
        string? original = null;
        await env.Api.WithDbAsync(async db =>
        {
            var s = await db.Servers.FirstAsync(x => x.Id == env.ServerId);
            original = s.HostKeyFingerprint;
            s.HostKeyFingerprint = "SHA256:AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
            await db.SaveChangesAsync();
        });
        try
        {
            var run = await WaitRunAsync(await RunNowAsync(job.Id), r => r.Summary.Status is RunStatusDto.Completed or RunStatusDto.Failed);
            run.Summary.Status.Should().Be(RunStatusDto.Failed);
            run.Summary.FailureClass.Should().Be(FailureClassDto.HostKeyMismatch);
            run.Summary.ErrorMessage.Should().Contain("HOST KEY MISMATCH");
        }
        finally
        {
            await env.Api.WithDbAsync(async db =>
            {
                var s = await db.Servers.FirstAsync(x => x.Id == env.ServerId);
                s.HostKeyFingerprint = original;
                await db.SaveChangesAsync();
            });
        }
    }

    [Fact]
    public async Task Storage_outage_fails_attempt_then_recovers_on_retry()
    {
        var job = await CreateJobAsync(BackupScript);
        await env.S3.PauseAsync();
        long offloadId;
        try
        {
            var run = await WaitRunAsync(await RunNowAsync(job.Id), r => r.Summary.Status is RunStatusDto.Completed or RunStatusDto.Failed);
            run.Summary.Status.Should().Be(RunStatusDto.Completed, "the backup itself does not depend on object storage");
            offloadId = run.Offloads[0].Id;
            var failedAttempt = await WaitOffloadAsync(offloadId, o => o.Summary.Attempt >= 1 && o.Summary.Status == OffloadStatusDto.Pending && o.Log!.Contains("Retrying"), 180);
            failedAttempt.Summary.CleanupStatus.Should().Be(CleanupStatusDto.Pending, "nothing is deleted from NFS until the copy is verified");
        }
        finally
        {
            await env.S3.UnpauseAsync();
        }

        // Fast-forward the backoff instead of waiting 30 s.
        await env.Api.WithDbAsync(db => db.OffloadRuns.Where(o => o.Id == offloadId).ExecuteUpdateAsync(s => s.SetProperty(o => o.NextAttemptAt, DateTimeOffset.UtcNow)));
        var offload = await WaitOffloadAsync(offloadId, o => o.Summary.Status is OffloadStatusDto.Completed or OffloadStatusDto.Failed, 180);
        offload.Summary.Status.Should().Be(OffloadStatusDto.Completed, offload.Log);
        offload.Summary.Attempt.Should().BeGreaterThan(1);
    }

    [Fact]
    public async Task Retention_deletes_expired_points_and_keeps_the_newest_verified_point()
    {
        var job = await CreateJobAsync(BackupScript, retention: 0.0001m); // ≈ 8.6 seconds
        var offloadIds = new List<long>();
        for (var i = 0; i < 2; i++)
        {
            var run = await WaitRunAsync(await RunNowAsync(job.Id), r => r.Summary.Status is RunStatusDto.Completed or RunStatusDto.Failed);
            run.Summary.Status.Should().Be(RunStatusDto.Completed, run.Log);
            var o = await WaitOffloadAsync(run.Offloads[0].Id, x => x.Summary.Status is OffloadStatusDto.Completed or OffloadStatusDto.Failed);
            o.Summary.Status.Should().Be(OffloadStatusDto.Completed, o.Log);
            offloadIds.Add(o.Summary.Id);
        }
        await Task.Delay(TimeSpan.FromSeconds(10));

        await using (var scope = env.Api.Services.CreateAsyncScope())
        {
            (await scope.ServiceProvider.GetRequiredService<RetentionService>().TickAsync(CancellationToken.None)).Should().BeGreaterThanOrEqualTo(1);
        }

        var older = await (await env.Admin.GetAsync($"/api/v1/offloads/{offloadIds[0]}")).ReadAsync<OffloadDetailDto>();
        var newer = await (await env.Admin.GetAsync($"/api/v1/offloads/{offloadIds[1]}")).ReadAsync<OffloadDetailDto>();
        older.Summary.RetentionState.Should().Be(RetentionStateDto.Deleted);
        newer.Summary.RetentionState.Should().Be(RetentionStateDto.Expired, "the newest verified point is always kept");

        using var s3 = new AmazonS3Client(new BasicAWSCredentials(E2EEnvironment.S3AccessKey, E2EEnvironment.S3SecretKey), new AmazonS3Config { ServiceURL = env.S3Endpoint, ForcePathStyle = true, AuthenticationRegion = "us-east-1" });
        (await s3.ListObjectsV2Async(new ListObjectsV2Request { BucketName = E2EEnvironment.Bucket, Prefix = older.Summary.RemotePrefix })).S3Objects.Should().BeNullOrEmpty();
        (await s3.ListObjectsV2Async(new ListObjectsV2Request { BucketName = E2EEnvironment.Bucket, Prefix = newer.Summary.RemotePrefix })).S3Objects.Should().NotBeEmpty();

        var audit = await env.Api.WithDbAsync(db => db.AuditEvents.AnyAsync(a => a.Action == "recovery_point.deleted" && a.ResourceId == offloadIds[0].ToString()));
        audit.Should().BeTrue();
    }
}
