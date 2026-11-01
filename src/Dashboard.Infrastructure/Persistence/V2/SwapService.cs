using System.Text.Json;
using Dashboard.Core.Abstractions;
using Dashboard.Core.Recipes;
using Dashboard.Core.Supervisor;

namespace Dashboard.Infrastructure.Persistence.V2;

/// <summary>The first end-to-end swap flow (P10a, doc 7): pre-flight -> build
/// argv -> record a pending deployment -> ask the supervisor -> track to a
/// terminal state. Composed entirely out of the P09b services -- no direct
/// AppDbContext access here, since every read/write it needs (model,
/// recipe, server, instance, deployment rows) already has a service for it.
/// The dashboard asks; the supervisor does: nothing here touches the Docker
/// API, and starting/stopping a model always goes through
/// <see cref="ISupervisorClient"/>.</summary>
public sealed class SwapService : ISwapService
{
    private static readonly TimeSpan DefaultHealthTimeout = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan DefaultTrackingTimeout = TimeSpan.FromMinutes(10);

    private readonly IInstanceService _instances;
    private readonly IModelService _models;
    private readonly IRecipeService _recipes;
    private readonly IServerService _servers;
    private readonly IDeploymentService _deployments;
    private readonly ISupervisorClient _supervisorClient;
    private readonly IDeploymentTracker _tracker;
    private readonly ISupervisorTokenProvider _tokenProvider;

    public SwapService(
        IInstanceService instances, IModelService models, IRecipeService recipes, IServerService servers,
        IDeploymentService deployments, ISupervisorClient supervisorClient, IDeploymentTracker tracker,
        ISupervisorTokenProvider tokenProvider)
    {
        _instances = instances;
        _models = models;
        _recipes = recipes;
        _servers = servers;
        _deployments = deployments;
        _supervisorClient = supervisorClient;
        _tracker = tracker;
        _tokenProvider = tokenProvider;
    }

    public async Task<SwapResult> SwapAsync(
        int instanceId, int modelId, int recipeId, bool acknowledgeServedNameChange = false, CancellationToken ct = default)
    {
        var instance = await RequireInstanceAsync(instanceId, ct);
        var token = RequireToken(instanceId);
        var model = await _models.GetAsync(modelId, ct) ?? throw new KeyNotFoundException($"No model with id {modelId}.");
        var recipe = await _recipes.GetAsync(recipeId, ct) ?? throw new KeyNotFoundException($"No recipe with id {recipeId}.");
        var server = await _servers.GetAsync(recipe.ServerId, ct) ?? throw new KeyNotFoundException($"No server with id {recipe.ServerId}.");

        // 1 - pre-flight: P09a compatibility + P09b validation, full model-aware check.
        var compat = CompatibilityPass.Check(
            CoreRecipeMappers.ToCoreModel(model), CoreRecipeMappers.ToCoreRecipe(recipe), CoreRecipeMappers.ToCoreServer(server));
        if (compat.HardBlocks.Count > 0)
            throw new ArgumentException(string.Join(" ", compat.HardBlocks.Select(i => i.Message)));

        var active = await _deployments.GetActiveAsync(instanceId, ct);

        // "Same model + same recipe already active" -> not a swap, no row created.
        if (active is not null && active.ModelId == modelId && active.RecipeId == recipeId)
            return new SwapResult { Outcome = "already_active" };

        // Served-name gate: a raw API call can't bypass the UI's click-through.
        if (active is not null)
        {
            var activeRecipe = await _recipes.GetAsync(active.RecipeId, ct);
            if (activeRecipe is not null
                && !string.Equals(activeRecipe.ServedModelName, recipe.ServedModelName, StringComparison.Ordinal)
                && !acknowledgeServedNameChange)
            {
                throw new ServedNameChangeRequiresAcknowledgementException(activeRecipe.ServedModelName, recipe.ServedModelName);
            }
        }

        // 2 - build {argv, env} (P09a).
        var built = ArgBuilder.Build(
            CoreRecipeMappers.ToCoreModel(model), CoreRecipeMappers.ToCoreRecipe(recipe),
            CoreRecipeMappers.ToCoreInstance(instance), CoreRecipeMappers.ToCoreServer(server));

        // 3 - insert "pending" deployment row, args_snapshot = argv. The partial
        // unique index guarantees one in flight -- a concurrent swap attempt
        // surfaces as DeploymentService's own friendly InvalidOperationException.
        var deployment = await _deployments.CreateAsync(new Deployment
        {
            InstanceId = instanceId, ModelId = modelId, RecipeId = recipeId,
            ArgsSnapshot = JsonSerializer.Serialize(built.Argv), Status = "pending",
        }, ct);

        // 4 - ask the supervisor; 5 - track to terminal.
        return await DeployTrackAndResolveAsync(
            instance, token, deployment.Id, built.Argv, built.Env, DefaultHealthTimeout, swapStopReason: "swapped", ct);
    }

    public async Task<SwapResult> RollbackAsync(int instanceId, CancellationToken ct = default)
    {
        var instance = await RequireInstanceAsync(instanceId, ct);
        var token = RequireToken(instanceId);
        var active = await _deployments.GetActiveAsync(instanceId, ct)
            ?? throw new InvalidOperationException("Nothing to roll back on this instance -- no active deployment.");

        // "The PREVIOUS deployment" -- the one the current active row itself
        // replaced -- not the active row's own args_snapshot (that would just
        // replay what's already running). History is newest-first; the most
        // recently stopped row other than the active one is exactly that.
        var history = await _deployments.ListByInstanceAsync(instanceId, ct);
        var previous = history.FirstOrDefault(d => d.Id != active.Id && d.Status == "stopped")
            ?? throw new InvalidOperationException("No previous deployment to roll back to on this instance.");

        var argv = JsonSerializer.Deserialize<List<string>>(previous.ArgsSnapshot)
            ?? throw new InvalidOperationException($"Deployment {previous.Id}'s args_snapshot could not be read.");

        // Rollback replays the PREVIOUS deployment's own args_snapshot
        // byte-for-byte -- never the live recipe/model, which may have
        // changed since (doc 7). A rollback row still references the same
        // model/recipe ids for bookkeeping, even though what actually runs
        // is frozen in args_snapshot, not rebuilt from them.
        var deployment = await _deployments.CreateAsync(new Deployment
        {
            InstanceId = instanceId, ModelId = previous.ModelId, RecipeId = previous.RecipeId,
            ArgsSnapshot = previous.ArgsSnapshot, Status = "pending",
        }, ct);

        return await DeployTrackAndResolveAsync(
            instance, token, deployment.Id, argv, env: null, DefaultHealthTimeout, swapStopReason: "rollback", ct);
    }

    public async Task StopAsync(int instanceId, CancellationToken ct = default)
    {
        var instance = await RequireInstanceAsync(instanceId, ct);
        var token = RequireToken(instanceId);
        await _supervisorClient.StopAsync(instance.AdminUrl!, token, ct);

        var now = DateTime.UtcNow;
        var active = await _deployments.GetActiveAsync(instanceId, ct);
        if (active is not null)
            await _deployments.TransitionAsync(active.Id, status: "stopped", stopReason: "manual", stoppedAt: now, ct: ct);
        var pending = await _deployments.GetPendingAsync(instanceId, ct);
        if (pending is not null)
            await _deployments.TransitionAsync(pending.Id, status: "stopped", stopReason: "manual", stoppedAt: now, ct: ct);
    }

    public async Task<SwapResult> CancelAsync(int instanceId, CancellationToken ct = default)
    {
        var instance = await RequireInstanceAsync(instanceId, ct);
        var token = RequireToken(instanceId);
        var pending = await _deployments.GetPendingAsync(instanceId, ct)
            ?? throw new InvalidOperationException("Nothing to cancel on this instance -- no deploy in flight.");

        var result = await _supervisorClient.CancelAsync(instance.AdminUrl!, token, pending.Id.ToString(), ct);

        var now = DateTime.UtcNow;
        await _deployments.TransitionAsync(pending.Id, status: "stopped", stopReason: "cancelled", stoppedAt: now, ct: ct);

        // Phase 2 (supervisor reports "idle"): nothing is left running at
        // all, including the model this instance had before the swap --
        // the previous-active row must not keep showing "active" in the DB
        // once the instance is verifiably idle (owner-confirmed 2026-11-01).
        // Phase 1 ("ready"): the old model was never touched, so its row
        // legitimately stays active -- nothing to do here.
        if (string.Equals(result.State, "idle", StringComparison.Ordinal))
        {
            var active = await _deployments.GetActiveAsync(instanceId, ct);
            if (active is not null)
                await _deployments.TransitionAsync(active.Id, status: "stopped", stopReason: "cancelled", stoppedAt: now, ct: ct);
        }

        return new SwapResult { Outcome = "cancelled", DeploymentId = pending.Id };
    }

    public async Task<SwapResult> RestartAsync(int instanceId, CancellationToken ct = default)
    {
        var instance = await RequireInstanceAsync(instanceId, ct);
        var token = RequireToken(instanceId);
        var active = await _deployments.GetActiveAsync(instanceId, ct)
            ?? throw new InvalidOperationException("Nothing to restart on this instance -- no active deployment.");

        var argv = JsonSerializer.Deserialize<List<string>>(active.ArgsSnapshot)
            ?? throw new InvalidOperationException($"Deployment {active.Id}'s args_snapshot could not be read.");

        var deployment = await _deployments.CreateAsync(new Deployment
        {
            InstanceId = instanceId, ModelId = active.ModelId, RecipeId = active.RecipeId,
            ArgsSnapshot = active.ArgsSnapshot, Status = "pending",
        }, ct);

        try
        {
            await _supervisorClient.RestartAsync(instance.AdminUrl!, token, deployment.Id.ToString(), ct);
        }
        catch (Exception)
        {
            await _deployments.TransitionAsync(deployment.Id, status: "failed", ct: ct);
            throw;
        }

        var tracked = await _tracker.TrackToTerminalAsync(
            instance.AdminUrl!, token, deployment.Id.ToString(), DefaultTrackingTimeout, ct);
        return await ResolveTrackedResultAsync(deployment.Id, tracked, swapStopReason: "swapped", ct);
    }

    /// <summary>Steps 4-5 shared by Swap and Rollback: POST /admin/deploy,
    /// then track to a terminal state and resolve it.</summary>
    private async Task<SwapResult> DeployTrackAndResolveAsync(
        Instance instance, string token, int deploymentId, IReadOnlyList<string> argv,
        IReadOnlyDictionary<string, string>? env, TimeSpan healthTimeout, string swapStopReason, CancellationToken ct)
    {
        try
        {
            await _supervisorClient.DeployAsync(
                instance.AdminUrl!, token, argv, env, deploymentId.ToString(), healthTimeout.TotalSeconds, smoke: null, ct: ct);
        }
        catch (Exception)
        {
            await _deployments.TransitionAsync(deploymentId, status: "failed", ct: ct);
            throw;
        }

        DeploymentTrackingResult tracked;
        try
        {
            tracked = await _tracker.TrackToTerminalAsync(instance.AdminUrl!, token, deploymentId.ToString(), DefaultTrackingTimeout, ct);
        }
        catch (TimeoutException)
        {
            await _deployments.TransitionAsync(deploymentId, status: "failed", failureReason: "health_timeout", ct: ct);
            return new SwapResult { Outcome = "failed", DeploymentId = deploymentId, FailureReason = "health_timeout" };
        }

        return await ResolveTrackedResultAsync(deploymentId, tracked, swapStopReason, ct);
    }

    private async Task<SwapResult> ResolveTrackedResultAsync(
        int deploymentId, DeploymentTrackingResult tracked, string swapStopReason, CancellationToken ct)
    {
        switch (tracked.TerminalState)
        {
            case "ready":
                await _deployments.ActivateAndStopPreviousAsync(deploymentId, swapStopReason, ct);
                return new SwapResult { Outcome = "swapped", DeploymentId = deploymentId };

            case "failed":
                var reason = GetString(tracked.Detail, "reason");
                var exitCode = GetInt(tracked.Detail, "exit_code");
                var logTail = GetString(tracked.Detail, "log_tail");
                // Kept in the return value only this phase -- not persisted to
                // the deployments row's own failure columns yet (P10b).
                await _deployments.TransitionAsync(deploymentId, status: "failed", ct: ct);
                return new SwapResult
                {
                    Outcome = "failed", DeploymentId = deploymentId,
                    FailureReason = reason, ExitCode = exitCode, LogTail = logTail,
                };

            case "cancelled":
                await _deployments.TransitionAsync(deploymentId, status: "stopped", stopReason: "cancelled", stoppedAt: DateTime.UtcNow, ct: ct);
                return new SwapResult { Outcome = "cancelled", DeploymentId = deploymentId };

            default:
                throw new InvalidOperationException($"Unexpected terminal state '{tracked.TerminalState}' for deployment {deploymentId}.");
        }
    }

    private async Task<Instance> RequireInstanceAsync(int instanceId, CancellationToken ct)
    {
        var instance = await _instances.GetAsync(instanceId, ct) ?? throw new KeyNotFoundException($"No instance with id {instanceId}.");
        if (string.IsNullOrWhiteSpace(instance.AdminUrl))
            throw new SupervisorNotRegisteredException(instanceId);
        return instance;
    }

    private string RequireToken(int instanceId) =>
        _tokenProvider.GetToken(instanceId)
            ?? throw new InvalidOperationException($"No supervisor token is configured for instance {instanceId}.");

    private static string? GetString(Dictionary<string, object?>? detail, string key)
    {
        if (detail is null || !detail.TryGetValue(key, out var value) || value is null)
            return null;
        return value switch
        {
            JsonElement { ValueKind: JsonValueKind.String } je => je.GetString(),
            JsonElement je => je.ToString(),
            _ => value.ToString(),
        };
    }

    private static int? GetInt(Dictionary<string, object?>? detail, string key)
    {
        if (detail is null || !detail.TryGetValue(key, out var value) || value is null)
            return null;
        return value is JsonElement { ValueKind: JsonValueKind.Number } je && je.TryGetInt32(out var i) ? i : null;
    }
}
