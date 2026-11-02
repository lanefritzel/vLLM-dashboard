using Dashboard.Core.Supervisor;

namespace Dashboard.Core.Abstractions;

/// <summary>The first end-to-end swap flow (P10a): validate -> build argv ->
/// record a pending deployment -> ask the supervisor -> track to a terminal
/// state. The dashboard asks; the supervisor does -- nothing here starts a
/// model directly, and no code path touches the Docker API.</summary>
public interface ISwapService
{
    /// <summary>Deploys the given (model, recipe) to the instance. Returns
    /// "already_active" (no new row) if that exact (model, recipe) pair is
    /// already the active deployment. Throws
    /// <see cref="ServedNameChangeRequiresAcknowledgementException"/> if this
    /// would change the active deployment's served_model_name and
    /// <paramref name="acknowledgeServedNameChange"/> isn't set.</summary>
    Task<SwapResult> SwapAsync(
        int instanceId, int modelId, int recipeId, bool acknowledgeServedNameChange = false, CancellationToken ct = default);

    /// <summary>Replays the PREVIOUS deployment's own args_snapshot
    /// byte-for-byte -- never the live recipe, which may have changed since.</summary>
    Task<SwapResult> RollbackAsync(int instanceId, CancellationToken ct = default);

    /// <summary>Leaves the instance idle (no pending/active row); stop_reason="manual".</summary>
    Task StopAsync(int instanceId, CancellationToken ct = default);

    /// <summary>Cancels an in-flight swap; stop_reason="cancelled" on the
    /// cancelled pending row, and also on the previous-active row if the
    /// supervisor reports it already went idle (phase 2 -- nothing left
    /// running, so the DB must not keep showing a stale "active" row).</summary>
    Task<SwapResult> CancelAsync(int instanceId, CancellationToken ct = default);

    /// <summary>Stop + start the current argv, no config change -- same
    /// tracked pipeline as a swap.</summary>
    Task<SwapResult> RestartAsync(int instanceId, CancellationToken ct = default);

    /// <summary>P12's "Fix" reconcile action: forces a redeploy of the
    /// instance's current DB-recorded active deployment's own args_snapshot,
    /// bypassing <see cref="SwapAsync"/>'s "already active" short-circuit --
    /// which, by definition, would wrongly no-op here, since the whole point
    /// of "Fix" is that the DB's own bookkeeping disagrees with what the
    /// supervisor is actually running even though the DB thinks this exact
    /// (model, recipe) is already active. Also skips the served-name
    /// acknowledgement gate -- this is an admin correction, not a live
    /// user-initiated model change.</summary>
    Task<SwapResult> ForceRedeployAsync(int instanceId, CancellationToken ct = default);
}
