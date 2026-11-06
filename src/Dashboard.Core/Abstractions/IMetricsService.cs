using Dashboard.Core.Models;

namespace Dashboard.Core.Abstractions;

/// <summary>Facade: scrape a source, aggregate, store, and surface state changes.</summary>
public interface IMetricsService
{
    Task<MetricsSample> ScrapeAndStoreAsync(SourceInfo source, CancellationToken ct = default);
    void SetState(SourceInfo source, bool up, string? error);
    SourceState? GetState(string sourceId);
}
