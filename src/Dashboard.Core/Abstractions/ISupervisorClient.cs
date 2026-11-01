using Dashboard.Core.Supervisor;

namespace Dashboard.Core.Abstractions;

/// <summary>Typed HTTP client for a single supervisor's admin API (P10a, doc 5).
/// Every method targets an explicit <paramref name="adminUrl"/> + bearer
/// token, rather than binding to one fixed instance -- the caller resolves
/// both (<c>instances.admin_url</c>, <see cref="ISupervisorTokenProvider"/>)
/// per call, since a dashboard can have multiple instances/supervisors.
/// Throws <see cref="SupervisorApiException"/> for any non-2xx response.</summary>
public interface ISupervisorClient
{
    Task<bool> HealthAsync(string adminUrl, CancellationToken ct = default);

    Task<SupervisorStatus> GetStatusAsync(string adminUrl, string token, CancellationToken ct = default);

    Task<string> DeployAsync(
        string adminUrl, string token, IReadOnlyList<string> argv, IReadOnlyDictionary<string, string>? env,
        string deploymentId, double healthTimeout, object? smoke = null, CancellationToken ct = default);

    Task<IReadOnlyList<SupervisorDeploymentEvent>> GetEventsAsync(
        string adminUrl, string token, string deploymentId, int after = 0, CancellationToken ct = default);

    Task<SupervisorStopResult> StopAsync(string adminUrl, string token, CancellationToken ct = default);

    Task<SupervisorCancelResult> CancelAsync(string adminUrl, string token, string deploymentId, CancellationToken ct = default);

    Task<string> RestartAsync(string adminUrl, string token, string? deploymentId = null, CancellationToken ct = default);

    Task<SupervisorGpuInfo> GetGpuAsync(string adminUrl, string token, CancellationToken ct = default);

    Task<SupervisorSmokeResult> SmokeTestAsync(string adminUrl, string token, CancellationToken ct = default);
}
