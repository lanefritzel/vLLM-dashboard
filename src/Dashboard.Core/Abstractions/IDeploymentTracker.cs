namespace Dashboard.Core.Abstractions;

public sealed record DeploymentTrackingResult
{
    /// <summary>"ready" | "failed" | "cancelled" -- the first of these seen on
    /// the deployment's own event stream. "crashed" is a POST-ready event
    /// (the child died after this same deploy already succeeded) and is
    /// deliberately not a terminal state tracked here -- it's the post-ready
    /// watcher's concern, not this deploy attempt's own outcome.</summary>
    public required string TerminalState { get; init; }

    /// <summary>The terminal event's own detail payload, if any (e.g. a
    /// failed deploy's {reason, exit_code, log_tail}, or a cancelled
    /// deploy's {phase}).</summary>
    public Dictionary<string, object?>? Detail { get; init; }
}

/// <summary>Watches one deployment attempt to a terminal state (P10a: polling
/// for now; P10b adds the push receiver feeding the same interface, so
/// <see cref="Dashboard.Core.Abstractions.ISwapService"/> never needs to
/// change when that lands).</summary>
public interface IDeploymentTracker
{
    Task<DeploymentTrackingResult> TrackToTerminalAsync(
        string adminUrl, string token, string deploymentId, TimeSpan timeout, CancellationToken ct = default);
}
