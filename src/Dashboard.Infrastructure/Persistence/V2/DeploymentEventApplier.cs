using System.Collections.Concurrent;
using System.Text.Json;
using Dashboard.Core.Abstractions;
using Dashboard.Core.Supervisor;
using Dashboard.Infrastructure.Events;
using Dashboard.Infrastructure.Identity;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Dashboard.Infrastructure.Persistence.V2;

/// <summary>The "events -> rows" pipeline (P10b) -- the one place that ever
/// applies a supervisor event to deployments/deployment_events, whether it
/// arrived by push or was fetched during catch-up. Idempotent (a seq at or
/// below what's already recorded is a no-op) and serialized per deployment
/// (an in-memory semaphore per id) -- doc 5: "apply events for one
/// deployment sequentially ... never let an out-of-order ready flip rows
/// before loading events are stored."</summary>
public sealed class DeploymentEventApplier : IDeploymentEventApplier
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IDeploymentService _deployments;
    private readonly ISupervisorClient _supervisorClient;
    private readonly IHubContext<DeploymentHub> _hub;
    private readonly ConcurrentDictionary<int, SemaphoreSlim> _locks = new();

    public DeploymentEventApplier(
        IServiceScopeFactory scopeFactory, IDeploymentService deployments, ISupervisorClient supervisorClient,
        IHubContext<DeploymentHub> hub)
    {
        _scopeFactory = scopeFactory;
        _deployments = deployments;
        _supervisorClient = supervisorClient;
        _hub = hub;
    }

    private SemaphoreSlim LockFor(int deploymentId) => _locks.GetOrAdd(deploymentId, static _ => new SemaphoreSlim(1, 1));

    public async Task ApplyWithGapCatchUpAsync(
        int deploymentId, string adminUrl, SupervisorPushEvent evt, CancellationToken ct = default)
    {
        var gate = LockFor(deploymentId);
        await gate.WaitAsync(ct);
        try
        {
            var deployment = await _deployments.GetAsync(deploymentId, ct);
            if (deployment is null)
                return;

            // Gap detection (doc 5): compared against LastSeenSeq, which
            // advances for every kind, not just the "state" events stored in
            // deployment_events -- see that column's own doc comment.
            if (evt.Seq > deployment.LastSeenSeq + 1)
                await FetchAndApplyGapAsync(deploymentId, adminUrl, afterSeq: deployment.LastSeenSeq, beforeSeq: evt.Seq, ct);

            await ApplyAsync(deploymentId, evt, ct);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task CatchUpAsync(int deploymentId, string adminUrl, CancellationToken ct = default)
    {
        var gate = LockFor(deploymentId);
        await gate.WaitAsync(ct);
        try
        {
            var deployment = await _deployments.GetAsync(deploymentId, ct);
            if (deployment is null)
                return;
            await FetchAndApplyGapAsync(deploymentId, adminUrl, afterSeq: deployment.LastSeenSeq, beforeSeq: int.MaxValue, ct);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>Only "state" kind events are recoverable this way -- GET
    /// /admin/deployments/{id}/events only ever returns those (the
    /// supervisor never stores "report"/"log" pushes in its own per-deployment
    /// event list, only pushes them); those rely on the supervisor's own
    /// push-retry-until-acked semantics to eventually arrive instead.</summary>
    private async Task FetchAndApplyGapAsync(int deploymentId, string adminUrl, int afterSeq, int beforeSeq, CancellationToken ct)
    {
        var missing = await _supervisorClient.GetEventsAsync(adminUrl, deploymentId.ToString(), afterSeq, ct);
        foreach (var missed in missing.Where(e => e.Seq < beforeSeq).OrderBy(e => e.Seq))
            await ApplyStateEventAsync(deploymentId, missed.Seq, missed.State, missed.Detail, ct);
    }

    private async Task ApplyAsync(int deploymentId, SupervisorPushEvent evt, CancellationToken ct)
    {
        switch (evt.Kind)
        {
            case "state":
                await ApplyStateEventAsync(deploymentId, evt.Seq, evt.State!, evt.Detail, ct);
                break;
            case "report":
                await ApplyReportAsync(deploymentId, evt.Seq, evt.Report, ct);
                break;
            case "log":
                await ApplyLogAsync(deploymentId, evt.Seq, evt.Lines, ct);
                break;
        }
    }

    private async Task ApplyStateEventAsync(int deploymentId, int seq, string state, Dictionary<string, object?>? detail, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var deployment = await db.DeploymentsV2.FindAsync([deploymentId], ct);
        if (deployment is null || seq <= deployment.LastSeenSeq)
            return; // already applied (dedupe), or the row is gone

        var detailJson = detail is null ? null : JsonSerializer.Serialize(detail);
        db.DeploymentEventsV2.Add(new DeploymentEvent { DeploymentId = deploymentId, Seq = seq, State = state, Detail = detailJson });
        deployment.LastSeenSeq = seq;

        // A "failed"/"crashed" state event's own detail already carries the
        // same {reason, exit_code, log_tail} shape the separate "report"
        // push does (doc 5) -- applying it here too means catch-up (which
        // can only recover "state" events, see FetchAndApplyGapAsync) still
        // ends up with the failure report even if the "report" push itself
        // was never delivered.
        if ((state == "failed" || state == "crashed") && detail is not null)
        {
            deployment.FailureReason = PushDetailHelpers.GetString(detail, "reason");
            deployment.ExitCode = PushDetailHelpers.GetInt(detail, "exit_code");
            deployment.LogTail = PushDetailHelpers.GetString(detail, "log_tail");
        }

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (PostgresErrors.IsUniqueViolation(ex))
        {
            return; // raced with another path already applying this seq
        }

        await _hub.Clients.Group(DeploymentHub.GroupFor(deploymentId))
            .SendAsync(DeploymentHub.StateEvent, new { seq, state, detail }, ct);

        switch (state)
        {
            case "ready":
                // PendingStopReason is set by SwapService when the pending row
                // was first inserted -- "swapped" for a normal swap/restart,
                // "rollback" for a rollback; it's the only record of which
                // operation produced this row, now that applying its outcome
                // happens asynchronously rather than inline in SwapService.
                var stopReason = deployment.PendingStopReason ?? "swapped";
                await _deployments.ActivateAndStopPreviousAsync(deploymentId, stopReason, ct);
                break;
            case "failed":
                await _deployments.TransitionAsync(deploymentId, status: "failed", ct: ct);
                break;
            case "crashed":
                // Only ever emitted by the supervisor for a deployment that
                // already reached "ready" (P03a's post-ready watcher) -- a
                // crash before ready is reported as "failed" instead, never
                // "crashed", so no extra guard on the deployment's current
                // status is needed here.
                await _deployments.TransitionAsync(deploymentId, status: "stopped", stopReason: "crash", stoppedAt: DateTime.UtcNow, ct: ct);
                break;
            case "cancelled":
                await _deployments.TransitionAsync(deploymentId, status: "stopped", stopReason: "cancelled", stoppedAt: DateTime.UtcNow, ct: ct);
                break;
        }
    }

    private async Task ApplyReportAsync(int deploymentId, int seq, Dictionary<string, object?>? report, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var deployment = await db.DeploymentsV2.FindAsync([deploymentId], ct);
        if (deployment is null || seq <= deployment.LastSeenSeq)
            return;

        deployment.LastSeenSeq = seq;
        if (report is not null)
        {
            deployment.FailureReason = PushDetailHelpers.GetString(report, "reason");
            deployment.ExitCode = PushDetailHelpers.GetInt(report, "exit_code");
            deployment.LogTail = PushDetailHelpers.GetString(report, "log_tail");
        }
        await db.SaveChangesAsync(ct);
    }

    private async Task ApplyLogAsync(int deploymentId, int seq, List<string>? lines, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var deployment = await db.DeploymentsV2.FindAsync([deploymentId], ct);
        if (deployment is null || seq <= deployment.LastSeenSeq)
            return;

        // Never persisted (doc 5) -- broadcast only.
        deployment.LastSeenSeq = seq;
        await db.SaveChangesAsync(ct);

        if (lines is { Count: > 0 })
            await _hub.Clients.Group(DeploymentHub.GroupFor(deploymentId)).SendAsync(DeploymentHub.LogEvent, lines, ct);
    }
}
