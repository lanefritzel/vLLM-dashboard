using Dashboard.Core.Supervisor;

namespace Dashboard.Core.Abstractions;

/// <summary>The per-deployment "events -> rows" pipeline (P10b) -- the single
/// place that applies a supervisor push (or a caught-up copy of one) to the
/// deployments/deployment_events tables and broadcasts live progress. Used
/// by both the push receiver endpoint and the catch-up service, so there is
/// exactly one code path that ever flips a deployment's status.</summary>
public interface IDeploymentEventApplier
{
    /// <summary>The primary entry point for a freshly-pushed event: if
    /// <paramref name="evt"/>'s seq is ahead of what this deployment has
    /// seen, fetches and applies the gap from the supervisor first (GET
    /// .../events?after=), then applies <paramref name="evt"/> itself.
    /// Idempotent -- a duplicate or already-covered seq is a no-op.</summary>
    Task ApplyWithGapCatchUpAsync(
        int deploymentId, string adminUrl, string token, SupervisorPushEvent evt, CancellationToken ct = default);

    /// <summary>Catch-up with no specific incoming event -- fetches and
    /// applies everything newer than this deployment's own LastSeenSeq.
    /// Used by the startup/periodic catch-up service for deployments still
    /// "pending" (doc 5: "also catch up on dashboard start and periodically
    /// while a deployment is pending").</summary>
    Task CatchUpAsync(int deploymentId, string adminUrl, string token, CancellationToken ct = default);
}
