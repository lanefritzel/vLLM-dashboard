using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Dashboard.Core.Abstractions;
using Dashboard.Core.Models;
using Microsoft.Extensions.Logging;

namespace Dashboard.Infrastructure.Docker;

/// <summary>
/// Talks to the Docker Engine API (plain HTTP JSON over HttpClient). Implements the
/// hot-swap flow: inspect, stop, remove, pull-if-missing, create, start, health-wait.
/// </summary>
public sealed class DockerOrchestrator : IDockerOrchestrator
{
    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<DockerOrchestrator> _logger;

    public DockerOrchestrator(IHttpClientFactory httpFactory, ILogger<DockerOrchestrator> logger)
    {
        _httpFactory = httpFactory;
        _logger = logger;
    }

    public Task<bool> IsDockerHostConfiguredAsync(SourceInfo source, CancellationToken ct = default)
        => Task.FromResult(!string.IsNullOrWhiteSpace(source.DockerHost));

    public async Task<InferenceResult> SwapModelAsync(
        SourceInfo source, ModelInfo model, LaunchSpec spec,
        IAsyncActionProgress progress, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(source.DockerHost))
            return new InferenceResult { Success = false, Error = "No Docker host configured for this source." };

        var dockerBase = NormalizeDockerBase(source.DockerHost);
        var containerName = source.ContainerName ?? source.Label.Replace(" ", "-").ToLowerInvariant();
        var modelValue = model.SwapArgs ?? model.Name;

        try
        {
            await progress.ReportAsync(new SwapStep { Step = "stop", Message = "Stopping existing container" });
            await StopAndRemoveAsync(dockerBase, containerName, ct);

            await progress.ReportAsync(new SwapStep { Step = "pull", Message = "Ensuring image is present" });
            await EnsureImageAsync(dockerBase, spec.Image, ct);

            await progress.ReportAsync(new SwapStep { Step = "start", Message = "Creating and starting container" });
            await CreateAndStartAsync(dockerBase, containerName, spec, modelValue, ct);

            await progress.ReportAsync(new SwapStep { Step = "health", Message = "Waiting for backend health" });
            var healthy = await HealthWaitAsync(source.InferenceUrl, model.Name, ct);
            if (!healthy)
                return new InferenceResult { Success = false, Error = "Health check timed out after container start." };

            await progress.ReportAsync(new SwapStep { Step = "done", Message = "Swap complete", Complete = true });
            return new InferenceResult { Success = true, Text = model.Name };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Hot-swap failed for source {Source}.", source.Label);
            await progress.ReportAsync(new SwapStep { Step = "failed", Message = ex.Message, Complete = true });
            return new InferenceResult { Success = false, Error = ex.Message };
        }
    }

    private static string NormalizeDockerBase(string dockerHost)
    {
        var h = dockerHost.Trim();
        if (h.StartsWith("tcp://", StringComparison.OrdinalIgnoreCase))
            h = "http://" + h.Substring(6);
        else if (h.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                 h.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            // keep as-is
        }
        else if (h.StartsWith("//", StringComparison.Ordinal))
            h = "http:" + h;
        else
            h = "http://" + h;
        return h.TrimEnd('/');
    }

    private async Task StopAndRemoveAsync(string dockerBase, string name, CancellationToken ct)
    {
        using var client = _httpFactory.CreateClient("docker");
        using var inspect = await client.GetAsync($"{dockerBase}/v1.43/containers/{name}/json", ct);
        if (inspect.StatusCode == HttpStatusCode.NotFound)
            return; // nothing to stop

        await client.PostAsync($"{dockerBase}/v1.43/containers/{name}/stop?t=10", null, ct);
        await Task.Delay(500, ct);
        await client.DeleteAsync($"{dockerBase}/v1.43/containers/{name}?v=true&force=true", ct);
    }

    private async Task EnsureImageAsync(string dockerBase, string image, CancellationToken ct)
    {
        using var client = _httpFactory.CreateClient("docker");

        // Check local images for this exact reference.
        var listUrl = $"{dockerBase}/v1.43/images/{Uri.EscapeDataString(image)}/json";
        var found = false;
        try
        {
            using var get = await client.GetAsync(listUrl, ct);
            found = get.IsSuccessStatusCode;
        }
        catch { found = false; }
        if (found)
            return;

        var parts = image.Split(':');
        var repo = parts[0];
        var tag = parts.Length > 1 ? parts[1] : "latest";

        var pullUrl = $"{dockerBase}/v1.43/images/create?fromImage={Uri.EscapeDataString(repo)}&tag={Uri.EscapeDataString(tag)}";
        _logger.LogInformation("Pulling image {Image}.", image);
        using var pull = await client.PostAsync(pullUrl, null, ct);
        // Drain the pull progress (NDJSON) so the request completes.
        _ = await pull.Content.ReadAsStringAsync(ct);
    }

    private async Task CreateAndStartAsync(string dockerBase, string name, LaunchSpec spec, string modelValue, CancellationToken ct)
    {
        using var client = _httpFactory.CreateClient("docker");

        var cmd = spec.ModelArgTemplate.Replace("{model}", modelValue).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var portBindings = spec.Ports.ToDictionary(p => p.Container + "/tcp",
            p => new[] { new { HostPort = p.Host } });

        var config = new Dictionary<string, object>
        {
            ["Image"] = spec.Image,
            ["Cmd"] = cmd,
            ["Labels"] = new { created = "vllm-dashboard" },
        };

        var hostConfig = new Dictionary<string, object>
        {
            ["NetworkMode"] = "bridge",
            ["PortBindings"] = portBindings,
            ["RestartPolicy"] = new { Name = spec.RestartPolicy },
        };
        if (spec.Volumes.Count > 0)
        {
            hostConfig["Binds"] = spec.Volumes.Select(v =>
                $"{v.Host}:{v.Container}:{(v.ReadOnly ? "ro" : "rw")}").ToArray();
        }
        if (spec.Env.Count > 0)
            config["Env"] = spec.Env.Select(kv => $"{kv.Key}={kv.Value}").ToArray();
        if (!string.IsNullOrWhiteSpace(spec.GpuOptions))
        {
            hostConfig["DeviceRequests"] = new[]
            {
                new { Driver = "nvidia", DeviceIds = new[] { "all" }, Capabilities = new[] { new[] { "gpu" } } }
            };
        }

        var payload = new
        {
            name,
            config,
            hostConfig
        };

        var json = JsonSerializer.Serialize(payload);
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        using var create = await client.PostAsync($"{dockerBase}/v1.43/containers/create?name={Uri.EscapeDataString(name)}", content, ct);
        var createBody = await create.Content.ReadAsStringAsync(ct);
        if (!create.IsSuccessStatusCode)
            throw new InvalidOperationException($"Container create failed: {createBody}");

        var id = JsonSerializer.Deserialize<JsonElement>(createBody).GetProperty("Id").GetString();
        using var start = await client.PostAsync($"{dockerBase}/v1.43/containers/{id}/start", null, ct);
        if (!start.IsSuccessStatusCode)
            throw new InvalidOperationException($"Container start failed: {await start.Content.ReadAsStringAsync()}");
    }

    private async Task<bool> HealthWaitAsync(string inferenceUrl, string expectedModel, CancellationToken ct)
    {
        using var client = _httpFactory.CreateClient("docker");
        var deadline = DateTime.UtcNow.AddSeconds(180);
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                var resp = await client.GetAsync(inferenceUrl + "/v1/models", ct);
                if (resp.IsSuccessStatusCode)
                {
                    var body = await resp.Content.ReadAsStringAsync(ct);
                    if (body.Contains(expectedModel, StringComparison.Ordinal))
                        return true;
                }
            }
            catch { /* not ready yet */ }
            await Task.Delay(2000, ct);
        }
        return false;
    }
}
