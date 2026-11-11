namespace Dashboard.Core.Models;

/// <summary>What each inference engine can report or support, so the UI can say "not available"
/// rather than render a misleading blank/zero. Values are confirmed against real engines
/// (Findings docs): llama.cpp's /metrics has no KV-cache usage and its startup has no warm-up
/// phase; the neuron-map plugin hooks vLLM's plugin entry point and exists only there.</summary>
public static class EngineCapabilities
{
    public static bool ReportsKvCacheUsage(SourceType engine) => engine == SourceType.Vllm;
    public static bool HasNeuronMap(SourceType engine) => engine == SourceType.Vllm;
    public static bool HasWarmupPhase(SourceType engine) => engine == SourceType.Vllm;

    /// <summary>The ONE list of engines a user can choose for an instance. The picker, the label
    /// lookup and save-time validation are all derived from it, so adding an engine means adding a
    /// row here (plus its arg builder, which a test requires) and nothing else. The storage string
    /// comes from <see cref="SourceTypeExtensions.ToStorage"/>, never a second literal.</summary>
    public static IReadOnlyList<EngineOption> Options { get; } =
    [
        new(SourceType.Vllm, SourceType.Vllm.ToStorage(), "vLLM"),
        new(SourceType.LlamaCpp, SourceType.LlamaCpp.ToStorage(), "llama.cpp"),
    ];

    /// <summary>What a new instance starts as.</summary>
    public static EngineOption DefaultEngine => Options[0];

    /// <summary>User-facing engine name for labels and messages.</summary>
    public static string DisplayName(SourceType engine) =>
        Options.FirstOrDefault(o => o.Type == engine)?.Label ?? engine.ToString();

    /// <summary>True only for the exact storage string of a selectable engine.
    /// <see cref="SourceTypeExtensions.FromStorage"/> quietly maps anything unrecognised to vLLM, so a
    /// save must reject a typo rather than let it become vLLM.</summary>
    public static bool IsKnownEngine(string? value) => Options.Any(o => o.Storage == value);
}

/// <summary>One selectable engine: its type, the string persisted on an instance, and its label.</summary>
public sealed record EngineOption(SourceType Type, string Storage, string Label);
