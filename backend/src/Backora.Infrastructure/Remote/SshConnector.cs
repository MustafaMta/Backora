using System.Diagnostics;
using System.Net.Sockets;
using System.Text;
using Backora.Application.Abstractions;
using Backora.Domain;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace Backora.Infrastructure.Remote;

public sealed class SshOptions
{
    public const string Section = "Ssh";
    public int ConnectTimeoutSeconds { get; set; } = 20;
    public int KeepAliveSeconds { get; set; } = 30;
}

/// <summary>
/// SSH.NET based connector. Targets are reached through a direct-tcpip channel on the worker (equivalent to
/// <c>ssh -J</c>), so the target's private key never touches the worker's disk and BOTH host keys are
/// verified against fingerprints an administrator explicitly trusted. Host-key verification cannot be
/// disabled.
/// </summary>
public sealed class SshConnector(IOptions<SshOptions> options, ILogger<SshConnector> logger) : ISshConnector
{
    private readonly SshOptions _options = options.Value;

    public async Task<IRemoteSession> ConnectWorkerAsync(SshEndpoint worker, CancellationToken ct)
    {
        var client = CreateClient(worker.Host, worker.Port, worker, enforceTrust: true, observed: null);
        await ConnectAsync(client, worker.Host, ct);
        return new SshRemoteSession(client, null, null, worker, worker.Host, worker.Port);
    }

    public async Task<IRemoteSession> ConnectTargetAsync(SshEndpoint worker, SshEndpoint target, CancellationToken ct)
    {
        var workerClient = CreateClient(worker.Host, worker.Port, worker, enforceTrust: true, observed: null);
        await ConnectAsync(workerClient, worker.Host, ct);
        ForwardedPortLocal? tunnel = null;
        SshClient? targetClient = null;
        try
        {
            tunnel = new ForwardedPortLocal("127.0.0.1", 0, target.Host, (uint)target.Port);
            workerClient.AddForwardedPort(tunnel);
            tunnel.Start();
            targetClient = CreateClient("127.0.0.1", (int)tunnel.BoundPort, target, enforceTrust: true, observed: null, displayHost: target.Host);
            await ConnectAsync(targetClient, target.Host, ct);
            return new SshRemoteSession(targetClient, workerClient, tunnel, target, "127.0.0.1", (int)tunnel.BoundPort);
        }
        catch
        {
            targetClient?.Dispose();
            try { tunnel?.Stop(); } catch (Exception) { /* best effort */ }
            workerClient.Dispose();
            throw;
        }
    }

    public async Task<(HostKeyObservation Worker, HostKeyObservation? Target, string? Error, string Stage)> ProbeAsync(
        SshEndpoint worker, SshEndpoint? target, CancellationToken ct)
    {
        var workerObs = new ObservedKey();
        HostKeyObservation? targetResult = null;
        using var workerClient = CreateClient(worker.Host, worker.Port, worker, enforceTrust: false, observed: workerObs);
        try
        {
            await ConnectAsync(workerClient, worker.Host, ct);
        }
        catch (Exception ex)
        {
            return (workerObs.ToObservation(worker), null, Describe(ex, "worker"), "Worker");
        }

        var workerResult = workerObs.ToObservation(worker);
        if (!workerResult.Trusted)
        {
            return (workerResult, null, $"Worker host key {workerObs.Fingerprint} is not trusted. Trust it before testing targets.", "Worker");
        }

        var check = workerClient.RunCommand("echo backora-ok");
        if (check.ExitStatus != 0 || !check.Result.Contains("backora-ok", StringComparison.Ordinal))
        {
            return (workerResult, null, "Connected to worker but command execution failed.", "Worker");
        }

        if (target is null) return (workerResult, null, null, "Worker");

        var targetObs = new ObservedKey();
        ForwardedPortLocal? tunnel = null;
        try
        {
            tunnel = new ForwardedPortLocal("127.0.0.1", 0, target.Host, (uint)target.Port);
            workerClient.AddForwardedPort(tunnel);
            tunnel.Start();
            using var targetClient = CreateClient("127.0.0.1", (int)tunnel.BoundPort, target, enforceTrust: false, observed: targetObs, displayHost: target.Host);
            try
            {
                await ConnectAsync(targetClient, target.Host, ct);
            }
            catch (Exception ex)
            {
                return (workerResult, targetObs.Fingerprint is null ? null : targetObs.ToObservation(target), Describe(ex, "target"), "Target");
            }

            targetResult = targetObs.ToObservation(target);
            var echo = targetClient.RunCommand("echo backora-ok");
            if (echo.ExitStatus != 0 || !echo.Result.Contains("backora-ok", StringComparison.Ordinal))
            {
                return (workerResult, targetResult, "Authenticated to target but command execution failed.", "Target");
            }

            return (workerResult, targetResult, targetResult.Trusted ? null : "Target host key is not trusted yet.", "Target");
        }
        finally
        {
            try { tunnel?.Stop(); } catch (Exception) { /* best effort */ }
        }
    }

    private SshClient CreateClient(string connectHost, int port, SshEndpoint endpoint, bool enforceTrust, ObservedKey? observed, string? displayHost = null)
    {
        PrivateKeyFile keyFile;
        try
        {
            using var ms = new MemoryStream(Encoding.UTF8.GetBytes(endpoint.PrivateKeyPem.Replace("\r\n", "\n", StringComparison.Ordinal).Trim() + "\n"));
            keyFile = new PrivateKeyFile(ms);
        }
        catch (Exception ex) when (ex is SshException or ArgumentException or InvalidOperationException or NotSupportedException)
        {
            throw new RemoteOperationException(FailureClass.Configuration, $"The private key for {endpoint.User}@{displayHost ?? endpoint.Host} could not be parsed: {ex.Message}");
        }

        var connectionInfo = new ConnectionInfo(connectHost, port, endpoint.User, new PrivateKeyAuthenticationMethod(endpoint.User, keyFile))
        {
            Timeout = TimeSpan.FromSeconds(_options.ConnectTimeoutSeconds),
        };
        var client = new SshClient(connectionInfo)
        {
            KeepAliveInterval = TimeSpan.FromSeconds(_options.KeepAliveSeconds),
        };
        var host = displayHost ?? endpoint.Host;
        client.HostKeyReceived += (_, e) =>
        {
            var presented = "SHA256:" + e.FingerPrintSHA256;
            if (observed is not null)
            {
                observed.Fingerprint = presented;
                observed.Expected = endpoint.TrustedFingerprint;
            }

            var trusted = endpoint.TrustedFingerprint is not null &&
                          string.Equals(endpoint.TrustedFingerprint, presented, StringComparison.Ordinal);
            if (enforceTrust && !trusted)
            {
                logger.LogWarning("Rejected SSH host key for {Host}: presented {Presented}, expected {Expected}", host, presented, endpoint.TrustedFingerprint ?? "(none trusted)");
                e.CanTrust = false;
                throw new HostKeyNotTrustedException(host, presented, endpoint.TrustedFingerprint);
            }

            // Probe mode: allow the handshake so we can report the fingerprint, but a mismatch with an
            // already-trusted key still aborts (never authenticate to an impostor).
            e.CanTrust = endpoint.TrustedFingerprint is null || trusted;
        };
        return client;
    }

    private static async Task ConnectAsync(SshClient client, string host, CancellationToken ct)
    {
        try
        {
            await client.ConnectAsync(ct);
        }
        catch (HostKeyNotTrustedException) { throw; }
        catch (SshConnectionException ex) when (ex.InnerException is HostKeyNotTrustedException hk) { throw hk; }
        catch (SshAuthenticationException ex)
        {
            throw new RemoteOperationException(FailureClass.Authentication, $"SSH authentication to {host} failed: {ex.Message}", ex);
        }
        catch (SshConnectionException ex) when (ex.Message.Contains("Key exchange negotiation failed", StringComparison.OrdinalIgnoreCase)
                                               || ex.Message.Contains("host key", StringComparison.OrdinalIgnoreCase))
        {
            throw new RemoteOperationException(FailureClass.HostKeyMismatch, $"SSH host key verification failed for {host}.", ex);
        }
        catch (Exception ex) when (ex is SocketException or SshConnectionException or SshOperationTimeoutException or TimeoutException or ProxyException)
        {
            throw new RemoteOperationException(FailureClass.Connectivity, $"Could not connect to {host}: {ex.Message}", ex);
        }
    }

    private static string Describe(Exception ex, string stage) => ex switch
    {
        HostKeyNotTrustedException hk => hk.Message,
        RemoteOperationException roe => roe.Message,
        _ => $"Unexpected error while connecting to {stage}: {ex.Message}",
    };

    private sealed class ObservedKey
    {
        public string? Fingerprint { get; set; }
        public string? Expected { get; set; }

        public HostKeyObservation ToObservation(SshEndpoint ep) =>
            new(ep.Host, ep.Port, Fingerprint ?? "(not received)", Fingerprint is not null && Expected is not null && Fingerprint == Expected);
    }
}

internal sealed class SshRemoteSession(
    SshClient client,
    SshClient? jumpClient,
    ForwardedPortLocal? tunnel,
    SshEndpoint endpoint,
    string connectHost,
    int connectPort) : IRemoteSession
{
    private SftpClient? _sftp;

    public async Task<CommandResult> RunAsync(string command, TimeSpan timeout, CancellationToken ct)
    {
        using var cmd = client.CreateCommand(command);
        cmd.CommandTimeout = timeout;
        var sw = Stopwatch.StartNew();
        // ExecuteAsync honours only its token, not CommandTimeout: without this deadline a channel that never closes (a
        // dropped connection, a stalled NFS read on the other side) would block the operation forever while its lease
        // heartbeat keeps another worker from taking it over.
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);
        try
        {
            await cmd.ExecuteAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new RemoteOperationException(FailureClass.Timeout, $"Remote command timed out after {sw.Elapsed.TotalSeconds:F0}s.");
        }
        catch (SshOperationTimeoutException ex)
        {
            throw new RemoteOperationException(FailureClass.Timeout, $"Remote command timed out after {sw.Elapsed.TotalSeconds:F0}s.", ex);
        }
        catch (SshConnectionException ex)
        {
            throw new RemoteOperationException(FailureClass.Connectivity, $"SSH connection lost: {ex.Message}", ex);
        }
        return new CommandResult(cmd.ExitStatus ?? -1, cmd.Result ?? string.Empty, cmd.Error ?? string.Empty);
    }

    public async Task<CommandResult> RunWithInputAsync(string command, ReadOnlyMemory<byte> stdin, TimeSpan timeout, CancellationToken ct)
    {
        using var cmd = client.CreateCommand(command);
        cmd.CommandTimeout = timeout;
        var sw = Stopwatch.StartNew();
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);
        try
        {
            var execution = cmd.ExecuteAsync(timeoutCts.Token);
            // The input stream is the channel's stdin; disposing it sends EOF so the remote reader finishes.
            await using (var input = cmd.CreateInputStream())
            {
                await input.WriteAsync(stdin, timeoutCts.Token);
                await input.FlushAsync(timeoutCts.Token);
            }
            await execution;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new RemoteOperationException(FailureClass.Timeout, $"Remote command timed out after {sw.Elapsed.TotalSeconds:F0}s.");
        }
        catch (SshOperationTimeoutException ex)
        {
            throw new RemoteOperationException(FailureClass.Timeout, $"Remote command timed out after {sw.Elapsed.TotalSeconds:F0}s.", ex);
        }
        catch (SshConnectionException ex)
        {
            throw new RemoteOperationException(FailureClass.Connectivity, $"SSH connection lost: {ex.Message}", ex);
        }
        return new CommandResult(cmd.ExitStatus ?? -1, cmd.Result ?? string.Empty, cmd.Error ?? string.Empty);
    }

    public async Task WriteFileAsync(string path, string content, short mode, CancellationToken ct)
    {
        _sftp ??= await OpenSftpAsync(ct);
        await using var ms = new MemoryStream(Encoding.UTF8.GetBytes(content));
        await Task.Run(() =>
        {
            _sftp.UploadFile(ms, path, canOverride: true);
            _sftp.ChangePermissions(path, mode);
        }, ct);
    }

    private async Task<SftpClient> OpenSftpAsync(CancellationToken ct)
    {
        using var ms = new MemoryStream(Encoding.UTF8.GetBytes(endpoint.PrivateKeyPem.Replace("\r\n", "\n", StringComparison.Ordinal).Trim() + "\n"));
        var keyFile = new PrivateKeyFile(ms);
        var info = new ConnectionInfo(connectHost, connectPort, endpoint.User, new PrivateKeyAuthenticationMethod(endpoint.User, keyFile))
        {
            Timeout = TimeSpan.FromSeconds(20),
        };
        // Every SFTP request gets a deadline (the default waits forever).
        var sftp = new SftpClient(info) { OperationTimeout = TimeSpan.FromMinutes(2), KeepAliveInterval = TimeSpan.FromSeconds(30) };
        sftp.HostKeyReceived += (_, e) =>
        {
            var presented = "SHA256:" + e.FingerPrintSHA256;
            e.CanTrust = endpoint.TrustedFingerprint == presented;
            if (!e.CanTrust) throw new HostKeyNotTrustedException(endpoint.Host, presented, endpoint.TrustedFingerprint);
        };
        await sftp.ConnectAsync(ct);
        return sftp;
    }

    public ValueTask DisposeAsync()
    {
        _sftp?.Dispose();
        client.Dispose();
        try { tunnel?.Stop(); } catch (Exception) { /* best effort */ }
        tunnel?.Dispose();
        jumpClient?.Dispose();
        return ValueTask.CompletedTask;
    }
}
