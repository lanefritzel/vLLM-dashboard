using Dashboard.Core.Models;

namespace Dashboard.Core.Tests;

public class LaunchSpecTests
{
    [Fact]
    public void SubstituteModel_ReplacesToken()
    {
        var spec = new LaunchSpec { Id = "s1", SourceId = "src", Image = "vllm/vllm-openai:latest", ModelArgTemplate = "--model {model} --serve" };
        Assert.Equal("--model qwen3.8-vllm --serve", spec.SubstituteModel("qwen3.8-vllm"));
    }

    [Fact]
    public void SubstituteModel_NoTokenLeavesTemplateUnchanged()
    {
        var spec = new LaunchSpec { ModelArgTemplate = "--flag only" };
        Assert.Equal("--flag only", spec.SubstituteModel("any"));
    }

    [Fact]
    public void SubstituteModel_ReplacesAllOccurrences()
    {
        var spec = new LaunchSpec { ModelArgTemplate = "--model {model} --alt {model}" };
        Assert.Equal("--model x --alt x", spec.SubstituteModel("x"));
    }

    [Fact]
    public void Defaults_AreSensible()
    {
        var spec = new LaunchSpec();
        Assert.Equal("--model {model}", spec.ModelArgTemplate);
        Assert.Equal("unless-stopped", spec.RestartPolicy);
        Assert.Empty(spec.Ports);
        Assert.Empty(spec.Volumes);
        Assert.Empty(spec.Env);
    }
}
