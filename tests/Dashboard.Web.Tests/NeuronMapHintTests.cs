using Bunit;
using Bunit.TestDoubles;
using Dashboard.Core.Abstractions;
using Dashboard.Infrastructure.Persistence.V2;
using Dashboard.Infrastructure.Visualizer;
using Dashboard.Web.Components.Overview;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Dashboard.Web.Tests;

public class NeuronMapHintTests : BunitContext
{
    private static readonly TimeSpan Threshold = TimeSpan.FromSeconds(20);

    [Theory]
    [InlineData("live", false, 25, false, true)]    // connected 25s, never any data: explain
    [InlineData("live", false, 5, false, false)]    // just opened; nobody has prompted yet
    [InlineData("live", true, 60, false, false)]    // data has arrived: no hint
    [InlineData("live", false, 60, true, false)]    // the plugin was switched off on purpose
    [InlineData("connecting", false, 60, false, false)]
    [InlineData("unavailable", false, 60, false, false)] // it has its own message
    [InlineData("idle", false, 60, false, false)]
    public void ShouldShowNoDataHint_OnlyForAConnectedFeedThatNeverProducedData(
        string state, bool hasData, int liveSeconds, bool pluginOff, bool expected)
    {
        Assert.Equal(expected, NeuronMap.ShouldShowNoDataHint(state, hasData, TimeSpan.FromSeconds(liveSeconds), pluginOff, Threshold));
    }

    [Fact]
    public void ShouldShowNoDataHint_IsFalse_WhenTheFeedWasNeverLive()
    {
        Assert.False(NeuronMap.ShouldShowNoDataHint("live", false, null, false, Threshold));
    }

    private IRenderedComponent<NeuronMap> RenderOpenMap()
    {
        Services.AddSingleton(new VisualizerControlService(
            ProxyStub.Create<IVisualizerEndpointResolver>(),
            ProxyStub.Create<IHttpClientFactory>(),
            ProxyStub.Create<IConfigStore>(),
            NullLogger<VisualizerControlService>.Instance));
        JSInterop.Mode = JSRuntimeMode.Loose;
        JSInterop.Setup<string>("neuronmap_pref").SetResult("1"); // the map was left open

        var cut = Render<NeuronMap>(p => p
            .Add(c => c.Instance, new Instance { Id = 1, ServerId = 1, Label = "i", ProxySlug = "i" })
            .Add(c => c.NoDataHintAfter, TimeSpan.Zero));
        cut.WaitForState(() => cut.Markup.Contains("nm-canvas"));
        return cut;
    }

    [Fact]
    public async Task ConnectedWithNoSnapshots_ExplainsThatCudaGraphRecipesShowNothing()
    {
        var cut = RenderOpenMap();

        await cut.InvokeAsync(() => cut.Instance.OnStatus("live", ""));

        Assert.Contains("Connected, but no activity yet", cut.Markup);
        Assert.Contains("Enforce eager", cut.Markup);
        Assert.Contains("help#eager-vs-graphs", cut.Markup);
    }

    [Fact]
    public async Task TheHint_GoesAwayOnceDataArrives_AndStaysGoneAfterClear()
    {
        var cut = RenderOpenMap();
        await cut.InvokeAsync(() => cut.Instance.OnStatus("live", ""));
        Assert.Contains("Connected, but no activity yet", cut.Markup);

        await cut.InvokeAsync(() => cut.Instance.OnHasData(true));
        Assert.DoesNotContain("Connected, but no activity yet", cut.Markup);

        // Clear empties the canvas (the page is told "no data" again) but data did arrive.
        await cut.InvokeAsync(() => cut.Instance.OnHasData(false));
        Assert.DoesNotContain("Connected, but no activity yet", cut.Markup);
    }

    [Fact]
    public async Task NotReachable_MentionsThatSpeedRecipesTurnThePluginOffOnPurpose()
    {
        var cut = RenderOpenMap();

        await cut.InvokeAsync(() => cut.Instance.OnStatus("unavailable", "connection refused"));

        Assert.Contains("No neuron-map feed from this instance", cut.Markup);
        Assert.Contains("VIS_ENABLED=0", cut.Markup);
        Assert.DoesNotContain("Connected, but no activity yet", cut.Markup);
    }
}
