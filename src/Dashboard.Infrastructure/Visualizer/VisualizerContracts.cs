namespace Dashboard.Infrastructure.Visualizer;

/// <summary>Where to read one source's neuron-map stream.</summary>
public sealed record VisualizerEndpoint(Uri Url, string? Token);

/// <summary>Turns a source id (the instance id, as used by Live Traffic) into the plugin's
/// stream URL. Null means "no host configured yet". A seam so the feed can be tested
/// with no database.</summary>
public interface IVisualizerEndpointResolver
{
    Task<VisualizerEndpoint?> ResolveAsync(string sourceId, CancellationToken ct);
}

/// <summary>Where the feed delivers frames and status. The real one is SignalR; tests
/// capture them in memory.</summary>
public interface IVisualizerSink
{
    Task SendSnapshotAsync(string sourceId, string json, CancellationToken ct);
    Task SendStatusAsync(string sourceId, VisualizerStatus status, CancellationToken ct);
}

/// <summary>State is one of: connecting, live, unavailable, idle.</summary>
public sealed record VisualizerStatus(string State, string? Detail = null);
