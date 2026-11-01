using System.Text.Json;
using Dashboard.Infrastructure.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Dashboard.Infrastructure.Persistence.V2;

public interface IDeploymentService
{
    Task<IReadOnlyList<Deployment>> ListByInstanceAsync(int instanceId, CancellationToken ct = default);
    Task<Deployment?> GetAsync(int id, CancellationToken ct = default);
    Task<Deployment?> GetActiveAsync(int instanceId, CancellationToken ct = default);
    Task<Deployment?> GetPendingAsync(int instanceId, CancellationToken ct = default);

    /// <summary>Inserts a new "pending" deployment with its <c>args_snapshot</c>
    /// frozen at exactly the given argv -- the partial unique index on
    /// (instance_id) WHERE status IN ('pending','active') means a second
    /// concurrent swap on the same instance surfaces here as a friendly
    /// "a swap is already in progress" error, not a raw constraint violation.</summary>
    Task<Deployment> CreateAsync(Deployment deployment, CancellationToken ct = default);

    /// <summary>Status/stop_reason/failure-field transitions only -- never touches
    /// <c>args_snapshot</c> (immutable at runtime, doc 4): a deployment row's argv
    /// is a point-in-time record of what was actually launched, and must stay that
    /// way even if the recipe or model it came from is edited afterward.</summary>
    Task<Deployment> TransitionAsync(
        int id, string status, string? stopReason = null, string? failureReason = null,
        int? exitCode = null, string? logTail = null, DateTime? startedAt = null, DateTime? stoppedAt = null,
        CancellationToken ct = default);

    /// <summary>One transaction (P10a doc 7: "on ready -> new row active,
    /// previous active row stopped ... (one transaction)"): stops whatever
    /// else is active on this deployment's instance, THEN activates this
    /// one -- in that order, since the partial unique index
    /// (one_active_per_instance) is checked per-statement, not deferred;
    /// activating first would violate it while the old row is still active.</summary>
    Task<Deployment> ActivateAndStopPreviousAsync(int deploymentId, string previousStopReason, CancellationToken ct = default);
}

/// <summary>CRUD (create + status transitions only) for the new-schema
/// <see cref="Deployment"/> table (P09b). Used by P10a/P10b to record a swap's
/// lifecycle; calling the supervisor itself is out of scope here.</summary>
public sealed class DeploymentService : IDeploymentService
{
    private readonly IServiceScopeFactory _scopeFactory;

    public DeploymentService(IServiceScopeFactory scopeFactory) => _scopeFactory = scopeFactory;

    public async Task<IReadOnlyList<Deployment>> ListByInstanceAsync(int instanceId, CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.DeploymentsV2
            .Where(d => d.InstanceId == instanceId)
            .OrderByDescending(d => d.CreatedAt)
            .ToListAsync(ct);
    }

    public async Task<Deployment?> GetAsync(int id, CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.DeploymentsV2.FindAsync([id], ct);
    }

    public async Task<Deployment?> GetActiveAsync(int instanceId, CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.DeploymentsV2.FirstOrDefaultAsync(d => d.InstanceId == instanceId && d.Status == "active", ct);
    }

    public async Task<Deployment?> GetPendingAsync(int instanceId, CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.DeploymentsV2.FirstOrDefaultAsync(d => d.InstanceId == instanceId && d.Status == "pending", ct);
    }

    public async Task<Deployment> CreateAsync(Deployment deployment, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(deployment.ArgsSnapshot) || deployment.ArgsSnapshot == "null")
            throw new ArgumentException("A deployment must have an args_snapshot.");
        // Freeze it as given -- re-serializing through JsonDocument would be a
        // no-op for well-formed JSON, but guards against a caller accidentally
        // handing us a non-JSON string, which the jsonb column would otherwise
        // reject at the DB with a much less friendly error.
        JsonDocument.Parse(deployment.ArgsSnapshot);

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.DeploymentsV2.Add(deployment);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (PostgresErrors.IsUniqueViolation(ex))
        {
            throw new InvalidOperationException("A swap is already in progress on this instance.", ex);
        }
        return deployment;
    }

    public async Task<Deployment> TransitionAsync(
        int id, string status, string? stopReason = null, string? failureReason = null,
        int? exitCode = null, string? logTail = null, DateTime? startedAt = null, DateTime? stoppedAt = null,
        CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var deployment = await db.DeploymentsV2.FindAsync([id], ct)
            ?? throw new KeyNotFoundException($"No deployment with id {id}.");

        // args_snapshot is deliberately left untouched -- immutable at runtime.
        deployment.Status = status;
        if (stopReason is not null) deployment.StopReason = stopReason;
        if (failureReason is not null) deployment.FailureReason = failureReason;
        if (exitCode is not null) deployment.ExitCode = exitCode;
        if (logTail is not null) deployment.LogTail = logTail;
        if (startedAt is not null) deployment.StartedAt = startedAt;
        if (stoppedAt is not null) deployment.StoppedAt = stoppedAt;

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (PostgresErrors.IsUniqueViolation(ex))
        {
            throw new InvalidOperationException("A swap is already in progress on this instance.", ex);
        }
        return deployment;
    }

    public async Task<Deployment> ActivateAndStopPreviousAsync(int deploymentId, string previousStopReason, CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await using var transaction = await db.Database.BeginTransactionAsync(ct);

        var deployment = await db.DeploymentsV2.FindAsync([deploymentId], ct)
            ?? throw new KeyNotFoundException($"No deployment with id {deploymentId}.");

        var previous = await db.DeploymentsV2
            .FirstOrDefaultAsync(d => d.InstanceId == deployment.InstanceId && d.Status == "active" && d.Id != deploymentId, ct);

        if (previous is not null)
        {
            previous.Status = "stopped";
            previous.StopReason = previousStopReason;
            previous.StoppedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct); // stop the old row first, see the interface doc comment
        }

        deployment.Status = "active";
        deployment.StartedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);

        await transaction.CommitAsync(ct);
        return deployment;
    }
}
