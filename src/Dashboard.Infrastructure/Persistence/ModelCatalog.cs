using Dashboard.Core.Abstractions;
using Dashboard.Core.Models;
using Dashboard.Infrastructure.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Dashboard.Infrastructure.Persistence;

/// <summary>EF-backed per-source model registry with the single-active invariant.
/// Singleton; opens a fresh AppDbContext per operation via IServiceScopeFactory so
/// concurrent calls from different components on the same circuit never share a
/// context instance.</summary>
public sealed class ModelCatalog : IModelCatalog
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IConfigStore _config;

    public ModelCatalog(IServiceScopeFactory scopeFactory, IConfigStore config)
    {
        _scopeFactory = scopeFactory;
        _config = config;
    }

    public async Task<IReadOnlyList<ModelInfo>> GetBySourceAsync(string sourceId, CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var list = await db.Models.AsNoTracking()
            .Where(m => m.SourceId == Guid.Parse(sourceId))
            .OrderBy(m => m.SortOrder).ThenBy(m => m.Name)
            .ToListAsync(ct);
        return list.Select(m => m.ToInfo()).ToList();
    }

    public async Task<ModelInfo?> GetActiveAsync(string sourceId, CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var m = await db.Models.AsNoTracking()
            .FirstOrDefaultAsync(x => x.SourceId == Guid.Parse(sourceId) && x.IsActive, ct);
        return m?.ToInfo();
    }

    public async Task<ModelInfo> CreateAsync(ModelInfo model, CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var anyActive = await db.Models.AnyAsync(x => x.SourceId == Guid.Parse(model.SourceId) && x.IsActive, ct);
        var entity = new Model
        {
            SourceId = Guid.Parse(model.SourceId),
            Name = model.Name,
            DisplayName = model.DisplayName,
            SwapArgs = model.SwapArgs,
            IsActive = model.IsActive || !anyActive,
            SortOrder = model.SortOrder,
        };
        if (entity.IsActive)
            await DeactivateOthersAsync(db, entity.SourceId, ct);
        db.Models.Add(entity);
        await db.SaveChangesAsync(ct);
        _config.RaiseChanged();
        return entity.ToInfo();
    }

    public async Task<ModelInfo> UpdateAsync(ModelInfo model, CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var entity = await db.Models.SingleAsync(x => x.Id == Guid.Parse(model.Id), ct);
        entity.Name = model.Name;
        entity.DisplayName = model.DisplayName;
        entity.SwapArgs = model.SwapArgs;
        entity.SortOrder = model.SortOrder;
        if (model.IsActive)
        {
            await DeactivateOthersAsync(db, entity.SourceId, ct);
            entity.IsActive = true;
        }
        await db.SaveChangesAsync(ct);
        _config.RaiseChanged();
        return entity.ToInfo();
    }

    public async Task DeleteAsync(string modelId, CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var entity = await db.Models.FirstOrDefaultAsync(x => x.Id == Guid.Parse(modelId), ct);
        if (entity is null) return;
        db.Models.Remove(entity);
        await db.SaveChangesAsync(ct);
        _config.RaiseChanged();
    }

    public async Task ActivateAsync(string modelId, CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var entity = await db.Models.SingleAsync(x => x.Id == Guid.Parse(modelId), ct);
        await DeactivateOthersAsync(db, entity.SourceId, ct);
        entity.IsActive = true;
        await db.SaveChangesAsync(ct);
        _config.RaiseChanged();
    }

    private static Task DeactivateOthersAsync(AppDbContext db, Guid sourceId, CancellationToken ct)
    {
        // Single atomic SQL update; keeps the operation async-only and independent of
        // any pending entity in the change tracker.
        return db.Database.ExecuteSqlRawAsync(
            "UPDATE \"Models\" SET \"IsActive\" = false WHERE \"SourceId\" = {0} AND \"IsActive\" = true",
            new object[] { sourceId }, ct);
    }
}
