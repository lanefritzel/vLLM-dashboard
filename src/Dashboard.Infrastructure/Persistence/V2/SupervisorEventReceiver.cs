using Dashboard.Core.Abstractions;
using Dashboard.Core.Supervisor;

namespace Dashboard.Infrastructure.Persistence.V2;

public enum SupervisorEventReceiverResult
{
    Ok,
    BadRequest,
    NotFound,
    Unauthorized,
}

/// <summary>The receiver endpoint's own logic (P10b, doc 5), pulled out of
/// Program.cs's minimal API route so it's unit-testable without a full web
/// host. Reachable on the dashboard's own published port (unlike the
/// supervisor's loopback-only admin API), so it still needs a check -- this
/// used to be a per-instance bearer token; now it's "did this request
/// actually come from this same machine" (<paramref name="isLoopback"/> in
/// <see cref="HandleAsync"/>), since a supervisor always runs alongside the
/// dashboard on one host and pushes to it over 127.0.0.1.</summary>
public sealed class SupervisorEventReceiver
{
    private readonly IDeploymentService _deployments;
    private readonly IInstanceService _instances;
    private readonly IDeploymentEventApplier _applier;

    public SupervisorEventReceiver(IDeploymentService deployments, IInstanceService instances, IDeploymentEventApplier applier)
    {
        _deployments = deployments;
        _instances = instances;
        _applier = applier;
    }

    public async Task<SupervisorEventReceiverResult> HandleAsync(SupervisorPushEvent evt, bool isLoopback, CancellationToken ct = default)
    {
        if (!isLoopback)
            return SupervisorEventReceiverResult.Unauthorized;

        if (!int.TryParse(evt.DeploymentId, out var deploymentId))
            return SupervisorEventReceiverResult.BadRequest;

        var deployment = await _deployments.GetAsync(deploymentId, ct);
        if (deployment is null)
            return SupervisorEventReceiverResult.NotFound;

        var instance = await _instances.GetAsync(deployment.InstanceId, ct);
        if (instance?.AdminUrl is null)
            return SupervisorEventReceiverResult.NotFound;

        await _applier.ApplyWithGapCatchUpAsync(deploymentId, instance.AdminUrl, evt, ct);
        return SupervisorEventReceiverResult.Ok;
    }
}
