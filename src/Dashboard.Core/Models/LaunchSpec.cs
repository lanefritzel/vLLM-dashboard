namespace Dashboard.Core.Models;

public sealed record PortMapping
{
    public string Host { get; init; } = "";
    public string Container { get; init; } = "";
}

public sealed record VolumeMapping
{
    public string Host { get; init; } = "";
    public string Container { get; init; } = "";
    public bool ReadOnly { get; init; }
}

/// <summary>Everything needed to (re)create a backend container for a source.</summary>
public sealed record LaunchSpec
{
    public string Id { get; init; } = "";
    public string SourceId { get; init; } = "";
    public string Image { get; init; } = "";
    public string ModelArgTemplate { get; init; } = "--model {model}";
    public List<PortMapping> Ports { get; set; } = new();
    public Dictionary<string, string> Env { get; set; } = new();
    public List<VolumeMapping> Volumes { get; set; } = new();
    public string RestartPolicy { get; init; } = "unless-stopped";
    public string? ExtraRunArgs { get; init; }
    public string? GpuOptions { get; init; }

    /// <summary>Substitutes <c>{model}</c> in the template with the given value.</summary>
    public string SubstituteModel(string modelValue) =>
        ModelArgTemplate.Replace("{model}", modelValue);
}
