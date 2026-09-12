namespace Dashboard.Core.Models;

/// <summary>A physical (or virtual) machine that can run one or more vLLM instances
/// (<see cref="SourceInfo"/> records with <see cref="SourceInfo.PhysicalServerId"/>
/// set). Properties genuinely tied to the hardware/OS itself — the Docker daemon's own
/// API address, the GPU installed, and a persistent Hugging Face cache shared by every
/// instance on this box — live here instead of being re-entered per instance, which is
/// what let two different vLLM instances on the same real box silently diverge (one
/// with no cache directory at all) and collide on the same container name.</summary>
public sealed record PhysicalServerInfo
{
    public string Id { get; init; } = "";
    public string Label { get; init; } = "";

    /// <summary>The Docker Engine API address for this machine (e.g.
    /// "tcp://host:2375") — shared by every vLLM instance that runs on it.</summary>
    public string DockerHost { get; init; } = "";

    public GpuVendor GpuVendor { get; init; } = GpuVendor.None;

    /// <summary>Informational only — not read by anything that launches a container.
    /// The GPU's actual total video memory, so the UI can warn when a model's expected
    /// footprint is likely to exceed what the card actually has.</summary>
    public int? GpuVramTotalGb { get; init; }

    /// <summary>Host path for a persistent Hugging Face cache, shared by every vLLM
    /// instance on this machine so the same repo is never downloaded twice for two
    /// instances on the same box.</summary>
    public string? HfCacheHostPath { get; init; }

    /// <summary>Mapped to the HF_TOKEN env var for gated/private Hugging Face repos.
    /// Shared by every instance on this machine.</summary>
    public string? HfToken { get; init; }

    public DateTime CreatedAt { get; init; }
    public DateTime UpdatedAt { get; init; }
}
