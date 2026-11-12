using System.Collections.Concurrent;
using Dashboard.Core.Abstractions;
using Dashboard.Core.Models;

namespace Dashboard.Infrastructure.Proxy;

/// <inheritdoc cref="ILlamaCppLiveMetricsPublisher" />
public sealed class LlamaCppLiveMetricsPublisher : ILlamaCppLiveMetricsPublisher
{
    private readonly IMetricsService _metrics;
    private readonly IMetricsBroadcaster _broadcaster;

    // Raw timings, not the KV percent -- ctxSize can differ call to call (a different
    // recipe may now be active), so the percent is always recomputed at publish time
    // against whatever ctxSize that call was given, never cached.
    private readonly ConcurrentDictionary<string, LlamaCppChunkTimings> _lastKnown = new();

    public LlamaCppLiveMetricsPublisher(IMetricsService metrics, IMetricsBroadcaster broadcaster)
    {
        _metrics = metrics;
        _broadcaster = broadcaster;
    }

    public async Task PublishAsync(string sourceId, string sourceLabel, LlamaCppChunkTimings timings, int ctxSize, CancellationToken ct = default)
    {
        var sample = new MetricsSample
        {
            Ts = DateTime.UtcNow,
            SourceId = sourceId,
            SourceLabel = sourceLabel,
            Up = true,
            GenTps = timings.PredictedPerSecond,
            KvPct = timings.ToKvCachePercent(ctxSize),
        };

        _lastKnown[sourceId] = timings;
        _metrics.RecordExternalSample(sample);
        await _broadcaster.BroadcastAsync(sample, ct);
    }

    public LlamaCppChunkTimings? LastKnown(string sourceId) =>
        _lastKnown.TryGetValue(sourceId, out var t) ? t : null;
}
