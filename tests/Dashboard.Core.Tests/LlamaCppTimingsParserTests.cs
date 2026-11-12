using Dashboard.Core.Models;
using Dashboard.Infrastructure.Proxy;

namespace Dashboard.Core.Tests;

/// <summary>P7: parses the per-chunk <c>timings</c> object llama-server embeds in every
/// streamed SSE chunk when the request body carries <c>"timings_per_token": true</c> (see
/// ModelProxyServiceTests' InjectTimingsPerToken_* tests for the request-side counterpart).
/// Confirmed against a real server (serenity, build b12282-aa75ff323): without that flag, a
/// chunk only carries <c>timings</c> in the FINAL chunk -- these are real intermediate-chunk
/// bodies captured mid-generation with the flag set, not synthesized.</summary>
public class LlamaCppTimingsParserTests
{
    // Captured live from a real request with "timings_per_token": true, chunk 2 of the stream.
    private const string RealChunk = """
        {"choices":[{"finish_reason":null,"index":0,"delta":{"reasoning_content":"   "}}],"created":1794442418,"id":"chatcmpl-qbdkbXYjVJkUdx5oK1vFV1RPnKdd8Zyz","model":"main","system_fingerprint":"b12282-aa75ff323","object":"chat.completion.chunk","timings":{"cache_n":7,"prompt_n":15,"prompt_ms":110.583,"prompt_per_token_ms":7.3722,"prompt_per_second":135.64471935107568,"predicted_n":5,"predicted_ms":107.101,"predicted_per_token_ms":26.77525,"predicted_per_second":37.34792392227897}}
        """;

    [Fact]
    public void TryParse_ReadsPredictedPerSecondAndTheThreeTokenCounts()
    {
        var timings = LlamaCppTimingsParser.TryParse(RealChunk);

        Assert.NotNull(timings);
        Assert.Equal(37.34792392227897, timings!.PredictedPerSecond, 1e-9);
        Assert.Equal(15, timings.PromptN);
        Assert.Equal(7, timings.CacheN);
        Assert.Equal(5, timings.PredictedN);
    }

    [Fact]
    public void TryParse_ReturnsNullWhenNoTimingsObjectIsPresent()
    {
        // The default shape (no "timings_per_token": true on the request) -- every
        // intermediate chunk looks like this; only the final one carries timings.
        var json = """{"choices":[{"finish_reason":null,"index":0,"delta":{"content":"Hi"}}]}""";
        Assert.Null(LlamaCppTimingsParser.TryParse(json));
    }

    [Fact]
    public void TryParse_ReturnsNullForBlankOrMalformedInput()
    {
        Assert.Null(LlamaCppTimingsParser.TryParse(null));
        Assert.Null(LlamaCppTimingsParser.TryParse(""));
        Assert.Null(LlamaCppTimingsParser.TryParse("not json"));
        Assert.Null(LlamaCppTimingsParser.TryParse("[1,2,3]"));
    }

    [Fact]
    public void TryParse_ReturnsNullWhenTimingsIsPresentButNotAnObject()
    {
        Assert.Null(LlamaCppTimingsParser.TryParse("""{"timings":"not an object"}"""));
    }

    /// <summary>The very first chunk of a request: predicted_n=1, predicted_ms effectively
    /// zero -- predicted_per_second is 0.0 there (confirmed live), not a divide-by-zero
    /// crash or a missing field.</summary>
    [Fact]
    public void TryParse_HandlesTheFirstChunksZeroRateWithoutError()
    {
        var json = """{"timings":{"cache_n":7,"prompt_n":15,"predicted_n":1,"predicted_ms":0.001,"predicted_per_second":0.0}}""";
        var timings = LlamaCppTimingsParser.TryParse(json);
        Assert.NotNull(timings);
        Assert.Equal(0.0, timings!.PredictedPerSecond);
        Assert.Equal(1, timings.PredictedN);
    }
}

/// <summary>P7: the KV-cache-percent formula, confirmed against llama.cpp's own bundled
/// web UI source (its "This turn · KV cache" panel sums the same three fields).</summary>
public class LlamaCppChunkTimingsTests
{
    private static LlamaCppChunkTimings Timings(int promptN, int cacheN, int predictedN) => new()
    {
        PredictedPerSecond = 0, PromptN = promptN, CacheN = cacheN, PredictedN = predictedN,
    };

    [Fact]
    public void ToKvCachePercent_SumsPromptCacheAndPredicted_AsAPercentOfCtxSize()
    {
        // 15 + 7 + 5 = 27 tokens occupied, out of a 4096 context.
        var pct = Timings(promptN: 15, cacheN: 7, predictedN: 5).ToKvCachePercent(ctxSize: 4096);
        Assert.Equal(27.0 / 4096 * 100, pct, 1e-9);
    }

    [Fact]
    public void ToKvCachePercent_ClampsToOneHundred_IfTheSumExceedsCtxSize()
    {
        var pct = Timings(promptN: 4000, cacheN: 0, predictedN: 200).ToKvCachePercent(ctxSize: 4096);
        Assert.Equal(100.0, pct);
    }

    [Fact]
    public void ToKvCachePercent_ReturnsZero_ForAZeroOrNegativeCtxSize()
    {
        Assert.Equal(0.0, Timings(10, 0, 0).ToKvCachePercent(ctxSize: 0));
        Assert.Equal(0.0, Timings(10, 0, 0).ToKvCachePercent(ctxSize: -1));
    }
}
