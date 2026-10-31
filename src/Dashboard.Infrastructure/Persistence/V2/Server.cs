namespace Dashboard.Infrastructure.Persistence.V2;

/// <summary>A physical machine running the supervisor + vLLM (table <c>servers</c>).
/// Single-row MVP, but the schema supports N servers at no extra cost — see doc 4.
/// Distinct from the legacy <see cref="Dashboard.Infrastructure.Persistence.PhysicalServer"/>,
/// which the old swap-era code paths still use until P16.</summary>
public class Server
{
    public int Id { get; set; }
    public string Label { get; set; } = "";
    public string? DockerHost { get; set; }
    public string? GpuName { get; set; }
    public int GpuCount { get; set; } = 1;
    public int? GpuMemGb { get; set; }
    public string ModelsDir { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
