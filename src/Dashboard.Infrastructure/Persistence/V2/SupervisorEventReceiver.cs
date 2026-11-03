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
/// host. "Authenticated with the shared bearer token ... It is reachable on
/// the published port, so the token check is the protection -- reject
/// without it": the token checked is whichever instance the event's own
/// deployment_id resolves to, via <see cref="ISupervisorTokenProvider"/> --
/// not a single global secret, since different instances could have
/// different tokens.</summary>
public sealed class SupervisorEventReceiver
{
    private readonly IDeploymentService _deployments;
    private readonly IInstanceService _instances;
    private readonly ISupervisorTokenProvider _tokenProvider;
    private readonly IDeploymentEventApplier _applier;

    public SupervisorEventReceiver(
        IDeploymentService deployments, IInstanceService instances, ISupervisorTokenProvider tokenProvider,
        IDeploymentEventApplier applier)
    {
        _deployments = deployments;
        _instances = instances;
        _tokenProvider = tokenProvider;
        _applier = applier;
    }

    public async Task<SupervisorEventReceiverResult> HandleAsync(SupervisorPushEvent evt, string? authorizationHeader, CancellationToken ct = default)
    {
        if (!int.TryParse(evt.DeploymentId, out var deploymentId))
            return SupervisorEventReceiverResult.BadRequest;

        var deployment = await _deployments.GetAsync(deploymentId, ct);
        if (deployment is null)
            return SupervisorEventReceiverResult.NotFound;

        var instance = await _instances.GetAsync(deployment.InstanceId, ct);
        var expectedToken = instance is null ? null : await _tokenProvider.GetTokenAsync(instance.Id, ct);
        if (string.IsNullOrEmpty(expectedToken) || authorizationHeader != $"Bearer {expectedToken}")
            return SupervisorEventReceiverResult.Unauthorized;

        await _applier.ApplyWithGapCatchUpAsync(deploymentId, instance!.AdminUrl!, expectedToken, evt, ct);
        return SupervisorEventReceiverResult.Ok;
    }
}
