using Dashboard.Core.Abstractions;
using Dashboard.Core.Supervisor;

namespace Dashboard.Infrastructure.Tests;

/// <summary>Throws on every call -- for tests that construct a service needing an
/// <see cref="ISupervisorClient"/> but never exercise the one path that actually
/// calls it (e.g. <c>ModelService.DeleteAsync</c> with <c>deleteFiles: false</c>,
/// the default). Making that obvious beats a null or a silently-succeeding mock.</summary>
public class UnusedSupervisorClient : ISupervisorClient
{
    private static Exception NotUsed() => new InvalidOperationException("This test's ISupervisorClient is not expected to be called.");

    public Task<bool> HealthAsync(string adminUrl, CancellationToken ct = default) => throw NotUsed();
    public Task<SupervisorStatus> GetStatusAsync(string adminUrl, CancellationToken ct = default) => throw NotUsed();
    public Task<string> DeployAsync(string adminUrl, IReadOnlyList<string> argv, IReadOnlyDictionary<string, string>? env, string deploymentId, double healthTimeout, object? smoke = null, CancellationToken ct = default) => throw NotUsed();
    public Task<IReadOnlyList<SupervisorDeploymentEvent>> GetEventsAsync(string adminUrl, string deploymentId, int after = 0, CancellationToken ct = default) => throw NotUsed();
    public Task<string> GetDeploymentLogsAsync(string adminUrl, string deploymentId, int lines = 200, CancellationToken ct = default) => throw NotUsed();
    public Task<SupervisorStopResult> StopAsync(string adminUrl, CancellationToken ct = default) => throw NotUsed();
    public Task<SupervisorCancelResult> CancelAsync(string adminUrl, string deploymentId, CancellationToken ct = default) => throw NotUsed();
    public Task<string> RestartAsync(string adminUrl, string? deploymentId = null, CancellationToken ct = default) => throw NotUsed();
    public Task<SupervisorGpuInfo> GetGpuAsync(string adminUrl, CancellationToken ct = default) => throw NotUsed();
    public Task<string> GetAutoStartAsync(string adminUrl, CancellationToken ct = default) => throw NotUsed();
    public Task<string> SetAutoStartAsync(string adminUrl, string autoStart, CancellationToken ct = default) => throw NotUsed();
    public Task<SupervisorSmokeResult> SmokeTestAsync(string adminUrl, CancellationToken ct = default) => throw NotUsed();
    public Task<SupervisorDownloadInspectResult> InspectDownloadAsync(string adminUrl, string repoId, CancellationToken ct = default) => throw NotUsed();
    public Task<string> StartDownloadAsync(string adminUrl, string repoId, string slug, string? filename = null, CancellationToken ct = default) => throw NotUsed();
    public Task<SupervisorDownloadStatus> GetDownloadAsync(string adminUrl, string downloadId, CancellationToken ct = default) => throw NotUsed();
    public Task CancelDownloadAsync(string adminUrl, string downloadId, CancellationToken ct = default) => throw NotUsed();
    public virtual Task DeleteModelFilesAsync(string adminUrl, string path, CancellationToken ct = default) => throw NotUsed();
}
