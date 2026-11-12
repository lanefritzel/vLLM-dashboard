using System.Text.Json;
using Dashboard.Core.Models;

namespace Dashboard.Infrastructure.Proxy;

/// <summary>
/// Parses the <c>timings</c> object llama-server embeds in a streamed completion chunk
/// -- but only when the request body carried <c>"timings_per_token": true</c> (see
/// <see cref="ModelProxyService.InjectTimingsPerToken"/>). Without that flag, a chunk
/// only carries <c>timings</c> in the FINAL one, confirmed against a real server
/// (serenity, build b12282-aa75ff323): polled through an entire real generation, not a
/// single snapshot. Never throws on malformed/missing input, same contract as
/// <see cref="Dashboard.Infrastructure.Scrapers.LlamaCppMetricsParser"/>.
/// </summary>
public static class LlamaCppTimingsParser
{
    public static LlamaCppChunkTimings? TryParse(string? chunkJson)
    {
        if (string.IsNullOrWhiteSpace(chunkJson))
            return null;

        try
        {
            using var doc = JsonDocument.Parse(chunkJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object ||
                !doc.RootElement.TryGetProperty("timings", out var timings) ||
                timings.ValueKind != JsonValueKind.Object)
                return null;

            return new LlamaCppChunkTimings
            {
                PredictedPerSecond = GetDouble(timings, "predicted_per_second"),
                PromptN = GetInt(timings, "prompt_n"),
                CacheN = GetInt(timings, "cache_n"),
                PredictedN = GetInt(timings, "predicted_n"),
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static double GetDouble(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : 0.0;

    private static int GetInt(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : 0;
}
