using System.Collections.Concurrent;
using Dashboard.Core.Abstractions;

namespace Dashboard.Application.Idle;

public sealed class ActivityTracker : IActivityTracker
{
    private readonly ConcurrentDictionary<string, DateTime> _lastActivityUtc = new();

    public void Touch(string sourceId) => _lastActivityUtc[sourceId] = DateTime.UtcNow;

    public DateTime? LastActivityUtc(string sourceId) =>
        _lastActivityUtc.TryGetValue(sourceId, out var t) ? t : null;
}
