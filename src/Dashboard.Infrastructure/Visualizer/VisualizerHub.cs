using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;

namespace Dashboard.Infrastructure.Visualizer;

/// <summary>
/// SignalR hub for the Overview neuron map: a browser joins a per-source group, which makes
/// <see cref="VisualizerFeedService"/> open the plugin stream (if it is not already open).
/// Own hub/connection, like <c>TrafficHub</c>, so it can reconnect without touching any
/// other live view.
/// </summary>
public sealed class VisualizerHub : Hub
{
    public const string SnapshotEvent = "visualizerSnapshot";
    public const string StatusEvent = "visualizerStatus";

    private readonly VisualizerFeedService _feed;
    private readonly ILogger<VisualizerHub> _logger;

    public VisualizerHub(VisualizerFeedService feed, ILogger<VisualizerHub> logger)
    {
        _feed = feed;
        _logger = logger;
    }

    public static string GroupFor(string sourceId) => "visualizer_" + sourceId;

    public async Task JoinSource(string sourceId)
    {
        // Whole body guarded: an unhandled exception in a hub method drops the connection
        // (same lesson as TrafficHub.JoinSource).
        try
        {
            await Groups.AddToGroupAsync(Context.ConnectionId, GroupFor(sourceId));
            _feed.Join(Context.ConnectionId, sourceId);
            var (status, last) = _feed.Peek(sourceId);
            await Clients.Caller.SendAsync(StatusEvent, status);
            if (last is not null)
                await Clients.Caller.SendAsync(SnapshotEvent, last);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Visualizer join failed for source {SourceId}.", sourceId);
        }
    }

    public async Task LeaveSource(string sourceId)
    {
        _feed.Leave(Context.ConnectionId, sourceId);
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, GroupFor(sourceId));
    }

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        _feed.Disconnect(Context.ConnectionId);
        return base.OnDisconnectedAsync(exception);
    }
}

public sealed class SignalRVisualizerSink : IVisualizerSink
{
    private readonly IHubContext<VisualizerHub> _hub;

    public SignalRVisualizerSink(IHubContext<VisualizerHub> hub) => _hub = hub;

    public Task SendSnapshotAsync(string sourceId, string json, CancellationToken ct) =>
        _hub.Clients.Group(VisualizerHub.GroupFor(sourceId)).SendAsync(VisualizerHub.SnapshotEvent, json, ct);

    public Task SendStatusAsync(string sourceId, VisualizerStatus status, CancellationToken ct) =>
        _hub.Clients.Group(VisualizerHub.GroupFor(sourceId)).SendAsync(VisualizerHub.StatusEvent, status, ct);
}
