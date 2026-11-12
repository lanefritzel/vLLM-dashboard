using Dashboard.Core.Abstractions;
using Dashboard.Core.Models;
using Microsoft.AspNetCore.SignalR;

namespace Dashboard.Application.Metrics;

/// <inheritdoc cref="IMetricsBroadcaster" />
public sealed class MetricsBroadcaster : IMetricsBroadcaster
{
    private readonly IHubContext<MetricsHub> _hub;
    private readonly IBroadcastThrottler _throttler;

    public MetricsBroadcaster(IHubContext<MetricsHub> hub, IBroadcastThrottler throttler)
    {
        _hub = hub;
        _throttler = throttler;
    }

    public async Task BroadcastAsync(MetricsSample sample, CancellationToken ct = default)
    {
        if (!_throttler.Allow(sample.SourceId))
            return;
        await _hub.Clients.Group(MetricsHub.GroupFor(sample.SourceId)).SendAsync(MetricsHub.SampleEvent, sample, ct);
    }
}
