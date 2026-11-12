using Dashboard.Core.Abstractions;
using Dashboard.Infrastructure.Identity;
using Dashboard.Infrastructure.Persistence.V2;
using Dashboard.Infrastructure.Proxy;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Dashboard.Infrastructure.Tests;

/// <summary>P11's Definition of done + must-not-regress list, against the REAL
/// ModelProxyService behind an in-memory TestServer, with a real listening
/// fake-upstream server standing in for vLLM (a real socket is needed since
/// ModelProxyService's own HttpClient dials an absolute URL, unlike
/// TestServer's in-memory transport).</summary>
[Collection("Postgres")]
public class ModelProxyServiceTests(PostgresFixture postgres) : IAsyncDisposable
{
    private IHost? _dashboardHost;
    private IHost? _fakeUpstream;

    /// <summary>The last request body the fake upstream (standing in for vLLM) received --
    /// lets a test assert what the proxy actually forwarded, e.g. a rewritten "model".</summary>
    private string? _lastUpstreamBody;

    public async ValueTask DisposeAsync()
    {
        if (_dashboardHost is not null) await _dashboardHost.StopAsync();
        if (_fakeUpstream is not null) await _fakeUpstream.StopAsync();
    }

    /// <summary>A minimal real HTTP server standing in for vLLM -- serves one
    /// controlled chat/completions SSE response, split into caller-supplied
    /// chunks with a small delay between each so a test can observe the
    /// stream mid-flight.</summary>
    private async Task<string> StartFakeUpstreamAsync(IReadOnlyList<string> sseChunks, TimeSpan delayBetweenChunks)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = builder.Build();
        app.MapPost("/v1/chat/completions", async (HttpContext context) =>
        {
            using (var reader = new StreamReader(context.Request.Body))
                _lastUpstreamBody = await reader.ReadToEndAsync();
            context.Response.ContentType = "text/event-stream";
            foreach (var chunk in sseChunks)
            {
                await context.Response.WriteAsync(chunk);
                await context.Response.Body.FlushAsync();
                if (delayBetweenChunks > TimeSpan.Zero)
                    await Task.Delay(delayBetweenChunks);
            }
        });
        _fakeUpstream = app;
        await app.StartAsync();
        var address = app.Urls.First();
        return address;
    }

    private async Task<(IServiceScopeFactory ScopeFactory, HttpClient Client, string TraffichubUrl)> StartDashboardHostAsync()
    {
        var connectionString = await postgres.CreateFreshDatabaseAsync();

        var hostBuilder = new HostBuilder()
            .ConfigureWebHost(webBuilder =>
            {
                webBuilder.UseTestServer();
                webBuilder.ConfigureServices(services =>
                {
                    services.AddSignalR();
                    services.AddDbContext<AppDbContext>(o => o.UseNpgsql(connectionString));
                    services.AddHttpClient("proxy", c => c.Timeout = Timeout.InfiniteTimeSpan);
                    services.AddSingleton<IReadOnlyGuard, ReadOnlyGuard>();
                    services.AddSingleton<IInstanceService, InstanceService>();
                    services.AddSingleton<IModelService, ModelService>();
                    services.AddSingleton<IRecipeService, RecipeService>();
                    services.AddSingleton<IDeploymentService, DeploymentService>();
                    services.AddSingleton<IInFlightTrafficTracker, InFlightTrafficTracker>();
                    services.AddSingleton<IBroadcastThrottler, Dashboard.Application.Metrics.MetricsBroadcastThrottler>();
                    services.AddSingleton<IMetricsAggregator, Dashboard.Infrastructure.Scrapers.MetricsAggregator>();
                    services.AddSingleton<IMetricsSourceFactory, Dashboard.Infrastructure.Scrapers.MetricsSourceFactory>();
                    services.AddSingleton<IMetricsService, Dashboard.Application.Metrics.MetricsService>();
                    services.AddSingleton<ISampleStore, Dashboard.Infrastructure.Persistence.SampleStore>();
                    services.AddSingleton<IMetricsBroadcaster, Dashboard.Application.Metrics.MetricsBroadcaster>();
                    services.AddSingleton<ILlamaCppLiveMetricsPublisher, LlamaCppLiveMetricsPublisher>();
                    services.AddSingleton<IModelProxyService, ModelProxyService>();
                });
                webBuilder.Configure(app =>
                {
                    app.UseRouting();
                    app.UseEndpoints(endpoints =>
                    {
                        endpoints.MapHub<TrafficHub>("/traffic-hub");
                        endpoints.Map("/proxy/{slug}/{**path}", async (HttpContext context, string slug, string? path, IModelProxyService proxy) =>
                        {
                            await proxy.HandleAsync(context, slug, path ?? "");
                        });
                    });
                });
            });

        _dashboardHost = await hostBuilder.StartAsync();
        await _dashboardHost.Services.GetRequiredService<AppDbContext>().Database.MigrateAsync();

        var testServer = _dashboardHost.GetTestServer();
        var client = testServer.CreateClient();
        return (_dashboardHost.Services.GetRequiredService<IServiceScopeFactory>(), client, "http://localhost/traffic-hub");
    }

    private HubConnection NewTrafficConnection(string url)
    {
        var testServer = _dashboardHost!.GetTestServer();
        return new HubConnectionBuilder()
            .WithUrl(url, o => o.HttpMessageHandlerFactory = _ => testServer.CreateHandler())
            .Build();
    }

    private static async Task<(Server Server, Instance Instance, Model ModelA, Model ModelB)> SeedAsync(
        IServiceScopeFactory scopeFactory, string upstreamAddress)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var upstreamUri = new Uri(upstreamAddress);

        var server = new Server { Label = "primary", ModelsDir = "/models" };
        db.ServersV2.Add(server);
        await db.SaveChangesAsync();

        // AdminUrl's host is what the proxy derives the inference base URL from
        // (same host, instance's own Port) -- the admin port itself is irrelevant
        // here since nothing in this test calls it.
        var instance = new Instance
        {
            ServerId = server.Id, Label = "main", ProxySlug = "main",
            AdminUrl = $"http://{upstreamUri.Host}:1", Port = upstreamUri.Port,
        };
        var modelA = new Model { ServerId = server.Id, Name = "model-a", Path = "/models/hf-cache/model-a" };
        var modelB = new Model { ServerId = server.Id, Name = "model-b", Path = "/models/hf-cache/model-b" };
        db.InstancesV2.Add(instance);
        db.ModelsV2.AddRange(modelA, modelB);
        await db.SaveChangesAsync();

        var recipe = new Recipe { ServerId = server.Id, Name = "default", ServedModelName = "main" };
        // Registered (its own recipe exists) but not deployed/active -- lets
        // RegisteredButNotActiveModel_ReturnsHelpfulError request a served name
        // that's real but not currently forwardable.
        var inactiveRecipe = new Recipe { ServerId = server.Id, Name = "model-b-recipe", ServedModelName = "model-b-served" };
        db.RecipesV2.AddRange(recipe, inactiveRecipe);
        await db.SaveChangesAsync();

        db.DeploymentsV2.Add(new Deployment
        {
            InstanceId = instance.Id, ModelId = modelA.Id, RecipeId = recipe.Id,
            ArgsSnapshot = "[]", Status = "active", StartedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();

        return (server, instance, modelA, modelB);
    }

    [Fact]
    public async Task GetModelsList_ReturnsTheFullRegistry_NotJustTheActiveModel()
    {
        var upstream = await StartFakeUpstreamAsync([], TimeSpan.Zero);
        var (scopeFactory, client, _) = await StartDashboardHostAsync();
        await SeedAsync(scopeFactory, upstream);

        var response = await client.GetAsync("/proxy/main/v1/models");
        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();

        Assert.Contains("model-a", body);
        Assert.Contains("model-b", body); // registered but NOT active -- still listed
    }

    [Fact]
    public async Task Completion_StreamsByteForByteAndAppearsInLiveTraffic_VisibleMidStream()
    {
        var sseChunks = new[] { "data: {\"choices\":[{\"delta\":{\"content\":\"Hel\"}}]}\n\n", "data: {\"choices\":[{\"delta\":{\"content\":\"lo!\"}}]}\n\n", "data: [DONE]\n\n" };
        var upstream = await StartFakeUpstreamAsync(sseChunks, TimeSpan.FromMilliseconds(400));
        var (scopeFactory, client, trafficHubUrl) = await StartDashboardHostAsync();
        var (_, instance, modelA, _) = await SeedAsync(scopeFactory, upstream);

        var connection = NewTrafficConnection(trafficHubUrl);
        string? midStreamStartedModel = null;
        var gotChunk = false;
        connection.On<object>(TrafficHub.StartedEvent, _ => { });
        connection.On<object>(TrafficHub.ChunkEvent, _ => gotChunk = true);
        await connection.StartAsync();

        var requestTask = client.PostAsync("/proxy/main/v1/chat/completions",
            new StringContent("""{"model":"main","messages":[{"role":"user","content":"hi"}],"stream":true}""",
                System.Text.Encoding.UTF8, "application/json"));

        // Join mid-stream (the upstream is still pausing between chunks) --
        // TrafficHub.JoinSource's own catch-up replay must surface the
        // already-started request to a viewer who wasn't connected when it began.
        await Task.Delay(150);
        var replayed = new List<object>();
        connection.On<object>(TrafficHub.StartedEvent, e => replayed.Add(e));
        await connection.InvokeAsync("JoinSource", instance.Id.ToString());
        await Task.Delay(150);
        midStreamStartedModel = replayed.Count > 0 ? "seen" : null;

        var response = await requestTask;
        var fullBody = await response.Content.ReadAsStringAsync();

        Assert.NotNull(midStreamStartedModel); // visible before the request finished
        Assert.True(gotChunk);
        foreach (var chunk in sseChunks)
            Assert.Contains(chunk.TrimEnd('\n'), fullBody.Replace("\r\n", "\n"));

        await connection.DisposeAsync();
    }

    [Fact]
    public async Task RegisteredButNotActiveModel_ReturnsHelpfulError()
    {
        var upstream = await StartFakeUpstreamAsync([], TimeSpan.Zero);
        var (scopeFactory, client, _) = await StartDashboardHostAsync();
        await SeedAsync(scopeFactory, upstream);

        var response = await client.PostAsync("/proxy/main/v1/chat/completions",
            new StringContent("""{"model":"model-b-served","messages":[]}""", System.Text.Encoding.UTF8, "application/json"));

        Assert.Equal(System.Net.HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("model-b-served", body);
        Assert.Contains("registered but not active", body);
        Assert.Contains("swap it in the dashboard", body);
    }

    [Fact]
    public async Task CatalogNameFromModelsList_OfTheActiveModel_IsRewrittenToTheServedName()
    {
        var sseChunks = new[] { "data: {\"choices\":[{\"delta\":{\"content\":\"OK\"}}]}\n\n", "data: [DONE]\n\n" };
        var upstream = await StartFakeUpstreamAsync(sseChunks, TimeSpan.Zero);
        var (scopeFactory, client, _) = await StartDashboardHostAsync();
        await SeedAsync(scopeFactory, upstream);

        // "model-a" is what GET /v1/models lists; the active recipe serves it as "main".
        var response = await client.PostAsync("/proxy/main/v1/chat/completions",
            new StringContent("""{"model":"model-a","messages":[{"role":"user","content":"hi"}],"temperature":0.2,"stream":true}""",
                System.Text.Encoding.UTF8, "application/json"));

        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("OK", await response.Content.ReadAsStringAsync());
        using var forwarded = System.Text.Json.JsonDocument.Parse(_lastUpstreamBody!);
        Assert.Equal("main", forwarded.RootElement.GetProperty("model").GetString());
        // every other field survives the rewrite
        Assert.Equal(0.2, forwarded.RootElement.GetProperty("temperature").GetDouble());
        Assert.True(forwarded.RootElement.GetProperty("stream").GetBoolean());
        Assert.Equal("hi", forwarded.RootElement.GetProperty("messages")[0].GetProperty("content").GetString());
    }

    [Fact]
    public async Task ServedName_StillForwardedUntouched()
    {
        var upstream = await StartFakeUpstreamAsync(["data: [DONE]\n\n"], TimeSpan.Zero);
        var (scopeFactory, client, _) = await StartDashboardHostAsync();
        await SeedAsync(scopeFactory, upstream);

        const string body = """{"model":"main","messages":[]}""";
        var response = await client.PostAsync("/proxy/main/v1/chat/completions",
            new StringContent(body, System.Text.Encoding.UTF8, "application/json"));

        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(body, _lastUpstreamBody); // byte-for-byte, no rewrite
    }

    [Fact]
    public async Task CatalogNameOfAModelThatIsNotActive_ReturnsTheSwapItInTheDashboardError()
    {
        var upstream = await StartFakeUpstreamAsync([], TimeSpan.Zero);
        var (scopeFactory, client, _) = await StartDashboardHostAsync();
        await SeedAsync(scopeFactory, upstream);

        // "model-b" is listed by /v1/models but model-a is the one deployed.
        var response = await client.PostAsync("/proxy/main/v1/chat/completions",
            new StringContent("""{"model":"model-b","messages":[]}""", System.Text.Encoding.UTF8, "application/json"));

        Assert.Equal(System.Net.HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("registered but not active", body);
        Assert.Null(_lastUpstreamBody); // never forwarded
    }

    [Theory]
    [InlineData("""{"model":"model-a","n":1}""", "main")]
    [InlineData("""{"n":1}""", "main")]
    public void RewriteModelField_SetsModelAndKeepsOtherFields(string json, string served)
    {
        var rewritten = ModelProxyService.RewriteModelField(json, served);

        using var doc = System.Text.Json.JsonDocument.Parse(rewritten!);
        Assert.Equal(served, doc.RootElement.GetProperty("model").GetString());
        Assert.Equal(1, doc.RootElement.GetProperty("n").GetInt32());
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[1,2]")]
    public void RewriteModelField_NonObjectBody_ReturnsNull(string json) =>
        Assert.Null(ModelProxyService.RewriteModelField(json, "main"));

    [Fact]
    public async Task UnregisteredModel_Returns404()
    {
        var upstream = await StartFakeUpstreamAsync([], TimeSpan.Zero);
        var (scopeFactory, client, _) = await StartDashboardHostAsync();
        await SeedAsync(scopeFactory, upstream);

        var response = await client.PostAsync("/proxy/main/v1/chat/completions",
            new StringContent("""{"model":"nonexistent","messages":[]}""", System.Text.Encoding.UTF8, "application/json"));

        Assert.Equal(System.Net.HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task AlreadyActiveModel_ForwardsNormally()
    {
        var sseChunks = new[] { "data: {\"choices\":[{\"delta\":{\"content\":\"OK\"}}]}\n\n", "data: [DONE]\n\n" };
        var upstream = await StartFakeUpstreamAsync(sseChunks, TimeSpan.Zero);
        var (scopeFactory, client, _) = await StartDashboardHostAsync();
        await SeedAsync(scopeFactory, upstream);

        var response = await client.PostAsync("/proxy/main/v1/chat/completions",
            new StringContent("""{"model":"main","messages":[]}""", System.Text.Encoding.UTF8, "application/json"));

        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("OK", body);
    }

    [Fact]
    public async Task UnknownSlug_Returns404()
    {
        var (scopeFactory, client, _) = await StartDashboardHostAsync();
        var response = await client.GetAsync("/proxy/nope/v1/models");
        Assert.Equal(System.Net.HttpStatusCode.NotFound, response.StatusCode);
    }

    /// <summary>P12: "needs supervisor registration" is a reconcile banner, not
    /// a traffic outage -- an instance with NO admin_url at all (never had a
    /// supervisor registered) must still forward using its own <see cref="Instance.Host"/>.</summary>
    [Fact]
    public async Task NoAdminUrlButHostSet_StillForwardsNormally()
    {
        var sseChunks = new[] { "data: {\"choices\":[{\"delta\":{\"content\":\"OK\"}}]}\n\n", "data: [DONE]\n\n" };
        var upstream = await StartFakeUpstreamAsync(sseChunks, TimeSpan.Zero);
        var (scopeFactory, client, _) = await StartDashboardHostAsync();
        var upstreamUri = new Uri(upstream);

        using (var scope = scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var server = new Server { Label = "primary", ModelsDir = "/models" };
            db.ServersV2.Add(server);
            await db.SaveChangesAsync();

            var instance = new Instance
            {
                ServerId = server.Id, Label = "main", ProxySlug = "main",
                AdminUrl = null, Host = upstreamUri.Host, Port = upstreamUri.Port,
            };
            var modelA = new Model { ServerId = server.Id, Name = "model-a", Path = "/models/hf-cache/model-a" };
            db.InstancesV2.Add(instance);
            db.ModelsV2.Add(modelA);
            await db.SaveChangesAsync();

            var recipe = new Recipe { ServerId = server.Id, Name = "default", ServedModelName = "main" };
            db.RecipesV2.Add(recipe);
            await db.SaveChangesAsync();

            db.DeploymentsV2.Add(new Deployment
            {
                InstanceId = instance.Id, ModelId = modelA.Id, RecipeId = recipe.Id,
                ArgsSnapshot = "[]", Status = "active", StartedAt = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        var response = await client.PostAsync("/proxy/main/v1/chat/completions",
            new StringContent("""{"model":"main","messages":[]}""", System.Text.Encoding.UTF8, "application/json"));

        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("OK", body);
    }

    /// <summary>Neither Host nor AdminUrl set -- genuinely nothing to forward
    /// to, distinct from "needs supervisor registration" (which is still a
    /// servable instance once Host is set).</summary>
    [Fact]
    public async Task NoHostAndNoAdminUrl_Returns502()
    {
        var (scopeFactory, client, _) = await StartDashboardHostAsync();
        using (var scope = scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var server = new Server { Label = "primary", ModelsDir = "/models" };
            db.ServersV2.Add(server);
            await db.SaveChangesAsync();
            db.InstancesV2.Add(new Instance { ServerId = server.Id, Label = "main", ProxySlug = "main", AdminUrl = null, Host = null });
            await db.SaveChangesAsync();
        }

        var response = await client.GetAsync("/proxy/main/v1/models");
        Assert.Equal(System.Net.HttpStatusCode.BadGateway, response.StatusCode);
    }
}
