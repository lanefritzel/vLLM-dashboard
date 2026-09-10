namespace Dashboard.Core.Models;

/// <summary>A model in the per-source registry.</summary>
public sealed record ModelInfo
{
    public string Id { get; init; } = "";
    public string SourceId { get; init; } = "";
    public string Name { get; init; } = "";
    public string? DisplayName { get; init; }
    public string? SwapArgs { get; init; }
    public bool IsActive { get; init; }
    public int SortOrder { get; init; }
}
