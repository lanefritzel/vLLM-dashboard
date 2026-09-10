using System.Globalization;

namespace Dashboard.Infrastructure.Scrapers;

/// <summary>Raw parsed metric values for a single model, pre-aggregation.</summary>
public sealed class ParsedModelMetrics
{
    public string ModelName { get; init; } = "";

    public double? Running { get; set; }
    public double? Waiting { get; set; }
    public double? KvPct { get; set; }

    public double? GenTokensTotal { get; set; }
    public double? PromptTokensTotal { get; set; }
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

    public int PopulatedCount { get; set; }
}

/// <summary>Result of parsing one Prometheus /metrics body.</summary>
public sealed class PrometheusParseResult
{
    public Dictionary<string, ParsedModelMetrics> Models { get; } = new(StringComparer.Ordinal);

    public ParsedModelMetrics? PickPrimary()
    {
        ParsedModelMetrics? best = null;
        foreach (var m in Models.Values)
        {
            if (best is null || m.PopulatedCount > best.PopulatedCount)
                best = m;
        }
        return best;
    }
}

/// <summary>
/// Parses Prometheus text exposition format into per-model metric values.
/// Label-aware: groups by <c>model_name</c>, sums across <c>engine</c> (and any other
/// label dimension). Primary metric names win over legacy fallbacks. Never throws on
/// malformed input.
/// </summary>
public static class PrometheusParser
{
    public static PrometheusParseResult Parse(string? text)
    {
        var result = new PrometheusParseResult();
        if (string.IsNullOrWhiteSpace(text))
            return result;

        // Accumulator: (model, metricKey) -> summed value (sums across label dims).
        var accum = new Dictionary<(string Model, string Key), double>();

        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            if (line.Length == 0 || line[0] == '#')
                continue;

            var parsed = TryParseDataLine(line);
            if (parsed is null)
                continue;

            var (name, value, modelName) = parsed.Value;
            if (name.EndsWith("_bucket", StringComparison.Ordinal))
                continue; // buckets not used at MVP

            var model = string.IsNullOrEmpty(modelName) ? "_none" : modelName;
            var dictKey = (model, name);
            if (accum.TryGetValue(dictKey, out var existing))
                accum[dictKey] = existing + value;
            else
                accum[dictKey] = value;
        }

        foreach (var ((model, key), value) in accum)
        {
            var m = result.Models.TryGetValue(model, out var existing)
                ? existing
                : result.Models[model] = new ParsedModelMetrics { ModelName = model == "_none" ? "" : model };

            if (TryApply(key, m, value))
                m.PopulatedCount++;
        }

        return result;
    }

    private static (string Name, double Value, string ModelName)? TryParseDataLine(string line)
    {
        int labelStart = line.IndexOf('{');
        string head;
        string labelsBlock = "";
        string tail;

        if (labelStart >= 0)
        {
            int labelEnd = line.IndexOf('}', labelStart);
            if (labelEnd < 0)
                return null;
            head = line.Substring(0, labelStart).Trim();
            labelsBlock = line.Substring(labelStart + 1, labelEnd - labelStart - 1);
            tail = line.Substring(labelEnd + 1).Trim();
        }
        else
        {
            head = line;
            tail = "";
        }

        if (head.Length == 0)
            return null;

        int space = head.IndexOfAny(new[] { ' ', '\t' });
        string name = space < 0 ? head : head.Substring(0, space).Trim();
        if (name.Length == 0)
            return null;

        string valueText = tail.Length > 0 ? tail : (space < 0 ? "" : head.Substring(space + 1).Trim());
        if (!double.TryParse(valueText, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
            return null; // NaN/+Inf/empty -> skip

        string modelName = ExtractLabel(labelsBlock, "model_name");
        return (name, value, modelName);
    }

    private static string ExtractLabel(string labelsBlock, string labelName)
    {
        if (labelsBlock.Length == 0)
            return "";
        var search = labelName + "=\"";
        int idx = labelsBlock.IndexOf(search, StringComparison.Ordinal);
        if (idx < 0)
            return "";
        int start = idx + search.Length;
        int end = labelsBlock.IndexOf('"', start);
        if (end < 0)
            return "";
        return labelsBlock.Substring(start, end - start);
    }

    private static bool TryApply(string key, ParsedModelMetrics m, double value)
    {
        switch (key)
        {
            case "vllm:num_requests_running": m.Running = value; return true;
            case "vllm:num_requests_waiting": m.Waiting = value; return true;

            case "vllm:kv_cache_usage_perc": m.KvPct = value; return true;
            case "vllm:gpu_cache_usage_avg":
                m.KvPct ??= value * 100.0; // legacy 0-1 scale, only if primary absent
                return true;

            case "vllm:generation_tokens_total": m.GenTokensTotal = value; return true;
            case "vllm:num_tokens_generated": m.GenTokensTotal ??= value; return true;
            case "vllm:prompt_tokens_total": m.PromptTokensTotal = value; return true;
            case "vllm:num_prompt_tokens": m.PromptTokensTotal ??= value; return true;

            case "vllm:e2e_request_latency_seconds_count":
                m.ReqCountTotal ??= value;
                m.E2eCount = value;
                return true;
            case "vllm:request_success_total":
                m.ReqCountTotal ??= value; return true;

            case "vllm:time_to_first_token_seconds_sum": m.TtftSum = value; return true;
            case "vllm:time_to_first_token_seconds_count": m.TtftCount = value; return true;

            case "vllm:request_time_per_output_token_seconds_sum": m.ItlSum ??= value; return true;
            case "vllm:request_time_per_output_token_seconds_count": m.ItlCount ??= value; return true;
            case "vllm:inter_token_latency_seconds_sum": m.ItlSum ??= value; return true;
            case "vllm:inter_token_latency_seconds_count": m.ItlCount ??= value; return true;

            case "vllm:e2e_request_latency_seconds_sum": m.E2eSum = value; return true;
            case "vllm:request_queue_time_seconds_sum": m.QueueSum = value; return true;
            case "vllm:request_queue_time_seconds_count": m.QueueCount = value; return true;

            case "vllm:prefix_cache_hits_total": m.PrefixHitsTotal = value; return true;
            case "vllm:prefix_cache_queries_total": m.PrefixQueriesTotal = value; return true;
            case "vllm:num_preemptions_total": m.PreemptionsTotal = value; return true;
        }
        return false;
    }
}
