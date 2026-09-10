using Dashboard.Core.Models;

namespace Dashboard.Core.Abstractions;

/// <summary>Source CRUD + enumeration (drives the polling service and selectors).</summary>
public interface ISourceCatalog
{
    Task<IReadOnlyList<SourceInfo>> GetAllAsync(CancellationToken ct = default);
    Task<IReadOnlyList<SourceInfo>> GetActiveAsync(CancellationToken ct = default);
    Task<SourceInfo?> GetByIdAsync(string id, CancellationToken ct = default);

    Task<SourceInfo> CreateAsync(SourceInfo source, CancellationToken ct = default);
    Task<SourceInfo> UpdateAsync(SourceInfo source, CancellationToken ct = default);
    Task DeleteAsync(string id, CancellationToken ct = default);
}
