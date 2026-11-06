using System.Collections.Concurrent;
using Dashboard.Core.Abstractions;
using Dashboard.Core.Models;

namespace Dashboard.Application.Metrics;

/// <summary>
/// Singleton facade. Scrapes a source, aggregates against the previous raw
/// snapshot, stores the sample, and tracks up/error state per source.
/// </summary>
public sealed class MetricsService : IMetricsService
{
    private readonly IMetricsSourceFactory _factory;
    private readonly IMetricsAggregator _aggregator;
    private readonly ISampleStore _samples;

    private readonly ConcurrentDictionary<string, MetricsSnapshot> _lastRaw = new();
    private readonly ConcurrentDictionary<string, SourceState> _states = new();

    public MetricsService(IMetricsSourceFactory factory, IMetricsAggregator aggregator, ISampleStore samples)
    {
        _factory = factory;
        _aggregator = aggregator;
        _samples = samples;
    }

    public async Task<MetricsSample> ScrapeAndStoreAsync(SourceInfo source, CancellationToken ct = default)
    {
        var scraper = _factory.Create(source);
        var raw = await scraper.ScrapeAsync(ct);

        var now = DateTime.UtcNow;
        _lastRaw.TryGetValue(source.Id, out var prev);
        var sample = _aggregator.Aggregate(prev, raw, now) with
        {
            SourceId = source.Id,
            SourceLabel = source.Label,
        };

        _lastRaw[source.Id] = raw;
        _samples.Append(sample);

        SetState(source, raw.Up, raw.Error);
        return sample;
    }

    public void SetState(SourceInfo source, bool up, string? error)
    {
        _states[source.Id] = new SourceState
        {
            Id = source.Id,
            Label = source.Label,
            Up = up,
            Error = error,
            LastSeen = up ? DateTime.UtcNow : null,
        };
    }

    public SourceState? GetState(string sourceId) =>
        _states.TryGetValue(sourceId, out var s) ? s : null;

    /// <summary>State for every known source (for the header selector).</summary>
    public IReadOnlyCollection<SourceState> GetStates() => _states.Values.ToList();
}
