using Dashboard.Core.Models;

namespace Dashboard.Core.Abstractions;

/// <summary>Queries historical samples (raw for recent windows, rollups for older).</summary>
public interface IMetricsHistory
{
    Task<IReadOnlyList<MetricsSample>> GetAsync(
        DateTime from, DateTime to, string? sourceId, string? modelName, CancellationToken ct = default);

    Task<IReadOnlyList<HistorySummary>> SummarizeAsync(
        DateTime from, DateTime to, string? sourceId, string? modelName, CancellationToken ct = default);

    /// <summary>Peak/avg/low generation throughput over the given rolling window,
    /// computed from raw per-scrape samples (not hourly rollup averages) and only
    /// over samples where generation was actually happening (GenTps > 0) — an idle
    /// source reading 0 shouldn't drag the average down or count as the "low".
    /// Returns null if the source has no qualifying samples in the window (e.g. it's
    /// never generated anything, or the window exceeds the configured retention).</summary>
    Task<GenerationThroughputStats?> GetActiveGenerationStatsAsync(
        string sourceId, TimeSpan window, CancellationToken ct = default);
}

public sealed record GenerationThroughputStats
{
    public double Peak { get; init; }
    public double Avg { get; init; }
    public double Low { get; init; }
}

public sealed record HistorySummary
{
    public string ModelName { get; init; } = "";
    public long Requests { get; init; }
    public long TokensGenerated { get; init; }
    public long TokensPrompt { get; init; }
    public double? AvgTtftSec { get; init; }
    public double? AvgE2eSec { get; init; }
    public double? PeakKvPct { get; init; }
    public int Samples { get; init; }
}
