using System.Text.RegularExpressions;

namespace Dashboard.Core.Recipes;

public sealed record CompatibilityIssue(string Code, string Message);

public sealed record CompatibilityResult
{
    public IReadOnlyList<CompatibilityIssue> HardBlocks { get; init; } = Array.Empty<CompatibilityIssue>();
    public IReadOnlyList<CompatibilityIssue> SoftWarnings { get; init; } = Array.Empty<CompatibilityIssue>();
}

/// <summary>
/// Pure pre-flight checks (P09a) over the same inputs <see cref="ArgBuilder"/> takes.
/// Returns hard-blocks and soft-warnings; enforcing them (refusing a deploy, showing
/// a banner) is up to the caller (P09b save-time validation, P10a dispatch, the UI)
/// -- this type only decides what's wrong, never acts on it.
/// </summary>
public static class CompatibilityPass
{
    private static readonly Regex ServedModelNamePattern = new(@"^[A-Za-z0-9][A-Za-z0-9_.\-]*$", RegexOptions.Compiled);

    /// <summary>The subset of checks that need only a recipe + server, never a
    /// specific model -- a <see cref="Recipe"/> is model-agnostic (doc 2) and can
    /// be saved before any model is chosen for it, so these are the rules a
    /// recipe-only save can actually enforce. The model-dependent checks below
    /// (model path/availability/sizing) only make sense once a model is picked,
    /// i.e. at deploy pre-flight (P10a) -- see <see cref="Check"/>.</summary>
    public static CompatibilityResult CheckRecipe(Recipe recipe, RecipeServer server)
    {
        var hardBlocks = new List<CompatibilityIssue>();
        var softWarnings = new List<CompatibilityIssue>();

        if (recipe.MaxModelLen <= 0)
            hardBlocks.Add(new CompatibilityIssue("max_model_len_not_positive", $"max_model_len ({recipe.MaxModelLen}) must be greater than 0."));

        if (string.IsNullOrWhiteSpace(recipe.ServedModelName) || !ServedModelNamePattern.IsMatch(recipe.ServedModelName))
        {
            hardBlocks.Add(new CompatibilityIssue(
                "served_model_name_invalid",
                $"served_model_name ({recipe.ServedModelName}) must be a valid identifier (letters, digits, '.', '_', '-', not starting with one of the symbols)."));
        }

        // Engine-specific rules: a recipe is only checked against ITS engine's dial
        // set (P1) -- a llama.cpp recipe has no dtype/GPU-memory/tensor-parallel rules.
        if (recipe is VllmRecipe vllm)
            CheckVllmDials(vllm, server, hardBlocks, softWarnings);

        return new CompatibilityResult { HardBlocks = hardBlocks, SoftWarnings = softWarnings };
    }

    public static CompatibilityResult Check(RecipeModel model, Recipe recipe, RecipeServer server)
    {
        var recipeOnly = CheckRecipe(recipe, server);
        var hardBlocks = new List<CompatibilityIssue>(recipeOnly.HardBlocks);
        var softWarnings = new List<CompatibilityIssue>(recipeOnly.SoftWarnings);

        if (!string.IsNullOrWhiteSpace(model.Path) && !string.IsNullOrWhiteSpace(server.ModelsDir))
        {
            // Only the server's models_dir itself needs to be mounted into the
            // container (ArgBuilder.ContainerModelPath rewrites any path under
            // it to /models/...) -- a model can live anywhere underneath, in
            // whatever structure the user already has (their own hf-cache-style
            // layout, a downloaded repo straight under the root, nested folders,
            // etc.). This used to additionally require a literal "hf-cache"
            // subfolder, which only ever matched models the dashboard's own HF
            // download feature placed there -- any pre-existing/hand-registered
            // model was hard-blocked from ever deploying. hf-cache/ is still
            // where the dashboard's own downloads default to (see
            // supervisor/downloads.py's dest_dir), it's just no longer a
            // requirement for everything else.
            var expectedPrefix = NormalizePath(server.ModelsDir.TrimEnd('/', '\\')) + "/";
            var normalizedModelPath = NormalizePath(model.Path);
            if (!normalizedModelPath.StartsWith(expectedPrefix, StringComparison.Ordinal))
            {
                hardBlocks.Add(new CompatibilityIssue(
                    "model_path_outside_models_dir",
                    $"This model's path ({model.Path}) is not under this server's models_dir ({server.ModelsDir})."));
            }
        }

        if (string.IsNullOrWhiteSpace(model.Path))
            hardBlocks.Add(new CompatibilityIssue("model_path_missing", "This model has no path configured."));

        if (!string.Equals(model.Status, "available", StringComparison.Ordinal))
            hardBlocks.Add(new CompatibilityIssue("model_not_available", $"This model's status is '{model.Status}', not 'available'."));

        if (model.ConfigMaxPositionEmbeddings is { } maxPositionEmbeddings && recipe.MaxModelLen > maxPositionEmbeddings)
        {
            softWarnings.Add(new CompatibilityIssue(
                "max_model_len_exceeds_model_limit",
                $"max_model_len ({recipe.MaxModelLen}) exceeds this model's own max_position_embeddings ({maxPositionEmbeddings})."));
        }

        if (recipe is VllmRecipe vllm && model.SizeBytes is { } sizeBytes && server.GpuMemGb is { } gpuMemGb && gpuMemGb > 0 && vllm.GpuMemoryUtilization > 0)
        {
            var fraction = sizeBytes / (gpuMemGb * 1e9 * vllm.GpuMemoryUtilization);
            if (fraction > 0.9)
            {
                softWarnings.Add(new CompatibilityIssue(
                    "gpu_memory_utilization_too_low",
                    $"gpu_memory_utilization ({vllm.GpuMemoryUtilization}) may be too low to fit this model's weights in this server's GPU memory."));
            }
        }

        return new CompatibilityResult { HardBlocks = hardBlocks, SoftWarnings = softWarnings };
    }

    private static void CheckVllmDials(
        VllmRecipe recipe, RecipeServer server, List<CompatibilityIssue> hardBlocks, List<CompatibilityIssue> softWarnings)
    {
        // dtype rule (P09b v1, owner-confirmed 2026-11-01 -- the second qwen
        // incident): bfloat16 is hard-blocked on the Intel stack, since its
        // decode kernel only supports float16; unset/"auto" just warns there
        // (it may silently resolve to bfloat16 at runtime); float16 passes;
        // a non-Intel server gets no dtype issue at all. "Intel stack" =
        // server.GpuName contains "intel" (case-insensitive) -- fragile by
        // design, no vendor enum.
        var isIntelStack = server.GpuName?.Contains("intel", StringComparison.OrdinalIgnoreCase) ?? false;
        if (isIntelStack)
        {
            if (string.Equals(recipe.Dtype, "bfloat16", StringComparison.OrdinalIgnoreCase))
            {
                hardBlocks.Add(new CompatibilityIssue(
                    "dtype_bfloat16_blocked_on_intel",
                    "dtype=bfloat16 is blocked on the Intel stack -- its decode kernel only supports float16. Use dtype=float16."));
            }
            else if (string.IsNullOrWhiteSpace(recipe.Dtype) || string.Equals(recipe.Dtype, "auto", StringComparison.OrdinalIgnoreCase))
            {
                softWarnings.Add(new CompatibilityIssue(
                    "dtype_auto_on_intel",
                    "dtype is unset/auto on the Intel stack -- this may silently resolve to bfloat16 at runtime. Set dtype=float16 explicitly."));
            }
        }

        if (recipe.GpuMemoryUtilization <= 0 || recipe.GpuMemoryUtilization > 1)
        {
            hardBlocks.Add(new CompatibilityIssue(
                "gpu_memory_utilization_out_of_range",
                $"gpu_memory_utilization ({recipe.GpuMemoryUtilization}) must be greater than 0 and at most 1."));
        }

        var tensorParallelSize = ExtractTensorParallelSize(recipe);
        if (tensorParallelSize > server.GpuCount)
        {
            hardBlocks.Add(new CompatibilityIssue(
                "tensor_parallel_exceeds_gpu_count",
                $"tensor-parallel-size ({tensorParallelSize}) exceeds this server's GPU count ({server.GpuCount})."));
        }
    }

    private static string NormalizePath(string path) => path.Replace('\\', '/');

    /// <summary>tensor-parallel-size lives in extra_args, not a dedicated recipe
    /// field (hidden in the MVP UI) -- defaults to 1 (no tensor parallelism) when
    /// absent.</summary>
    private static int ExtractTensorParallelSize(Recipe recipe)
    {
        foreach (var extra in recipe.ExtraArgs)
        {
            if (string.Equals(extra.Key, "tensor-parallel-size", StringComparison.Ordinal) &&
                int.TryParse(extra.Value, out var value))
                return value;
        }
        return 1;
    }
}
