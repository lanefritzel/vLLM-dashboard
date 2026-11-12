using Dashboard.Core.Abstractions;
using Dashboard.Core.Models;

namespace Dashboard.Infrastructure.Scrapers;

/// <summary>Builds the right source implementation for a given source config.</summary>
public sealed class MetricsSourceFactory : IMetricsSourceFactory
{
    private readonly IHttpClientFactory _httpFactory;

    public MetricsSourceFactory(IHttpClientFactory httpFactory)
    {
        _httpFactory = httpFactory;
    }

    public IMetricsSource Create(SourceInfo source)
    {
        return source.Type switch
        {
            SourceType.Ollama => new OllamaSource(source.InferenceUrl, _httpFactory),
            SourceType.Mock => new MockMetricsSource(),
            SourceType.LlamaCpp => new LlamaCppMetricsSource(source.InferenceUrl, _httpFactory),
            _ => new VllmPrometheusSource(source.MetricsUrl, _httpFactory),
        };
    }
}
