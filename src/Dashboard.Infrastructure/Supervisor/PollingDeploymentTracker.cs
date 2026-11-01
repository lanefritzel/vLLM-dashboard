using Dashboard.Core.Abstractions;

namespace Dashboard.Infrastructure.Supervisor;

/// <summary>Polling implementation of <see cref="IDeploymentTracker"/> (P10a) --
/// polls <c>GET /admin/deployments/{id}/events?after=</c> about once a
/// second until a terminal state event appears. P10b replaces the polling
/// with a push receiver feeding the same interface; nothing above this
/// needs to change when that lands.</summary>
public sealed class PollingDeploymentTracker : IDeploymentTracker
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);

    // "crashed" is deliberately excluded -- it's the post-ready watcher's
    // concern (the child died AFTER this same deploy already reached
    // ready), not this deploy attempt's own terminal outcome.
    private static readonly HashSet<string> TerminalStates = new(StringComparer.Ordinal) { "ready", "failed", "cancelled" };

    private readonly ISupervisorClient _client;

    public PollingDeploymentTracker(ISupervisorClient client) => _client = client;

    public async Task<DeploymentTrackingResult> TrackToTerminalAsync(
        string adminUrl, string token, string deploymentId, TimeSpan timeout, CancellationToken ct = default)
    {
        var deadline = DateTime.UtcNow + timeout;
        var after = 0;
        while (true)
        {
            var events = await _client.GetEventsAsync(adminUrl, token, deploymentId, after, ct);
            foreach (var evt in events)
            {
                after = Math.Max(after, evt.Seq);
                if (TerminalStates.Contains(evt.State))
                    return new DeploymentTrackingResult { TerminalState = evt.State, Detail = evt.Detail };
            }

            if (DateTime.UtcNow >= deadline)
                throw new TimeoutException($"Deployment {deploymentId} did not reach a terminal state within {timeout}.");
            await Task.Delay(PollInterval, ct);
        }
    }
}
