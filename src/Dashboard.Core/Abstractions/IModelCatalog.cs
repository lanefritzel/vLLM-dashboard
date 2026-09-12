using Dashboard.Core.Models;

namespace Dashboard.Core.Abstractions;

/// <summary>Per-physical-server model registry (shared by every vLLM instance on
/// that server) plus per-instance "which model is active/preloaded" state, which
/// lives on the instance rather than the model — see <see cref="ModelInfo"/> and
/// <see cref="SourceInfo.ActiveModelId"/> for why.</summary>
public interface IModelCatalog
{
    Task<IReadOnlyList<ModelInfo>> GetByPhysicalServerAsync(string physicalServerId, CancellationToken ct = default);

    Task<ModelInfo> CreateAsync(ModelInfo model, CancellationToken ct = default);
    Task<ModelInfo> UpdateAsync(ModelInfo model, CancellationToken ct = default);
    Task DeleteAsync(string modelId, CancellationToken ct = default);

    /// <summary>The model a given vLLM instance currently has loaded, or null if
    /// nothing is (or the bookkeeping has drifted from reality).</summary>
    Task<ModelInfo?> GetActiveAsync(string sourceId, CancellationToken ct = default);

    /// <summary>Marks the given model as the given instance's active one. Instances
    /// are independent — this never affects any other instance's own active model,
    /// even one on the same physical server.</summary>
    Task ActivateAsync(string sourceId, string modelId, CancellationToken ct = default);

    /// <summary>Marks the given instance as having nothing active — used by
    /// idle-unload, where the instance ends up with no active model at all until the
    /// next request swaps one back in.</summary>
    Task DeactivateAsync(string sourceId, CancellationToken ct = default);

    /// <summary>The model flagged to auto-load on this instance at dashboard
    /// startup, if any.</summary>
    Task<ModelInfo?> GetPreloadModelAsync(string sourceId, CancellationToken ct = default);

    /// <summary>Sets (or clears, with null) which model this instance should
    /// auto-load at dashboard startup.</summary>
    Task SetPreloadModelAsync(string sourceId, string? modelId, CancellationToken ct = default);
}
