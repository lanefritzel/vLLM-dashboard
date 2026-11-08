using System.Collections.Concurrent;
using Dashboard.Infrastructure.Persistence.V2;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Playwright;

namespace Dashboard.E2E.Tests;

/// <summary>The Overview "Neuron map" section end to end: the real dashboard in a real
/// browser, fed by a stand-in for the vLLM visualizer plugin that streams the 12 snapshots
/// recorded from the Intel GPU run. No GPU, no vLLM.</summary>
[Collection("E2E")]
public class NeuronMapE2ETests
{
    private static readonly string[] Frames = File.ReadAllLines(
        Path.Combine(AppContext.BaseDirectory, "real_snapshots.jsonl")).Where(l => l.Length > 0).ToArray();

    private readonly DashboardAppFixture _fixture;

    public NeuronMapE2ETests(DashboardAppFixture fixture) => _fixture = fixture;

    private async Task SeedInstanceAsync()
    {
        using var db = _fixture.NewDbContext();
        await db.DeploymentsV2.ExecuteDeleteAsync();
        await db.RecipesV2.ExecuteDeleteAsync();
        await db.ModelsV2.ExecuteDeleteAsync();
        await db.InstancesV2.ExecuteDeleteAsync();
        await db.ServersV2.ExecuteDeleteAsync();

        var server = new Server { Label = $"srv-{Guid.NewGuid():N}", ModelsDir = "/models", GpuMemGb = 32 };
        db.ServersV2.Add(server);
        await db.SaveChangesAsync();
        db.InstancesV2.Add(new Instance
        {
            ServerId = server.Id, Label = "primary", ProxySlug = $"slug-{Guid.NewGuid():N}",
            AdminUrl = "http://127.0.0.1:9/admin", Host = "127.0.0.1", Port = 9,
        });
        await db.SaveChangesAsync();
    }

    /// <summary>Stand-in plugin: streams the recorded frames, then keepalives; counts open streams.</summary>
    private sealed class StubPlugin : IAsyncDisposable
    {
        private readonly WebApplication _app;
        public int OpenStreams;
        public volatile bool Disabled;
        public volatile bool PendingDisable;
        public readonly ConcurrentQueue<string> Posts = new();

        public StubPlugin(int port)
        {
            var b = WebApplication.CreateBuilder();
            b.WebHost.UseUrls($"http://127.0.0.1:{port}");
            _app = b.Build();
            _app.MapGet("/stream", async (HttpContext ctx) =>
            {
                Interlocked.Increment(ref OpenStreams);
                try
                {
                    ctx.Response.ContentType = "text/event-stream";
                    var i = 0;
                    while (!ctx.RequestAborted.IsCancellationRequested)
                    {
                        await ctx.Response.WriteAsync("data: " + Frames[i++ % Frames.Length] + "\n\n", ctx.RequestAborted);
                        await ctx.Response.Body.FlushAsync(ctx.RequestAborted);
                        await Task.Delay(120, ctx.RequestAborted);
                    }
                }
                catch (OperationCanceledException) { }
                finally { Interlocked.Decrement(ref OpenStreams); }
            });
        }

        private object Health() => new
        {
            disabled = Disabled, disabled_reason = Disabled ? "manual" : "",
            pending_disable = PendingDisable, pending_enable = false,
        };

        public void MapControl()
        {
            _app.MapGet("/health", () => Results.Json(Health()));
            _app.MapPost("/disable", (HttpContext c) =>
            {
                Posts.Enqueue("/disable" + c.Request.QueryString);
                if (c.Request.Query["now"] == "1") { Disabled = true; PendingDisable = false; }
                else PendingDisable = true;   // like the real plugin: waits for the answer to finish
                return Results.Json(Health());
            });
            _app.MapPost("/enable", (HttpContext c) =>
            {
                Posts.Enqueue("/enable" + c.Request.QueryString);
                Disabled = false; PendingDisable = false;
                return Results.Json(Health());
            });
        }

        public Task StartAsync() => _app.StartAsync();
        public async ValueTask DisposeAsync() => await _app.DisposeAsync();
    }

    private static async Task WaitUntil(Func<bool> cond, int ms = 15000)
    {
        var end = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < end) { if (cond()) return; await Task.Delay(100); }
        Assert.True(cond(), "condition not met in time");
    }

    [Fact]
    public async Task Overview_ShowsLiveNeuronMap_AndHidingItClosesThePluginStream()
    {
        await SeedInstanceAsync();
        await using var plugin = new StubPlugin(_fixture.VisualizerPort);
        await plugin.StartAsync();

        var page = await _fixture.NewLoggedInPageAsync();
        await page.GotoAsync(_fixture.BaseUrl + "/", new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });

        // Hidden by default: nothing is streamed from the plugin until someone asks.
        await Assertions.Expect(page.GetByRole(AriaRole.Button, new() { Name = "Show neuron map" })).ToBeVisibleAsync();
        Assert.Equal(0, Volatile.Read(ref plugin.OpenStreams));

        await page.GetByRole(AriaRole.Button, new() { Name = "Show neuron map" }).ClickAsync();

        await Assertions.Expect(page.Locator(".nm-canvas")).ToBeVisibleAsync();
        await Assertions.Expect(page.Locator(".nm-toolbar .ui-chip, .nm-toolbar [class*=chip]").First).ToContainTextAsync("live", new() { Timeout = 15000 });
        // Frames are reaching the canvas: the stats line only fills in once snapshots were drawn.
        await Assertions.Expect(page.Locator(".nm-stat")).ToContainTextAsync("updates/s", new() { Timeout = 15000 });
        Assert.Equal(1, Volatile.Read(ref plugin.OpenStreams));

        // Optional: NM_SHOT=/some/path.png saves a picture of the section for eyeballing.
        if (Environment.GetEnvironmentVariable("NM_SHOT") is { Length: > 0 } shot)
        {
            await Task.Delay(800);
            await page.ScreenshotAsync(new() { Path = shot, FullPage = true });
        }

        // The canvas actually has pixels from the data (not just the empty background).
        var litPixels = await page.EvaluateAsync<int>(@"() => {
            const c = document.querySelector('.nm-canvas'); const ctx = c.getContext('2d');
            const d = ctx.getImageData(0, 0, c.width, c.height).data; let n = 0;
            for (let i = 0; i < d.length; i += 4) if (d[i] > 120 && d[i+1] > 120 && d[i+2] > 140) n++;
            return n; }");
        Assert.True(litPixels > 50, $"canvas looks empty ({litPixels} bright pixels)");

        await page.GetByRole(AriaRole.Button, new() { Name = "Hide neuron map" }).ClickAsync();
        await WaitUntil(() => Volatile.Read(ref plugin.OpenStreams) == 0);   // plugin can go back to sleep

        await page.Context.CloseAsync();
    }

    [Fact]
    public async Task Overview_PluginSwitch_TurnsOffAfterTheAnswer_AndBackOn_AndRemembersTheChoice()
    {
        await SeedInstanceAsync();
        await using var plugin = new StubPlugin(_fixture.VisualizerPort);
        plugin.MapControl();
        await plugin.StartAsync();

        var page = await _fixture.NewLoggedInPageAsync();
        await page.GotoAsync(_fixture.BaseUrl + "/", new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });
        await page.GetByRole(AriaRole.Button, new() { Name = "Show neuron map" }).ClickAsync();
        var toolbar = page.Locator(".nm-toolbar");
        await Assertions.Expect(page.GetByText("Plugin off", new() { Exact = true })).ToBeVisibleAsync(new() { Timeout = 15000 });

        // Off is graceful: the plugin is told to wait for the current answer, and the page says so.
        await page.GetByText("Plugin off", new() { Exact = true }).ClickAsync();
        await WaitUntil(() => plugin.Posts.Contains("/disable"));
        Assert.DoesNotContain("/disable?now=1", plugin.Posts);
        await Assertions.Expect(toolbar).ToContainTextAsync("turning off after this answer", new() { Timeout = 8000 });
        await Assertions.Expect(page.GetByRole(AriaRole.Button, new() { Name = "Turn off now" })).ToBeVisibleAsync();

        // The model goes quiet: the plugin finishes switching off and the page follows on its next check.
        plugin.Disabled = true; plugin.PendingDisable = false;
        await Assertions.Expect(toolbar).ToContainTextAsync("plugin off", new() { Timeout = 10000 });

        // Back on.
        await page.GetByText("Plugin on", new() { Exact = true }).ClickAsync();
        await WaitUntil(() => plugin.Posts.Contains("/enable"));
        await Assertions.Expect(toolbar).Not.ToContainTextAsync("plugin off", new() { Timeout = 10000 });
        await Assertions.Expect(toolbar).ToContainTextAsync("live", new() { Timeout = 10000 });

        // The choice is remembered per instance (so it can be re-applied after vLLM restarts).
        using var db = _fixture.NewDbContext();
        var saved = await db.Configs.AsNoTracking().Where(c => c.Key.StartsWith("visualizer.desired.")).ToListAsync();
        Assert.Single(saved);
        Assert.Equal("on", saved[0].Value);

        await page.Context.CloseAsync();
    }

    /// <summary>Average red/green/blue of the bright pixels on the map canvas.</summary>
    private static async Task<(double R, double G, double B, int Lit)> CanvasTintAsync(IPage page)
    {
        var j = await page.EvaluateAsync<System.Text.Json.JsonElement>(@"() => {
            const c = document.querySelector('.nm-canvas'); const d = c.getContext('2d').getImageData(0, 0, c.width, c.height).data;
            let r = 0, g = 0, b = 0, n = 0;
            for (let i = 0; i < d.length; i += 4) { if (d[i] + d[i+1] + d[i+2] > 330) { r += d[i]; g += d[i+1]; b += d[i+2]; n++; } }
            return n ? { r: r/n, g: g/n, b: b/n, lit: n } : { r: 0, g: 0, b: 0, lit: 0 }; }");
        return (j.GetProperty("r").GetDouble(), j.GetProperty("g").GetDouble(), j.GetProperty("b").GetDouble(), j.GetProperty("lit").GetInt32());
    }

    [Fact]
    public async Task Overview_PalettePicker_ChangesTheColors_AndRemembersTheChoice()
    {
        await SeedInstanceAsync();
        await using var plugin = new StubPlugin(_fixture.VisualizerPort);
        plugin.MapControl();
        await plugin.StartAsync();

        var page = await _fixture.NewLoggedInPageAsync();
        await page.GotoAsync(_fixture.BaseUrl + "/", new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });
        await page.GetByRole(AriaRole.Button, new() { Name = "Show neuron map" }).ClickAsync();
        await Assertions.Expect(page.Locator(".nm-stat")).ToContainTextAsync("updates/s", new() { Timeout = 15000 });

        // The picker lists many looks, each with a swatch, and starts on Heat.
        await Assertions.Expect(page.GetByRole(AriaRole.Button, new() { Name = "Palette: Heat" })).ToBeVisibleAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "Palette: Heat" }).ClickAsync();
        Assert.True(await page.Locator(".nm-pal").CountAsync() >= 14);
        Assert.Equal(await page.Locator(".nm-pal").CountAsync(), await page.Locator(".nm-pal-bar").CountAsync());
        if (Environment.GetEnvironmentVariable("NM_SHOT_PICKER") is { Length: > 0 } shot)
        {
            await Task.Delay(800);
            await page.ScreenshotAsync(new() { Path = shot, FullPage = true });
        }

        // Ocean is blue-dominant, Matrix is green-dominant: the canvas really changed.
        await page.Locator(".nm-pal", new() { HasText = "Ocean" }).ClickAsync();
        await Assertions.Expect(page.GetByRole(AriaRole.Button, new() { Name = "Palette: Ocean" })).ToBeVisibleAsync();
        await Assertions.Expect(page.Locator(".nm-pal")).ToHaveCountAsync(0);   // closes after picking
        await Task.Delay(700);
        var ocean = await CanvasTintAsync(page);
        Assert.True(ocean.Lit > 50, "ocean canvas looks empty");
        Assert.True(ocean.B > ocean.R, $"ocean should lean blue (r={ocean.R:0} b={ocean.B:0})");

        await page.GetByRole(AriaRole.Button, new() { Name = "Palette: Ocean" }).ClickAsync();
        await page.Locator(".nm-pal", new() { HasText = "Matrix" }).ClickAsync();
        await Task.Delay(700);
        var matrix = await CanvasTintAsync(page);
        Assert.True(matrix.G > matrix.R && matrix.G > matrix.B, $"matrix should lean green (r={matrix.R:0} g={matrix.G:0} b={matrix.B:0})");

        // Reload: the choice sticks.
        await page.ReloadAsync(new() { WaitUntil = WaitUntilState.NetworkIdle });
        await Assertions.Expect(page.GetByRole(AriaRole.Button, new() { Name = "Palette: Matrix" })).ToBeVisibleAsync(new() { Timeout = 15000 });

        await page.Context.CloseAsync();
    }

    [Fact]
    public async Task Overview_ExplainsWhenThePluginIsNotReachable_WithoutBreakingThePage()
    {
        await SeedInstanceAsync();   // nothing listens on the plugin port

        var page = await _fixture.NewLoggedInPageAsync();
        await page.GotoAsync(_fixture.BaseUrl + "/", new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });
        await page.GetByRole(AriaRole.Button, new() { Name = "Show neuron map" }).ClickAsync();

        await Assertions.Expect(page.GetByText("No neuron-map feed from this instance")).ToBeVisibleAsync(new() { Timeout = 15000 });
        // The rest of Overview is still there.
        await Assertions.Expect(page.GetByText("Live metrics")).ToBeVisibleAsync();

        await page.Context.CloseAsync();
    }
}
