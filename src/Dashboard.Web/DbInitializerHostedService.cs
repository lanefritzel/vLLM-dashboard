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
                await SeedInitialApiKeyAsync(scope.ServiceProvider, cancellationToken);
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
            ["security.requireApiKey"] = "false",
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

    // Printed to stdout (docker logs), never the DB-backed AppLogs/Logs page -- that
    // page is a durable, UI-visible history, and a raw API key has no business sitting
    // there indefinitely (worse once this is multi-user and other accounts can see it).
    private static async Task SeedInitialApiKeyAsync(IServiceProvider services, CancellationToken ct)
    {
        var apiKeys = services.GetRequiredService<Dashboard.Infrastructure.Persistence.V2.IApiKeyService>();
        if (await apiKeys.AnyActiveAsync(ct))
            return;

        var (_, rawKey) = await apiKeys.GenerateAsync("default", userId: null, ct);
        Console.WriteLine("================================================================");
        Console.WriteLine(" Generated an API key for the inference proxy (/proxy/<slug>/...).");
        Console.WriteLine(" Copy it now -- it will not be shown again:");
        Console.WriteLine($"   {rawKey}");
        Console.WriteLine(" Enforcement is OFF by default. Turn it on from Security once");
        Console.WriteLine(" you've saved this key somewhere safe.");
        Console.WriteLine("================================================================");
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
