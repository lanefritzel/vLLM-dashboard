namespace Dashboard.Core.Models;

/// <summary>Runtime view of a Source (what the UI / selector needs).</summary>
public sealed record SourceInfo
{
    public string Id { get; init; } = "";
    public string Label { get; init; } = "";
    public SourceType Type { get; init; }
    public string MetricsUrl { get; init; } = "";
    public string InferenceUrl { get; init; } = "";
    public string? DockerHost { get; init; }
    public string? ContainerName { get; init; }
    public int ScrapeIntervalSeconds { get; init; } = 5;
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
