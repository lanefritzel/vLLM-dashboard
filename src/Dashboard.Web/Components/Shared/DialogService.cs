namespace Dashboard.Web.Components.Shared;

/// <summary>Themed replacement for native <c>confirm()</c>/<c>alert()</c> — those
/// ignore the app's dark theme entirely and render as a jarring native browser popup.
/// Scoped (not singleton): dialog state belongs to one user's circuit, never shared
/// across sessions. <see cref="DialogHost"/> is the single component (mounted once in
/// MainLayout) that actually renders whatever request is pending.</summary>
public interface IDialogService
{
    Task<bool> ConfirmAsync(string message, string title = "Confirm", string confirmText = "Confirm", string cancelText = "Cancel");
    Task AlertAsync(string message, string title = "Notice");
}

public sealed class DialogRequest
{
    public required string Message { get; init; }
    public required string Title { get; init; }
    public required string ConfirmText { get; init; }
    public string? CancelText { get; init; }
    public bool IsAlert { get; init; }
    public required TaskCompletionSource<bool> Completion { get; init; }
}

public sealed class DialogService : IDialogService
{
    public event Action? StateChanged;

    public DialogRequest? Current { get; private set; }

    public Task<bool> ConfirmAsync(string message, string title = "Confirm", string confirmText = "Confirm", string cancelText = "Cancel")
    {
        var tcs = new TaskCompletionSource<bool>();
        Current = new DialogRequest
        {
            Message = message,
            Title = title,
            ConfirmText = confirmText,
            CancelText = cancelText,
            IsAlert = false,
            Completion = tcs,
        };
        StateChanged?.Invoke();
        return tcs.Task;
    }

    public Task AlertAsync(string message, string title = "Notice")
    {
        var tcs = new TaskCompletionSource<bool>();
        Current = new DialogRequest
        {
            Message = message,
            Title = title,
            ConfirmText = "OK",
            CancelText = null,
            IsAlert = true,
            Completion = tcs,
        };
        StateChanged?.Invoke();
        return tcs.Task;
    }

    public void Resolve(bool result)
    {
        var request = Current;
        if (request is null) return;
        Current = null;
        StateChanged?.Invoke();
        request.Completion.TrySetResult(result);
    }
}
