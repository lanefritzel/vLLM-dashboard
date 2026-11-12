using Dashboard.Core.Models;

namespace Dashboard.Core.Abstractions;

/// <summary>Tells any connected live-chart viewers about a sample, throttled so a fast
/// producer (a scrape loop, or P7's proxy-observed llama.cpp chunks) can never flood the
/// UI. Wraps exactly what <c>InstanceMetricsPollingService</c>'s own scrape loop already
/// does inline (check <see cref="IBroadcastThrottler"/>, then send) behind one call, so a
/// second caller with a different sample source (see
/// <see cref="ILlamaCppLiveMetricsPublisher"/>) doesn't have to duplicate that pairing.</summary>
public interface IMetricsBroadcaster
{
    Task BroadcastAsync(MetricsSample sample, CancellationToken ct = default);
}
