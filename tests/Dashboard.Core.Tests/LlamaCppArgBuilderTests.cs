using Dashboard.Core.Recipes;

namespace Dashboard.Core.Tests;

/// <summary>Spike (multi-engine support): does the existing <see cref="Recipe"/>/
/// <see cref="RecipeModel"/>/<see cref="RecipeInstance"/>/<see cref="RecipeServer"/>
/// shape -- built for vLLM -- carry over to a second engine's arg builder? Written
/// before <see cref="LlamaCppArgBuilder"/> exists, same pattern as
/// <see cref="ArgBuilderTests"/>.</summary>
public class LlamaCppArgBuilderTests
{
    private static RecipeModel PlainModel(string path = "/data/models/plain-model") => new() { Path = path };
    private static RecipeServer PlainServer(string modelsDir = "/data/models") => new() { ModelsDir = modelsDir };
    private static RecipeInstance PlainInstance(int port = 9999) => new() { Port = port };
    private static Recipe PlainRecipe() => new() { ServedModelName = "main" };

    private static Dictionary<string, string?> ToFlagDictionary(IReadOnlyList<string> argv)
    {
        var result = new Dictionary<string, string?>(StringComparer.Ordinal);
        for (var i = 3; i < argv.Count; i++)
        {
            var flag = argv[i];
            var hasValue = i + 1 < argv.Count && !argv[i + 1].StartsWith("--", StringComparison.Ordinal);
            result[flag] = hasValue ? argv[++i] : null;
        }
        return result;
    }

    [Fact]
    public void Build_PlainModel_EmitsLlamaServerModelPortAndAlias()
    {
        var result = LlamaCppArgBuilder.Build(PlainModel(), PlainRecipe(), PlainInstance(), PlainServer());

        Assert.Equal(new[] { "llama-server", "--model", "/models/plain-model" }, result.Argv.Take(3));
        var flags = ToFlagDictionary(result.Argv);
        Assert.Equal("main", flags["--alias"]);
        Assert.Equal("9999", flags["--port"]);
    }

    [Fact]
    public void Build_SubstitutesTheModelsDirPrefixForTheContainerPath()
    {
        var model = PlainModel("/home/lanef/hf-models/hf-cache/some-model");
        var server = PlainServer("/home/lanef/hf-models");
        var result = LlamaCppArgBuilder.Build(model, PlainRecipe(), PlainInstance(), server);

        Assert.Equal("/models/hf-cache/some-model", result.Argv[2]);
    }

    [Fact]
    public void Build_InjectsTheInstancesPort()
    {
        var result = LlamaCppArgBuilder.Build(PlainModel(), PlainRecipe(), PlainInstance(port: 8123), PlainServer());

        Assert.Equal("8123", ToFlagDictionary(result.Argv)["--port"]);
    }

    [Fact]
    public void Build_UsesMaxModelLenAsTheContextSize()
    {
        var recipe = PlainRecipe() with { MaxModelLen = 32768 };
        var result = LlamaCppArgBuilder.Build(PlainModel(), recipe, PlainInstance(), PlainServer());

        Assert.Equal("32768", ToFlagDictionary(result.Argv)["--ctx-size"]);
    }

    [Fact]
    public void Build_ExtraArgOverridingADial_ReplacesInPlace_NeverTwice()
    {
        var recipe = PlainRecipe() with
        {
            MaxModelLen = 8192,
            ExtraArgs = new[] { new RecipeExtraArg("ctx-size", "16384") },
        };
        var result = LlamaCppArgBuilder.Build(PlainModel(), recipe, PlainInstance(), PlainServer());

        Assert.Single(result.Argv, a => a == "--ctx-size");
        Assert.Equal("16384", ToFlagDictionary(result.Argv)["--ctx-size"]);
    }

    [Fact]
    public void Build_ExtraArgWithBlankValue_EmitsABooleanFlag()
    {
        var recipe = PlainRecipe() with { ExtraArgs = new[] { new RecipeExtraArg("flash-attn", "") } };
        var result = LlamaCppArgBuilder.Build(PlainModel(), recipe, PlainInstance(), PlainServer());

        var flags = ToFlagDictionary(result.Argv);
        Assert.True(flags.ContainsKey("--flash-attn"));
        Assert.Null(flags["--flash-attn"]);
    }

    [Fact]
    public void Build_ExtraEnv_MergesWithLaterKeyWinningOnADuplicate()
    {
        var recipe = PlainRecipe() with
        {
            ExtraEnv = new[]
            {
                new RecipeEnvOverride("GGML_CUDA", "0"),
                new RecipeEnvOverride("GGML_CUDA", "1"),
            },
        };
        var result = LlamaCppArgBuilder.Build(PlainModel(), recipe, PlainInstance(), PlainServer());

        Assert.Equal("1", result.Env["GGML_CUDA"]);
    }

    [Fact]
    public void Build_SameInputsAlwaysProduceIdenticalArgvOrder()
    {
        var recipe = PlainRecipe() with
        {
            ExtraArgs = new[] { new RecipeExtraArg("foo", "bar"), new RecipeExtraArg("baz", "") },
        };
        var first = LlamaCppArgBuilder.Build(PlainModel(), recipe, PlainInstance(), PlainServer());
        var second = LlamaCppArgBuilder.Build(PlainModel(), recipe, PlainInstance(), PlainServer());

        Assert.Equal(first.Argv, second.Argv);
    }

    /// <summary>The whole point of this spike: the vLLM-only dials on the shared
    /// Recipe record have no llama.cpp equivalent and must never leak into its
    /// argv. If this starts failing, it means the shared Recipe shape no longer
    /// holds and needs splitting per engine (an open question in the proposal doc).</summary>
    [Fact]
    public void Build_NeverEmitsAnyVllmOnlyDial()
    {
        var recipe = PlainRecipe() with
        {
            GpuMemoryUtilization = 0.5,
            KvCacheDtype = "fp8",
            Dtype = "bfloat16",
            EnforceEager = true,
            MaxNumBatchedTokens = 4096,
            BlockSize = 32,
            SpeculativeDecoding = true,
            SpeculativeTokens = 3,
            EnableAutoToolChoice = true,
            ToolCallParser = "qwen3_coder",
            ReasoningParser = "deepseek_r1",
        };
        var result = LlamaCppArgBuilder.Build(PlainModel(), recipe, PlainInstance(), PlainServer());

        string[] vllmOnlyFlags =
        [
            "--gpu-memory-utilization", "--kv-cache-dtype", "--dtype", "--enforce-eager",
            "--max-num-batched-tokens", "--block-size", "--speculative-config",
            "--enable-auto-tool-choice", "--tool-call-parser", "--reasoning-parser",
            "--served-model-name", "--load-format", "--trust-remote-code",
        ];
        foreach (var flag in vllmOnlyFlags)
            Assert.DoesNotContain(result.Argv, a => a == flag);
    }
}
