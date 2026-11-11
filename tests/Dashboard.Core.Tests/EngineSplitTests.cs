using Dashboard.Core.Models;
using Dashboard.Core.Recipes;

namespace Dashboard.Core.Tests;

/// <summary>P1: Recipe is split into a shared base plus per-engine dial sets, with
/// an IArgBuilder factory keyed by engine (mirrors IMetricsSourceFactory).</summary>
public class EngineSplitTests
{
    private static RecipeModel PlainModel() => new() { Path = "/data/models/m" };
    private static RecipeServer PlainServer(string? gpuName = null) => new() { ModelsDir = "/data/models", GpuName = gpuName };
    private static RecipeInstance PlainInstance() => new() { Port = 9999 };

    // ---- structural contract (replaces the old runtime Build_NeverEmitsAnyVllmOnlyDial) ----

    [Fact]
    public void LlamaCppRecipe_HasNoVllmOnlyDials()
    {
        string[] vllmOnly =
        [
            "GpuMemoryUtilization", "KvCacheDtype", "Dtype", "EnforceEager", "MaxNumBatchedTokens",
            "BlockSize", "SpeculativeDecoding", "SpeculativeTokens", "EnableAutoToolChoice",
            "ToolCallParser", "ReasoningParser",
        ];
        var names = typeof(LlamaCppRecipe).GetProperties().Select(p => p.Name).ToHashSet();
        foreach (var dial in vllmOnly)
            Assert.DoesNotContain(dial, names);
    }

    [Fact]
    public void SharedBase_HoldsOnlyTheFieldsBothEnginesUse()
    {
        var names = typeof(Recipe).GetProperties().Select(p => p.Name).Where(n => n != "EqualityContract").OrderBy(n => n);
        Assert.Equal(new[] { "ExtraArgs", "ExtraEnv", "MaxModelLen", "ServedModelName" }, names);
        Assert.True(typeof(Recipe).IsAbstract);
        Assert.True(typeof(VllmRecipe).IsSubclassOf(typeof(Recipe)));
        Assert.True(typeof(LlamaCppRecipe).IsSubclassOf(typeof(Recipe)));
    }

    // ---- factory ----

    [Fact]
    public void Factory_Vllm_BuildsVllmArgv()
    {
        var result = new ArgBuilderFactory().Create(SourceType.Vllm)
            .Build(PlainModel(), new VllmRecipe(), PlainInstance(), PlainServer());
        Assert.Equal(new[] { "vllm", "serve" }, result.Argv.Take(2));
    }

    [Fact]
    public void Factory_LlamaCpp_BuildsLlamaServerArgv()
    {
        var result = new ArgBuilderFactory().Create(SourceType.LlamaCpp)
            .Build(PlainModel(), new LlamaCppRecipe(), PlainInstance(), PlainServer());
        Assert.Equal("llama-server", result.Argv[0]);
    }

    [Fact]
    public void Factory_RecipeOfTheWrongEngine_Throws()
    {
        var builder = new ArgBuilderFactory().Create(SourceType.LlamaCpp);
        Assert.Throws<ArgumentException>(() => builder.Build(PlainModel(), new VllmRecipe(), PlainInstance(), PlainServer()));
    }

    [Fact]
    public void Factory_EngineWithNoArgBuilder_Throws()
    {
        Assert.Throws<NotSupportedException>(() => new ArgBuilderFactory().Create(SourceType.Ollama));
    }

    [Fact]
    public void Factory_ReportsTheEngineItBuildsFor()
    {
        var factory = new ArgBuilderFactory();
        Assert.Equal(SourceType.Vllm, factory.Create(SourceType.Vllm).Engine);
        Assert.Equal(SourceType.LlamaCpp, factory.Create(SourceType.LlamaCpp).Engine);
    }

    // ---- compatibility: vLLM-only rules must not apply to llama.cpp ----

    [Fact]
    public void LlamaCpp_OnIntelStack_GetsNoDtypeIssue()
    {
        var result = CompatibilityPass.CheckRecipe(new LlamaCppRecipe(), PlainServer("Intel Arc B580"));
        Assert.Empty(result.HardBlocks);
        Assert.Empty(result.SoftWarnings);
    }

    [Fact]
    public void LlamaCpp_TensorParallelExtraArg_IsNotChecked()
    {
        var recipe = new LlamaCppRecipe { ExtraArgs = new[] { new RecipeExtraArg("tensor-parallel-size", "8") } };
        var result = CompatibilityPass.CheckRecipe(recipe, PlainServer());
        Assert.Empty(result.HardBlocks);
    }

    [Fact]
    public void LlamaCpp_SharedRulesStillApply()
    {
        var result = CompatibilityPass.CheckRecipe(new LlamaCppRecipe { MaxModelLen = 0, ServedModelName = "-bad" }, PlainServer());
        Assert.Contains(result.HardBlocks, i => i.Code == "max_model_len_not_positive");
        Assert.Contains(result.HardBlocks, i => i.Code == "served_model_name_invalid");
    }

    [Fact]
    public void LlamaCpp_Check_NoGpuMemoryWarning_ButOversizedContextStillWarns()
    {
        var model = new RecipeModel { Path = "/data/models/m", SizeBytes = 80_000_000_000, ConfigMaxPositionEmbeddings = 4096 };
        var server = new RecipeServer { ModelsDir = "/data/models", GpuMemGb = 24 };
        var result = CompatibilityPass.Check(model, new LlamaCppRecipe { MaxModelLen = 8192 }, server);
        Assert.DoesNotContain(result.SoftWarnings, i => i.Code == "gpu_memory_utilization_too_low");
        Assert.Contains(result.SoftWarnings, i => i.Code == "max_model_len_exceeds_model_limit");
    }
}
