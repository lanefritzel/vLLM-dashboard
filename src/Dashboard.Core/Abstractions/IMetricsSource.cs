using Dashboard.Core.Models;

namespace Dashboard.Core.Abstractions;

/// <summary>Strategy: scrapes one source and reports its health.</summary>
public interface IMetricsSource
{
    SourceType Type { get; }
    Task<MetricsSnapshot> ScrapeAsync(CancellationToken ct = default);
    Task<bool> HealthAsync(CancellationToken ct = default);
}
