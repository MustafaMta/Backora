using System.Collections.Concurrent;
using Backora.Application.Abstractions;
using Backora.Application.Operations;
using Backora.Application.Telemetry;
using Backora.Domain.Platform;
using Backora.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Backora.Api.Execution;

public sealed class WorkerIdentity
{
    public string Id { get; } = $"{Environment.MachineName}:{Environment.ProcessId}:{Guid.NewGuid().ToString("N")[..6]}";
    public DateTimeOffset StartedAt { get; } = DateTimeOffset.UtcNow;
    private int _active;
    public int Active => _active;
    public void Increment() => Interlocked.Increment(ref _active);
    public void Decrement() => Interlocked.Decrement(ref _active);
}

/// <summary>
/// Claims operations of one kind with PostgreSQL leases and processes up to N concurrently. A heartbeat
/// loop renews each lease; if renewal fails (DB outage, lease stolen after a long pause) the operation's
/// token is cancelled so two workers never act on the same operation. On shutdown the dispatcher stops
/// claiming, cancels in-flight processing (remote processes keep running) and releases leases so another
/// replica adopts them immediately.
/// </summary>
public sealed class OperationDispatcher<TProcessor>(
    TProcessor processor,
    ILeaseStore leases,
    WorkerIdentity identity,
    IOptions<WorkerOptions> options,
    ILogger<OperationDispatcher<TProcessor>> logger) : BackgroundService
    where TProcessor : IOperationProcessor
{
    private readonly ConcurrentDictionary<long, Task> _inFlight = new();

    private int Capacity => processor.Kind switch
    {
        OperationKinds.Backup => options.Value.MaxConcurrentBackups,
        OperationKinds.Offload => options.Value.MaxConcurrentTransfers,
        _ => options.Value.MaxConcurrentRestores,
    };

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var lease = TimeSpan.FromSeconds(options.Value.LeaseSeconds);
        logger.LogInformation("{Kind} dispatcher started on {Worker} (capacity {Capacity})", processor.Kind, identity.Id, Capacity);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                while (_inFlight.Count < Capacity && !stoppingToken.IsCancellationRequested)
                {
                    var id = await leases.ClaimNextAsync(processor.Kind, identity.Id, lease, stoppingToken);
                    if (id is null) break;
                    _inFlight[id.Value] = RunAsync(id.Value, lease, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                logger.LogError(ex, "{Kind} dispatcher failed to claim work (database unavailable?)", processor.Kind);
            }

            try { await Task.Delay(TimeSpan.FromSeconds(options.Value.ClaimIntervalSeconds), stoppingToken); }
            catch (OperationCanceledException) { break; }
        }

        logger.LogInformation("{Kind} dispatcher draining {Count} operation(s)", processor.Kind, _inFlight.Count);
        await Task.WhenAll(_inFlight.Values);
    }

    private async Task RunAsync(long id, TimeSpan lease, CancellationToken stoppingToken)
    {
        await Task.Yield();
        using var operationCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        identity.Increment();
        var heartbeat = HeartbeatAsync(id, lease, operationCts);
        try
        {
            using var activity = BackoraTelemetry.ActivitySource.StartActivity($"{processor.Kind}.process");
            activity?.SetTag("backora.operation.kind", processor.Kind);
            activity?.SetTag("backora.operation.id", id);
            await processor.ProcessAsync(id, identity.Id, operationCts.Token);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "{Kind} {Id} processing crashed; the lease will expire and the operation will be retried", processor.Kind, id);
        }
        finally
        {
            await operationCts.CancelAsync();
            try { await heartbeat; } catch (OperationCanceledException) { /* expected */ }
            try
            {
                // Release with a fresh token: also on shutdown so another replica can adopt the operation right away.
                using var releaseCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await leases.ReleaseAsync(processor.Kind, id, identity.Id, releaseCts.Token);
            }
            catch (Exception ex) { logger.LogWarning(ex, "Could not release lease for {Kind} {Id}; it will expire", processor.Kind, id); }
            identity.Decrement();
            _inFlight.TryRemove(id, out _);
        }
    }

    private async Task HeartbeatAsync(long id, TimeSpan lease, CancellationTokenSource operationCts)
    {
        var interval = TimeSpan.FromSeconds(Math.Max(5, lease.TotalSeconds / 4));
        var failures = 0;
        while (!operationCts.IsCancellationRequested)
        {
            await Task.Delay(interval, operationCts.Token);
            try
            {
                if (!await leases.RenewAsync(processor.Kind, id, identity.Id, lease, operationCts.Token))
                {
                    logger.LogWarning("Lease for {Kind} {Id} was lost; stopping local processing", processor.Kind, id);
                    BackoraTelemetry.LeaseLost.Add(1, new System.Diagnostics.TagList { { "kind", processor.Kind } });
                    await operationCts.CancelAsync();
                    return;
                }
                failures = 0;
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                failures++;
                logger.LogWarning(ex, "Lease renewal failed for {Kind} {Id} ({Failures})", processor.Kind, id, failures);
                // Stop before the lease can expire so we never overlap with an adopting worker.
                if (interval * (failures + 1) >= lease * 0.75)
                {
                    await operationCts.CancelAsync();
                    return;
                }
            }
        }
    }
}

/// <summary>Runs a periodic task under a cluster-wide lock so exactly one replica executes it.</summary>
public abstract class ClusterSingletonService(IServiceScopeFactory scopes, ILeaseStore leases, WorkerIdentity identity, ILogger logger) : BackgroundService
{
    protected abstract string LockName { get; }
    protected abstract TimeSpan Interval { get; }
    protected abstract Task<int> RunOnceAsync(IServiceProvider services, CancellationToken ct);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (await leases.TryAcquireClusterLockAsync(LockName, identity.Id, Interval + TimeSpan.FromSeconds(60), stoppingToken))
                {
                    await using var scope = scopes.CreateAsyncScope();
                    var count = await RunOnceAsync(scope.ServiceProvider, stoppingToken);
                    if (count > 0) logger.LogInformation("{Task} processed {Count} item(s)", LockName, count);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                logger.LogError(ex, "{Task} iteration failed", LockName);
            }
            try { await Task.Delay(Interval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }
}

public sealed class SchedulerHostedService(IServiceScopeFactory s, ILeaseStore l, WorkerIdentity i, IOptions<WorkerOptions> o, ILogger<SchedulerHostedService> log)
    : ClusterSingletonService(s, l, i, log)
{
    protected override string LockName => "scheduler";
    protected override TimeSpan Interval => TimeSpan.FromSeconds(o.Value.SchedulerIntervalSeconds);
    protected override Task<int> RunOnceAsync(IServiceProvider services, CancellationToken ct) => services.GetRequiredService<SchedulerService>().TickAsync(ct);
}

public sealed class RetentionHostedService(IServiceScopeFactory s, ILeaseStore l, WorkerIdentity i, IOptions<WorkerOptions> o, ILogger<RetentionHostedService> log)
    : ClusterSingletonService(s, l, i, log)
{
    protected override string LockName => "retention";
    protected override TimeSpan Interval => TimeSpan.FromMinutes(o.Value.RetentionIntervalMinutes);
    protected override Task<int> RunOnceAsync(IServiceProvider services, CancellationToken ct) => services.GetRequiredService<RetentionService>().TickAsync(ct);
}

public sealed class NotificationHostedService(IServiceScopeFactory s, ILeaseStore l, WorkerIdentity i, IOptions<WorkerOptions> o, ILogger<NotificationHostedService> log)
    : ClusterSingletonService(s, l, i, log)
{
    protected override string LockName => "notifications";
    protected override TimeSpan Interval => TimeSpan.FromSeconds(o.Value.NotificationIntervalSeconds);
    protected override Task<int> RunOnceAsync(IServiceProvider services, CancellationToken ct) => services.GetRequiredService<NotificationDispatcher>().TickAsync(ct);
}

/// <summary>Publishes this worker's liveness (used by the dashboard and readiness checks) and prunes stale entries.</summary>
public sealed class HeartbeatHostedService(IServiceScopeFactory scopes, WorkerIdentity identity, ILogger<HeartbeatHostedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<BackoraDbContext>();
                var now = DateTimeOffset.UtcNow;
                var hb = await db.WorkerHeartbeats.FirstOrDefaultAsync(h => h.WorkerId == identity.Id, stoppingToken);
                if (hb is null)
                {
                    db.WorkerHeartbeats.Add(new WorkerHeartbeat
                    {
                        WorkerId = identity.Id, HostName = Environment.MachineName, Version = "2.0.0", StartedAt = identity.StartedAt, LastSeenAt = now, ActiveOperations = identity.Active,
                    });
                }
                else
                {
                    hb.LastSeenAt = now;
                    hb.ActiveOperations = identity.Active;
                }
                await db.SaveChangesAsync(stoppingToken);
                await db.WorkerHeartbeats.Where(h => h.LastSeenAt < now.AddDays(-1)).ExecuteDeleteAsync(stoppingToken);
                await db.IdempotencyRecords.Where(r => r.CreatedAt < now.AddDays(-7)).ExecuteDeleteAsync(stoppingToken);
                await db.RefreshTokens.Where(t => t.ExpiresAt < now.AddDays(-30)).ExecuteDeleteAsync(stoppingToken);
                await db.OperationLeases.Where(l => l.ExpiresAt < now.AddDays(-1)).ExecuteDeleteAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { logger.LogWarning(ex, "Heartbeat failed"); }
            try { await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken); }
            catch (OperationCanceledException) { break; }
        }

        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<BackoraDbContext>();
            await db.WorkerHeartbeats.Where(h => h.WorkerId == identity.Id).ExecuteDeleteAsync(CancellationToken.None);
        }
        catch (Exception ex) { logger.LogDebug(ex, "Could not remove heartbeat at shutdown"); }
    }
}
