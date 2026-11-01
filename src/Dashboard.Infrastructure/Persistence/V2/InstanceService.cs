using System.Text.RegularExpressions;
using Dashboard.Infrastructure.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Dashboard.Infrastructure.Persistence.V2;

public interface IInstanceService
{
    Task<IReadOnlyList<Instance>> ListByServerAsync(int serverId, CancellationToken ct = default);
    Task<Instance?> GetAsync(int id, CancellationToken ct = default);
    Task<Instance> CreateAsync(Instance instance, CancellationToken ct = default);

    /// <summary>Editing an existing instance's proxy slug is allowed, but it
    /// changes client-facing URLs -- <paramref name="slugChanged"/> tells the
    /// caller (the UI) whether to show that warning; this method itself still
    /// just saves (doc 5/P09b: a warning, not a block).</summary>
    Task<(Instance Instance, bool SlugChanged)> UpdateAsync(Instance instance, CancellationToken ct = default);

    Task DeleteAsync(int id, CancellationToken ct = default);
}

/// <summary>CRUD for the new-schema <see cref="Instance"/> table (P09b).</summary>
public sealed class InstanceService : IInstanceService
{
    // URL-safe: lowercase letters/digits/hyphens, not starting or ending with a
    // hyphen -- the same shape a route segment in /proxy/<slug>/v1 needs to be.
    private static readonly Regex SlugPattern = new(@"^[a-z0-9]+(-[a-z0-9]+)*$", RegexOptions.Compiled);

    private readonly IServiceScopeFactory _scopeFactory;

    public InstanceService(IServiceScopeFactory scopeFactory) => _scopeFactory = scopeFactory;

    private static void ValidateForSave(Instance instance)
    {
        if (string.IsNullOrWhiteSpace(instance.Label))
            throw new ArgumentException("An instance must have a label.");
        if (string.IsNullOrWhiteSpace(instance.ProxySlug) || !SlugPattern.IsMatch(instance.ProxySlug))
        {
            throw new ArgumentException(
                $"An instance's proxy slug ('{instance.ProxySlug}') must be URL-safe: lowercase letters, digits and hyphens only, not starting or ending with a hyphen.");
        }
    }

    public async Task<IReadOnlyList<Instance>> ListByServerAsync(int serverId, CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.InstancesV2.Where(i => i.ServerId == serverId).OrderBy(i => i.Label).ToListAsync(ct);
    }

    public async Task<Instance?> GetAsync(int id, CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.InstancesV2.FindAsync([id], ct);
    }

    public async Task<Instance> CreateAsync(Instance instance, CancellationToken ct = default)
    {
        ValidateForSave(instance);
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.InstancesV2.Add(instance);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (PostgresErrors.IsUniqueViolation(ex))
        {
            throw new InvalidOperationException($"The proxy slug '{instance.ProxySlug}' is already in use.", ex);
        }
        return instance;
    }

    public async Task<(Instance Instance, bool SlugChanged)> UpdateAsync(Instance instance, CancellationToken ct = default)
    {
        ValidateForSave(instance);
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var existingSlug = await db.InstancesV2
            .Where(i => i.Id == instance.Id)
            .Select(i => i.ProxySlug)
            .FirstOrDefaultAsync(ct);
        var slugChanged = existingSlug is not null && !string.Equals(existingSlug, instance.ProxySlug, StringComparison.Ordinal);

        db.InstancesV2.Update(instance);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (PostgresErrors.IsUniqueViolation(ex))
        {
            throw new InvalidOperationException($"The proxy slug '{instance.ProxySlug}' is already in use.", ex);
        }
        return (instance, slugChanged);
    }

    public async Task DeleteAsync(int id, CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var instance = await db.InstancesV2.FindAsync([id], ct);
        if (instance is null)
            return;
        db.InstancesV2.Remove(instance);
        await db.SaveChangesAsync(ct);
    }
}
