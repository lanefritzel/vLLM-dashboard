using Dashboard.Infrastructure.Scrapers;

namespace Dashboard.Core.Tests;

public class PrometheusParserTests
{
    [Fact]
    public void Parse_SumsAcrossEngines_GroupsByModel()
    {
        var body =
            "# HELP vllm:num_requests_running Number of running requests.\n" +
            "# TYPE vllm:num_requests_running gauge\n" +
            "vllm:num_requests_running{model_name=\"qwen3.8-vllm\",engine=\"0\"} 3\n" +
            "vllm:num_requests_running{model_name=\"qwen3.8-vllm\",engine=\"1\"} 2\n" +
            "vllm:num_requests_waiting{model_name=\"qwen3.8-vllm\"} 0\n" +
            "vllm:kv_cache_usage_perc{model_name=\"qwen3.8-vllm\"} 0.42\n" +
            "vllm:generation_tokens_total{model_name=\"qwen3.8-vllm\",engine=\"0\"} 100\n" +
            "vllm:generation_tokens_total{model_name=\"qwen3.8-vllm\",engine=\"1\"} 50\n" +
            "vllm:time_to_first_token_seconds_sum{model_name=\"qwen3.8-vllm\"} 1.5\n" +
            "vllm:time_to_first_token_seconds_count{model_name=\"qwen3.8-vllm\"} 3\n" +
            "vllm:http_request_duration_seconds_bucket{model_name=\"x\",le=\"0.1\"} 5\n";

        var result = PrometheusParser.Parse(body);

        Assert.Single(result.Models);
        var m = result.Models["qwen3.8-vllm"];
        Assert.NotNull(m.Running); Assert.Equal(5, m.Running!.Value, 1e-6);       // 3 + 2 across engines
        Assert.NotNull(m.Waiting); Assert.Equal(0, m.Waiting!.Value, 1e-6);
        Assert.NotNull(m.KvPct); Assert.Equal(42.0, m.KvPct!.Value, 1e-6);
        Assert.NotNull(m.GenTokensTotal); Assert.Equal(150, m.GenTokensTotal!.Value, 1e-6); // 100 + 50
        Assert.NotNull(m.TtftSum); Assert.Equal(1.5, m.TtftSum!.Value, 1e-6);
        Assert.NotNull(m.TtftCount); Assert.Equal(3, m.TtftCount!.Value, 1e-6);
    }

    [Fact]
    public void Parse_SkipsBuckets_Comments_AndBlankLines()
    {
        var body = "vllm:http_request_duration_seconds_bucket{model_name=\"x\",le=\"0.1\"} 5\n\n# comment only\n";
        var result = PrometheusParser.Parse(body);
        Assert.False(result.Models.ContainsKey("x"));
        Assert.Empty(result.Models);
    }

    [Fact]
    public void Parse_NeverThrows_OnNullOrMalformed()
    {
        Assert.Empty(PrometheusParser.Parse(null).Models);
        Assert.Empty(PrometheusParser.Parse("   \n\n# only comments\n").Models);

        var weird = PrometheusParser.Parse("garbage no value\n{broken label 1.0\nvllm:num_requests_running 2x\n");
        Assert.Empty(weird.Models);
    }

    [Fact]
    public void Parse_LegacyGpuCacheUsageScalesBy100_OnlyIfPrimaryAbsent()
    {
        var legacyOnly = PrometheusParser.Parse("vllm:gpu_cache_usage_avg{model_name=\"m\"} 0.5\n").PickPrimary();
        Assert.NotNull(legacyOnly);
        Assert.NotNull(legacyOnly!.KvPct);
        Assert.Equal(50.0, legacyOnly.KvPct!.Value, 1e-6);

        var both = PrometheusParser.Parse(
            "vllm:kv_cache_usage_perc{model_name=\"m\"} 0.7\n" +
            "vllm:gpu_cache_usage_avg{model_name=\"m\"} 0.5\n").PickPrimary();
        Assert.NotNull(both);
        Assert.NotNull(both!.KvPct);
        Assert.Equal(70.0, both.KvPct!.Value, 1e-6); // primary wins over legacy
    }

    [Fact]
    public void PickPrimary_ReturnsMostPopulatedModel()
    {
        var body =
            "vllm:num_requests_running{model_name=\"a\"} 1\n" +
            "vllm:num_requests_waiting{model_name=\"a\"} 2\n" +
            "vllm:kv_cache_usage_perc{model_name=\"a\"} 0.5\n" +
            "vllm:num_requests_running{model_name=\"b\"} 1\n";

        var result = PrometheusParser.Parse(body);
        Assert.Equal(2, result.Models.Count);
        Assert.Equal("a", result.PickPrimary()!.ModelName);
    }

    [Fact]
    public void Parse_MetricWithoutModelNameFallsBackToEmpty()
    {
        var result = PrometheusParser.Parse("vllm:num_requests_running 4\n");
        Assert.Single(result.Models);
        var m = result.PickPrimary()!;
        Assert.Equal("", m.ModelName);
        Assert.NotNull(m.Running);
        Assert.Equal(4, m.Running!.Value, 1e-6);
    }
}
