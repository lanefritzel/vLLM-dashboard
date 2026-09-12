using Dashboard.Core.Models;

namespace Dashboard.Infrastructure.Persistence;

public class PhysicalServer
{
    public Guid Id { get; set; }
    public string Label { get; set; } = "";
    public string DockerHost { get; set; } = "";
    public string GpuVendor { get; set; } = "none";
    public int? GpuVramTotalGb { get; set; }
    public string? HfCacheHostPath { get; set; }
    public string? HfToken { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public List<Source> Sources { get; set; } = new();

    public PhysicalServerInfo ToInfo() => new()
    {
        Id = Id.ToString(),
        Label = Label,
        DockerHost = DockerHost,
        GpuVendor = GpuVendorExtensions.FromStorage(GpuVendor),
        GpuVramTotalGb = GpuVramTotalGb,
        HfCacheHostPath = HfCacheHostPath,
        HfToken = HfToken,
        CreatedAt = CreatedAt,
        UpdatedAt = UpdatedAt,
    };
}
