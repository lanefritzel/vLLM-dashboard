using Dashboard.Core.Abstractions;
using Dashboard.Core.Models;

namespace Dashboard.Infrastructure.Scrapers;

/// <summary>
/// Deterministic-ish random-walk generator used for mock sources and local testing.
/// Produces cumulative counters (monotonic) so the aggregator yields real rates,
/// plus random-walk gauges.
/// </summary>
public sealed class MockMetricsSource : IMetricsSource
{
    private readonly Random _rng;
    private DateTime _lastTs = DateTime.UtcNow;

    private double _running, _waiting, _kv;
    private double _genTotal, _promptTotal, _reqCount;
    private double _ttftSum, _ttftCount, _itlSum, _itlCount;
    private double _e2eSum, _e2eCount, _queueSum, _queueCount;
    private double _prefixHits, _prefixQueries, _preempt;

    public MockMetricsSource(int seed = 0)
    {
        _rng = new Random(seed == 0 ? Environment.TickCount : seed);
        _running = _rng.Next(0, 5);
        _waiting = _rng.Next(0, 3);
        _kv = 20 + _rng.Next(0, 40);
    }

    public SourceType Type => SourceType.Mock;

    public Task<MetricsSnapshot> ScrapeAsync(CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        double dt = Math.Max(0.05, (now - _lastTs).TotalSeconds);
        _lastTs = now;

        // Random walks for gauges.
        _running = Clamp(_running + (_rng.NextDouble() - 0.5) * 2.0, 0, 8);
        _waiting = Clamp(_waiting + (_rng.NextDouble() - 0.5) * 1.5, 0, 4);
        _kv = Clamp(_kv + (_rng.NextDouble() - 0.5) * 10.0, 20, 95);

        // Simulated throughput (tok/s) with bursts.
        double genRate = _rng.NextDouble() * 1500.0;
        double promptRate = _rng.NextDouble() * 500.0;
        double reqRate = _rng.NextDouble() * 3.0 + 0.05;
        double newReqs = reqRate * dt;

        _genTotal += genRate * dt;
        _promptTotal += promptRate * dt;
        _reqCount += newReqs;

        // Latency histograms: cumulative sum + count.
        double ttft = 0.08 + _rng.NextDouble() * 0.7;   // 80ms..800ms
        double itl = 0.01 + _rng.NextDouble() * 0.05;   // 10..60ms
        double e2e = 0.5 + _rng.NextDouble() * 7.5;     // 0.5..8s
        double queue = 0.0 + _rng.NextDouble() * 0.3;   // 0..300ms
        _ttftSum += ttft * newReqs; _ttftCount += newReqs;
        _itlSum += itl * newReqs; _itlCount += newReqs;
        _e2eSum += e2e * newReqs; _e2eCount += newReqs;
        _queueSum += queue * newReqs; _queueCount += newReqs;

        double q = _rng.NextDouble() * newReqs * 10.0;
        _prefixQueries += q;
        _prefixHits += q * (0.5 + _rng.NextDouble() * 0.4);
        if (_rng.NextDouble() < 0.05) _preempt += 1;

        var snap = new MetricsSnapshot
        {
            Ts = now,
            Up = true,
            ModelName = "mock-model",
            Running = _running,
            Waiting = _waiting,
            KvPct = _kv,
            GenTokensTotal = _genTotal,
            PromptTokensTotal = _promptTotal,
            ReqCountTotal = _reqCount,
            PreemptionsTotal = _preempt,
            PrefixHitsTotal = _prefixHits,
            PrefixQueriesTotal = _prefixQueries,
            TtftSum = _ttftSum,
            TtftCount = _ttftCount,
            ItlSum = _itlSum,
            ItlCount = _itlCount,
            E2eSum = _e2eSum,
            E2eCount = _e2eCount,
            QueueSum = _queueSum,
            QueueCount = _queueCount,
        };
        return Task.FromResult(snap);
    }

    public Task<bool> HealthAsync(CancellationToken ct = default) => Task.FromResult(true);

    private static double Clamp(double v, double lo, double hi) => v < lo ? lo : v > hi ? hi : v;
}
