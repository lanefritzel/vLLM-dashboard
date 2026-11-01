using Dashboard.Core.Abstractions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Dashboard.Infrastructure.Persistence.V2;

/// <summary>Catch-up fallback for the push receiver (P10b, doc 5: "also catch
/// up on dashboard start and periodically while a deployment is pending.
/// Polling is the fallback, never the primary path"). Every few seconds,
/// fetches and applies any events a still-"pending" deployment's own
/// LastSeenSeq hasn't caught up to -- covers both a dropped push (network
/// blip) and the receiver having been down entirely (restart, 503) while a
/// swap was in flight. Lives in Infrastructure, not Application, since it
/// depends directly on the V2 persistence services (EF-entity-shaped,
/// Infrastructure-only by design since P09b), not just Core abstractions.</summary>
public sealed class DeploymentCatchUpService : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);

    private readonly IDeploymentService _deployments;
    private readonly IInstanceService _instances;
    private readonly ISupervisorTokenProvider _tokenProvider;
    private readonly IDeploymentEventApplier _applier;
    private readonly ILogger<DeploymentCatchUpService> _logger;

    public DeploymentCatchUpService(
        IDeploymentService deployments, IInstanceService instances, ISupervisorTokenProvider tokenProvider,
        IDeploymentEventApplier applier, ILogger<DeploymentCatchUpService> logger)
    {
        _deployments = deployments;
        _instances = instances;
        _tokenProvider = tokenProvider;
        _applier = applier;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await CatchUpAllPendingAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Deployment catch-up pass failed; will retry on the next tick.");
            }

            try
            {
                await Task.Delay(PollInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task CatchUpAllPendingAsync(CancellationToken ct)
    {
        var pending = await _deployments.ListAllPendingAsync(ct);
        foreach (var deployment in pending)
        {
            var instance = await _instances.GetAsync(deployment.InstanceId, ct);
            if (instance?.AdminUrl is null)
                continue;
            var token = _tokenProvider.GetToken(instance.Id);
            if (token is null)
                continue;

            try
            {
                await _applier.CatchUpAsync(deployment.Id, instance.AdminUrl, token, ct);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // One instance being briefly unreachable shouldn't stop
                // catch-up for every OTHER pending deployment this tick.
                _logger.LogWarning(ex, "Catch-up failed for deployment {DeploymentId}; will retry on the next tick.", deployment.Id);
            }
        }
    }
}
