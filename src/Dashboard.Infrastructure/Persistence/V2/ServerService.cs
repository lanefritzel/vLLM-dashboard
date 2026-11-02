using Dashboard.Core.Abstractions;
using Dashboard.Infrastructure.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Dashboard.Infrastructure.Persistence.V2;

public interface IServerService
{
    Task<IReadOnlyList<Server>> ListAsync(CancellationToken ct = default);
    Task<Server?> GetAsync(int id, CancellationToken ct = default);
    Task<Server> CreateAsync(Server server, CancellationToken ct = default);
    Task<Server> UpdateAsync(Server server, CancellationToken ct = default);
    Task DeleteAsync(int id, CancellationToken ct = default);
}

/// <summary>CRUD for the new-schema <see cref="Server"/> table (P09b). A blank
/// label is rejected at save time -- the same "first qwen incident" guardrail
/// family as the blank model path below, just for this entity's own identifying
/// field.</summary>
public sealed class ServerService : IServerService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IReadOnlyGuard _readOnlyGuard;

    public ServerService(IServiceScopeFactory scopeFactory, IReadOnlyGuard readOnlyGuard)
    {
        _scopeFactory = scopeFactory;
        _readOnlyGuard = readOnlyGuard;
    }

    private static void ValidateForSave(Server server)
    {
        if (string.IsNullOrWhiteSpace(server.Label))
            throw new ArgumentException("A server must have a label.");
        if (string.IsNullOrWhiteSpace(server.ModelsDir))
            throw new ArgumentException("A server must have a models directory.");
    }

    public async Task<IReadOnlyList<Server>> ListAsync(CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.ServersV2.OrderBy(s => s.Label).ToListAsync(ct);
    }

    public async Task<Server?> GetAsync(int id, CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.ServersV2.FindAsync([id], ct);
    }

    public async Task<Server> CreateAsync(Server server, CancellationToken ct = default)
    {
        _readOnlyGuard.EnsureWritable();
        ValidateForSave(server);
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.ServersV2.Add(server);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (PostgresErrors.IsUniqueViolation(ex))
        {
            throw new InvalidOperationException($"A server named '{server.Label}' already exists.", ex);
        }
        return server;
    }

    public async Task<Server> UpdateAsync(Server server, CancellationToken ct = default)
    {
        _readOnlyGuard.EnsureWritable();
        ValidateForSave(server);
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.ServersV2.Update(server);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (PostgresErrors.IsUniqueViolation(ex))
        {
            throw new InvalidOperationException($"A server named '{server.Label}' already exists.", ex);
        }
        return server;
    }

    public async Task DeleteAsync(int id, CancellationToken ct = default)
    {
        _readOnlyGuard.EnsureWritable();
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var server = await db.ServersV2.FindAsync([id], ct);
        if (server is null)
            return;
        db.ServersV2.Remove(server);
        await db.SaveChangesAsync(ct);
    }
}
