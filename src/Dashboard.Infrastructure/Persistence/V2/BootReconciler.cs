using System.Text.Json;
using System.Linq;
using Dashboard.Core.Abstractions;
using Dashboard.Core.Supervisor;

namespace Dashboard.Infrastructure.Persistence.V2;

/// <summary>P12's reconciler: compares the supervisor's own <c>GET
/// /admin/status</c> against Postgres for every instance, never correcting
/// either side on its own -- just reports what it sees. The actual
/// correction is always an explicit operator action (Adopt/Fix/Mark-stopped),
/// never automatic.</summary>
public sealed class BootReconciler : IBootReconciler, IReconcileActions
{
    private readonly IServerService _servers;
    private readonly IInstanceService _instances;
    private readonly IDeploymentService _deployments;
    private readonly ISupervisorClient _supervisorClient;
    private readonly ISupervisorTokenProvider _tokenProvider;

    public BootReconciler(
        IServerService servers, IInstanceService instances, IDeploymentService deployments,
        ISupervisorClient supervisorClient, ISupervisorTokenProvider tokenProvider)
    {
        _servers = servers;
        _instances = instances;
        _deployments = deployments;
        _supervisorClient = supervisorClient;
        _tokenProvider = tokenProvider;
    }

    public async Task<IReadOnlyList<ReconcileState>> ReconcileAllAsync(CancellationToken ct = default)
    {
        // IInstanceService is always server-scoped (P09b) -- there's no
        // single "every instance" query, so this walks IServerService's own
        // list first. Single-row MVP today (doc 4), but this stays correct
        // once there's more than one server.
        var servers = await _servers.ListAsync(ct);
        var states = new List<ReconcileState>();
        foreach (var server in servers)
        {
            var instances = await _instances.ListByServerAsync(server.Id, ct);
            foreach (var instance in instances)
                states.Add(await ReconcileInstanceAsync(instance.Id, ct));
        }
        return states;
    }

    public async Task<ReconcileState> ReconcileInstanceAsync(int instanceId, CancellationToken ct = default)
    {
        var instance = await _instances.GetAsync(instanceId, ct)
            ?? throw new KeyNotFoundException($"No instance with id {instanceId}.");

        if (string.IsNullOrWhiteSpace(instance.AdminUrl))
        {
            return new ReconcileState
            {
                InstanceId = instanceId,
                Status = ReconcileStatus.NeedsRegistration,
                Message = "This instance has no supervisor registered yet.",
            };
        }

        var token = _tokenProvider.GetToken(instanceId);
        var dbActive = await _deployments.GetActiveAsync(instanceId, ct);

        SupervisorStatus status;
        try
        {
            if (token is null)
                throw new InvalidOperationException($"No supervisor token is configured for instance {instanceId}.");
            status = await _supervisorClient.GetStatusAsync(instance.AdminUrl, token, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new ReconcileState
            {
                InstanceId = instanceId,
                Status = ReconcileStatus.Down,
                DbActiveDeploymentId = dbActive?.Id,
                Message = $"Supervisor unreachable: {ex.Message}",
            };
        }

        var supervisorServing = string.Equals(status.State, "ready", StringComparison.Ordinal) && status.VllmHealthy;

        if (!supervisorServing)
        {
            return new ReconcileState
            {
                InstanceId = instanceId,
                Status = dbActive is not null ? ReconcileStatus.IdleVsActive : ReconcileStatus.Ok,
                SupervisorState = status.State,
                DbActiveDeploymentId = dbActive?.Id,
                Message = dbActive is not null
                    ? $"The supervisor is idle, but deployment {dbActive.Id} is still recorded as active."
                    : null,
            };
        }

        // current_deployment_id is cleared the instant a deploy concludes
        // (deploy_runner.py) -- it's never populated in the "ready" steady
        // state this reconciler actually runs against, so the only real
        // signal for "what's running" once past the deploy phase is the
        // child process's own argv. Compare that against the DB's active
        // row's own frozen args_snapshot -- not just "is something active",
        // since a wrong active row with a DIFFERENT argv must still surface
        // as a mismatch.
        var supervisorArgv = status.Child?.Argv;
        var dbArgv = dbActive is not null ? TryDeserializeArgv(dbActive.ArgsSnapshot) : null;
        var argvMatches = dbArgv is not null && supervisorArgv is not null && dbArgv.SequenceEqual(supervisorArgv);

        if (dbActive is not null && argvMatches)
        {
            return new ReconcileState
            {
                InstanceId = instanceId,
                Status = ReconcileStatus.Ok,
                SupervisorState = status.State,
                SupervisorArgv = supervisorArgv,
                DbActiveDeploymentId = dbActive.Id,
            };
        }

        return new ReconcileState
        {
            InstanceId = instanceId,
            Status = ReconcileStatus.Mismatch,
            SupervisorState = status.State,
            SupervisorArgv = supervisorArgv,
            DbActiveDeploymentId = dbActive?.Id,
            Message = dbActive is null
                ? "The supervisor is serving a model this instance has no active deployment recorded for."
                : $"The supervisor's running argv doesn't match the DB's active deployment {dbActive.Id}.",
        };
    }

    private static List<string>? TryDeserializeArgv(string argsSnapshot)
    {
        try
        {
            return JsonSerializer.Deserialize<List<string>>(argsSnapshot);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public async Task AdoptAsync(int instanceId, int modelId, int recipeId, IReadOnlyList<string> actualArgv, CancellationToken ct = default)
    {
        if (actualArgv.Count == 0)
            throw new ArgumentException("Adopt needs the supervisor's own actual argv -- none was given.");

        var now = DateTime.UtcNow;

        // Stop whatever the DB currently (wrongly) has as pending/active on
        // this instance FIRST -- the partial unique index on (instance_id)
        // WHERE status IN ('pending','active') means the new "active" row
        // below can't be inserted until the old ones are cleared.
        var active = await _deployments.GetActiveAsync(instanceId, ct);
        if (active is not null)
            await _deployments.TransitionAsync(active.Id, status: "stopped", stopReason: "manual", stoppedAt: now, ct: ct);
        var pending = await _deployments.GetPendingAsync(instanceId, ct);
        if (pending is not null)
            await _deployments.TransitionAsync(pending.Id, status: "stopped", stopReason: "manual", stoppedAt: now, ct: ct);

        var adopted = await _deployments.CreateAsync(new Deployment
        {
            InstanceId = instanceId,
            ModelId = modelId,
            RecipeId = recipeId,
            ArgsSnapshot = JsonSerializer.Serialize(actualArgv),
            Status = "pending",
        }, ct);

        // Go straight to active -- Adopt isn't asking the supervisor to do
        // anything (it's already running), just recording what IS running.
        await _deployments.TransitionAsync(adopted.Id, status: "active", startedAt: now, ct: ct);
    }

    public async Task MarkStoppedAsync(int instanceId, CancellationToken ct = default)
    {
        var active = await _deployments.GetActiveAsync(instanceId, ct)
            ?? throw new InvalidOperationException($"Instance {instanceId} has no active deployment to mark stopped.");

        await _deployments.TransitionAsync(active.Id, status: "stopped", stopReason: "manual", stoppedAt: DateTime.UtcNow, ct: ct);
    }
}
