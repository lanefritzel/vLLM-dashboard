using Dashboard.Infrastructure.Persistence.V2;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Configurations;
using DotNet.Testcontainers.Containers;
using DotNet.Testcontainers.Images;
using Microsoft.EntityFrameworkCore;
using Microsoft.Playwright;

namespace Dashboard.E2E.Tests;

/// <summary>Multi-engine (llama.cpp) real-process E2E check. Builds the REAL
/// supervisor/Dockerfile.llamacpp image (not the fake-vLLM Dockerfile.test every other
/// E2E test uses) and runs the REAL llama-server against a REAL GGUF, driven through the
/// actual built dashboard in a real browser. Confirms what unit/integration tests alone
/// can't: Servers page engine picker -> Test connection -> Models &amp; Recipes llama.cpp
/// recipe editor -> a real swap deploy -> ready -> smoke pass -> clean stop, all through
/// the UI against a process that was never faked. Needs a GGUF on disk -- see
/// <see cref="LlamaCppSupervisorImageFixture.HostGgufDir"/>.</summary>
public sealed class LlamaCppSupervisorImageFixture : IAsyncLifetime
{
    public const int AdminPort = 8080;

    public const string GgufFileName = "qwen2.5-0.5b-instruct-q4_k_m.gguf";

    /// <summary>Host directory holding <see cref="GgufFileName"/>. Overridable via the
    /// LLAMACPP_E2E_GGUF_DIR env var -- the default is a session-scoped scratchpad path from
    /// the spike that downloaded it, which will eventually stop existing. Re-download the
    /// model and point this at wherever it lives if that default ever 404s.</summary>
    public static string HostGgufDir =>
        Environment.GetEnvironmentVariable("LLAMACPP_E2E_GGUF_DIR")
        ?? "/tmp/claude-1000/-home-lanef-appdevelopment-application-development-vLLM-dashboard/afd135dc-e0ce-4601-89b0-b2c744656aec/scratchpad/llamacpp-spike-models";

    private IFutureDockerImage? _image;

    public async Task InitializeAsync()
    {
        if (!File.Exists(Path.Combine(HostGgufDir, GgufFileName)))
        {
            throw new InvalidOperationException(
                $"No GGUF at {Path.Combine(HostGgufDir, GgufFileName)}. Download Qwen2.5-0.5B-Instruct-GGUF " +
                "(Q4_K_M) somewhere durable and set the LLAMACPP_E2E_GGUF_DIR env var to that directory.");
        }

        var supervisorDir = Path.Combine(FindRepoRoot(), "supervisor");
        _image = new ImageFromDockerfileBuilder()
            .WithDockerfileDirectory(supervisorDir) // build context is supervisor/, not the repo root (see Dockerfile.llamacpp's own COPY paths)
            .WithDockerfile("Dockerfile.llamacpp")
            .Build();
        await _image.CreateAsync();
    }

    public async Task DisposeAsync()
    {
        if (_image is not null)
            await _image.DisposeAsync();
    }

    public async Task<IContainer> StartContainerAsync()
    {
        var builder = new ContainerBuilder(_image!)
            .WithPortBinding(AdminPort, true)
            .WithEnvironment("ADMIN_PORT", AdminPort.ToString())
            .WithEnvironment("ADMIN_BIND_HOST", "0.0.0.0") // see SupervisorImageFixture for why
            .WithEnvironment("AUTO_START", "off")
            .WithBindMount(HostGgufDir, "/models", AccessMode.ReadOnly)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(r => r.ForPort(AdminPort).ForPath("/admin/health")));
        var container = builder.Build();
        await container.StartAsync();
        return container;
    }

    public static string AdminUrl(IContainer container) => $"http://127.0.0.1:{container.GetMappedPublicPort(AdminPort)}";

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, ".git")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException($"Could not find repo root (.git) from {AppContext.BaseDirectory}.");
    }
}

[CollectionDefinition("E2EWithLlamaCpp")]
public sealed class E2EWithLlamaCppCollection : ICollectionFixture<DashboardAppFixture>, ICollectionFixture<LlamaCppSupervisorImageFixture>;

[Collection("E2EWithLlamaCpp")]
public class LlamaCppE2ETests : IAsyncDisposable
{
    private readonly DashboardAppFixture _fixture;
    private readonly LlamaCppSupervisorImageFixture _supervisorImage;
    private IContainer? _container;

    public LlamaCppE2ETests(DashboardAppFixture fixture, LlamaCppSupervisorImageFixture supervisorImage)
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
    }

    [Fact]
    public async Task RealLlamaCppEngine_AddInstance_EditRecipe_SwapDeploy_ReachesReady_WithNoMismatch()
    {
        await WipeV2TablesAsync();
        _container = await _supervisorImage.StartContainerAsync();
        var adminUrl = LlamaCppSupervisorImageFixture.AdminUrl(_container);

        var page = await _fixture.NewLoggedInPageAsync();

        // --- Servers page: add server + a llama.cpp instance, confirm no engine mismatch ---
        await page.GotoAsync(_fixture.BaseUrl + "/servers", new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });

        // Servers.razor's dialogs now render through the new <Dialog> component (<dialog
        // class="ui-dialog">), not the old .modal-dialog-custom div -- unlike SwapCeremonyDialog
        // below, which hasn't been migrated yet. Confirmed empirically: .modal-dialog-custom here
        // timed out against the real page.
        await page.ClickAsync("button:has-text('+ Add server')");
        var serverDialog = page.Locator("dialog[open]");
        await serverDialog.Locator("input").First.FillAsync("srv-" + Guid.NewGuid().ToString("n"));
        await serverDialog.Locator("button:has-text('Save')").ClickAsync();
        await serverDialog.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Hidden });

        await page.ClickAsync("button:has-text('+ Add instance')");
        var instanceDialog = page.Locator("dialog[open]");
        await instanceDialog.Locator("label:has-text('Label') + input").FillAsync("primary");
        await instanceDialog.Locator("label:has-text('Proxy slug') + input").FillAsync("slug-" + Guid.NewGuid().ToString("n"));
        await instanceDialog.Locator("label:has-text('Engine') + select").SelectOptionAsync("llamacpp");
        var adminUrlInput = instanceDialog.Locator("label:has-text('Supervisor URL') + input");
        await adminUrlInput.FillAsync(adminUrl);
        await adminUrlInput.PressAsync("Tab"); // commits the @bind before Test connection un-disables

        await instanceDialog.Locator("button:has-text('Test connection')").ClickAsync();
        // Servers.razor's Test connection result renders through the Callout component
        // (ui-callout-<tone>), not a raw Bootstrap .alert-* class -- unlike SwapCeremonyDialog,
        // which still uses .alert-success/.alert-warning/.alert-danger directly (see below).
        await instanceDialog.Locator(".ui-callout-success:has-text('Connected')").WaitForAsync(new LocatorWaitForOptions { Timeout = 15000 });
        Assert.Equal(0, await instanceDialog.Locator(".ui-callout-warn").CountAsync()); // no engine mismatch

        // The compose snippet reflects the chosen engine without needing a container_name/etc.
        await instanceDialog.Locator("text=Dockerfile.llamacpp").WaitForAsync();

        await instanceDialog.Locator("button:has-text('Save')").ClickAsync();
        await instanceDialog.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Hidden });

        // --- Seed a local GGUF model + llama.cpp recipe directly (same pattern every other
        // E2E test uses for models -- there's no "add model" flow being exercised here) ---
        int serverId, instanceId;
        using (var db = _fixture.NewDbContext())
        {
            var server = await db.ServersV2.SingleAsync();
            server.ModelsDir = "/models";
            var instance = await db.InstancesV2.SingleAsync();
            serverId = server.Id;
            instanceId = instance.Id;

            db.ModelsV2.Add(new Model
            {
                ServerId = serverId, Name = "qwen2.5-0.5b", SourceType = "local",
                Path = $"/models/{LlamaCppSupervisorImageFixture.GgufFileName}", LoadFormat = "gguf",
            });
            await db.SaveChangesAsync();
        }

        // --- Models & Recipes: the llama.cpp recipe editor hides every vLLM-only section ---
        await page.GotoAsync(_fixture.BaseUrl + "/models-recipes", new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });
        await page.ClickAsync("tr:has-text('qwen2.5-0.5b') button:has-text('New recipe')");
        var recipeDialog = page.Locator("dialog[open]"); // RecipeEditorDialog.razor also uses the new <Dialog>
        await recipeDialog.Locator("text=New recipe").WaitForAsync();

        Assert.Equal(0, await recipeDialog.Locator("text=GPU memory").CountAsync());
        await recipeDialog.Locator("text=Context size (--ctx-size)").WaitForAsync();

        var servedNameInput = recipeDialog.Locator("label:has-text('Served model name') + input");
        await servedNameInput.FillAsync("qwen-main");
        await servedNameInput.PressAsync("Tab");
        var maxLenInput = recipeDialog.Locator("label:has-text('Context size') + input");
        await maxLenInput.FillAsync("2048");
        await maxLenInput.PressAsync("Tab");

        await recipeDialog.Locator("button:has-text('Save')").ClickAsync();
        await recipeDialog.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Hidden });

        // --- Overview: a real swap, deploying the real llama-server against the real GGUF ---
        await page.GotoAsync(_fixture.BaseUrl + "/", new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });
        await page.ClickAsync("button:has-text('Pick a recipe')");
        var swapDialog = page.Locator(".modal-dialog-custom");
        await swapDialog.Locator("text=Swap model").WaitForAsync();
        await swapDialog.Locator("select >> nth=0").SelectOptionAsync(new SelectOptionValue { Label = "qwen2.5-0.5b" });
        // The recipe editor's "Name" field (the swap picker's label) defaults to
        // NextAvailableRecipeName(model.Name) = "<model>-recipe" for the model's first recipe --
        // this test never typed a different Name, so that's what the dropdown shows.
        await swapDialog.Locator("select >> nth=1").SelectOptionAsync(new SelectOptionValue { Label = "qwen2.5-0.5b-recipe" });
        await swapDialog.Locator("button:has-text('Preview')").ClickAsync();
        await swapDialog.Locator("text=Review changes").WaitForAsync();

        // P3 finding, now fixed: this text must name the real engine, not a hardcoded "vLLM".
        await swapDialog.Locator("text=llama.cpp process will restart").WaitForAsync();

        await swapDialog.Locator("button:has-text('Swap now')").ClickAsync();
        await swapDialog.Locator("text=Swapping…").WaitForAsync();

        // The result panel is still a raw .alert-success/.alert-danger div in SwapCeremonyDialog
        // (not yet migrated to Callout) -- same selector SwapCeremonyE2ETests already relies on.
        // Real weight load off an HDD/cold cache can take a while -- generous timeout, no GPU involved.
        string resultText;
        try
        {
            await swapDialog.Locator(".alert-success").WaitForAsync(new LocatorWaitForOptions { Timeout = 120_000 });
        }
        catch (TimeoutException)
        {
            var dialogText = await swapDialog.InnerTextAsync();
            using var db = _fixture.NewDbContext();
            var deployments = await db.DeploymentsV2.OrderByDescending(d => d.Id).Take(3).ToListAsync();
            var dump = string.Join("; ", deployments.Select(d =>
                $"id={d.Id} status={d.Status} failure={d.FailureReason} exit={d.ExitCode}"));
            throw new Exception($"Timed out waiting for .alert-success. Dialog text: [{dialogText}]. DB rows: [{dump}]");
        }
        resultText = await swapDialog.Locator(".alert-success").InnerTextAsync();
        Assert.Contains("qwen2.5-0.5b", resultText);

        await swapDialog.Locator("button:has-text('Done')").ClickAsync();
        await swapDialog.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Hidden });

        // --- Overview reflects the live llama.cpp deployment with no vLLM-only panels blowing up ---
        await page.ReloadAsync(new PageReloadOptions { WaitUntil = WaitUntilState.NetworkIdle });
        await page.Locator("text=Serving: qwen2.5-0.5b").WaitForAsync();
    }
}
