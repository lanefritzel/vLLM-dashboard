namespace Dashboard.Core.Supervisor;

public sealed record SwapResult
{
    /// <summary>"swapped" | "already_active" | "failed" | "stopped" | "cancelled".</summary>
    public required string Outcome { get; init; }

    /// <summary>The new deployments row id -- null for "already_active" (no
    /// row is created, per the page's own "not a swap" rule).</summary>
    public int? DeploymentId { get; init; }

    /// <summary>Kept in memory/the return value only this phase -- NOT
    /// persisted to the deployments row's own failure columns yet
    /// (P10b wires that up alongside the push receiver).</summary>
    public string? FailureReason { get; init; }
    public int? ExitCode { get; init; }
    public string? LogTail { get; init; }
}

/// <summary>Thrown when a swap would change the active deployment's
/// served_model_name without <c>acknowledge_served_name_change=true</c> --
/// a raw API call can't bypass the UI's click-through this way (doc 7).</summary>
public sealed class ServedNameChangeRequiresAcknowledgementException : Exception
{
    public string OldServedModelName { get; }
    public string NewServedModelName { get; }

    public ServedNameChangeRequiresAcknowledgementException(string oldName, string newName)
        : base($"Changing served_model_name from '{oldName}' to '{newName}' requires acknowledge_served_name_change=true.")
    {
        OldServedModelName = oldName;
        NewServedModelName = newName;
    }
}

/// <summary>Thrown when an instance's admin_url isn't set -- "a NULL
/// admin_url -> clear error 'supervisor not registered'" (doc 5/P10a).</summary>
public sealed class SupervisorNotRegisteredException : Exception
{
    public SupervisorNotRegisteredException(int instanceId)
        : base($"Instance {instanceId} has no supervisor registered (admin_url is not set).")
    {
    }
}
