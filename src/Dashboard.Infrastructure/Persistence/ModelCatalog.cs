using Dashboard.Core.Abstractions;
using Dashboard.Core.Models;
using Dashboard.Infrastructure.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Dashboard.Infrastructure.Persistence;

/// <summary>EF-backed per-physical-server model registry, plus per-instance
/// active/preload state (stored on the owning Source, not the model — see
/// IModelCatalog). Singleton; opens a fresh AppDbContext per operation via
/// IServiceScopeFactory so concurrent calls from different components on the same
/// circuit never share a context instance.</summary>
public sealed class ModelCatalog : IModelCatalog
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IConfigStore _config;

    public ModelCatalog(IServiceScopeFactory scopeFactory, IConfigStore config)
    {
        _scopeFactory = scopeFactory;
        _config = config;
    }

    public async Task<IReadOnlyList<ModelInfo>> GetByPhysicalServerAsync(string physicalServerId, CancellationToken ct = default)
    {
        if (!Guid.TryParse(physicalServerId, out var serverGuid))
            return Array.Empty<ModelInfo>();
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var list = await db.Models.AsNoTracking()
            .Where(m => m.PhysicalServerId == serverGuid)
            .OrderBy(m => m.SortOrder).ThenBy(m => m.Name)
            .ToListAsync(ct);
        return list.Select(m => m.ToInfo()).ToList();
    }

    public async Task<ModelInfo> CreateAsync(ModelInfo model, CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var entity = new Model
        {
            PhysicalServerId = Guid.Parse(model.PhysicalServerId),
            Name = model.Name,
            DisplayName = model.DisplayName,
            ModelSource = model.ModelSource.ToStorage(),
            ModelPathOrRepoId = model.ModelPathOrRepoId,
            ImageOverride = model.ImageOverride,
            OptionsJson = System.Text.Json.JsonSerializer.Serialize(model.Options),
            SortOrder = model.SortOrder,
            IdleTtlSeconds = model.IdleTtlSeconds,
        };
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
        entity.PhysicalServerId = Guid.Parse(model.PhysicalServerId);
        entity.Name = model.Name;
        entity.DisplayName = model.DisplayName;
        entity.ModelSource = model.ModelSource.ToStorage();
        entity.ModelPathOrRepoId = model.ModelPathOrRepoId;
        entity.ImageOverride = model.ImageOverride;
        entity.OptionsJson = System.Text.Json.JsonSerializer.Serialize(model.Options);
        entity.SortOrder = model.SortOrder;
        entity.IdleTtlSeconds = model.IdleTtlSeconds;
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

    public async Task<ModelInfo?> GetActiveAsync(string sourceId, CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var source = await db.Sources.AsNoTracking().FirstOrDefaultAsync(s => s.Id == Guid.Parse(sourceId), ct);
        if (source?.ActiveModelId is not { } modelId) return null;
        var model = await db.Models.AsNoTracking().FirstOrDefaultAsync(m => m.Id == modelId, ct);
        return model?.ToInfo();
    }

    public async Task ActivateAsync(string sourceId, string modelId, CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var source = await db.Sources.SingleAsync(s => s.Id == Guid.Parse(sourceId), ct);
        source.ActiveModelId = Guid.Parse(modelId);
        source.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        _config.RaiseChanged();
    }

    public async Task DeactivateAsync(string sourceId, CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var source = await db.Sources.FirstOrDefaultAsync(s => s.Id == Guid.Parse(sourceId), ct);
        if (source is null || source.ActiveModelId is null) return;
        source.ActiveModelId = null;
        source.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        _config.RaiseChanged();
    }

    public async Task<ModelInfo?> GetPreloadModelAsync(string sourceId, CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var source = await db.Sources.AsNoTracking().FirstOrDefaultAsync(s => s.Id == Guid.Parse(sourceId), ct);
        if (source?.PreloadModelId is not { } modelId) return null;
        var model = await db.Models.AsNoTracking().FirstOrDefaultAsync(m => m.Id == modelId, ct);
        return model?.ToInfo();
    }

    public async Task SetPreloadModelAsync(string sourceId, string? modelId, CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var source = await db.Sources.SingleAsync(s => s.Id == Guid.Parse(sourceId), ct);
        source.PreloadModelId = string.IsNullOrWhiteSpace(modelId) ? null : Guid.Parse(modelId);
        source.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        _config.RaiseChanged();
    }
}
