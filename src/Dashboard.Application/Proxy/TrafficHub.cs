using Microsoft.AspNetCore.SignalR;

namespace Dashboard.Application.Proxy;

/// <summary>
/// SignalR hub for the live traffic view: clients join a per-source group to watch
/// requests flow through <see cref="ModelProxyService"/> in real time (request
/// started, streamed response chunks, finished). Mirrors <c>MetricsHub</c>'s
/// per-source group pattern but as its own hub/connection since it's a distinct
/// event stream, not a metrics sample.
/// </summary>
public sealed class TrafficHub : Hub
{
    public const string StartedEvent = "trafficStarted";
    public const string ChunkEvent = "trafficChunk";
    public const string FinishedEvent = "trafficFinished";

    public static string GroupFor(string sourceId) => "traffic_" + sourceId;

    public Task JoinSource(string sourceId) => Groups.AddToGroupAsync(Context.ConnectionId, GroupFor(sourceId));

    public Task LeaveSource(string sourceId) => Groups.RemoveFromGroupAsync(Context.ConnectionId, GroupFor(sourceId));
}

public sealed record TrafficRequestStarted
{
    public string RequestId { get; init; } = "";
    public string SourceId { get; init; } = "";
    public string Method { get; init; } = "";
    public string Path { get; init; } = "";
    public string? Model { get; init; }
    public IReadOnlyList<TrafficMessage> Messages { get; init; } = Array.Empty<TrafficMessage>();
    public DateTimeOffset StartedAt { get; init; }
}

/// <summary>One entry from the request's OpenAI-style <c>messages</c> array (system
/// prompt, prior turns, the new user message) — shown verbatim so it's clear exactly
/// what the model was given, which is the point of a debugging view like this.</summary>
public sealed record TrafficMessage
{
    public string Role { get; init; } = "";
    public string Content { get; init; } = "";
}

public enum TrafficChunkKind { Content, Reasoning }

public sealed record TrafficChunk
{
    public string RequestId { get; init; } = "";
    public string SourceId { get; init; } = "";
    public string Text { get; init; } = "";
    public TrafficChunkKind Kind { get; init; } = TrafficChunkKind.Content;
}

public sealed record TrafficFinished
{
    public string RequestId { get; init; } = "";
    public string SourceId { get; init; } = "";
    public int StatusCode { get; init; }
    public long ElapsedMs { get; init; }
    public string? Error { get; init; }
}
