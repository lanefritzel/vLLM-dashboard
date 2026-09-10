using Dashboard.Core.Abstractions;
using Dashboard.Core.Models;
using Dashboard.Infrastructure.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Dashboard.Infrastructure.Persistence;

/// <summary>EF-backed launch spec CRUD (drives Docker hot-swap). Singleton; opens a
/// fresh AppDbContext per operation via IServiceScopeFactory so concurrent calls
/// from different components on the same circuit never share a context instance.</summary>
public sealed class LaunchSpecStore : ILaunchSpecStore
{
    private readonly IServiceScopeFactory _scopeFactory;

    public LaunchSpecStore(IServiceScopeFactory scopeFactory)
    {
        _scopeFactory = scopeFactory;
    }

    public async Task<LaunchSpec?> GetBySourceAsync(string sourceId, CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var entity = await db.LaunchSpecs.AsNoTracking()
            .FirstOrDefaultAsync(x => x.SourceId == Guid.Parse(sourceId), ct);
        return entity?.ToInfo();
    }

    public async Task<LaunchSpec> SaveAsync(LaunchSpec spec, CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        if (Guid.TryParse(spec.Id, out var id) &&
            await db.LaunchSpecs.AnyAsync(x => x.Id == id, ct))
        {
            var existing = await db.LaunchSpecs.SingleAsync(x => x.Id == id, ct);
            Apply(existing, spec);
            await db.SaveChangesAsync(ct);
            return existing.ToInfo();
        }

        var entity = new LaunchSpecEntity
        {
            Id = Guid.TryParse(spec.Id, out var newId) && newId != Guid.Empty ? newId : Guid.NewGuid(),
        };
        Apply(entity, spec);
        db.LaunchSpecs.Add(entity);
        await db.SaveChangesAsync(ct);
        return entity.ToInfo();
    }

    private static void Apply(LaunchSpecEntity entity, LaunchSpec spec)
    {
        entity.SourceId = Guid.Parse(spec.SourceId);
        entity.Image = spec.Image;
        entity.ModelArgTemplate = spec.ModelArgTemplate;
        entity.PortsJson = System.Text.Json.JsonSerializer.Serialize(spec.Ports);
        entity.ServingPort = spec.ServingPort;
        entity.EnvJson = System.Text.Json.JsonSerializer.Serialize(spec.Env);
        entity.VolumesJson = System.Text.Json.JsonSerializer.Serialize(spec.Volumes);
        entity.RestartPolicy = spec.RestartPolicy;
        entity.ExtraRunArgs = spec.ExtraRunArgs;
        entity.Entrypoint = spec.Entrypoint;
        entity.HfCacheHostPath = spec.HfCacheHostPath;
        entity.HfToken = spec.HfToken;
        entity.HealthWaitTimeoutSeconds = spec.HealthWaitTimeoutSeconds > 0 ? spec.HealthWaitTimeoutSeconds : 600;
        entity.GpuOptions = spec.GpuOptions;
        entity.GpuVendor = spec.GpuVendor.ToStorage();
        entity.DevicesJson = System.Text.Json.JsonSerializer.Serialize(spec.Devices);
        entity.NetworkMode = string.IsNullOrWhiteSpace(spec.NetworkMode) ? "bridge" : spec.NetworkMode;
        entity.Privileged = spec.Privileged;
        entity.IpcMode = spec.IpcMode;
        entity.ComposeProject = spec.ComposeProject;
        entity.ComposeService = spec.ComposeService;
    }
}
