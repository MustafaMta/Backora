using Backora.DesignSystem;
using Backora.Web.Api;
using Backora.Web.Auth;
using Backora.Web.Services;
using Microsoft.AspNetCore.Components;

namespace Backora.Web.Components.Shared;

/// <summary>Base class for authenticated pages: API access, permission checks, uniform error handling.</summary>
public abstract class AppPage : ComponentBase, IDisposable
{
    private readonly CancellationTokenSource _cts = new();

    [Inject] protected BackoraApi Api { get; set; } = default!;
    [Inject] protected ToastService Toasts { get; set; } = default!;
    [Inject] protected NavigationManager Nav { get; set; } = default!;
    [Inject] protected UserContext CurrentUser { get; set; } = default!;
    [Inject] protected ClientClock Clock { get; set; } = default!;

    protected CancellationToken PageToken => _cts.Token;

    protected bool Can(string permission) => CurrentUser.Can(permission);

    protected override async Task OnInitializedAsync()
    {
        await CurrentUser.EnsureLoadedAsync();
        await OnPageInitializedAsync();
    }

    protected virtual Task OnPageInitializedAsync() => Task.CompletedTask;

    /// <summary>
    /// Standard handling of an API result: toasts success/failure and redirects to login when the session
    /// ended. Returns true when the call succeeded.
    /// </summary>
    protected bool Handle(ApiResult result, string? successTitle = null, string? successMessage = null, bool toastOnError = true)
    {
        if (result.Ok)
        {
            if (successTitle is not null) Toasts.Success(successTitle, successMessage);
            return true;
        }
        if (result.Outcome == ApiOutcome.Unauthorized)
        {
            RedirectToLogin();
            return false;
        }
        if (IsPasswordChangeRequired(result))
        {
            Nav.NavigateTo("/account/security");
            return false;
        }
        if (toastOnError) Toasts.Error(result.Title, result.Message, result.Outcome == ApiOutcome.ServerError ? result.CorrelationId : null);
        return false;
    }

    /// <summary>Returns an error message for page-level error states (null when OK) and handles session expiry.</summary>
    protected string? ErrorOf(ApiResult result)
    {
        if (result.Ok) return null;
        if (result.Outcome == ApiOutcome.Unauthorized) { RedirectToLogin(); return null; }
        if (IsPasswordChangeRequired(result)) { Nav.NavigateTo("/account/security"); return "You must change your password before using Backora."; }
        return result.Message;
    }

    protected static bool IsPasswordChangeRequired(ApiResult result) =>
        result.Outcome == ApiOutcome.Forbidden && string.Equals(result.Problem?.Code, "password_change_required", StringComparison.OrdinalIgnoreCase);

    protected void RedirectToLogin()
    {
        var relative = "/" + Nav.ToBaseRelativePath(Nav.Uri);
        Nav.NavigateTo($"/login?returnUrl={Uri.EscapeDataString(ReturnUrl.Sanitize(relative))}", forceLoad: true);
    }

    protected static string NewIdempotencyKey() => Guid.NewGuid().ToString("N");

    private bool _disposed;

    public virtual void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _cts.Cancel();
        _cts.Dispose();
        GC.SuppressFinalize(this);
    }
}
