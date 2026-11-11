using System.Net;
using Dashboard.Core.Models;
using Dashboard.Infrastructure.Scrapers;

namespace Dashboard.Core.Tests;

/// <summary>P3: SourceType picks the metrics source; llama.cpp reads its single port
/// from InferenceUrl (metrics, health and inference share one port), vLLM from MetricsUrl.</summary>
public class MetricsSourceFactoryTests
{
    private sealed class RecordingHandler : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = new();
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request.RequestUri!);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("") });
        }
    }

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    [Fact]
    public void LlamaCpp_ResolvesToTheLlamaCppSource()
    {
        var source = new MetricsSourceFactory(new Factory(new RecordingHandler()))
            .Create(new SourceInfo { Type = SourceType.LlamaCpp, InferenceUrl = "http://h:8080" });
        Assert.IsType<LlamaCppMetricsSource>(source);
        Assert.Equal(SourceType.LlamaCpp, source.Type);
    }

    [Fact]
    public async Task LlamaCpp_ScrapesInferenceUrl_NotMetricsUrl()
    {
        var handler = new RecordingHandler();
        var source = new MetricsSourceFactory(new Factory(handler)).Create(new SourceInfo
        {
            Type = SourceType.LlamaCpp, InferenceUrl = "http://llama.invalid:8080", MetricsUrl = "http://wrong.invalid:1",
        });
        await source.ScrapeAsync();
        Assert.Equal("llama.invalid", handler.Requests.Single().Host);
        Assert.EndsWith("/metrics", handler.Requests.Single().AbsolutePath);
    }

    [Fact]
    public void Vllm_StillResolvesToThePrometheusSource()
    {
        var source = new MetricsSourceFactory(new Factory(new RecordingHandler()))
            .Create(new SourceInfo { Type = SourceType.Vllm, MetricsUrl = "http://h:9999" });
        Assert.IsType<VllmPrometheusSource>(source);
    }

    [Fact]
    public void SourceType_LlamaCpp_RoundTripsThroughStorage()
    {
        Assert.Equal("llamacpp", SourceType.LlamaCpp.ToStorage());
        Assert.Equal(SourceType.LlamaCpp, SourceTypeExtensions.FromStorage("llamacpp"));
    }
}
