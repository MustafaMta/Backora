using System.Collections.Concurrent;
using System.Security.Cryptography;
using Backora.Application.Abstractions;
using Backora.Application.Execution;
using Backora.Domain;
using Backora.Domain.Execution;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Backora.Application.Encryption;

/// <summary>
/// Holds an operator-supplied age identity in API-process memory only, until the restore it belongs to is
/// ready (status AwaitingKey, FIFO created on the worker host), then writes it into that FIFO over SSH via
/// the command's standard input. The key never touches PostgreSQL, queues, files, command lines or logs,
/// and never enters the Backora worker process.
///
/// Every key has a bounded lifetime (≤ the restore's key-request expiry); its buffer is zeroed as soon as
/// it is delivered, discarded, superseded or expired. If the API restarts before delivery the key is simply
/// gone: the restore stays in AwaitingKey and the operator submits the key again.
/// </summary>
public sealed class DecryptionKeyCourier(
    IServiceScopeFactory scopes,
    ISshConnector ssh,
    IOptions<ClientEncryptionOptions> options,
    ILogger<DecryptionKeyCourier> logger) : IDisposable
{
    private readonly ConcurrentDictionary<long, Delivery> _pending = new();
    private readonly CancellationTokenSource _shutdown = new();
    private ClientEncryptionOptions Options => options.Value;

    private sealed class Delivery(byte[] key, DateTimeOffset deadline, CancellationToken shutdown) : IDisposable
    {
        private readonly CancellationTokenSource _cts = CancellationTokenSource.CreateLinkedTokenSource(shutdown);
        private int _disposed;
        public byte[] Key { get; } = key;
        public DateTimeOffset Deadline { get; set; } = deadline;
        public CancellationToken Token { get; private set; }
        public Delivery Start() { Token = _cts.Token; return this; }
        public Task? Worker { get; set; }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
            CryptographicOperations.ZeroMemory(Key);
            try { _cts.Cancel(); } catch (ObjectDisposedException) { }
            _cts.Dispose();
        }
    }

    /// <summary>True while a key for <paramref name="restoreId"/> is held and not yet delivered.</summary>
    public bool IsPending(long restoreId) => _pending.ContainsKey(restoreId);

    /// <summary>
    /// Takes ownership of <paramref name="identityLine"/> (it is zeroed by the courier) and delivers it when the
    /// restore reaches AwaitingKey, at the latest by <paramref name="deadline"/>.
    /// </summary>
    public void Submit(long restoreId, byte[] identityLine, DateTimeOffset deadline)
    {
        var maxDeadline = DateTimeOffset.UtcNow + Options.KeyRequestTimeout;
        var delivery = new Delivery(identityLine, deadline < maxDeadline ? deadline : maxDeadline, _shutdown.Token).Start();
        _pending.AddOrUpdate(restoreId, delivery, (_, old) => { old.Dispose(); return delivery; });
        delivery.Worker = Task.Run(() => RunAsync(restoreId, delivery));
    }

    /// <summary>Drops (and zeroes) a pending key, e.g. when the restore is cancelled.</summary>
    public void Discard(long restoreId)
    {
        if (_pending.TryRemove(restoreId, out var d)) d.Dispose();
    }

    private async Task RunAsync(long restoreId, Delivery delivery)
    {
        var poll = TimeSpan.FromMilliseconds(Math.Clamp(Options.CourierPollMilliseconds, 100, 30_000));
        try
        {
            while (!delivery.Token.IsCancellationRequested && DateTimeOffset.UtcNow < delivery.Deadline)
            {
                var state = await ReadStateAsync(restoreId, delivery.Token);
                // Running after a key request means this attempt already received a key (from an earlier submission).
                if (state is null || state.Status is RestoreStatus.Completed or RestoreStatus.Failed or RestoreStatus.Cancelled or RestoreStatus.Cancelling or RestoreStatus.Verifying
                    || (state.Status == RestoreStatus.Running && state.KeyRequestedAt is not null))
                {
                    logger.LogInformation("Restore {RestoreId} is no longer waiting for a key; discarding the held key", restoreId);
                    return;
                }
                if (state.KeyRequestExpiresAt is { } expires && expires < delivery.Deadline) delivery.Deadline = expires;
                if (state.Status == RestoreStatus.AwaitingKey && state.RemoteDirectory is { } dir)
                {
                    if (await TryDeliverAsync(restoreId, dir, delivery))
                    {
                        logger.LogInformation("Decryption key for restore {RestoreId} was handed to the worker host", restoreId);
                        return;
                    }
                }
                await Task.Delay(poll, delivery.Token);
            }
            if (!delivery.Token.IsCancellationRequested)
            {
                logger.LogWarning("Decryption key for restore {RestoreId} expired before the restore was ready; it was discarded", restoreId);
            }
        }
        catch (OperationCanceledException) { /* discarded, superseded or shutting down */ }
        catch (Exception ex)
        {
            // Exceptions never carry the key (it is only ever written to an SSH channel).
            logger.LogWarning(ex, "Key delivery for restore {RestoreId} failed; the operator can submit the key again", restoreId);
        }
        finally
        {
            _pending.TryRemove(new KeyValuePair<long, Delivery>(restoreId, delivery));
            delivery.Dispose();
        }
    }

    private sealed record RestoreState(RestoreStatus Status, string? RemoteDirectory, DateTimeOffset? KeyRequestedAt, DateTimeOffset? KeyRequestExpiresAt);

    private async Task<RestoreState?> ReadStateAsync(long restoreId, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<IAppDbContext>();
        return await db.RestoreRuns.AsNoTracking().Where(r => r.Id == restoreId)
            .Select(r => new RestoreState(r.Status, r.RemoteDirectory, r.KeyRequestedAt, r.KeyRequestExpiresAt)).FirstOrDefaultAsync(ct);
    }

    private async Task<bool> TryDeliverAsync(long restoreId, string directory, Delivery delivery)
    {
        if (!ShellQuote.IsSafeAbsolutePath(directory) || !directory.Contains("backora", StringComparison.OrdinalIgnoreCase)) return false;
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var env = await scope.ServiceProvider.GetRequiredService<ExecutionEnvironmentFactory>().CreateAsync(delivery.Token);
            await using var worker = await ssh.ConnectWorkerAsync(env.Worker, delivery.Token);
            var timeout = Math.Clamp(Options.DeliveryTimeoutSeconds, 5, 300);
            // The key travels only through this command's stdin. `timeout` guarantees the writer never blocks
            // forever if the reader has gone away (open() on a FIFO without a reader blocks).
            var fifo = ShellQuote.Quote(directory.TrimEnd('/') + "/key.fifo");
            var command = $"F={fifo}; [ -p \"$F\" ] || {{ echo NOFIFO; exit 3; }}; exec timeout {timeout} sh -c 'cat > \"$1\"' sh \"$F\"";
            var result = await worker.RunWithInputAsync(command, delivery.Key, TimeSpan.FromSeconds(timeout + 15), delivery.Token);
            if (result.Succeeded) return true;
            logger.LogInformation("Key delivery for restore {RestoreId} not completed yet (exit {ExitCode}); will retry", restoreId, result.ExitCode);
            return false;
        }
        catch (RemoteOperationException ex)
        {
            logger.LogWarning("Key delivery for restore {RestoreId} could not reach the worker host ({FailureClass}): {Message}", restoreId, ex.FailureClass, ex.Message);
            return false;
        }
    }

    public void Dispose()
    {
        _shutdown.Cancel();
        foreach (var id in _pending.Keys.ToList()) Discard(id);
        _shutdown.Dispose();
    }
}
