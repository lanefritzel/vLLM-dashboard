using Dashboard.Core.Models;

namespace Dashboard.Infrastructure.Persistence;

public class LaunchSpecEntity
{
    public Guid Id { get; set; }
    public Guid SourceId { get; set; }
    public string Image { get; set; } = "";
    public string ModelArgTemplate { get; set; } = "--model {model}";
    public string PortsJson { get; set; } = "[]";
    public int? ServingPort { get; set; }
    public string EnvJson { get; set; } = "{}";
    public string VolumesJson { get; set; } = "[]";
    public string RestartPolicy { get; set; } = "unless-stopped";
    public string? ExtraRunArgs { get; set; }
    public string? Entrypoint { get; set; }
    public string? HfCacheHostPath { get; set; }
    public string? HfToken { get; set; }
    public int HealthWaitTimeoutSeconds { get; set; } = 600;
    public int? MemoryLimitMb { get; set; }
    public string? GpuOptions { get; set; }
    public string GpuVendor { get; set; } = "none";
    public string DevicesJson { get; set; } = "[]";
    public string NetworkMode { get; set; } = "bridge";
    public bool Privileged { get; set; }
    public string? IpcMode { get; set; }
    public string? ComposeProject { get; set; }
    public string? ComposeService { get; set; }

    public Source? Source { get; set; }

    public Core.Models.LaunchSpec ToInfo()
    {
        var spec = new Core.Models.LaunchSpec
        {
            Id = Id.ToString(),
            SourceId = SourceId.ToString(),
            Image = Image,
            ModelArgTemplate = ModelArgTemplate,
            ServingPort = ServingPort,
            RestartPolicy = RestartPolicy,
            ExtraRunArgs = ExtraRunArgs,
            Entrypoint = Entrypoint,
            HfCacheHostPath = HfCacheHostPath,
            HfToken = HfToken,
            HealthWaitTimeoutSeconds = HealthWaitTimeoutSeconds > 0 ? HealthWaitTimeoutSeconds : 600,
            MemoryLimitMb = MemoryLimitMb,
            GpuOptions = GpuOptions,
            GpuVendor = Core.Models.GpuVendorExtensions.FromStorage(GpuVendor),
            NetworkMode = string.IsNullOrWhiteSpace(NetworkMode) ? "bridge" : NetworkMode,
            Privileged = Privileged,
            IpcMode = IpcMode,
            ComposeProject = ComposeProject,
            ComposeService = ComposeService,
        };

        try
        {
            spec.Ports = System.Text.Json.JsonSerializer.Deserialize<List<Core.Models.PortMapping>>(PortsJson) ?? new();
            spec.Volumes = System.Text.Json.JsonSerializer.Deserialize<List<Core.Models.VolumeMapping>>(VolumesJson) ?? new();
            spec.Env = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(EnvJson) ?? new();
            spec.Devices = System.Text.Json.JsonSerializer.Deserialize<List<Core.Models.DeviceMapping>>(DevicesJson) ?? new();
        }
        catch
        {
            // Malformed JSON: leave defaults.
        }

        return spec;
    }

    public static LaunchSpecEntity FromInfo(Core.Models.LaunchSpec spec) => new()
    {
        Id = Guid.Parse(spec.Id),
        SourceId = Guid.Parse(spec.SourceId),
        Image = spec.Image,
        ModelArgTemplate = spec.ModelArgTemplate,
        PortsJson = System.Text.Json.JsonSerializer.Serialize(spec.Ports),
        ServingPort = spec.ServingPort,
        EnvJson = System.Text.Json.JsonSerializer.Serialize(spec.Env),
        VolumesJson = System.Text.Json.JsonSerializer.Serialize(spec.Volumes),
        RestartPolicy = spec.RestartPolicy,
        ExtraRunArgs = spec.ExtraRunArgs,
        Entrypoint = spec.Entrypoint,
        HfCacheHostPath = spec.HfCacheHostPath,
        HfToken = spec.HfToken,
        HealthWaitTimeoutSeconds = spec.HealthWaitTimeoutSeconds > 0 ? spec.HealthWaitTimeoutSeconds : 600,
        MemoryLimitMb = spec.MemoryLimitMb,
        GpuOptions = spec.GpuOptions,
        GpuVendor = spec.GpuVendor.ToStorage(),
        DevicesJson = System.Text.Json.JsonSerializer.Serialize(spec.Devices),
        NetworkMode = string.IsNullOrWhiteSpace(spec.NetworkMode) ? "bridge" : spec.NetworkMode,
        Privileged = spec.Privileged,
        IpcMode = spec.IpcMode,
        ComposeProject = spec.ComposeProject,
        ComposeService = spec.ComposeService,
    };
}
