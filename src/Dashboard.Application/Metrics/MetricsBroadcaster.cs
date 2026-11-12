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
        // Start/end bracket markers (GenTps == 0) are the only genuine zero a
        // llama.cpp source ever reports -- throttling them away silently along with
        // whatever real sample happens to land in the same ~1s window is exactly
        // what made the chart's ramp-up/ramp-down edges vanish even though they were
        // written to the DB (RecordExternalSample, a few lines up the call chain in
        // LlamaCppLiveMetricsPublisher.PublishAsync, was never throttled -- only this
        // live broadcast was). They're rare (one pair per request) and the whole
        // point of a marker is that this exact edge renders, so they always go
        // through regardless of the throttle window.
        var isMarker = sample.GenTps == 0;
        if (!isMarker && !_throttler.Allow(sample.SourceId))
            return;
        await _hub.Clients.Group(MetricsHub.GroupFor(sample.SourceId)).SendAsync(MetricsHub.SampleEvent, sample, ct);
    }
}
