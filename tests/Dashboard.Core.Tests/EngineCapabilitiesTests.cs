using Dashboard.Core.Models;
using Dashboard.Core.Recipes;

namespace Dashboard.Core.Tests;

/// <summary>P3: what each engine can actually report/support, so the UI shows "not available"
/// instead of a misleading blank or zero (Findings 3).</summary>
public class EngineCapabilitiesTests
{
    [Theory]
    [InlineData(SourceType.Vllm, true)]
    [InlineData(SourceType.LlamaCpp, false)]
    public void ReportsKvCacheUsage(SourceType engine, bool expected) =>
        Assert.Equal(expected, EngineCapabilities.ReportsKvCacheUsage(engine));

    [Theory]
    [InlineData(SourceType.Vllm, true)]
    [InlineData(SourceType.LlamaCpp, false)]
    public void HasNeuronMap(SourceType engine, bool expected) =>
        Assert.Equal(expected, EngineCapabilities.HasNeuronMap(engine));

    [Theory]
    [InlineData(SourceType.Vllm, true)]
    [InlineData(SourceType.LlamaCpp, false)]
    public void HasWarmupPhase(SourceType engine, bool expected) =>
        Assert.Equal(expected, EngineCapabilities.HasWarmupPhase(engine));

    [Theory]
    [InlineData(SourceType.Vllm, "vLLM")]
    [InlineData(SourceType.LlamaCpp, "llama.cpp")]
    public void DisplayName(SourceType engine, string expected) =>
        Assert.Equal(expected, EngineCapabilities.DisplayName(engine));

    [Theory]
    [InlineData("vllm", true)]
    [InlineData("llamacpp", true)]
    [InlineData("llama", false)]
    [InlineData("", false)]
    [InlineData("VLLM", false)]
    public void IsKnownEngine_AcceptsOnlyTheExactStorageStrings(string value, bool expected) =>
        Assert.Equal(expected, EngineCapabilities.IsKnownEngine(value));

    // ---- the single list every engine picker / validator is built from ----

    [Fact]
    public void Options_AreTheUserSelectableEngines_VllmFirst()
    {
        Assert.Equal(["vllm", "llamacpp"], EngineCapabilities.Options.Select(o => o.Storage));
        Assert.Equal(SourceType.Vllm, EngineCapabilities.DefaultEngine.Type);
    }

    [Fact]
    public void EveryOption_RoundTripsThroughStorage_IsKnown_AndHasALabel()
    {
        foreach (var o in EngineCapabilities.Options)
        {
            Assert.Equal(o.Type, SourceTypeExtensions.FromStorage(o.Storage));
            Assert.True(EngineCapabilities.IsKnownEngine(o.Storage));
            Assert.Equal(o.Label, EngineCapabilities.DisplayName(o.Type));
            Assert.False(string.IsNullOrWhiteSpace(o.Label));
        }
    }

    [Fact]
    public void EveryOption_HasAnArgBuilder_SoThePickerNeverOffersAnEngineThatCantDeploy()
    {
        foreach (var o in EngineCapabilities.Options)
            Assert.Equal(o.Type, new ArgBuilderFactory().Create(o.Type).Engine);
    }

    [Fact]
    public void IsKnownEngine_IsDerivedFromTheOptions_NotASecondList()
    {
        // Every SourceType that is NOT an option (Ollama, Mock) must be rejected.
        foreach (var type in Enum.GetValues<SourceType>().Where(t => EngineCapabilities.Options.All(o => o.Type != t)))
            Assert.False(EngineCapabilities.IsKnownEngine(type.ToStorage()));
    }
}
