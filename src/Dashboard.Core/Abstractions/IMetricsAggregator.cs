using Dashboard.Core.Models;

namespace Dashboard.Core.Abstractions;

/// <summary>
/// Turns a raw snapshot (plus the previous raw snapshot for rate computation)
/// into an aggregated sample (rates, histogram averages, reset-safe).
/// </summary>
public interface IMetricsAggregator
{
    /// <param name="prevRaw">Previous raw snapshot (null for first sample).</param>
    /// <param name="raw">Current raw snapshot.</param>
    /// <param name="now">Sample timestamp.</param>
    MetricsSample Aggregate(MetricsSnapshot? prevRaw, MetricsSnapshot raw, DateTime now);
}
