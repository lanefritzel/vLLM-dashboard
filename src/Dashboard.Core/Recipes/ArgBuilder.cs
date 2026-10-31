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
    public static ArgBuilderResult Build(RecipeModel model, Recipe recipe, RecipeInstance instance, RecipeServer server)
    {
        var argv = new List<string> { "vllm", "serve", ContainerModelPath(model.Path, server.ModelsDir) };

        // 1 - model-intrinsic. Quantization is deliberately never emitted here --
        // passing --quantization on an already-quantized model crashes startup;
        // it's read from config.json for display only.
        if (string.Equals(model.LoadFormat, "gguf", StringComparison.OrdinalIgnoreCase))
            AppendFlag(argv, "--load-format", "gguf");
        if (model.TrustRemoteCode)
            AppendFlag(argv, "--trust-remote-code", null);

        // 2 - recipe dials
        AppendFlag(argv, "--served-model-name", recipe.ServedModelName);
        AppendFlag(argv, "--max-model-len", recipe.MaxModelLen.ToString(CultureInfo.InvariantCulture));
        // "0.####": a plain ToString() on a value that started life as a float
        // (e.g. Postgres "real", widened to this field's double) can print up to
        // 17 garbage digits (0.8f -> "0.800000011920929") -- rounds that away
        // while still showing up to 4 real decimal places.
        AppendFlag(argv, "--gpu-memory-utilization", recipe.GpuMemoryUtilization.ToString("0.####", CultureInfo.InvariantCulture));
        if (!IsAuto(recipe.KvCacheDtype))
            AppendFlag(argv, "--kv-cache-dtype", recipe.KvCacheDtype);
        if (!IsAuto(recipe.Dtype))
            AppendFlag(argv, "--dtype", recipe.Dtype);
        if (recipe.EnforceEager)
            AppendFlag(argv, "--enforce-eager", null);
        AppendFlag(argv, "--max-num-batched-tokens", recipe.MaxNumBatchedTokens.ToString(CultureInfo.InvariantCulture));
        AppendFlag(argv, "--block-size", recipe.BlockSize.ToString(CultureInfo.InvariantCulture));
        if (recipe.EnableAutoToolChoice)
            AppendFlag(argv, "--enable-auto-tool-choice", null);
        if (!string.IsNullOrWhiteSpace(recipe.ToolCallParser))
            AppendFlag(argv, "--tool-call-parser", recipe.ToolCallParser);
        if (!string.IsNullOrWhiteSpace(recipe.ReasoningParser))
            AppendFlag(argv, "--reasoning-parser", recipe.ReasoningParser);
        if (recipe.SpeculativeDecoding && recipe.SpeculativeTokens is { } tokens)
        {
            AppendFlag(argv, "--speculative-config",
                $$"""{"method":"mtp","num_speculative_tokens":{{tokens.ToString(CultureInfo.InvariantCulture)}}}""");
        }

        // 3 - extra_args: may override anything above
        foreach (var extra in recipe.ExtraArgs)
            AppendFlag(argv, "--" + extra.Key, string.IsNullOrEmpty(extra.Value) ? null : extra.Value);

        // 4 - the instance's own port, injected last
        AppendFlag(argv, "--port", instance.Port.ToString(CultureInfo.InvariantCulture));

        var env = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var e in recipe.ExtraEnv)
            env[e.Key] = e.Value; // later entries win on a duplicate key

        return new ArgBuilderResult { Argv = argv, Env = env };
    }

    private static bool IsAuto(string value) => string.Equals(value, "auto", StringComparison.OrdinalIgnoreCase);

    /// <summary>Appends "flag [value]" to argv, or — if that flag is already present
    /// — replaces its value in place (or removes a stale value if it's now a bare
    /// toggle), so a later caller (extra_args overriding a dial) never produces the
    /// same flag twice.</summary>
    private static void AppendFlag(List<string> argv, string flag, string? value)
    {
        var existingIndex = argv.IndexOf(flag);
        if (existingIndex >= 0)
        {
            var hadValue = existingIndex + 1 < argv.Count && !argv[existingIndex + 1].StartsWith("--", StringComparison.Ordinal);
            if (hadValue)
                argv.RemoveAt(existingIndex + 1);
            if (value is not null)
                argv.Insert(existingIndex + 1, value);
            return;
        }

        argv.Add(flag);
        if (value is not null)
            argv.Add(value);
    }

    /// <summary>Host models.path with the server's models_dir prefix replaced by
    /// /models (e.g. &lt;models_dir&gt;/hf-cache/&lt;slug&gt; -> /models/hf-cache/&lt;slug&gt;).
    /// Leaves the path untouched if it doesn't actually start with that prefix --
    /// a mismatch here is a data problem for the compatibility pass to flag
    /// (and, pre-conversion, for P01b's report), not something to silently guess at.</summary>
    private static string ContainerModelPath(string hostPath, string modelsDir)
    {
        var root = modelsDir.TrimEnd('/');
        return hostPath.StartsWith(root, StringComparison.Ordinal)
            ? "/models" + hostPath[root.Length..]
            : hostPath;
    }
}
