using System.Text.Json;
using Dashboard.Core.Abstractions;
using Dashboard.Core.Models;
using Dashboard.Infrastructure.Docker;

namespace Dashboard.Core.Tests;

public class DockerOrchestratorPayloadTests
{
    private static JsonElement ToJson(object payload) =>
        JsonSerializer.SerializeToElement(payload);

    private static ModelInfo HfModel(string repoId = "Qwen/Qwen2.5-7B-Instruct", string name = "qwen2.5-7b") => new()
    {
        Name = name,
        ModelSource = ModelSource.HuggingFace,
        ModelPathOrRepoId = repoId,
    };

    [Fact]
    public void BuildCreatePayload_PutsConfigFieldsAtTopLevelAndHostConfigAsSibling()
    {
        var spec = new LaunchSpec { Image = "vllm/vllm-openai:latest" };
        var json = ToJson(DockerOrchestrator.BuildCreatePayload(spec, HfModel(), new NoneGpuProvisioner()));

        Assert.Equal("vllm/vllm-openai:latest", json.GetProperty("Image").GetString());
        Assert.True(json.TryGetProperty("HostConfig", out _));
        Assert.False(json.TryGetProperty("config", out _));
        Assert.False(json.TryGetProperty("hostConfig", out _));
        Assert.False(json.TryGetProperty("name", out _));
    }

    [Fact]
    public void BuildCreatePayload_UsesServeSubcommandAndServedModelName()
    {
        var spec = new LaunchSpec();
        var json = ToJson(DockerOrchestrator.BuildCreatePayload(spec, HfModel(name: "my-model"), new NoneGpuProvisioner()));

        var cmd = json.GetProperty("Cmd").EnumerateArray().Select(e => e.GetString()).ToArray();
        Assert.Equal(new[] { "serve", "Qwen/Qwen2.5-7B-Instruct", "--served-model-name", "my-model" }, cmd);
    }

    [Fact]
    public void BuildCreatePayload_LocalPathModel_DerivesContainerPathAndAddsReadOnlyVolume()
    {
        var model = new ModelInfo
        {
            Name = "qwen3.8-vllm",
            ModelSource = ModelSource.LocalPath,
            ModelPathOrRepoId = "/media/external6tb/llm-models/hf-cache/Qwen3.8-27B-INT4",
        };
        var json = ToJson(DockerOrchestrator.BuildCreatePayload(new LaunchSpec(), model, new NoneGpuProvisioner()));

        var cmd = json.GetProperty("Cmd").EnumerateArray().Select(e => e.GetString()).ToArray();
        Assert.Equal("serve", cmd[0]);
        Assert.Equal("/models/qwen3-8-vllm", cmd[1]);

        var binds = json.GetProperty("HostConfig").GetProperty("Binds").EnumerateArray().Select(e => e.GetString()).ToArray();
        Assert.Contains("/media/external6tb/llm-models/hf-cache/Qwen3.8-27B-INT4:/models/qwen3-8-vllm:ro", binds);
    }

    [Fact]
    public void BuildCreatePayload_EmitsToggleOptionFlagOnlyWhenTrue()
    {
        var on = HfModel();
        on.Options["enable-auto-tool-choice"] = "true";
        var off = HfModel();
        off.Options["enable-auto-tool-choice"] = "false";

        var onCmd = ToJson(DockerOrchestrator.BuildCreatePayload(new LaunchSpec(), on, new NoneGpuProvisioner()))
            .GetProperty("Cmd").EnumerateArray().Select(e => e.GetString()).ToArray();
        var offCmd = ToJson(DockerOrchestrator.BuildCreatePayload(new LaunchSpec(), off, new NoneGpuProvisioner()))
            .GetProperty("Cmd").EnumerateArray().Select(e => e.GetString()).ToArray();

        Assert.Contains("--enable-auto-tool-choice", onCmd);
        Assert.DoesNotContain("--enable-auto-tool-choice", offCmd);
    }

    [Fact]
    public void BuildCreatePayload_EmitsValueOptionsAsFlagPlusValue()
    {
        var model = HfModel();
        model.Options["max-model-len"] = "131072";
        model.Options["dtype"] = "float16";

        var cmd = ToJson(DockerOrchestrator.BuildCreatePayload(new LaunchSpec(), model, new NoneGpuProvisioner()))
            .GetProperty("Cmd").EnumerateArray().Select(e => e.GetString()).ToArray();

        Assert.Contains("--max-model-len", cmd);
        Assert.Equal("131072", cmd[Array.IndexOf(cmd, "--max-model-len") + 1]);
        Assert.Contains("--dtype", cmd);
        Assert.Equal("float16", cmd[Array.IndexOf(cmd, "--dtype") + 1]);
    }

    [Fact]
    public void BuildCreatePayload_IgnoresUnknownOptionKeys()
    {
        var model = HfModel();
        model.Options["not-a-real-flag"] = "whatever";

        var cmd = ToJson(DockerOrchestrator.BuildCreatePayload(new LaunchSpec(), model, new NoneGpuProvisioner()))
            .GetProperty("Cmd").EnumerateArray().Select(e => e.GetString()).ToArray();

        Assert.DoesNotContain("whatever", cmd);
        Assert.DoesNotContain("--not-a-real-flag", cmd);
    }

    [Fact]
    public void BuildCreatePayload_ComposeLabels_AppliedWhenProvided()
    {
        var json = ToJson(DockerOrchestrator.BuildCreatePayload(
            new LaunchSpec(), HfModel(), new NoneGpuProvisioner(),
            composeProject: "docker-compose", composeService: "vllm-scaler"));

        var labels = json.GetProperty("Labels");
        Assert.Equal("docker-compose", labels.GetProperty("com.docker.compose.project").GetString());
        Assert.Equal("vllm-scaler", labels.GetProperty("com.docker.compose.service").GetString());
        Assert.Equal("vllm-dashboard", labels.GetProperty("created").GetString());
    }

    [Fact]
    public void BuildCreatePayload_ComposeLabels_OmittedWhenNotProvided()
    {
        var json = ToJson(DockerOrchestrator.BuildCreatePayload(new LaunchSpec(), HfModel(), new NoneGpuProvisioner()));

        var labels = json.GetProperty("Labels");
        Assert.False(labels.TryGetProperty("com.docker.compose.project", out _));
        Assert.False(labels.TryGetProperty("com.docker.compose.service", out _));
    }

    [Fact]
    public void BuildCreatePayload_ServingPort_PassesPortFlagToProcess()
    {
        // Docker's PortBindings alone doesn't make the process bind to the right port —
        // under host networking it does nothing at all, and even under bridge mode the
        // process itself must be told to bind here or it falls back to vLLM's own
        // default port regardless of what Docker maps.
        var spec = new LaunchSpec { ServingPort = 9999 };
        var cmd = ToJson(DockerOrchestrator.BuildCreatePayload(spec, HfModel(), new NoneGpuProvisioner()))
            .GetProperty("Cmd").EnumerateArray().Select(e => e.GetString()).ToArray();

        Assert.Contains("--port", cmd);
        Assert.Equal("9999", cmd[Array.IndexOf(cmd, "--port") + 1]);
    }

    [Fact]
    public void BuildCreatePayload_OmitsPortFlagWhenServingPortNotSet()
    {
        var cmd = ToJson(DockerOrchestrator.BuildCreatePayload(new LaunchSpec(), HfModel(), new NoneGpuProvisioner()))
            .GetProperty("Cmd").EnumerateArray().Select(e => e.GetString()).ToArray();

        Assert.DoesNotContain("--port", cmd);
    }

    [Fact]
    public void BuildCreatePayload_AppendsExtraRunArgsAfterOptions()
    {
        var spec = new LaunchSpec { ExtraRunArgs = "--foo bar" };
        var cmd = ToJson(DockerOrchestrator.BuildCreatePayload(spec, HfModel(name: "m1"), new NoneGpuProvisioner()))
            .GetProperty("Cmd").EnumerateArray().Select(e => e.GetString()).ToArray();

        Assert.Equal(new[] { "serve", "Qwen/Qwen2.5-7B-Instruct", "--served-model-name", "m1", "--foo", "bar" }, cmd);
    }

    [Fact]
    public void BuildCreatePayload_UsesModelImageOverrideWhenSet()
    {
        var spec = new LaunchSpec { Image = "default:latest" };
        var model = HfModel();
        model = model with { ImageOverride = "special:v2" };

        var json = ToJson(DockerOrchestrator.BuildCreatePayload(spec, model, new NoneGpuProvisioner()));
        Assert.Equal("special:v2", json.GetProperty("Image").GetString());
    }

    [Fact]
    public void BuildCreatePayload_FallsBackToSpecImageWhenNoOverride()
    {
        var spec = new LaunchSpec { Image = "default:latest" };
        var json = ToJson(DockerOrchestrator.BuildCreatePayload(spec, HfModel(), new NoneGpuProvisioner()));
        Assert.Equal("default:latest", json.GetProperty("Image").GetString());
    }

    [Fact]
    public void BuildCreatePayload_SetsEntrypointOnlyWhenConfigured()
    {
        var withEntrypoint = new LaunchSpec { Entrypoint = "vllm" };
        var withoutEntrypoint = new LaunchSpec();

        var withJson = ToJson(DockerOrchestrator.BuildCreatePayload(withEntrypoint, HfModel(), new NoneGpuProvisioner()));
        var withoutJson = ToJson(DockerOrchestrator.BuildCreatePayload(withoutEntrypoint, HfModel(), new NoneGpuProvisioner()));

        var entrypoint = withJson.GetProperty("Entrypoint").EnumerateArray().Select(e => e.GetString()).ToArray();
        Assert.Equal(new[] { "vllm" }, entrypoint);
        Assert.False(withoutJson.TryGetProperty("Entrypoint", out _));
    }

    [Fact]
    public void BuildCreatePayload_UsesServingPortForSinglePortBinding()
    {
        var spec = new LaunchSpec { ServingPort = 9999 };
        var json = ToJson(DockerOrchestrator.BuildCreatePayload(spec, HfModel(), new NoneGpuProvisioner()));

        var bindings = json.GetProperty("HostConfig").GetProperty("PortBindings");
        var hostPort = bindings.GetProperty("9999/tcp")[0].GetProperty("HostPort").GetString();
        Assert.Equal("9999", hostPort);
    }

    [Fact]
    public void BuildCreatePayload_OmitsPortBindingsWhenServingPortNotSet()
    {
        var json = ToJson(DockerOrchestrator.BuildCreatePayload(new LaunchSpec(), HfModel(), new NoneGpuProvisioner()));
        Assert.False(json.GetProperty("HostConfig").TryGetProperty("PortBindings", out _));
    }

    [Fact]
    public void BuildCreatePayload_HfCacheHostPath_AddsVolumeAndSetsHfHome()
    {
        var spec = new LaunchSpec { HfCacheHostPath = "/home/user/.cache/huggingface" };
        var json = ToJson(DockerOrchestrator.BuildCreatePayload(spec, HfModel(), new NoneGpuProvisioner()));

        var binds = json.GetProperty("HostConfig").GetProperty("Binds").EnumerateArray().Select(e => e.GetString()).ToArray();
        Assert.Contains("/home/user/.cache/huggingface:/root/.cache/huggingface:rw", binds);

        var env = json.GetProperty("Env").EnumerateArray().Select(e => e.GetString()).ToArray();
        Assert.Contains("HF_HOME=/root/.cache/huggingface", env);
    }

    [Fact]
    public void BuildCreatePayload_HfToken_SetsEnvVar()
    {
        var spec = new LaunchSpec { HfToken = "hf_secret123" };
        var json = ToJson(DockerOrchestrator.BuildCreatePayload(spec, HfModel(), new NoneGpuProvisioner()));

        var env = json.GetProperty("Env").EnumerateArray().Select(e => e.GetString()).ToArray();
        Assert.Contains("HF_TOKEN=hf_secret123", env);
    }

    [Fact]
    public void BuildCreatePayload_UsesSpecNetworkMode()
    {
        var spec = new LaunchSpec { NetworkMode = "host" };
        var json = ToJson(DockerOrchestrator.BuildCreatePayload(spec, HfModel(), new NoneGpuProvisioner()));

        Assert.Equal("host", json.GetProperty("HostConfig").GetProperty("NetworkMode").GetString());
    }

    [Fact]
    public void BuildCreatePayload_SetsPrivilegedOnlyWhenTrue()
    {
        var on = new LaunchSpec { Privileged = true };
        var off = new LaunchSpec { Privileged = false };

        var onJson = ToJson(DockerOrchestrator.BuildCreatePayload(on, HfModel(), new NoneGpuProvisioner()));
        var offJson = ToJson(DockerOrchestrator.BuildCreatePayload(off, HfModel(), new NoneGpuProvisioner()));

        Assert.True(onJson.GetProperty("HostConfig").GetProperty("Privileged").GetBoolean());
        Assert.False(offJson.GetProperty("HostConfig").TryGetProperty("Privileged", out _));
    }

    [Fact]
    public void BuildCreatePayload_SetsIpcModeOnlyWhenSet()
    {
        var withIpc = new LaunchSpec { IpcMode = "host" };
        var withoutIpc = new LaunchSpec();

        var withJson = ToJson(DockerOrchestrator.BuildCreatePayload(withIpc, HfModel(), new NoneGpuProvisioner()));
        var withoutJson = ToJson(DockerOrchestrator.BuildCreatePayload(withoutIpc, HfModel(), new NoneGpuProvisioner()));

        Assert.Equal("host", withJson.GetProperty("HostConfig").GetProperty("IpcMode").GetString());
        Assert.False(withoutJson.GetProperty("HostConfig").TryGetProperty("IpcMode", out _));
    }

    [Fact]
    public void NvidiaGpuProvisioner_DefaultsToAllWhenGpuOptionsBlank()
    {
        var spec = new LaunchSpec { GpuVendor = GpuVendor.Nvidia, GpuOptions = "" };
        var json = ToJson(DockerOrchestrator.BuildCreatePayload(spec, HfModel(), new NvidiaGpuProvisioner()));

        var req = json.GetProperty("HostConfig").GetProperty("DeviceRequests")[0];
        Assert.Equal("nvidia", req.GetProperty("Driver").GetString());
        var ids = req.GetProperty("DeviceIds").EnumerateArray().Select(e => e.GetString()).ToArray();
        Assert.Equal(new[] { "all" }, ids);
    }

    [Fact]
    public void NvidiaGpuProvisioner_ParsesCommaSeparatedDeviceIds()
    {
        var spec = new LaunchSpec { GpuVendor = GpuVendor.Nvidia, GpuOptions = "0,1" };
        var json = ToJson(DockerOrchestrator.BuildCreatePayload(spec, HfModel(), new NvidiaGpuProvisioner()));

        var ids = json.GetProperty("HostConfig").GetProperty("DeviceRequests")[0]
            .GetProperty("DeviceIds").EnumerateArray().Select(e => e.GetString()).ToArray();
        Assert.Equal(new[] { "0", "1" }, ids);
    }

    [Fact]
    public void IntelGpuProvisioner_BuildsDevicesFromSpec()
    {
        var spec = new LaunchSpec
        {
            GpuVendor = GpuVendor.Intel,
            Devices = new List<DeviceMapping> { new() { HostPath = "/dev/dri", ContainerPath = "/dev/dri" } },
        };
        var json = ToJson(DockerOrchestrator.BuildCreatePayload(spec, HfModel(), new IntelGpuProvisioner()));

        var device = json.GetProperty("HostConfig").GetProperty("Devices")[0];
        Assert.Equal("/dev/dri", device.GetProperty("PathOnHost").GetString());
        Assert.Equal("/dev/dri", device.GetProperty("PathInContainer").GetString());
        Assert.Equal("rwm", device.GetProperty("CgroupPermissions").GetString());
    }

    [Fact]
    public void IntelGpuProvisioner_OmitsDevicesKeyWhenEmpty()
    {
        var spec = new LaunchSpec { GpuVendor = GpuVendor.Intel };
        var json = ToJson(DockerOrchestrator.BuildCreatePayload(spec, HfModel(), new IntelGpuProvisioner()));

        Assert.False(json.GetProperty("HostConfig").TryGetProperty("Devices", out _));
    }

    [Fact]
    public void NoneGpuProvisioner_AddsNoGpuKeys()
    {
        var spec = new LaunchSpec { GpuVendor = GpuVendor.None };
        var json = ToJson(DockerOrchestrator.BuildCreatePayload(spec, HfModel(), new NoneGpuProvisioner()));

        Assert.False(json.GetProperty("HostConfig").TryGetProperty("DeviceRequests", out _));
        Assert.False(json.GetProperty("HostConfig").TryGetProperty("Devices", out _));
    }

    [Fact]
    public void GpuProvisionerFactory_PicksCorrectImplementationPerVendor()
    {
        var factory = new GpuProvisionerFactory();
        Assert.IsType<NvidiaGpuProvisioner>(factory.Create(GpuVendor.Nvidia));
        Assert.IsType<IntelGpuProvisioner>(factory.Create(GpuVendor.Intel));
        Assert.IsType<NoneGpuProvisioner>(factory.Create(GpuVendor.None));
    }

    [Theory]
    [InlineData("vllm/vllm-openai:latest", "vllm/vllm-openai", "latest")]
    [InlineData("intel/llm-scaler-vllm:0.21.0-b3", "intel/llm-scaler-vllm", "0.21.0-b3")]
    [InlineData("myregistry.local:5000/vllm/vllm-openai:v0.5.0", "myregistry.local:5000/vllm/vllm-openai", "v0.5.0")]
    [InlineData("myregistry.local:5000/vllm/vllm-openai", "myregistry.local:5000/vllm/vllm-openai", "latest")]
    [InlineData("busybox", "busybox", "latest")]
    public void SplitImageReference_HandlesRegistryPortsAndBareNames(string image, string expectedRepo, string expectedTag)
    {
        var (repo, tag) = DockerOrchestrator.SplitImageReference(image);
        Assert.Equal(expectedRepo, repo);
        Assert.Equal(expectedTag, tag);
    }

    [Theory]
    [InlineData(new byte[] { 1, 0, 0, 0, 0, 0, 0, 0 }, 0)]
    [InlineData(new byte[] { 1, 0, 0, 0, 0, 0, 0, 1 }, 1)]
    [InlineData(new byte[] { 1, 0, 0, 0, 0, 0, 1, 0 }, 256)]
    [InlineData(new byte[] { 2, 0, 0, 0, 0, 1, 0, 0 }, 65536)]
    [InlineData(new byte[] { 1, 0, 0, 0, 0x00, 0x01, 0x00, 0x00 }, 65536)]
    public void ParseFrameSize_ReadsBigEndianSizeFromBytesFourToSeven(byte[] header, int expected)
    {
        Assert.Equal(expected, DockerOrchestrator.ParseFrameSize(header));
    }
}
