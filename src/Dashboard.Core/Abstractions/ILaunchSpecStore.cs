using Dashboard.Core.Models;

namespace Dashboard.Core.Abstractions;

/// <summary>Launch spec CRUD (drives Docker hot-swap).</summary>
public interface ILaunchSpecStore
{
    Task<LaunchSpec?> GetBySourceAsync(string sourceId, CancellationToken ct = default);
    Task<LaunchSpec> SaveAsync(LaunchSpec spec, CancellationToken ct = default);
}
