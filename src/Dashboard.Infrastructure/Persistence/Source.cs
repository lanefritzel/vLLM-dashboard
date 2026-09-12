using Dashboard.Core.Models;

namespace Dashboard.Infrastructure.Persistence;

public class Source
{
    public Guid Id { get; set; }
    public string Slug { get; set; } = "";
    public string Label { get; set; } = "";
    public string Type { get; set; } = SourceType.Vllm.ToStorage();
    public string MetricsUrl { get; set; } = "";
    public string InferenceUrl { get; set; } = "";
    public string? DockerHost { get; set; }
    public Guid? PhysicalServerId { get; set; }
    public string? ContainerName { get; set; }

    /// <summary>Which model (from the physical server's shared catalog) this
    /// instance currently has loaded. Independent per instance — see
    /// <see cref="Core.Models.SourceInfo.ActiveModelId"/>.</summary>
    public Guid? ActiveModelId { get; set; }

    /// <summary>Which model this instance should auto-load at dashboard startup.</summary>
    public Guid? PreloadModelId { get; set; }

    public int ScrapeIntervalSeconds { get; set; } = 5;
    public bool Active { get; set; } = true;
    public int SortOrder { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public LaunchSpecEntity? LaunchSpec { get; set; }
    public PhysicalServer? PhysicalServer { get; set; }

    public SourceInfo ToInfo() => new()
    {
        Id = Id.ToString(),
        Slug = Slug,
        Label = Label,
        Type = SourceTypeExtensions.FromStorage(Type),
        MetricsUrl = MetricsUrl,
        InferenceUrl = InferenceUrl,
        DockerHost = DockerHost,
        PhysicalServerId = PhysicalServerId?.ToString(),
        ContainerName = ContainerName,
        ActiveModelId = ActiveModelId?.ToString(),
        PreloadModelId = PreloadModelId?.ToString(),
        ScrapeIntervalSeconds = ScrapeIntervalSeconds,
        Active = Active,
        SortOrder = SortOrder,
        CreatedAt = CreatedAt,
        UpdatedAt = UpdatedAt,
    };
}
