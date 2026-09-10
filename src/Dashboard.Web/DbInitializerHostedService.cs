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

    private async Task SeedInitialSourcesAsync(AppDbContext db, CancellationToken ct)
    {
        if (await db.Sources.AnyAsync(ct))
            return; // only on a truly first run

        var vllmUrl = configuration["VLLM_URL"] ?? "http://tes.home:9999";
        var vllmInfer = configuration["VLLM_INFERENCE_URL"] ?? vllmUrl;
        var ollamaUrl = configuration["OLLAMA_URL"];

        var tes = new Source
        {
            Label = "tes — main vLLM",
            Type = SourceType.Vllm.ToStorage(),
            MetricsUrl = vllmUrl,
            InferenceUrl = vllmInfer,
            DockerHost = "tcp://tes.home:2375",
            ContainerName = null,
            ScrapeIntervalSeconds = 5,
            Active = true,
            SortOrder = 0,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        db.Sources.Add(tes);

        db.Models.Add(new Model
        {
            SourceId = tes.Id,
            Name = "qwen3.8-vllm",
            DisplayName = "Qwen3.8 (vLLM)",
            SwapArgs = null,
            IsActive = true,
            SortOrder = 0,
        });

        if (!string.IsNullOrWhiteSpace(ollamaUrl))
        {
            db.Sources.Add(new Source
            {
                Label = "serenity — Ollama",
                Type = SourceType.Ollama.ToStorage(),
                MetricsUrl = ollamaUrl,
                InferenceUrl = ollamaUrl,
                ScrapeIntervalSeconds = 5,
                Active = false,
                SortOrder = 1,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            });
        }

        await db.SaveChangesAsync(ct);
        logger.LogInformation("Seeded initial sources (tes vLLM + model, optional serenity Ollama).");
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
