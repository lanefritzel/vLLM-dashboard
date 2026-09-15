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
            await BackfillModelRegistryAsync(db, cancellationToken);
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
                ScrapeIntervalSeconds = 2,
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
                ScrapeIntervalSeconds = 2,
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

    /// <summary>One-time (idempotent) migration of models from their old per-instance
    /// ownership (SourceId) onto the owning instance's PhysicalServerId — shared by
    /// every instance on that server — and of each instance's old IsActive/
    /// PreloadOnStartup model flags onto the instance itself (ActiveModelId/
    /// PreloadModelId), before those old columns become permanently unused. Runs
    /// after <see cref="BackfillPhysicalServersAsync"/> since it depends on sources
    /// already having their PhysicalServerId resolved.</summary>
    private async Task BackfillModelRegistryAsync(AppDbContext db, CancellationToken ct)
    {
        var unresolvedModels = await db.Models.Where(m => m.PhysicalServerId == null).OrderBy(m => m.Id).ToListAsync(ct);
        if (unresolvedModels.Count > 0)
        {
            var ownerIds = unresolvedModels.Select(m => m.SourceId).Distinct().ToList();
            var owners = await db.Sources.Where(s => ownerIds.Contains(s.Id)).ToDictionaryAsync(s => s.Id, ct);

            // Two old per-instance copies of "the same" model — exactly the
            // duplication this migration exists to fix — now collide on the new
            // shared (PhysicalServerId, Name) uniqueness. Rename the later one rather
            // than merge/drop it: their settings (Options, IdleTtlSeconds, etc.) may
            // genuinely differ per old instance, and merging would silently discard
            // one side. A visible, harmless duplicate the user can consolidate
            // themselves beats losing configuration silently.
            var taken = await db.Models.Where(m => m.PhysicalServerId != null)
                .Select(m => new { m.PhysicalServerId, m.Name })
                .ToListAsync(ct);
            var takenNames = new HashSet<(Guid ServerId, string Name)>(taken.Select(t => (t.PhysicalServerId!.Value, t.Name)));

            foreach (var model in unresolvedModels)
            {
                if (!owners.TryGetValue(model.SourceId, out var owner) || owner.PhysicalServerId is not { } serverId)
                    continue; // this instance itself has no physical server yet — leave for a later run

                var name = model.Name;
                if (takenNames.Contains((serverId, name)))
                {
                    var suffix = 2;
                    string candidate;
                    do
                    {
                        candidate = suffix == 2 ? $"{model.Name} (from {owner.Label})" : $"{model.Name} (from {owner.Label} #{suffix})";
                        suffix++;
                    } while (takenNames.Contains((serverId, candidate)));
                    logger.LogWarning(
                        "Backfill: renamed duplicate model '{Old}' to '{New}' — another instance on the same physical server already had this exact model name registered separately.",
                        model.Name, candidate);
                    name = candidate;
                }

                model.Name = name;
                model.PhysicalServerId = serverId;
                takenNames.Add((serverId, name));
            }
        }

        var vllmSources = await db.Sources.Where(s => s.PhysicalServerId != null).ToListAsync(ct);
        foreach (var source in vllmSources)
        {
            if (source.ActiveModelId is null)
            {
                var active = await db.Models.FirstOrDefaultAsync(m => m.SourceId == source.Id && m.IsActive, ct);
                if (active is not null)
                    source.ActiveModelId = active.Id;
            }
            if (source.PreloadModelId is null)
            {
                var preload = await db.Models.FirstOrDefaultAsync(m => m.SourceId == source.Id && m.PreloadOnStartup, ct);
                if (preload is not null)
                    source.PreloadModelId = preload.Id;
            }
        }

        await db.SaveChangesAsync(ct);
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
