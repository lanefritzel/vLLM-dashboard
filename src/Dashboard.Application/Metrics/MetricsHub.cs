using Microsoft.AspNetCore.SignalR;

namespace Dashboard.Application.Metrics;

/// <summary>
/// SignalR hub. Clients join a per-source group to receive live samples for that
/// source. A global <c>configUpdated</c> event is broadcast to everyone so any
/// connected island can re-read sources/models/config after a change.
/// </summary>
public sealed class MetricsHub : Hub
{
    public const string SampleEvent = "receiveSample";
    public const string ConfigUpdatedEvent = "configUpdated";

    public static string GroupFor(string sourceId) => "source_" + sourceId;

    public Task JoinSource(string sourceId) => Groups.AddToGroupAsync(Context.ConnectionId, GroupFor(sourceId));

    public Task LeaveSource(string sourceId) => Groups.RemoveFromGroupAsync(Context.ConnectionId, GroupFor(sourceId));
}
