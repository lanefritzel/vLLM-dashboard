using Dashboard.Infrastructure.Persistence.V2;
using Microsoft.EntityFrameworkCore;
using Microsoft.Playwright;

namespace Dashboard.E2E.Tests;

/// <summary>Covers the P13 DoD bullets that need a stubbed supervisor admin API
/// standing in for a real one, since disk space and download timing can't be
/// controlled deterministically against a real HF download: "Add model against
/// a stubbed repo shows the progress bar and ends 'available'", "with stubbed
/// low disk the download is blocked with the required-vs-available text", and
/// "Add model is disabled during a download".</summary>
[Collection("E2E")]
public class AddModelDownloadE2ETests
{
    private readonly DashboardAppFixture _fixture;

    public AddModelDownloadE2ETests(DashboardAppFixture fixture) => _fixture = fixture;

    /// <summary>Wipes every V2 table first -- see ModelsRecipesE2ETests'
    /// SeedServerAndInstanceAsync for why: the page always binds to the
    /// alphabetically-first server, and the shared Postgres lives for the
    /// whole fixture, so a leftover server from another test could win.</summary>
    private async Task<Instance> SeedServerAndInstanceAsync(string adminUrl)
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

        var instance = new Instance
        {
            ServerId = server.Id, Label = "primary", ProxySlug = $"slug-{Guid.NewGuid():N}", AdminUrl = adminUrl,
        };
        db.InstancesV2.Add(instance);
        await db.SaveChangesAsync();

        return instance;
    }

    [Fact]
    public async Task AddModel_AgainstAStubbedRepo_ShowsProgressBar_AndEndsAvailable()
    {
        var fakeAdmin = new FakeSupervisorAdmin();
        var adminUrl = await fakeAdmin.StartAsync();
        await SeedServerAndInstanceAsync(adminUrl);

        var page = await _fixture.NewLoggedInPageAsync();
        await page.GotoAsync(_fixture.BaseUrl + "/models-recipes", new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });

        await page.ClickAsync("button:has-text('+ Add model')");
        var dialog = page.Locator("dialog[open]");
        await dialog.Locator("input.form-control").First.FillAsync("e2e-hf-model");
        await dialog.Locator("input[placeholder='org/repo-name']").FillAsync("org/stub-repo");
        await dialog.Locator("button:has-text('Inspect')").ClickAsync();

        await dialog.Locator("text=Files:").WaitForAsync();
        await dialog.Locator("button:has-text('Download')").ClickAsync();

        await dialog.Locator(".progress-bar").WaitForAsync();
        await dialog.Locator("text=/50.*%/").WaitForAsync();

        await dialog.Locator("text=✓ available").WaitForAsync(new LocatorWaitForOptions { Timeout = 10000 });
        await dialog.Locator("button:has-text('Add to catalog')").ClickAsync();

        await dialog.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Hidden });
        await page.Locator("table:has(th:has-text('Source')) >> text=e2e-hf-model").WaitForAsync();

        await fakeAdmin.DisposeAsync();
    }

    [Fact]
    public async Task AddModel_AgainstAStubbedLowDiskRepo_BlocksWithRequiredVsAvailableText()
    {
        var fakeAdmin = new FakeSupervisorAdmin { FreeBytes = 100, FileSizeBytes = 1_000_000 };
        var adminUrl = await fakeAdmin.StartAsync();
        await SeedServerAndInstanceAsync(adminUrl);

        var page = await _fixture.NewLoggedInPageAsync();
        await page.GotoAsync(_fixture.BaseUrl + "/models-recipes", new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });

        await page.ClickAsync("button:has-text('+ Add model')");
        var dialog = page.Locator("dialog[open]");
        await dialog.Locator("input.form-control").First.FillAsync("e2e-low-disk-model");
        await dialog.Locator("input[placeholder='org/repo-name']").FillAsync("org/stub-repo-big");
        await dialog.Locator("button:has-text('Inspect')").ClickAsync();

        await dialog.Locator(".ui-callout-danger:has-text('Not enough disk space')").WaitForAsync();
        await dialog.Locator("text=Required 1,000,000 bytes, available 100 bytes.").WaitForAsync();
        Assert.Equal(0, await dialog.Locator("button:has-text('Download')").CountAsync());

        await fakeAdmin.DisposeAsync();
    }

    [Fact]
    public async Task AddModel_CannotBeClosedOrCancelled_WhileADownloadIsInFlight()
    {
        var fakeAdmin = new FakeSupervisorAdmin();
        var adminUrl = await fakeAdmin.StartAsync();
        await SeedServerAndInstanceAsync(adminUrl);

        var page = await _fixture.NewLoggedInPageAsync();
        await page.GotoAsync(_fixture.BaseUrl + "/models-recipes", new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });

        await page.ClickAsync("button:has-text('+ Add model')");
        var dialog = page.Locator("dialog[open]");
        await dialog.Locator("input.form-control").First.FillAsync("e2e-busy-model");
        await dialog.Locator("input[placeholder='org/repo-name']").FillAsync("org/stub-repo-busy");
        await dialog.Locator("button:has-text('Inspect')").ClickAsync();
        await dialog.Locator("text=Files:").WaitForAsync();
        await dialog.Locator("button:has-text('Download')").ClickAsync();

        await dialog.Locator(".progress-bar").WaitForAsync();

        // Mid-download: the dialog's own close "X" is a no-op (RequestCancelIfIdle
        // short-circuits while IsBusy), and the Name field stays disabled. Checked
        // immediately after the click (no extra delay) so this doesn't race the
        // fake admin's 1s poll boundary into its "done" state.
        Assert.True(await dialog.Locator("input.form-control").First.IsDisabledAsync());
        await dialog.Locator("button[aria-label='Close']").ClickAsync();
        Assert.True(await dialog.IsVisibleAsync());

        await dialog.Locator("text=✓ available").WaitForAsync(new LocatorWaitForOptions { Timeout = 10000 });
        await dialog.Locator("button[aria-label='Close']").ClickAsync();
        await dialog.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Hidden });

        await fakeAdmin.DisposeAsync();
    }
}
