using System.Net;
using System.Net.Http;
using Dashboard.Core.Abstractions;
using Dashboard.Core.Models;

namespace Dashboard.Infrastructure.Scrapers;

/// <summary>Scrapes a vLLM /metrics endpoint and parses into a snapshot.</summary>
public sealed class VllmPrometheusSource : IMetricsSource
{
    private readonly string _metricsUrl;
    private readonly IHttpClientFactory _httpFactory;

    public VllmPrometheusSource(string metricsUrl, IHttpClientFactory httpFactory)
    {
        _metricsUrl = NormalizeMetricsUrl(metricsUrl);
        _httpFactory = httpFactory;
    }

    private static string NormalizeMetricsUrl(string baseOrFull)
    {
        var s = baseOrFull.Trim().TrimEnd('/');
        return s.EndsWith("/metrics", StringComparison.OrdinalIgnoreCase) ? s : s + "/metrics";
    }

    public SourceType Type => SourceType.Vllm;

    public async Task<MetricsSnapshot> ScrapeAsync(CancellationToken ct = default)
    {
        using var client = _httpFactory.CreateClient("metrics");
        string text;
        try
        {
            text = await client.GetStringAsync(_metricsUrl, ct);
        }
        catch (Exception ex)
        {
            return new MetricsSnapshot
            {
                Ts = DateTime.UtcNow,
                Up = false,
                Error = Shorten(ex),
            };
        }

        var parsed = PrometheusParser.Parse(text);
        var model = parsed.PickPrimary();
        if (model is null)
        {
            return new MetricsSnapshot
            {
                Ts = DateTime.UtcNow,
                Up = false,
                Error = "No vllm metrics parsed (empty or unexpected body).",
            };
        }

        return new MetricsSnapshot
        {
            Ts = DateTime.UtcNow,
            Up = true,
            ModelName = model.ModelName,
            Running = model.Running,
            Waiting = model.Waiting,
            KvPct = model.KvPct,
            GenTokensTotal = model.GenTokensTotal,
            PromptTokensTotal = model.PromptTokensTotal,
            ReqCountTotal = model.ReqCountTotal,
            PreemptionsTotal = model.PreemptionsTotal,
            PrefixHitsTotal = model.PrefixHitsTotal,
            PrefixQueriesTotal = model.PrefixQueriesTotal,
            TtftSum = model.TtftSum,
            TtftCount = model.TtftCount,
            ItlSum = model.ItlSum,
            ItlCount = model.ItlCount,
            E2eSum = model.E2eSum,
            E2eCount = model.E2eCount,
            QueueSum = model.QueueSum,
            QueueCount = model.QueueCount,
        };
    }

    public async Task<bool> HealthAsync(CancellationToken ct = default)
    {
        using var client = _httpFactory.CreateClient("metrics");
        try
        {
            var resp = await client.GetAsync(_metricsUrl, ct);
            return resp.StatusCode is HttpStatusCode.OK or HttpStatusCode.NoContent;
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
