using Dashboard.Core.Models;
using Dashboard.Core.Onboarding;

namespace Dashboard.Core.Tests;

/// <summary>P4: the compose service block a user pastes into their own compose file. Pure text
/// out, no Docker. Intel/vLLM mirrors the repo's own reference supervisor service
/// (deploy/docker-compose.p16a-test.yml).</summary>
public class ComposeSnippetTests
{
    private static ComposeSnippetRequest Req(SourceType engine, GpuVendor gpu) =>
        new() { Engine = engine, Gpu = gpu, ModelsDir = "/data/models" };

    [Fact]
    public void Vllm_Intel_MatchesTheReferenceSupervisorService()
    {
        var text = ComposeSnippet.Generate(Req(SourceType.Vllm, GpuVendor.Intel));

        Assert.Equal("""
              supervisor:
                build:
                  context: ./supervisor
                  dockerfile: Dockerfile
                  args:
                    BASE_IMAGE: intel/llm-scaler-vllm:0.21.0-b3
                restart: unless-stopped
                network_mode: host
                init: true
                ipc: host
                privileged: true
                devices:
                  - /dev/dri
                volumes:
                  - /dev/dri/by-path:/dev/dri/by-path
                  - supervisor-state:/state
                  - /data/models:/models:ro
                environment:
                  ADMIN_PORT: "8000"
                  VLLM_ALLOW_LONG_MAX_MODEL_LEN: "1"
                  VLLM_WORKER_MULTIPROC_METHOD: "spawn"

            # Also add to your top-level `volumes:` section:
            #   supervisor-state:

            """.Replace("\r\n", "\n"), text);
    }

    [Fact]
    public void LlamaCpp_Cpu_UsesItsOwnDockerfile_AndNeedsNoGpuOrBaseImageOverride()
    {
        var text = ComposeSnippet.Generate(Req(SourceType.LlamaCpp, GpuVendor.None));

        Assert.Equal("""
              supervisor:
                build:
                  context: ./supervisor
                  dockerfile: Dockerfile.llamacpp
                restart: unless-stopped
                network_mode: host
                init: true
                volumes:
                  - supervisor-state:/state
                  - /data/models:/models:ro
                environment:
                  ADMIN_PORT: "8000"

            # Also add to your top-level `volumes:` section:
            #   supervisor-state:

            """.Replace("\r\n", "\n"), text);
    }

    [Fact]
    public void Vllm_Nvidia_ReservesTheGpu_AndAsksForAPinnedBaseImage()
    {
        var text = ComposeSnippet.Generate(Req(SourceType.Vllm, GpuVendor.Nvidia));
        Assert.Contains("driver: nvidia", text);
        Assert.Contains("capabilities: [gpu]", text);
        Assert.Contains("ipc: host", text);
        Assert.DoesNotContain("privileged", text);
        Assert.DoesNotContain("/dev/dri", text);
        // No NVIDIA vLLM base image is verified in this repo, so the user must choose one.
        Assert.Contains("BASE_IMAGE: vllm/vllm-openai:<pin-a-tag>", text);
    }

    [Fact]
    public void LlamaCpp_WithAGpu_AddsPassthrough_AndAsksForAGpuBuildOfTheBaseImage()
    {
        var intel = ComposeSnippet.Generate(Req(SourceType.LlamaCpp, GpuVendor.Intel));
        Assert.Contains("- /dev/dri", intel);
        Assert.Contains("# BASE_IMAGE:", intel);
        Assert.DoesNotContain("driver: nvidia", intel);

        var nvidia = ComposeSnippet.Generate(Req(SourceType.LlamaCpp, GpuVendor.Nvidia));
        Assert.Contains("driver: nvidia", nvidia);
        Assert.Contains("# BASE_IMAGE:", nvidia);
    }

    [Fact]
    public void LlamaCpp_NeverCarriesVllmOnlyEnvOrIpc()
    {
        foreach (var gpu in Enum.GetValues<GpuVendor>())
        {
            var text = ComposeSnippet.Generate(Req(SourceType.LlamaCpp, gpu));
            Assert.DoesNotContain("VLLM_", text);
            Assert.DoesNotContain("ipc: host", text);
        }
    }

    [Fact]
    public void ServiceName_ContainerName_AdminPort_AndEventUrl_AreUsed()
    {
        var text = ComposeSnippet.Generate(new ComposeSnippetRequest
        {
            Engine = SourceType.LlamaCpp, Gpu = GpuVendor.None, ModelsDir = "/m/",
            ServiceName = "llama-box", ContainerName = "llama-box-1", AdminPort = 18001,
            DashboardEventUrl = "http://127.0.0.1:8080/internal/supervisor-events",
        });
        Assert.StartsWith("  llama-box:\n", text);
        Assert.Contains("container_name: llama-box-1", text);
        Assert.Contains("llama-box-state:/state", text);
        Assert.Contains("ADMIN_PORT: \"18001\"", text);
        Assert.Contains("DASHBOARD_EVENT_URL: \"http://127.0.0.1:8080/internal/supervisor-events\"", text);
        Assert.Contains("- /m:/models:ro", text); // trailing slash trimmed
    }

    [Fact]
    public void EveryEngineAndGpuCombination_ProducesASnippet_SameInputsSameText()
    {
        foreach (var option in EngineCapabilities.Options)
            foreach (var gpu in Enum.GetValues<GpuVendor>())
            {
                var a = ComposeSnippet.Generate(Req(option.Type, gpu));
                Assert.Equal(a, ComposeSnippet.Generate(Req(option.Type, gpu)));
                Assert.Contains("ADMIN_PORT", a);
            }
    }

    [Fact]
    public void AnEngineWithoutASupervisorImage_IsRejected()
    {
        Assert.Throws<NotSupportedException>(() => ComposeSnippet.Generate(Req(SourceType.Ollama, GpuVendor.None)));
    }
}
