using Dashboard.Core.Models;

namespace Dashboard.Core.Abstractions;

/// <summary>Source CRUD + enumeration (drives the polling service and selectors).</summary>
public interface ISourceCatalog
{
    Task<IReadOnlyList<SourceInfo>> GetAllAsync(CancellationToken ct = default);
    Task<IReadOnlyList<SourceInfo>> GetActiveAsync(CancellationToken ct = default);
    Task<SourceInfo?> GetByIdAsync(string id, CancellationToken ct = default);

    /// <summary>Looks a source up by its GUID id OR its short slug (e.g. the
    /// "tes-vllm" in "/proxy/tes-vllm/v1/..."). Used by the proxy route, which
    /// accepts either so existing GUID-based clients keep working.</summary>
    Task<SourceInfo?> GetByIdOrSlugAsync(string idOrSlug, CancellationToken ct = default);

    Task<SourceInfo> CreateAsync(SourceInfo source, CancellationToken ct = default);
    Task<SourceInfo> UpdateAsync(SourceInfo source, CancellationToken ct = default);
    Task DeleteAsync(string id, CancellationToken ct = default);
}
