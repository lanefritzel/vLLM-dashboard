using Dashboard.Application.Metrics;
using Dashboard.Core.Abstractions;
using Dashboard.Core.Models;

namespace Dashboard.Core.Tests;

/// <summary>P7: <c>RecordExternalSample</c> is for a caller that already HAS a real sample
/// (the proxy, observing llama.cpp's own live per-chunk <c>timings</c> -- see
/// LlamaCppTimingsParserTests) rather than one <c>MetricsService</c> scraped itself. It must
/// do exactly what <c>ScrapeAndStoreAsync</c> does after its own scrape (store + track
/// up/down state) so llama.cpp's proxy-observed samples feed the same 30-day rollups and
/// GetState-backed UI as vLLM's polled ones -- not a second, inconsistent code path.
/// Broadcasting to SignalR is deliberately NOT this method's job, same as it isn't
/// ScrapeAndStoreAsync's -- the caller (here, the as-yet-unbuilt live-metrics publisher)
/// decides when/whether to broadcast, exactly as InstanceMetricsPollingService already
/// does for the scrape-loop case.</summary>
public class MetricsServiceTests
{
    private sealed class FakeSampleStore : ISampleStore
    {
        public List<MetricsSample> Appended { get; } = new();
        public void Append(MetricsSample sample) => Appended.Add(sample);
        public IReadOnlyList<MetricsSample> GetRecent(string sourceId, int maxPoints) =>
            Appended.Where(s => s.SourceId == sourceId).ToList();
    }

    private static MetricsService NewSut(out FakeSampleStore store)
    {
        store = new FakeSampleStore();
        // No scrape ever happens in these tests, so a real factory/aggregator is never
        // invoked -- null! is safe here (ScrapeAndStoreAsync's own path is untested by
        // this file; see its pre-existing coverage elsewhere for that).
        return new MetricsService(null!, null!, store);
    }

    [Fact]
    public void RecordExternalSample_AppendsTheSampleToTheStoreUnchanged()
    {
        var sut = NewSut(out var store);
        var sample = new MetricsSample { SourceId = "instance-7", SourceLabel = "llama.cpp", Up = true, GenTps = 37.3 };

        sut.RecordExternalSample(sample);

        Assert.Same(sample, Assert.Single(store.Appended));
    }

    [Fact]
    public void RecordExternalSample_MarksTheSourceUp_WhenTheSampleIsUp()
    {
        var sut = NewSut(out _);
        sut.RecordExternalSample(new MetricsSample { SourceId = "instance-7", SourceLabel = "llama.cpp", Up = true });

        var state = sut.GetState("instance-7");
        Assert.NotNull(state);
        Assert.True(state!.Up);
        Assert.NotNull(state.LastSeen);
    }

    [Fact]
    public void RecordExternalSample_MarksTheSourceDown_WhenTheSampleIsDown()
    {
        var sut = NewSut(out _);
        sut.RecordExternalSample(new MetricsSample { SourceId = "instance-7", SourceLabel = "llama.cpp", Up = false, Error = "stream ended" });

        var state = sut.GetState("instance-7");
        Assert.NotNull(state);
        Assert.False(state!.Up);
        Assert.Equal("stream ended", state.Error);
        Assert.Null(state.LastSeen);
    }
}
