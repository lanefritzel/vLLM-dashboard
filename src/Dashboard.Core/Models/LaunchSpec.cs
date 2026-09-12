namespace Dashboard.Core.Models;

// Settable (not init) rather than the record default so Blazor's @bind can two-way
// bind directly to entries of a List<T> of these in the launch-spec row editors.

public sealed record PortMapping
{
    public string Host { get; set; } = "";
    public string Container { get; set; } = "";
}

public sealed record VolumeMapping
{
    public string Host { get; set; } = "";
    public string Container { get; set; } = "";
    public bool ReadOnly { get; set; }
}

/// <summary>A raw Docker <c>--device</c> passthrough entry (e.g. Intel's /dev/dri).</summary>
public sealed record DeviceMapping
{
    public string HostPath { get; set; } = "";
    public string ContainerPath { get; set; } = "";
}

/// <summary>Which GPU vendor's passthrough mechanism a launch spec uses. NVIDIA is
/// reserved via the nvidia-container-runtime's DeviceRequests API (see
/// <see cref="LaunchSpec.GpuOptions"/>); Intel (and anything else needing raw device
/// nodes) goes through <see cref="LaunchSpec.Devices"/>.</summary>
public enum GpuVendor
{
    None,
    Nvidia,
    Intel,
}

public static class GpuVendorExtensions
{
    public static string ToStorage(this GpuVendor vendor) => vendor switch
    {
        GpuVendor.Nvidia => "nvidia",
        GpuVendor.Intel => "intel",
        _ => "none",
    };

    public static GpuVendor FromStorage(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "nvidia" => GpuVendor.Nvidia,
        "intel" => GpuVendor.Intel,
        _ => GpuVendor.None,
    };
}

/// <summary>Everything needed to (re)create a backend container for a source. This is
/// the "Server settings" half of hot-swap — things true of the physical box/Docker
/// host regardless of which model is running. Model-specific things (weights
/// location, launch flags) live on <see cref="ModelInfo"/> instead.</summary>
public sealed record LaunchSpec
{
    public string Id { get; init; } = "";
    public string SourceId { get; init; } = "";

    /// <summary>Default image; a model's ImageOverride wins when set.</summary>
    public string Image { get; init; } = "";

    /// <summary>Deprecated/vestigial: command construction is now catalog-driven (see
    /// VllmOptionCatalog + DockerOrchestrator), not template substitution. Kept only so
    /// SubstituteModel and its existing tests keep working; not read by the orchestrator.</summary>
    public string ModelArgTemplate { get; init; } = "--model {model}";

    /// <summary>Deprecated/vestigial: superseded by ServingPort (a single host=container
    /// mapping is all this use case needs). Kept, unused, rather than a destructive drop.</summary>
    public List<PortMapping> Ports { get; set; } = new();

    /// <summary>The one port this source's backend serves on (host and container use the
    /// same value) — matches Source.InferenceUrl/MetricsUrl already assuming one port.</summary>
    public int? ServingPort { get; init; }

    /// <summary>Advanced escape hatch for env vars not covered by a guided field
    /// (HF_HOME/HF_TOKEN are synthesized automatically from HfCacheHostPath/HfToken).</summary>
    public Dictionary<string, string> Env { get; set; } = new();

    /// <summary>Advanced escape hatch for volume mounts not covered by a guided field.</summary>
    public List<VolumeMapping> Volumes { get; set; } = new();

    public string RestartPolicy { get; init; } = "unless-stopped";

    /// <summary>Flags shared by every model on this source, appended after the
    /// per-model options — for boilerplate that never varies by model.</summary>
    public string? ExtraRunArgs { get; init; }

    /// <summary>Overrides the image's own entrypoint when set (space-split), e.g. "vllm"
    /// for images whose default entrypoint isn't already the vllm CLI.</summary>
    public string? Entrypoint { get; init; }

    /// <summary>Deprecated: superseded by <see cref="Models.PhysicalServerInfo.HfCacheHostPath"/>
    /// (shared by every vLLM instance on the same machine, rather than re-entered per
    /// instance — the exact gap that let one instance silently have no cache at all
    /// while another on the same box did). Kept in the schema for the upgrade backfill
    /// only; the app itself never reads or writes it going forward.</summary>
    public string? HfCacheHostPath { get; init; }

    /// <summary>Deprecated: superseded by <see cref="Models.PhysicalServerInfo.HfToken"/>.
    /// Kept in the schema for the upgrade backfill only.</summary>
    public string? HfToken { get; init; }

    /// <summary>How long to wait for the post-swap health check before giving up.
    /// Higher than a plain "container is starting" wait needs to be, since a
    /// first-time swap to an uncached HF model downloads its weights first.</summary>
    public int HealthWaitTimeoutSeconds { get; init; } = 600;

    /// <summary>Hard cap on the container's system RAM usage (megabytes), enforced by
    /// Docker itself (a cgroup memory limit) — not a vLLM setting. Null means no limit.
    /// This protects system RAM only. On a discrete GPU, VRAM is a completely separate
    /// memory pool this limit has no effect on at all — see
    /// <see cref="GpuVramTotalGb"/> and a model's <c>gpu-memory-utilization</c> option
    /// for the setting that actually bounds VRAM.</summary>
    public int? MemoryLimitMb { get; init; }

    /// <summary>Which specific device(s) <em>this instance</em> uses, for a physical
    /// server with more than one GPU (each instance can be pinned to a different one).
    /// NVIDIA-only: device ids for the nvidia-container-runtime DeviceRequests API
    /// ("all" or a comma-separated list like "0,1"). Only meaningful when the linked
    /// <see cref="Models.PhysicalServerInfo.GpuVendor"/> is
    /// <see cref="Models.GpuVendor.Nvidia"/>.</summary>
    public string? GpuOptions { get; init; }

    /// <summary>Deprecated: superseded by <see cref="Models.PhysicalServerInfo.GpuVendor"/>
    /// (the GPU is a property of the physical machine, shared by every instance on it —
    /// see <see cref="GpuOptions"/>/<see cref="Devices"/> for per-instance device
    /// *selection* on a multi-GPU box, which stays here). Kept in the schema for the
    /// upgrade backfill only.</summary>
    public GpuVendor GpuVendor { get; init; } = GpuVendor.None;

    /// <summary>Deprecated: superseded by <see cref="Models.PhysicalServerInfo.GpuVramTotalGb"/>.
    /// Kept in the schema for the upgrade backfill only.</summary>
    public int? GpuVramTotalGb { get; init; }

    /// <summary>Which specific device(s) <em>this instance</em> uses. Raw --device
    /// passthrough entries. Only meaningful when the linked
    /// <see cref="Models.PhysicalServerInfo.GpuVendor"/> is
    /// <see cref="Models.GpuVendor.Intel"/>.</summary>
    public List<DeviceMapping> Devices { get; set; } = new();

    public string NetworkMode { get; init; } = "bridge";
    public bool Privileged { get; init; }
    public string? IpcMode { get; init; }

    /// <summary>The Compose project/service labels (com.docker.compose.project/service)
    /// last observed on a real container for this source, so they can be stamped back
    /// onto a replacement even when there's no existing container left to inspect —
    /// e.g. after idle-unload deletes it, before the next request creates a fresh one.
    /// Discovered and persisted automatically by DockerOrchestrator; never set by the UI.</summary>
    public string? ComposeProject { get; init; }
    public string? ComposeService { get; init; }

    /// <summary>Substitutes <c>{model}</c> in the template with the given value.</summary>
    public string SubstituteModel(string modelValue) =>
        ModelArgTemplate.Replace("{model}", modelValue);
}
