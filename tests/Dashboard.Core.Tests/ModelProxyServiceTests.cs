using Dashboard.Infrastructure.Proxy;

namespace Dashboard.Core.Tests;

public class ModelProxyServiceTests
{
    [Fact]
    public void TryExtractModelName_ReturnsModelFromValidOpenAiRequest()
    {
        var json = """{"model": "qwen3.8-vllm", "messages": [{"role": "user", "content": "hi"}]}""";
        Assert.Equal("qwen3.8-vllm", ModelProxyService.TryExtractModelName(json));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void TryExtractModelName_ReturnsNullForBlankInput(string? input)
    {
        Assert.Null(ModelProxyService.TryExtractModelName(input));
    }

    [Fact]
    public void TryExtractModelName_ReturnsNullForMalformedJson()
    {
        Assert.Null(ModelProxyService.TryExtractModelName("{not valid json"));
    }

    [Fact]
    public void TryExtractModelName_ReturnsNullWhenModelFieldMissing()
    {
        Assert.Null(ModelProxyService.TryExtractModelName("""{"prompt": "hi"}"""));
    }

    [Fact]
    public void TryExtractModelName_ReturnsNullWhenModelFieldIsNotAString()
    {
        Assert.Null(ModelProxyService.TryExtractModelName("""{"model": 123}"""));
    }

    [Fact]
    public void TryExtractModelName_ReturnsNullWhenModelFieldIsBlank()
    {
        Assert.Null(ModelProxyService.TryExtractModelName("""{"model": "  "}"""));
    }

    [Fact]
    public void TryExtractModelName_ReturnsNullForNonObjectJson()
    {
        Assert.Null(ModelProxyService.TryExtractModelName("[1,2,3]"));
        Assert.Null(ModelProxyService.TryExtractModelName("\"just a string\""));
    }

    [Theory]
    [InlineData("v1/models", true)]
    [InlineData("/v1/models", true)]
    [InlineData("v1/models/", true)]
    [InlineData("/v1/models/", true)]
    [InlineData("V1/MODELS", true)]
    [InlineData("v1/chat/completions", false)]
    [InlineData("v1/completions", false)]
    [InlineData("", false)]
    public void IsModelsListPath_MatchesOnlyTheModelsEndpoint(string path, bool expected)
    {
        Assert.Equal(expected, ModelProxyService.IsModelsListPath(path));
    }

    [Fact]
    public void TryExtractDeltaText_ReturnsStreamingDeltaContent()
    {
        var json = """{"choices":[{"index":0,"delta":{"role":"assistant","content":"Hello"},"finish_reason":null}]}""";
        Assert.Equal("Hello", ModelProxyService.TryExtractDeltaText(json));
    }

    [Fact]
    public void TryExtractDeltaText_ReturnsNonStreamingMessageContent()
    {
        var json = """{"choices":[{"index":0,"message":{"role":"assistant","content":"Full answer"},"finish_reason":"stop"}]}""";
        Assert.Equal("Full answer", ModelProxyService.TryExtractDeltaText(json));
    }

    [Fact]
    public void TryExtractDeltaText_ReturnsLegacyCompletionsText()
    {
        var json = """{"choices":[{"index":0,"text":"legacy text","finish_reason":null}]}""";
        Assert.Equal("legacy text", ModelProxyService.TryExtractDeltaText(json));
    }

    [Fact]
    public void TryExtractDeltaText_IgnoresReasoningOnlyDeltas()
    {
        var json = """{"choices":[{"index":0,"delta":{"reasoning":"thinking..."},"finish_reason":null}]}""";
        Assert.Null(ModelProxyService.TryExtractDeltaText(json));
    }

    [Fact]
    public void TryExtractDeltaText_ReturnsNullForToolCallDeltas()
    {
        var json = """{"choices":[{"index":0,"delta":{"tool_calls":[{"index":0,"id":"call_1"}]},"finish_reason":null}]}""";
        Assert.Null(ModelProxyService.TryExtractDeltaText(json));
    }

    [Fact]
    public void TryExtractDeltaText_ReturnsNullWhenChoicesMissing()
    {
        Assert.Null(ModelProxyService.TryExtractDeltaText("""{"object":"list","data":[]}"""));
    }

    [Fact]
    public void TryExtractDeltaText_ReturnsNullForMalformedJson()
    {
        Assert.Null(ModelProxyService.TryExtractDeltaText("not json"));
    }

    [Fact]
    public void TryExtractReasoningText_ReturnsReasoningField()
    {
        var json = """{"choices":[{"index":0,"delta":{"reasoning":"Let me think"},"finish_reason":null}]}""";
        Assert.Equal("Let me think", ModelProxyService.TryExtractReasoningText(json));
    }

    [Fact]
    public void TryExtractReasoningText_ReturnsReasoningContentField()
    {
        var json = """{"choices":[{"index":0,"delta":{"reasoning_content":"Considering options"},"finish_reason":null}]}""";
        Assert.Equal("Considering options", ModelProxyService.TryExtractReasoningText(json));
    }

    [Fact]
    public void TryExtractReasoningText_ReturnsNullWhenOnlyContentPresent()
    {
        var json = """{"choices":[{"index":0,"delta":{"content":"Hello"},"finish_reason":null}]}""";
        Assert.Null(ModelProxyService.TryExtractReasoningText(json));
    }

    [Fact]
    public void ExtractMessages_ReturnsRoleAndStringContentForEachMessage()
    {
        var json = """
            {"model":"m","messages":[
                {"role":"system","content":"You are helpful."},
                {"role":"user","content":"Hi there"}
            ]}
            """;
        var messages = ModelProxyService.ExtractMessages(json);
        Assert.Equal(2, messages.Count);
        Assert.Equal("system", messages[0].Role);
        Assert.Equal("You are helpful.", messages[0].Content);
        Assert.Equal("user", messages[1].Role);
        Assert.Equal("Hi there", messages[1].Content);
    }

    [Fact]
    public void ExtractMessages_JoinsTextPartsAndLabelsNonTextPartsForMultimodalContent()
    {
        var json = """
            {"messages":[
                {"role":"user","content":[
                    {"type":"text","text":"What is this?"},
                    {"type":"image_url","image_url":{"url":"http://example.com/x.png"}}
                ]}
            ]}
            """;
        var messages = ModelProxyService.ExtractMessages(json);
        Assert.Single(messages);
        Assert.Equal("What is this?\n[image_url]", messages[0].Content);
    }

    [Fact]
    public void ExtractMessages_ReturnsEmptyForMissingMessagesArray()
    {
        Assert.Empty(ModelProxyService.ExtractMessages("""{"model":"m","prompt":"legacy"}"""));
    }

    [Fact]
    public void ExtractMessages_ReturnsEmptyForMalformedJson()
    {
        Assert.Empty(ModelProxyService.ExtractMessages("not json"));
    }

    [Fact]
    public void ExtractMessages_ReturnsEmptyForBlankInput()
    {
        Assert.Empty(ModelProxyService.ExtractMessages(null));
        Assert.Empty(ModelProxyService.ExtractMessages(""));
    }
}
