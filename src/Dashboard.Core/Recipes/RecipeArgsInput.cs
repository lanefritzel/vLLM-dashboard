namespace Dashboard.Core.Recipes;

/// <summary>The engine-independent part of a recipe: only the fields that mean the
/// same thing on every engine (P1). Engine-specific dials live on the derived
/// <see cref="VllmRecipe"/> / <see cref="LlamaCppRecipe"/>, so an engine's builder
/// can't even see another engine's dials -- structural, not runtime-ignored.
/// Deliberately not the EF entity (Dashboard.Infrastructure.Persistence.V2.Recipe):
/// pure code with no DB dependency (P09a).</summary>
public abstract record Recipe
{
    public string ServedModelName { get; init; } = "main";
    public int MaxModelLen { get; init; } = 8192;

    /// <summary>Key/value rows, no leading "--"; a blank value means a boolean flag.
    /// Applied last, after every dial, and in encounter order: a key that
    /// duplicates an earlier flag REPLACES its value in place rather than emitting
    /// the flag twice (doc 11 Phase 9).</summary>
    public IReadOnlyList<RecipeExtraArg> ExtraArgs { get; init; } = Array.Empty<RecipeExtraArg>();

    /// <summary>Env overrides for the engine process. Later entries win on a duplicate
    /// key. The compose-owned base env is merged in by the caller, not here.</summary>
    public IReadOnlyList<RecipeEnvOverride> ExtraEnv { get; init; } = Array.Empty<RecipeEnvOverride>();
}

/// <summary>vLLM's dial set, on top of the shared <see cref="Recipe"/> fields.</summary>
public sealed record VllmRecipe : Recipe
{
    public double GpuMemoryUtilization { get; init; } = 0.90;
    public string KvCacheDtype { get; init; } = "auto";
    public string Dtype { get; init; } = "auto";
    public bool EnforceEager { get; init; }
    public int MaxNumBatchedTokens { get; init; } = 8192;
    public int BlockSize { get; init; } = 16;
    public bool SpeculativeDecoding { get; init; }

    /// <summary>num_speculative_tokens; method is hard-coded to "mtp" (the only
    /// method the schema supports — owner-confirmed 2026-10-31). Ignored unless
    /// <see cref="SpeculativeDecoding"/> is true.</summary>
    public int? SpeculativeTokens { get; init; }

    public bool EnableAutoToolChoice { get; init; }
    public string? ToolCallParser { get; init; }
    public string? ReasoningParser { get; init; }
}

/// <summary>llama.cpp's dial set. Empty for now: the spike found only the shared
/// fields (served name -> --alias, max len -> --ctx-size, extra args/env) carry
/// over, and llama.cpp-native dials get added here when a phase needs them.</summary>
public sealed record LlamaCppRecipe : Recipe;

public sealed record RecipeExtraArg(string Key, string? Value);

public sealed record RecipeEnvOverride(string Key, string Value);

/// <summary>The model-intrinsic facts the arg builder needs — never anything the
/// user dials in directly (that's <see cref="Recipe"/>).</summary>
public sealed record RecipeModel
{
    /// <summary>Host path on the models volume, anywhere under models_dir (e.g.
    /// "&lt;models_dir&gt;/qwen" or "&lt;models_dir&gt;/hf-cache/qwen").</summary>
    public required string Path { get; init; }

    public string LoadFormat { get; init; } = "auto";

    /// <summary>Whether this model needs --trust-remote-code — read from the model's
    /// own config/catalog flag, never user-set directly.</summary>
    public bool TrustRemoteCode { get; init; }

    /// <summary>"available" | "downloading" | "download_failed" — a hard-block
    /// unless "available" (P09a compatibility pass).</summary>
    public string Status { get; init; } = "available";

    public long? SizeBytes { get; init; }

    /// <summary>From the model's parsed config.json, when present — drives the
    /// oversized-context-length soft-warning.</summary>
    public long? ConfigMaxPositionEmbeddings { get; init; }
}

public sealed record RecipeInstance
{
    public int Port { get; init; } = 9999;
}

public sealed record RecipeServer
{
    /// <summary>Host path prefix that the container sees as "/models" — substituted
    /// out of <see cref="RecipeModel.Path"/> to get the container-side path.</summary>
    public required string ModelsDir { get; init; }

    public int GpuCount { get; init; } = 1;
    public int? GpuMemGb { get; init; }

    /// <summary>Free-text GPU name (P09b). "Intel stack" = this contains "intel"
    /// (case-insensitive) — fragile by design, no vendor enum — used only to
    /// decide the dtype save-time rule in <see cref="CompatibilityPass"/>.</summary>
    public string? GpuName { get; init; }
}
