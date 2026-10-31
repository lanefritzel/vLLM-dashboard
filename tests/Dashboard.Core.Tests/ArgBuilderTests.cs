using Dashboard.Core.Recipes;

namespace Dashboard.Core.Tests;

public class ArgBuilderTests
{
    private static RecipeModel PlainModel(string path = "/data/models/plain-model") => new() { Path = path };
    private static RecipeServer PlainServer(string modelsDir = "/data/models") => new() { ModelsDir = modelsDir };
    private static RecipeInstance PlainInstance(int port = 9999) => new() { Port = port };
    private static Recipe PlainRecipe() => new() { ServedModelName = "main" };

    /// <summary>Parses "--flag value" / bare "--flag" pairs out of an argv list
    /// (skipping the leading "vllm" "serve" "&lt;path&gt;"), for order-independent
    /// comparison -- see <see cref="LiveQwenFixture_MatchesTheOracleFlagForFlag"/>
    /// for why order-independence is the right comparison here.</summary>
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
    public void Build_PlainModel_EmitsBaseArgsAndPort()
    {
        var result = ArgBuilder.Build(PlainModel(), PlainRecipe(), PlainInstance(), PlainServer());

        Assert.Equal(new[] { "vllm", "serve", "/models/plain-model" }, result.Argv.Take(3));
        var flags = ToFlagDictionary(result.Argv);
        Assert.Equal("main", flags["--served-model-name"]);
        Assert.Equal("9999", flags["--port"]);
        Assert.DoesNotContain("--quantization", flags.Keys);
    }

    [Fact]
    public void Build_GgufModel_EmitsLoadFormatFlag()
    {
        var model = PlainModel() with { LoadFormat = "gguf" };
        var result = ArgBuilder.Build(model, PlainRecipe(), PlainInstance(), PlainServer());

        var flags = ToFlagDictionary(result.Argv);
        Assert.Equal("gguf", flags["--load-format"]);
    }

    [Fact]
    public void Build_PreQuantizedModel_NeverEmitsQuantizationFlag()
    {
        // RecipeModel has no Quantization property at all -- there is nothing for
        // the builder to emit, by construction, regardless of recipe settings.
        var result = ArgBuilder.Build(PlainModel(), PlainRecipe(), PlainInstance(), PlainServer());

        Assert.DoesNotContain(result.Argv, a => a == "--quantization");
    }

    [Fact]
    public void Build_ExtraArgOverridingADial_ReplacesInPlace_NeverTwice()
    {
        var recipe = PlainRecipe() with
        {
            Dtype = "float16",
            ExtraArgs = new[] { new RecipeExtraArg("dtype", "bfloat16") },
        };
        var result = ArgBuilder.Build(PlainModel(), recipe, PlainInstance(), PlainServer());

        Assert.Single(result.Argv, a => a == "--dtype");
        var flags = ToFlagDictionary(result.Argv);
        Assert.Equal("bfloat16", flags["--dtype"]);
    }

    [Fact]
    public void Build_ExtraArgWithBlankValue_EmitsABooleanFlag()
    {
        var recipe = PlainRecipe() with { ExtraArgs = new[] { new RecipeExtraArg("enable-prefix-caching", "") } };
        var result = ArgBuilder.Build(PlainModel(), recipe, PlainInstance(), PlainServer());

        var flags = ToFlagDictionary(result.Argv);
        Assert.True(flags.ContainsKey("--enable-prefix-caching"));
        Assert.Null(flags["--enable-prefix-caching"]);
    }

    [Fact]
    public void Build_ExtraEnv_MergesWithLaterKeyWinningOnADuplicate()
    {
        var recipe = PlainRecipe() with
        {
            ExtraEnv = new[]
            {
                new RecipeEnvOverride("VLLM_WORKER_MULTIPROC_METHOD", "fork"),
                new RecipeEnvOverride("VLLM_WORKER_MULTIPROC_METHOD", "spawn"),
                new RecipeEnvOverride("VLLM_ALLOW_LONG_MAX_MODEL_LEN", "1"),
            },
        };
        var result = ArgBuilder.Build(PlainModel(), recipe, PlainInstance(), PlainServer());

        Assert.Equal("spawn", result.Env["VLLM_WORKER_MULTIPROC_METHOD"]);
        Assert.Equal("1", result.Env["VLLM_ALLOW_LONG_MAX_MODEL_LEN"]);
    }

    [Fact]
    public void Build_InjectsTheInstancesPort()
    {
        var result = ArgBuilder.Build(PlainModel(), PlainRecipe(), PlainInstance(port: 8123), PlainServer());

        Assert.Equal("8123", ToFlagDictionary(result.Argv)["--port"]);
    }

    [Fact]
    public void Build_NeverEmitsAFlagTwice()
    {
        var recipe = PlainRecipe() with
        {
            Dtype = "float16",
            ExtraArgs = new[] { new RecipeExtraArg("dtype", "bfloat16"), new RecipeExtraArg("dtype", "float32") },
        };
        var result = ArgBuilder.Build(PlainModel(), recipe, PlainInstance(), PlainServer());

        var occurrences = result.Argv.Count(a => a == "--dtype");
        Assert.Equal(1, occurrences);
        Assert.Equal("float32", ToFlagDictionary(result.Argv)["--dtype"]);
    }

    [Fact]
    public void Build_SubstitutesTheModelsDirPrefixForTheContainerPath()
    {
        var model = PlainModel("/home/lanef/hf-models/hf-cache/some-model");
        var server = PlainServer("/home/lanef/hf-models");
        var result = ArgBuilder.Build(model, PlainRecipe(), PlainInstance(), server);

        Assert.Equal("/models/hf-cache/some-model", result.Argv[2]);
    }

    [Fact]
    public void Build_SameInputsAlwaysProduceIdenticalArgvOrder()
    {
        var recipe = PlainRecipe() with
        {
            ToolCallParser = "qwen3_coder",
            ExtraArgs = new[] { new RecipeExtraArg("foo", "bar"), new RecipeExtraArg("baz", "") },
        };
        var first = ArgBuilder.Build(PlainModel(), recipe, PlainInstance(), PlainServer());
        var second = ArgBuilder.Build(PlainModel(), recipe, PlainInstance(), PlainServer());

        Assert.Equal(first.Argv, second.Argv);
    }

    /// <summary>The live qwen reference command from the P00/P01b pages. Compared
    /// as a flag-&gt;value dictionary rather than positionally: the reference
    /// command places --port right after --served-model-name (the OLD app's own
    /// ordering), while this builder's fixed precedence always injects --port last
    /// (P09a's own rule) -- so an exact positional match would fail even on fully
    /// correct output. "Flag-for-flag" here means the same set of flags with the
    /// same values, which is also what P01b's conversion golden test needs.</summary>
    [Fact]
    public void LiveQwenFixture_MatchesTheOracleFlagForFlag()
    {
        var model = new RecipeModel { Path = "/data/models/qwen3.8-int4" };
        var server = new RecipeServer { ModelsDir = "/data/models" };
        var instance = new RecipeInstance { Port = 9999 };
        var recipe = new Recipe
        {
            ServedModelName = "qwen3.8-vllm",
            Dtype = "float16",
            EnforceEager = true,
            GpuMemoryUtilization = 0.90,
            MaxModelLen = 131072,
            KvCacheDtype = "fp8_e4m3",
            BlockSize = 64,
            MaxNumBatchedTokens = 8192,
            EnableAutoToolChoice = true,
            ToolCallParser = "qwen3_coder",
            ReasoningParser = "qwen3",
            SpeculativeDecoding = true,
            SpeculativeTokens = 4,
            ExtraArgs = new[]
            {
                new RecipeExtraArg("enable-mamba-cache-stochastic-rounding", ""),
                new RecipeExtraArg("mamba-ssm-cache-dtype", "float16"),
            },
            ExtraEnv = new[]
            {
                new RecipeEnvOverride("VLLM_ALLOW_LONG_MAX_MODEL_LEN", "1"),
                new RecipeEnvOverride("VLLM_WORKER_MULTIPROC_METHOD", "spawn"),
            },
        };

        var result = ArgBuilder.Build(model, recipe, instance, server);

        Assert.Equal("/models/qwen3.8-int4", result.Argv[2]);
        var flags = ToFlagDictionary(result.Argv);
        Assert.Equal("qwen3.8-vllm", flags["--served-model-name"]);
        Assert.Equal("9999", flags["--port"]);
        Assert.Equal("float16", flags["--dtype"]);
        Assert.Null(flags["--enforce-eager"]);
        Assert.Null(flags["--enable-mamba-cache-stochastic-rounding"]);
        Assert.Equal("float16", flags["--mamba-ssm-cache-dtype"]);
        Assert.Equal(0.90, double.Parse(flags["--gpu-memory-utilization"]!));
        Assert.Equal("131072", flags["--max-model-len"]);
        Assert.Equal("fp8_e4m3", flags["--kv-cache-dtype"]);
        Assert.Equal("64", flags["--block-size"]);
        Assert.Equal("8192", flags["--max-num-batched-tokens"]);
        Assert.Null(flags["--enable-auto-tool-choice"]);
        Assert.Equal("qwen3_coder", flags["--tool-call-parser"]);
        Assert.Equal("qwen3", flags["--reasoning-parser"]);
        Assert.Equal("""{"method":"mtp","num_speculative_tokens":4}""", flags["--speculative-config"]);
        Assert.DoesNotContain("--quantization", flags.Keys);

        Assert.Equal("1", result.Env["VLLM_ALLOW_LONG_MAX_MODEL_LEN"]);
        Assert.Equal("spawn", result.Env["VLLM_WORKER_MULTIPROC_METHOD"]);
    }
}
