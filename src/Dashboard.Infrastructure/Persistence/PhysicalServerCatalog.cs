using Dashboard.Core.Abstractions;
using Dashboard.Core.Models;
using Dashboard.Infrastructure.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Dashboard.Infrastructure.Persistence;

/// <summary>EF-backed physical server CRUD. Raises ConfigStore.Changed after writes so
/// dependent views (e.g. a source's linked server) re-read fresh data. Singleton;
/// opens a fresh AppDbContext per operation via IServiceScopeFactory — same pattern as
/// <see cref="SourceCatalog"/>.</summary>
public sealed class PhysicalServerCatalog : IPhysicalServerCatalog
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IConfigStore _config;

    public PhysicalServerCatalog(IServiceScopeFactory scopeFactory, IConfigStore config)
    {
        _scopeFactory = scopeFactory;
        _config = config;
    }

    public async Task<IReadOnlyList<PhysicalServerInfo>> GetAllAsync(CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var list = await db.PhysicalServers.AsNoTracking().OrderBy(s => s.Label).ToListAsync(ct);
        return list.Select(s => s.ToInfo()).ToList();
    }

    public async Task<PhysicalServerInfo?> GetByIdAsync(string id, CancellationToken ct = default)
    {
        if (!Guid.TryParse(id, out var guid)) return null;
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var s = await db.PhysicalServers.AsNoTracking().FirstOrDefaultAsync(x => x.Id == guid, ct);
        return s?.ToInfo();
    }

    public async Task<PhysicalServerInfo> CreateAsync(PhysicalServerInfo server, CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var entity = new PhysicalServer
        {
            Label = server.Label,
            DockerHost = server.DockerHost,
            GpuVendor = server.GpuVendor.ToStorage(),
            GpuVramTotalGb = server.GpuVramTotalGb,
            HfCacheHostPath = server.HfCacheHostPath,
            HfToken = server.HfToken,
        };
        db.PhysicalServers.Add(entity);
        await db.SaveChangesAsync(ct);
        _config.RaiseChanged();
        return entity.ToInfo();
    }

    public async Task<PhysicalServerInfo> UpdateAsync(PhysicalServerInfo server, CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var entity = await db.PhysicalServers.SingleAsync(x => x.Id == Guid.Parse(server.Id), ct);

        entity.Label = server.Label;
        entity.DockerHost = server.DockerHost;
        entity.GpuVendor = server.GpuVendor.ToStorage();
        entity.GpuVramTotalGb = server.GpuVramTotalGb;
        entity.HfCacheHostPath = server.HfCacheHostPath;
        entity.HfToken = server.HfToken;
        entity.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        _config.RaiseChanged();
        return entity.ToInfo();
    }

    public async Task DeleteAsync(string id, CancellationToken ct = default)
    {
        if (!Guid.TryParse(id, out var guid)) return;
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var entity = await db.PhysicalServers.FirstOrDefaultAsync(x => x.Id == guid, ct);
        if (entity is null) return;
        db.PhysicalServers.Remove(entity);
        await db.SaveChangesAsync(ct);
        _config.RaiseChanged();
    }
}
