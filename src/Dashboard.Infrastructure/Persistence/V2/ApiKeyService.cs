using System.Security.Cryptography;
using System.Text;
using Dashboard.Infrastructure.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Dashboard.Infrastructure.Persistence.V2;

public interface IApiKeyService
{
    Task<bool> AnyActiveAsync(CancellationToken ct = default);
    Task<IReadOnlyList<ApiKey>> ListAsync(CancellationToken ct = default);

    /// <summary>Returns the new row plus the raw key -- the only time the raw value
    /// ever exists outside the caller's clipboard; only its hash is persisted.</summary>
    Task<(ApiKey Record, string RawKey)> GenerateAsync(string label, string? userId = null, CancellationToken ct = default);

    /// <summary>Null for a missing/revoked key. Bumps LastUsedAt on success, so this
    /// is a write, not just a lookup -- callers on the hot request path should still
    /// treat it as cheap (one indexed lookup, one single-row update).</summary>
    Task<ApiKey?> ValidateAsync(string? rawKey, CancellationToken ct = default);
    /// <summary>Returns whether any active key remains after the revoke.</summary>
    Task<bool> RevokeAsync(int id, CancellationToken ct = default);
}

/// <summary>Credentials for the machine-to-machine inference proxy (P-API-1). Keys are
/// hashed at rest (SHA-256) the same way a password would be -- a stolen DB snapshot
/// should never hand out usable keys.</summary>
public sealed class ApiKeyService : IApiKeyService
{
    private const string KeyPrefix = "vdk_";

    private readonly IServiceScopeFactory _scopeFactory;

    public ApiKeyService(IServiceScopeFactory scopeFactory)
    {
        _scopeFactory = scopeFactory;
    }

    public async Task<bool> AnyActiveAsync(CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.ApiKeysV2.AnyAsync(k => k.RevokedAt == null, ct);
    }

    public async Task<IReadOnlyList<ApiKey>> ListAsync(CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.ApiKeysV2.AsNoTracking()
            .Where(k => k.RevokedAt == null)
            .OrderByDescending(k => k.CreatedAt)
            .ToListAsync(ct);
    }

    public async Task<(ApiKey Record, string RawKey)> GenerateAsync(string label, string? userId = null, CancellationToken ct = default)
    {
        var raw = KeyPrefix + Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');

        var record = new ApiKey
        {
            KeyHash = Hash(raw),
            Label = label,
            UserId = userId,
            CreatedAt = DateTime.UtcNow,
        };

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.ApiKeysV2.Add(record);
        await db.SaveChangesAsync(ct);
        return (record, raw);
    }

    public async Task<ApiKey?> ValidateAsync(string? rawKey, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(rawKey))
            return null;

        var hash = Hash(rawKey);
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var record = await db.ApiKeysV2.FirstOrDefaultAsync(k => k.KeyHash == hash && k.RevokedAt == null, ct);
        if (record is null)
            return null;

        record.LastUsedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        return record;
    }

    public async Task<bool> RevokeAsync(int id, CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var record = await db.ApiKeysV2.FirstOrDefaultAsync(k => k.Id == id, ct);
        if (record is not null)
        {
            record.RevokedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
        }
        return await db.ApiKeysV2.AnyAsync(k => k.RevokedAt == null, ct);
    }

    private static string Hash(string raw) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw)));
}
