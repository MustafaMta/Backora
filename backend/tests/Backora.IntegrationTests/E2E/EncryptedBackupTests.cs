using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Backora.Application.Encryption;
using Backora.Contracts.Security;
using Backora.Contracts.V1;
using Backora.IntegrationTests.Infrastructure;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Backora.IntegrationTests.E2E;

/// <summary>
/// Customer-key (age) client-side encryption, end to end over real SSH, NFS, rclone, age and S3:
/// ciphertext-only uploads, key validation before anything is created, in-memory key delivery through the
/// worker-host FIFO, byte-identical restores, tamper detection, worker restarts while waiting for the key,
/// key rotation, replicas, a large streamed file and an interrupted upload. Requires Docker.
/// </summary>
[Trait("Category", "E2E")]
public sealed class EncryptedBackupTests(E2EEnvironment env) : IClassFixture<E2EEnvironment>
{
    private const string Header = "age-encryption.org/v1\n";

    private static string Script(string marker) => $$"""
        #!/bin/bash
        set -euo pipefail
        OUT="{NFS_PATH}/{JOB_NAME}"
        mkdir -p "$OUT"
        tar -czf "$OUT/{JOB_NAME}-$(date +%Y%m%d%H%M%S)-$RANDOM.tar.gz" -C /srv/data .
        for i in $(seq 1 200); do echo "{{marker}} line $i"; done > "$OUT/marker-$RANDOM.txt"
        echo "Backup completed successfully"
        """;

    private const string LargeScript = """
        #!/bin/bash
        set -euo pipefail
        OUT="{NFS_PATH}/{JOB_NAME}"
        mkdir -p "$OUT"
        head -c 209715200 /dev/urandom > "$OUT/large-$RANDOM.bin"
        echo "Large backup completed"
        """;

    private AmazonS3Client S3() => new(new BasicAWSCredentials(E2EEnvironment.S3AccessKey, E2EEnvironment.S3SecretKey),
        new AmazonS3Config { ServiceURL = env.S3Endpoint, ForcePathStyle = true, AuthenticationRegion = "us-east-1" });

    // ───────────────────────────── helpers ─────────────────────────────

    private async Task<GeneratedKeyPairDto> GenerateKeyAsync()
    {
        var response = await env.Admin.PostAsync("/api/v1/storage/s3/encryption/generate", null);
        response.EnsureSuccessStatusCode();
        response.Headers.CacheControl!.NoStore.Should().BeTrue("the private key must never be cached");
        return (await response.ReadAsync<GeneratedKeyPairDto>());
    }

    private async Task<S3DestinationDto> EncryptedDestinationAsync(string recipient, string? bucket = null)
    {
        bucket ??= "enc-" + Guid.NewGuid().ToString("N")[..10];
        using var s3 = S3();
        await s3.PutBucketAsync(bucket);
        var dest = await (await env.Admin.PostAsJsonAsync("/api/v1/storage/s3", new S3DestinationUpsertRequest("enc-" + Guid.NewGuid().ToString("N")[..8], env.S3Endpoint,
            "us-east-1", bucket, E2EEnvironment.S3AccessKey, E2EEnvironment.S3SecretKey, true, null, null, EncryptionModeDto.CustomerKey, recipient))).ReadAsync<S3DestinationDto>();
        dest.EncryptionMode.Should().Be(EncryptionModeDto.CustomerKey);
        dest.EncryptionRecipient.Should().Be(recipient);
        dest.EncryptionKeyFingerprint.Should().Be(AgeKeys.RecipientFingerprint(recipient));
        dest.EncryptionKeyHistory.Should().ContainSingle(k => k.RetiredAt == null);
        return dest;
    }

    private async Task<BackupJobDto> JobAsync(long destinationId, string script, IReadOnlyList<long>? replicas = null)
    {
        var name = "enc-" + Guid.NewGuid().ToString("N")[..10];
        var t = await (await env.Admin.PostAsJsonAsync("/api/v1/templates", Requests.Template("tpl-" + name, script, "Files"))).ReadAsync<TemplateDto>();
        return await (await env.Admin.PostAsJsonAsync("/api/v1/backup-jobs", Requests.Job(name, t.Id, env.ServerId, destination: destinationId, timeout: 30, replicas: replicas))).ReadAsync<BackupJobDto>();
    }

    private async Task<RunDetailDto> RunAsync(long jobId)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/backup-jobs/{jobId}/runs");
        req.Headers.Add(ApiHeaders.IdempotencyKey, Guid.NewGuid().ToString());
        var created = await (await env.Admin.SendAsync(req)).ReadAsync<RunCreatedResponse>();
        var run = await WaitAsync($"/api/v1/runs/{created.RunId}", (RunDetailDto r) => r.Summary.Status is RunStatusDto.Completed or RunStatusDto.Failed);
        run.Summary.Status.Should().Be(RunStatusDto.Completed, run.Log);
        return run;
    }

    private async Task<T> WaitAsync<T>(string url, Func<T, bool> until, int seconds = 180)
    {
        T last = default!;
        for (var i = 0; i < seconds * 2; i++)
        {
            last = await (await env.Admin.GetAsync(url)).ReadAsync<T>();
            if (until(last)) return last;
            await Task.Delay(500);
        }
        throw new TimeoutException($"{url} did not reach the expected state: {JsonSerializer.Serialize(last)[..Math.Min(3000, JsonSerializer.Serialize(last).Length)]}");
    }

    private Task<OffloadDetailDto> WaitOffloadAsync(long id, int seconds = 180) =>
        WaitAsync($"/api/v1/offloads/{id}", (OffloadDetailDto o) => o.Summary.Status is OffloadStatusDto.Completed or OffloadStatusDto.Failed, seconds);

    private async Task<OffloadDetailDto> BackupAsync(long jobId)
    {
        var run = await RunAsync(jobId);
        var offload = await WaitOffloadAsync(run.Offloads.Single(o => !o.IsReplica).Id);
        offload.Summary.Status.Should().Be(OffloadStatusDto.Completed, offload.Log);
        return offload;
    }

    private async Task<HttpResponseMessage> PostRestoreAsync(long pointId, string? key)
    {
        var pre = await (await env.Admin.PostAsJsonAsync("/api/v1/restores/precheck", new RestorePrecheckRequest(pointId))).ReadAsync<RestorePrecheckResponse>();
        using var req = new HttpRequestMessage(HttpMethod.Post, "/api/v1/restores")
        {
            Content = JsonContent.Create(new CreateRestoreRequest(pointId, "Encrypted restore drill", pre.ConfirmationPhrase, key)),
        };
        req.Headers.Add(ApiHeaders.IdempotencyKey, Guid.NewGuid().ToString());
        return await env.Admin.SendAsync(req);
    }

    private Task<RestoreDetailDto> WaitRestoreAsync(long id, int seconds = 240) =>
        WaitAsync($"/api/v1/restores/{id}", (RestoreDetailDto r) => r.Summary.Status is RestoreStatusDto.Completed or RestoreStatusDto.Failed or RestoreStatusDto.Cancelled, seconds);

    private async Task<RestoreDetailDto> RestoreAsync(long pointId, string key)
    {
        var response = await PostRestoreAsync(pointId, key);
        var created = await response.ReadAsync<RestoreSummaryDto>();
        created.RequiresDecryptionKey.Should().BeTrue();
        return await WaitRestoreAsync(created.Id);
    }

    private static async Task<JsonElement> ProblemAsync(HttpResponseMessage r) => JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement;

    private async Task<byte[]> DownloadAsync(string bucket, string key)
    {
        using var s3 = S3();
        using var obj = await s3.GetObjectAsync(bucket, key);
        using var ms = new MemoryStream();
        await obj.ResponseStream.CopyToAsync(ms);
        return ms.ToArray();
    }

    /// <summary>
    /// Test-harness only: puts an identity into a private 0600 file (/root, removed before the leak scan) of the worker container so the real
    /// age binary can be exercised directly, without the key appearing on a command line. Removed right after use.
    /// </summary>
    private Task PutIdentityAsync(string name, string identity) =>
        env.Worker.CopyAsync(Encoding.ASCII.GetBytes(identity + "\n"), $"/root/e2e-id-{name}", fileMode: DotNet.Testcontainers.Configurations.UnixFileModes.UserRead | DotNet.Testcontainers.Configurations.UnixFileModes.UserWrite);

    private static string Sha(byte[] b) => Convert.ToHexStringLower(SHA256.HashData(b));

    /// <summary>
    /// Case 15: the private keys must not appear in a pg_dump of the database (all job/restore/audit rows),
    /// in any captured API/worker log line, in S3 object metadata, or in files left on the worker host.
    /// </summary>
    private async Task AssertKeysNeverPersistedAsync(IEnumerable<string> keys, IEnumerable<string> buckets)
    {
        var needles = keys.SelectMany(k => new[] { k, k.ToLowerInvariant(), k["AGE-SECRET-KEY-1".Length..] }).ToList();

        var dump = await env.Postgres.ExecAsync(["pg_dump", "-U", "postgres", "-d", "postgres", "--data-only"]);
        dump.ExitCode.Should().Be(0, dump.Stderr);
        dump.Stdout.Should().Contain("restore_runs").And.Contain("age-v1", "the dump covers the encrypted recovery points");
        foreach (var n in needles) dump.Stdout.Should().NotContain(n, "no private key may be stored in PostgreSQL");

        var logs = env.Logs.All;
        logs.Should().NotBeEmpty();
        foreach (var n in needles) logs.Should().NotContain(n, "no private key may be logged");

        using var s3 = S3();
        foreach (var bucket in buckets)
        {
            var objects = await s3.ListObjectsV2Async(new ListObjectsV2Request { BucketName = bucket });
            foreach (var o in objects.S3Objects ?? [])
            {
                var meta = await s3.GetObjectMetadataAsync(bucket, o.Key);
                var all = string.Join('\n', meta.Metadata.Keys.Select(k => $"{k}={meta.Metadata[k]}")) + string.Join('\n', meta.ResponseMetadata.Metadata.Select(p => $"{p.Key}={p.Value}"));
                foreach (var n in needles) all.Should().NotContain(n);
            }
        }

        foreach (var n in needles.Where(n => !n.StartsWith("AGE-", StringComparison.OrdinalIgnoreCase) && !n.StartsWith("age-", StringComparison.Ordinal)))
        {
            var hits = await env.ExecAsync(env.Worker, $"grep -rIl -- '{n}' /var/tmp/backora /BackoraDB /tmp /root 2>/dev/null; true");
            hits.Trim().Should().BeEmpty("no private key may be written to the worker host's disk");
        }
    }

    // ───────────────────────────── tests ─────────────────────────────

    [Fact]
    public async Task Encrypted_backup_uploads_only_ciphertext_and_restores_byte_identical_content_with_the_right_key_only()
    {
        // Key generated by Backora; it must interoperate with the real age binary (both directions).
        var keyA = await GenerateKeyAsync();
        var keyB = AgeKeys.Generate();
        (await (await env.Admin.PostAsJsonAsync("/api/v1/storage/s3/encryption/inspect", new InspectRecipientRequest(keyA.Recipient))).ReadAsync<RecipientInspectionDto>())
            .Should().BeEquivalentTo(new RecipientInspectionDto(true, keyA.Fingerprint, null, "age-x25519"));
        // The identity reaches this test-only age process via sh -c (test harness), never via Backora.
        await PutIdentityAsync("a", keyA.PrivateKey);
        var interop = await env.Worker.ExecAsync(["sh", "-c",
            $"printf 'interop-check' | age -r {keyA.Recipient} -o /tmp/interop.age && age -d -i /root/e2e-id-a /tmp/interop.age; rc=$?; rm -f /tmp/interop.age; exit $rc"]);
        interop.ExitCode.Should().Be(0, interop.Stderr);
        interop.Stdout.Should().Be("interop-check", "a key generated by Backora decrypts a file encrypted by the real age binary");

        // (1)(2)(3) backup → encrypted with the configured recipient → uploaded
        var dest = await EncryptedDestinationAsync(keyA.Recipient);
        var marker = "BACKORA-PLAINTEXT-MARKER-" + Guid.NewGuid().ToString("N");
        var job = await JobAsync(dest.Id, Script(marker));
        var run = await RunAsync(job.Id);
        run.Artifacts.Should().HaveCount(2);
        var offload = await WaitOffloadAsync(run.Offloads.Single().Id);
        offload.Summary.Status.Should().Be(OffloadStatusDto.Completed, offload.Log);
        offload.Summary.VerificationStatus.Should().Be(VerificationStatusDto.Verified);
        offload.Summary.Encrypted.Should().BeTrue();
        offload.Summary.EncryptionFormat.Should().Be("age-v1");
        offload.Summary.EncryptionKeyFingerprint.Should().Be(keyA.Fingerprint);
        offload.Summary.CleanupStatus.Should().Be(CleanupStatusDto.Completed);
        offload.Log.Should().Contain("only ciphertext").And.Contain("age v1 ciphertext").And.Contain("Removed the staged ciphertext");
        (await env.ExecAsync(env.Worker, $"ls /BackoraDB/{job.Name}/")).Should().NotContain(".tar.gz", "the verified plaintext is removed from NFS");
        (await env.ExecAsync(env.Worker, $"ls -d /var/tmp/backora/backora-offload-{offload.Summary.Id}-* 2>&1 || true")).Should().Contain("No such file", "the stage directory is removed after verification");

        // Server-side filter for the UI.
        var encryptedPoints = await (await env.Admin.GetAsync($"/api/v1/recovery-points?encrypted=true&jobId={job.Id}")).ReadAsync<PagedResult<OffloadSummaryDto>>();
        encryptedPoints.Items.Should().ContainSingle(p => p.Id == offload.Summary.Id);
        var plainPoints = await (await env.Admin.GetAsync($"/api/v1/recovery-points?encrypted=false&pageSize=200")).ReadAsync<PagedResult<OffloadSummaryDto>>();
        plainPoints.Items.Should().NotContain(p => p.Encrypted);

        // (4)(5) raw S3 objects are age ciphertext and contain none of the plaintext
        using (var s3 = S3())
        {
            var listed = await s3.ListObjectsV2Async(new ListObjectsV2Request { BucketName = dest.Bucket, Prefix = offload.Summary.RemotePrefix });
            listed.S3Objects.Select(o => o.Key[offload.Summary.RemotePrefix.Length..]).Should().BeEquivalentTo(run.Artifacts.Select(a => a.RelativePath + ".age"));
        }
        var markerArtifact = run.Artifacts.Single(a => a.RelativePath.StartsWith("marker-", StringComparison.Ordinal));
        var tarArtifact = run.Artifacts.Single(a => a.RelativePath.EndsWith(".tar.gz", StringComparison.Ordinal));
        var markerObject = await DownloadAsync(dest.Bucket, offload.Summary.RemotePrefix + markerArtifact.RelativePath + ".age");
        Encoding.ASCII.GetString(markerObject, 0, Header.Length).Should().Be(Header);
        Encoding.Latin1.GetString(markerObject).Should().NotContain(marker).And.NotContain("BACKORA-PLAINTEXT-MARKER");
        markerObject.Length.Should().BeGreaterThan((int)markerArtifact.SizeBytes, "age adds a header and authentication tags");
        var tarObject = await DownloadAsync(dest.Bucket, offload.Summary.RemotePrefix + tarArtifact.RelativePath + ".age");
        Encoding.ASCII.GetString(tarObject, 0, Header.Length).Should().Be(Header);
        Sha(tarObject).Should().NotBe(tarArtifact.Sha256);

        // Precheck explains that a key is needed.
        var pre = await (await env.Admin.PostAsJsonAsync("/api/v1/restores/precheck", new RestorePrecheckRequest(offload.Summary.Id))).ReadAsync<RestorePrecheckResponse>();
        pre.CanRestore.Should().BeTrue(string.Join("; ", pre.Checks.Select(c => $"{c.Name}:{c.State}:{c.Message}")));
        pre.Checks.Should().Contain(c => c.Name == "Decryption key required" && c.State == CheckState.Warning && c.Message.Contains(keyA.Fingerprint));
        pre.Checks.Should().Contain(c => c.Name == "Decryption tool" && c.State == CheckState.Passed);
        pre.Checks.Should().Contain(c => c.Name == "Object storage availability" && c.State == CheckState.Passed);

        // (6)(7) restore without a key is rejected before anything is created
        var restoresBefore = await env.Api.WithDbAsync(db => db.RestoreRuns.CountAsync(r => r.OffloadRunId == offload.Summary.Id));
        var noKey = await PostRestoreAsync(offload.Summary.Id, null);
        noKey.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        var noKeyProblem = await ProblemAsync(noKey);
        noKeyProblem.GetProperty("code").GetString().Should().Be("decryption_key_required");
        noKeyProblem.GetProperty("detail").GetString().Should().Contain("A decryption key is required");

        // (8)(9) wrong key rejected (API) — and age itself refuses it on the worker host
        var wrong = await PostRestoreAsync(offload.Summary.Id, keyB.Identity);
        wrong.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        var wrongProblem = await ProblemAsync(wrong);
        wrongProblem.GetProperty("code").GetString().Should().Be("decryption_key_mismatch");
        (await wrong.Content.ReadAsStringAsync()).Should().NotContain(keyB.Identity).And.NotContain(keyB.Identity[16..]);
        (await env.Api.WithDbAsync(db => db.RestoreRuns.CountAsync(r => r.OffloadRunId == offload.Summary.Id))).Should().Be(restoresBefore, "nothing is created for a rejected key");
        var verifyWrong = await (await env.Admin.PostAsJsonAsync($"/api/v1/recovery-points/{offload.Summary.Id}/verify-key", new VerifyDecryptionKeyRequest(keyB.Identity))).ReadAsync<KeyVerificationDto>();
        verifyWrong.Matches.Should().BeFalse();
        verifyWrong.Fingerprint.Should().Be(keyB.Fingerprint);
        var verifyRight = await (await env.Admin.PostAsJsonAsync($"/api/v1/recovery-points/{offload.Summary.Id}/verify-key", new VerifyDecryptionKeyRequest(keyA.PrivateKey))).ReadAsync<KeyVerificationDto>();
        verifyRight.Matches.Should().BeTrue();
        (await env.Admin.PostAsJsonAsync($"/api/v1/recovery-points/{offload.Summary.Id}/verify-key", new VerifyDecryptionKeyRequest("not-a-key"))).StatusCode.Should().Be(HttpStatusCode.BadRequest);

        await env.Worker.CopyAsync(markerObject, "/tmp/rp-marker.age");
        await PutIdentityAsync("b", keyB.Identity);
        var ageWrong = await env.Worker.ExecAsync(["sh", "-c", "age -d -i /root/e2e-id-b -o /dev/null /tmp/rp-marker.age"]);
        ageWrong.ExitCode.Should().NotBe(0);
        ageWrong.Stderr.Should().Contain("no identity matched");
        var ageRight = await env.Worker.ExecAsync(["sh", "-c", "age -d -i /root/e2e-id-a /tmp/rp-marker.age | sha256sum | cut -d' ' -f1; rm -f /tmp/rp-marker.age /root/e2e-id-a /root/e2e-id-b"]);
        ageRight.Stdout.Trim().Should().Be(markerArtifact.Sha256, "the real age binary decrypts the uploaded object to the original bytes");

        // Unauthorised users cannot use the key endpoints.
        var viewer = await env.Api.ClientForRoleAsync(env.Admin, Roles.Viewer);
        (await viewer.PostAsJsonAsync($"/api/v1/recovery-points/{offload.Summary.Id}/verify-key", new VerifyDecryptionKeyRequest(keyA.PrivateKey))).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await viewer.PostAsJsonAsync("/api/v1/restores/1/decryption-key", new SubmitDecryptionKeyRequest(keyA.PrivateKey))).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var backupOperator = await env.Api.ClientForRoleAsync(env.Admin, Roles.BackupOperator);
        (await backupOperator.PostAsync("/api/v1/storage/s3/encryption/generate", null)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await backupOperator.PostAsJsonAsync("/api/v1/restores", new CreateRestoreRequest(offload.Summary.Id, "not allowed", pre.ConfirmationPhrase, keyA.PrivateKey))).StatusCode.Should().Be(HttpStatusCode.Forbidden);

        // (10)(11)(12) correct key → restore → byte-for-byte identical
        var restore = await RestoreAsync(offload.Summary.Id, keyA.PrivateKey);
        restore.Summary.Status.Should().Be(RestoreStatusDto.Completed, restore.Log);
        restore.Summary.VerificationStatus.Should().Be(VerificationStatusDto.Verified);
        restore.Summary.FilesVerified.Should().Be(2);
        restore.Summary.DecryptionKeyFingerprint.Should().Be(keyA.Fingerprint);
        restore.Log.Should().Contain("Decryption key received").And.Contain("match the SHA-256 manifest");
        restore.Timeline.Should().Contain(t => t.Title == "Waiting for decryption key");
        var restoredHashes = await env.ExecAsync(env.Worker, $"cd '{restore.Summary.TargetPath}' && sha256sum * | sort -k2");
        foreach (var a in run.Artifacts) restoredHashes.Should().Contain($"{a.Sha256}  {a.RelativePath}");
        (await env.ExecAsync(env.Worker, $"ls -A '{restore.Summary.TargetPath}'")).Should().NotContain(".partial");
        var restoredData = await env.ExecAsync(env.Worker,
            $"rm -rf /tmp/verify-enc && mkdir -p /tmp/verify-enc && tar -xzf '{restore.Summary.TargetPath}/{tarArtifact.RelativePath}' -C /tmp/verify-enc && cd /tmp/verify-enc && find . -type f -exec sha256sum {{}} + | sort -k2");
        restoredData.Should().Be(await env.ExecAsync(env.Target, "cd /srv/data && find . -type f -exec sha256sum {} + | sort -k2"));
        (await env.ExecAsync(env.Worker, $"ls -d /var/tmp/backora/backora-restore-{restore.Summary.Id}-* 2>&1 || true")).Should().Contain("No such file", "the FIFO/script dir is removed");

        // (13) corrupted ciphertext (same size, one flipped byte) → authentication failure, no plaintext left
        var tampered = (byte[])markerObject.Clone();
        tampered[^40] ^= 0x01;
        using (var s3 = S3())
        {
            await s3.PutObjectAsync(new PutObjectRequest { BucketName = dest.Bucket, Key = offload.Summary.RemotePrefix + markerArtifact.RelativePath + ".age", InputStream = new MemoryStream(tampered) });
        }
        var corrupted = await RestoreAsync(offload.Summary.Id, keyA.PrivateKey);
        corrupted.Summary.Status.Should().Be(RestoreStatusDto.Failed, corrupted.Log);
        corrupted.Summary.FailureClass.Should().Be(FailureClassDto.VerificationFailed);
        corrupted.Summary.ErrorMessage.Should().Contain("Decryption failed").And.Contain("no unencrypted copy");
        var leftovers = await env.ExecAsync(env.Worker, $"ls -A '{corrupted.Summary.TargetPath}' 2>/dev/null; true");
        leftovers.Trim().Should().BeEmpty("partial and already-restored files are removed when authentication fails");
        (await env.ExecAsync(env.Worker, $"ls -d /var/tmp/backora/backora-restore-{corrupted.Summary.Id}-* 2>&1 || true")).Should().Contain("No such file");

        // (15) the private keys were never persisted anywhere
        await AssertKeysNeverPersistedAsync([keyA.PrivateKey, keyB.Identity], [dest.Bucket]);
        var auditJson = (await env.Api.WithDbAsync(db => db.AuditEvents.Where(a => a.Action.Contains("encryption") || a.Action.Contains("decryption") || a.Action.StartsWith("restore.")).Select(a => new { a.Action, a.DetailsJson }).ToListAsync()))
            .Select(a => a.Action + " " + a.DetailsJson).ToList();
        auditJson.Should().Contain(a => a.StartsWith("storage.s3.encryption_key_pair_generated", StringComparison.Ordinal) && a.Contains(keyA.Fingerprint));
        auditJson.Should().Contain(a => a.StartsWith("restore.decryption_key_rejected", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Worker_restart_while_awaiting_the_key_and_lost_in_memory_key_are_recoverable_and_nothing_is_persisted()
    {
        var key = AgeKeys.Generate();
        var dest = await EncryptedDestinationAsync(key.Recipient);
        var job = await JobAsync(dest.Id, Script("restart-" + Guid.NewGuid().ToString("N")));
        var point = await BackupAsync(job.Id);
        var courier = env.Api.Services.GetRequiredService<DecryptionKeyCourier>();

        // Create the restore while no worker runs, then drop the API's in-memory copy (as if the API restarted).
        await env.StopWorkerAsync();
        long restoreId;
        try
        {
            var created = await (await PostRestoreAsync(point.Summary.Id, key.Identity)).ReadAsync<RestoreSummaryDto>();
            restoreId = created.Id;
            created.Status.Should().Be(RestoreStatusDto.Pending);
            created.RequiresDecryptionKey.Should().BeTrue();
            courier.IsPending(restoreId).Should().BeTrue();
            courier.Discard(restoreId);
            courier.IsPending(restoreId).Should().BeFalse();
        }
        finally
        {
            await env.StartWorkerAsync();
        }

        var waiting = await WaitAsync($"/api/v1/restores/{restoreId}", (RestoreDetailDto r) => r.Summary.Status == RestoreStatusDto.AwaitingKey);
        waiting.Summary.KeyRequestExpiresAt.Should().BeAfter(DateTimeOffset.UtcNow.AddMinutes(20));
        var dir = await env.Api.WithDbAsync(db => db.RestoreRuns.Where(r => r.Id == restoreId).Select(r => r.RemoteDirectory).SingleAsync());
        (await env.ExecAsync(env.Worker, $"test -p '{dir}/key.fifo' && stat -c '%a' '{dir}/key.fifo'")).Trim().Should().Be("600");
        var pid = (await env.ExecAsync(env.Worker, $"cat '{dir}/pid'")).Trim();

        // (14) restart the worker while the restore waits for the key
        await env.StopWorkerAsync();
        try
        {
            (await env.ExecAsync(env.Worker, $"kill -0 {pid} && echo alive")).Should().Contain("alive", "the detached script keeps waiting on the FIFO");
            var row = await env.Api.WithDbAsync(db => db.RestoreRuns.AsNoTracking().SingleAsync(r => r.Id == restoreId));
            row.Status.Should().Be(Backora.Domain.RestoreStatus.AwaitingKey);
            JsonSerializer.Serialize(row).Should().NotContain(key.Identity).And.NotContain(key.Identity[16..]);
        }
        finally
        {
            await env.StartWorkerAsync();
        }
        (await WaitAsync($"/api/v1/restores/{restoreId}", (RestoreDetailDto r) => r.Summary.Status == RestoreStatusDto.AwaitingKey)).Summary.Status.Should().Be(RestoreStatusDto.AwaitingKey);

        // Re-submission: wrong key → 422, right key → 202, then the restore completes.
        var other = AgeKeys.Generate();
        var bad = await env.Admin.PostAsJsonAsync($"/api/v1/restores/{restoreId}/decryption-key", new SubmitDecryptionKeyRequest(other.Identity));
        bad.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        var accepted = await env.Admin.PostAsJsonAsync($"/api/v1/restores/{restoreId}/decryption-key", new SubmitDecryptionKeyRequest(key.Identity));
        accepted.StatusCode.Should().Be(HttpStatusCode.Accepted);
        var done = await WaitRestoreAsync(restoreId);
        done.Summary.Status.Should().Be(RestoreStatusDto.Completed, done.Log);
        done.Summary.VerificationStatus.Should().Be(VerificationStatusDto.Verified);
        done.Summary.KeyRequestExpiresAt.Should().BeNull();
        courier.IsPending(restoreId).Should().BeFalse("the key is zeroed and dropped once delivered");
        (await env.Admin.PostAsJsonAsync($"/api/v1/restores/{restoreId}/decryption-key", new SubmitDecryptionKeyRequest(key.Identity))).StatusCode.Should().Be(HttpStatusCode.Conflict);

        // Cancel while waiting: the script is killed and the FIFO/work dir removed.
        var cancelId = await AwaitingRestoreWithoutKeyAsync(point.Summary.Id, key.Identity, courier);
        var cancelDir = await env.Api.WithDbAsync(db => db.RestoreRuns.Where(r => r.Id == cancelId).Select(r => r.RemoteDirectory).SingleAsync());
        (await env.Admin.PostAsync($"/api/v1/restores/{cancelId}/cancel", null)).EnsureSuccessStatusCode();
        (await WaitRestoreAsync(cancelId)).Summary.Status.Should().Be(RestoreStatusDto.Cancelled);
        (await env.ExecAsync(env.Worker, $"ls -d '{cancelDir}' 2>&1 || true")).Should().Contain("No such file");

        // Expiry: a key that is never supplied fails the restore cleanly.
        var expireId = await AwaitingRestoreWithoutKeyAsync(point.Summary.Id, key.Identity, courier);
        var expireDir = await env.Api.WithDbAsync(db => db.RestoreRuns.Where(r => r.Id == expireId).Select(r => r.RemoteDirectory).SingleAsync());
        await env.Api.WithDbAsync(db => db.RestoreRuns.Where(r => r.Id == expireId).ExecuteUpdateAsync(s => s.SetProperty(r => r.KeyRequestExpiresAt, DateTimeOffset.UtcNow.AddSeconds(-1))));
        var expired = await WaitRestoreAsync(expireId);
        expired.Summary.Status.Should().Be(RestoreStatusDto.Failed);
        expired.Summary.ErrorMessage.Should().Contain("decryption key was not supplied in time");
        (await env.ExecAsync(env.Worker, $"ls -d '{expireDir}' 2>&1 || true")).Should().Contain("No such file");
        (await env.ExecAsync(env.Worker, "pgrep -f 'bash [.]/restore[.]sh' || true")).Trim().Should().BeEmpty();

        await AssertKeysNeverPersistedAsync([key.Identity, other.Identity], [dest.Bucket]);
    }

    /// <summary>Creates a restore whose in-memory key is discarded before the worker can use it, and waits for AwaitingKey.</summary>
    private async Task<long> AwaitingRestoreWithoutKeyAsync(long pointId, string key, DecryptionKeyCourier courier)
    {
        await env.StopWorkerAsync();
        long id;
        try
        {
            id = (await (await PostRestoreAsync(pointId, key)).ReadAsync<RestoreSummaryDto>()).Id;
            courier.Discard(id);
        }
        finally
        {
            await env.StartWorkerAsync();
        }
        await WaitAsync($"/api/v1/restores/{id}", (RestoreDetailDto r) => r.Summary.Status == RestoreStatusDto.AwaitingKey);
        return id;
    }

    [Fact]
    public async Task Key_rotation_keeps_old_points_on_the_old_key_and_replicas_follow_the_encryption_rules()
    {
        var keyA = AgeKeys.Generate();
        var keyB = AgeKeys.Generate();
        var dest = await EncryptedDestinationAsync(keyA.Recipient);
        using var s3 = S3();
        var plainBucket = "plain-" + Guid.NewGuid().ToString("N")[..10];
        await s3.PutBucketAsync(plainBucket);
        var plainDr = await (await env.Admin.PostAsJsonAsync("/api/v1/storage/s3", new S3DestinationUpsertRequest("plain-dr-" + Guid.NewGuid().ToString("N")[..6], env.S3Endpoint,
            "us-east-1", plainBucket, E2EEnvironment.S3AccessKey, E2EEnvironment.S3SecretKey, true, null, null))).ReadAsync<S3DestinationDto>();

        var job = await JobAsync(dest.Id, Script("rotation-" + Guid.NewGuid().ToString("N")), replicas: [plainDr.Id]);
        var run1 = await RunAsync(job.Id);
        var point1 = await WaitOffloadAsync(run1.Offloads.Single(o => !o.IsReplica).Id);
        point1.Summary.EncryptionKeyFingerprint.Should().Be(keyA.Fingerprint);

        // Encrypted point → unencrypted DR destination: the ciphertext is copied as-is and keeps key A.
        var copy1 = await WaitAsync($"/api/v1/runs/{run1.Summary.Id}", (RunDetailDto r) => r.Offloads.Any(o => o.IsReplica && o.Status is OffloadStatusDto.Completed or OffloadStatusDto.Failed));
        var replica1 = await WaitOffloadAsync(copy1.Offloads.Single(o => o.IsReplica).Id);
        replica1.Summary.Status.Should().Be(OffloadStatusDto.Completed, replica1.Log);
        replica1.Summary.Encrypted.Should().BeTrue();
        replica1.Summary.EncryptionKeyFingerprint.Should().Be(keyA.Fingerprint);
        replica1.Log.Should().Contain("copying them unchanged");
        foreach (var a in run1.Artifacts)
        {
            var src = await DownloadAsync(dest.Bucket, point1.Summary.RemotePrefix + a.RelativePath + ".age");
            var dst = await DownloadAsync(plainBucket, replica1.Summary.RemotePrefix + a.RelativePath + ".age");
            Sha(dst).Should().Be(Sha(src), "the DR copy is the identical ciphertext");
        }

        // (16) rotate A → B
        var current = await (await env.Admin.GetAsync($"/api/v1/storage/s3/{dest.Id}")).ReadAsync<S3DestinationDto>();
        var rotated = await (await env.Admin.PutAsJsonAsync($"/api/v1/storage/s3/{dest.Id}", new S3DestinationUpsertRequest(current.Name, current.Endpoint, current.Region, current.Bucket,
            "", null, true, null, current.Version, EncryptionModeDto.CustomerKey, keyB.Recipient))).ReadAsync<S3DestinationDto>();
        rotated.EncryptionKeyFingerprint.Should().Be(keyB.Fingerprint);
        rotated.EncryptionKeyHistory.Should().HaveCount(2);
        rotated.EncryptionKeyHistory!.Single(k => k.Fingerprint == keyA.Fingerprint).Should().Match<EncryptionKeyDto>(k => k.RetiredAt != null && k.RecoveryPointCount == 1);
        rotated.EncryptionKeyHistory!.Single(k => k.Fingerprint == keyB.Fingerprint).RetiredAt.Should().BeNull();
        (await env.Api.WithDbAsync(db => db.AuditEvents.AnyAsync(a => a.Action == "storage.s3.encryption_rotated" && a.ResourceId == dest.Id.ToString()))).Should().BeTrue();

        var run2 = await RunAsync(job.Id);
        var point2 = await WaitOffloadAsync(run2.Offloads.Single(o => !o.IsReplica).Id);
        point2.Summary.EncryptionKeyFingerprint.Should().Be(keyB.Fingerprint, "new points use the new key");
        (await (await env.Admin.GetAsync($"/api/v1/offloads/{point1.Summary.Id}")).ReadAsync<OffloadDetailDto>()).Summary.EncryptionKeyFingerprint.Should().Be(keyA.Fingerprint, "old points keep their key; nothing is re-encrypted");

        // Old point: key B rejected, key A works. New point: key A rejected, key B works.
        (await (await env.Admin.PostAsJsonAsync($"/api/v1/recovery-points/{point1.Summary.Id}/verify-key", new VerifyDecryptionKeyRequest(keyB.Identity))).ReadAsync<KeyVerificationDto>()).Matches.Should().BeFalse();
        (await PostRestoreAsync(point1.Summary.Id, keyB.Identity)).StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await PostRestoreAsync(point2.Summary.Id, keyA.Identity)).StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        var r1 = await RestoreAsync(point1.Summary.Id, keyA.Identity);
        r1.Summary.Status.Should().Be(RestoreStatusDto.Completed, r1.Log);
        var r2 = await RestoreAsync(point2.Summary.Id, keyB.Identity);
        r2.Summary.Status.Should().Be(RestoreStatusDto.Completed, r2.Log);

        // Restoring the DR copy (ciphertext copied as-is) also needs key A.
        var r3 = await RestoreAsync(replica1.Summary.Id, keyA.Identity);
        r3.Summary.Status.Should().Be(RestoreStatusDto.Completed, r3.Log);

        // Encrypted point (key B) → destination encrypting to key A: refused, re-encryption needs the private key.
        var destA = await EncryptedDestinationAsync(keyA.Recipient);
        var refused = await (await env.Admin.PostAsJsonAsync($"/api/v1/recovery-points/{point2.Summary.Id}/replicate", new ReplicateRecoveryPointRequest(destA.Id))).ReadAsync<IdResponse>();
        var refusedDone = await WaitOffloadAsync(refused.Id);
        refusedDone.Summary.Status.Should().Be(OffloadStatusDto.Failed);
        refusedDone.Summary.FailureClass.Should().Be(FailureClassDto.Configuration);
        refusedDone.Summary.ErrorMessage.Should().Contain("different key");

        // Plaintext point → encrypted destination: streamed through age on the worker host, restorable with the key.
        var plainJob = await JobAsync(env.DestinationId, Script("plain-" + Guid.NewGuid().ToString("N")));
        var plainPoint = await BackupAsync(plainJob.Id);
        plainPoint.Summary.Encrypted.Should().BeFalse();
        var streamed = await (await env.Admin.PostAsJsonAsync($"/api/v1/recovery-points/{plainPoint.Summary.Id}/replicate", new ReplicateRecoveryPointRequest(destA.Id))).ReadAsync<IdResponse>();
        var streamedDone = await WaitOffloadAsync(streamed.Id);
        streamedDone.Summary.Status.Should().Be(OffloadStatusDto.Completed, streamedDone.Log);
        streamedDone.Summary.Encrypted.Should().BeTrue();
        streamedDone.Summary.EncryptionKeyFingerprint.Should().Be(keyA.Fingerprint);
        var streamedObjects = await s3.ListObjectsV2Async(new ListObjectsV2Request { BucketName = destA.Bucket, Prefix = streamedDone.Summary.RemotePrefix });
        streamedObjects.S3Objects.Should().OnlyContain(o => o.Key.EndsWith(".age", StringComparison.Ordinal));
        var r4 = await RestoreAsync(streamedDone.Summary.Id, keyA.Identity);
        r4.Summary.Status.Should().Be(RestoreStatusDto.Completed, r4.Log);
        r4.Summary.VerificationStatus.Should().Be(VerificationStatusDto.Verified);

        // Disabling encryption needs explicit confirmation (and is audited).
        current = await (await env.Admin.GetAsync($"/api/v1/storage/s3/{dest.Id}")).ReadAsync<S3DestinationDto>();
        var unconfirmed = await env.Admin.PutAsJsonAsync($"/api/v1/storage/s3/{dest.Id}", new S3DestinationUpsertRequest(current.Name, current.Endpoint, current.Region, current.Bucket,
            "", null, true, null, current.Version, EncryptionModeDto.None));
        unconfirmed.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await ProblemAsync(unconfirmed)).GetProperty("code").GetString().Should().Be("encryption_disable_unconfirmed");
        var disabled = await (await env.Admin.PutAsJsonAsync($"/api/v1/storage/s3/{dest.Id}", new S3DestinationUpsertRequest(current.Name, current.Endpoint, current.Region, current.Bucket,
            "", null, true, null, current.Version, EncryptionModeDto.None, null, ConfirmDisableEncryption: true))).ReadAsync<S3DestinationDto>();
        disabled.EncryptionMode.Should().Be(EncryptionModeDto.None);
        disabled.EncryptionKeyHistory.Should().HaveCount(2).And.OnlyContain(k => k.RetiredAt != null);
        (await env.Api.WithDbAsync(db => db.AuditEvents.AnyAsync(a => a.Action == "storage.s3.encryption_disabled" && a.ResourceId == dest.Id.ToString()))).Should().BeTrue();

        await AssertKeysNeverPersistedAsync([keyA.Identity, keyB.Identity], [dest.Bucket, plainBucket, destA.Bucket]);
    }

    [Fact]
    public async Task Large_file_streams_through_age_and_an_interrupted_upload_recovers_on_retry_before_it_is_completed()
    {
        var key = AgeKeys.Generate();
        var dest = await EncryptedDestinationAsync(key.Recipient);
        var job = await JobAsync(dest.Id, LargeScript);

        // Hold object storage so the upload hangs mid-transfer, then kill rclone on the worker host.
        long offloadId;
        await env.S3.PauseAsync();
        try
        {
            var run = await RunAsync(job.Id);
            run.Artifacts.Should().ContainSingle().Which.SizeBytes.Should().Be(209_715_200);
            offloadId = run.Offloads.Single().Id;
            await WaitAsync($"/api/v1/offloads/{offloadId}", (OffloadDetailDto o) => o.Log?.Contains("Uploading staged ciphertext") == true, 240);
            await Task.Delay(1500);
            (await env.ExecAsync(env.Worker, "pkill -f 'rclone copy [.]/stage' && echo killed")).Should().Contain("killed");
        }
        finally
        {
            await env.S3.UnpauseAsync();
        }

        var failedAttempt = await WaitAsync($"/api/v1/offloads/{offloadId}", (OffloadDetailDto o) => o.Summary.Status == OffloadStatusDto.Pending && o.Log!.Contains("Retrying"), 120);
        failedAttempt.Summary.VerificationStatus.Should().NotBe(VerificationStatusDto.Verified);
        failedAttempt.Summary.CleanupStatus.Should().Be(CleanupStatusDto.Pending, "the plaintext stays on NFS until a verified upload exists");
        (await env.ExecAsync(env.Worker, $"ls /BackoraDB/{job.Name}/")).Should().Contain("large-");
        (await env.ExecAsync(env.Worker, $"ls -d /var/tmp/backora/backora-offload-{offloadId}-a1 2>&1 || true")).Should().Contain("No such file", "a failed attempt's stage is removed; retries use a fresh dir");
        (await env.Api.WithDbAsync(db => db.OffloadRuns.AnyAsync(o => o.Id == offloadId && o.Status == Backora.Domain.OffloadStatus.Completed))).Should().BeFalse();

        await env.Api.WithDbAsync(db => db.OffloadRuns.Where(o => o.Id == offloadId).ExecuteUpdateAsync(s => s.SetProperty(o => o.NextAttemptAt, DateTimeOffset.UtcNow)));
        var offload = await WaitOffloadAsync(offloadId, 300);
        offload.Summary.Status.Should().Be(OffloadStatusDto.Completed, offload.Log);
        offload.Summary.Attempt.Should().Be(2);
        offload.Summary.VerificationStatus.Should().Be(VerificationStatusDto.Verified);
        offload.Log!.IndexOf("Verified 1 object", StringComparison.Ordinal).Should().BeLessThan(offload.Log.IndexOf("Removed 1 verified file", StringComparison.Ordinal), "NFS cleanup only after verification");

        var artifact = offload.Artifacts.Single();
        var cipher = await env.Api.WithDbAsync(db => db.OffloadRuns.AsNoTracking().SingleAsync(o => o.Id == offloadId));
        var recorded = cipher.EncryptedObjects.Single();
        recorded.CiphertextSize.Should().BeGreaterThan(artifact.SizeBytes);
        var head = await S3().GetObjectMetadataAsync(dest.Bucket, offload.Summary.RemotePrefix + artifact.RelativePath + ".age");
        head.ContentLength.Should().Be(recorded.CiphertextSize);

        var restore = await RestoreAsync(offloadId, key.Identity);
        restore.Summary.Status.Should().Be(RestoreStatusDto.Completed, restore.Log);
        restore.Summary.VerificationStatus.Should().Be(VerificationStatusDto.Verified, "the 200 MiB file decrypted to the exact bytes captured at backup time");
        (await env.ExecAsync(env.Worker, $"stat -c %s '{restore.Summary.TargetPath}/{artifact.RelativePath}'")).Trim().Should().Be("209715200");
        await env.ExecAsync(env.Worker, $"rm -rf '{restore.Summary.TargetPath}'");
    }
}
