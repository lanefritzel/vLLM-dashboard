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
/// Command construction is model-driven: everything specific to running one model
/// (weights location, launch flags) comes from that <see cref="ModelInfo"/>, while
/// <see cref="LaunchSpec"/> only supplies things true of the physical box/Docker host.
/// </summary>
public sealed class DockerOrchestrator : IDockerOrchestrator
{
    private readonly IHttpClientFactory _httpFactory;
    private readonly IGpuProvisionerFactory _gpuProvisionerFactory;
    private readonly ILogger<DockerOrchestrator> _logger;

    public DockerOrchestrator(
        IHttpClientFactory httpFactory,
        IGpuProvisionerFactory gpuProvisionerFactory,
        ILogger<DockerOrchestrator> logger)
    {
        _httpFactory = httpFactory;
        _gpuProvisionerFactory = gpuProvisionerFactory;
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
        var image = string.IsNullOrWhiteSpace(model.ImageOverride) ? spec.Image : model.ImageOverride;

        try
        {
            await progress.ReportAsync(new SwapStep { Step = "stop", Message = "Stopping existing container" });
            await StopAndRemoveAsync(dockerBase, containerName, ct);

            await progress.ReportAsync(new SwapStep { Step = "pull", Message = "Ensuring image is present" });
            await EnsureImageAsync(dockerBase, image, ct);

            await progress.ReportAsync(new SwapStep { Step = "start", Message = "Creating and starting container" });
            await CreateAndStartAsync(dockerBase, containerName, spec, model, ct);

            await progress.ReportAsync(new SwapStep
            {
                Step = "health",
                Message = model.ModelSource == ModelSource.HuggingFace
                    ? "Waiting for backend health (a first-time swap may be downloading the model from Hugging Face)"
                    : "Waiting for backend health",
            });
            var healthy = await HealthWaitAsync(source.InferenceUrl, model.Name, spec.HealthWaitTimeoutSeconds, ct);
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

        var (repo, tag) = SplitImageReference(image);

        var pullUrl = $"{dockerBase}/v1.43/images/create?fromImage={Uri.EscapeDataString(repo)}&tag={Uri.EscapeDataString(tag)}";
        _logger.LogInformation("Pulling image {Image}.", image);
        using var pull = await client.PostAsync(pullUrl, null, ct);
        // Drain the pull progress (NDJSON) so the request completes.
        _ = await pull.Content.ReadAsStringAsync(ct);
    }

    /// <summary>Splits "repo:tag" on the LAST colon, so registry:port/repo:tag refs
    /// (e.g. "myregistry.local:5000/vllm/vllm-openai:v0.5.0") split correctly instead
    /// of treating the registry's port as the tag.</summary>
    internal static (string Repo, string Tag) SplitImageReference(string image)
    {
        var lastColon = image.LastIndexOf(':');
        // A colon before the last '/' belongs to a registry:port, not a tag separator.
        var lastSlash = image.LastIndexOf('/');
        if (lastColon < 0 || lastColon < lastSlash)
            return (image, "latest");
        return (image[..lastColon], image[(lastColon + 1)..]);
    }

    private async Task CreateAndStartAsync(string dockerBase, string name, LaunchSpec spec, ModelInfo model, CancellationToken ct)
    {
        using var client = _httpFactory.CreateClient("docker");

        var payload = BuildCreatePayload(spec, model, _gpuProvisionerFactory.Create(spec.GpuVendor));

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

    /// <summary>Resolves the --model value and, for a LocalPath model, the implied
    /// read-only volume mount for its weights folder. HuggingFace models pass their
    /// repo id straight through with no implied volume (vLLM downloads/caches it
    /// itself); LocalPath models get a fixed, dashboard-owned container path so the
    /// user only ever types the host-side folder once, never keeping two paths in sync.</summary>
    internal static (string ModelValue, VolumeMapping? ImpliedVolume) ResolveModel(ModelInfo model)
    {
        if (model.ModelSource == ModelSource.LocalPath)
        {
            var containerPath = "/models/" + SanitizeForPath(model.Name);
            return (containerPath, new VolumeMapping { Host = model.ModelPathOrRepoId, Container = containerPath, ReadOnly = true });
        }
        return (model.ModelPathOrRepoId, null);
    }

    private static string SanitizeForPath(string value)
    {
        var chars = value.Trim().ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '-');
        var sanitized = new string(chars.ToArray());
        return string.IsNullOrEmpty(sanitized) ? "model" : sanitized;
    }

    /// <summary>
    /// Builds the exact Docker container-create request body. Pure/no I/O, so it's
    /// unit-testable without a live Docker daemon: given a LaunchSpec + ModelInfo (and
    /// the GPU provisioner picked for the spec's vendor), this is everything the
    /// orchestrator sends.
    /// </summary>
    internal static object BuildCreatePayload(LaunchSpec spec, ModelInfo model, IGpuProvisioner gpuProvisioner)
    {
        var (modelValue, impliedVolume) = ResolveModel(model);

        var cmd = new List<string> { "serve", modelValue };
        foreach (var (key, value) in model.Options)
        {
            var def = VllmOptionCatalog.Find(key);
            if (def is null) continue; // ignore anything not in the known catalog
            if (def.ControlType == VllmOptionControlType.Toggle)
            {
                if (string.Equals(value, "true", StringComparison.OrdinalIgnoreCase))
                    cmd.Add(def.CliFlag);
            }
            else if (!string.IsNullOrWhiteSpace(value))
            {
                cmd.Add(def.CliFlag);
                cmd.Add(value);
            }
        }
        cmd.Add("--served-model-name");
        cmd.Add(model.Name);
        if (!string.IsNullOrWhiteSpace(spec.ExtraRunArgs))
            cmd.AddRange(spec.ExtraRunArgs.Split(' ', StringSplitOptions.RemoveEmptyEntries));

        var image = string.IsNullOrWhiteSpace(model.ImageOverride) ? spec.Image : model.ImageOverride;

        var config = new Dictionary<string, object>
        {
            ["Image"] = image,
            ["Cmd"] = cmd,
            ["Labels"] = new { created = "vllm-dashboard" },
        };
        if (!string.IsNullOrWhiteSpace(spec.Entrypoint))
            config["Entrypoint"] = spec.Entrypoint.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        var hostConfig = new Dictionary<string, object>
        {
            ["NetworkMode"] = string.IsNullOrWhiteSpace(spec.NetworkMode) ? "bridge" : spec.NetworkMode,
            ["RestartPolicy"] = new { Name = spec.RestartPolicy },
        };
        if (spec.ServingPort is { } port)
        {
            hostConfig["PortBindings"] = new Dictionary<string, object>
            {
                [port + "/tcp"] = new[] { new { HostPort = port.ToString() } },
            };
        }

        var volumes = new List<VolumeMapping>(spec.Volumes);
        if (impliedVolume is not null)
            volumes.Add(impliedVolume);

        var env = new Dictionary<string, string>(spec.Env);
        if (!string.IsNullOrWhiteSpace(spec.HfCacheHostPath))
        {
            const string hfCacheContainerPath = "/root/.cache/huggingface";
            volumes.Add(new VolumeMapping { Host = spec.HfCacheHostPath, Container = hfCacheContainerPath });
            env["HF_HOME"] = hfCacheContainerPath;
        }
        if (!string.IsNullOrWhiteSpace(spec.HfToken))
            env["HF_TOKEN"] = spec.HfToken;

        if (volumes.Count > 0)
        {
            hostConfig["Binds"] = volumes.Select(v =>
                $"{v.Host}:{v.Container}:{(v.ReadOnly ? "ro" : "rw")}").ToArray();
        }
        if (env.Count > 0)
            config["Env"] = env.Select(kv => $"{kv.Key}={kv.Value}").ToArray();
        if (spec.Privileged)
            hostConfig["Privileged"] = true;
        if (!string.IsNullOrWhiteSpace(spec.IpcMode))
            hostConfig["IpcMode"] = spec.IpcMode;

        gpuProvisioner.Apply(hostConfig, spec);

        // Docker's ContainerCreate expects config fields (Image/Cmd/Env/Labels) at the
        // top level of the body, with HostConfig as a sibling top-level key — not
        // nested under "config"/"hostConfig" wrapper keys. `name` is passed via the
        // query string on the create call, not the body.
        var body = new Dictionary<string, object>(config)
        {
            ["HostConfig"] = hostConfig,
        };
        return body;
    }

    private async Task<bool> HealthWaitAsync(string inferenceUrl, string expectedModel, int timeoutSeconds, CancellationToken ct)
    {
        using var client = _httpFactory.CreateClient("docker");
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds > 0 ? timeoutSeconds : 600);
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
