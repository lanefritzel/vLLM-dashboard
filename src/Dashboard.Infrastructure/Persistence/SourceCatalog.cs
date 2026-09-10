using Dashboard.Core.Abstractions;
using Dashboard.Core.Models;
using Dashboard.Infrastructure.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Dashboard.Infrastructure.Persistence;

/// <summary>EF-backed source CRUD. Raises ConfigStore.Changed after writes so the
/// polling service re-binds timers. Singleton; opens a fresh AppDbContext per
/// operation via IServiceScopeFactory so concurrent calls from different
/// components on the same circuit never share a context instance.</summary>
public sealed class SourceCatalog : ISourceCatalog
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IConfigStore _config;

    public SourceCatalog(IServiceScopeFactory scopeFactory, IConfigStore config)
    {
        _scopeFactory = scopeFactory;
        _config = config;
    }

    public async Task<IReadOnlyList<SourceInfo>> GetAllAsync(CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var list = await db.Sources.AsNoTracking().OrderBy(s => s.SortOrder).ThenBy(s => s.Label).ToListAsync(ct);
        return list.Select(s => s.ToInfo()).ToList();
    }

    public async Task<IReadOnlyList<SourceInfo>> GetActiveAsync(CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var list = await db.Sources.AsNoTracking().Where(s => s.Active).OrderBy(s => s.SortOrder).ToListAsync(ct);
        return list.Select(s => s.ToInfo()).ToList();
    }

    public async Task<SourceInfo?> GetByIdAsync(string id, CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var s = await db.Sources.AsNoTracking().FirstOrDefaultAsync(x => x.Id == Guid.Parse(id), ct);
        return s?.ToInfo();
    }

    public async Task<SourceInfo?> GetByIdOrSlugAsync(string idOrSlug, CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var s = Guid.TryParse(idOrSlug, out var guid)
            ? await db.Sources.AsNoTracking().FirstOrDefaultAsync(x => x.Id == guid, ct)
            : await db.Sources.AsNoTracking().FirstOrDefaultAsync(x => x.Slug == idOrSlug.Trim().ToLowerInvariant(), ct);
        return s?.ToInfo();
    }

    public async Task<SourceInfo> CreateAsync(SourceInfo source, CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var baseSlug = Core.Models.Slug.From(string.IsNullOrWhiteSpace(source.Slug) ? source.Label : source.Slug);
        var entity = new Source
        {
            Slug = await EnsureUniqueSlugAsync(db, baseSlug, excludeId: null, ct),
            Label = source.Label,
            Type = source.Type.ToStorage(),
            MetricsUrl = source.MetricsUrl,
            InferenceUrl = source.InferenceUrl,
            DockerHost = source.DockerHost,
            ContainerName = source.ContainerName,
            ScrapeIntervalSeconds = source.ScrapeIntervalSeconds,
            Active = source.Active,
            SortOrder = source.SortOrder,
        };
        db.Sources.Add(entity);
        await db.SaveChangesAsync(ct);
        _config.RaiseChanged();
        return entity.ToInfo();
    }

    public async Task<SourceInfo> UpdateAsync(SourceInfo source, CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var entity = await db.Sources.SingleAsync(x => x.Id == Guid.Parse(source.Id), ct);

        var baseSlug = Core.Models.Slug.From(string.IsNullOrWhiteSpace(source.Slug) ? source.Label : source.Slug);
        if (!string.Equals(baseSlug, entity.Slug, StringComparison.Ordinal))
            entity.Slug = await EnsureUniqueSlugAsync(db, baseSlug, excludeId: entity.Id, ct);

        entity.Label = source.Label;
        entity.Type = source.Type.ToStorage();
        entity.MetricsUrl = source.MetricsUrl;
        entity.InferenceUrl = source.InferenceUrl;
        entity.DockerHost = source.DockerHost;
        entity.ContainerName = source.ContainerName;
        entity.ScrapeIntervalSeconds = source.ScrapeIntervalSeconds;
        entity.Active = source.Active;
        entity.SortOrder = source.SortOrder;
        entity.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        _config.RaiseChanged();
        return entity.ToInfo();
    }

    /// <summary>Appends "-2", "-3", … until the slug doesn't collide with another
    /// source. Runs inside the caller's already-open scope/context.</summary>
    private static async Task<string> EnsureUniqueSlugAsync(AppDbContext db, string baseSlug, Guid? excludeId, CancellationToken ct)
    {
        var candidate = baseSlug;
        var suffix = 2;
        while (await db.Sources.AnyAsync(s => s.Slug == candidate && (excludeId == null || s.Id != excludeId), ct))
        {
            candidate = $"{baseSlug}-{suffix}";
            suffix++;
        }
        return candidate;
    }

    public async Task DeleteAsync(string id, CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var entity = await db.Sources.FirstOrDefaultAsync(x => x.Id == Guid.Parse(id), ct);
        if (entity is null) return;
        db.Sources.Remove(entity);
        await db.SaveChangesAsync(ct);
        _config.RaiseChanged();
    }
}
