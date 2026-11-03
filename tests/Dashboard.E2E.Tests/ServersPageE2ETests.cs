using Dashboard.Infrastructure.Persistence;
using Dashboard.Infrastructure.Persistence.V2;
using Dashboard.Infrastructure.Tests;
using DotNet.Testcontainers.Containers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Playwright;

namespace Dashboard.E2E.Tests;

/// <summary>P15b's own DoD bullets for the Servers page: registering a real
/// supervisor via URL + token (wrong token -> clear error, right token ->
/// connection summary, saving enables swapping), the boot-restore toggle
/// reading back after reload, the proxy-slug-change warning, the onboarding
/// checklist's fresh-vs-migrated-database distinction, and the token never
/// appearing in the page source after saving. Runs against a REAL supervisor
/// container -- same collection/fixture reuse as SwapCeremonyE2ETests.</summary>
[Collection("E2EWithSupervisor")]
public class ServersPageE2ETests : IAsyncDisposable
{
    private readonly DashboardAppFixture _fixture;
    private readonly SupervisorImageFixture _supervisorImage;
    private IContainer? _container;

    public ServersPageE2ETests(DashboardAppFixture fixture, SupervisorImageFixture supervisorImage)
    {
        _fixture = fixture;
        _supervisorImage = supervisorImage;
    }

    public async ValueTask DisposeAsync()
    {
        if (_container is not null)
            await _container.DisposeAsync();
    }

    private async Task WipeV2TablesAsync()
    {
        using var db = _fixture.NewDbContext();
        await db.DeploymentsV2.ExecuteDeleteAsync();
        await db.RecipesV2.ExecuteDeleteAsync();
        await db.ModelsV2.ExecuteDeleteAsync();
        await db.InstancesV2.ExecuteDeleteAsync();
        await db.ServersV2.ExecuteDeleteAsync();
        await db.Sources.ExecuteDeleteAsync();
    }

    [Fact]
    public async Task TestConnection_WrongToken_ShowsClearError_RightToken_ShowsSummary_AndSavingEnablesSwap()
    {
        await WipeV2TablesAsync();
        _container = await _supervisorImage.StartContainerAsync();
        var adminUrl = SupervisorImageFixture.AdminUrl(_container);

        var page = await _fixture.NewLoggedInPageAsync();
        await page.GotoAsync(_fixture.BaseUrl + "/servers", new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });

        await page.ClickAsync("button:has-text('+ Add server')");
        var serverDialog = page.Locator(".modal-dialog-custom");
        await serverDialog.Locator("input").First.FillAsync("srv-" + Guid.NewGuid().ToString("n"));
        await serverDialog.Locator("button:has-text('Save')").ClickAsync();
        await serverDialog.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Hidden });

        await page.ClickAsync("button:has-text('+ Add instance')");
        var instanceDialog = page.Locator(".modal-dialog-custom");
        await instanceDialog.Locator("label:has-text('Label') + input").FillAsync("primary");
        await instanceDialog.Locator("label:has-text('Proxy slug') + input").FillAsync("slug-" + Guid.NewGuid().ToString("n"));
        await instanceDialog.Locator("label:has-text('Supervisor URL') + input").FillAsync(adminUrl);
        await instanceDialog.Locator("label:has-text('Token') + input").FillAsync("the-wrong-token");

        await instanceDialog.Locator("button:has-text('Test connection')").ClickAsync();
        await instanceDialog.Locator(".alert-danger:has-text('wrong token')").WaitForAsync(new LocatorWaitForOptions { Timeout = 15000 });

        await instanceDialog.Locator("label:has-text('Token') + input").FillAsync(SupervisorImageFixture.Token);
        await instanceDialog.Locator("button:has-text('Test connection')").ClickAsync();
        await instanceDialog.Locator(".alert-success:has-text('Connected')").WaitForAsync(new LocatorWaitForOptions { Timeout = 15000 });

        await instanceDialog.Locator("button:has-text('Save')").ClickAsync();
        await instanceDialog.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Hidden });

        // Saving enabled swapping: the instance no longer shows "needs supervisor registration".
        await page.ReloadAsync(new PageReloadOptions { WaitUntil = WaitUntilState.NetworkIdle });
        Assert.Equal(0, await page.Locator("text=needs supervisor registration").CountAsync());
    }

    [Fact]
    public async Task BootRestoreToggle_ChangesTheSupervisorSetting_AndReadsBackAfterReload()
    {
        await WipeV2TablesAsync();
        _container = await _supervisorImage.StartContainerAsync();
        var adminUrl = SupervisorImageFixture.AdminUrl(_container);

        using (var db = _fixture.NewDbContext())
        {
            var server = new Server { Label = $"srv-{Guid.NewGuid():N}", ModelsDir = "/models", GpuMemGb = 80 };
            db.ServersV2.Add(server);
            await db.SaveChangesAsync();
            db.InstancesV2.Add(new Instance
            {
                ServerId = server.Id, Label = "primary", ProxySlug = $"slug-{Guid.NewGuid():N}",
                AdminUrl = adminUrl, AdminTokenEnc = _fixture.EncryptTokenForApp(SupervisorImageFixture.Token),
            });
            await db.SaveChangesAsync();
        }

        var page = await _fixture.NewLoggedInPageAsync();
        await page.GotoAsync(_fixture.BaseUrl + "/servers", new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });
        await page.ClickAsync("button:has-text('Edit') >> nth=1"); // 0 = server's own Edit, 1 = instance's
        var dialog = page.Locator(".modal-dialog-custom");
        var toggle = dialog.Locator("input[type=checkbox]");
        await toggle.WaitForAsync(new LocatorWaitForOptions { Timeout = 15000 });

        Assert.True(await toggle.IsCheckedAsync()); // default on

        await toggle.UncheckAsync();
        await Task.Delay(500); // the toggle's own PUT is fire-and-forget from the UI's perspective

        await page.ReloadAsync(new PageReloadOptions { WaitUntil = WaitUntilState.NetworkIdle });
        await page.ClickAsync("button:has-text('Edit') >> nth=1");
        var toggleAfterReload = page.Locator(".modal-dialog-custom input[type=checkbox]");
        await toggleAfterReload.WaitForAsync(new LocatorWaitForOptions { Timeout = 15000 });

        // The dialog shows a hardcoded "on" default the instant it opens, then
        // asynchronously fetches and applies the real value (RefreshAutoStartAsync
        // is fire-and-forget from OpenEditInstance) -- poll rather than read the
        // very first render's state.
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        bool? isChecked = null;
        while (DateTime.UtcNow < deadline)
        {
            isChecked = await toggleAfterReload.IsCheckedAsync();
            if (isChecked == false) break;
            await Task.Delay(200);
        }
        Assert.False(isChecked);
    }

    [Fact]
    public async Task EditingAProxySlug_ShowsTheChangesClientUrlsWarning()
    {
        await WipeV2TablesAsync();

        using (var db = _fixture.NewDbContext())
        {
            var server = new Server { Label = $"srv-{Guid.NewGuid():N}", ModelsDir = "/models", GpuMemGb = 80 };
            db.ServersV2.Add(server);
            await db.SaveChangesAsync();
            db.InstancesV2.Add(new Instance { ServerId = server.Id, Label = "primary", ProxySlug = "old-slug" });
            await db.SaveChangesAsync();
        }

        var page = await _fixture.NewLoggedInPageAsync();
        await page.GotoAsync(_fixture.BaseUrl + "/servers", new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });
        await page.ClickAsync("button:has-text('Edit') >> nth=1");
        var dialog = page.Locator(".modal-dialog-custom");
        await dialog.Locator("text=Changing this changes every client's proxy URL.").WaitForAsync();

        await dialog.Locator("label:has-text('Proxy slug') + input").FillAsync("new-slug");
        await dialog.Locator("button:has-text('Save')").ClickAsync();

        var notice = page.Locator(".modal-dialog-custom:has-text('Notice')");
        await notice.WaitForAsync();
        await notice.Locator("text=proxy URL for this instance is now different").WaitForAsync();
    }

    [Fact]
    public async Task Onboarding_ShowsOnAFreshDatabase_ButNeverOnAMigratedOne()
    {
        await WipeV2TablesAsync();

        var page = await _fixture.NewLoggedInPageAsync();
        await page.GotoAsync(_fixture.BaseUrl + "/", new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });
        await page.Locator("text=Get started").WaitForAsync();

        // Simulate a migrated database: a legacy Source row exists (P01b's own
        // conversion runs against this table), even though there's still no V2
        // deployment -- the checklist must not show for this case.
        using (var db = _fixture.NewDbContext())
        {
            db.Sources.Add(new Source
            {
                Label = "legacy-source", Slug = $"legacy-{Guid.NewGuid():N}",
                Type = "vllm", MetricsUrl = "http://localhost:1", InferenceUrl = "http://localhost:1",
            });
            await db.SaveChangesAsync();
        }

        await page.ReloadAsync(new PageReloadOptions { WaitUntil = WaitUntilState.NetworkIdle });
        Assert.Equal(0, await page.Locator("text=Get started").CountAsync());
    }

    [Fact]
    public async Task TheToken_IsNotVisibleInThePageSource_AfterSaving()
    {
        await WipeV2TablesAsync();
        _container = await _supervisorImage.StartContainerAsync();
        var adminUrl = SupervisorImageFixture.AdminUrl(_container);
        const string secretToken = "super-secret-token-xyz-not-the-real-one";

        var page = await _fixture.NewLoggedInPageAsync();
        await page.GotoAsync(_fixture.BaseUrl + "/servers", new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });

        await page.ClickAsync("button:has-text('+ Add server')");
        var serverDialog = page.Locator(".modal-dialog-custom");
        await serverDialog.Locator("input").First.FillAsync("srv-" + Guid.NewGuid().ToString("n"));
        await serverDialog.Locator("button:has-text('Save')").ClickAsync();
        await serverDialog.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Hidden });

        await page.ClickAsync("button:has-text('+ Add instance')");
        var instanceDialog = page.Locator(".modal-dialog-custom");
        await instanceDialog.Locator("label:has-text('Label') + input").FillAsync("primary");
        await instanceDialog.Locator("label:has-text('Proxy slug') + input").FillAsync("slug-" + Guid.NewGuid().ToString("n"));
        await instanceDialog.Locator("label:has-text('Supervisor URL') + input").FillAsync(adminUrl);
        await instanceDialog.Locator("label:has-text('Token') + input").FillAsync(secretToken);
        await instanceDialog.Locator("button:has-text('Save')").ClickAsync();
        await instanceDialog.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Hidden });

        var html = await page.ContentAsync();
        Assert.DoesNotContain(secretToken, html);

        using var db = _fixture.NewDbContext();
        var stored = await db.InstancesV2.Select(i => i.AdminTokenEnc).FirstAsync();
        Assert.NotNull(stored);
        Assert.DoesNotContain(secretToken, stored);
    }
}
