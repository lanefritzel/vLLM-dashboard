using Dashboard.Infrastructure.Scrapers;

namespace Dashboard.Core.Tests;

/// <summary>Spike (multi-engine support): llama.cpp's own <c>/metrics</c> Prometheus
/// body is a different, much sparser metric set than vLLM's -- no per-model labels
/// (llama-server only ever serves the one model it was started with), and no
/// TTFT/ITL/E2E/queue histograms or prefix-cache counters at all. Metric names below
/// are confirmed against a real run: <c>docker run ghcr.io/ggml-org/llama.cpp:server</c>
/// (build b12242-3b4d6ab3f, CPU-only, 2026-11-10) serving Qwen2.5-0.5B-Instruct-GGUF
/// with <c>--metrics</c> -- not guessed.</summary>
public class LlamaCppMetricsParserTests
{
    [Fact]
    public void Parse_ReadsRunningAndWaiting()
    {
        var body =
            "# HELP llamacpp:requests_processing Number of requests processing.\n" +
            "# TYPE llamacpp:requests_processing gauge\n" +
            "llamacpp:requests_processing 2\n" +
            "llamacpp:requests_deferred 1\n";

        var m = LlamaCppMetricsParser.Parse(body);

        Assert.NotNull(m.Running); Assert.Equal(2, m.Running!.Value, 1e-6);
        Assert.NotNull(m.Waiting); Assert.Equal(1, m.Waiting!.Value, 1e-6);
    }

    [Fact]
    public void Parse_ReadsCumulativeTokenCounters()
    {
        var body =
            "llamacpp:prompt_tokens_total 1000\n" +
            "llamacpp:tokens_predicted_total 2500\n";

        var m = LlamaCppMetricsParser.Parse(body);

        Assert.NotNull(m.PromptTokensTotal); Assert.Equal(1000, m.PromptTokensTotal!.Value, 1e-6);
        Assert.NotNull(m.GenTokensTotal); Assert.Equal(2500, m.GenTokensTotal!.Value, 1e-6);
    }

    [Fact]
    public void Parse_SkipsCommentsAndBlankLines()
    {
        var body = "# HELP llamacpp:requests_processing x\n\n# TYPE llamacpp:requests_processing gauge\nllamacpp:requests_processing 0\n";
        var m = LlamaCppMetricsParser.Parse(body);
        Assert.NotNull(m.Running);
        Assert.Equal(0, m.Running!.Value, 1e-6);
    }

    [Fact]
    public void Parse_IgnoresUnknownMetricNames()
    {
        var body = "llamacpp:n_decode_total 42\nsome_other_metric 7\n";
        var m = LlamaCppMetricsParser.Parse(body);
        Assert.Null(m.Running);
        Assert.Null(m.GenTokensTotal);
    }

    [Fact]
    public void Parse_EmptyOrNullBody_ReturnsAllNulls()
    {
        Assert.Null(LlamaCppMetricsParser.Parse(null).Running);
        Assert.Null(LlamaCppMetricsParser.Parse("").Running);
    }

    /// <summary>The whole point of this test: llama.cpp has nothing resembling
    /// vLLM's TTFT/ITL/E2E/queue histograms or prefix-cache counters in its
    /// exporter, so these always stay null here -- never guessed or backfilled.
    /// KvPct is here too: a real llama-server's /metrics (grepped directly, see
    /// class doc) has no KV-cache usage metric under any name -- an earlier
    /// version of this parser guessed "llamacpp:kv_cache_usage_ratio" and was
    /// wrong, confirmed once an actual server was run.</summary>
    [Fact]
    public void Parse_NeverPopulatesLatencyHistogramsPrefixCacheOrKvUsageFields()
    {
        var body =
            "llamacpp:requests_processing 1\n" +
            "llamacpp:prompt_tokens_total 10\n" +
            "llamacpp:tokens_predicted_total 20\n";

        var m = LlamaCppMetricsParser.Parse(body);

        Assert.Null(m.KvPct);
        Assert.Null(m.TtftSum); Assert.Null(m.TtftCount);
        Assert.Null(m.ItlSum); Assert.Null(m.ItlCount);
        Assert.Null(m.E2eSum); Assert.Null(m.E2eCount);
        Assert.Null(m.QueueSum); Assert.Null(m.QueueCount);
        Assert.Null(m.PrefixHitsTotal); Assert.Null(m.PrefixQueriesTotal);
        Assert.Null(m.PreemptionsTotal); Assert.Null(m.ReqCountTotal);
    }
}
