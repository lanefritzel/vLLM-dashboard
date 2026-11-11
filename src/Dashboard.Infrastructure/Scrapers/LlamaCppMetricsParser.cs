using System.Globalization;

namespace Dashboard.Infrastructure.Scrapers;

/// <summary>Raw parsed values from llama.cpp's own <c>/metrics</c> body. Unlike
/// <see cref="PrometheusParser"/>, there's no per-model grouping: a llama-server
/// process serves exactly one model, so metric lines carry no model_name label.</summary>
public sealed class LlamaCppParsedMetrics
{
    public double? Running { get; set; }
    public double? Waiting { get; set; }
    public double? GenTokensTotal { get; set; }
    public double? PromptTokensTotal { get; set; }

    // Never populated -- llama.cpp's exporter has no equivalent (confirmed
    // against a real --metrics run, not just guessed -- see LlamaCppMetricsParser).
    // KvPct included: no KV-cache usage metric exists under any name.
    public double? KvPct { get; set; }
    public double? ReqCountTotal { get; set; }
    public double? PreemptionsTotal { get; set; }
    public double? PrefixHitsTotal { get; set; }
    public double? PrefixQueriesTotal { get; set; }
    public double? TtftSum { get; set; }
    public double? TtftCount { get; set; }
    public double? ItlSum { get; set; }
    public double? ItlCount { get; set; }
    public double? E2eSum { get; set; }
    public double? E2eCount { get; set; }
    public double? QueueSum { get; set; }
    public double? QueueCount { get; set; }
}

/// <summary>
/// Spike (multi-engine support proposal): parses llama.cpp's Prometheus
/// <c>/metrics</c> exposition body. Metric names confirmed against a real run --
/// <c>docker run ghcr.io/ggml-org/llama.cpp:server</c> (build b12242-3b4d6ab3f,
/// CPU-only) serving Qwen2.5-0.5B-Instruct-GGUF with <c>--metrics</c>, 2026-11-10
/// -- not just general knowledge. There is no KV-cache usage metric under any
/// name on that build, unlike vLLM's kv_cache_usage_perc; an earlier guess at
/// "llamacpp:kv_cache_usage_ratio" was wrong and has been removed. Never throws
/// on malformed input, same contract as <see cref="PrometheusParser"/>.
/// </summary>
public static class LlamaCppMetricsParser
{
    public static LlamaCppParsedMetrics Parse(string? text)
    {
        var result = new LlamaCppParsedMetrics();
        if (string.IsNullOrWhiteSpace(text))
            return result;

        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            if (line.Length == 0 || line[0] == '#')
                continue;

            int space = line.IndexOfAny(new[] { ' ', '\t' });
            if (space < 0)
                continue;
            var name = line[..space].Trim();
            var valueText = line[(space + 1)..].Trim();
            if (!double.TryParse(valueText, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
                continue;

            switch (name)
            {
                case "llamacpp:requests_processing": result.Running = value; break;
                case "llamacpp:requests_deferred": result.Waiting = value; break;
                case "llamacpp:prompt_tokens_total": result.PromptTokensTotal = value; break;
                case "llamacpp:tokens_predicted_total": result.GenTokensTotal = value; break;
            }
        }

        return result;
    }
}
