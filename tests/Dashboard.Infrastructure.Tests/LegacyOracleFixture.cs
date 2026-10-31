using System.Text.Json;
using System.Text.Json.Serialization;

namespace Dashboard.Infrastructure.Tests;

/// <summary>Plain DTOs matching tests/fixtures/legacy-oracle/*.json (P01b) -- legacy
/// rows to seed before running the conversion routine, not EF entities, so loading
/// a fixture never implies a particular ID scheme.</summary>
public sealed class LegacyOracleFixture
{
    public string Description { get; set; } = "";
    public FixturePhysicalServer? PhysicalServer { get; set; }
    public List<FixtureSource> Sources { get; set; } = new();

    public static LegacyOracleFixture Load(string fileName)
    {
        var path = Path.Combine(FindRepoRoot(), "tests", "fixtures", "legacy-oracle", fileName);
        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<LegacyOracleFixture>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
        })!;
    }

    private static string FindRepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "Dashboard.slnx")))
            dir = Path.GetDirectoryName(dir);
        return dir ?? throw new InvalidOperationException("Could not find repo root (Dashboard.slnx) above " + AppContext.BaseDirectory);
    }
}

public sealed class FixturePhysicalServer
{
    public string Label { get; set; } = "";
    public string DockerHost { get; set; } = "";
    public string GpuVendor { get; set; } = "none";
    public int? GpuVramTotalGb { get; set; }
    public string? HfCacheHostPath { get; set; }
    public string? HfToken { get; set; }
}

public sealed class FixtureSource
{
    public string Slug { get; set; } = "";
    public string Label { get; set; } = "";
    public string Type { get; set; } = "vllm";
    public string MetricsUrl { get; set; } = "";
    public string InferenceUrl { get; set; } = "";
    public string? DockerHost { get; set; }
    public bool Active { get; set; }
    public FixtureLaunchSpec? LaunchSpec { get; set; }
    public List<FixtureModel> Models { get; set; } = new();
}

public sealed class FixtureLaunchSpec
{
    public string Image { get; set; } = "";
    public string RestartPolicy { get; set; } = "unless-stopped";
    public int? ServingPort { get; set; }
    public string NetworkMode { get; set; } = "bridge";
    public string? IpcMode { get; set; }
    public bool Privileged { get; set; }
    public string? Entrypoint { get; set; }
    public List<FixtureDevice> Devices { get; set; } = new();
    public List<FixtureVolume> Volumes { get; set; } = new();
}

public sealed class FixtureDevice
{
    public string HostPath { get; set; } = "";
    public string ContainerPath { get; set; } = "";
}

public sealed class FixtureVolume
{
    public string Host { get; set; } = "";
    public string Container { get; set; } = "";
    public bool ReadOnly { get; set; }
}

public sealed class FixtureModel
{
    public string Name { get; set; } = "";
    public string? DisplayName { get; set; }
    public string ModelSource { get; set; } = "huggingface";
    public string ModelPathOrRepoId { get; set; } = "";
    public bool IsActive { get; set; }
    public Dictionary<string, string> OptionsJson { get; set; } = new();
}
