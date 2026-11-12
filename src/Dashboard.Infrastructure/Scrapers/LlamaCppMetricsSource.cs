using System.Net;
using System.Net.Http;
using Dashboard.Core.Abstractions;
using Dashboard.Core.Models;

namespace Dashboard.Infrastructure.Scrapers;

/// <summary>Spike (multi-engine support proposal): scrapes a llama.cpp
/// <c>llama-server</c>'s <c>/metrics</c> and <c>/health</c> endpoints, same shape
/// as <see cref="VllmPrometheusSource"/>. The snapshot carries only what llama.cpp
/// actually exposes -- no TTFT/ITL/E2E/prefix-cache fields, by design (see
/// <see cref="LlamaCppMetricsParser"/>).</summary>
public sealed class LlamaCppMetricsSource : IMetricsSource
{
    private readonly string _metricsUrl;
    private readonly string _healthUrl;
    private readonly IHttpClientFactory _httpFactory;

    public LlamaCppMetricsSource(string baseUrl, IHttpClientFactory httpFactory)
    {
        var b = baseUrl.Trim().TrimEnd('/');
        _metricsUrl = b + "/metrics";
        _healthUrl = b + "/health";
        _httpFactory = httpFactory;
    }

    public SourceType Type => SourceType.LlamaCpp;

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
            return new MetricsSnapshot { Ts = DateTime.UtcNow, Up = false, Error = Shorten(ex) };
        }

        var m = LlamaCppMetricsParser.Parse(text);

        // Same contract as VllmPrometheusSource: a reachable server whose body has none of our
        // metrics is "down" with a reason, not "up" with blank stats. llama-server serves /metrics
        // only when started with --metrics; without it the body is empty/unexpected (or a 404, which
        // is caught above). Zero is real data -- only an ABSENT metric counts as not parsed.
        if (m.Running is null && m.Waiting is null && m.GenTokensTotal is null && m.PromptTokensTotal is null)
        {
            return new MetricsSnapshot
            {
                Ts = DateTime.UtcNow,
                Up = false,
                Error = "No llama.cpp metrics parsed (empty or unexpected body) -- is llama-server running with --metrics?",
            };
        }

        return new MetricsSnapshot
        {
            Ts = DateTime.UtcNow,
            Up = true,
            Running = m.Running,
            Waiting = m.Waiting,
            KvPct = m.KvPct,

            // P7: deliberately NOT surfaced here, even though LlamaCppMetricsParser still
            // parses them. llama-server is effectively single-threaded while it's busy
            // streaming a completion, so a /metrics GET made during a long generation can
            // sit queued for however long that generation takes; MetricsAggregator's
            // counter-rate math (delta tokens / delta wall-clock time since the last
            // successful scrape) is sound in general, but when the *previous* successful
            // scrape landed well before a long busy stretch, the next one's token delta
            // can land compressed enough to read as an absurd tok/s spike -- confirmed
            // live against serenity (rows with no KvPct, i.e. from this poller, at
            // 1000-4000+ "tok/s" during/after a long generation). The P7 proxy path
            // (LlamaCppLiveMetricsPublisher) is llama.cpp's sole live throughput source
            // now -- it reads the engine's own per-chunk timings.predicted_per_second
            // directly, with no polling-interval race to be wrong about. Leaving these
            // null here, rather than also computing them and letting both producers write
            // to the same source, is what keeps GenTps/PromptTps samples for a llama.cpp
            // instance unambiguously single-sourced.
            GenTokensTotal = null,
            PromptTokensTotal = null,
        };
    }

    public async Task<bool> HealthAsync(CancellationToken ct = default)
    {
        using var client = _httpFactory.CreateClient("metrics");
        try
        {
            var resp = await client.GetAsync(_healthUrl, ct);
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
