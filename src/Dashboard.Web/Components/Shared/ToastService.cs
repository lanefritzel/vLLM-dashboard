namespace Dashboard.Web.Components.Shared;

/// <summary>Themed replacement for a one-off "Saved!" banner — one place that
/// composes transient feedback instead of every page inventing its own. Scoped
/// (not singleton), mirroring <see cref="DialogService"/>: toast state belongs
/// to one user's circuit. <see cref="ToastHost"/> (mounted once in MainLayout)
/// renders whatever's in <see cref="Toasts"/>.</summary>
public sealed class ToastItem
{
    public required Guid Id { get; init; }
    public required string Message { get; init; }
    public string? Title { get; init; }

    /// <summary>success | info | warn | danger — matches the ui-toast-* CSS tone.</summary>
    public required string Tone { get; init; }
    public string? ActionText { get; init; }
    public Action? OnAction { get; init; }
}

public interface IToastService
{
    IReadOnlyList<ToastItem> Toasts { get; }
    event Action? StateChanged;

    /// <summary>durationMs: null persists until dismissed (the default for warn/danger);
    /// success/info default to a 5s auto-dismiss unless overridden.</summary>
    void Show(string message, string tone = "info", string? title = null, int? durationMs = null, string? actionText = null, Action? onAction = null);
    void Dismiss(Guid id);
    void Pause(Guid id);
    void Resume(Guid id);
}

public sealed class ToastService : IToastService, IDisposable
{
    private static readonly Dictionary<string, int?> DefaultDurationMs = new(StringComparer.OrdinalIgnoreCase)
    {
        ["success"] = 5000,
        ["info"] = 5000,
        ["warn"] = null,
        ["danger"] = null,
    };

    private readonly List<ToastItem> _toasts = [];
    private readonly Dictionary<Guid, System.Timers.Timer> _timers = [];
    private readonly Dictionary<Guid, (int RemainingMs, DateTime StartedAtUtc)> _remaining = [];

    public event Action? StateChanged;
    public IReadOnlyList<ToastItem> Toasts => _toasts;

    public void Show(string message, string tone = "info", string? title = null, int? durationMs = null, string? actionText = null, Action? onAction = null)
    {
        var id = Guid.NewGuid();
        var duration = durationMs ?? (DefaultDurationMs.TryGetValue(tone, out var d) ? d : null);

        _toasts.Add(new ToastItem
        {
            Id = id,
            Message = message,
            Title = title,
            Tone = tone,
            ActionText = actionText,
            OnAction = onAction,
        });

        if (duration is int ms)
            StartTimer(id, ms);

        StateChanged?.Invoke();
    }

    public void Dismiss(Guid id)
    {
        var removed = _toasts.RemoveAll(t => t.Id == id) > 0;
        if (!removed) return;

        if (_timers.Remove(id, out var timer))
        {
            timer.Stop();
            timer.Dispose();
        }
        _remaining.Remove(id);

        StateChanged?.Invoke();
    }

    /// <summary>Called on pointer/focus enter — stops the auto-dismiss clock and
    /// remembers how much time was left so Resume can pick back up.</summary>
    public void Pause(Guid id)
    {
        if (!_timers.TryGetValue(id, out var timer) || !_remaining.TryGetValue(id, out var state))
            return;

        timer.Stop();
        var elapsedMs = (int)(DateTime.UtcNow - state.StartedAtUtc).TotalMilliseconds;
        _remaining[id] = (Math.Max(0, state.RemainingMs - elapsedMs), DateTime.UtcNow);
    }

    public void Resume(Guid id)
    {
        if (!_timers.TryGetValue(id, out var timer) || !_remaining.TryGetValue(id, out var state))
            return;

        timer.Interval = Math.Max(1, state.RemainingMs);
        _remaining[id] = (state.RemainingMs, DateTime.UtcNow);
        timer.Start();
    }

    private void StartTimer(Guid id, int ms)
    {
        _remaining[id] = (ms, DateTime.UtcNow);
        var timer = new System.Timers.Timer(ms) { AutoReset = false };
        timer.Elapsed += (_, _) => Dismiss(id);
        _timers[id] = timer;
        timer.Start();
    }

    public void Dispose()
    {
        foreach (var timer in _timers.Values)
        {
            timer.Stop();
            timer.Dispose();
        }
        _timers.Clear();
    }
}
