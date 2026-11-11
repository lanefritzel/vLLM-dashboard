using Dashboard.Core.Models;
using Dashboard.Core.Supervisor;

namespace Dashboard.Core.Onboarding;

/// <summary>Compares the engine a supervisor reports (GET /admin/status) with the one the user
/// chose for the instance, so a mismatch is shown instead of silently trusted (P4).</summary>
public static class EngineMismatch
{
    /// <summary>A user-facing warning, or null when they agree. A supervisor that reports no engine
    /// predates engine reporting and is vLLM-only.</summary>
    public static string? Check(SourceType chosen, SupervisorEngineInfo? reported)
    {
        var chosenLabel = EngineCapabilities.DisplayName(chosen);
        if (reported is null)
        {
            return chosen == SourceType.Vllm
                ? null
                : $"This supervisor doesn't report an engine (it predates engine reporting, so it runs vLLM), but this instance is set to {chosenLabel}.";
        }

        if (reported.Name == chosen.ToStorage())
            return null;

        var reportedLabel = EngineCapabilities.Options.FirstOrDefault(o => o.Storage == reported.Name)?.Label ?? reported.Name;
        return $"This supervisor reports engine '{reportedLabel}', but this instance is set to {chosenLabel}. " +
               "Change the instance's engine, or point it at a supervisor running that engine.";
    }
}
