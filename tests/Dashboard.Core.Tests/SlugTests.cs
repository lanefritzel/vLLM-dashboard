using Dashboard.Core.Models;

namespace Dashboard.Core.Tests;

public class SlugTests
{
    [Theory]
    [InlineData("tes.home local passthrough (vLLM)", "tes-home-local-passthrough-vllm")]
    [InlineData("tes — main vLLM", "tes-main-vllm")]
    [InlineData("  Leading and trailing spaces  ", "leading-and-trailing-spaces")]
    [InlineData("ALREADY-lower-case", "already-lower-case")]
    [InlineData("multiple   spaces___and--dashes", "multiple-spaces-and-dashes")]
    public void From_NormalizesLabelToUrlSafeSlug(string label, string expected)
    {
        Assert.Equal(expected, Slug.From(label));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("!!!")]
    public void From_FallsBackToDefaultForBlankOrSymbolOnlyInput(string label)
    {
        Assert.Equal("source", Slug.From(label));
    }

    [Fact]
    public void From_TruncatesVeryLongLabels()
    {
        var longLabel = new string('a', 200);
        var slug = Slug.From(longLabel);
        Assert.True(slug.Length <= 60);
    }
}
