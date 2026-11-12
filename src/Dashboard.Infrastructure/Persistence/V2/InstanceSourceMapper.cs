using Dashboard.Core.Models;

namespace Dashboard.Infrastructure.Persistence.V2;

/// <summary>An instance's <see cref="SourceInfo"/> for the metrics scraper (P3). vLLM scrapes
/// <see cref="SourceInfo.MetricsUrl"/>; llama.cpp serves metrics, health and inference on one
/// port and its source reads <see cref="SourceInfo.InferenceUrl"/>, so the base URL goes in both.</summary>
public static class InstanceSourceMapper
{
    public static SourceInfo ToSourceInfo(Instance instance, string sourceId, string baseUrl) => new()
    {
        Id = sourceId,
        Label = instance.Label,
        Type = instance.EngineType,
        MetricsUrl = baseUrl,
        InferenceUrl = baseUrl,
        ScrapeIntervalSeconds = Math.Max(1, instance.PollIntervalMs / 1000),
    };
}
