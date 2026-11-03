using Dashboard.Core.Abstractions;
using Dashboard.Infrastructure.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Dashboard.Infrastructure.Persistence.V2;

public sealed class OnboardingService : IOnboardingService
{
    private readonly IServiceScopeFactory _scopeFactory;

    public OnboardingService(IServiceScopeFactory scopeFactory) => _scopeFactory = scopeFactory;

    public async Task<OnboardingStatus> GetStatusAsync(CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var hasDeployment = await db.DeploymentsV2.AnyAsync(ct);
        // Any row in the OLD schema's own Sources table means this database was
        // ever a pre-V2 install (P01b's conversion runs against exactly this
        // table) -- "an upgraded DB with converted rows skips it", regardless
        // of whether a deployment has happened yet on the converted instance.
        var everHadLegacySources = await db.Sources.AnyAsync(ct);

        return new OnboardingStatus
        {
            ShouldShow = !hasDeployment && !everHadLegacySources,
            HasServerAndInstance = await db.ServersV2.AnyAsync(ct) && await db.InstancesV2.AnyAsync(ct),
            HasModel = await db.ModelsV2.AnyAsync(ct),
            HasRecipe = await db.RecipesV2.AnyAsync(ct),
            HasDeployment = hasDeployment,
        };
    }
}
