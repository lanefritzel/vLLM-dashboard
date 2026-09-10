namespace Dashboard.Core.Models;

/// <summary>Where a model's weights come from — drives which guided field the Model
/// form shows and how DockerOrchestrator resolves the --model value at swap time.</summary>
public enum ModelSource
{
    HuggingFace,
    LocalPath,
}

public static class ModelSourceExtensions
{
    public static string ToStorage(this ModelSource source) => source switch
    {
        ModelSource.LocalPath => "local",
        _ => "huggingface",
    };

    public static ModelSource FromStorage(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "local" => ModelSource.LocalPath,
        _ => ModelSource.HuggingFace,
    };
}

/// <summary>A model in the per-source registry. This is the "model-driven" half of
/// hot-swap: everything specific to running THIS model (where its weights are, which
/// flags it needs) lives here, rather than in the shared per-source LaunchSpec.</summary>
public sealed record ModelInfo
{
    public string Id { get; init; } = "";
    public string SourceId { get; init; } = "";

    /// <summary>Served model name — passed as --served-model-name and matched against
    /// the post-swap health check response.</summary>
    public string Name { get; init; } = "";
    public string? DisplayName { get; init; }

    public ModelSource ModelSource { get; init; } = ModelSource.HuggingFace;

    /// <summary>An HF repo id when ModelSource is HuggingFace, or the host-side weights
    /// folder when ModelSource is LocalPath (DockerOrchestrator derives the container
    /// path and the actual --model value automatically for LocalPath).</summary>
    public string ModelPathOrRepoId { get; init; } = "";

    /// <summary>Overrides the source's default image for this model only. Null/blank
    /// means inherit the source's LaunchSpec.Image.</summary>
    public string? ImageOverride { get; init; }

    /// <summary>Catalog key -> value for the vLLM options this model overrides (see
    /// VllmOptionCatalog). Anything not present here uses vLLM's own built-in default —
    /// "use default" simply means the flag is never emitted.</summary>
    public Dictionary<string, string> Options { get; set; } = new();

    public bool IsActive { get; init; }
    public int SortOrder { get; init; }

    /// <summary>How long this model may sit idle (no proxied requests) before
    /// <c>IdleUnloadService</c> stops its container to free the GPU. Null/0 disables
    /// idle-unload for this model — it stays loaded until something else swaps it out.</summary>
    public int? IdleTtlSeconds { get; init; }

    /// <summary>Explicit opt-in: bring this model's container up when the dashboard
    /// itself starts, instead of waiting for the first proxied request. Only meaningful
    /// alongside IsActive — a preload-flagged model that isn't the active one is a no-op.</summary>
    public bool PreloadOnStartup { get; init; }
}
