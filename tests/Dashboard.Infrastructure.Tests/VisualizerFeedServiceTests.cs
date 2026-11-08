using System.Collections.Concurrent;
using Dashboard.Infrastructure.Visualizer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Dashboard.Infrastructure.Tests;

/// <summary>
/// The neuron-map feed against a stand-in for the vLLM plugin: an in-memory server that
/// streams the 12 snapshots recorded from the real Intel GPU run. No GPU, no database.
/// </summary>
public sealed class VisualizerFeedServiceTests : IAsyncLifetime
{
    private static readonly string[] Frames = File.ReadAllLines(
        Path.Combine(AppContext.BaseDirectory, "fixtures", "neuronmap", "real_snapshots.jsonl"))
        .Where(l => l.Length > 0).ToArray();

    private WebApplication _app = null!;
    private TestServer _server = null!;
    private int _openStreams;
    private int _totalStreams;
    private int _failFirst;            // answer 503 this many times before streaming
    private string? _lastAuthHeader;

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        _app = builder.Build();
        _app.MapGet("/stream", async (HttpContext ctx) =>
        {
            _lastAuthHeader = ctx.Request.Headers.Authorization;
            if (Interlocked.Decrement(ref _failFirst) >= 0)
            {
                ctx.Response.StatusCode = 503;
                return;
            }
            Interlocked.Increment(ref _openStreams);
            Interlocked.Increment(ref _totalStreams);
            try
            {
                ctx.Response.ContentType = "text/event-stream";
                foreach (var f in Frames)
                {
                    await ctx.Response.WriteAsync("data: " + f + "\n\n", ctx.RequestAborted);
                    await ctx.Response.Body.FlushAsync(ctx.RequestAborted);
                }
                while (!ctx.RequestAborted.IsCancellationRequested)
                {
                    await ctx.Response.WriteAsync(": keepalive\n\n", ctx.RequestAborted);
                    await ctx.Response.Body.FlushAsync(ctx.RequestAborted);
                    await Task.Delay(50, ctx.RequestAborted);
                }
            }
            catch (OperationCanceledException) { }
            finally { Interlocked.Decrement(ref _openStreams); }
        });
        await _app.StartAsync();
        _server = _app.GetTestServer();
    }

    public async Task DisposeAsync() => await _app.DisposeAsync();

    private sealed class Sink : IVisualizerSink
    {
        public readonly ConcurrentQueue<string> Snapshots = new();
        public readonly ConcurrentQueue<VisualizerStatus> Statuses = new();
        public Task SendSnapshotAsync(string sourceId, string json, CancellationToken ct) { Snapshots.Enqueue(json); return Task.CompletedTask; }
        public Task SendStatusAsync(string sourceId, VisualizerStatus status, CancellationToken ct) { Statuses.Enqueue(status); return Task.CompletedTask; }
    }

    private sealed class Resolver : IVisualizerEndpointResolver
    {
        public VisualizerEndpoint? Endpoint { get; set; }
        public Task<VisualizerEndpoint?> ResolveAsync(string sourceId, CancellationToken ct) => Task.FromResult(Endpoint);
    }

    private sealed class Factory : IHttpClientFactory
    {
        private readonly TestServer _server;
        public Factory(TestServer server) => _server = server;
        public HttpClient CreateClient(string name) => _server.CreateClient();
    }

    private (VisualizerFeedService Feed, Sink Sink, Resolver Resolver) Make(int minFrameMs = 0, string? token = null)
    {
        var sink = new Sink();
        var resolver = new Resolver { Endpoint = new VisualizerEndpoint(new Uri("http://plugin.test/stream"), token) };
        var feed = new VisualizerFeedService(
            resolver, sink, new Factory(_server),
            Options.Create(new VisualizerOptions { MinFrameIntervalMs = minFrameMs, MaxReconnectSeconds = 2 }),
            NullLogger<VisualizerFeedService>.Instance);
        return (feed, sink, resolver);
    }

    private static async Task WaitUntil(Func<bool> cond, int ms = 8000)
    {
        var end = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < end)
        {
            if (cond()) return;
            await Task.Delay(20);
        }
        Assert.True(cond(), "condition not met in time");
    }

    [Fact]
    public async Task FirstViewer_OpensStream_AndForwardsEveryRealFrame()
    {
        var (feed, sink, _) = Make();
        await using var _ = feed;
        feed.Join("c1", "7");
        await WaitUntil(() => sink.Snapshots.Count >= Frames.Length);

        Assert.Equal(Frames, sink.Snapshots.Take(Frames.Length).ToArray());
        Assert.Contains(sink.Statuses, s => s.State == "live");
        Assert.Equal(1, feed.ActiveFeedCount);
    }

    [Fact]
    public async Task LastViewerLeaving_ClosesTheStream_SoThePluginCanSleep()
    {
        var (feed, sink, _) = Make();
        await using var _ = feed;
        feed.Join("c1", "7");
        await WaitUntil(() => Volatile.Read(ref _openStreams) == 1);

        feed.Leave("c1", "7");

        Assert.Equal(0, feed.ActiveFeedCount);
        await WaitUntil(() => Volatile.Read(ref _openStreams) == 0);
    }

    [Fact]
    public async Task TwoViewers_ShareOneStream_AndItStaysOpenUntilBothLeave()
    {
        var (feed, _, _) = Make();
        await using var _ = feed;
        feed.Join("c1", "7");
        feed.Join("c2", "7");
        await WaitUntil(() => Volatile.Read(ref _openStreams) == 1);
        Assert.Equal(1, Volatile.Read(ref _totalStreams));
        Assert.Equal(2, feed.ViewerCount("7"));

        feed.Leave("c1", "7");
        await Task.Delay(200);
        Assert.Equal(1, Volatile.Read(ref _openStreams));

        feed.Leave("c2", "7");
        await WaitUntil(() => Volatile.Read(ref _openStreams) == 0);
    }

    [Fact]
    public async Task BrowserDisconnect_CleansUpEverySourceItWatched()
    {
        var (feed, _, _) = Make();
        await using var _ = feed;
        feed.Join("c1", "7");
        feed.Join("c1", "8");
        Assert.Equal(2, feed.ActiveFeedCount);

        feed.Disconnect("c1");

        Assert.Equal(0, feed.ActiveFeedCount);
        await WaitUntil(() => Volatile.Read(ref _openStreams) == 0);
    }

    [Fact]
    public async Task PluginDown_ReportsUnavailable_ThenRecoversByItself()
    {
        _failFirst = 2;
        var (feed, sink, _) = Make();
        await using var _ = feed;
        feed.Join("c1", "7");

        await WaitUntil(() => sink.Snapshots.Count > 0, 15000);

        var states = sink.Statuses.Select(s => s.State).ToList();
        Assert.Contains("unavailable", states);
        Assert.True(states.IndexOf("unavailable") < states.LastIndexOf("live"));
        Assert.Contains(sink.Statuses, s => s.Detail is not null && s.Detail.Contains("503"));
    }

    [Fact]
    public async Task NoHostConfigured_ReportsUnavailable_WithoutCrashing()
    {
        var (feed, sink, resolver) = Make();
        await using var _ = feed;
        resolver.Endpoint = null;
        feed.Join("c1", "7");

        await WaitUntil(() => sink.Statuses.Any(s => s.State == "unavailable"));
        Assert.Empty(sink.Snapshots);
        Assert.Equal(0, Volatile.Read(ref _totalStreams));
    }

    [Fact]
    public async Task Token_IsSentAsABearerHeader()
    {
        var (feed, sink, _) = Make(token: "s3cret");
        await using var _ = feed;
        feed.Join("c1", "7");
        await WaitUntil(() => sink.Snapshots.Count > 0);
        Assert.Equal("Bearer s3cret", _lastAuthHeader);
    }

    [Fact]
    public async Task LateJoiner_GetsTheLatestFrameAndStatusImmediately()
    {
        var (feed, sink, _) = Make();
        await using var _ = feed;
        feed.Join("c1", "7");
        await WaitUntil(() => sink.Snapshots.Count >= Frames.Length);

        var (status, last) = feed.Peek("7");

        Assert.Equal("live", status.State);
        Assert.Equal(Frames[^1], last);
    }

    [Fact]
    public async Task FrameThrottle_DropsFramesThatArriveTooQuickly()
    {
        var (feed, sink, _) = Make(minFrameMs: 60_000);
        await using var _ = feed;
        feed.Join("c1", "7");
        await WaitUntil(() => sink.Snapshots.Count >= 1);
        await Task.Delay(300);
        Assert.Single(sink.Snapshots);
    }

    [Fact]
    public async Task Resolver_BuildsPluginUrlFromHostOverrideAndPort()
    {
        var resolver = new InstanceVisualizerEndpointResolver(
            scopes: null!, Options.Create(new VisualizerOptions { HostOverride = "tes.home", Port = 9998, Token = " " }));

        var ep = await resolver.ResolveAsync("7", CancellationToken.None);

        Assert.Equal("http://tes.home:9998/stream", ep!.Url.ToString());
        Assert.Null(ep.Token);
    }
}
