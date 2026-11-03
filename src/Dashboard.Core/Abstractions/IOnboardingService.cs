namespace Dashboard.Core.Abstractions;

/// <summary>P15b's first-run onboarding checklist: whether to show it at all,
/// and which of its 4 steps are already done.</summary>
public interface IOnboardingService
{
    Task<OnboardingStatus> GetStatusAsync(CancellationToken ct = default);
}

public sealed record OnboardingStatus
{
    /// <summary>False for a database that was ever converted from the legacy
    /// schema (doc: "an upgraded DB with converted rows skips it") or that
    /// already has a deployment -- "never reappears" falls out of this being
    /// a live check against deployments, which this app never hard-deletes.</summary>
    public required bool ShouldShow { get; init; }

    public required bool HasServerAndInstance { get; init; }
    public required bool HasModel { get; init; }
    public required bool HasRecipe { get; init; }
    public required bool HasDeployment { get; init; }
}
