using System.Globalization;
using System.Text.Json;
using Dashboard.Infrastructure.Identity;
using Dashboard.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using V2 = Dashboard.Infrastructure.Persistence.V2;

namespace Dashboard.Infrastructure.Conversion;

/// <summary>
/// See <see cref="ILegacyConversionService"/>. Keys every new row by the same
/// natural key the new schema already enforces uniqueness on (servers.label,
/// instances.proxy_slug, (server_id, name) for models/recipes) -- a second run
/// finds everything already there and inserts nothing, which is what makes this
/// idempotent without needing a separate "already converted" marker table.
/// </summary>
public sealed class LegacyConversionService(AppDbContext db) : ILegacyConversionService
{
    public async Task<ConversionReport> ConvertAsync(CancellationToken ct = default)
    {
        var notes = new List<ConversionReportLine>();
        var ollamaDropped = new List<string>();
        int serversConverted = 0, instancesConverted = 0, modelsConverted = 0, recipesCreated = 0;

        await using var tx = await db.Database.BeginTransactionAsync(ct);

        var serverIdMap = new Dictionary<Guid, int>();
        var existingServersByLabel = await db.ServersV2.ToDictionaryAsync(s => s.Label, ct);
        foreach (var legacy in await db.PhysicalServers.ToListAsync(ct))
        {
            if (!existingServersByLabel.TryGetValue(legacy.Label, out var server))
            {
                server = new V2.Server
                {
                    Label = legacy.Label,
                    DockerHost = legacy.DockerHost,
                    GpuName = legacy.GpuVendor,
                    GpuMemGb = legacy.GpuVramTotalGb,
                    ModelsDir = legacy.HfCacheHostPath ?? "",
                };
                db.ServersV2.Add(server);
                await db.SaveChangesAsync(ct); // need its new Id before anything below can reference it
                existingServersByLabel[legacy.Label] = server;
                serversConverted++;
            }
            serverIdMap[legacy.Id] = server.Id;
        }

        var existingInstancesBySlug = await db.InstancesV2.ToDictionaryAsync(i => i.ProxySlug, ct);
        foreach (var source in await db.Sources.ToListAsync(ct))
        {
            if (string.Equals(source.Type, "ollama", StringComparison.OrdinalIgnoreCase))
            {
                ollamaDropped.Add(source.Label);
                notes.Add(new ConversionReportLine("ollama_source_dropped",
                    $"Ollama source '{source.Label}' (slug '{source.Slug}') has no destination in the new schema and was dropped, along with its models."));
                continue;
            }

            if (source.PhysicalServerId is not { } physicalServerId || !serverIdMap.TryGetValue(physicalServerId, out var serverId))
            {
                notes.Add(new ConversionReportLine("source_no_physical_server",
                    $"Source '{source.Label}' (slug '{source.Slug}') has no physical server configured and could not be converted."));
                continue;
            }

            if (existingInstancesBySlug.ContainsKey(source.Slug))
                continue;

            var instance = new V2.Instance
            {
                ServerId = serverId,
                Label = source.Label,
                ProxySlug = source.Slug, // kept byte-for-byte (invariant 1)
                AdminUrl = null, // needs supervisor registration
                Port = ParsePort(source.InferenceUrl) ?? 9999,
                IsActive = source.Active,
            };
            db.InstancesV2.Add(instance);
            await db.SaveChangesAsync(ct);
            existingInstancesBySlug[source.Slug] = instance;
            instancesConverted++;
        }

        var existingModelsByKey = await db.ModelsV2.ToDictionaryAsync(m => (m.ServerId, m.Name), ct);
        var existingRecipesByKey = await db.RecipesV2.ToDictionaryAsync(r => (r.ServerId, r.Name), ct);
        foreach (var model in await db.Models.ToListAsync(ct))
        {
            if (model.PhysicalServerId is not { } physicalServerId || !serverIdMap.TryGetValue(physicalServerId, out var serverId))
                continue; // belonged to a dropped source (e.g. Ollama) or was never assigned a server

            var key = (serverId, model.Name);
            if (!existingModelsByKey.TryGetValue(key, out var newModel))
            {
                var isLocal = string.Equals(model.ModelSource, "local", StringComparison.OrdinalIgnoreCase);
                var path = isLocal ? model.ModelPathOrRepoId : "";
                var repoId = isLocal ? null : model.ModelPathOrRepoId;
                var isGguf = model.ModelPathOrRepoId.Contains("GGUF", StringComparison.OrdinalIgnoreCase);
                var available = isLocal && !string.IsNullOrWhiteSpace(path);

                if (!available)
                {
                    notes.Add(new ConversionReportLine("model_never_downloaded",
                        $"Model '{model.Name}' was registered but never downloaded -- marked download_failed; this is not an actual download failure."));
                }

                newModel = new V2.Model
                {
                    ServerId = serverId,
                    Name = model.Name,
                    DisplayName = model.DisplayName,
                    SourceType = isLocal ? "local" : "huggingface",
                    RepoId = repoId,
                    Path = path,
                    LoadFormat = isGguf ? "gguf" : "auto",
                    Status = available ? "available" : "download_failed",
                };
                db.ModelsV2.Add(newModel);
                await db.SaveChangesAsync(ct);
                existingModelsByKey[key] = newModel;
                modelsConverted++;
            }

            var recipeName = $"{model.Name} (migrated)";
            var recipeKey = (serverId, recipeName);
            if (!existingRecipesByKey.ContainsKey(recipeKey))
            {
                var recipe = BuildRecipeFromOptions(serverId, recipeName, model, notes);
                db.RecipesV2.Add(recipe);
                await db.SaveChangesAsync(ct);
                existingRecipesByKey[recipeKey] = recipe;
                recipesCreated++;
            }
        }

        await tx.CommitAsync(ct);

        return new ConversionReport
        {
            ServersConverted = serversConverted,
            InstancesConverted = instancesConverted,
            ModelsConverted = modelsConverted,
            RecipesCreated = recipesCreated,
            OllamaSourcesDropped = ollamaDropped,
            Notes = notes,
        };
    }

    private static int? ParsePort(string? inferenceUrl) =>
        Uri.TryCreate(inferenceUrl, UriKind.Absolute, out var uri) ? uri.Port : null;

    /// <summary>Catalog options -> recipe dials where one exists; anything else ->
    /// extra_args (doc 4's conversion rule). served_model_name comes from the old
    /// served name (<see cref="Model.Name"/>), not from OptionsJson -- the old app
    /// never stored it there. A converted recipe is never blocked from being
    /// created for failing P09b's save-time validation (e.g. dtype=bfloat16 on the
    /// Intel stack) -- it's stored as-is and flagged in the report instead.</summary>
    private static V2.Recipe BuildRecipeFromOptions(int serverId, string recipeName, Model model, List<ConversionReportLine> notes)
    {
        var recipe = new V2.Recipe { ServerId = serverId, Name = recipeName, ServedModelName = model.Name };
        Dictionary<string, string> options;
        try
        {
            options = JsonSerializer.Deserialize<Dictionary<string, string>>(model.OptionsJson) ?? new();
        }
        catch (JsonException)
        {
            options = new();
            notes.Add(new ConversionReportLine("model_options_malformed", $"Model '{model.Name}': stored options JSON was malformed -- recipe created with defaults only."));
        }

        var extraArgs = new List<Dictionary<string, string?>>();
        foreach (var (key, value) in options)
        {
            switch (key)
            {
                case "dtype": recipe.Dtype = value; break;
                case "kv-cache-dtype": recipe.KvCacheDtype = value; break;
                case "max-model-len" when int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var maxModelLen): recipe.MaxModelLen = maxModelLen; break;
                case "gpu-memory-utilization" when double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var gpuUtil): recipe.GpuMemoryUtilization = (float)gpuUtil; break;
                case "enforce-eager": recipe.EnforceEager = string.Equals(value, "true", StringComparison.OrdinalIgnoreCase); break;
                case "max-num-batched-tokens" when int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var maxBatched): recipe.MaxNumBatchedTokens = maxBatched; break;
                case "block-size" when int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var blockSize): recipe.BlockSize = blockSize; break;
                case "enable-auto-tool-choice": recipe.EnableAutoToolChoice = string.Equals(value, "true", StringComparison.OrdinalIgnoreCase); break;
                case "tool-call-parser": recipe.ToolCallParser = value; break;
                case "reasoning-parser": recipe.ReasoningParser = value; break;
                default:
                    extraArgs.Add(new Dictionary<string, string?> { ["key"] = key, ["value"] = value });
                    break;
            }
        }

        recipe.ExtraArgs = JsonSerializer.Serialize(extraArgs);
        if (extraArgs.Count > 0)
        {
            notes.Add(new ConversionReportLine("options_without_dial",
                $"Model '{model.Name}': option(s) {string.Join(", ", extraArgs.Select(e => e["key"]))} have no recipe dial -- moved to extra_args."));
        }

        if (string.Equals(recipe.Dtype, "bfloat16", StringComparison.OrdinalIgnoreCase))
        {
            notes.Add(new ConversionReportLine("dtype_bfloat16_on_intel_pending_validation",
                $"Model '{model.Name}': converted recipe has dtype=bfloat16 -- P09b's save-time validation blocks this on the Intel stack; flagged here, not blocked at conversion time."));
        }

        return recipe;
    }
}
