namespace Backora.DesignSystem;

public enum ToastKind { Success, Error, Warning, Info }

public sealed record ToastMessage(Guid Id, ToastKind Kind, string Title, string? Message, string? Reference);

/// <summary>Scoped (per circuit) toast queue.</summary>
public sealed class ToastService
{
    private readonly List<ToastMessage> _items = [];
    public event Action? Changed;

    public IReadOnlyList<ToastMessage> Items => _items;

    public void Show(ToastKind kind, string title, string? message = null, string? reference = null)
    {
        var toast = new ToastMessage(Guid.NewGuid(), kind, title, message, reference);
        _items.Add(toast);
        if (_items.Count > 5) _items.RemoveAt(0);
        Changed?.Invoke();
        _ = DismissLater(toast.Id, kind == ToastKind.Error ? 10000 : 6000);
    }

    public void Success(string title, string? message = null) => Show(ToastKind.Success, title, message);
    public void Error(string title, string? message = null, string? reference = null) => Show(ToastKind.Error, title, message, reference);
    public void Warning(string title, string? message = null) => Show(ToastKind.Warning, title, message);
    public void Info(string title, string? message = null) => Show(ToastKind.Info, title, message);

    public void Dismiss(Guid id)
    {
        if (_items.RemoveAll(t => t.Id == id) > 0) Changed?.Invoke();
    }

    private async Task DismissLater(Guid id, int ms)
    {
        await Task.Delay(ms);
        Dismiss(id);
    }
}
