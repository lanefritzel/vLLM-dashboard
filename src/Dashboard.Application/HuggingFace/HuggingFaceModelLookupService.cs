using System.Text.Json;
using Dashboard.Core.Abstractions;
using Dashboard.Core.Models;
using Microsoft.Extensions.Logging;

namespace Dashboard.Application.HuggingFace;

/// <summary>
/// Looks up a Hugging Face repo's public metadata via its own API — no auth needed
/// for public repos. Two calls: the repo API (parameter count, quantization config,
/// tags) and the repo's own config.json (the model's default max context length,
/// the exact field that silently defaulted to 262,144 tokens in the Gemma incident
/// this looks up specifically so it can be shown before a swap, not discovered
/// after one).
///
/// Best-effort throughout: a failure at either step degrades to a partial or null
/// result rather than throwing — this is a UI convenience, not something that
/// should ever block a save.
/// </summary>
public sealed class HuggingFaceModelLookupService : IHuggingFaceModelLookup
{
    private static readonly string[] QuantizationTagMarkers =
    {
        "4-bit", "8-bit", "gptq", "awq", "gguf", "quantized", "int4", "int8", "fp8", "nvfp4",
    };

    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<HuggingFaceModelLookupService> _logger;

    public HuggingFaceModelLookupService(IHttpClientFactory httpFactory, ILogger<HuggingFaceModelLookupService> logger)
    {
        _httpFactory = httpFactory;
        _logger = logger;
    }

    public async Task<HuggingFaceModelInfo?> LookupAsync(string repoId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(repoId))
            return null;

        var trimmedRepoId = repoId.Trim();
        var client = _httpFactory.CreateClient("huggingface");

        JsonDocument apiDoc;
        try
        {
            var apiUrl = $"https://huggingface.co/api/models/{EscapeRepoPath(trimmedRepoId)}";
            using var resp = await client.GetAsync(apiUrl, ct);
            if (!resp.IsSuccessStatusCode)
                return null; // repo not found / not public / rate-limited — nothing to report
            apiDoc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Hugging Face lookup failed for {RepoId}.", trimmedRepoId);
            return null;
        }

        using (apiDoc)
        {
            var root = apiDoc.RootElement;
            var parameterCount = TryGetParameterCount(root);
            var (isQuantized, quantMethod) = TryGetQuantization(root);

            var maxContextLength = await TryGetMaxContextLengthAsync(client, trimmedRepoId, ct);

            return new HuggingFaceModelInfo
            {
                RepoId = trimmedRepoId,
                ParameterCount = parameterCount,
                IsQuantized = isQuantized,
                QuantizationMethod = quantMethod,
                DefaultMaxContextLength = maxContextLength,
            };
        }
    }

    internal static long? TryGetParameterCount(JsonElement root)
    {
        if (root.TryGetProperty("safetensors", out var safetensors) &&
            safetensors.TryGetProperty("total", out var totalEl) &&
            totalEl.ValueKind == JsonValueKind.Number &&
            totalEl.TryGetInt64(out var total))
        {
            return total;
        }
        return null;
    }

    internal static (bool IsQuantized, string? Method) TryGetQuantization(JsonElement root)
    {
        if (root.TryGetProperty("config", out var config) &&
            config.TryGetProperty("quantization_config", out var quantConfig) &&
            quantConfig.ValueKind == JsonValueKind.Object)
        {
            string? method = null;
            if (quantConfig.TryGetProperty("quant_method", out var methodEl) && methodEl.ValueKind == JsonValueKind.String)
                method = methodEl.GetString();
            return (true, method);
        }

        // Fallback: some repos don't surface quantization_config in this summarized
        // API response but do tag themselves clearly (e.g. "4-bit", "gptq").
        if (root.TryGetProperty("tags", out var tags) && tags.ValueKind == JsonValueKind.Array)
        {
            foreach (var tagEl in tags.EnumerateArray())
            {
                var tag = tagEl.GetString()?.ToLowerInvariant() ?? "";
                if (Array.Exists(QuantizationTagMarkers, marker => tag.Contains(marker, StringComparison.Ordinal)))
                    return (true, null);
            }
        }

        return (false, null);
    }

    /// <summary>The repo API's own "config" field is a summary and often omits
    /// max_position_embeddings entirely — the real value lives in the repo's actual
    /// config.json, fetched separately here.</summary>
    private async Task<int?> TryGetMaxContextLengthAsync(HttpClient client, string repoId, CancellationToken ct)
    {
        try
        {
            var configUrl = $"https://huggingface.co/{EscapeRepoPath(repoId)}/resolve/main/config.json";
            using var resp = await client.GetAsync(configUrl, ct);
            if (!resp.IsSuccessStatusCode)
                return null;

            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            var root = doc.RootElement;

            if (TryGetMaxPositionEmbeddings(root, out var topLevel))
                return topLevel;

            // Multimodal configs commonly nest the text model's own settings under
            // "text_config" — check there explicitly rather than a generic recursive
            // scan, since a generic scan could just as easily find "vision_config"'s
            // (usually smaller, and irrelevant to text generation) value instead.
            if (root.TryGetProperty("text_config", out var textConfig) &&
                TryGetMaxPositionEmbeddings(textConfig, out var nested))
            {
                return nested;
            }

            return null;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not read config.json for {RepoId}.", repoId);
            return null;
        }
    }

    internal static bool TryGetMaxPositionEmbeddings(JsonElement obj, out int value)
    {
        value = 0;
        if (obj.ValueKind == JsonValueKind.Object &&
            obj.TryGetProperty("max_position_embeddings", out var el) &&
            el.ValueKind == JsonValueKind.Number &&
            el.TryGetInt32(out var v))
        {
            value = v;
            return true;
        }
        return false;
    }

    /// <summary>Escapes each "/"-separated segment of a repo id (e.g.
    /// "google/gemma-4-31B-it") independently, preserving the slash that separates
    /// the org from the repo name.</summary>
    private static string EscapeRepoPath(string repoId) =>
        string.Join('/', repoId.Split('/').Select(Uri.EscapeDataString));
}
