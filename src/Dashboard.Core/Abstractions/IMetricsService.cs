using Dashboard.Core.Models;

namespace Dashboard.Core.Abstractions;

/// <summary>Facade: scrape a source, aggregate, store, and surface state changes.</summary>
public interface IMetricsService
{
    Task<MetricsSample> ScrapeAndStoreAsync(SourceInfo source, CancellationToken ct = default);
    void SetState(SourceInfo source, bool up, string? error);
    SourceState? GetState(string sourceId);

    /// <summary>
    /// Request rate averaged over the last <paramref name="window"/> of samples, not the
    /// raw 1-poll-interval point rate -- with sparse completions, a true instantaneous
    /// rate reads 0 almost all the time even while requests are steadily completing.
    /// Null if there's no data in the window yet (distinct from a real 0 rate).
    /// </summary>
    double? GetReqRateWindowed(string sourceId, TimeSpan window);
}
