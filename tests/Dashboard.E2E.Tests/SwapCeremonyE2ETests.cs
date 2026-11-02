using Dashboard.Infrastructure.Persistence.V2;
using Dashboard.Infrastructure.Tests;
using DotNet.Testcontainers.Containers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Playwright;

namespace Dashboard.E2E.Tests;

/// <summary>P14's own DoD bullets that specifically need a REAL supervisor
/// container (not a C# stand-in): "progress steps appear as events arrive",
/// "the result screen shows the new model", and the fake-crash failure path
/// persisting across a reload. Reuses <see cref="SupervisorImageFixture"/>
/// from Dashboard.Infrastructure.Tests (P10a/P10b's own real-supervisor test
/// image) rather than duplicating it. A separate collection from the plain
/// "E2E" one (see ModelsRecipesE2ETests) -- mixing supervisor-container tests
/// into every E2E run would slow down tests that don't need one at all.</summary>
[Collection("E2EWithSupervisor")]
public class SwapCeremonyE2ETests : IAsyncDisposable
{
    private readonly DashboardAppFixture _fixture;
    private readonly SupervisorImageFixture _supervisorImage;
    private IContainer? _container;

    public SwapCeremonyE2ETests(DashboardAppFixture fixture, SupervisorImageFixture supervisorImage)
    {
        _fixture = fixture;
        _supervisorImage = supervisorImage;
    }

    public async ValueTask DisposeAsync()
    {
        if (_container is not null)
            await _container.DisposeAsync();
    }

    /// <summary>Wipes every V2 table first -- see ModelsRecipesE2ETests for why
    /// (the page always binds to the alphabetically-first server, and this
    /// fixture's Postgres is shared for the whole collection). <paramref
    /// name="extraEnv"/> is forwarded to the container (P14: GPU_BACKEND=fake
    /// for the Stop/VRAM tests).</summary>
    private async Task<SeedResult> SeedAsync(IReadOnlyDictionary<string, string>? extraEnv = null)
    {
        _container = await _supervisorImage.StartContainerAsync(extraEnv);
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
            AdminUrl = adminUrl, Port = 9999,
        };
        db.InstancesV2.Add(instance);

        var model1 = new Model { ServerId = server.Id, Name = "fake-model", Path = "/models/hf-cache/fake-model", SourceType = "local", LoadFormat = "auto" };
        var model2 = new Model { ServerId = server.Id, Name = "fake-model-2", Path = "/models/hf-cache/fake-model-2", SourceType = "local", LoadFormat = "auto" };
        db.ModelsV2.AddRange(model1, model2);
        await db.SaveChangesAsync();

        var recipe1 = new Recipe { ServerId = server.Id, Name = "recipe-ok", ServedModelName = "main" };
        var crashRecipe = new Recipe
        {
            ServerId = server.Id, Name = "recipe-crash", ServedModelName = "main",
            ExtraArgs = """[{"key":"fake-crash","value":null}]""",
        };
        var otherServedNameRecipe = new Recipe { ServerId = server.Id, Name = "recipe-other-served", ServedModelName = "other" };
        var slowRecipe = new Recipe
        {
            ServerId = server.Id, Name = "recipe-slow", ServedModelName = "main",
            ExtraArgs = """[{"key":"fake-slow-start","value":"8"}]""",
        };
        db.RecipesV2.AddRange(recipe1, crashRecipe, otherServedNameRecipe, slowRecipe);
        await db.SaveChangesAsync();

        return new SeedResult(server, instance, model1, model2, recipe1, crashRecipe, otherServedNameRecipe, slowRecipe);
    }

    private sealed record SeedResult(
        Server Server, Instance Instance, Model Model1, Model Model2,
        Recipe Recipe1, Recipe CrashRecipe, Recipe OtherServedNameRecipe, Recipe SlowRecipe);

    /// <summary>Drives the picker -> preview -> "Swap now" sequence, leaving the
    /// dialog in Progress phase -- the setup every test after the first two shares.</summary>
    private static async Task OpenAndStartSwapAsync(IPage page, ILocator dialog, string modelLabel, string recipeLabel, string openButtonText = "Pick a recipe")
    {
        await page.ClickAsync($"button:has-text('{openButtonText}')");
        await dialog.Locator("select >> nth=0").SelectOptionAsync(new SelectOptionValue { Label = modelLabel });
        await dialog.Locator("select >> nth=1").SelectOptionAsync(new SelectOptionValue { Label = recipeLabel });
        await dialog.Locator("button:has-text('Preview')").ClickAsync();
        await dialog.Locator("text=Review changes").WaitForAsync();
    }

    private async Task<Deployment> SeedActiveDeploymentAsync(Instance instance, Model model, Recipe recipe)
    {
        using var db = _fixture.NewDbContext();
        var deployment = new Deployment
        {
            InstanceId = instance.Id, ModelId = model.Id, RecipeId = recipe.Id,
            ArgsSnapshot = $"""["vllm","serve","{model.Name}"]""", Status = "active",
            StartedAt = DateTime.UtcNow.AddMinutes(-5), CreatedAt = DateTime.UtcNow.AddMinutes(-5),
        };
        db.DeploymentsV2.Add(deployment);
        await db.SaveChangesAsync();
        return deployment;
    }

    [Fact]
    public async Task FullSwapThroughTheUi_ShowsLiveProgress_AndResultShowsTheNewModel()
    {
        var (_, _, model1, _, recipe1, _, _, _) = await SeedAsync();

        var page = await _fixture.NewLoggedInPageAsync();
        await page.GotoAsync(_fixture.BaseUrl + "/", new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });

        await page.ClickAsync("button:has-text('Pick a recipe')");
        var dialog = page.Locator(".modal-dialog-custom");
        await dialog.Locator("text=Swap model").WaitForAsync();

        await dialog.Locator("select >> nth=0").SelectOptionAsync(new SelectOptionValue { Label = model1.Name });
        await dialog.Locator("select >> nth=1").SelectOptionAsync(new SelectOptionValue { Label = recipe1.Name });
        await dialog.Locator("button:has-text('Preview')").ClickAsync();
        await dialog.Locator("text=Review changes").WaitForAsync();
        await dialog.Locator("button:has-text('Swap now')").ClickAsync();

        await dialog.Locator("text=Swapping…").WaitForAsync();
        await dialog.Locator(".ceremony-steps").WaitForAsync();

        // Progress steps appear as events arrive -- at least one step should
        // reach "done" before the terminal result shows up.
        await dialog.Locator(".step-done, .step-current").First.WaitForAsync(new LocatorWaitForOptions { Timeout = 30000 });

        try
        {
            await dialog.Locator(".alert-success").WaitForAsync(new LocatorWaitForOptions { Timeout = 60000 });
        }
        catch (TimeoutException)
        {
            var dialogText = await dialog.InnerTextAsync();
            using var db = _fixture.NewDbContext();
            var deployments = await db.DeploymentsV2.OrderByDescending(d => d.Id).Take(3).ToListAsync();
            var dump = string.Join("; ", deployments.Select(d =>
                $"id={d.Id} status={d.Status} failure={d.FailureReason} exit={d.ExitCode} started={d.StartedAt} stopped={d.StoppedAt}"));
            throw new Exception($"Timed out waiting for .alert-success. Dialog text: [{dialogText}]. DB rows: [{dump}]");
        }

        var resultText = await dialog.Locator(".alert-success").InnerTextAsync();
        Assert.Contains(model1.Name, resultText);

        await dialog.Locator("button:has-text('Done')").ClickAsync();
        await dialog.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Hidden });

        await page.Locator($"text=Serving: {model1.Name}").WaitForAsync();
    }

    [Fact]
    public async Task FakeCrashSwap_ShowsFailureReasonAndExitCode_AndPersistsAfterReload()
    {
        var (_, instance, model1, _, _, crashRecipe, _, _) = await SeedAsync();

        var page = await _fixture.NewLoggedInPageAsync();
        await page.GotoAsync(_fixture.BaseUrl + "/", new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });

        await page.ClickAsync("button:has-text('Pick a recipe')");
        var dialog = page.Locator(".modal-dialog-custom");
        await dialog.Locator("select >> nth=0").SelectOptionAsync(new SelectOptionValue { Label = model1.Name });
        await dialog.Locator("select >> nth=1").SelectOptionAsync(new SelectOptionValue { Label = crashRecipe.Name });
        await dialog.Locator("button:has-text('Preview')").ClickAsync();
        await dialog.Locator("button:has-text('Swap now')").ClickAsync();

        await dialog.Locator(".alert-danger:has-text('Swap failed')").WaitForAsync(new LocatorWaitForOptions { Timeout = 30000 });
        var failureText = await dialog.Locator(".alert-danger").InnerTextAsync();
        Assert.Contains("code", failureText);

        await dialog.Locator("button:has-text('Done')").ClickAsync();
        await dialog.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Hidden });

        await page.GotoAsync(_fixture.BaseUrl + "/deployments", new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });
        await page.Locator("tr:has-text('recipe-crash')").WaitForAsync();
        var rowText = await page.Locator("tr:has-text('recipe-crash')").InnerTextAsync();
        Assert.Contains("failed", rowText);
    }

    [Fact]
    public async Task ServedNameChange_ShowsAcknowledgePanel_AndGatesSwapNowUntilChecked()
    {
        var seed = await SeedAsync();
        await SeedActiveDeploymentAsync(seed.Instance, seed.Model1, seed.Recipe1);

        var page = await _fixture.NewLoggedInPageAsync();
        await page.GotoAsync(_fixture.BaseUrl + "/", new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });
        var dialog = page.Locator(".modal-dialog-custom");

        await OpenAndStartSwapAsync(page, dialog, seed.Model2.Name, seed.OtherServedNameRecipe.Name, openButtonText: "Swap");

        var ackCheckbox = dialog.Locator("input[type=checkbox]");
        await ackCheckbox.WaitForAsync();
        Assert.True(await dialog.Locator("button:has-text('Swap now')").IsDisabledAsync());

        await ackCheckbox.CheckAsync();
        Assert.False(await dialog.Locator("button:has-text('Swap now')").IsDisabledAsync());

        // Deliberately don't complete the swap -- the fake "active" row seeded
        // above isn't something the real supervisor actually has running, so
        // following through would exercise an unrealistic stop/start pair.
        await dialog.Locator("button:has-text('Back')").ClickAsync();
        await dialog.Locator("button:has-text('Cancel')").ClickAsync();
    }

    [Fact]
    public async Task Cancel_ShowsPhase1LabelThenPhase2Label_AndLeavesTheInstanceIdle()
    {
        var seed = await SeedAsync();

        var page = await _fixture.NewLoggedInPageAsync();
        await page.GotoAsync(_fixture.BaseUrl + "/", new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });
        var dialog = page.Locator(".modal-dialog-custom");

        // First, a real swap to give the instance an active deployment to
        // genuinely stop in the second swap below -- a swap into a truly idle
        // instance never emits a "stop" event at all (nothing to stop), so
        // phase 1 would never be observable with a real "old process" behind it.
        await OpenAndStartSwapAsync(page, dialog, seed.Model1.Name, seed.Recipe1.Name);
        await dialog.Locator("button:has-text('Swap now')").ClickAsync();
        await dialog.Locator(".alert-success").WaitForAsync(new LocatorWaitForOptions { Timeout = 60000 });
        await dialog.Locator("button:has-text('Done')").ClickAsync();
        await dialog.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Hidden });

        // Second swap, into a slow-starting recipe -- gives a real window
        // (old process stop + the generic GPU backend's own release wait)
        // during which Cancel's label must still read "phase 1".
        await OpenAndStartSwapAsync(page, dialog, seed.Model2.Name, seed.SlowRecipe.Name, openButtonText: "Swap");
        await dialog.Locator("button:has-text('Swap now')").ClickAsync();
        await dialog.Locator(".ceremony-steps").WaitForAsync();

        var cancelButton = dialog.Locator("button:has-text('Cancel')");
        var sawPhase1 = false;
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(8);
        while (DateTime.UtcNow < deadline)
        {
            var text = await cancelButton.InnerTextAsync();
            if (text.Contains("nothing has changed yet"))
            {
                sawPhase1 = true;
                break;
            }
            if (text.Contains("instance will be left idle"))
                break; // already moved past phase 1 -- the assertion below will fail honestly
            await Task.Delay(100);
        }
        Assert.True(sawPhase1, "Never observed the phase-1 Cancel label before the swap moved past it.");

        deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
        var sawPhase2 = false;
        while (DateTime.UtcNow < deadline)
        {
            var text = await cancelButton.InnerTextAsync();
            if (text.Contains("instance will be left idle"))
            {
                sawPhase2 = true;
                break;
            }
            await Task.Delay(200);
        }
        Assert.True(sawPhase2, "Never observed the phase-2 Cancel label.");

        await cancelButton.ClickAsync();
        await dialog.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Hidden });
        await page.Locator("text=No active model — instance is idle").WaitForAsync();
    }

    [Fact]
    public async Task Stop_AfterASuccessfulSwap_ShowsVramReleased()
    {
        var seed = await SeedAsync();

        var page = await _fixture.NewLoggedInPageAsync();
        await page.GotoAsync(_fixture.BaseUrl + "/", new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });
        var dialog = page.Locator(".modal-dialog-custom");

        await OpenAndStartSwapAsync(page, dialog, seed.Model1.Name, seed.Recipe1.Name);
        await dialog.Locator("button:has-text('Swap now')").ClickAsync();
        await dialog.Locator(".alert-success").WaitForAsync(new LocatorWaitForOptions { Timeout = 60000 });
        await dialog.Locator("button:has-text('Done')").ClickAsync();
        await dialog.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Hidden });

        await page.ClickAsync("button:has-text('Stop')");
        await page.Locator(".alert-success:has-text('VRAM released')").WaitForAsync(new LocatorWaitForOptions { Timeout = 15000 });
    }

    [Fact]
    public async Task Stop_WithTheFakeGpuBackendHoldingVram_ShowsAWarningAndRecheck()
    {
        var seed = await SeedAsync(new Dictionary<string, string> { ["GPU_BACKEND"] = "fake" });
        // "Held high" -- the generic release-wait will time out at its own
        // ceiling (same behavior the supervisor's own test_gpu_release.py
        // tests prove, just reached here through the real HTTP/UI path) and
        // Stop's response will report Released=false.
        await _container!.ExecAsync(new[]
        {
            "sh", "-c", "echo '{\"vendor\":\"fake\",\"count\":1,\"total_mb\":80000,\"used_mb\":40000}' > /tmp/fake_gpu_backend.json",
        });

        var page = await _fixture.NewLoggedInPageAsync();
        await page.GotoAsync(_fixture.BaseUrl + "/", new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });
        var dialog = page.Locator(".modal-dialog-custom");

        await OpenAndStartSwapAsync(page, dialog, seed.Model1.Name, seed.Recipe1.Name);
        await dialog.Locator("button:has-text('Swap now')").ClickAsync();
        await dialog.Locator(".alert-success").WaitForAsync(new LocatorWaitForOptions { Timeout = 60000 });
        await dialog.Locator("button:has-text('Done')").ClickAsync();
        await dialog.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Hidden });

        var catResult = await _container!.ExecAsync(new[] { "cat", "/tmp/fake_gpu_backend.json" });
        string gpuProbe;
        try
        {
            using var http = new HttpClient();
            http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", SupervisorImageFixture.Token);
            gpuProbe = await http.GetStringAsync(SupervisorImageFixture.AdminUrl(_container) + "/admin/gpu");
        }
        catch (Exception ex)
        {
            gpuProbe = "probe failed: " + ex.Message;
        }

        await page.ClickAsync("button:has-text('Stop')");
        try
        {
            await page.Locator(".alert-warning:has-text('VRAM may not be fully released'), .alert-success:has-text('VRAM released')")
                .First.WaitForAsync(new LocatorWaitForOptions { Timeout = 20000 });
        }
        catch (TimeoutException)
        {
            var heroText = await page.Locator(".status-card").First.InnerTextAsync();
            throw new Exception(
                $"Neither alert appeared. fake_gpu_backend.json=[{catResult.Stdout}] /admin/gpu (no auth)=[{gpuProbe}] hero=[{heroText}]");
        }
        var shown = await page.Locator(".alert-warning, .alert-success").First.InnerTextAsync();
        Assert.Contains("VRAM", shown);
        Assert.Contains("may not be fully released", shown);
        Assert.Equal(1, await page.Locator("button:has-text('Re-check')").CountAsync());
    }

    [Fact]
    public async Task Rollback_ReplaysThePreviousSnapshot_AndDeploymentsPageShowsItsArgv()
    {
        var seed = await SeedAsync();

        var page = await _fixture.NewLoggedInPageAsync();
        await page.GotoAsync(_fixture.BaseUrl + "/", new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });
        var dialog = page.Locator(".modal-dialog-custom");

        // Swap 1: model1 -> active.
        await OpenAndStartSwapAsync(page, dialog, seed.Model1.Name, seed.Recipe1.Name);
        await dialog.Locator("button:has-text('Swap now')").ClickAsync();
        await dialog.Locator(".alert-success").WaitForAsync(new LocatorWaitForOptions { Timeout = 60000 });
        await dialog.Locator("button:has-text('Done')").ClickAsync();
        await dialog.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Hidden });

        // Swap 2: model2, same served name ("main") -- no ack needed.
        await OpenAndStartSwapAsync(page, dialog, seed.Model2.Name, seed.Recipe1.Name, openButtonText: "Swap");
        await dialog.Locator("button:has-text('Swap now')").ClickAsync();
        await dialog.Locator(".alert-success").WaitForAsync(new LocatorWaitForOptions { Timeout = 60000 });
        await dialog.Locator("button:has-text('Done')").ClickAsync();
        await dialog.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Hidden });

        await page.Locator($"text=Serving: {seed.Model2.Name}").WaitForAsync();

        // Rollback should replay model1's own argv.
        await page.ClickAsync("button:has-text('Rollback')");
        await dialog.Locator(".alert-success").WaitForAsync(new LocatorWaitForOptions { Timeout = 60000 });
        await dialog.Locator("button:has-text('Done')").ClickAsync();
        await dialog.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Hidden });

        await page.Locator($"text=Serving: {seed.Model1.Name}").WaitForAsync();

        await page.GotoAsync(_fixture.BaseUrl + "/deployments", new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });
        var oldestRow = page.Locator("tr", new PageLocatorOptions { HasTextString = seed.Model1.Name }).Last;
        await oldestRow.ClickAsync();
        await page.Locator($"text=args_snapshot:").WaitForAsync();
    }

    [Fact]
    public async Task RedeployThis_FromTheDeploymentsPage_StartsANewDeployment()
    {
        var seed = await SeedAsync();

        var page = await _fixture.NewLoggedInPageAsync();
        await page.GotoAsync(_fixture.BaseUrl + "/", new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });
        var dialog = page.Locator(".modal-dialog-custom");

        await OpenAndStartSwapAsync(page, dialog, seed.Model1.Name, seed.Recipe1.Name);
        await dialog.Locator("button:has-text('Swap now')").ClickAsync();
        await dialog.Locator(".alert-success").WaitForAsync(new LocatorWaitForOptions { Timeout = 60000 });
        await dialog.Locator("button:has-text('Done')").ClickAsync();
        await dialog.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Hidden });

        await page.ClickAsync("button:has-text('Stop')");
        await page.Locator(".alert-success:has-text('VRAM released'), .alert-warning").WaitForAsync(new LocatorWaitForOptions { Timeout = 15000 });

        await page.GotoAsync(_fixture.BaseUrl + "/deployments", new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });
        await page.ClickAsync("button:has-text('Redeploy this')");
        var notice = page.Locator(".modal-dialog-custom:has-text('Notice')");
        await notice.WaitForAsync();
        await notice.Locator("button:has-text('OK')").ClickAsync();

        await WaitUntilAsync(async () =>
        {
            using var db = _fixture.NewDbContext();
            return await db.DeploymentsV2.CountAsync(d => d.InstanceId == seed.Instance.Id) >= 2;
        }, TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task IdleHero_OffersBothButtons_AndNeitherDeadEnds()
    {
        var seed = await SeedAsync();

        var page = await _fixture.NewLoggedInPageAsync();
        await page.GotoAsync(_fixture.BaseUrl + "/", new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });
        await page.Locator("text=No active model — instance is idle").WaitForAsync();

        var dialog = page.Locator(".modal-dialog-custom");

        // "Pick a recipe" -- not a dead end: it opens the picker, which can be
        // backed out of cleanly.
        await page.ClickAsync("button:has-text('Pick a recipe')");
        await dialog.Locator("text=Swap model").WaitForAsync();
        await dialog.Locator("button:has-text('Cancel')").ClickAsync();
        await dialog.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Hidden });

        // Build "last working" history: a real swap, then Stop it.
        await OpenAndStartSwapAsync(page, dialog, seed.Model1.Name, seed.Recipe1.Name);
        await dialog.Locator("button:has-text('Swap now')").ClickAsync();
        await dialog.Locator(".alert-success").WaitForAsync(new LocatorWaitForOptions { Timeout = 60000 });
        await dialog.Locator("button:has-text('Done')").ClickAsync();
        await dialog.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Hidden });
        await page.ClickAsync("button:has-text('Stop')");
        await page.Locator(".alert-success:has-text('VRAM released'), .alert-warning").WaitForAsync(new LocatorWaitForOptions { Timeout = 15000 });
        await page.Locator("text=No active model — instance is idle").WaitForAsync();

        // "Re-deploy last working" -- not a dead end either: it resumes the
        // ceremony directly into Progress and actually completes.
        await page.ClickAsync("button:has-text('Re-deploy last working')");
        await dialog.Locator(".alert-success").WaitForAsync(new LocatorWaitForOptions { Timeout = 60000 });
        await dialog.Locator("button:has-text('Done')").ClickAsync();
        await dialog.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Hidden });
        await page.Locator($"text=Serving: {seed.Model1.Name}").WaitForAsync();
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

[CollectionDefinition("E2EWithSupervisor")]
public sealed class E2EWithSupervisorCollection : ICollectionFixture<DashboardAppFixture>, ICollectionFixture<SupervisorImageFixture>;
