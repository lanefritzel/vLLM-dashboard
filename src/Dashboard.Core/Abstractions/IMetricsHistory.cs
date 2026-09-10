using Dashboard.Core.Models;

namespace Dashboard.Core.Abstractions;

/// <summary>Queries historical samples (raw for recent windows, rollups for older).</summary>
public interface IMetricsHistory
{
    Task<IReadOnlyList<MetricsSample>> GetAsync(
        DateTime from, DateTime to, string? sourceId, string? modelName, CancellationToken ct = default);

    Task<IReadOnlyList<HistorySummary>> SummarizeAsync(
        DateTime from, DateTime to, string? sourceId, string? modelName, CancellationToken ct = default);
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
