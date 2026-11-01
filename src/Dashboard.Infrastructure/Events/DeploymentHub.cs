using Microsoft.AspNetCore.SignalR;

namespace Dashboard.Infrastructure.Events;

/// <summary>
/// SignalR hub (P10b). Clients join a per-deployment group to receive that
/// deployment's live state events and log lines as the supervisor pushes
/// them -- log lines are broadcast only, never persisted to Postgres (doc 5).
/// Lives in Infrastructure, not alongside the other hubs in Application,
/// since it's broadcast-from <see cref="Dashboard.Infrastructure.Persistence.V2.DeploymentEventApplier"/>
/// there, and Infrastructure doesn't (and shouldn't) depend on Application.
/// </summary>
public sealed class DeploymentHub : Hub
{
    public const string StateEvent = "receiveState";
    public const string LogEvent = "receiveLog";

    public static string GroupFor(int deploymentId) => "deployment_" + deploymentId;

    public Task JoinDeployment(int deploymentId) => Groups.AddToGroupAsync(Context.ConnectionId, GroupFor(deploymentId));

    public Task LeaveDeployment(int deploymentId) => Groups.RemoveFromGroupAsync(Context.ConnectionId, GroupFor(deploymentId));
}
