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

/// <summary>A model registered on a physical server's shared catalog — every vLLM
/// instance on that server can load it, since they all read from the same disk (see
/// <see cref="Models.PhysicalServerInfo"/>). This is the "model-driven" half of
/// hot-swap: everything specific to running THIS model (where its weights are, which
/// flags it needs) lives here. Which instance currently has a given model loaded is
/// tracked on the instance itself (<see cref="SourceInfo.ActiveModelId"/>), not here
/// — the same model can be the active choice on one instance while a different
/// instance on the same box runs something else entirely.</summary>
public sealed record ModelInfo
{
    public string Id { get; init; } = "";
    public string PhysicalServerId { get; init; } = "";

    /// <summary>Deprecated: superseded by <see cref="PhysicalServerId"/> (a model is
    /// now registered on the physical server, shared by every vLLM instance on it,
    /// rather than owned by one instance). Kept in the schema for the upgrade
    /// backfill only.</summary>
    public string? SourceId { get; init; }

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

    /// <summary>Deprecated: superseded by <see cref="SourceInfo.ActiveModelId"/> —
    /// "active" is now a property of the instance running it, not the model itself
    /// (two instances on the same server can each have a different model active
    /// simultaneously). Kept in the schema for the upgrade backfill only.</summary>
    public bool IsActive { get; init; }

    public int SortOrder { get; init; }

    /// <summary>How long this model may sit idle (no proxied requests) before
    /// <c>IdleUnloadService</c> stops its container to free the GPU. Null/0 disables
    /// idle-unload for this model — it stays loaded until something else swaps it out.
    /// A property of the model's own tolerance for idling, independent of which
    /// instance happens to be running it.</summary>
    public int? IdleTtlSeconds { get; init; }

    /// <summary>Deprecated: superseded by <see cref="SourceInfo.PreloadModelId"/> —
    /// "preload this on startup" is now a property of the instance that should load
    /// it, not the model itself. Kept in the schema for the upgrade backfill only.</summary>
    public bool PreloadOnStartup { get; init; }

    /// <summary>True when editing from <paramref name="before"/> to <paramref name="after"/>
    /// changed something that actually feeds the launch command (see
    /// DockerOrchestrator.BuildCreatePayload) — a container already running this model
    /// won't pick the change up until it's recreated. DisplayName/SortOrder/IdleTtlSeconds/
    /// PhysicalServerId don't affect the launch command itself, so they're excluded.</summary>
    public static bool RequiresReload(ModelInfo before, ModelInfo after) =>
        !string.Equals(before.Name, after.Name, StringComparison.Ordinal) ||
        before.ModelSource != after.ModelSource ||
        !string.Equals(before.ModelPathOrRepoId, after.ModelPathOrRepoId, StringComparison.Ordinal) ||
        !string.Equals(before.ImageOverride, after.ImageOverride, StringComparison.Ordinal) ||
        !OptionsEqual(before.Options, after.Options);

    private static bool OptionsEqual(Dictionary<string, string> a, Dictionary<string, string> b)
    {
        if (a.Count != b.Count) return false;
        foreach (var (key, value) in a)
        {
            if (!b.TryGetValue(key, out var otherValue) || !string.Equals(value, otherValue, StringComparison.Ordinal))
                return false;
        }
        return true;
    }
}
