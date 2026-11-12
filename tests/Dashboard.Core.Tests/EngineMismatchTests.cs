using Dashboard.Core.Models;
using Dashboard.Core.Onboarding;
using Dashboard.Core.Supervisor;

namespace Dashboard.Core.Tests;

/// <summary>P4: the dashboard checks the engine a supervisor reports against the one the user
/// picked, instead of silently trusting the selection.</summary>
public class EngineMismatchTests
{
    private static SupervisorEngineInfo Reported(string name) => new() { Name = name };

    [Theory]
    [InlineData(SourceType.Vllm, "vllm")]
    [InlineData(SourceType.LlamaCpp, "llamacpp")]
    public void Matching_HasNoWarning(SourceType chosen, string reported) =>
        Assert.Null(EngineMismatch.Check(chosen, Reported(reported)));

    [Fact]
    public void Mismatch_NamesBothEnginesInUserFacingTerms()
    {
        var message = EngineMismatch.Check(SourceType.LlamaCpp, Reported("vllm"));
        Assert.NotNull(message);
        Assert.Contains("vLLM", message);
        Assert.Contains("llama.cpp", message);
    }

    [Fact]
    public void ASupervisorThatReportsNothing_IsAPreP2VllmSupervisor()
    {
        Assert.Null(EngineMismatch.Check(SourceType.Vllm, null));
        Assert.NotNull(EngineMismatch.Check(SourceType.LlamaCpp, null));
    }

    [Fact]
    public void AnUnrecognisedReportedEngine_IsAMismatch_NotSilentlyAccepted()
    {
        var message = EngineMismatch.Check(SourceType.Vllm, Reported("ollama"));
        Assert.NotNull(message);
        Assert.Contains("ollama", message);
    }
}
