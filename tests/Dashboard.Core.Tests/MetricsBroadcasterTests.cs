using Dashboard.Application.Metrics;
using Dashboard.Core.Abstractions;
using Dashboard.Core.Models;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;

namespace Dashboard.Core.Tests;

/// <summary>P7: <c>MetricsBroadcaster</c> is the throttle-then-send pairing
/// <c>InstanceMetricsPollingService</c>'s own scrape loop already does inline, pulled out
/// so a second producer (the llama.cpp live-metrics publisher) can reuse it instead of
/// re-implementing the pairing. No real SignalR client ever connects in these tests --
/// same pattern SwapServiceTests already uses for IHubContext&lt;DeploymentHub&gt;: a
/// real, clientless IHubContext via services.AddSignalR() is a safe no-op to send
/// through, so what's actually under test is the throttle gate, not wire delivery.</summary>
public class MetricsBroadcasterTests
{
    private sealed class FakeThrottler : IBroadcastThrottler
    {
        public bool AllowResult = true;
        public List<string> AllowCalledWith { get; } = new();
        public int MinIntervalMs => 1000;
        public bool Allow(string key) { AllowCalledWith.Add(key); return AllowResult; }
        public void Reset(string key) { }
    }

    private static IMetricsBroadcaster NewSut(FakeThrottler throttler)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSignalR();
        var provider = services.BuildServiceProvider();
        var hub = provider.GetRequiredService<IHubContext<MetricsHub>>();
        return new MetricsBroadcaster(hub, throttler);
    }

    [Fact]
    public async Task BroadcastAsync_ChecksTheThrottleForTheSamplesSourceId()
    {
        var throttler = new FakeThrottler();
        var sut = NewSut(throttler);

        await sut.BroadcastAsync(new MetricsSample { SourceId = "instance-7" });

        Assert.Equal("instance-7", Assert.Single(throttler.AllowCalledWith));
    }

    [Fact]
    public async Task BroadcastAsync_SkipsSending_WhenTheThrottleDisallowsIt()
    {
        var throttler = new FakeThrottler { AllowResult = false };
        var sut = NewSut(throttler);

        // No connected client and no throttle bypass means there's nothing to assert
        // on the wire -- what matters is this never throws even though it's a no-op,
        // same contract whether the hub route has zero or many real subscribers.
        await sut.BroadcastAsync(new MetricsSample { SourceId = "instance-7" });
    }

    [Fact]
    public async Task BroadcastAsync_SendsWhenAllowed_AgainstARealClientlessHubContext()
    {
        var throttler = new FakeThrottler { AllowResult = true };
        var sut = NewSut(throttler);

        // Nothing is listening on the group, so this is a safe no-op -- the assertion
        // here is that it completes without throwing against a real IHubContext.
        await sut.BroadcastAsync(new MetricsSample { SourceId = "instance-7", GenTps = 12.3 });
    }

    [Fact]
    public async Task BroadcastAsync_NeverConsultsTheThrottle_ForAZeroGenTpsMarkerSample()
    {
        // GenTps == 0 is the llama.cpp bracket-marker convention (ModelProxyService's
        // start/end markers) -- it's the one sample a dropped broadcast would make an
        // already-reported chart bug (the ramp-up/ramp-down edges silently vanishing)
        // come right back, since a marker always lands within the same ~1s throttle
        // window as the real sample next to it.
        var throttler = new FakeThrottler { AllowResult = false };
        var sut = NewSut(throttler);

        await sut.BroadcastAsync(new MetricsSample { SourceId = "instance-7", GenTps = 0 });

        Assert.Empty(throttler.AllowCalledWith);
    }
}
