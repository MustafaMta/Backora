using Backora.Application.Abstractions;
using Backora.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Backora.Application.Operations;

public sealed class WorkerOptions
{
    public const string Section = "Worker";

    public int MaxConcurrentBackups { get; set; } = 10;
    public int MaxConcurrentTransfers { get; set; } = 4;
    public int MaxConcurrentRestores { get; set; } = 2;
    public int PollIntervalSeconds { get; set; } = 5;
    public int ClaimIntervalSeconds { get; set; } = 3;
    public int LeaseSeconds { get; set; } = 120;
    public int SchedulerIntervalSeconds { get; set; } = 20;
    public int RetentionIntervalMinutes { get; set; } = 15;
    public int NotificationIntervalSeconds { get; set; } = 10;

    /// <summary>Clock skew allowance between targets/NFS and the worker when discovering artifacts.</summary>
    public int ArtifactDiscoverySkewMinutes { get; set; } = 5;

    /// <summary>A transfer whose byte counter does not advance for this long is terminated and retried.</summary>
    public int TransferStallSeconds { get; set; } = 900;

    /// <summary>
    /// Compression of a finished backup on the worker, before its checksums: "zstd" (default — each output becomes ONE
    /// compressed file on the share and the raw output is removed) or "none". A job can turn it off with the variable
    /// COMPRESSION__&lt;job&gt; (or COMPRESSION for all jobs) set to none.
    /// </summary>
    public string BackupCompression { get; set; } = "zstd";

    /// <summary>
    /// Compression the worker applies while uploading: "none" (default — backups are already compressed on the worker
    /// after they finish) or "zstd". Already-compressed backups are never compressed again.
    /// </summary>
    public string UploadCompression { get; set; } = "none";
}

/// <summary>Outcome of one processing step.</summary>
public enum StepOutcome
{
    /// <summary>Operation reached a terminal state.</summary>
    Terminal,
    /// <summary>Continue immediately (state advanced).</summary>
    Continue,
    /// <summary>Wait for the poll interval, then continue (remote work in progress).</summary>
    Poll,
    /// <summary>Stop processing and release ownership (e.g. retry scheduled in the future).</summary>
    Yield,
}

/// <summary>
/// Shared driver for lease-owned operations. Each step runs in a fresh DI scope with a fresh DbContext so
/// state is always reloaded from the database (picking up cancellation requests made through the API).
/// Optimistic concurrency conflicts simply cause the step to be re-evaluated.
/// </summary>
public abstract class OperationProcessorBase(IServiceScopeFactory scopes, IOptions<WorkerOptions> options, ILogger logger) : IOperationProcessor
{
    protected WorkerOptions Options { get; } = options.Value;
    protected ILogger Logger { get; } = logger;

    public abstract string Kind { get; }

    protected abstract Task<StepOutcome> StepAsync(IServiceProvider services, long id, OperationContext context, CancellationToken ct);

    public async Task ProcessAsync(long id, string owner, CancellationToken ct)
    {
        var context = new OperationContext(owner);
        try
        {
            while (!ct.IsCancellationRequested)
            {
                StepOutcome outcome;
                await using (var scope = scopes.CreateAsyncScope())
                {
                    try
                    {
                        outcome = await StepAsync(scope.ServiceProvider, id, context, ct);
                    }
                    catch (DbUpdateConcurrencyException)
                    {
                        Logger.LogDebug("{Kind} {Id}: concurrent update detected, re-evaluating", Kind, id);
                        outcome = StepOutcome.Continue;
                    }
                }

                switch (outcome)
                {
                    case StepOutcome.Terminal or StepOutcome.Yield:
                        return;
                    case StepOutcome.Poll:
                        await Task.Delay(TimeSpan.FromSeconds(Options.PollIntervalSeconds), ct);
                        break;
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Shutdown or lease loss: remote processes keep running and another worker adopts the operation.
            Logger.LogInformation("{Kind} {Id}: processing interrupted (shutdown or lease lost); operation will be adopted", Kind, id);
        }
        finally
        {
            await context.DisposeAsync();
        }
    }

    protected static bool IsTransient(FailureClass failureClass) =>
        failureClass is FailureClass.Connectivity or FailureClass.Timeout;
}

public interface IOperationProcessor
{
    string Kind { get; }
    Task ProcessAsync(long id, string owner, CancellationToken ct);
}

/// <summary>Per-operation state kept across steps (e.g. an open SSH session).</summary>
public sealed class OperationContext(string owner) : IAsyncDisposable
{
    public string Owner { get; } = owner;
    public IRemoteSession? Session { get; private set; }
    public string? SessionKey { get; private set; }

    public async Task<IRemoteSession> GetSessionAsync(string key, Func<Task<IRemoteSession>> factory)
    {
        if (Session is not null && SessionKey == key) return Session;
        await ResetSessionAsync();
        Session = await factory();
        SessionKey = key;
        return Session;
    }

    public async Task ResetSessionAsync()
    {
        if (Session is not null)
        {
            try { await Session.DisposeAsync(); } catch (Exception) { /* ignore */ }
        }
        Session = null;
        SessionKey = null;
    }

    public ValueTask DisposeAsync() => new(ResetSessionAsync());
}
