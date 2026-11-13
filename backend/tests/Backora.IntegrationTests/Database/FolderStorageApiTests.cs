using System.Net;
using System.Net.Http.Json;
using Backora.Contracts.V1;
using Backora.Domain;
using Backora.Domain.Backups;
using Backora.Domain.Infrastructure;
using Backora.IntegrationTests.Infrastructure;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace Backora.IntegrationTests.Database;

/// <summary>A folder on the execution host (a mounted NFS share) can be added as a storage site through the API.</summary>
[Collection(PostgresCollection.Name)]
public sealed class FolderStorageApiTests(PostgresContainer pg) : IAsyncLifetime
{
    private BackoraApiFactory _factory = null!;

    public async Task InitializeAsync()
    {
        _factory = new BackoraApiFactory(await pg.CreateDatabaseAsync("folders"));
        _ = _factory.Server;
    }

    public async Task DisposeAsync() => await _factory.DisposeAsync();

    private static S3DestinationUpsertRequest Folder(string name, string path) =>
        new(name, "", "", "", "", null, false, null, null, Kind: StorageKindDto.Filesystem, LocalPath: path);

    [Fact]
    public async Task A_folder_site_is_created_without_keys_and_unsafe_folders_are_refused()
    {
        await _factory.WithDbAsync(async db =>
        {
            var settings = await db.StorageSettings.FirstOrDefaultAsync();
            if (settings is null) { db.Add(new StorageSettings { NfsHost = "nfs.example:/share", NfsPath = "/BackoraDB" }); }
            else settings.NfsPath = "/BackoraDB";
            await db.SaveChangesAsync();
        });
        var admin = await _factory.AdminClientAsync();

        var created = await admin.PostAsJsonAsync("/api/v1/storage/s3", Folder("Branch DR " + Guid.NewGuid().ToString("N")[..6], "/mnt/dr-site/"));
        created.StatusCode.Should().Be(HttpStatusCode.Created, await created.Content.ReadAsStringAsync());
        var dto = (await created.Content.ReadFromJsonAsync<S3DestinationDto>())!;
        dto.Kind.Should().Be(StorageKindDto.Filesystem);
        dto.LocalPath.Should().Be("/mnt/dr-site");
        dto.AccessKeyIdMasked.Should().BeEmpty();

        (await admin.PostAsJsonAsync("/api/v1/storage/s3", Folder("On staging", "/BackoraDB/dr"))).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await admin.PostAsJsonAsync("/api/v1/storage/s3", Folder("System", "/etc/backups"))).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await admin.PostAsJsonAsync("/api/v1/storage/s3", Folder("Injection", "/mnt/x;rm -rf /"))).StatusCode.Should().Be(HttpStatusCode.BadRequest);

        await _factory.WithDbAsync(async db =>
        {
            var d = await db.S3Destinations.SingleAsync(x => x.Id == dto.Id);
            d.Kind.Should().Be(Backora.Domain.StorageKind.Filesystem);
            d.SecretKeyCiphertext.Should().BeEmpty("a folder site stores no credentials");
            d.Endpoint.Should().Be("file:///mnt/dr-site");
        });
    }

    [Fact]
    public async Task Every_job_copying_to_one_dr_site_can_be_moved_to_another()
    {
        long amazon = 0, branch = 0, primary = 0, jobA = 0, jobB = 0;
        await _factory.WithDbAsync(async db =>
        {
            var suffix = Guid.NewGuid().ToString("N")[..6];
            var server = new Server { Name = "srv-" + suffix, Host = "10.1.1.1", SshUser = "root" };
            var template = new ScriptTemplate { Name = "tpl-" + suffix, Category = "MariaDB", Script = "echo hi" };
            var ecs = new S3Destination { Name = "ECS " + suffix, Endpoint = "https://ecs.example", Region = "r", Bucket = "b1", AccessKeyId = "a", SecretKeyCiphertext = "x" };
            var aws = new S3Destination { Name = "Amazon DR " + suffix, Endpoint = "https://s3.example", Region = "r", Bucket = "b2", AccessKeyId = "a", SecretKeyCiphertext = "x" };
            var nfs = new S3Destination { Name = "Branch DR " + suffix, Kind = StorageKind.Filesystem, LocalPath = "/mnt/dr-site", Endpoint = "file:///mnt/dr-site", AccessKeyId = "", SecretKeyCiphertext = "" };
            db.AddRange(server, template, ecs, aws, nfs);
            await db.SaveChangesAsync();
            var a = new BackupJob { Name = "job-a-" + suffix, Category = "MariaDB", ScriptTemplateId = template.Id, ServerId = server.Id, Status = JobStatus.Active, S3DestinationId = ecs.Id };
            a.ReplicationTargets.Add(new BackupJobReplicationTarget { S3DestinationId = aws.Id });
            var b = new BackupJob { Name = "job-b-" + suffix, Category = "MariaDB", ScriptTemplateId = template.Id, ServerId = server.Id, Status = JobStatus.Active, S3DestinationId = ecs.Id };
            b.ReplicationTargets.Add(new BackupJobReplicationTarget { S3DestinationId = aws.Id });
            b.ReplicationTargets.Add(new BackupJobReplicationTarget { S3DestinationId = nfs.Id });   // already copies there too
            db.AddRange(a, b);
            await db.SaveChangesAsync();
            (amazon, branch, primary, jobA, jobB) = (aws.Id, nfs.Id, ecs.Id, a.Id, b.Id);
        });
        var admin = await _factory.AdminClientAsync();

        var r = await admin.PostAsJsonAsync($"/api/v1/storage/s3/{amazon}/move-dr-jobs", new MoveDrJobsRequest(branch));
        r.StatusCode.Should().Be(HttpStatusCode.OK, await r.Content.ReadAsStringAsync());
        (await r.Content.ReadFromJsonAsync<MoveDrJobsResponse>())!.Jobs.Should().Be(2);

        await _factory.WithDbAsync(async db =>
        {
            var targets = await db.BackupJobReplicationTargets.Where(t => t.BackupJobId == jobA || t.BackupJobId == jobB).ToListAsync();
            targets.Should().OnlyContain(t => t.S3DestinationId == branch, "nobody copies to the old site any more");
            targets.Should().HaveCount(2, "one target per job: no duplicate for the job that already copied there");
        });
        (await admin.PostAsJsonAsync($"/api/v1/storage/s3/{amazon}/move-dr-jobs", new MoveDrJobsRequest(amazon))).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        _ = primary;
    }
}
