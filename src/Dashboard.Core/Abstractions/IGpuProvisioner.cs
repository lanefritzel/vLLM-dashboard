using Dashboard.Core.Models;

namespace Dashboard.Core.Abstractions;

/// <summary>
/// Applies one GPU vendor's passthrough mechanism to a Docker container-create
/// HostConfig. Implementations are pure/stateless; see <see cref="IGpuProvisionerFactory"/>
/// for picking the right one per <see cref="LaunchSpec.GpuVendor"/>.
/// </summary>
public interface IGpuProvisioner
{
    void Apply(Dictionary<string, object> hostConfig, LaunchSpec spec);
}

/// <summary>Creates the right <see cref="IGpuProvisioner"/> for a given GPU vendor.
/// Mirrors <see cref="IMetricsSourceFactory"/>'s shape: adding a new vendor (e.g. AMD
/// ROCm) means adding one provisioner + one switch case, not touching the orchestrator.</summary>
public interface IGpuProvisionerFactory
{
    IGpuProvisioner Create(GpuVendor vendor);
}
