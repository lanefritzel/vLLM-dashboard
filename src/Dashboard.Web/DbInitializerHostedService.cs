using Dashboard.Infrastructure.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Dashboard.Web;

public sealed class DbInitializerHostedService(
    IServiceScopeFactory scopeFactory,
    ILogger<DbInitializerHostedService> logger,
    int maxAttempts = 10,
    int retryDelayMs = 3000) : IHostedService
{
    // Retries because on a fresh `docker compose up` the dashboard can win the race
    // against Postgres (or Postgres isn't reachable yet); without a retry the
    // migrations never run and the first request fails with "relation ... does not exist".
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                await db.Database.MigrateAsync(cancellationToken);
                await SeedConfigDefaultsAsync(db, cancellationToken);
                logger.LogInformation("Database initialized (migrations applied, config seeded).");
                return;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                var last = attempt >= maxAttempts;
                logger.LogError(ex, "Database initialization failed (attempt {Attempt}/{Max}){Retry}",
                    attempt, maxAttempts, last ? "" : "; retrying");
                if (last) return;
                try { await Task.Delay(retryDelayMs, cancellationToken); }
                catch (OperationCanceledException) { return; }
            }
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
