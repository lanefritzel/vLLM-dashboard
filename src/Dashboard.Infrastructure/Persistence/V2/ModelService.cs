using Dashboard.Core.Abstractions;
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
    Task DeleteAsync(int id, CancellationToken ct = default);
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

    public ModelService(IServiceScopeFactory scopeFactory, IReadOnlyGuard readOnlyGuard)
    {
        _scopeFactory = scopeFactory;
        _readOnlyGuard = readOnlyGuard;
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

    public async Task DeleteAsync(int id, CancellationToken ct = default)
    {
        _readOnlyGuard.EnsureWritable();
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var model = await db.ModelsV2.FindAsync([id], ct);
        if (model is null)
            return;

        var recipes = await db.DeploymentsV2.CountAsync(d => d.ModelId == id, ct);
        if (recipes > 0)
            throw new InvalidOperationException(
                $"Model '{model.Name}' can't be deleted: it was used by {recipes} deployment{(recipes == 1 ? "" : "s")} " +
                "that are kept in the Deployments history.");

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
