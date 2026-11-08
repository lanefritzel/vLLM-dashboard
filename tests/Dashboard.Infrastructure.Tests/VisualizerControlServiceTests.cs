using System.Collections.Concurrent;
using Dashboard.Core.Abstractions;
using Dashboard.Infrastructure.Visualizer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Logging.Abstractions;

namespace Dashboard.Infrastructure.Tests;

/// <summary>The dashboard's plugin on/off switch against a stand-in for the plugin's control
/// endpoints (/health, POST /enable, POST /disable[?now=1]). No GPU, no database.</summary>
public sealed class VisualizerControlServiceTests : IAsyncLifetime
{
    private WebApplication _app = null!;
    private TestServer _server = null!;
    private readonly ConcurrentQueue<string> _posts = new();
    private string? _lastAuth;
    private bool _disabled, _pendingDisable, _pendingEnable;
    private int _healthStatus = 200;

    public async Task InitializeAsync()
    {
        var b = WebApplication.CreateBuilder();
        b.WebHost.UseTestServer();
        _app = b.Build();
        _app.MapGet("/health", (HttpContext ctx) =>
        {
            _lastAuth = ctx.Request.Headers.Authorization;
            if (_healthStatus != 200) return Results.StatusCode(_healthStatus);
            return Results.Json(Health());
        });
        _app.MapPost("/disable", (HttpContext ctx) =>
        {
            _lastAuth = ctx.Request.Headers.Authorization;
            _posts.Enqueue("/disable" + ctx.Request.QueryString);
            if (ctx.Request.Query["now"] == "1") { _disabled = true; _pendingDisable = false; }
            else _pendingDisable = true;
            return Results.Json(Health());
        });
        _app.MapPost("/enable", (HttpContext ctx) =>
        {
            _lastAuth = ctx.Request.Headers.Authorization;
            _posts.Enqueue("/enable" + ctx.Request.QueryString);
            if (ctx.Request.Query["now"] == "1" || !_disabled) { _disabled = false; _pendingEnable = false; _pendingDisable = false; }
            else _pendingEnable = true;
            return Results.Json(Health());
        });
        await _app.StartAsync();
        _server = _app.GetTestServer();
    }

    public async Task DisposeAsync() => await _app.DisposeAsync();

    private object Health() => new
    {
        disabled = _disabled, disabled_reason = _disabled ? "manual" : "",
        pending_disable = _pendingDisable, pending_enable = _pendingEnable,
    };

    private sealed class Resolver : IVisualizerEndpointResolver
    {
        public VisualizerEndpoint? Endpoint { get; set; } = new(new Uri("http://plugin.test/stream"), null);
        public Task<VisualizerEndpoint?> ResolveAsync(string sourceId, CancellationToken ct) => Task.FromResult(Endpoint);
    }

    private sealed class Factory(TestServer server) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => server.CreateClient();
    }

    private sealed class MemoryConfig : IConfigStore
    {
        public readonly Dictionary<string, string> Values = new();
        public int GetInt(string key, int d) => int.TryParse(Get(key, d.ToString()), out var v) ? v : d;
        public string Get(string key, string d) => Values.TryGetValue(key, out var v) ? v : d;
        public Task SetAsync(string key, string value, string? by = null, CancellationToken ct = default) { Values[key] = value; return Task.CompletedTask; }
        public int RetentionDays => 30;
        public bool RollupsEnabled => true;
        public int UiUpdateRateMs => 1000;
        public int DefaultHistoryWindowMinutes => 60;
#pragma warning disable CS0067
        public event EventHandler? Changed;
#pragma warning restore CS0067
        public void RaiseChanged() { }
    }

    private (VisualizerControlService Svc, MemoryConfig Config, Resolver Resolver) Make()
    {
        var config = new MemoryConfig();
        var resolver = new Resolver();
        return (new VisualizerControlService(resolver, new Factory(_server), config, NullLogger<VisualizerControlService>.Instance), config, resolver);
    }

    [Fact]
    public async Task GetState_ReadsWhatThePluginReports()
    {
        var (svc, _, _) = Make();
        _pendingDisable = true;

        var s = await svc.GetStateAsync("7");

        Assert.True(s.Reachable);
        Assert.True(s.Enabled);
        Assert.True(s.PendingDisable);
        Assert.False(s.PendingEnable);
    }

    [Fact]
    public async Task GetState_IsUnreachable_WhenThePluginErrorsOrHasNoHost()
    {
        var (svc, _, resolver) = Make();
        _healthStatus = 503;
        Assert.False((await svc.GetStateAsync("7")).Reachable);

        _healthStatus = 200;
        resolver.Endpoint = null;
        Assert.False((await svc.GetStateAsync("7")).Reachable);
    }

    [Fact]
    public async Task TurningOff_IsGraceful_AndTheChoiceIsRemembered()
    {
        var (svc, config, _) = Make();

        var s = await svc.SetEnabledAsync("7", enabled: false, updatedBy: "tester");

        Assert.Equal(new[] { "/disable" }, _posts.ToArray());   // no ?now=1: waits for the answer to finish
        Assert.True(s.PendingDisable);
        Assert.True(s.Enabled);                                  // still on until the model is idle
        Assert.Equal("off", config.Values["visualizer.desired.7"]);
        Assert.Equal("off", svc.DesiredFor("7"));
    }

    [Fact]
    public async Task TurnOffNow_SkipsTheWait()
    {
        var (svc, _, _) = Make();
        var s = await svc.SetEnabledAsync("7", false, now: true);
        Assert.Equal(new[] { "/disable?now=1" }, _posts.ToArray());
        Assert.False(s.Enabled);
        Assert.False(s.PendingDisable);
    }

    [Fact]
    public async Task TurningOn_WaitsForTheNextRequest_WhenAnAnswerIsStreaming()
    {
        var (svc, config, _) = Make();
        _disabled = true;

        var s = await svc.SetEnabledAsync("7", enabled: true);

        Assert.Equal(new[] { "/enable" }, _posts.ToArray());
        Assert.False(s.Enabled);
        Assert.True(s.PendingEnable);
        Assert.Equal("on", config.Values["visualizer.desired.7"]);
    }

    [Fact]
    public async Task Reconcile_ReappliesOff_AfterVllmRestartedAndThePluginCameBackOn()
    {
        var (svc, config, _) = Make();
        config.Values["visualizer.desired.7"] = "off";
        var state = await svc.GetStateAsync("7");   // plugin is on again

        var after = await svc.ReconcileAsync("7", state);

        Assert.Equal(new[] { "/disable" }, _posts.ToArray());
        Assert.True(after.PendingDisable);
    }

    [Fact]
    public async Task Reconcile_ReappliesOn_WhenTheOperatorWantedItOn()
    {
        var (svc, config, _) = Make();
        config.Values["visualizer.desired.7"] = "on";
        _disabled = true;

        await svc.ReconcileAsync("7", await svc.GetStateAsync("7"));

        Assert.Equal(new[] { "/enable" }, _posts.ToArray());
    }

    [Fact]
    public async Task Reconcile_DoesNothing_WhenAlreadyInAgreement_OrAChangeIsPending_OrNoChoiceWasMade()
    {
        var (svc, config, _) = Make();

        await svc.ReconcileAsync("7", await svc.GetStateAsync("7"));          // no choice made
        config.Values["visualizer.desired.7"] = "off";
        _pendingDisable = true;
        await svc.ReconcileAsync("7", await svc.GetStateAsync("7"));          // already draining
        config.Values["visualizer.desired.7"] = "on";
        _pendingDisable = false;
        await svc.ReconcileAsync("7", await svc.GetStateAsync("7"));          // already on

        Assert.Empty(_posts);
    }

    [Fact]
    public async Task Reconcile_LeavesAnUnreachablePluginAlone()
    {
        var (svc, config, _) = Make();
        config.Values["visualizer.desired.7"] = "off";

        var s = await svc.ReconcileAsync("7", VisualizerPluginState.Unreachable);

        Assert.False(s.Reachable);
        Assert.Empty(_posts);
    }

    [Fact]
    public async Task Token_IsSentAsABearerHeader()
    {
        var (svc, _, resolver) = Make();
        resolver.Endpoint = new VisualizerEndpoint(new Uri("http://plugin.test/stream"), "s3cret");

        await svc.GetStateAsync("7");
        Assert.Equal("Bearer s3cret", _lastAuth);

        await svc.SetEnabledAsync("7", false);
        Assert.Equal("Bearer s3cret", _lastAuth);
    }
}
