global using V2Recipe = Dashboard.Infrastructure.Persistence.V2.Recipe;
using Dashboard.Core.Models;
using Dashboard.Core.Recipes;
using Dashboard.Infrastructure.Persistence.V2;

namespace Dashboard.Core.Tests;

/// <summary>P3: a V2 Instance has an engine (default vLLM) and the poller / swap path
/// use it instead of assuming vLLM.</summary>
public class InstanceEngineTests
{
    [Fact]
    public void NewInstance_DefaultsToVllm()
    {
        var instance = new Instance();
        Assert.Equal("vllm", instance.Engine);
        Assert.Equal(SourceType.Vllm, instance.EngineType);
    }

    [Fact]
    public void EngineType_ReadsTheStoredString()
    {
        Assert.Equal(SourceType.LlamaCpp, new Instance { Engine = "llamacpp" }.EngineType);
    }

    // ---- recipe mapping by engine ----

    private static V2Recipe EntityRecipe() => new()
    {
        ServedModelName = "qwen", MaxModelLen = 4096, GpuMemoryUtilization = 0.5f, Dtype = "float16",
        ExtraArgs = """[{"key":"flash-attn","value":""}]""", ExtraEnv = """[{"key":"A","value":"1"}]""",
    };

    [Fact]
    public void ToCoreRecipe_Vllm_CarriesTheVllmDials()
    {
        var recipe = Assert.IsType<VllmRecipe>(CoreRecipeMappers.ToCoreRecipe(EntityRecipe(), SourceType.Vllm));
        Assert.Equal(0.5, recipe.GpuMemoryUtilization, 3);
        Assert.Equal("float16", recipe.Dtype);
    }

    [Fact]
    public void ToCoreRecipe_LlamaCpp_CarriesOnlyTheSharedFields()
    {
        var recipe = Assert.IsType<LlamaCppRecipe>(CoreRecipeMappers.ToCoreRecipe(EntityRecipe(), SourceType.LlamaCpp));
        Assert.Equal("qwen", recipe.ServedModelName);
        Assert.Equal(4096, recipe.MaxModelLen);
        Assert.Equal("flash-attn", Assert.Single(recipe.ExtraArgs).Key);
        Assert.Equal("1", Assert.Single(recipe.ExtraEnv).Value);
    }

    [Fact]
    public void ToCoreRecipe_UnsupportedEngine_Throws()
    {
        Assert.Throws<NotSupportedException>(() => CoreRecipeMappers.ToCoreRecipe(EntityRecipe(), SourceType.Ollama));
    }

    // ---- metrics source for an instance ----

    [Fact]
    public void ToSourceInfo_LlamaCpp_UsesInferenceUrl_AndTheInstancesEngine()
    {
        var info = InstanceSourceMapper.ToSourceInfo(
            new Instance { Label = "box", Engine = "llamacpp", PollIntervalMs = 3000 }, "src-1", "http://h:8080");
        Assert.Equal(SourceType.LlamaCpp, info.Type);
        Assert.Equal("http://h:8080", info.InferenceUrl);
        Assert.Equal(3, info.ScrapeIntervalSeconds);
        Assert.Equal("src-1", info.Id);
    }

    [Fact]
    public void ToSourceInfo_Vllm_UsesMetricsUrl()
    {
        var info = InstanceSourceMapper.ToSourceInfo(new Instance { Label = "box" }, "src-1", "http://h:9999");
        Assert.Equal(SourceType.Vllm, info.Type);
        Assert.Equal("http://h:9999", info.MetricsUrl);
    }
}
