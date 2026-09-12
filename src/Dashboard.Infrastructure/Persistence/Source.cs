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
    public int ScrapeIntervalSeconds { get; set; } = 5;
    public bool Active { get; set; } = true;
    public int SortOrder { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public List<Model> Models { get; set; } = new();
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
        ScrapeIntervalSeconds = ScrapeIntervalSeconds,
        Active = Active,
        SortOrder = SortOrder,
        CreatedAt = CreatedAt,
        UpdatedAt = UpdatedAt,
    };
}
