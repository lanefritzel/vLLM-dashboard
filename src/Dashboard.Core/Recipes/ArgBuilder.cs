using System.Globalization;

namespace Dashboard.Core.Recipes;

public sealed record ArgBuilderResult
{
    public required IReadOnlyList<string> Argv { get; init; }
    public required IReadOnlyDictionary<string, string> Env { get; init; }
}

/// <summary>
/// Pure function (P09a): (model, recipe, instance, server) -> the exact argv + env
/// the supervisor should run. No I/O, no DB -- everything needed is passed in.
/// Fixed precedence: model-intrinsic flags, then recipe dials (in the order below),
/// then extra_args (which may override anything above by replacing a flag's value
/// in place rather than emitting it twice), then the instance's own --port.
/// </summary>
public static class ArgBuilder
{
    public static ArgBuilderResult Build(RecipeModel model, VllmRecipe recipe, RecipeInstance instance, RecipeServer server)
    {
        var argv = new List<string> { "vllm", "serve", ArgBuilderCommon.ContainerModelPath(model.Path, server.ModelsDir) };

        // 1 - model-intrinsic. Quantization is deliberately never emitted here --
        // passing --quantization on an already-quantized model crashes startup;
        // it's read from config.json for display only.
        if (string.Equals(model.LoadFormat, "gguf", StringComparison.OrdinalIgnoreCase))
            ArgBuilderCommon.AppendFlag(argv, "--load-format", "gguf");
        if (model.TrustRemoteCode)
            ArgBuilderCommon.AppendFlag(argv, "--trust-remote-code", null);

        // 2 - recipe dials
        ArgBuilderCommon.AppendFlag(argv, EngineFlags.VllmServedModelName, recipe.ServedModelName);
        ArgBuilderCommon.AppendFlag(argv, EngineFlags.VllmMaxModelLen, recipe.MaxModelLen.ToString(CultureInfo.InvariantCulture));
        // "0.####": a plain ToString() on a value that started life as a float
        // (e.g. Postgres "real", widened to this field's double) can print up to
        // 17 garbage digits (0.8f -> "0.800000011920929") -- rounds that away
        // while still showing up to 4 real decimal places.
        ArgBuilderCommon.AppendFlag(argv, "--gpu-memory-utilization", recipe.GpuMemoryUtilization.ToString("0.####", CultureInfo.InvariantCulture));
        if (!IsAuto(recipe.KvCacheDtype))
            ArgBuilderCommon.AppendFlag(argv, "--kv-cache-dtype", recipe.KvCacheDtype);
        if (!IsAuto(recipe.Dtype))
            ArgBuilderCommon.AppendFlag(argv, "--dtype", recipe.Dtype);
        if (recipe.EnforceEager)
            ArgBuilderCommon.AppendFlag(argv, "--enforce-eager", null);
        ArgBuilderCommon.AppendFlag(argv, "--max-num-batched-tokens", recipe.MaxNumBatchedTokens.ToString(CultureInfo.InvariantCulture));
        ArgBuilderCommon.AppendFlag(argv, "--block-size", recipe.BlockSize.ToString(CultureInfo.InvariantCulture));
        if (recipe.EnableAutoToolChoice)
            ArgBuilderCommon.AppendFlag(argv, "--enable-auto-tool-choice", null);
        if (!string.IsNullOrWhiteSpace(recipe.ToolCallParser))
            ArgBuilderCommon.AppendFlag(argv, "--tool-call-parser", recipe.ToolCallParser);
        if (!string.IsNullOrWhiteSpace(recipe.ReasoningParser))
            ArgBuilderCommon.AppendFlag(argv, "--reasoning-parser", recipe.ReasoningParser);
        if (recipe.SpeculativeDecoding && recipe.SpeculativeTokens is { } tokens)
        {
            ArgBuilderCommon.AppendFlag(argv, "--speculative-config",
                $$"""{"method":"mtp","num_speculative_tokens":{{tokens.ToString(CultureInfo.InvariantCulture)}}}""");
        }

        // 3 - extra_args: may override anything above
        foreach (var extra in recipe.ExtraArgs)
            ArgBuilderCommon.AppendFlag(argv, "--" + extra.Key, string.IsNullOrEmpty(extra.Value) ? null : extra.Value);

        // 4 - the instance's own port, injected last
        ArgBuilderCommon.AppendFlag(argv, "--port", instance.Port.ToString(CultureInfo.InvariantCulture));

        return new ArgBuilderResult { Argv = argv, Env = ArgBuilderCommon.BuildEnv(recipe) };
    }

    private static bool IsAuto(string value) => string.Equals(value, "auto", StringComparison.OrdinalIgnoreCase);
}
