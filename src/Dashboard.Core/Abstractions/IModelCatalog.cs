using Dashboard.Core.Models;

namespace Dashboard.Core.Abstractions;

/// <summary>Per-source model registry with the single-active invariant.</summary>
public interface IModelCatalog
{
    Task<IReadOnlyList<ModelInfo>> GetBySourceAsync(string sourceId, CancellationToken ct = default);
    Task<ModelInfo?> GetActiveAsync(string sourceId, CancellationToken ct = default);

    Task<ModelInfo> CreateAsync(ModelInfo model, CancellationToken ct = default);
    Task<ModelInfo> UpdateAsync(ModelInfo model, CancellationToken ct = default);
    Task DeleteAsync(string modelId, CancellationToken ct = default);
    Task ActivateAsync(string modelId, CancellationToken ct = default);

    /// <summary>Marks a model inactive without activating any replacement — used by
    /// idle-unload, where the source ends up with no active model at all until the
    /// next request swaps one back in.</summary>
    Task DeactivateAsync(string modelId, CancellationToken ct = default);
}
