using Dashboard.Core.Models;
using Dashboard.Infrastructure.Identity;
using Dashboard.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Dashboard.Web;

public sealed class DbInitializerHostedService(
    IServiceScopeFactory scopeFactory,
    IConfiguration configuration,
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
            await SeedInitialSourcesAsync(db, cancellationToken);
            logger.LogInformation("Database initialized (migrations applied, config + sources seeded).");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Database initialization failed");
        }
    }

    /// <summary>Only ever seeds a source when an operator explicitly opts in via env
    /// var — a brand-new install for a different user must start with an empty
    /// Sources list (use the Server/Model Setup Wizard from there), not a source
    /// pointed at a hostname and model name specific to one particular deployment.</summary>
    private async Task SeedInitialSourcesAsync(AppDbContext db, CancellationToken ct)
    {
        if (await db.Sources.AnyAsync(ct))
            return; // only on a truly first run

        var vllmUrl = configuration["VLLM_URL"];
        var ollamaUrl = configuration["OLLAMA_URL"];
        var sortOrder = 0;

        if (!string.IsNullOrWhiteSpace(vllmUrl))
        {
            var vllmInfer = configuration["VLLM_INFERENCE_URL"] ?? vllmUrl;
            db.Sources.Add(new Source
            {
                Label = "vLLM",
                Type = SourceType.Vllm.ToStorage(),
                MetricsUrl = vllmUrl,
                InferenceUrl = vllmInfer,
                DockerHost = configuration["VLLM_DOCKER_HOST"],
                ContainerName = null,
                ScrapeIntervalSeconds = 5,
                Active = true,
                SortOrder = sortOrder++,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            });
        }

        if (!string.IsNullOrWhiteSpace(ollamaUrl))
        {
            db.Sources.Add(new Source
            {
                Label = "Ollama",
                Type = SourceType.Ollama.ToStorage(),
                MetricsUrl = ollamaUrl,
                InferenceUrl = ollamaUrl,
                ScrapeIntervalSeconds = 5,
                Active = false,
                SortOrder = sortOrder++,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            });
        }

        if (sortOrder == 0)
        {
            logger.LogInformation("No VLLM_URL/OLLAMA_URL set — starting with no sources. Use the Server Setup Wizard on the Settings page.");
            return;
        }

        await db.SaveChangesAsync(ct);
        logger.LogInformation("Seeded initial sources from environment configuration.");
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
                db.Configs.Add(new AppConfig { Key = key, Value = value, UpdatedAt = DateTime.UtcNow });
        }

        if (db.ChangeTracker.HasChanges())
            await db.SaveChangesAsync(ct);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
