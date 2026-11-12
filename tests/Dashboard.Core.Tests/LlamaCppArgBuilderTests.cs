using Dashboard.Core.Recipes;

namespace Dashboard.Core.Tests;

/// <summary>llama.cpp's argv builder over <see cref="LlamaCppRecipe"/>. The "never emits a
/// vLLM-only dial" contract is now structural -- see <see cref="EngineSplitTests"/>.</summary>
public class LlamaCppArgBuilderTests
{
    private static RecipeModel PlainModel(string path = "/data/models/plain-model") => new() { Path = path };
    private static RecipeServer PlainServer(string modelsDir = "/data/models") => new() { ModelsDir = modelsDir };
    private static RecipeInstance PlainInstance(int port = 9999) => new() { Port = port };
    private static LlamaCppRecipe PlainRecipe() => new() { ServedModelName = "main" };

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

    /// <summary>llama-server only serves /metrics when started with --metrics (confirmed
    /// against a real server); without it the dashboard's scraper would see 404 forever.</summary>
    [Fact]
    public void Build_AlwaysEnablesTheMetricsEndpoint()
    {
        var result = LlamaCppArgBuilder.Build(PlainModel(), PlainRecipe(), PlainInstance(), PlainServer());

        var flags = ToFlagDictionary(result.Argv);
        Assert.True(flags.ContainsKey("--metrics"));
        Assert.Null(flags["--metrics"]);
    }

    /// <summary>P6: Auto is llama.cpp's own default (confirmed via a real CUDA build's
    /// --help), so this omits the flag entirely rather than spelling out "auto".</summary>
    [Fact]
    public void Build_GpuLayersAuto_OmitsTheFlag()
    {
        var recipe = PlainRecipe() with { GpuLayers = LlamaCppGpuLayers.Auto };
        var result = LlamaCppArgBuilder.Build(PlainModel(), recipe, PlainInstance(), PlainServer());

        Assert.False(ToFlagDictionary(result.Argv).ContainsKey("--gpu-layers"));
    }

    [Fact]
    public void Build_GpuLayersAll_EmitsGpuLayersAll()
    {
        var recipe = PlainRecipe() with { GpuLayers = LlamaCppGpuLayers.All };
        var result = LlamaCppArgBuilder.Build(PlainModel(), recipe, PlainInstance(), PlainServer());

        Assert.Equal("all", ToFlagDictionary(result.Argv)["--gpu-layers"]);
    }

    [Fact]
    public void Build_GpuLayersCpu_EmitsGpuLayersZero()
    {
        var recipe = PlainRecipe() with { GpuLayers = LlamaCppGpuLayers.Cpu };
        var result = LlamaCppArgBuilder.Build(PlainModel(), recipe, PlainInstance(), PlainServer());

        Assert.Equal("0", ToFlagDictionary(result.Argv)["--gpu-layers"]);
    }
}
