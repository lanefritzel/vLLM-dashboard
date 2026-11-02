using Dashboard.Infrastructure.Persistence.V2;
using Microsoft.EntityFrameworkCore;
using Microsoft.Playwright;

namespace Dashboard.E2E.Tests;

/// <summary>Drives the real P13 "Models &amp; Recipes" page in a real browser against
/// the real built app + a throwaway Postgres, covering the phase's own DoD bullets
/// that don't need a real/stubbed supervisor (add a local model, create+edit+save a
/// recipe, inline validation blocking, duplicate, delete-with-confirmation).</summary>
[Collection("E2E")]
public class ModelsRecipesE2ETests
{
    private readonly DashboardAppFixture _fixture;

    public ModelsRecipesE2ETests(DashboardAppFixture fixture) => _fixture = fixture;

    /// <summary>Seeds one server + one instance (no AdminUrl -- these tests only
    /// exercise the local-path model flow, not the HF/supervisor download flow)
    /// directly through EF, bypassing the (not-yet-built) Servers page UI. Wipes
    /// every V2 table first: the page has no server picker yet and always binds
    /// to <c>ListAsync().OrderBy(Label).First()</c>, so with the shared Postgres
    /// living for the whole fixture, a leftover server from an earlier test could
    /// sort first and silently steal the page away from this test's own data.</summary>
    private async Task<(Server server, Instance instance)> SeedServerAndInstanceAsync()
    {
        using var db = _fixture.NewDbContext();
        await db.DeploymentsV2.ExecuteDeleteAsync(); // FK: deployments -> recipes/models, must go first
        await db.RecipesV2.ExecuteDeleteAsync();
        await db.ModelsV2.ExecuteDeleteAsync();
        await db.InstancesV2.ExecuteDeleteAsync();
        await db.ServersV2.ExecuteDeleteAsync();

        var server = new Server { Label = $"srv-{Guid.NewGuid():N}", ModelsDir = "/models", GpuMemGb = 80 };
        db.ServersV2.Add(server);
        await db.SaveChangesAsync();

        var instance = new Instance { ServerId = server.Id, Label = "primary", ProxySlug = $"slug-{Guid.NewGuid():N}" };
        db.InstancesV2.Add(instance);
        await db.SaveChangesAsync();

        return (server, instance);
    }

    [Fact]
    public async Task AddLocalModel_CreateRecipe_EditADial_Save_AppearsInTheList()
    {
        await SeedServerAndInstanceAsync();
        var page = await _fixture.NewLoggedInPageAsync();
        await page.GotoAsync(_fixture.BaseUrl + "/models-recipes", new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });

        await page.ClickAsync("button:has-text('+ Add model')");
        await page.Locator(".modal-dialog-custom select.form-select").SelectOptionAsync("local");
        await page.FillAsync(".modal-dialog-custom input.form-control >> nth=0", "my-local-model");
        var localPathInput = page.Locator("input[placeholder='/models/hf-cache/my-model']");
        await localPathInput.FillAsync("/models/hf-cache/my-local-model");
        await localPathInput.PressAsync("Tab"); // plain @bind (no :event=oninput) only commits on blur/change
        await page.ClickAsync(".modal-dialog-custom button:has-text('Save')");

        await page.Locator("table:has(th:has-text('Source')) >> text=my-local-model").WaitForAsync();

        await page.ClickAsync("tr:has-text('my-local-model') button:has-text('New recipe')");
        var dialog = page.Locator(".modal-dialog-custom");
        await dialog.Locator("text=New recipe").WaitForAsync();

        var maxLenInput = dialog.Locator("label:has-text('Max model length') + input");
        await maxLenInput.FillAsync("4096");

        await dialog.Locator("button:has-text('Save')").ClickAsync();
        await dialog.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Hidden });

        await page.Locator("table:has(th:has-text('Served as')) >> text=4,096").WaitForAsync();
    }

    [Fact]
    public async Task NewRecipe_ServedNamePrefillsToMain_WhenNothingIsActive()
    {
        var (server, _) = await SeedServerAndInstanceAsync();
        using (var db = _fixture.NewDbContext())
        {
            db.ModelsV2.Add(new Model { ServerId = server.Id, Name = "seed-model", Path = "/models/hf-cache/seed-model", SourceType = "local" });
            await db.SaveChangesAsync();
        }

        var page = await _fixture.NewLoggedInPageAsync();
        await page.GotoAsync(_fixture.BaseUrl + "/models-recipes", new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });

        await page.ClickAsync("tr:has-text('seed-model') button:has-text('New recipe')");
        var dialog = page.Locator(".modal-dialog-custom");
        var servedNameInput = dialog.Locator("label:has-text('Served model name') + input");
        Assert.Equal("main", await servedNameInput.InputValueAsync());
    }

    [Fact]
    public async Task NewRecipe_ServedNamePrefillsToTheActiveRecipesServedName_WhenOneIsActive()
    {
        var (server, instance) = await SeedServerAndInstanceAsync();
        using (var db = _fixture.NewDbContext())
        {
            var activeModel = new Model { ServerId = server.Id, Name = "active-model", Path = "/models/hf-cache/active-model", SourceType = "local" };
            var otherModel = new Model { ServerId = server.Id, Name = "other-model", Path = "/models/hf-cache/other-model", SourceType = "local" };
            db.ModelsV2.AddRange(activeModel, otherModel);
            await db.SaveChangesAsync();

            var activeRecipe = new Recipe { ServerId = server.Id, Name = "active-recipe", ServedModelName = "custom-active-name" };
            db.RecipesV2.Add(activeRecipe);
            await db.SaveChangesAsync();

            db.DeploymentsV2.Add(new Deployment
            {
                InstanceId = instance.Id, ModelId = activeModel.Id, RecipeId = activeRecipe.Id,
                ArgsSnapshot = "[]", Status = "active",
            });
            await db.SaveChangesAsync();
        }

        var page = await _fixture.NewLoggedInPageAsync();
        await page.GotoAsync(_fixture.BaseUrl + "/models-recipes", new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });

        await page.ClickAsync("tr:has-text('other-model') button:has-text('New recipe')");
        var dialog = page.Locator(".modal-dialog-custom");
        var servedNameInput = dialog.Locator("label:has-text('Served model name') + input");
        Assert.Equal("custom-active-name", await servedNameInput.InputValueAsync());
    }

    [Fact]
    public async Task Save_BlockedWithInlineError_ForBlankModelPath()
    {
        var (server, _) = await SeedServerAndInstanceAsync();
        using (var db = _fixture.NewDbContext())
        {
            // Bypasses the UI's own local-path requirement (AddModelDialog.CanSaveLocal)
            // deliberately -- a converted legacy row is one real way this can happen.
            db.ModelsV2.Add(new Model { ServerId = server.Id, Name = "blank-path-model", Path = "", SourceType = "local" });
            await db.SaveChangesAsync();
        }

        var page = await _fixture.NewLoggedInPageAsync();
        await page.GotoAsync(_fixture.BaseUrl + "/models-recipes", new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });

        await page.ClickAsync("tr:has-text('blank-path-model') button:has-text('New recipe')");
        var dialog = page.Locator(".modal-dialog-custom");

        await dialog.Locator(".alert-danger:has-text('This model has no path configured.')").WaitForAsync();
        Assert.True(await dialog.Locator("button:has-text('Save')").IsDisabledAsync());
    }

    [Fact]
    public async Task OnAnIntelServer_AutoDtypeShowsASoftWarning_ButDoesNotBlockSave()
    {
        var (server, _) = await SeedServerAndInstanceAsync();
        using (var db = _fixture.NewDbContext())
        {
            var s = await db.ServersV2.SingleAsync(x => x.Id == server.Id);
            s.GpuName = "Intel Max 1550";
            db.ModelsV2.Add(new Model { ServerId = server.Id, Name = "seed-model-auto", Path = "/models/hf-cache/seed-model-auto", SourceType = "local" });
            await db.SaveChangesAsync();
        }

        var page = await _fixture.NewLoggedInPageAsync();
        await page.GotoAsync(_fixture.BaseUrl + "/models-recipes", new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });

        await page.ClickAsync("tr:has-text('seed-model-auto') button:has-text('New recipe')");
        var dialog = page.Locator(".modal-dialog-custom");

        // dtype defaults to "auto" -- the warning should appear without any interaction.
        await dialog.Locator(".alert-warning:has-text('unset/auto on the Intel stack')").WaitForAsync();
        Assert.False(await dialog.Locator("button:has-text('Save')").IsDisabledAsync());
    }

    [Fact]
    public async Task Save_BlockedWithInlineError_ForNonIdentifierServedName()
    {
        var (server, _) = await SeedServerAndInstanceAsync();
        using (var db = _fixture.NewDbContext())
        {
            db.ModelsV2.Add(new Model { ServerId = server.Id, Name = "seed-model-2", Path = "/models/hf-cache/seed-model-2", SourceType = "local" });
            await db.SaveChangesAsync();
        }

        var page = await _fixture.NewLoggedInPageAsync();
        await page.GotoAsync(_fixture.BaseUrl + "/models-recipes", new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });

        await page.ClickAsync("tr:has-text('seed-model-2') button:has-text('New recipe')");
        var dialog = page.Locator(".modal-dialog-custom");
        var servedNameInput = dialog.Locator("label:has-text('Served model name') + input");
        await servedNameInput.FillAsync("not a valid name!");
        await servedNameInput.PressAsync("Tab");

        await dialog.Locator(".alert-danger:has-text('must be a valid identifier')").WaitForAsync();
        Assert.True(await dialog.Locator("button:has-text('Save')").IsDisabledAsync());
    }

    [Fact]
    public async Task Save_BlockedWithInlineError_ForOutOfRangeGpuMemoryUtilization()
    {
        var (server, _) = await SeedServerAndInstanceAsync();
        using (var db = _fixture.NewDbContext())
        {
            db.ModelsV2.Add(new Model { ServerId = server.Id, Name = "seed-model-3", Path = "/models/hf-cache/seed-model-3", SourceType = "local" });
            await db.SaveChangesAsync();
        }

        var page = await _fixture.NewLoggedInPageAsync();
        await page.GotoAsync(_fixture.BaseUrl + "/models-recipes", new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });

        await page.ClickAsync("tr:has-text('seed-model-3') button:has-text('New recipe')");
        var dialog = page.Locator(".modal-dialog-custom");
        var gpuInput = dialog.Locator("label:has-text('GPU memory utilization') + input");
        await gpuInput.FillAsync("1.5");
        await gpuInput.PressAsync("Tab");

        await dialog.Locator(".alert-danger:has-text('must be greater than 0 and at most 1')").WaitForAsync();
        Assert.True(await dialog.Locator("button:has-text('Save')").IsDisabledAsync());
    }

    [Fact]
    public async Task OnAnIntelServer_Bfloat16IsBlocked_AndFloat16SavesCleanly()
    {
        var (server, _) = await SeedServerAndInstanceAsync();
        using (var db = _fixture.NewDbContext())
        {
            var s = await db.ServersV2.SingleAsync(x => x.Id == server.Id);
            s.GpuName = "Intel Max 1550";
            db.ModelsV2.Add(new Model { ServerId = server.Id, Name = "seed-model-4", Path = "/models/hf-cache/seed-model-4", SourceType = "local" });
            await db.SaveChangesAsync();
        }

        var page = await _fixture.NewLoggedInPageAsync();
        await page.GotoAsync(_fixture.BaseUrl + "/models-recipes", new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });

        await page.ClickAsync("tr:has-text('seed-model-4') button:has-text('New recipe')");
        var dialog = page.Locator(".modal-dialog-custom");
        var dtypeSelect = dialog.Locator("label:has-text('Data type (--dtype)') + select");

        await dtypeSelect.SelectOptionAsync("bfloat16");
        await dialog.Locator(".alert-danger:has-text('blocked on the Intel stack')").WaitForAsync();
        Assert.True(await dialog.Locator("button:has-text('Save')").IsDisabledAsync());

        await dtypeSelect.SelectOptionAsync("float16");
        await WaitUntilAsync(async () => !await dialog.Locator("button:has-text('Save')").IsDisabledAsync(), TimeSpan.FromSeconds(5));
        Assert.False(await dialog.Locator(".alert-danger").IsVisibleAsync());

        await dialog.Locator("button:has-text('Save')").ClickAsync();
        await dialog.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Hidden });
    }

    [Fact]
    public async Task Duplicate_CreatesNameCopy_AndDelete_AsksForConfirmation()
    {
        var (server, _) = await SeedServerAndInstanceAsync();
        using (var db = _fixture.NewDbContext())
        {
            var model = new Model { ServerId = server.Id, Name = "seed-model-5", Path = "/models/hf-cache/seed-model-5", SourceType = "local" };
            db.ModelsV2.Add(model);
            await db.SaveChangesAsync();
            db.RecipesV2.Add(new Recipe { ServerId = server.Id, Name = "r1", ServedModelName = "main" });
            await db.SaveChangesAsync();
        }

        var page = await _fixture.NewLoggedInPageAsync();
        await page.GotoAsync(_fixture.BaseUrl + "/models-recipes", new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });

        await page.ClickAsync("tr:has-text('r1') button:has-text('Duplicate')");
        await page.Locator("text=r1-copy").WaitForAsync();

        await page.ClickAsync("tr:has-text('r1-copy') button:has-text('Delete')");
        var confirmDialog = page.Locator(".modal-dialog-custom:has-text('Confirm')");
        await confirmDialog.WaitForAsync();
        await confirmDialog.Locator("button:has-text('Delete')").ClickAsync();
        await confirmDialog.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Hidden });

        await WaitUntilAsync(async () => await page.Locator("text=r1-copy").CountAsync() == 0, TimeSpan.FromSeconds(5));
        Assert.Equal(1, await page.Locator("text=r1").CountAsync());
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (await condition())
                return;
            await Task.Delay(100);
        }
        Assert.True(await condition(), $"Condition did not become true within {timeout}.");
    }
}
