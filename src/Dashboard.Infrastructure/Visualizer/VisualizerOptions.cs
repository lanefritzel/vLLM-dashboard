namespace Dashboard.Infrastructure.Visualizer;

/// <summary>Settings for the neuron-map feed (config section "Visualizer", e.g. env
/// <c>Visualizer__Port</c>). The feed is the vLLM visualizer plugin's small HTTP endpoint,
/// reached on the instance's own host.</summary>
public sealed class VisualizerOptions
{
    /// <summary>Port the plugin listens on (its VIS_PORT).</summary>
    public int Port { get; set; } = 9998;

    /// <summary>Shared token, if the plugin was started with VIS_TOKEN. Sent as a Bearer header.</summary>
    public string? Token { get; set; }

    /// <summary>Use this host instead of the one derived from the instance (rarely needed).</summary>
    public string? HostOverride { get; set; }

    /// <summary>Lowest gap between frames forwarded to browsers. 0 disables throttling.</summary>
    public int MinFrameIntervalMs { get; set; } = 30;

    /// <summary>Longest wait between reconnect attempts while viewers are present.</summary>
    public int MaxReconnectSeconds { get; set; } = 10;
}
