using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;

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

    private readonly IInFlightTrafficTracker _inFlight;
    private readonly ILogger<TrafficHub> _logger;

    public TrafficHub(IInFlightTrafficTracker inFlight, ILogger<TrafficHub> logger)
    {
        _inFlight = inFlight;
        _logger = logger;
    }

    public static string GroupFor(string sourceId) => "traffic_" + sourceId;

    public async Task JoinSource(string sourceId)
    {
        // Whole body wrapped, including the group join itself: an unhandled exception
        // anywhere in a Hub method kills the connection outright with no detail sent to
        // the client (confirmed live — the browser only ever saw a bare "connection
        // closed with an error"). Whatever the cause, that must never happen here, since
        // it silently takes the group membership down with it — losing not just the
        // catch-up but every *future* live broadcast for this connection too.
        try
        {
            await Groups.AddToGroupAsync(Context.ConnectionId, GroupFor(sourceId));

            // Catch this connection up on whatever's already mid-stream for this source —
            // without this, a viewer who opens Live Traffic (or switches source) after a
            // request's "started" broadcast already went out sees nothing for it: every
            // later chunk references a request id their card list never heard of.
            foreach (var snap in _inFlight.GetForSource(sourceId))
            {
                await Clients.Caller.SendAsync(StartedEvent, snap.Started);
                if (snap.ReasoningText.Length > 0)
                {
                    await Clients.Caller.SendAsync(ChunkEvent, new TrafficChunk
                    {
                        RequestId = snap.Started.RequestId,
                        SourceId = sourceId,
                        Text = snap.ReasoningText,
                        Kind = TrafficChunkKind.Reasoning,
                    });
                }
                if (snap.ContentText.Length > 0)
                {
                    await Clients.Caller.SendAsync(ChunkEvent, new TrafficChunk
                    {
                        RequestId = snap.Started.RequestId,
                        SourceId = sourceId,
                        Text = snap.ContentText,
                        Kind = TrafficChunkKind.Content,
                    });
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Live Traffic catch-up replay failed for source {SourceId} (connection {ConnectionId}).", sourceId, Context.ConnectionId);
        }
    }

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
