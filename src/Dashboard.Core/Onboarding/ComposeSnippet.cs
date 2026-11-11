using Dashboard.Core.Models;

namespace Dashboard.Core.Onboarding;

public sealed record ComposeSnippetRequest
{
    public required SourceType Engine { get; init; }
    public GpuVendor Gpu { get; init; } = GpuVendor.None;

    /// <summary>Host path of the models root; mounted read-only at /models (the container-side
    /// root the arg builders substitute into every model path).</summary>
    public required string ModelsDir { get; init; }

    public string ServiceName { get; init; } = "supervisor";
    public string? ContainerName { get; init; }
    public int AdminPort { get; init; } = 8000;

    /// <summary>Where the supervisor pushes deployment events; omitted from the snippet when null.</summary>
    public string? DashboardEventUrl { get; init; }

    public string BuildContext { get; init; } = "./supervisor";
}

/// <summary>
/// The compose service block a user pastes into their own compose file to run this engine's
/// supervisor (P4). Pure and deterministic -- the dashboard never touches Docker; it only shows
/// text. Uses a <c>build:</c> block (the supervisor is built per hardware stack from this repo;
/// no supervisor images are published yet).
///
/// What is verified: Intel + vLLM mirrors the repo's reference supervisor service
/// (deploy/docker-compose.p16a-test.yml), and llama.cpp on CPU uses Dockerfile.llamacpp's own
/// pinned base. What is NOT verified in this repo, so the snippet asks the user to fill it in:
/// the NVIDIA vLLM base image, and any GPU-enabled llama.cpp base image. The NVIDIA device
/// reservation is Docker Compose's documented format, not something run here.
/// </summary>
public static class ComposeSnippet
{
    private const string IntelVllmBaseImage = "intel/llm-scaler-vllm:0.21.0-b3"; // supervisor/Dockerfile's own default

    /// <summary>User-facing label for a GPU choice, for the picker.</summary>
    public static string GpuLabel(GpuVendor gpu) => gpu switch
    {
        GpuVendor.Nvidia => "NVIDIA",
        GpuVendor.Intel => "Intel",
        _ => "None (CPU only)",
    };

    /// <summary>Best-effort preselection from a server's free-text GPU name -- only a default for the
    /// picker, never authoritative.</summary>
    public static GpuVendor GuessGpu(string? gpuName) =>
        gpuName?.Contains("nvidia", StringComparison.OrdinalIgnoreCase) == true ? GpuVendor.Nvidia
        : gpuName?.Contains("intel", StringComparison.OrdinalIgnoreCase) == true ? GpuVendor.Intel
        : GpuVendor.None;

    public static string Generate(ComposeSnippetRequest r)
    {
        if (!EngineCapabilities.Options.Any(o => o.Type == r.Engine))
            throw new NotSupportedException($"No supervisor image exists for engine '{r.Engine}'.");

        var vllm = r.Engine == SourceType.Vllm;
        var stateVolume = r.ServiceName + "-state";
        var lines = new List<string>
        {
            $"  {r.ServiceName}:",
            "    build:",
            $"      context: {r.BuildContext}",
            $"      dockerfile: {(vllm ? "Dockerfile" : "Dockerfile.llamacpp")}",
        };

        AppendBuildArgs(lines, r, vllm);

        if (r.ContainerName is { Length: > 0 })
            lines.Add($"    container_name: {r.ContainerName}");
        lines.Add("    restart: unless-stopped");
        lines.Add("    network_mode: host");
        lines.Add("    init: true");

        // vLLM wants a shared IPC namespace for its workers; llama.cpp is a single process.
        if (vllm)
            lines.Add("    ipc: host");
        if (vllm && r.Gpu == GpuVendor.Intel)
            lines.Add("    privileged: true");
        if (r.Gpu == GpuVendor.Intel)
        {
            lines.Add("    devices:");
            lines.Add("      - /dev/dri");
        }
        if (r.Gpu == GpuVendor.Nvidia)
        {
            lines.Add("    deploy:");
            lines.Add("      resources:");
            lines.Add("        reservations:");
            lines.Add("          devices:");
            lines.Add("            - driver: nvidia");
            lines.Add("              count: all");
            lines.Add("              capabilities: [gpu]");
        }

        lines.Add("    volumes:");
        if (r.Gpu == GpuVendor.Intel)
            lines.Add("      - /dev/dri/by-path:/dev/dri/by-path");
        lines.Add($"      - {stateVolume}:/state");
        lines.Add($"      - {r.ModelsDir.TrimEnd('/')}:/models:ro");

        lines.Add("    environment:");
        lines.Add($"      ADMIN_PORT: \"{r.AdminPort}\"");
        if (r.DashboardEventUrl is { Length: > 0 })
            lines.Add($"      DASHBOARD_EVENT_URL: \"{r.DashboardEventUrl}\"");
        if (vllm)
        {
            lines.Add("      VLLM_ALLOW_LONG_MAX_MODEL_LEN: \"1\"");
            lines.Add("      VLLM_WORKER_MULTIPROC_METHOD: \"spawn\"");
        }

        lines.Add("");
        lines.Add("# Also add to your top-level `volumes:` section:");
        lines.Add($"#   {stateVolume}:");
        return string.Join("\n", lines) + "\n";
    }

    private static void AppendBuildArgs(List<string> lines, ComposeSnippetRequest r, bool vllm)
    {
        if (vllm)
        {
            lines.Add("      args:");
            lines.Add(r.Gpu == GpuVendor.Intel
                ? $"        BASE_IMAGE: {IntelVllmBaseImage}"
                : "        BASE_IMAGE: vllm/vllm-openai:<pin-a-tag>");
        }
        else if (r.Gpu != GpuVendor.None)
        {
            // CPU needs no override (Dockerfile.llamacpp pins a CPU base); a GPU wants a GPU-enabled build.
            lines.Add("      args:");
            lines.Add("        # BASE_IMAGE: <a GPU-enabled llama.cpp server image for your hardware>");
        }
    }
}
