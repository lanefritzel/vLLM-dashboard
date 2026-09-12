using Dashboard.Core.Models;

namespace Dashboard.Core.Abstractions;

/// <summary>Physical server CRUD + enumeration. A physical server is the machine a
/// vLLM instance (<see cref="SourceInfo"/>) runs on; see
/// <see cref="Models.PhysicalServerInfo"/> for what it owns.</summary>
public interface IPhysicalServerCatalog
{
    Task<IReadOnlyList<PhysicalServerInfo>> GetAllAsync(CancellationToken ct = default);
    Task<PhysicalServerInfo?> GetByIdAsync(string id, CancellationToken ct = default);
    Task<PhysicalServerInfo> CreateAsync(PhysicalServerInfo server, CancellationToken ct = default);
    Task<PhysicalServerInfo> UpdateAsync(PhysicalServerInfo server, CancellationToken ct = default);
    Task DeleteAsync(string id, CancellationToken ct = default);
}
