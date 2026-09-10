using Dashboard.Core.Abstractions;
using Dashboard.Core.Models;

namespace Dashboard.Infrastructure.Docker;

/// <summary>No GPU passthrough. Default for sources that don't need one.</summary>
public sealed class NoneGpuProvisioner : IGpuProvisioner
{
    public void Apply(Dictionary<string, object> hostConfig, LaunchSpec spec)
    {
        // Intentionally does nothing.
    }
}

/// <summary>
/// NVIDIA passthrough via the nvidia-container-runtime's DeviceRequests API.
/// Reads <see cref="LaunchSpec.GpuOptions"/> as either "all" or a comma-separated
/// list of device ids (e.g. "0,1"); blank/whitespace defaults to "all" to preserve
/// prior behavior from before per-id parsing existed.
/// </summary>
public sealed class NvidiaGpuProvisioner : IGpuProvisioner
{
    public void Apply(Dictionary<string, object> hostConfig, LaunchSpec spec)
    {
        var raw = spec.GpuOptions?.Trim();
        var deviceIds = string.IsNullOrWhiteSpace(raw) || raw.Equals("all", StringComparison.OrdinalIgnoreCase)
            ? new[] { "all" }
            : raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        hostConfig["DeviceRequests"] = new[]
        {
            new { Driver = "nvidia", DeviceIds = deviceIds, Capabilities = new[] { new[] { "gpu" } } }
        };
    }
}

/// <summary>
/// Raw Docker <c>--device</c> passthrough (e.g. Intel's /dev/dri) via
/// <see cref="LaunchSpec.Devices"/>. Works for Intel today; equally usable for any
/// other hardware that's exposed as a plain device node rather than through a
/// vendor container runtime.
/// </summary>
public sealed class IntelGpuProvisioner : IGpuProvisioner
{
    public void Apply(Dictionary<string, object> hostConfig, LaunchSpec spec)
    {
        if (spec.Devices.Count == 0) return;

        hostConfig["Devices"] = spec.Devices.Select(d => new
        {
            PathOnHost = d.HostPath,
            PathInContainer = string.IsNullOrWhiteSpace(d.ContainerPath) ? d.HostPath : d.ContainerPath,
            CgroupPermissions = "rwm",
        }).ToArray();
    }
}

/// <summary>Builds the right <see cref="IGpuProvisioner"/> for a given vendor.</summary>
public sealed class GpuProvisionerFactory : IGpuProvisionerFactory
{
    public IGpuProvisioner Create(GpuVendor vendor) => vendor switch
    {
        GpuVendor.Nvidia => new NvidiaGpuProvisioner(),
        GpuVendor.Intel => new IntelGpuProvisioner(),
        _ => new NoneGpuProvisioner(),
    };
}
