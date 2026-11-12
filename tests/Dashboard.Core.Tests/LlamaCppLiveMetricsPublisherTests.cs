using Dashboard.Core.Abstractions;
using Dashboard.Core.Models;
using Dashboard.Infrastructure.Proxy;

namespace Dashboard.Core.Tests;

/// <summary>P7: PublishAsync is pure wiring -- build the sample, store it, broadcast it.
/// The actual KV-percent math is LlamaCppChunkTimings' own (tested in
/// LlamaCppChunkTimingsTests); what's under test here is that this class gets every
/// field to the right place and doesn't do anything on its own behalf.</summary>
public class LlamaCppLiveMetricsPublisherTests
{
    private sealed class FakeMetricsService : IMetricsService
    {
        public MetricsSample? Recorded;
        public Task<MetricsSample> ScrapeAndStoreAsync(SourceInfo source, CancellationToken ct = default) =>
            throw new NotSupportedException("Not used by this publisher.");
        public void SetState(SourceInfo source, bool up, string? error) { }
        public SourceState? GetState(string sourceId) => null;
        public void RecordExternalSample(MetricsSample sample) => Recorded = sample;
    }

    private sealed class FakeBroadcaster : IMetricsBroadcaster
    {
        public MetricsSample? Broadcast;
        public Task BroadcastAsync(MetricsSample sample, CancellationToken ct = default)
        {
            Broadcast = sample;
            return Task.CompletedTask;
        }
    }

    private static LlamaCppChunkTimings Timings() => new()
    {
        PredictedPerSecond = 37.3, PromptN = 15, CacheN = 7, PredictedN = 5,
    };

    [Fact]
    public async Task PublishAsync_RecordsASampleWithTheRightSourceIdAndLabel()
    {
        var metrics = new FakeMetricsService();
        var sut = new LlamaCppLiveMetricsPublisher(metrics, new FakeBroadcaster());

        await sut.PublishAsync("instance-7", "gemma-uncensored", Timings(), ctxSize: 4096);

        Assert.NotNull(metrics.Recorded);
        Assert.Equal("instance-7", metrics.Recorded!.SourceId);
        Assert.Equal("gemma-uncensored", metrics.Recorded.SourceLabel);
        Assert.True(metrics.Recorded.Up);
    }

    [Fact]
    public async Task PublishAsync_SetsGenTps_FromTheChunksOwnPredictedPerSecond()
    {
        var metrics = new FakeMetricsService();
        var sut = new LlamaCppLiveMetricsPublisher(metrics, new FakeBroadcaster());

        await sut.PublishAsync("instance-7", "label", Timings(), ctxSize: 4096);

        Assert.Equal(37.3, metrics.Recorded!.GenTps);
    }

    [Fact]
    public async Task PublishAsync_SetsKvPct_UsingTheChunksOwnPercentFormula()
    {
        var metrics = new FakeMetricsService();
        var sut = new LlamaCppLiveMetricsPublisher(metrics, new FakeBroadcaster());

        await sut.PublishAsync("instance-7", "label", Timings(), ctxSize: 4096);

        Assert.Equal(Timings().ToKvCachePercent(4096), metrics.Recorded!.KvPct);
    }

    [Fact]
    public async Task PublishAsync_BroadcastsTheExactSampleItRecorded()
    {
        var metrics = new FakeMetricsService();
        var broadcaster = new FakeBroadcaster();
        var sut = new LlamaCppLiveMetricsPublisher(metrics, broadcaster);

        await sut.PublishAsync("instance-7", "label", Timings(), ctxSize: 4096);

        Assert.Same(metrics.Recorded, broadcaster.Broadcast);
    }

    // ---- LastKnown: lets the proxy bracket a NEW request's start marker with the
    // previous request's real cache occupancy instead of a fabricated 0%. ----

    [Fact]
    public void LastKnown_IsNull_ForASourceThatHasNeverPublished()
    {
        var sut = new LlamaCppLiveMetricsPublisher(new FakeMetricsService(), new FakeBroadcaster());
        Assert.Null(sut.LastKnown("instance-7"));
    }

    [Fact]
    public async Task LastKnown_ReturnsTheRawTimingsFromTheMostRecentPublish_ForThatSource()
    {
        var sut = new LlamaCppLiveMetricsPublisher(new FakeMetricsService(), new FakeBroadcaster());

        await sut.PublishAsync("instance-7", "label", Timings(), ctxSize: 4096);

        Assert.Equal(Timings(), sut.LastKnown("instance-7"));
    }

    [Fact]
    public async Task LastKnown_IsPerSource_AndTracksOnlyTheMostRecentPublishForIt()
    {
        var sut = new LlamaCppLiveMetricsPublisher(new FakeMetricsService(), new FakeBroadcaster());
        var later = Timings() with { PredictedN = 20 };

        await sut.PublishAsync("instance-7", "label", Timings(), ctxSize: 4096);
        await sut.PublishAsync("instance-7", "label", later, ctxSize: 4096);
        await sut.PublishAsync("instance-9", "other", Timings(), ctxSize: 4096);

        Assert.Equal(later, sut.LastKnown("instance-7"));
        Assert.Equal(Timings(), sut.LastKnown("instance-9"));
    }
}
