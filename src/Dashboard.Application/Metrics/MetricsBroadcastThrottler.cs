using Dashboard.Core.Abstractions;

namespace Dashboard.Application.Metrics;

/// <summary>
/// Limits live broadcasts per source to at most one per interval, so the UI is
/// never flooded even if a scrape loop runs faster than the render rate.
/// </summary>
public sealed class MetricsBroadcastThrottler : IBroadcastThrottler
{
    private readonly object _lock = new();
    private readonly Dictionary<string, DateTime> _last = new();

    public MetricsBroadcastThrottler(int minIntervalMs = 1000)
    {
        MinIntervalMs = Math.Max(200, minIntervalMs);
    }

    public int MinIntervalMs { get; }

    public bool Allow(string sourceId)
    {
        lock (_lock)
        {
            var now = DateTime.UtcNow;
            if (_last.TryGetValue(sourceId, out var last) &&
                (now - last).TotalMilliseconds < MinIntervalMs)
            {
                return false;
            }
            _last[sourceId] = now;
            return true;
        }
    }

    public void Reset(string sourceId)
    {
        lock (_lock)
            _last.Remove(sourceId);
    }
}
