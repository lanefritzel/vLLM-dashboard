using Dashboard.Core.Abstractions;
using Dashboard.Core.Recipes;
using Dashboard.Infrastructure.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Dashboard.Infrastructure.Persistence.V2;

public interface IModelService
{
    Task<IReadOnlyList<Model>> ListByServerAsync(int serverId, CancellationToken ct = default);
    Task<Model?> GetAsync(int id, CancellationToken ct = default);
    Task<Model> CreateAsync(Model model, CancellationToken ct = default);
    Task<Model> UpdateAsync(Model model, CancellationToken ct = default);

    /// <summary>Always removes the catalog row, same as before. When
    /// <paramref name="deleteFiles"/> is true, also deletes the model's weight files
    /// via the server's supervisor (never directly from this process -- see
    /// <see cref="Dashboard.Core.Abstractions.ISupervisorClient.DeleteModelFilesAsync"/>)
    /// BEFORE touching the DB row, so a file-delete failure never leaves an orphaned
    /// multi-GB directory with no catalog entry to retry from. Throws if files were
    /// requested but no reachable supervisor is registered for this model's server,
    /// or if that supervisor doesn't support the "model_delete" capability yet.</summary>
    Task DeleteAsync(int id, bool deleteFiles = false, CancellationToken ct = default);
}

/// <summary>CRUD for the new-schema <see cref="Model"/> table (P09b). Blank
/// name/path are rejected here -- the literal first qwen incident (an empty
/// --model crash-looped the container). Whether this model's path actually
/// lives under its server's models_dir is a RECIPE-save-time and
/// deploy-pre-flight check (<see cref="Dashboard.Core.Recipes.CompatibilityPass"/>),
/// not enforced here, since a bare model row can be registered (e.g. by a
/// P08 download) before any recipe ever references it.</summary>
public sealed class ModelService : IModelService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IReadOnlyGuard _readOnlyGuard;
    private readonly ISupervisorClient _supervisorClient;

    public ModelService(IServiceScopeFactory scopeFactory, IReadOnlyGuard readOnlyGuard, ISupervisorClient supervisorClient)
    {
        _scopeFactory = scopeFactory;
        _readOnlyGuard = readOnlyGuard;
        _supervisorClient = supervisorClient;
    }

    private static void ValidateForSave(Model model)
    {
        if (string.IsNullOrWhiteSpace(model.Name))
            throw new ArgumentException("A model must have a name.");
        if (string.IsNullOrWhiteSpace(model.Path))
            throw new ArgumentException("A model must have a weights path.");
    }

    public async Task<IReadOnlyList<Model>> ListByServerAsync(int serverId, CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.ModelsV2.Where(m => m.ServerId == serverId).OrderBy(m => m.Name).ToListAsync(ct);
    }

    public async Task<Model?> GetAsync(int id, CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.ModelsV2.FindAsync([id], ct);
    }

    public async Task<Model> CreateAsync(Model model, CancellationToken ct = default)
    {
        _readOnlyGuard.EnsureWritable();
        ValidateForSave(model);
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.ModelsV2.Add(model);
        await db.SaveChangesAsync(ct);
        return model;
    }

    public async Task<Model> UpdateAsync(Model model, CancellationToken ct = default)
    {
        _readOnlyGuard.EnsureWritable();
        ValidateForSave(model);
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.ModelsV2.Update(model);
        await db.SaveChangesAsync(ct);
        return model;
    }

    public async Task DeleteAsync(int id, bool deleteFiles = false, CancellationToken ct = default)
    {
        _readOnlyGuard.EnsureWritable();
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var model = await db.ModelsV2.FindAsync([id], ct);
        if (model is null)
            return;

        // Deployment history is a reporting record, not a reason to keep a model
        // around -- same call as recipes (ModelId is SetNull, not Restrict; the
        // Deployment row's own ModelName snapshot keeps the history legible).
        if (deleteFiles)
        {
            var server = await db.ServersV2.FindAsync([model.ServerId], ct)
                ?? throw new InvalidOperationException($"Model '{model.Name}'s files can't be deleted: its server no longer exists.");
            var instance = await db.InstancesV2
                .Where(i => i.ServerId == model.ServerId && i.AdminUrl != null)
                .FirstOrDefaultAsync(ct);
            if (instance?.AdminUrl is null)
                throw new InvalidOperationException(
                    $"Model '{model.Name}'s files can't be deleted: no reachable supervisor is registered for this server.");

            // Files first, DB row second: if the supervisor call fails, abort
            // entirely rather than leave an orphaned directory with no catalog
            // entry to retry the delete from.
            var containerPath = ArgBuilderCommon.ContainerModelPath(model.Path, server.ModelsDir);
            await _supervisorClient.DeleteModelFilesAsync(instance.AdminUrl, containerPath, ct);
        }

        db.ModelsV2.Remove(model);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (PostgresErrors.IsForeignKeyViolation(ex))
        {
            throw new InvalidOperationException($"Model '{model.Name}' can't be deleted: something else still refers to it.", ex);
        }
    }
}
