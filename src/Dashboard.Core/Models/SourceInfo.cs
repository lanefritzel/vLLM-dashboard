namespace Dashboard.Core.Models;

/// <summary>Runtime view of a Source (what the UI / selector needs).</summary>
public sealed record SourceInfo
{
    public string Id { get; init; } = "";
    public string Slug { get; init; } = "";
    public string Label { get; init; } = "";
    public SourceType Type { get; init; }
    public string MetricsUrl { get; init; } = "";
    public string InferenceUrl { get; init; } = "";

    /// <summary>Deprecated: superseded by <see cref="PhysicalServerId"/> (a vLLM
    /// instance's Docker host now lives on its <see cref="Models.PhysicalServerInfo"/>,
    /// shared by every instance on that machine). Kept in the schema for the upgrade
    /// backfill only — the app itself never reads or writes it going forward.</summary>
    public string? DockerHost { get; init; }

    /// <summary>The physical (or virtual) machine this vLLM instance runs on — null
    /// for non-vLLM sources (Ollama/Mock), which never used Docker orchestration.
    /// Required for any vLLM source that wants hot-swap/idle-unload.</summary>
    public string? PhysicalServerId { get; init; }

    public string? ContainerName { get; init; }

    /// <summary>Which model (from the physical server's shared catalog) this
    /// instance currently has loaded — null if nothing's loaded, or if reality has
    /// drifted from this bookkeeping. Distinct instances on the same server track
    /// this independently, so one can hold a model permanently active while another
    /// gets hot-swapped freely.</summary>
    public string? ActiveModelId { get; init; }

    /// <summary>Which model this instance should automatically load when the
    /// dashboard itself starts, instead of waiting for the first proxied request.
    /// Null disables preload for this instance.</summary>
    public string? PreloadModelId { get; init; }

    public int ScrapeIntervalSeconds { get; init; } = 2;
    public bool Active { get; init; }
    public int SortOrder { get; init; }
    public DateTime CreatedAt { get; init; }
    public DateTime UpdatedAt { get; init; }
}

public sealed record SourceState
{
    public string Id { get; init; } = "";
    public string Label { get; init; } = "";
    public bool Up { get; init; }
    public string? Error { get; init; }
    public DateTime? LastSeen { get; init; }
}
