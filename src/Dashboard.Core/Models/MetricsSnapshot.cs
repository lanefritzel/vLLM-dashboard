namespace Dashboard.Core.Models;

/// <summary>
/// Raw parsed values from a single scrape, before rate/aggregation.
/// Counters/histograms carry cumulative values; the aggregator turns them into rates.
/// </summary>
public sealed record MetricsSnapshot
{
    public DateTime Ts { get; init; }
    public bool Up { get; init; }
    public string? Error { get; init; }

    // Gauges (already summed over engines).
    public double? Running { get; init; }
    public double? Waiting { get; init; }
    public double? KvPct { get; init; }

    // Cumulative counters (raw).
    public double? GenTokensTotal { get; init; }
    public double? PromptTokensTotal { get; init; }
    public double? ReqCountTotal { get; init; }
    public double? PreemptionsTotal { get; init; }
    public double? PrefixHitsTotal { get; init; }
    public double? PrefixQueriesTotal { get; init; }

    // Histogram _sum / _count (raw cumulative).
    public double? TtftSum { get; init; }
    public double? TtftCount { get; init; }
    public double? ItlSum { get; init; }
    public double? ItlCount { get; init; }
    public double? E2eSum { get; init; }
    public double? E2eCount { get; init; }
    public double? QueueSum { get; init; }
    public double? QueueCount { get; init; }

    public string? ModelName { get; init; }

    // Ollama-only fields (scaffold).
    public IReadOnlyList<string>? LoadedModels { get; init; }
    public long? TotalVramBytes { get; init; }
    public int? CatalogCount { get; init; }
}
