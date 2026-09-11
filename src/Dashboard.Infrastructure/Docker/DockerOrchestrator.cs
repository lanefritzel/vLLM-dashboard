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
    private readonly ILaunchSpecStore _launchSpecs;
    private readonly ILogger<DockerOrchestrator> _logger;

    public DockerOrchestrator(
        IHttpClientFactory httpFactory,
        IGpuProvisionerFactory gpuProvisionerFactory,
        ILaunchSpecStore launchSpecs,
        ILogger<DockerOrchestrator> logger)
    {
        _httpFactory = httpFactory;
        _gpuProvisionerFactory = gpuProvisionerFactory;
        _launchSpecs = launchSpecs;
        _logger = logger;
    }

    public Task<bool> IsDockerHostConfiguredAsync(SourceInfo source, CancellationToken ct = default)
        => Task.FromResult(!string.IsNullOrWhiteSpace(source.DockerHost));

    public async Task<DockerHostTestResult> TestDockerHostAsync(string dockerHost, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(dockerHost))
            return new DockerHostTestResult { Success = false, Message = "Enter a Docker host first." };

        var dockerBase = NormalizeDockerBase(dockerHost);
        try
        {
            using var client = _httpFactory.CreateClient("docker");
            using var resp = await client.GetAsync($"{dockerBase}/v1.43/version", ct);
            if (!resp.IsSuccessStatusCode)
                return new DockerHostTestResult { Success = false, Message = $"Docker host responded with HTTP {(int)resp.StatusCode}." };

            var body = await resp.Content.ReadAsStringAsync(ct);
            var doc = JsonSerializer.Deserialize<JsonElement>(body);
            var version = doc.TryGetProperty("Version", out var v) && v.ValueKind == JsonValueKind.String
                ? v.GetString()
                : "unknown version";
            return new DockerHostTestResult { Success = true, Message = $"Connected — Docker {version}." };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new DockerHostTestResult { Success = false, Message = $"Could not reach Docker host: {ex.Message}" };
        }
    }

    public async Task<InferenceResult> SwapModelAsync(
        SourceInfo source, ModelInfo model, LaunchSpec spec,
        IAsyncActionProgress progress, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(source.DockerHost))
            return new InferenceResult { Success = false, Error = "No Docker host configured for this source." };

        var dockerBase = NormalizeDockerBase(source.DockerHost);
        var containerName = ResolveContainerName(source);
        var image = string.IsNullOrWhiteSpace(model.ImageOverride) ? spec.Image : model.ImageOverride;

        try
        {
            await progress.ReportAsync(new SwapStep { Step = "stop", Message = "Stopping existing container" });
            var (composeProject, composeService) = await StopAndRemoveAsync(dockerBase, containerName, ct);
            await PersistComposeLabelsIfObservedAsync(source.Id, spec, composeProject, composeService, ct);

            await progress.ReportAsync(new SwapStep { Step = "pull", Message = "Ensuring image is present" });
            await EnsureImageAsync(dockerBase, image, ct);

            await progress.ReportAsync(new SwapStep { Step = "start", Message = "Creating and starting container" });
            // The container we just stopped might not have existed at all (e.g. it was
            // idle-unloaded earlier, so there was nothing left to inspect) — fall back to
            // whatever labels were last observed and persisted for this source, so a
            // container recreated after idle-unload still rejoins its Compose stack.
            var effectiveProject = composeProject ?? spec.ComposeProject;
            var effectiveService = composeService ?? spec.ComposeService;
            await CreateAndStartAsync(dockerBase, containerName, spec, model, effectiveProject, effectiveService, ct);

            await progress.ReportAsync(new SwapStep
            {
                Step = "health",
                Message = model.ModelSource == ModelSource.HuggingFace
                    ? "Waiting for backend health (a first-time swap may be downloading the model from Hugging Face)"
                    : "Waiting for backend health",
            });
            var healthy = await HealthWaitAsync(source.InferenceUrl, model.Name, spec.HealthWaitTimeoutSeconds, ct);
            if (!healthy)
            {
                // A timed-out health check must not leave the container it just created
                // running unsupervised — that's exactly what let one bad swap balloon into
                // a full host OOM (the "failure" the UI reported was just this app giving
                // up watching; the actual process kept running and consuming memory with
                // nothing left to stop it).
                await progress.ReportAsync(new SwapStep { Step = "cleanup", Message = "Health check failed — stopping the container it started" });
                try
                {
                    await StopAndRemoveAsync(dockerBase, containerName, CancellationToken.None);
                }
                catch (Exception cleanupEx)
                {
                    _logger.LogError(cleanupEx, "Failed to clean up container {Container} after a failed health check.", containerName);
                }
                return new InferenceResult { Success = false, Error = "Health check timed out after container start. The container has been stopped." };
            }

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

    public async Task StreamLogsAsync(SourceInfo source, Func<string, Task> onLine, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(source.DockerHost))
            throw new InvalidOperationException("No Docker host configured for this source.");

        var dockerBase = NormalizeDockerBase(source.DockerHost);
        var containerName = ResolveContainerName(source);
        var url = $"{dockerBase}/v1.43/containers/{containerName}/logs?stdout=1&stderr=1&follow=1&tail=200";

        var client = _httpFactory.CreateClient("docker-logs");
        using var resp = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();
        await using var stream = await resp.Content.ReadAsStreamAsync(ct);

        // Docker multiplexes stdout/stderr over one connection (when the container
        // wasn't created with a TTY, which none of ours are): each frame is an 8-byte
        // header — [stream type][3 unused bytes][4-byte big-endian payload size] —
        // followed by that many bytes of the actual log text. Reading it as plain text
        // would corrupt lines with these binary headers mixed in.
        var header = new byte[8];
        while (true)
        {
            if (!await ReadExactAsync(stream, header, 8, ct))
                return;
            var size = ParseFrameSize(header);
            if (size == 0)
                continue;
            var payload = new byte[size];
            if (!await ReadExactAsync(stream, payload, size, ct))
                return;
            var text = Encoding.UTF8.GetString(payload);
            foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                await onLine(line);
        }
    }

    /// <summary>Big-endian uint32 payload size from a Docker log stream frame header
    /// (bytes 4-7; bytes 0-3 are the stream type + padding). Pure/no I/O.</summary>
    internal static int ParseFrameSize(byte[] header) =>
        (header[4] << 24) | (header[5] << 16) | (header[6] << 8) | header[7];

    private static async Task<bool> ReadExactAsync(Stream stream, byte[] buffer, int count, CancellationToken ct)
    {
        var offset = 0;
        while (offset < count)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset, count - offset), ct);
            if (read == 0)
                return false;
            offset += read;
        }
        return true;
    }

    public async Task<string?> GetActuallyRunningModelNameAsync(SourceInfo source, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(source.InferenceUrl))
            return null;
        try
        {
            var client = _httpFactory.CreateClient("inference");
            var url = source.InferenceUrl.TrimEnd('/') + "/v1/models";
            using var resp = await client.GetAsync(url, ct);
            if (!resp.IsSuccessStatusCode)
                return null;

            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            if (doc.RootElement.TryGetProperty("data", out var data) &&
                data.ValueKind == JsonValueKind.Array &&
                data.GetArrayLength() > 0 &&
                data[0].TryGetProperty("id", out var idEl) &&
                idEl.ValueKind == JsonValueKind.String)
            {
                return idEl.GetString();
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not query the actual running model for {Source}.", source.Label);
        }
        return null;
    }

    public async Task<InferenceResult> UnloadAsync(SourceInfo source, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(source.DockerHost))
            return new InferenceResult { Success = false, Error = "No Docker host configured for this source." };

        var dockerBase = NormalizeDockerBase(source.DockerHost);
        var containerName = ResolveContainerName(source);
        try
        {
            // Idle-unload deletes the container outright (no replacement gets created
            // here), so this is the ONLY chance to capture its Compose labels before
            // they're gone for good — the next swap-triggered recreate will have no
            // container left to inspect. Previously this result was discarded entirely,
            // which is exactly what caused a container recreated after idle-unload to
            // come back without its Compose labels and drop out of the stack view.
            var (composeProject, composeService) = await StopAndRemoveAsync(dockerBase, containerName, ct);
            await PersistComposeLabelsIfObservedAsync(source.Id, composeProject, composeService, ct);
            return new InferenceResult { Success = true };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Idle-unload failed for source {Source}.", source.Label);
            return new InferenceResult { Success = false, Error = ex.Message };
        }
    }

    /// <summary>Falls back to a Docker-valid name derived from the label when
    /// ContainerName isn't set. Sanitizes every run of non-alphanumeric characters (not
    /// just spaces) into a single hyphen — a label containing e.g. an em-dash produces
    /// an invalid Docker container name otherwise (a real incident: "tes — main vLLM"
    /// became the invalid "tes-—-main-vllm").</summary>
    private static string ResolveContainerName(SourceInfo source) =>
        string.IsNullOrWhiteSpace(source.ContainerName) ? Slug.From(source.Label) : source.ContainerName;

    /// <summary>Persists freshly observed Compose labels onto this source's LaunchSpec,
    /// if they're new — a one-time write per label change, not on every swap. No-op
    /// when nothing was observed (container didn't exist, or had no such labels), or
    /// when the spec already has these exact values, or when there's no spec to update
    /// yet at all.</summary>
    private async Task PersistComposeLabelsIfObservedAsync(string sourceId, string? composeProject, string? composeService, CancellationToken ct)
    {
        if (composeProject is null && composeService is null)
            return;
        try
        {
            var spec = await _launchSpecs.GetBySourceAsync(sourceId, ct);
            if (spec is null) return;
            await PersistComposeLabelsIfObservedAsync(sourceId, spec, composeProject, composeService, ct);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not persist Compose labels for source {SourceId}.", sourceId);
        }
    }

    private async Task PersistComposeLabelsIfObservedAsync(string sourceId, LaunchSpec spec, string? composeProject, string? composeService, CancellationToken ct)
    {
        if (composeProject is null && composeService is null)
            return;
        if (spec.ComposeProject == composeProject && spec.ComposeService == composeService)
            return;
        try
        {
            await _launchSpecs.SaveAsync(spec with
            {
                ComposeProject = composeProject ?? spec.ComposeProject,
                ComposeService = composeService ?? spec.ComposeService,
            }, ct);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not persist Compose labels for source {SourceId}.", sourceId);
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

    /// <summary>Stops and removes the existing container, returning its Compose
    /// project/service labels (if any) so the replacement can be stamped with the same
    /// ones — otherwise a container that was originally created by `docker compose up`
    /// becomes invisible to Compose/Portainer's stack view after the first hot-swap,
    /// even though it's running fine. Best-effort: a container with no such labels (or
    /// that doesn't exist yet) just yields (null, null), same as today's behavior.</summary>
    private async Task<(string? Project, string? Service)> StopAndRemoveAsync(string dockerBase, string name, CancellationToken ct)
    {
        using var client = _httpFactory.CreateClient("docker");
        using var inspect = await client.GetAsync($"{dockerBase}/v1.43/containers/{name}/json", ct);
        if (inspect.StatusCode == HttpStatusCode.NotFound)
            return (null, null); // nothing to stop, nothing to inherit

        string? project = null;
        string? service = null;
        try
        {
            var body = await inspect.Content.ReadAsStringAsync(ct);
            var doc = JsonSerializer.Deserialize<JsonElement>(body);
            if (doc.TryGetProperty("Config", out var config) &&
                config.TryGetProperty("Labels", out var labels) &&
                labels.ValueKind == JsonValueKind.Object)
            {
                if (labels.TryGetProperty("com.docker.compose.project", out var p) && p.ValueKind == JsonValueKind.String)
                    project = p.GetString();
                if (labels.TryGetProperty("com.docker.compose.service", out var s) && s.ValueKind == JsonValueKind.String)
                    service = s.GetString();
            }
        }
        catch
        {
            // Reading the old container's labels is a nice-to-have, not something that
            // should ever block a swap — fall through with (null, null).
        }

        await client.PostAsync($"{dockerBase}/v1.43/containers/{name}/stop?t=10", null, ct);
        await Task.Delay(500, ct);
        await client.DeleteAsync($"{dockerBase}/v1.43/containers/{name}?v=true&force=true", ct);
        return (project, service);
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

    private async Task CreateAndStartAsync(
        string dockerBase, string name, LaunchSpec spec, ModelInfo model,
        string? composeProject, string? composeService, CancellationToken ct)
    {
        using var client = _httpFactory.CreateClient("docker");

        var payload = BuildCreatePayload(spec, model, _gpuProvisionerFactory.Create(spec.GpuVendor), composeProject, composeService);

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
    internal static object BuildCreatePayload(
        LaunchSpec spec, ModelInfo model, IGpuProvisioner gpuProvisioner,
        string? composeProject = null, string? composeService = null)
    {
        var (modelValue, impliedVolume) = ResolveModel(model);

        var cmd = new List<string> { "serve", modelValue };
        if (spec.ServingPort is { } servingPort)
        {
            // ServingPort also drives HostConfig.PortBindings below, but that alone
            // only affects bridge-mode Docker port mapping — it does nothing under
            // host networking, and even under bridge mode the process itself still
            // needs to be told to bind here, or it falls back to vLLM's own default
            // port regardless of what Docker maps. Must pass this to the process
            // directly in both cases.
            cmd.Add("--port");
            cmd.Add(servingPort.ToString());
        }
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

        // Compose project/service labels, when the container being replaced had them,
        // are carried forward automatically (read from the old container by
        // StopAndRemoveAsync) so tools that read Compose labels — Portainer's stack
        // view, `docker compose ps`, etc. — still recognize the recreated container
        // instead of showing it as orphaned. No configuration needed for this; a
        // container that was never part of a Compose stack simply has neither.
        var labels = new Dictionary<string, string> { ["created"] = "vllm-dashboard" };
        if (!string.IsNullOrWhiteSpace(composeProject))
            labels["com.docker.compose.project"] = composeProject;
        if (!string.IsNullOrWhiteSpace(composeService))
            labels["com.docker.compose.service"] = composeService;

        var config = new Dictionary<string, object>
        {
            ["Image"] = image,
            ["Cmd"] = cmd,
            ["Labels"] = labels,
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
        if (spec.MemoryLimitMb is > 0)
            hostConfig["Memory"] = (long)spec.MemoryLimitMb.Value * 1024 * 1024;

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
