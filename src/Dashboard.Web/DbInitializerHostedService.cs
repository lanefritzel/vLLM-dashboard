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
            await BackfillPhysicalServersAsync(db, cancellationToken);
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

    /// <summary>One-time (but idempotent — safe to run every startup) migration of the
    /// old per-source DockerHost/GpuVendor/GpuVramTotalGb/HfCacheHostPath/HfToken
    /// fields onto a shared PhysicalServer. Two vLLM sources with the same DockerHost
    /// are the same physical machine — this merges them onto one PhysicalServer row
    /// instead of leaving each with its own independent (and possibly inconsistent,
    /// e.g. one missing a cache directory the other has) copy of the same settings.
    /// Only touches sources that don't already have a PhysicalServerId.</summary>
    private async Task BackfillPhysicalServersAsync(AppDbContext db, CancellationToken ct)
    {
        var candidates = await db.Sources
            .Include(s => s.LaunchSpec)
            .Where(s => s.PhysicalServerId == null && s.DockerHost != null && s.DockerHost != "")
            .ToListAsync(ct);
        if (candidates.Count == 0)
            return;

        var servers = await db.PhysicalServers.ToListAsync(ct);
        var groups = candidates.GroupBy(s => NormalizeHost(s.DockerHost!));
        var created = 0;

        foreach (var group in groups)
        {
            var existing = servers.FirstOrDefault(p => NormalizeHost(p.DockerHost) == group.Key);
            if (existing is null)
            {
                var first = group.First();
                existing = new PhysicalServer
                {
                    Label = ExtractHostname(first.DockerHost!),
                    DockerHost = first.DockerHost!.Trim(),
                    GpuVendor = first.LaunchSpec?.GpuVendor ?? "none",
                    GpuVramTotalGb = first.LaunchSpec?.GpuVramTotalGb,
                    HfCacheHostPath = first.LaunchSpec?.HfCacheHostPath,
                    HfToken = first.LaunchSpec?.HfToken,
                };
                db.PhysicalServers.Add(existing);
                servers.Add(existing);
                created++;
            }

            foreach (var source in group)
                source.PhysicalServerId = existing.Id;
        }

        await db.SaveChangesAsync(ct);
        if (created > 0)
            logger.LogInformation("Backfilled {Count} physical server(s) from existing sources' Docker hosts.", created);
    }

    private static string NormalizeHost(string dockerHost) => dockerHost.Trim().ToLowerInvariant();

    /// <summary>A reasonable default label for an auto-created physical server — just
    /// the bare hostname/IP, since the source's own label (e.g. "tes — main vLLM")
    /// describes the vLLM instance, not the machine it runs on. Editable afterward.</summary>
    private static string ExtractHostname(string dockerHost)
    {
        var host = dockerHost.Trim();
        foreach (var scheme in new[] { "tcp://", "http://", "https://" })
        {
            if (host.StartsWith(scheme, StringComparison.OrdinalIgnoreCase))
                host = host[scheme.Length..];
        }
        var hostname = host.Split(':', '/')[0];
        return string.IsNullOrWhiteSpace(hostname) ? dockerHost.Trim() : hostname;
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
