using Dashboard.Application.Metrics;
using Dashboard.Core.Abstractions;
using Dashboard.Core.Models;

namespace Dashboard.Core.Tests;

/// <summary>In-memory fake of the ring-buffer half of ISampleStore -- the durable-append
/// half (Append's EF side-effects) is irrelevant to GetReqRateWindowed, which only ever
/// reads back via GetRecent.</summary>
sealed class FakeSampleStore : ISampleStore
{
    private readonly List<MetricsSample> _samples = new();

    public void Add(MetricsSample sample) => _samples.Add(sample);

    public void Append(MetricsSample sample) => _samples.Add(sample);

    public IReadOnlyList<MetricsSample> GetRecent(string sourceId, int maxPoints)
    {
        var matching = _samples.Where(s => s.SourceId == sourceId).ToList();
        var start = Math.Max(0, matching.Count - maxPoints);
        return matching[start..];
    }
}

public class MetricsServiceTests
{
    private static readonly DateTime Now = DateTime.UtcNow;

    private static MetricsSample Sample(string sourceId, DateTime ts, double? reqTps) => new()
    {
        SourceId = sourceId,
        Ts = ts,
        Up = true,
        ReqTps = reqTps,
    };

    private static MetricsService NewService(FakeSampleStore store) =>
        new(factory: null!, aggregator: null!, samples: store);

    [Fact]
    public void GetReqRateWindowed_AveragesSamplesWithinTheWindow()
    {
        var store = new FakeSampleStore();
        store.Add(Sample("s1", Now.AddSeconds(-20), 0));
        store.Add(Sample("s1", Now.AddSeconds(-10), 2));
        store.Add(Sample("s1", Now, 4));
        var service = NewService(store);

        var rate = service.GetReqRateWindowed("s1", TimeSpan.FromSeconds(30));

        Assert.NotNull(rate);
        Assert.Equal(2.0, rate!.Value, 1e-9); // (0 + 2 + 4) / 3
    }

    [Fact]
    public void GetReqRateWindowed_ExcludesSamplesOlderThanTheWindow()
    {
        var store = new FakeSampleStore();
        store.Add(Sample("s1", Now.AddSeconds(-60), 100)); // outside a 30s window
        store.Add(Sample("s1", Now.AddSeconds(-5), 2));
        var service = NewService(store);

        var rate = service.GetReqRateWindowed("s1", TimeSpan.FromSeconds(30));

        Assert.NotNull(rate);
        Assert.Equal(2.0, rate!.Value, 1e-9);
    }

    [Fact]
    public void GetReqRateWindowed_EmptyBufferReturnsNull_NotZero()
    {
        var service = NewService(new FakeSampleStore());

        Assert.Null(service.GetReqRateWindowed("unknown-source", TimeSpan.FromSeconds(30)));
    }

    [Fact]
    public void GetReqRateWindowed_AllNullReqTpsInWindowReturnsNull()
    {
        var store = new FakeSampleStore();
        store.Add(Sample("s1", Now, null)); // e.g. first sample after a counter reset
        var service = NewService(store);

        Assert.Null(service.GetReqRateWindowed("s1", TimeSpan.FromSeconds(30)));
    }

    [Fact]
    public void GetReqRateWindowed_SingleSampleReturnsItsOwnRate()
    {
        var store = new FakeSampleStore();
        store.Add(Sample("s1", Now, 7.5));
        var service = NewService(store);

        var rate = service.GetReqRateWindowed("s1", TimeSpan.FromSeconds(30));

        Assert.NotNull(rate);
        Assert.Equal(7.5, rate!.Value, 1e-9);
    }

    [Fact]
    public void GetReqRateWindowed_DoesNotMixSamplesFromOtherSources()
    {
        var store = new FakeSampleStore();
        store.Add(Sample("s1", Now, 2));
        store.Add(Sample("s2", Now, 100));
        var service = NewService(store);

        var rate = service.GetReqRateWindowed("s1", TimeSpan.FromSeconds(30));

        Assert.NotNull(rate);
        Assert.Equal(2.0, rate!.Value, 1e-9);
    }
}
