using System.Security.Cryptography;
using Dashboard.Core.Abstractions;
using Dashboard.Infrastructure.Identity;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Dashboard.Infrastructure.Persistence.V2;

/// <summary>P15b: each instance's own supervisor token, read from
/// <see cref="Instance.AdminTokenEnc"/> and decrypted with ASP.NET Data
/// Protection -- replaces <see cref="ConfigSupervisorTokenProvider"/>'s single
/// config-wide token (now deleted; nothing else referenced it). The same
/// protector instance (a fixed purpose string) must be used to both encrypt
/// (Servers page, on save) and decrypt (here) -- see <see cref="Purpose"/>.</summary>
public sealed class InstanceTokenProvider : ISupervisorTokenProvider
{
    /// <summary>Shared with the Servers page's own encrypt-on-save code --
    /// changing this string would make every already-saved token undecryptable.</summary>
    public const string Purpose = "Dashboard.Instance.AdminToken.v1";

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IDataProtector _protector;

    public InstanceTokenProvider(IServiceScopeFactory scopeFactory, IDataProtectionProvider dataProtection)
    {
        _scopeFactory = scopeFactory;
        _protector = dataProtection.CreateProtector(Purpose);
    }

    public async Task<string?> GetTokenAsync(int instanceId, CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var enc = await db.InstancesV2
            .Where(i => i.Id == instanceId)
            .Select(i => i.AdminTokenEnc)
            .FirstOrDefaultAsync(ct);
        if (string.IsNullOrEmpty(enc))
            return null;

        try
        {
            return _protector.Unprotect(enc);
        }
        catch (CryptographicException)
        {
            // Keys rotated/lost, or the stored value predates this protector --
            // treat exactly like "no token configured," never throw out of a
            // read path this many callers depend on.
            return null;
        }
    }
}
