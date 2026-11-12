using Dashboard.Core.Models;

namespace Dashboard.Core.Abstractions;

/// <summary>Facade: scrape a source, aggregate, store, and surface state changes.</summary>
public interface IMetricsService
{
    Task<MetricsSample> ScrapeAndStoreAsync(SourceInfo source, CancellationToken ct = default);
    void SetState(SourceInfo source, bool up, string? error);
    SourceState? GetState(string sourceId);

    /// <summary>Stores and tracks the up/down state for a sample a caller already has
    /// (e.g. the proxy, observing llama.cpp's own live per-chunk data) rather than one
    /// obtained by scraping a <see cref="SourceInfo"/>. Does exactly what <see
    /// cref="ScrapeAndStoreAsync"/> does after its own scrape — same storage, same state
    /// tracking, so an externally-sourced sample feeds the same rollups/UI a scraped one
    /// does. Deliberately does not broadcast; that decision belongs to the caller, same
    /// as it already does for the scrape-loop case.</summary>
    void RecordExternalSample(MetricsSample sample);
}
