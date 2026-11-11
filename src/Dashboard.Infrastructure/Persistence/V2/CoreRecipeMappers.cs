using System.Text.Json;
using Dashboard.Core.Models;
using Dashboard.Core.Recipes;

namespace Dashboard.Infrastructure.Persistence.V2;

/// <summary>Maps the EF entities in this namespace to the pure, DB-free input
/// shapes <see cref="ArgBuilder"/>/<see cref="CompatibilityPass"/> take (P09a).
/// Shared by <see cref="RecipeService"/> (save-time validation) and the swap
/// orchestration service (P10a, which also needs to actually BUILD the argv,
/// not just validate).</summary>
internal static class CoreRecipeMappers
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public static VllmRecipe ToCoreRecipe(Recipe recipe) => new()
    {
        ServedModelName = recipe.ServedModelName,
        MaxModelLen = recipe.MaxModelLen,
        GpuMemoryUtilization = recipe.GpuMemoryUtilization,
        KvCacheDtype = recipe.KvCacheDtype,
        Dtype = recipe.Dtype,
        EnforceEager = recipe.EnforceEager,
        MaxNumBatchedTokens = recipe.MaxNumBatchedTokens,
        BlockSize = recipe.BlockSize,
        SpeculativeDecoding = recipe.SpeculativeDecoding,
        SpeculativeTokens = recipe.SpeculativeTokens,
        EnableAutoToolChoice = recipe.EnableAutoToolChoice,
        ToolCallParser = recipe.ToolCallParser,
        ReasoningParser = recipe.ReasoningParser,
        ExtraArgs = ParseExtraArgs(recipe.ExtraArgs),
        ExtraEnv = ParseExtraEnv(recipe.ExtraEnv),
    };

    /// <summary>The recipe in the pure shape for the given engine. A llama.cpp recipe keeps only
    /// the shared fields -- the entity's vLLM-only columns are simply not read.</summary>
    public static Dashboard.Core.Recipes.Recipe ToCoreRecipe(Recipe recipe, SourceType engine) => engine switch
    {
        SourceType.Vllm => ToCoreRecipe(recipe),
        SourceType.LlamaCpp => new LlamaCppRecipe
        {
            ServedModelName = recipe.ServedModelName,
            MaxModelLen = recipe.MaxModelLen,
            ExtraArgs = ParseExtraArgs(recipe.ExtraArgs),
            ExtraEnv = ParseExtraEnv(recipe.ExtraEnv),
        },
        _ => throw new NotSupportedException($"No recipe mapping for engine '{engine}'."),
    };

    public static RecipeServer ToCoreServer(Server server) => new()
    {
        ModelsDir = server.ModelsDir,
        GpuCount = server.GpuCount,
        GpuMemGb = server.GpuMemGb,
        GpuName = server.GpuName,
    };

    public static RecipeModel ToCoreModel(Model model) => new()
    {
        Path = model.Path,
        LoadFormat = model.LoadFormat,
        TrustRemoteCode = false, // no stored flag for this yet (P09b's Model entity doesn't carry it)
        Status = model.Status,
        SizeBytes = model.SizeBytes,
        ConfigMaxPositionEmbeddings = ReadNativeContextLength(model.Config),
    };

    public static RecipeInstance ToCoreInstance(Instance instance) => new() { Port = instance.Port };

    /// <summary>config.json's max_position_embeddings, if the model's stored
    /// config has one -- also what RecipeService.CreateFromModelAsync
    /// pre-fills max_model_len with (doc 8 §4).</summary>
    public static int? ReadNativeContextLength(string? config)
    {
        if (string.IsNullOrWhiteSpace(config))
            return null;
        try
        {
            using var doc = JsonDocument.Parse(config);
            if (doc.RootElement.TryGetProperty("max_position_embeddings", out var value) && value.TryGetInt64(out var length))
                return (int)Math.Clamp(length, int.MinValue, int.MaxValue);
        }
        catch (JsonException)
        {
            // Unreadable config -- fall back to the recipe schema's own default,
            // same "downloaded but config unreadable" tolerance P08 uses.
        }
        return null;
    }

    public static IReadOnlyList<RecipeExtraArg> ParseExtraArgs(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return Array.Empty<RecipeExtraArg>();
        var raw = JsonSerializer.Deserialize<List<Dictionary<string, string?>>>(json, JsonOptions) ?? new();
        return raw
            .Where(d => d.TryGetValue("key", out var k) && !string.IsNullOrWhiteSpace(k))
            .Select(d => new RecipeExtraArg(d["key"]!, d.GetValueOrDefault("value")))
            .ToList();
    }

    public static IReadOnlyList<RecipeEnvOverride> ParseExtraEnv(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return Array.Empty<RecipeEnvOverride>();
        var raw = JsonSerializer.Deserialize<List<Dictionary<string, string?>>>(json, JsonOptions) ?? new();
        return raw
            .Where(d => d.TryGetValue("key", out var k) && !string.IsNullOrWhiteSpace(k) && !string.IsNullOrEmpty(d.GetValueOrDefault("value")))
            .Select(d => new RecipeEnvOverride(d["key"]!, d["value"]!))
            .ToList();
    }
}
