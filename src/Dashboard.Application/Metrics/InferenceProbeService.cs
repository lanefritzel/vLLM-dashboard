using System.Text;
using System.Text.Json;
using Dashboard.Core.Abstractions;
using Dashboard.Core.Models;
using Microsoft.Extensions.Logging;

namespace Dashboard.Application.Metrics;

/// <summary>
/// Quick-test: POSTs to the source's inference endpoint (<c>/v1/completions</c>)
/// and measures latency. Uses the "inference" named HttpClient.
/// </summary>
public sealed class InferenceProbeService : IInferenceProbe
{
    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<InferenceProbeService> _logger;

    public InferenceProbeService(IHttpClientFactory httpFactory, ILogger<InferenceProbeService> logger)
    {
        _httpFactory = httpFactory;
        _logger = logger;
    }

    public async Task<InferenceResult> ProbeAsync(SourceInfo source, InferenceRequest request, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(source.InferenceUrl))
            return new InferenceResult { Success = false, Error = "Source has no inference URL configured." };

        var url = source.InferenceUrl.TrimEnd('/') + "/v1/completions";

        var payload = new
        {
            model = string.IsNullOrWhiteSpace(request.Model) ? null : request.Model,
            prompt = request.Prompt,
            max_tokens = request.MaxTokens,
            temperature = 0.0,
        };

        var client = _httpFactory.CreateClient("inference");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            using var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
            using var resp = await client.PostAsync(url, content, ct);
            var body = await resp.Content.ReadAsStringAsync(ct);
            sw.Stop();

            if (!resp.IsSuccessStatusCode)
                return new InferenceResult
                {
                    Success = false,
                    LatencyMs = (int)sw.ElapsedMilliseconds,
                    Error = $"HTTP {(int)resp.StatusCode}: {Truncate(body, 300)}",
                };

            return Parse(body, sw.ElapsedMilliseconds);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            sw.Stop();
            _logger.LogWarning(ex, "Inference probe to {Url} failed.", url);
            return new InferenceResult { Success = false, LatencyMs = (int)sw.ElapsedMilliseconds, Error = ex.Message };
        }
    }

    private static InferenceResult Parse(string body, long latencyMs)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;

            string? text = null;
            if (root.TryGetProperty("choices", out var choices) &&
                choices.GetArrayLength() > 0 &&
                choices[0].TryGetProperty("text", out var textEl))
            {
                text = textEl.GetString();
            }

            int? tokens = null;
            if (root.TryGetProperty("usage", out var usage) &&
                usage.TryGetProperty("total_tokens", out var total))
            {
                tokens = total.GetInt32();
            }

            return new InferenceResult
            {
                Success = true,
                Text = text,
                LatencyMs = (int)latencyMs,
                TokensUsed = tokens,
            };
        }
        catch (JsonException)
        {
            return new InferenceResult
            {
                Success = true,
                Text = Truncate(body, 1000),
                LatencyMs = (int)latencyMs,
            };
        }
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";
}
