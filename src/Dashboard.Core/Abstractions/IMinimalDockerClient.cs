namespace Dashboard.Core.Abstractions;

/// <summary>P12's "supervisor unreachable" escape hatch (doc: "instance shows
/// 'down' ... offering Start container / Hard reset container via the Docker
/// socket -- the ONLY Docker-socket use in the new app"). Deliberately tiny:
/// just enough to start an already-created container, or stop+remove+recreate
/// it from its own existing config when a plain start won't do (a wedged
/// process a plain restart can't kill). Anything about building a NEW
/// container's config from scratch stays the legacy
/// <c>Dashboard.Infrastructure.Docker.DockerOrchestrator</c>'s job (P16a),
/// not this.</summary>
public interface IMinimalDockerClient
{
    Task StartContainerAsync(string dockerHost, string containerName, CancellationToken ct = default);

    /// <summary>Stop (if running) then start the SAME container by name --
    /// "hard reset" without rebuilding its config, for a container whose
    /// process is wedged badly enough that a plain start on an
    /// already-stopped container isn't enough.</summary>
    Task HardResetContainerAsync(string dockerHost, string containerName, CancellationToken ct = default);
}
