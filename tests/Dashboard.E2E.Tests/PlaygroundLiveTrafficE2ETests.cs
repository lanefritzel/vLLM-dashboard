using System.Net;
using System.Net.Sockets;
using Dashboard.Infrastructure.Persistence.V2;
using Dashboard.Infrastructure.Tests;
using DotNet.Testcontainers.Containers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Playwright;

namespace Dashboard.E2E.Tests;

/// <summary>P15a's DoD bullets that need an actually-reachable fake vLLM, not
/// just the admin API: Playground streaming a real completion with real
/// TTFT/tok-s, Live Traffic showing a real card, and the Instances page's
/// clipboard affordances. The fake vLLM defaults to binding 127.0.0.1 inside
/// the container (tests/fake-vllm/fake_vllm.py), which a host-mapped Docker
/// port can't reach -- fixed here with a `{"key":"host","value":"0.0.0.0"}`
/// recipe extra-arg (the same passthrough mechanism `fake-crash`/
/// `fake-slow-start` already use), not by changing <c>ArgBuilder</c>, since
/// binding 0.0.0.0 by default would be a real behavior change, not a test
/// concern.</summary>
[Collection("E2EWithSupervisor")]
public class PlaygroundLiveTrafficE2ETests : IAsyncDisposable
{
    private readonly DashboardAppFixture _fixture;
    private readonly SupervisorImageFixture _supervisorImage;
    private IContainer? _container;

    public PlaygroundLiveTrafficE2ETests(DashboardAppFixture fixture, SupervisorImageFixture supervisorImage)
    {
        _fixture = fixture;
        _supervisorImage = supervisorImage;
    }

    public async ValueTask DisposeAsync()
    {
        if (_container is not null)
            await _container.DisposeAsync();
    }

    private async Task<(Instance Instance, Model Model, Recipe Recipe)> SeedAndDeployAsync()
    {
        var inferencePort = GetFreeTcpPort();
        _container = await _supervisorImage.StartContainerAsync(inferencePort: inferencePort);
        var adminUrl = SupervisorImageFixture.AdminUrl(_container);

        using var db = _fixture.NewDbContext();
        await db.DeploymentsV2.ExecuteDeleteAsync();
        await db.RecipesV2.ExecuteDeleteAsync();
        await db.ModelsV2.ExecuteDeleteAsync();
        await db.InstancesV2.ExecuteDeleteAsync();
        await db.ServersV2.ExecuteDeleteAsync();

        var server = new Server { Label = $"srv-{Guid.NewGuid():N}", ModelsDir = "/models", GpuMemGb = 80 };
        db.ServersV2.Add(server);
        await db.SaveChangesAsync();

        var instance = new Instance
        {
            ServerId = server.Id, Label = "primary", ProxySlug = $"slug-{Guid.NewGuid():N}",
            AdminUrl = adminUrl, Host = "127.0.0.1", Port = inferencePort,
        };
        db.InstancesV2.Add(instance);

        var model = new Model { ServerId = server.Id, Name = "fake-model", Path = "/models/hf-cache/fake-model", SourceType = "local", LoadFormat = "auto" };
        db.ModelsV2.Add(model);
        await db.SaveChangesAsync();

        var recipe = new Recipe
        {
            ServerId = server.Id, Name = "recipe-reachable", ServedModelName = "main",
            ExtraArgs = """[{"key":"host","value":"0.0.0.0"}]""",
        };
        db.RecipesV2.Add(recipe);
        await db.SaveChangesAsync();

        return (instance, model, recipe);
    }

    /// <summary>Drives a real swap through the UI to get the fake vLLM actually
    /// running and reachable -- same flow <c>SwapCeremonyE2ETests</c> uses.</summary>
    private static async Task CompleteASwapAsync(IPage page, string baseUrl, string modelLabel, string recipeLabel)
    {
        await page.GotoAsync(baseUrl + "/", new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });
        var dialog = page.Locator(".modal-dialog-custom");
        await page.ClickAsync("button:has-text('Pick a recipe')");
        await dialog.Locator("select >> nth=0").SelectOptionAsync(new SelectOptionValue { Label = modelLabel });
        await dialog.Locator("select >> nth=1").SelectOptionAsync(new SelectOptionValue { Label = recipeLabel });
        await dialog.Locator("button:has-text('Preview')").ClickAsync();
        await dialog.Locator("button:has-text('Swap now')").ClickAsync();
        await dialog.Locator(".alert-success").WaitForAsync(new LocatorWaitForOptions { Timeout = 60000 });
        await dialog.Locator("button:has-text('Done')").ClickAsync();
        await dialog.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Hidden });
    }

    [Fact]
    public async Task Playground_StreamsARealCompletion_AndShowsTimingAndRawToggle()
    {
        var (instance, model, recipe) = await SeedAndDeployAsync();

        var page = await _fixture.NewLoggedInPageAsync();
        await CompleteASwapAsync(page, _fixture.BaseUrl, model.Name, recipe.Name);

        await page.GotoAsync(_fixture.BaseUrl + "/playground", new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });
        await page.ClickAsync("button:has-text('Send')");

        var responsePre = page.Locator("pre.traffic-response");
        try
        {
            await WaitUntilAsync(async () => (await responsePre.InnerTextAsync()).Trim() is var t && t.Length > 0 && t != "…", TimeSpan.FromSeconds(30));
        }
        catch (Exception)
        {
            using var db = _fixture.NewDbContext();
            var deployment = await db.DeploymentsV2.OrderByDescending(d => d.Id).FirstOrDefaultAsync(d => d.InstanceId == instance.Id);
            string directHealth;
            try
            {
                using var http = new HttpClient();
                directHealth = await http.GetStringAsync($"http://127.0.0.1:{instance.Port}/health");
            }
            catch (Exception ex) { directHealth = "direct call failed: " + ex.Message; }

            string proxyError;
            try
            {
                using var http = new HttpClient();
                var resp = await http.PostAsync(
                    $"{_fixture.BaseUrl}/proxy/{instance.ProxySlug}/v1/chat/completions",
                    new StringContent($$"""{"model":"{{recipe.ServedModelName}}","messages":[{"role":"user","content":"hi"}],"stream":false}""", System.Text.Encoding.UTF8, "application/json"));
                proxyError = $"{(int)resp.StatusCode}: {await resp.Content.ReadAsStringAsync()}";
            }
            catch (Exception ex) { proxyError = "proxy call failed: " + ex.Message; }

            var bodyAtFailure = await page.Locator("body").InnerTextAsync();
            throw new Exception(
                $"No response text appeared. ArgsSnapshot=[{deployment?.ArgsSnapshot}] directHealth=[{directHealth}] proxyCall=[{proxyError}] pageBody=[{bodyAtFailure}]");
        }

        var pageText = await page.ContentAsync();
        Assert.DoesNotContain("TTFT —", pageText);

        await page.ClickAsync("summary:has-text('Raw request')");
        await page.ClickAsync("summary:has-text('Raw response')");
        var bodyText = await page.Locator("body").InnerTextAsync();
        Assert.Contains("\"stream\": true", bodyText);
        Assert.Contains("data:", bodyText);
    }

    [Fact]
    public async Task LiveTraffic_ShowsARealCard_AndTheTextFilterNarrowsIt()
    {
        var (instance, model, recipe) = await SeedAndDeployAsync();

        var page = await _fixture.NewLoggedInPageAsync();
        await CompleteASwapAsync(page, _fixture.BaseUrl, model.Name, recipe.Name);

        await page.GotoAsync(_fixture.BaseUrl + "/traffic", new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });

        // Fire a request through the real proxy from a second page while Live
        // Traffic is already joined to the hub -- mirrors a real external client.
        var secondPage = await _fixture.NewLoggedInPageAsync();
        var proxyUrl = $"{_fixture.BaseUrl}/proxy/{instance.ProxySlug}/v1/chat/completions";
        _ = secondPage.APIRequest.PostAsync(proxyUrl, new APIRequestContextOptions
        {
            DataObject = new { model = recipe.ServedModelName, messages = new[] { new { role = "user", content = "unique-marker-xyz" } }, stream = false },
        });

        await page.Locator(".traffic-card").First.WaitForAsync(new LocatorWaitForOptions { Timeout = 30000 });
        await page.Locator("text=unique-marker-xyz").WaitForAsync(new LocatorWaitForOptions { Timeout = 10000 });

        await page.FillAsync("input[placeholder='Filter by message/response text…']", "unique-marker-xyz");
        Assert.Equal(1, await page.Locator(".traffic-card").CountAsync());

        await page.FillAsync("input[placeholder='Filter by message/response text…']", "text-that-never-appears");
        await WaitUntilAsync(async () => await page.Locator(".traffic-card").CountAsync() == 0, TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Instances_CopyButtonPutsTheProxyUrlOnTheClipboard_AndSnippetHasTheActiveServedName()
    {
        var (instance, model, recipe) = await SeedAndDeployAsync();

        var context = await _fixture.Browser.NewContextAsync();
        await context.GrantPermissionsAsync(new[] { "clipboard-read", "clipboard-write" });
        var page = await context.NewPageAsync();
        await page.GotoAsync(_fixture.BaseUrl + "/Account/Login", new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });
        await page.FillAsync("input[name=\"Input.Email\"]", DashboardAppFixture.TestUserEmail);
        await page.FillAsync("input[name=\"Input.Password\"]", DashboardAppFixture.TestUserPassword);
        await page.ClickAsync("button[type=\"submit\"]");
        await page.WaitForLoadStateAsync(LoadState.NetworkIdle);

        await CompleteASwapAsync(page, _fixture.BaseUrl, model.Name, recipe.Name);

        await page.GotoAsync(_fixture.BaseUrl + "/instances", new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });
        var expectedUrl = await page.Locator("td code").First.InnerTextAsync();

        await page.ClickAsync("button:has-text('Copy')");
        var clipboard = await page.EvaluateAsync<string>("navigator.clipboard.readText()");
        Assert.Equal(expectedUrl, clipboard);

        await page.ClickAsync("button:has-text('Copy client snippet')");
        var snippetClipboard = await page.EvaluateAsync<string>("navigator.clipboard.readText()");
        Assert.Contains($"model = \"{recipe.ServedModelName}\"", snippetClipboard);
    }

    [Fact]
    public async Task Instances_ShowsNeedsSupervisorRegistration_WhenAdminUrlIsNull()
    {
        using var db = _fixture.NewDbContext();
        await db.DeploymentsV2.ExecuteDeleteAsync();
        await db.RecipesV2.ExecuteDeleteAsync();
        await db.ModelsV2.ExecuteDeleteAsync();
        await db.InstancesV2.ExecuteDeleteAsync();
        await db.ServersV2.ExecuteDeleteAsync();

        var server = new Server { Label = $"srv-{Guid.NewGuid():N}", ModelsDir = "/models", GpuMemGb = 80 };
        db.ServersV2.Add(server);
        await db.SaveChangesAsync();
        db.InstancesV2.Add(new Instance { ServerId = server.Id, Label = "unregistered", ProxySlug = $"slug-{Guid.NewGuid():N}" });
        await db.SaveChangesAsync();

        var page = await _fixture.NewLoggedInPageAsync();
        await page.GotoAsync(_fixture.BaseUrl + "/instances", new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });
        await page.Locator("text=needs supervisor registration").WaitForAsync();
    }

    /// <summary>Same pattern as DashboardAppFixture.GetFreeTcpPort -- a fresh
    /// ephemeral port per test, since a port fixed across tests collided when
    /// a previous container hadn't fully released it yet.</summary>
    private static int GetFreeTcpPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (await condition())
                return;
            await Task.Delay(200);
        }
        Assert.True(await condition(), $"Condition did not become true within {timeout}.");
    }
}
