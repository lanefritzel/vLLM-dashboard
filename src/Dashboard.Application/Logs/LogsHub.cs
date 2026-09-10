using Microsoft.AspNetCore.SignalR;

namespace Dashboard.Application.Logs;

/// <summary>
/// SignalR hub for live container log streaming: clients join a per-source group to
/// watch its container's stdout/stderr in real time. Unlike MetricsHub/TrafficHub
/// (always-on background work), log streaming is viewer-driven — joining/leaving
/// actually starts/stops the underlying Docker log-follow connection via
/// ILogStreamManager, so a source nobody's watching doesn't keep one open forever.
/// </summary>
public sealed class LogsHub : Hub
{
    public const string LineEvent = "logLine";

    private readonly ILogStreamManager _manager;

    public LogsHub(ILogStreamManager manager)
    {
        _manager = manager;
    }

    public static string GroupFor(string sourceId) => "logs_" + sourceId;

    public async Task JoinSource(string sourceId)
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, GroupFor(sourceId));
        await _manager.JoinAsync(Context.ConnectionId, sourceId);
    }

    public async Task LeaveSource(string sourceId)
    {
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, GroupFor(sourceId));
        await _manager.LeaveAsync(Context.ConnectionId, sourceId);
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        // A closed tab/lost connection never gets to call LeaveSource explicitly —
        // without this, that viewer's log stream would keep running forever.
        await _manager.DisconnectAsync(Context.ConnectionId);
        await base.OnDisconnectedAsync(exception);
    }
}
