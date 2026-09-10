namespace Dashboard.Core.Models;

/// <summary>Logical grouping for the vLLM options catalog, mirroring vLLM's own
/// documented CLI section headings so the grouping isn't an arbitrary invention.</summary>
public enum VllmOptionGroup
{
    ModelAndWeights,
    MemoryAndCache,
    Parallelism,
    ToolAndReasoning,
    Multimodal,
    LoRA,
    Scheduler,
    Observability,
}

/// <summary>How a catalog entry should be rendered/edited, so the UI never needs a
/// free-text field for anything the dashboard knows about.</summary>
public enum VllmOptionControlType
{
    /// <summary>Presence-only flag (e.g. --enable-auto-tool-choice): one checkbox.</summary>
    Toggle,
    Integer,
    Float,
    /// <summary>Fixed dropdown of known-valid values.</summary>
    Enum,
    /// <summary>A single guided text field, for flags whose valid values are too
    /// version-volatile to hardcode as an Enum (e.g. --tool-call-parser).</summary>
    Text,
}

/// <summary>Common vs. advanced tier, so a group's card can show the common entries
/// by default and tuck the rest behind a nested disclosure.</summary>
public enum VllmOptionTier
{
    Common,
    Advanced,
}

/// <summary>One vLLM `serve` flag the dashboard knows how to render and emit safely.
/// Only entries here are ever placed on the command line for a model — nothing
/// user-typed reaches the container's command except through the input control.</summary>
public sealed record VllmOptionDefinition
{
    public required string Key { get; init; }
    public required string CliFlag { get; init; }
    public required string Label { get; init; }
    public required VllmOptionGroup Group { get; init; }
    public required VllmOptionControlType ControlType { get; init; }
    public VllmOptionTier Tier { get; init; } = VllmOptionTier.Common;
    public string? Description { get; init; }
    public string[]? AllowedValues { get; init; }
    public double? Min { get; init; }
    public double? Max { get; init; }
    public string? Unit { get; init; }
}

/// <summary>
/// Curated subset of vLLM's `serve` CLI surface (vLLM exposes on the order of 200
/// flags across many advanced/cluster-scale categories; this ships the ones
/// meaningfully useful on a single-box setup, grouped the way vLLM's own docs group
/// them). Extending this is additive: one more entry here, no UI rework, since the
/// Model form renders every group/entry generically from this list.
/// </summary>
public static class VllmOptionCatalog
{
    public static readonly IReadOnlyList<VllmOptionDefinition> All = new List<VllmOptionDefinition>
    {
        // Model & weights
        new()
        {
            Key = "dtype", CliFlag = "--dtype", Label = "Data type", Group = VllmOptionGroup.ModelAndWeights,
            ControlType = VllmOptionControlType.Enum, Tier = VllmOptionTier.Common,
            AllowedValues = new[] { "auto", "bfloat16", "float16", "float32" },
            Description = "Weight/activation precision. \"auto\" picks based on the model's own config.",
        },
        new()
        {
            Key = "quantization", CliFlag = "--quantization", Label = "Quantization", Group = VllmOptionGroup.ModelAndWeights,
            ControlType = VllmOptionControlType.Enum, Tier = VllmOptionTier.Common,
            AllowedValues = new[] { "none", "awq", "gptq", "fp8", "bitsandbytes", "marlin" },
            Description = "Only needed if the model wasn't already quantized at export time.",
        },
        new()
        {
            Key = "trust-remote-code", CliFlag = "--trust-remote-code", Label = "Trust remote code",
            Group = VllmOptionGroup.ModelAndWeights, ControlType = VllmOptionControlType.Toggle,
            Description = "Required by some models that ship custom modeling code on the Hub.",
        },
        new()
        {
            Key = "revision", CliFlag = "--revision", Label = "Revision", Group = VllmOptionGroup.ModelAndWeights,
            ControlType = VllmOptionControlType.Text, Tier = VllmOptionTier.Advanced,
            Description = "Specific HF branch, tag, or commit to load instead of the default.",
        },

        // Memory & cache
        new()
        {
            Key = "max-model-len", CliFlag = "--max-model-len", Label = "Context length",
            Group = VllmOptionGroup.MemoryAndCache, ControlType = VllmOptionControlType.Integer,
            Min = 1, Unit = "tokens", Description = "Maximum context window. Leave unset to use the model's own default.",
        },
        new()
        {
            Key = "gpu-memory-utilization", CliFlag = "--gpu-memory-utilization", Label = "GPU memory utilization",
            Group = VllmOptionGroup.MemoryAndCache, ControlType = VllmOptionControlType.Float,
            Min = 0, Max = 1, Description = "Fraction of GPU memory vLLM is allowed to reserve (0-1).",
        },
        new()
        {
            Key = "enable-prefix-caching", CliFlag = "--enable-prefix-caching", Label = "Enable prefix caching",
            Group = VllmOptionGroup.MemoryAndCache, ControlType = VllmOptionControlType.Toggle,
        },
        new()
        {
            Key = "kv-cache-dtype", CliFlag = "--kv-cache-dtype", Label = "KV cache data type",
            Group = VllmOptionGroup.MemoryAndCache, ControlType = VllmOptionControlType.Enum, Tier = VllmOptionTier.Advanced,
            AllowedValues = new[] { "auto", "fp8", "bfloat16", "float16" },
        },
        new()
        {
            Key = "cpu-offload-gb", CliFlag = "--cpu-offload-gb", Label = "CPU offload",
            Group = VllmOptionGroup.MemoryAndCache, ControlType = VllmOptionControlType.Float, Tier = VllmOptionTier.Advanced,
            Min = 0, Unit = "GiB", Description = "GiB of weights to offload to CPU memory per GPU.",
        },

        // Parallelism
        new()
        {
            Key = "tensor-parallel-size", CliFlag = "--tensor-parallel-size", Label = "Tensor parallel size",
            Group = VllmOptionGroup.Parallelism, ControlType = VllmOptionControlType.Integer, Min = 1,
        },
        new()
        {
            Key = "pipeline-parallel-size", CliFlag = "--pipeline-parallel-size", Label = "Pipeline parallel size",
            Group = VllmOptionGroup.Parallelism, ControlType = VllmOptionControlType.Integer, Tier = VllmOptionTier.Advanced, Min = 1,
        },
        new()
        {
            Key = "enable-expert-parallel", CliFlag = "--enable-expert-parallel", Label = "Enable expert parallel",
            Group = VllmOptionGroup.Parallelism, ControlType = VllmOptionControlType.Toggle, Tier = VllmOptionTier.Advanced,
            Description = "Mixture-of-experts models only.",
        },

        // Tool & reasoning calling
        new()
        {
            Key = "enable-auto-tool-choice", CliFlag = "--enable-auto-tool-choice", Label = "Enable auto tool choice",
            Group = VllmOptionGroup.ToolAndReasoning, ControlType = VllmOptionControlType.Toggle,
        },
        new()
        {
            Key = "tool-call-parser", CliFlag = "--tool-call-parser", Label = "Tool call parser",
            Group = VllmOptionGroup.ToolAndReasoning, ControlType = VllmOptionControlType.Text,
            Description = "Parser name for this model's tool-call format (e.g. qwen3_coder, hermes).",
        },
        new()
        {
            Key = "reasoning-parser", CliFlag = "--reasoning-parser", Label = "Reasoning parser",
            Group = VllmOptionGroup.ToolAndReasoning, ControlType = VllmOptionControlType.Text,
            Description = "Parser name for this model's reasoning/thinking output format.",
        },

        // Multimodal
        new()
        {
            Key = "mm-encoder-tp-mode", CliFlag = "--mm-encoder-tp-mode", Label = "Multimodal encoder TP mode",
            Group = VllmOptionGroup.Multimodal, ControlType = VllmOptionControlType.Enum, Tier = VllmOptionTier.Advanced,
            AllowedValues = new[] { "data", "weights" }, Description = "Only relevant for multimodal models.",
        },

        // LoRA
        new()
        {
            Key = "enable-lora", CliFlag = "--enable-lora", Label = "Enable LoRA",
            Group = VllmOptionGroup.LoRA, ControlType = VllmOptionControlType.Toggle, Tier = VllmOptionTier.Advanced,
        },
        new()
        {
            Key = "max-loras", CliFlag = "--max-loras", Label = "Max LoRAs per batch",
            Group = VllmOptionGroup.LoRA, ControlType = VllmOptionControlType.Integer, Tier = VllmOptionTier.Advanced, Min = 1,
        },
        new()
        {
            Key = "max-lora-rank", CliFlag = "--max-lora-rank", Label = "Max LoRA rank",
            Group = VllmOptionGroup.LoRA, ControlType = VllmOptionControlType.Enum, Tier = VllmOptionTier.Advanced,
            AllowedValues = new[] { "1", "8", "16", "32", "64", "128", "256", "320", "512" },
        },

        // Scheduler
        new()
        {
            Key = "max-num-batched-tokens", CliFlag = "--max-num-batched-tokens", Label = "Max batched tokens",
            Group = VllmOptionGroup.Scheduler, ControlType = VllmOptionControlType.Integer, Tier = VllmOptionTier.Advanced,
            Min = 1, Unit = "tokens",
        },

        // Observability
        new()
        {
            Key = "disable-log-stats", CliFlag = "--disable-log-stats", Label = "Disable log stats",
            Group = VllmOptionGroup.Observability, ControlType = VllmOptionControlType.Toggle, Tier = VllmOptionTier.Advanced,
        },
    };

    public static VllmOptionDefinition? Find(string key) => All.FirstOrDefault(o => o.Key == key);

    public static IEnumerable<IGrouping<VllmOptionGroup, VllmOptionDefinition>> ByGroup() =>
        All.GroupBy(o => o.Group);
}
