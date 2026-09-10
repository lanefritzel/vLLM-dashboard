namespace Dashboard.Infrastructure.Persistence;

public class LaunchSpecEntity
{
    public Guid Id { get; set; }
    public Guid SourceId { get; set; }
    public string Image { get; set; } = "";
    public string ModelArgTemplate { get; set; } = "--model {model}";
    public string PortsJson { get; set; } = "[]";
    public string EnvJson { get; set; } = "{}";
    public string VolumesJson { get; set; } = "[]";
    public string RestartPolicy { get; set; } = "unless-stopped";
    public string? ExtraRunArgs { get; set; }
    public string? GpuOptions { get; set; }

    public Source? Source { get; set; }

    public Core.Models.LaunchSpec ToInfo()
    {
        var spec = new Core.Models.LaunchSpec
        {
            Id = Id.ToString(),
            SourceId = SourceId.ToString(),
            Image = Image,
            ModelArgTemplate = ModelArgTemplate,
            RestartPolicy = RestartPolicy,
            ExtraRunArgs = ExtraRunArgs,
            GpuOptions = GpuOptions,
        };

        try
        {
            spec.Ports = System.Text.Json.JsonSerializer.Deserialize<List<Core.Models.PortMapping>>(PortsJson) ?? new();
            spec.Volumes = System.Text.Json.JsonSerializer.Deserialize<List<Core.Models.VolumeMapping>>(VolumesJson) ?? new();
            spec.Env = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(EnvJson) ?? new();
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
        EnvJson = System.Text.Json.JsonSerializer.Serialize(spec.Env),
        VolumesJson = System.Text.Json.JsonSerializer.Serialize(spec.Volumes),
        RestartPolicy = spec.RestartPolicy,
        ExtraRunArgs = spec.ExtraRunArgs,
        GpuOptions = spec.GpuOptions,
    };
}
