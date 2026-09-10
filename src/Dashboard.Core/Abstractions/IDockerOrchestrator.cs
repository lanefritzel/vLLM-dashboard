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

    /// <summary>Asks the backend's own (real, not synthesized) <c>/v1/models</c> what
    /// it's actually serving right now — the source of truth over this app's own
    /// IsActive bookkeeping, which can drift (a model started outside the app, a crash,
    /// a host reboot). Returns null on any failure (unreachable, malformed, no models
    /// reported) — callers treat that as "unknown," not "definitely nothing."</summary>
    Task<string?> GetActuallyRunningModelNameAsync(SourceInfo source, CancellationToken ct = default);
}

/// <summary>Per-step progress reporting for hot-swap.</summary>
public interface IAsyncActionProgress
{
    Task ReportAsync(SwapStep step);
}

/// <summary>Shared no-op progress reporter for callers that trigger a swap without a
/// UI to report to (e.g. auto-swap-on-request, startup preload).</summary>
public sealed class NoOpProgress : IAsyncActionProgress
{
    public static readonly NoOpProgress Instance = new();
    public Task ReportAsync(SwapStep step) => Task.CompletedTask;
}
