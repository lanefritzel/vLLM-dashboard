namespace Dashboard.Core.Models;

/// <summary>Best-effort metadata about a Hugging Face repo, looked up live so the
/// model wizard/advanced page can warn about sizing before a swap, not after one
/// crashes. Any field can be null — a failed or partial lookup returns whatever it
/// could determine rather than nothing at all.</summary>
public sealed record HuggingFaceModelInfo
{
    public string RepoId { get; init; } = "";

    /// <summary>Total parameter count reported by Hugging Face's own safetensors
    /// metadata. For an already-quantized repo this reflects packed tensor element
    /// counts, not necessarily the "true" parameter count — treat it as directional
    /// for quantized repos, exact for unquantized ones.</summary>
    public long? ParameterCount { get; init; }

    public bool IsQuantized { get; init; }

    /// <summary>e.g. "auto-round", "awq", "gptq" — the method named in the repo's own
    /// config when present.</summary>
    public string? QuantizationMethod { get; init; }

    /// <summary>The model's own default max context length (tokens) from its
    /// published config — what vLLM will use if nothing overrides it. This is the
    /// exact value that caused the Gemma incident (262,144) when left unset.</summary>
    public int? DefaultMaxContextLength { get; init; }
}
