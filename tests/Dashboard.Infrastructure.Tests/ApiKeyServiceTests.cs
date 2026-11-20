using Dashboard.Infrastructure.Identity;
using Dashboard.Infrastructure.Persistence.V2;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Dashboard.Infrastructure.Tests;

/// <summary>Credentials for the machine-to-machine inference proxy (/proxy/&lt;slug&gt;/...):
/// a generated key validates until revoked, only its hash is ever persisted, and a bad or
/// revoked key never validates.</summary>
[Collection("Postgres")]
public class ApiKeyServiceTests(PostgresFixture postgres)
{
    private async Task<IServiceScopeFactory> NewMigratedScopeFactoryAsync()
    {
        var connectionString = await postgres.CreateFreshDatabaseAsync();
        var services = new ServiceCollection();
        services.AddDbContext<AppDbContext>(o => o.UseNpgsql(connectionString));
        var provider = services.BuildServiceProvider();

        using var scope = provider.CreateScope();
        await scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.MigrateAsync();
        return provider.GetRequiredService<IServiceScopeFactory>();
    }

    [Fact]
    public async Task GenerateAsync_ThenValidateAsync_RoundTripsAndNeverStoresTheRawKey()
    {
        var service = new ApiKeyService(await NewMigratedScopeFactoryAsync());

        var (record, raw) = await service.GenerateAsync("default");
        Assert.NotEqual(raw, record.KeyHash);

        var validated = await service.ValidateAsync(raw);
        Assert.NotNull(validated);
        Assert.Equal(record.Id, validated!.Id);
        Assert.NotNull(validated.LastUsedAt);
    }

    [Fact]
    public async Task ValidateAsync_RejectsAnUnknownOrMissingKey()
    {
        var service = new ApiKeyService(await NewMigratedScopeFactoryAsync());

        Assert.Null(await service.ValidateAsync("vdk_not-a-real-key"));
        Assert.Null(await service.ValidateAsync(null));
        Assert.Null(await service.ValidateAsync(""));
    }

    [Fact]
    public async Task RevokeAsync_StopsAPreviouslyValidKeyFromValidating()
    {
        var service = new ApiKeyService(await NewMigratedScopeFactoryAsync());
        var (record, raw) = await service.GenerateAsync("default");

        await service.RevokeAsync(record.Id);

        Assert.Null(await service.ValidateAsync(raw));
    }

    [Fact]
    public async Task AnyActiveAsync_IsFalseUntilAKeyExists_AndFalseAgainOnceItsRevoked()
    {
        var service = new ApiKeyService(await NewMigratedScopeFactoryAsync());
        Assert.False(await service.AnyActiveAsync());

        var (record, _) = await service.GenerateAsync("default");
        Assert.True(await service.AnyActiveAsync());

        await service.RevokeAsync(record.Id);
        Assert.False(await service.AnyActiveAsync());
    }

    [Fact]
    public async Task RevokeAsync_ReturnsWhetherAnyActiveKeyRemains()
    {
        var service = new ApiKeyService(await NewMigratedScopeFactoryAsync());
        var (first, _) = await service.GenerateAsync("first");
        var (second, _) = await service.GenerateAsync("second");

        Assert.True(await service.RevokeAsync(first.Id));
        Assert.False(await service.RevokeAsync(second.Id));
    }

    [Fact]
    public async Task ListAsync_OmitsRevokedKeys()
    {
        var service = new ApiKeyService(await NewMigratedScopeFactoryAsync());
        var (kept, _) = await service.GenerateAsync("kept");
        var (revoked, _) = await service.GenerateAsync("revoked");
        await service.RevokeAsync(revoked.Id);

        var active = await service.ListAsync();

        Assert.Single(active);
        Assert.Equal(kept.Id, active[0].Id);
    }
}
