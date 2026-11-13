using System.Diagnostics;
using System.Net.Http.Json;
using Amazon.Runtime;
using Amazon.S3;
using Backora.Contracts.V1;
using Backora.IntegrationTests.Infrastructure;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using DotNet.Testcontainers.Images;
using DotNet.Testcontainers.Networks;
using DotNet.Testcontainers.Volumes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Testcontainers.PostgreSql;

namespace Backora.IntegrationTests.E2E;

/// <summary>
/// Fully isolated, disposable environment for real backup/restore cycles:
///   • PostgreSQL 18 (Backora database)
///   • "worker" SSH host (jump host with the NFS share mounted at /BackoraDB and rclone installed)
///   • "target" SSH host (sample data under /srv/data, same share mounted) — reachable ONLY through the worker
///   • VersityGW S3-compatible object storage
///   • the real Backora API and Backora worker running in-process
/// Nothing touches production systems; every container and volume is removed afterwards.
/// </summary>
public sealed class E2EEnvironment : IAsyncLifetime
{
    public const string S3AccessKey = "e2eaccess";
    public const string S3SecretKey = "e2e-secret-key-not-production";
    public const string Bucket = "backora-e2e";

    private readonly string _id = Guid.NewGuid().ToString("N")[..8];
    private string _keyDir = string.Empty;
    private INetwork _network = null!;
    private IVolume _nfs = null!;
    private PostgreSqlContainer _postgres = null!;
    public PostgreSqlContainer Postgres => _postgres;

    /// <summary>Every log line written by the in-process API and worker (Debug and above), for secret-leak scans.</summary>
    public CapturingLoggerProvider Logs { get; } = new();
    public IContainer S3 { get; private set; } = null!;
    public IContainer Worker { get; private set; } = null!;
    public IContainer Target { get; private set; } = null!;
    public BackoraApiFactory Api { get; private set; } = null!;
    public WebApplication? WorkerHost { get; private set; }
    public HttpClient Admin { get; private set; } = null!;
    public string PrivateKey { get; private set; } = string.Empty;
    public int S3Port { get; private set; }
    public string S3Endpoint => $"http://127.0.0.1:{S3Port}";
    public long ServerId { get; private set; }
    public long DestinationId { get; private set; }

    public async Task InitializeAsync()
    {
        _keyDir = Directory.CreateTempSubdirectory("backora-e2e-").FullName;
        var keyFile = Path.Combine(_keyDir, "id_ed25519");
        Run("ssh-keygen", $"-t ed25519 -N \"\" -q -C backora-e2e -f \"{keyFile}\"");
        PrivateKey = await File.ReadAllTextAsync(keyFile);
        var publicKey = (await File.ReadAllTextAsync(keyFile + ".pub")).Trim();

        _network = new NetworkBuilder().WithName($"backora-e2e-{_id}").Build();
        _nfs = new VolumeBuilder().WithName($"backora-e2e-nfs-{_id}").Build();
        await _network.CreateAsync();
        await _nfs.CreateAsync();

        var image = new ImageFromDockerfileBuilder()
            .WithDockerfileDirectory(CommonDirectoryPath.GetProjectDirectory(), "E2E/docker")
            .WithName($"backora-e2e-sshhost:{_id}")
            .WithCleanUp(true)
            .Build();
        _postgres = new PostgreSqlBuilder("postgres:18-alpine").WithPassword("e2e_" + _id).Build();

        await Task.WhenAll(image.CreateAsync(), _postgres.StartAsync());

        S3 = new ContainerBuilder("versity/versitygw@sha256:89cad13cc981986ee284cbafea4613f9533ce6fd5f37a2c96c4c4fb8adb3a316")
            .WithNetwork(_network).WithNetworkAliases("s3")
            .WithEnvironment("ROOT_ACCESS_KEY", S3AccessKey).WithEnvironment("ROOT_SECRET_KEY", S3SecretKey)
            .WithCommand("posix", "/tmp")
            .WithPortBinding(7070, true)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilInternalTcpPortIsAvailable(7070))
            .Build();
        await S3.StartAsync();
        S3Port = S3.GetMappedPublicPort(7070);

        Target = new ContainerBuilder(image)
            .WithNetwork(_network).WithNetworkAliases("target")
            .WithEnvironment("AUTHORIZED_KEY", publicKey).WithEnvironment("SEED_DATA", "1")
            .WithVolumeMount(_nfs, "/BackoraDB")
            .WithWaitStrategy(Wait.ForUnixContainer().UntilInternalTcpPortIsAvailable(22))
            .Build();
        Worker = new ContainerBuilder(image)
            .WithNetwork(_network).WithNetworkAliases("worker")
            .WithEnvironment("AUTHORIZED_KEY", publicKey).WithEnvironment("ALLOW_TCP_FORWARDING", "yes")
            .WithEnvironment("S3_FORWARD_PORT", S3Port.ToString()).WithEnvironment("S3_FORWARD_TARGET", "s3:7070")
            .WithVolumeMount(_nfs, "/BackoraDB")
            .WithPortBinding(22, true)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilInternalTcpPortIsAvailable(22))
            .Build();
        await Task.WhenAll(Target.StartAsync(), Worker.StartAsync());

        using (var s3 = new AmazonS3Client(new BasicAWSCredentials(S3AccessKey, S3SecretKey), new AmazonS3Config { ServiceURL = S3Endpoint, ForcePathStyle = true, AuthenticationRegion = "us-east-1" }))
        {
            await s3.PutBucketAsync(Bucket);
        }

        Api = new BackoraApiFactory(new Npgsql.NpgsqlConnectionStringBuilder(_postgres.GetConnectionString()) { IncludeErrorDetail = true }.ConnectionString,
            configure: b => b.ConfigureLogging(l => l.AddProvider(Logs).AddFilter<CapturingLoggerProvider>(null, LogLevel.Debug)));
        _ = Api.Server;
        await StartWorkerAsync();
        Admin = await Api.AdminClientAsync();
        await ConfigurePlatformAsync();
    }

    public async Task StartWorkerAsync()
    {
        var settings = new Dictionary<string, string?>(Api.Settings)
        {
            ["urls"] = "http://127.0.0.1:0",
            ["Database:MigrateOnStartup"] = "false",
            ["Worker:PollIntervalSeconds"] = "1",
            ["Worker:ClaimIntervalSeconds"] = "1",
            ["Worker:LeaseSeconds"] = "20",
            ["Worker:SchedulerIntervalSeconds"] = "5",
            ["Worker:NotificationIntervalSeconds"] = "2",
            ["Worker:EnableRetention"] = "false",
            ["Worker:ShutdownTimeoutSeconds"] = "20",
            ["Worker:TransferStallSeconds"] = "20",
            ["Logging:LogLevel:Default"] = "Warning",
        };
        WorkerHost = Backora.Api.Execution.BackgroundExecutionHost.Build([], b =>
        {
            b.Configuration.AddInMemoryCollection(settings);
            b.Logging.AddProvider(Logs).AddFilter<CapturingLoggerProvider>(null, LogLevel.Debug);
        });
        await WorkerHost.StartAsync();
    }

    public async Task StopWorkerAsync()
    {
        if (WorkerHost is null) return;
        await WorkerHost.StopAsync();
        await WorkerHost.DisposeAsync();
        WorkerHost = null;
    }

    private async Task ConfigurePlatformAsync()
    {
        var workerPort = Worker.GetMappedPublicPort(22);
        (await Admin.PutAsJsonAsync("/api/v1/settings/nfs", new NfsSettingsUpdateRequest("nfs.e2e.test:/backora", "/BackoraDB", null))).EnsureSuccessStatusCode();
        (await Admin.PutAsJsonAsync("/api/v1/settings/ssh", new SshSettingsUpdateRequest(PrivateKey, null))).EnsureSuccessStatusCode();
        (await Admin.PutAsJsonAsync("/api/v1/settings/worker", new WorkerNodeUpdateRequest("127.0.0.1", workerPort, "root", PrivateKey, "rclone", "/var/tmp/backora", null))).EnsureSuccessStatusCode();

        // Untrusted first: the test must fail closed, then trust the presented fingerprint explicitly.
        var probe = await (await Admin.PostAsync("/api/v1/settings/worker/test", null)).ReadAsync<ConnectionTestResultDto>();
        if (probe.WorkerFingerprintTrusted) throw new InvalidOperationException("Worker key must not be trusted before explicit approval.");
        (await Admin.PostAsJsonAsync("/api/v1/settings/worker/trust-host-key", new TrustHostKeyRequest(probe.WorkerFingerprint!))).EnsureSuccessStatusCode();
        var verified = await (await Admin.PostAsync("/api/v1/settings/worker/test", null)).ReadAsync<ConnectionTestResultDto>();
        if (!verified.Success) throw new InvalidOperationException("Worker test failed: " + verified.Message);

        var server = await (await Admin.PostAsJsonAsync("/api/v1/servers", Requests.Server("e2e-target", "target"))).ReadAsync<ServerDto>();
        ServerId = server.Id;
        var test = await (await Admin.PostAsync($"/api/v1/servers/{ServerId}/test", null)).ReadAsync<ConnectionTestResultDto>();
        if (test.TargetFingerprint is null) throw new InvalidOperationException("Target probe failed: " + test.Message);
        (await Admin.PostAsJsonAsync($"/api/v1/servers/{ServerId}/trust-host-key", new TrustHostKeyRequest(test.TargetFingerprint))).EnsureSuccessStatusCode();
        var test2 = await (await Admin.PostAsync($"/api/v1/servers/{ServerId}/test", null)).ReadAsync<ConnectionTestResultDto>();
        if (!test2.Success) throw new InvalidOperationException("Target test failed after trust: " + test2.Message);

        var dest = await (await Admin.PostAsJsonAsync("/api/v1/storage/s3", new S3DestinationUpsertRequest("e2e-s3", S3Endpoint, "us-east-1", Bucket, S3AccessKey, S3SecretKey, true, null, null)))
            .ReadAsync<S3DestinationDto>();
        DestinationId = dest.Id;
    }

    public async Task<string> ExecAsync(IContainer container, string command)
    {
        var r = await container.ExecAsync(["sh", "-c", command]);
        return r.Stdout + r.Stderr;
    }

    public async Task DisposeAsync()
    {
        await StopWorkerAsync();
        if (Api is not null) await Api.DisposeAsync();
        foreach (var c in new[] { Worker, Target, S3 }) if (c is not null) await c.DisposeAsync();
        if (_postgres is not null) await _postgres.DisposeAsync();
        if (_nfs is not null) await _nfs.DeleteAsync();
        if (_network is not null) await _network.DeleteAsync();
        if (Directory.Exists(_keyDir)) Directory.Delete(_keyDir, true);
    }

    private static void Run(string file, string args)
    {
        using var p = Process.Start(new ProcessStartInfo(file, args) { RedirectStandardError = true, RedirectStandardOutput = true })!;
        p.WaitForExit();
        if (p.ExitCode != 0) throw new InvalidOperationException($"{file} failed: {p.StandardError.ReadToEnd()}");
    }
}

/// <summary>Collects formatted log messages (and exceptions) from every logger, thread-safe.</summary>
public sealed class CapturingLoggerProvider : ILoggerProvider
{
    private readonly System.Collections.Concurrent.ConcurrentQueue<string> _lines = new();

    public IReadOnlyCollection<string> Lines => _lines;

    public string All => string.Join('\n', _lines);

    public ILogger CreateLogger(string categoryName) => new Logger(categoryName, _lines);

    public void Dispose() { }

    private sealed class Logger(string category, System.Collections.Concurrent.ConcurrentQueue<string> lines) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var structured = state is IEnumerable<KeyValuePair<string, object?>> kv ? string.Join(';', kv.Select(p => $"{p.Key}={p.Value}")) : string.Empty;
            lines.Enqueue($"{logLevel} {category}: {formatter(state, exception)} {structured} {exception}");
        }
    }
}
