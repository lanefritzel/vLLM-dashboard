using System.Net;
using System.Net.Http;
using System.Text.Json;
using Dashboard.Core.Abstractions;
using Dashboard.Core.Models;

namespace Dashboard.Infrastructure.Scrapers;

/// <summary>
/// Ollama scaffold source: reports availability, loaded models, and VRAM.
/// Ollama exposes no token metrics, so the snapshot carries Ollama-shaped fields only.
/// </summary>
public sealed class OllamaSource : IMetricsSource
{
    private readonly string _base;
    private readonly IHttpClientFactory _httpFactory;

    public OllamaSource(string inferenceUrl, IHttpClientFactory httpFactory)
    {
        _base = inferenceUrl.TrimEnd('/');
        _httpFactory = httpFactory;
    }

    public SourceType Type => SourceType.Ollama;

    public async Task<MetricsSnapshot> ScrapeAsync(CancellationToken ct = default)
    {
        using var client = _httpFactory.CreateClient("metrics");

        // Availability via /api/version.
        try
        {
            await client.GetAsync(_base + "/api/version", ct);
        }
        catch (Exception ex)
        {
            return new MetricsSnapshot { Ts = DateTime.UtcNow, Up = false, Error = Shorten(ex) };
        }

        var loaded = new List<string>();
        long? vram = null;

        // /api/ps -> running (loaded) models + size_vram.
        try
        {
            var psText = await client.GetStringAsync(_base + "/api/ps", ct);
            using var ps = JsonDocument.Parse(psText);
            if (ps.RootElement.TryGetProperty("models", out var models) && models.ValueKind == JsonValueKind.Array)
            {
                foreach (var m in models.EnumerateArray())
                {
                    if (m.TryGetProperty("name", out var name))
                        loaded.Add(name.GetString() ?? "");
                    if (m.TryGetProperty("size_vram", out var sv))
                        vram = (vram ?? 0) + sv.GetInt64();
                }
            }
        }
        catch { /* not loaded models -> leave empty */ }

        int? catalog = null;
        try
        {
            var tagsText = await client.GetStringAsync(_base + "/api/tags", ct);
            using var tags = JsonDocument.Parse(tagsText);
            if (tags.RootElement.TryGetProperty("models", out var models) && models.ValueKind == JsonValueKind.Array)
                catalog = models.GetArrayLength();
        }
        catch { /* no catalog */ }

        return new MetricsSnapshot
        {
            Ts = DateTime.UtcNow,
            Up = true,
            ModelName = loaded.Count > 0 ? string.Join(", ", loaded) : null,
            LoadedModels = loaded,
            TotalVramBytes = vram,
            CatalogCount = catalog,
        };
    }

    public async Task<bool> HealthAsync(CancellationToken ct = default)
    {
        using var client = _httpFactory.CreateClient("metrics");
        try
        {
            var resp = await client.GetAsync(_base + "/api/version", ct);
            return resp.StatusCode == HttpStatusCode.OK;
        }
        catch
        {
            return false;
        }
    }

    private static string Shorten(Exception ex)
    {
        string msg = ex.Message;
        return msg.Length > 200 ? msg[..200] : msg;
    }
}
