using Dashboard.Core.Models;

namespace Dashboard.Core.Abstractions;

public sealed record SwapStep
{
    public string Step { get; init; } = "";
    public bool Complete { get; init; }
    public string? Message { get; init; }
}

/// <summary>Hot-swap the backend container for a source to a different model.</summary>
public interface IDockerOrchestrator
{
    Task<InferenceResult> SwapModelAsync(
        SourceInfo source, ModelInfo model, LaunchSpec spec,
        IAsyncActionProgress progress, CancellationToken ct = default);

    /// <summary>Stops and removes the source's current container without starting a
    /// replacement — used by idle-unload to actually free the GPU rather than just
    /// swap in something else. A no-op (success) if nothing was running.</summary>
    Task<InferenceResult> UnloadAsync(SourceInfo source, CancellationToken ct = default);

    Task<bool> IsDockerHostConfiguredAsync(SourceInfo source, CancellationToken ct = default);
}

/// <summary>Per-step progress reporting for hot-swap.</summary>
public interface IAsyncActionProgress
{
    Task ReportAsync(SwapStep step);
}
