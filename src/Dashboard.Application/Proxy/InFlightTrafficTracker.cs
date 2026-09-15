using System.Collections.Concurrent;
using System.Text;

namespace Dashboard.Application.Proxy;

/// <summary>
/// Tracks requests currently streaming through the proxy, purely so a Live Traffic
/// viewer who connects (or switches source) mid-stream can be caught up. Without this,
/// TrafficHub.JoinSource only adds the connection to the SignalR group — a request whose
/// "started" broadcast already went out before this viewer joined is invisible to them:
/// every later "chunk" for it references a request id their card list never heard of, so
/// they see nothing for it even while it's still actively streaming to the real client.
/// </summary>
public interface IInFlightTrafficTracker
{
    void Started(TrafficRequestStarted e);
    void Append(string requestId, string text, TrafficChunkKind kind);
    void Finished(string requestId);
    IReadOnlyList<InFlightSnapshot> GetForSource(string sourceId);
}

public sealed record InFlightSnapshot(TrafficRequestStarted Started, string ReasoningText, string ContentText);

public sealed class InFlightTrafficTracker : IInFlightTrafficTracker
{
    private sealed class Entry
    {
        public required TrafficRequestStarted Started;
        public readonly StringBuilder Reasoning = new();
        public readonly StringBuilder Content = new();
        public readonly object Lock = new();
    }

    private readonly ConcurrentDictionary<string, Entry> _entries = new();

    public void Started(TrafficRequestStarted e) =>
        _entries[e.RequestId] = new Entry { Started = e };

    public void Append(string requestId, string text, TrafficChunkKind kind)
    {
        if (!_entries.TryGetValue(requestId, out var entry)) return;
        lock (entry.Lock)
        {
            var sb = kind == TrafficChunkKind.Reasoning ? entry.Reasoning : entry.Content;
            sb.Append(text);
        }
    }

    // Requests are removed once finished — a viewer joining after that just sees it as
    // a normal finished card next time, same as before this tracker existed. Only the
    // still-in-flight window needed the catch-up.
    public void Finished(string requestId) => _entries.TryRemove(requestId, out _);

    public IReadOnlyList<InFlightSnapshot> GetForSource(string sourceId) =>
        _entries.Values
            .Where(e => e.Started.SourceId == sourceId)
            .Select(e =>
            {
                lock (e.Lock)
                    return new InFlightSnapshot(e.Started, e.Reasoning.ToString(), e.Content.ToString());
            })
            .OrderBy(s => s.Started.StartedAt)
            .ToList();
}
