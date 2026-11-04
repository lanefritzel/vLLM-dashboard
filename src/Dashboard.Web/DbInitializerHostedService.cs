using Dashboard.Infrastructure.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Dashboard.Web;

public sealed class DbInitializerHostedService(
    IServiceScopeFactory scopeFactory,
    ILogger<DbInitializerHostedService> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.Database.MigrateAsync(cancellationToken);
            await SeedConfigDefaultsAsync(db, cancellationToken);
            logger.LogInformation("Database initialized (migrations applied, config seeded).");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Database initialization failed");
        }
    }

    private static async Task SeedConfigDefaultsAsync(AppDbContext db, CancellationToken ct)
    {
        var defaults = new Dictionary<string, string>
        {
            ["retention.days"] = "30",
            ["rollups.enabled"] = "true",
            ["ui.updateRateMs"] = "1000",
            ["default.history.windowMinutes"] = "60",
        };

        var existing = await db.Configs.ToDictionaryAsync(c => c.Key, ct);
        foreach (var (key, value) in defaults)
        {
            if (!existing.ContainsKey(key))
                db.Configs.Add(new Dashboard.Infrastructure.Persistence.AppConfig { Key = key, Value = value, UpdatedAt = DateTime.UtcNow });
        }

        if (db.ChangeTracker.HasChanges())
            await db.SaveChangesAsync(ct);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
