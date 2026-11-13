using Microsoft.JSInterop;

namespace Backora.DesignSystem;

/// <summary>Lazily imported JS interop module. Safe to call during prerender (calls are skipped).</summary>
public sealed class BackoraJs : IAsyncDisposable, IDisposable
{
    private readonly Lazy<Task<IJSObjectReference>> _module;

    public BackoraJs(IJSRuntime js)
    {
        _module = new(() => js.InvokeAsync<IJSObjectReference>("import", "./_content/Backora.DesignSystem/js/backora.js").AsTask());
    }

    public async ValueTask InvokeVoidAsync(string identifier, params object?[] args)
    {
        try
        {
            var module = await _module.Value;
            if (module is null) return;
            await module.InvokeVoidAsync(identifier, args);
        }
        catch (JSDisconnectedException) { }
        catch (InvalidOperationException) { /* prerendering: JS unavailable */ }
        catch (TaskCanceledException) { }
    }

    public async ValueTask<T?> InvokeAsync<T>(string identifier, params object?[] args)
    {
        try
        {
            var module = await _module.Value;
            if (module is null) return default;
            return await module.InvokeAsync<T>(identifier, args);
        }
        catch (JSDisconnectedException) { return default; }
        catch (InvalidOperationException) { return default; }
        catch (TaskCanceledException) { return default; }
    }

    public void Dispose()
    {
        // Synchronous disposal (e.g. test hosts): the JS module is released with the circuit.
    }

    public async ValueTask DisposeAsync()
    {
        if (_module.IsValueCreated)
        {
            try { var m = await _module.Value; if (m is not null) await m.DisposeAsync(); }
            catch (JSDisconnectedException) { }
            catch (InvalidOperationException) { }
        }
    }
}
