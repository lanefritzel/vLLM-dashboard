namespace Dashboard.Core.Models;

/// <summary>
/// One aggregated sample: gauge values at <see cref="Ts"/> plus rates/averages
/// computed against the previous snapshot. This is what the ring buffer stores
/// and what is broadcast to the UI and persisted.
/// </summary>
public sealed record MetricsSample
{
    public DateTime Ts { get; init; }
    public string SourceId { get; init; } = "";
    public string SourceLabel { get; init; } = "";
    public string? ModelName { get; init; }

    public int Running { get; init; }
    public int Waiting { get; init; }
    public double? KvPct { get; init; }

    public double? GenTps { get; init; }
    public double? PromptTps { get; init; }
    public double? ReqTps { get; init; }

    public double? TtftAvgSec { get; init; }
    public double? ItlAvgSec { get; init; }
    public double? E2eAvgSec { get; init; }
    public double? QueueAvgSec { get; init; }

    public double? PrefixHitRatio { get; init; }
    public long? PreemptionsTotal { get; init; }

    public bool Up { get; init; }
    public string? Error { get; init; }

    // Ollama-only fields.
    public IReadOnlyList<string>? LoadedModels { get; init; }
    public long? TotalVramBytes { get; init; }
    public int? CatalogCount { get; init; }
}
