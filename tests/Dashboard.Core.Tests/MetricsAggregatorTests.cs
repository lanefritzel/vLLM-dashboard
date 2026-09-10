using Dashboard.Core.Models;
using Dashboard.Infrastructure.Scrapers;

namespace Dashboard.Core.Tests;

public class MetricsAggregatorTests
{
    private static readonly DateTime T0 = DateTime.UtcNow;
    private static readonly DateTime T1 = T0.AddSeconds(10);

    [Fact]
    public void Down_Sample_PropagatesError_AndFlagsDown()
    {
        var down = new MetricsSnapshot { Ts = T1, Up = false, ModelName = "m", Error = "timeout" };
        var sample = new MetricsAggregator().Aggregate(null, down, T1);
        Assert.False(sample.Up);
        Assert.Equal("timeout", sample.Error);
    }

    [Fact]
    public void FirstSample_ProducesNullRates_ButCumulativeAverages()
    {
        var raw = new MetricsSnapshot
        {
            Ts = T0, Up = true, ModelName = "m",
            Running = 2, Waiting = 1, KvPct = 0.5,
            GenTokensTotal = 100, TtftSum = 2.0, TtftCount = 2,
        };
        var sample = new MetricsAggregator().Aggregate(null, raw, T0);
        Assert.True(sample.Up);
        Assert.Null(sample.GenTps);      // no previous -> no rate
        Assert.Null(sample.PromptTps);
        Assert.NotNull(sample.TtftAvgSec); Assert.Equal(1.0, sample.TtftAvgSec!.Value, 1e-6); // cumulative avg 2/2
        Assert.Equal(2, sample.Running);
        Assert.Equal(1, sample.Waiting);
    }

    [Fact]
    public void Rate_CountersComputePerSecond()
    {
        var prev = new MetricsSnapshot { Ts = T0, Up = true, ModelName = "m", GenTokensTotal = 100, PromptTokensTotal = 50 };
        var cur = new MetricsSnapshot { Ts = T1, Up = true, ModelName = "m", GenTokensTotal = 1200, PromptTokensTotal = 500 };
        var sample = new MetricsAggregator().Aggregate(prev, cur, T1);
        Assert.NotNull(sample.GenTps); Assert.Equal(110, sample.GenTps!.Value, 1e-6);    // (1200-100)/10
        Assert.NotNull(sample.PromptTps); Assert.Equal(45, sample.PromptTps!.Value, 1e-6); // (500-50)/10
    }

    [Fact]
    public void Rate_CounterResetYieldsNull()
    {
        var prev = new MetricsSnapshot { Ts = T0, Up = true, ModelName = "m", GenTokensTotal = 1200 };
        var cur = new MetricsSnapshot { Ts = T1, Up = true, ModelName = "m", GenTokensTotal = 100 };
        var sample = new MetricsAggregator().Aggregate(prev, cur, T1);
        Assert.Null(sample.GenTps); // 100 < 1200 -> reset
    }

    [Fact]
    public void HistAvg_UsesSumAndCountDeltas()
    {
        var prev = new MetricsSnapshot { Ts = T0, Up = true, ModelName = "m", TtftSum = 1.0, TtftCount = 2 };
        var cur = new MetricsSnapshot { Ts = T1, Up = true, ModelName = "m", TtftSum = 3.0, TtftCount = 4 };
        var sample = new MetricsAggregator().Aggregate(prev, cur, T1);
        Assert.NotNull(sample.TtftAvgSec); Assert.Equal(1.0, sample.TtftAvgSec!.Value, 1e-6); // (3-1)/(4-2)
    }

    [Fact]
    public void HistAvg_NoNewSamplesFallsBackToCumulativeAverage()
    {
        var prev = new MetricsSnapshot { Ts = T0, Up = true, ModelName = "m", TtftSum = 2.0, TtftCount = 4 };
        var cur = new MetricsSnapshot { Ts = T1, Up = true, ModelName = "m", TtftSum = 2.0, TtftCount = 4 };
        var sample = new MetricsAggregator().Aggregate(prev, cur, T1);
        Assert.NotNull(sample.TtftAvgSec); Assert.Equal(0.5, sample.TtftAvgSec!.Value, 1e-6); // 2/4
    }

    [Fact]
    public void PrefixHitRatio_UsesDeltas()
    {
        var prev = new MetricsSnapshot { Ts = T0, Up = true, ModelName = "m", PrefixHitsTotal = 10, PrefixQueriesTotal = 20 };
        var cur = new MetricsSnapshot { Ts = T1, Up = true, ModelName = "m", PrefixHitsTotal = 20, PrefixQueriesTotal = 40 };
        var sample = new MetricsAggregator().Aggregate(prev, cur, T1);
        Assert.NotNull(sample.PrefixHitRatio); Assert.Equal(0.5, sample.PrefixHitRatio!.Value, 1e-6); // (20-10)/(40-20)
    }
}
