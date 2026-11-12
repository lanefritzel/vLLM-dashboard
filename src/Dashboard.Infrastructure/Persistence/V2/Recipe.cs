namespace Dashboard.Infrastructure.Persistence.V2;

/// <summary>A named, model-agnostic vLLM settings bundle (table <c>recipes</c>) — the
/// "spice" that replaces the legacy per-model launch options. Combined with a
/// <see cref="Model"/> to produce a <see cref="Deployment"/>.</summary>
public class Recipe
{
    public int Id { get; set; }
    public int ServerId { get; set; }
    public string Name { get; set; } = "";
    public string? Description { get; set; }

    // 1 - Identity
    public string ServedModelName { get; set; } = "main";

    // 2 - Sizing & memory
    public int MaxModelLen { get; set; } = 8192;
    public float GpuMemoryUtilization { get; set; } = 0.90f;
    public string KvCacheDtype { get; set; } = "auto";

    /// <summary>Save-time validation (P09b): bfloat16 is blocked on the Intel stack;
    /// unset/auto warns there.</summary>
    public string Dtype { get; set; } = "auto";

    // 3 - Performance
    public bool EnforceEager { get; set; }
    public int MaxNumBatchedTokens { get; set; } = 8192;
    public int BlockSize { get; set; } = 16;
    public bool SpeculativeDecoding { get; set; }

    /// <summary>num_speculative_tokens for --speculative-config; method is hard-coded
    /// to "mtp" (P09a, owner-confirmed 2026-10-31) since that's the only method the
    /// recipe schema supports today. Null when speculative decoding has no token
    /// count configured yet, even if SpeculativeDecoding is true.</summary>
    public int? SpeculativeTokens { get; set; }

    // 4 - Tools & reasoning
    public bool EnableAutoToolChoice { get; set; }
    public string? ToolCallParser { get; set; }
    public string? ReasoningParser { get; set; }

    /// <summary>llama.cpp-only (P6): "auto" | "all" | "cpu", stored via
    /// LlamaCppGpuLayersExtensions.ToStorage/FromStorage. Unread for a vLLM recipe.</summary>
    public string GpuLayers { get; set; } = "auto";

    // 5 - Advanced (escape hatches) -- key/value rows as jsonb; blank value = boolean flag.
    public string ExtraArgs { get; set; } = "[]";
    public string ExtraEnv { get; set; } = "[]";

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public Server? Server { get; set; }
}
