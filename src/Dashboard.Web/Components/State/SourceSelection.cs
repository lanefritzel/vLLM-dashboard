namespace Dashboard.Web.Components;

/// <summary>
/// App-scoped single source of truth for "which source is selected." Shared by the
/// header selector and every live page across interactive circuits. Changes raise
/// <see cref="Changed"/> so subscribers re-join the hub and re-seed charts.
/// </summary>
public sealed class SourceSelection
{
    private readonly object _lock = new();
    private string? _currentSourceId;

    public event EventHandler? Changed;

    public string? CurrentSourceId
    {
        get { lock (_lock) return _currentSourceId; }
    }

    public void SetCurrent(string? id)
    {
        bool changed;
        lock (_lock)
        {
            changed = _currentSourceId != id;
            _currentSourceId = id;
        }
        if (changed)
            Changed?.Invoke(this, EventArgs.Empty);
    }
}
