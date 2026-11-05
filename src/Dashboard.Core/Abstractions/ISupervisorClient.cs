using Dashboard.Core.Supervisor;

namespace Dashboard.Core.Abstractions;

/// <summary>Typed HTTP client for a single supervisor's admin API (P10a, doc 5).
/// Every method targets an explicit <paramref name="adminUrl"/>, rather than
/// binding to one fixed instance -- the caller resolves it (<c>instances.admin_url</c>)
/// per call, since a dashboard can have multiple instances/supervisors.
/// No bearer token: the admin API only ever binds to 127.0.0.1 on its own host
/// (supervisor.py), so it's never reachable from anywhere but the machine the
/// dashboard itself runs on -- there used to be a per-instance token on top of
/// that, but once the API can't be reached from the LAN at all, it was
/// protecting against nothing that local host access doesn't already grant.
/// Throws <see cref="SupervisorApiException"/> for any non-2xx response.</summary>
public interface ISupervisorClient
{
    Task<bool> HealthAsync(string adminUrl, CancellationToken ct = default);

    Task<SupervisorStatus> GetStatusAsync(string adminUrl, CancellationToken ct = default);

    Task<string> DeployAsync(
        string adminUrl, IReadOnlyList<string> argv, IReadOnlyDictionary<string, string>? env,
        string deploymentId, double healthTimeout, object? smoke = null, CancellationToken ct = default);

    Task<IReadOnlyList<SupervisorDeploymentEvent>> GetEventsAsync(
        string adminUrl, string deploymentId, int after = 0, CancellationToken ct = default);

    /// <summary>Pull-based counterpart to the supervisor's own best-effort,
    /// un-retried log push (P16a finding): log lines are "droppable" on
    /// purpose, so the UI's live log pane never had any way to recover from
    /// a dropped push -- the supervisor itself still has every line archived
    /// per deployment, it just never gets asked for it live. Returns the
    /// last <paramref name="lines"/> lines of this specific deployment's own
    /// archived output, independent of whether any push for it ever arrived.</summary>
    Task<string> GetDeploymentLogsAsync(
        string adminUrl, string deploymentId, int lines = 200, CancellationToken ct = default);

    Task<SupervisorStopResult> StopAsync(string adminUrl, CancellationToken ct = default);

    Task<SupervisorCancelResult> CancelAsync(string adminUrl, string deploymentId, CancellationToken ct = default);

    Task<string> RestartAsync(string adminUrl, string? deploymentId = null, CancellationToken ct = default);

    Task<SupervisorGpuInfo> GetGpuAsync(string adminUrl, CancellationToken ct = default);

    /// <summary>P15b's "Restore last known good model on boot" toggle. Value is
    /// "last_good" or "off".</summary>
    Task<string> GetAutoStartAsync(string adminUrl, CancellationToken ct = default);

    Task<string> SetAutoStartAsync(string adminUrl, string autoStart, CancellationToken ct = default);

    Task<SupervisorSmokeResult> SmokeTestAsync(string adminUrl, CancellationToken ct = default);

    /// <summary>P13's Add-model flow, step 1 -- lists the repo's files + parsed
    /// config.json + the server's own free disk space, before anything is
    /// downloaded.</summary>
    Task<SupervisorDownloadInspectResult> InspectDownloadAsync(string adminUrl, string repoId, CancellationToken ct = default);

    /// <summary>Starts a download into <c>&lt;models_dir&gt;/&lt;slug&gt;</c>.
    /// Throws <see cref="SupervisorApiException"/> with StatusCode 409 if one is
    /// already in flight on this instance's supervisor (one download at a time, doc 6).</summary>
    Task<string> StartDownloadAsync(string adminUrl, string repoId, string slug, string? filename = null, CancellationToken ct = default);

    Task<SupervisorDownloadStatus> GetDownloadAsync(string adminUrl, string downloadId, CancellationToken ct = default);

    /// <summary>Cancels an in-flight download, or cleans up (removes the partial
    /// directory of) an already-finished failed/cancelled one.</summary>
    Task CancelDownloadAsync(string adminUrl, string downloadId, CancellationToken ct = default);
}
