using Dashboard.Core.Models;

namespace Dashboard.Core.Abstractions;

/// <summary>Turns one parsed llama.cpp chunk's <see cref="LlamaCppChunkTimings"/> into a
/// live <see cref="MetricsSample"/> and gets it to the same place a vLLM instance's
/// polled samples already go (stored via <see cref="IMetricsService"/>, broadcast via
/// <see cref="IMetricsBroadcaster"/>) -- the only thing P7 adds is where the sample comes
/// from (the proxy observing a real stream, not a <c>/metrics</c> scrape), not a second,
/// parallel storage/broadcast mechanism.</summary>
public interface ILlamaCppLiveMetricsPublisher
{
    /// <param name="sourceId">Must match <see cref="Models.InstanceSourceId.For"/> for this
    /// instance -- the same id <c>InstanceMetricsPollingService</c> uses for its own scrapes
    /// -- so the Overview page's existing SignalR subscription (keyed on that id) receives
    /// this sample without needing to know it came from a different producer.</param>
    /// <param name="ctxSize">The active recipe's context size (<c>--ctx-size</c>), used
    /// to turn the chunk's raw token counts into a percentage — see <see
    /// cref="LlamaCppChunkTimings.ToKvCachePercent"/>.</param>
    Task PublishAsync(string sourceId, string sourceLabel, LlamaCppChunkTimings timings, int ctxSize, CancellationToken ct = default);

    /// <summary>The last timings this publisher has seen for a source, or null if none
    /// yet. The proxy reads this right before forwarding a NEW request, so it can bracket
    /// the request with a start marker (0 tok/s is true the instant a request begins, but
    /// the KV-cache slot's occupancy is whatever the last turn left it at, not 0 -- same
    /// reasoning as the end-of-stream marker in ModelProxyService.StreamWithTeeAsync)
    /// instead of either a fabricated 0% cache reading or a gap on the chart until the
    /// first real chunk of the new request arrives.</summary>
    LlamaCppChunkTimings? LastKnown(string sourceId);
}
