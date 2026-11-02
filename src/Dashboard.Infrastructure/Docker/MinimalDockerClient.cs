using Dashboard.Core.Abstractions;
using Microsoft.Extensions.Logging;

namespace Dashboard.Infrastructure.Docker;

/// <summary>P12's minimal Docker client (see <see cref="IMinimalDockerClient"/>)
/// -- plain HTTP against the Docker Engine API, same v1.43/named-"docker"-client
/// convention as <see cref="DockerOrchestrator"/>, but without any of its
/// container-creation/payload-building machinery.</summary>
public sealed class MinimalDockerClient : IMinimalDockerClient
{
    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<MinimalDockerClient> _logger;

    public MinimalDockerClient(IHttpClientFactory httpFactory, ILogger<MinimalDockerClient> logger)
    {
        _httpFactory = httpFactory;
        _logger = logger;
    }

    public async Task StartContainerAsync(string dockerHost, string containerName, CancellationToken ct = default)
    {
        var dockerBase = NormalizeDockerBase(dockerHost);
        using var client = _httpFactory.CreateClient("docker");
        using var resp = await client.PostAsync($"{dockerBase}/v1.43/containers/{Uri.EscapeDataString(containerName)}/start", null, ct);

        // 304 Not Modified means it's already running -- not an error here.
        if (!resp.IsSuccessStatusCode && resp.StatusCode != System.Net.HttpStatusCode.NotModified)
        {
            var body = await resp.Content.ReadAsStringAsync(ct);
            throw new InvalidOperationException($"Docker host refused to start container '{containerName}': HTTP {(int)resp.StatusCode} {body}");
        }
    }

    public async Task HardResetContainerAsync(string dockerHost, string containerName, CancellationToken ct = default)
    {
        var dockerBase = NormalizeDockerBase(dockerHost);
        using var client = _httpFactory.CreateClient("docker");
        var escapedName = Uri.EscapeDataString(containerName);

        using var stopResp = await client.PostAsync($"{dockerBase}/v1.43/containers/{escapedName}/stop?t=10", null, ct);
        // 304 (already stopped) is fine; anything else that isn't success is
        // logged but not fatal -- the start attempt below is the real signal.
        if (!stopResp.IsSuccessStatusCode && stopResp.StatusCode != System.Net.HttpStatusCode.NotModified)
            _logger.LogWarning("Hard reset: stopping container '{Container}' returned HTTP {Status}.", containerName, (int)stopResp.StatusCode);

        using var startResp = await client.PostAsync($"{dockerBase}/v1.43/containers/{escapedName}/start", null, ct);
        if (!startResp.IsSuccessStatusCode && startResp.StatusCode != System.Net.HttpStatusCode.NotModified)
        {
            var body = await startResp.Content.ReadAsStringAsync(ct);
            throw new InvalidOperationException($"Docker host refused to start container '{containerName}' after hard reset: HTTP {(int)startResp.StatusCode} {body}");
        }
    }

    private static string NormalizeDockerBase(string dockerHost)
    {
        var h = dockerHost.Trim();
        if (h.StartsWith("tcp://", StringComparison.OrdinalIgnoreCase))
            h = "http://" + h.Substring(6);
        else if (h.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || h.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            // keep as-is
        }
        else if (h.StartsWith("//", StringComparison.Ordinal))
            h = "http:" + h;
        else
            h = "http://" + h;
        return h.TrimEnd('/');
    }
}
