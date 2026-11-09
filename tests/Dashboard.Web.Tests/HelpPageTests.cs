using System.Text.RegularExpressions;
using Bunit;
using Dashboard.Web.Components.Pages;

namespace Dashboard.Web.Tests;

public class HelpPageTests : BunitContext
{
    private IRenderedComponent<Help> RenderHelp() => Render<Help>();

    [Fact]
    public void EveryInPageLink_PointsAtASectionThatExists()
    {
        var cut = RenderHelp();

        var ids = cut.FindAll("[id]").Select(e => e.Id).ToHashSet();
        var anchors = cut.FindAll("a[href^='#']").Select(a => a.GetAttribute("href")!.TrimStart('#')).Distinct().ToList();

        Assert.NotEmpty(anchors);
        Assert.All(anchors, a => Assert.Contains(a, ids));
    }

    private static string Flat(string text) => Regex.Replace(text, @"\s+", " ");

    [Fact]
    public void ChoosingSettings_IsListedInTheContents_AndExplainsHowToCalculateThem()
    {
        var cut = RenderHelp();

        Assert.NotEmpty(cut.FindAll("a[href='#choosing-settings']"));
        var text = Flat(cut.Find("#choosing-settings").TextContent);
        Assert.Contains("parameters × bytes per parameter", text);
        Assert.Contains("KV cache memory = total memory × utilization", text);
        Assert.Contains("KV bytes per token = 2 × layers × KV heads × head size × bytes per value", text);
        Assert.Contains("Maximum concurrency", text);
        Assert.Contains("max-num-seqs", text);
        Assert.Contains("Max batched tokens", text);
        Assert.Contains("estimated maximum model length", text);
    }

    [Fact]
    public void ChoosingSettings_IsGuidanceForAnyHardware_NotRecipesForOneCardOrModel()
    {
        var section = RenderHelp().Find("#choosing-settings");

        Assert.Empty(section.QuerySelectorAll("table"));
        var text = section.TextContent;
        foreach (var specific in new[] { "RTX", "3060", "12 GB", "12\u00a0GB", "Qwen", "Gemma", "gemma", "Huihui", "unsloth", "Llama", "nicklas373", "i7-" })
            Assert.DoesNotContain(specific, text);
    }

    [Fact]
    public void ChoosingSettings_WorkedExampleArithmeticIsRight()
    {
        // The page's illustration: 16 GB card, utilization 0.90, 5.5 GB weights, 1.5 GB working
        // memory, 1.0 GB graphs; 32 layers, 8 KV heads, head size 128, 16-bit KV values.
        var budgetGb = 16 * 0.90;
        var kvGb = budgetGb - 5.5 - 1.5 - 1.0;
        var kvBytesPerToken = 2 * 32 * 8 * 128 * 2;
        var tokens = kvGb * 1024 * 1024 * 1024 / kvBytesPerToken;

        var text = Flat(RenderHelp().Find("#choosing-settings").TextContent);

        Assert.Equal(14.4, budgetGb, 6);
        Assert.Equal(6.4, kvGb, 6);
        Assert.Equal(131072, kvBytesPerToken); // 128 KB
        Assert.InRange(tokens, 52_000, 53_000);
        Assert.Contains("= 14.4 GB", text);
        Assert.Contains("= 6.4 GB", text);
        Assert.Contains("= 128 KB", text);
        Assert.Contains("≈ 52,000 tokens", text);
    }

    [Fact]
    public void EagerVsGraphs_ExplainsThatTheNeuronMapNeedsEagerMode()
    {
        var text = RenderHelp().Find("#eager-vs-graphs").TextContent;

        Assert.Contains("neuron map only works with Enforce eager on", Regex.Replace(text, @"\s+", " "));
        Assert.Contains("VIS_ENABLED", text);
    }

    [Fact]
    public void Troubleshooting_CoversABlankNeuronMap_AndSlowWeightLoading()
    {
        var text = Regex.Replace(RenderHelp().Find("#troubleshooting").TextContent, @"\s+", " ");

        Assert.Contains("neuron map is blank", text);
        Assert.Contains("Enforce eager", text);
        Assert.Contains("Loading weights takes minutes", text);
    }
}
