using System.Text.Json;
using Dashboard.Application.HuggingFace;

namespace Dashboard.Core.Tests;

public class HuggingFaceModelLookupServiceTests
{
    // Shapes below are trimmed from real Hugging Face API responses captured live
    // against Intel/gemma-4-31B-it-int4-AutoRound (quantized) and google/gemma-4-31B-it
    // (unquantized) while diagnosing the Gemma incident.

    [Fact]
    public void TryGetParameterCount_ReadsSafetensorsTotal()
    {
        using var doc = JsonDocument.Parse("""{"safetensors":{"total":31273088876}}""");
        Assert.Equal(31273088876L, HuggingFaceModelLookupService.TryGetParameterCount(doc.RootElement));
    }

    [Fact]
    public void TryGetParameterCount_ReturnsNullWhenMissing()
    {
        using var doc = JsonDocument.Parse("""{"id":"some/repo"}""");
        Assert.Null(HuggingFaceModelLookupService.TryGetParameterCount(doc.RootElement));
    }

    [Fact]
    public void TryGetQuantization_DetectsQuantizationConfigWithMethod()
    {
        using var doc = JsonDocument.Parse("""
            {"config":{"quantization_config":{"bits":4,"quant_method":"auto-round"}}}
            """);
        var (isQuantized, method) = HuggingFaceModelLookupService.TryGetQuantization(doc.RootElement);
        Assert.True(isQuantized);
        Assert.Equal("auto-round", method);
    }

    [Fact]
    public void TryGetQuantization_FallsBackToTagsWhenNoQuantizationConfig()
    {
        using var doc = JsonDocument.Parse("""{"tags":["transformers","gptq","region:us"]}""");
        var (isQuantized, method) = HuggingFaceModelLookupService.TryGetQuantization(doc.RootElement);
        Assert.True(isQuantized);
        Assert.Null(method);
    }

    [Fact]
    public void TryGetQuantization_ReturnsFalseForAnUnquantizedRepo()
    {
        using var doc = JsonDocument.Parse("""
            {"config":{"architectures":["Gemma4ForConditionalGeneration"],"model_type":"gemma4"},
             "tags":["transformers","safetensors","gemma4","license:apache-2.0"]}
            """);
        var (isQuantized, method) = HuggingFaceModelLookupService.TryGetQuantization(doc.RootElement);
        Assert.False(isQuantized);
        Assert.Null(method);
    }

    [Fact]
    public void TryGetMaxPositionEmbeddings_ReadsTopLevelValue()
    {
        using var doc = JsonDocument.Parse("""{"max_position_embeddings":131072}""");
        Assert.True(HuggingFaceModelLookupService.TryGetMaxPositionEmbeddings(doc.RootElement, out var value));
        Assert.Equal(131072, value);
    }

    [Fact]
    public void TryGetMaxPositionEmbeddings_ReturnsFalseWhenAbsent()
    {
        using var doc = JsonDocument.Parse("""{"model_type":"gemma4"}""");
        Assert.False(HuggingFaceModelLookupService.TryGetMaxPositionEmbeddings(doc.RootElement, out _));
    }

    [Fact]
    public void TryGetMaxPositionEmbeddings_OnNestedTextConfig_ReadsTheTextValueNotVision()
    {
        // Real shape from google/gemma-4-31B-it's config.json: text_config and
        // vision_config each carry their own (different) max_position_embeddings —
        // the text one (262144) is what matters for text generation, not vision's
        // smaller 131072.
        using var doc = JsonDocument.Parse("""
            {"text_config":{"max_position_embeddings":262144,"sliding_window":1024},
             "vision_config":{"max_position_embeddings":131072}}
            """);
        Assert.True(doc.RootElement.TryGetProperty("text_config", out var textConfig));
        Assert.True(HuggingFaceModelLookupService.TryGetMaxPositionEmbeddings(textConfig, out var value));
        Assert.Equal(262144, value);
    }
}
