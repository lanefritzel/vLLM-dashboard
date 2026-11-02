namespace Dashboard.Core.Abstractions;

/// <summary>One instance's reconcile state (P12) -- comparing the
/// supervisor's own <c>GET /admin/status</c> against Postgres, never
/// silently correcting either direction.</summary>
public enum ReconcileStatus
{
    /// <summary>Supervisor and DB agree -- nothing to show.</summary>
    Ok,

    /// <summary>Supervisor is serving a model the DB doesn't have as this
    /// instance's active deployment (either a different one, or none).
    /// Resolved by Adopt (record what's running) or Fix (force what the
    /// DB says back onto the supervisor).</summary>
    Mismatch,

    /// <summary>Supervisor is idle but the DB has an active deployment row.
    /// Resolved by Mark-stopped or a fresh swap/restart.</summary>
    IdleVsActive,

    /// <summary><c>admin_url</c> is null -- this instance has never had a
    /// supervisor registered. Not "down": the instance may be perfectly
    /// reachable for inference at its own configured URL, there's simply no
    /// admin API to reconcile against yet (P15b's job to fix).</summary>
    NeedsRegistration,

    /// <summary><c>admin_url</c> is set but unreachable. Shown with the
    /// DB's own last-known state; Start/Hard-reset container become
    /// available when the instance also has a <c>ContainerName</c>.</summary>
    Down,
}

/// <summary>One instance's computed reconcile state -- the UI (P14/P15, out
/// of scope here) renders this as a banner; this phase only needs the
/// computation and the actions it can trigger.</summary>
public sealed record ReconcileState
{
    public required int InstanceId { get; init; }
    public required ReconcileStatus Status { get; init; }

    /// <summary>What the supervisor itself reports right now -- "ready"/"idle"/etc,
    /// null when unreachable or not registered.</summary>
    public string? SupervisorState { get; init; }

    /// <summary>The supervisor child's actual argv, when running -- Adopt's
    /// own source of truth for what to record, never guessed.</summary>
    public IReadOnlyList<string>? SupervisorArgv { get; init; }

    /// <summary>The DB's own current active deployment id, if any.</summary>
    public int? DbActiveDeploymentId { get; init; }

    public string? Message { get; init; }
}

public interface IBootReconciler
{
    Task<ReconcileState> ReconcileInstanceAsync(int instanceId, CancellationToken ct = default);

    Task<IReadOnlyList<ReconcileState>> ReconcileAllAsync(CancellationToken ct = default);
}

/// <summary>The Adopt/Fix/Mark-stopped operator actions (P12 doc table).
/// Fix itself is <see cref="ISwapService.ForceRedeployAsync"/> -- already a
/// swap-pipeline operation, not reconcile-specific bookkeeping -- so it's
/// not repeated here.</summary>
public interface IReconcileActions
{
    /// <summary>"Adopt": record what the supervisor is ACTUALLY running as
    /// this instance's active deployment -- stops any DB row that disagrees
    /// first (one transaction), then inserts a new "active" row with
    /// <paramref name="actualArgv"/> frozen as its args_snapshot, never the
    /// recipe's own (possibly different) built argv. The model/recipe ids
    /// are the operator's own confirmed best-guess match (UI's job to
    /// propose, P14/P15) -- this method just records the pair it's given.</summary>
    Task AdoptAsync(int instanceId, int modelId, int recipeId, IReadOnlyList<string> actualArgv, CancellationToken ct = default);

    /// <summary>"Mark stopped": the DB says active but the supervisor is
    /// verifiably idle -- stops the DB's own active row, stop_reason="manual".</summary>
    Task MarkStoppedAsync(int instanceId, CancellationToken ct = default);
}
