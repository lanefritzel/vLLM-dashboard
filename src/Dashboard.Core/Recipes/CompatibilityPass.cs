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
    public static CompatibilityResult Check(RecipeModel model, Recipe recipe, RecipeServer server)
    {
        var hardBlocks = new List<CompatibilityIssue>();
        var softWarnings = new List<CompatibilityIssue>();

        var tensorParallelSize = ExtractTensorParallelSize(recipe);
        if (tensorParallelSize > server.GpuCount)
        {
            hardBlocks.Add(new CompatibilityIssue(
                "tensor_parallel_exceeds_gpu_count",
                $"tensor-parallel-size ({tensorParallelSize}) exceeds this server's GPU count ({server.GpuCount})."));
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

        if (model.SizeBytes is { } sizeBytes && server.GpuMemGb is { } gpuMemGb && gpuMemGb > 0 && recipe.GpuMemoryUtilization > 0)
        {
            var fraction = sizeBytes / (gpuMemGb * 1e9 * recipe.GpuMemoryUtilization);
            if (fraction > 0.9)
            {
                softWarnings.Add(new CompatibilityIssue(
                    "gpu_memory_utilization_too_low",
                    $"gpu_memory_utilization ({recipe.GpuMemoryUtilization}) may be too low to fit this model's weights in this server's GPU memory."));
            }
        }

        return new CompatibilityResult { HardBlocks = hardBlocks, SoftWarnings = softWarnings };
    }

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
