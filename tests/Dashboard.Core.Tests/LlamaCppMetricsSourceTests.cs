using System.Net;
using Dashboard.Core.Models;
using Dashboard.Infrastructure.Scrapers;

namespace Dashboard.Core.Tests;

/// <summary>Spike (multi-engine support): <see cref="LlamaCppMetricsSource"/> is
/// the thin HTTP wrapper around <see cref="LlamaCppMetricsParser"/>, same split as
/// VllmPrometheusSource/PrometheusParser. No live llama-server needed -- a stub
/// handler stands in, same as the rest of this spike's no-GPU constraint.</summary>
public class LlamaCppMetricsSourceTests
{
    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;
        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) => _respond = respond;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(_respond(request));
    }

    private sealed class StubHttpClientFactory : IHttpClientFactory
    {
        private readonly HttpMessageHandler _handler;
        public StubHttpClientFactory(HttpMessageHandler handler) => _handler = handler;
        public HttpClient CreateClient(string name) => new(_handler, disposeHandler: false);
    }

    private static LlamaCppMetricsSource CreateSource(Func<HttpRequestMessage, HttpResponseMessage> respond) =>
        new("http://sup.invalid:9999", new StubHttpClientFactory(new StubHandler(respond)));

    private static HttpResponseMessage Text(HttpStatusCode code, string body) =>
        new(code) { Content = new StringContent(body) };

    [Fact]
    public async Task ScrapeAsync_ParsesTheMetricsBody_IntoASnapshot()
    {
        var source = CreateSource(req => req.RequestUri!.AbsolutePath.EndsWith("/metrics")
            ? Text(HttpStatusCode.OK, "llamacpp:requests_processing 2\nllamacpp:tokens_predicted_total 500\n")
            : Text(HttpStatusCode.OK, "ok"));

        var snapshot = await source.ScrapeAsync();

        Assert.True(snapshot.Up);
        Assert.Equal(2, snapshot.Running);
    }

    /// <summary>P7: confirmed live against serenity -- llama-server is effectively
    /// single-threaded while streaming a completion, so a /metrics scrape made during a
    /// long generation can queue for the whole generation; the next successful scrape's
    /// counter delta then lands compressed enough for MetricsAggregator's rate math to
    /// read as an absurd tok/s spike. The proxy-chunk live path (P7) is the sole GenTps/
    /// PromptTps source for llama.cpp now; this scraper must never also feed them, even
    /// though the parser still has the raw counters available.</summary>
    [Fact]
    public async Task ScrapeAsync_NeverSurfacesGenOrPromptTokenCounters_EvenWhenThePayloadHasThem()
    {
        var source = CreateSource(_ => Text(HttpStatusCode.OK,
            "llamacpp:tokens_predicted_total 500\nllamacpp:prompt_tokens_total 200\n"));

        var snapshot = await source.ScrapeAsync();

        Assert.Null(snapshot.GenTokensTotal);
        Assert.Null(snapshot.PromptTokensTotal);
    }

    [Fact]
    public async Task ScrapeAsync_WhenTheRequestFails_ReportsDownWithTheError()
    {
        var source = CreateSource(_ => throw new HttpRequestException("connection refused"));

        var snapshot = await source.ScrapeAsync();

        Assert.False(snapshot.Up);
        Assert.Contains("connection refused", snapshot.Error);
    }

    [Fact]
    public async Task HealthAsync_OkStatus_ReturnsTrue()
    {
        var source = CreateSource(_ => Text(HttpStatusCode.OK, "{\"status\":\"ok\"}"));

        Assert.True(await source.HealthAsync());
    }

    [Fact]
    public async Task HealthAsync_NonOkStatus_ReturnsFalse()
    {
        var source = CreateSource(_ => Text(HttpStatusCode.ServiceUnavailable, "loading model"));

        Assert.False(await source.HealthAsync());
    }

    [Fact]
    public void Type_IsLlamaCpp() =>
        Assert.Equal(SourceType.LlamaCpp, CreateSource(_ => Text(HttpStatusCode.OK, "")).Type);

    /// <summary>A llama-server started without --metrics answers /metrics with something that has
    /// none of our metrics (or 404s). That must read as down-with-a-reason, like the vLLM source,
    /// not as permanently "up" with blank stats.</summary>
    [Theory]
    [InlineData("")]
    [InlineData("<html>not prometheus</html>")]
    [InlineData("# only comments\n# TYPE x counter\n")]
    [InlineData("some_other_metric 3\n")]
    public async Task ScrapeAsync_NoLlamaCppMetricsInTheBody_IsDown_WithAHintAboutMetrics(string body)
    {
        var source = CreateSource(_ => Text(HttpStatusCode.OK, body));
        var snapshot = await source.ScrapeAsync();
        Assert.False(snapshot.Up);
        Assert.Contains("--metrics", snapshot.Error);
    }

    [Fact]
    public async Task ScrapeAsync_ZeroValuedMetrics_AreStillUp()
    {
        // An idle server reports 0, which is real data -- only an absent metric means "nothing parsed".
        var source = CreateSource(_ => Text(HttpStatusCode.OK, "llamacpp:requests_processing 0\n"));
        Assert.True((await source.ScrapeAsync()).Up);
    }
}
