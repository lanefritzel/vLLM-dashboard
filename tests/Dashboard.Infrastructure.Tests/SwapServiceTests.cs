using System.Text.Json;
using Dashboard.Core.Abstractions;
using Dashboard.Core.Supervisor;
using Dashboard.Infrastructure.Identity;
using Dashboard.Infrastructure.Persistence.V2;
using Dashboard.Infrastructure.Supervisor;
using DotNet.Testcontainers.Containers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Dashboard.Infrastructure.Tests;

/// <summary>P10a/P10b's Definition of done: the first end-to-end swap, driven
/// by SwapService against a REAL supervisor container (not a C# fake) and
/// the fake vLLM test double it already carries. Swap/Rollback/Restart
/// return "pending" immediately as of P10b -- the eventual outcome is
/// resolved by <see cref="IDeploymentEventApplier"/>, driven here via its
/// real catch-up path (polling the real container's own GET
/// /admin/deployments/{id}/events, not a C# fake) since there's no publicly
/// reachable receiver endpoint for the container to push to in this test
/// process. Each test gets its own fresh Postgres database AND its own
/// fresh supervisor container (from the image built once by
/// SupervisorImageFixture) -- see that file's own comment for why a shared
/// container isn't safe across tests.</summary>
[Collection("PostgresAndSupervisor")]
public class SwapServiceTests : IAsyncDisposable
{
    private readonly PostgresFixture _postgres;
    private readonly SupervisorImageFixture _supervisorImage;
    private IContainer? _container;

    public SwapServiceTests(PostgresFixture postgres, SupervisorImageFixture supervisorImage)
    {
        _postgres = postgres;
        _supervisorImage = supervisorImage;
    }

    public async ValueTask DisposeAsync()
    {
        if (_container is not null)
            await _container.DisposeAsync();
    }

    private async Task<(ISwapService Swap, IDeploymentEventApplier Applier, IServiceScopeFactory ScopeFactory, string AdminUrl)> NewSwapServiceAsync()
    {
        _container = await _supervisorImage.StartContainerAsync();
        var adminUrl = SupervisorImageFixture.AdminUrl(_container);

        var connectionString = await _postgres.CreateFreshDatabaseAsync();
        var services = new ServiceCollection();
        services.AddDbContext<AppDbContext>(o => o.UseNpgsql(connectionString));
        services.AddHttpClient("supervisor");
        services.AddSignalR(); // for IHubContext<DeploymentHub> -- no real clients connect in these tests
        services.AddSingleton<IReadOnlyGuard, ReadOnlyGuard>();
        services.AddSingleton<IServerService, ServerService>();
        services.AddSingleton<IInstanceService, InstanceService>();
        services.AddSingleton<IModelService, ModelService>();
        services.AddSingleton<IRecipeService, RecipeService>();
        services.AddSingleton<IDeploymentService, DeploymentService>();
        services.AddSingleton<ISupervisorClient, SupervisorClient>();
        services.AddSingleton<IDeploymentEventApplier, DeploymentEventApplier>();
        services.AddSingleton<ISwapService, SwapService>();
        var provider = services.BuildServiceProvider();

        using var scope = provider.CreateScope();
        await scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.MigrateAsync();

        return (
            provider.GetRequiredService<ISwapService>(), provider.GetRequiredService<IDeploymentEventApplier>(),
            provider.GetRequiredService<IServiceScopeFactory>(), adminUrl);
    }

    private static async Task<(Server Server, Instance Instance, Model Model)> SeedAsync(
        IServiceScopeFactory scopeFactory, string adminUrl)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var server = new Server { Label = "primary", ModelsDir = "/models" };
        db.ServersV2.Add(server);
        await db.SaveChangesAsync();

        var instance = new Instance { ServerId = server.Id, Label = "main", ProxySlug = "main", AdminUrl = adminUrl, Port = 9999 };
        db.InstancesV2.Add(instance);

        var model = new Model { ServerId = server.Id, Name = "fake-model", Path = "/models/hf-cache/fake-model" };
        db.ModelsV2.Add(model);

        await db.SaveChangesAsync();
        return (server, instance, model);
    }

    private static async Task<Recipe> AddRecipeAsync(
        IServiceScopeFactory scopeFactory, int serverId, string name, string servedModelName, string? extraArgsJson = null)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var recipe = new Recipe { ServerId = serverId, Name = name, ServedModelName = servedModelName };
        if (extraArgsJson is not null)
            recipe.ExtraArgs = extraArgsJson;
        db.RecipesV2.Add(recipe);
        await db.SaveChangesAsync();
        return recipe;
    }

    private static async Task<Deployment?> GetAsync(IServiceScopeFactory scopeFactory, int id)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.DeploymentsV2.FindAsync(id);
    }

    private static async Task<int> CountDeploymentsAsync(IServiceScopeFactory scopeFactory, int instanceId, string? status = null)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var query = db.DeploymentsV2.Where(d => d.InstanceId == instanceId);
        if (status is not null)
            query = query.Where(d => d.Status == status);
        return await query.CountAsync();
    }

    /// <summary>Drives the real catch-up path (the applier's own GET
    /// .../events call against the real container) until this deployment
    /// leaves "pending", standing in for the push receiver -- there's no
    /// publicly reachable endpoint for the container to push to from this
    /// test process. Returns the final row.</summary>
    private static async Task<Deployment> DriveToTerminalAsync(
        IServiceScopeFactory scopeFactory, IDeploymentEventApplier applier, string adminUrl, int deploymentId,
        TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(30));
        while (DateTime.UtcNow < deadline)
        {
            await applier.CatchUpAsync(deploymentId, adminUrl, CancellationToken.None);
            var deployment = await GetAsync(scopeFactory, deploymentId);
            if (deployment!.Status != "pending")
                return deployment;
            await Task.Delay(200);
        }
        throw new TimeoutException($"Deployment {deploymentId} never left 'pending'.");
    }

    [Fact]
    public async Task Swap_AToB_FlipsRowsAndFreezesArgsSnapshot()
    {
        var (swap, applier, scopeFactory, adminUrl) = await NewSwapServiceAsync();
        var (server, instance, modelA) = await SeedAsync(scopeFactory, adminUrl);
        var recipeA = await AddRecipeAsync(scopeFactory, server.Id, "recipe-a", "main");

        var first = await swap.SwapAsync(instance.Id, modelA.Id, recipeA.Id);
        Assert.Equal("pending", first.Outcome);
        var firstRow = await DriveToTerminalAsync(scopeFactory, applier, adminUrl, first.DeploymentId!.Value);
        Assert.Equal("active", firstRow.Status);
        var argv = JsonSerializer.Deserialize<List<string>>(firstRow.ArgsSnapshot)!;
        Assert.Contains("--served-model-name", argv);
        Assert.Contains("main", argv);

        Model modelB;
        using (var scope = scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            modelB = new Model { ServerId = server.Id, Name = "fake-model-b", Path = "/models/hf-cache/fake-model-b" };
            db.ModelsV2.Add(modelB);
            await db.SaveChangesAsync();
        }

        var second = await swap.SwapAsync(instance.Id, modelB.Id, recipeA.Id);
        Assert.Equal("pending", second.Outcome);
        var secondRow = await DriveToTerminalAsync(scopeFactory, applier, adminUrl, second.DeploymentId!.Value);
        Assert.Equal("active", secondRow.Status);

        var reloadedFirst = await GetAsync(scopeFactory, firstRow.Id);
        Assert.Equal("stopped", reloadedFirst!.Status);
        Assert.Equal("swapped", reloadedFirst.StopReason);
    }

    [Fact]
    public async Task Swap_DifferentServedModelNameWithoutAcknowledge_ThrowsWithBothNames()
    {
        var (swap, applier, scopeFactory, adminUrl) = await NewSwapServiceAsync();
        var (server, instance, model) = await SeedAsync(scopeFactory, adminUrl);
        var recipeA = await AddRecipeAsync(scopeFactory, server.Id, "recipe-a", "main");
        var recipeB = await AddRecipeAsync(scopeFactory, server.Id, "recipe-b", "other");

        var first = await swap.SwapAsync(instance.Id, model.Id, recipeA.Id);
        await DriveToTerminalAsync(scopeFactory, applier, adminUrl, first.DeploymentId!.Value);

        var ex = await Assert.ThrowsAsync<ServedNameChangeRequiresAcknowledgementException>(
            () => swap.SwapAsync(instance.Id, model.Id, recipeB.Id));
        Assert.Equal("main", ex.OldServedModelName);
        Assert.Equal("other", ex.NewServedModelName);

        var acknowledged = await swap.SwapAsync(instance.Id, model.Id, recipeB.Id, acknowledgeServedNameChange: true);
        Assert.Equal("pending", acknowledged.Outcome);
        var row = await DriveToTerminalAsync(scopeFactory, applier, adminUrl, acknowledged.DeploymentId!.Value);
        Assert.Equal("active", row.Status);
    }

    [Fact]
    public async Task Swap_ToACrashingModel_RowFailedWithFailureReport_PreviousRowStoppedAndNotRestored()
    {
        var (swap, applier, scopeFactory, adminUrl) = await NewSwapServiceAsync();
        var (server, instance, model) = await SeedAsync(scopeFactory, adminUrl);
        var goodRecipe = await AddRecipeAsync(scopeFactory, server.Id, "good", "main");
        var crashRecipe = await AddRecipeAsync(
            scopeFactory, server.Id, "crash", "main", extraArgsJson: """[{"key":"fake-crash","value":null}]""");

        var first = await swap.SwapAsync(instance.Id, model.Id, goodRecipe.Id);
        var firstRow = await DriveToTerminalAsync(scopeFactory, applier, adminUrl, first.DeploymentId!.Value);
        Assert.Equal("active", firstRow.Status);

        var second = await swap.SwapAsync(instance.Id, model.Id, crashRecipe.Id);
        Assert.Equal("pending", second.Outcome);
        var secondRow = await DriveToTerminalAsync(scopeFactory, applier, adminUrl, second.DeploymentId!.Value);

        Assert.Equal("failed", secondRow.Status);
        Assert.Equal("process_exited", secondRow.FailureReason);
        Assert.NotNull(secondRow.ExitCode);
        Assert.False(string.IsNullOrWhiteSpace(secondRow.LogTail));

        // The supervisor stopped the old model before the new one crashed, and there is
        // no auto-rollback -- so the previous row must no longer claim to be "active".
        // It is stopped (reason "swapped"), and the old model is NOT silently restored
        // (no new row for it).
        var reloadedFirst = await GetAsync(scopeFactory, firstRow.Id);
        Assert.Equal("stopped", reloadedFirst!.Status);
        Assert.Equal("swapped", reloadedFirst.StopReason);
    }

    [Fact]
    public async Task Swap_SameModelAndRecipeAlreadyActive_ReturnsAlreadyActiveWithNoNewRow()
    {
        var (swap, applier, scopeFactory, adminUrl) = await NewSwapServiceAsync();
        var (server, instance, model) = await SeedAsync(scopeFactory, adminUrl);
        var recipe = await AddRecipeAsync(scopeFactory, server.Id, "default", "main");

        var first = await swap.SwapAsync(instance.Id, model.Id, recipe.Id);
        await DriveToTerminalAsync(scopeFactory, applier, adminUrl, first.DeploymentId!.Value);

        var countBefore = await CountDeploymentsAsync(scopeFactory, instance.Id);
        var second = await swap.SwapAsync(instance.Id, model.Id, recipe.Id);
        Assert.Equal("already_active", second.Outcome);
        Assert.Null(second.DeploymentId);
        Assert.Equal(countBefore, await CountDeploymentsAsync(scopeFactory, instance.Id));
    }

    [Fact]
    public async Task Rollback_ReplaysThePreviousArgsSnapshotByteForByte()
    {
        var (swap, applier, scopeFactory, adminUrl) = await NewSwapServiceAsync();
        var (server, instance, modelA) = await SeedAsync(scopeFactory, adminUrl);
        var recipe = await AddRecipeAsync(scopeFactory, server.Id, "default", "main");

        var first = await swap.SwapAsync(instance.Id, modelA.Id, recipe.Id);
        var firstRow = await DriveToTerminalAsync(scopeFactory, applier, adminUrl, first.DeploymentId!.Value);

        Model modelB;
        using (var scope = scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            modelB = new Model { ServerId = server.Id, Name = "fake-model-b", Path = "/models/hf-cache/fake-model-b" };
            db.ModelsV2.Add(modelB);
            await db.SaveChangesAsync();
        }
        var second = await swap.SwapAsync(instance.Id, modelB.Id, recipe.Id);
        await DriveToTerminalAsync(scopeFactory, applier, adminUrl, second.DeploymentId!.Value);

        var rollback = await swap.RollbackAsync(instance.Id);
        Assert.Equal("pending", rollback.Outcome);
        var rollbackRow = await DriveToTerminalAsync(scopeFactory, applier, adminUrl, rollback.DeploymentId!.Value);
        Assert.Equal("active", rollbackRow.Status);
        Assert.Equal(firstRow.ArgsSnapshot, rollbackRow.ArgsSnapshot);

        // The row rollback replaced (modelB's, the one that was active just
        // before the rollback) gets stop_reason="rollback".
        var replacedRow = await GetAsync(scopeFactory, second.DeploymentId!.Value);
        Assert.Equal("rollback", replacedRow!.StopReason);
    }

    [Fact]
    public async Task Stop_LeavesInstanceIdleWithNoPendingOrActiveRow()
    {
        var (swap, applier, scopeFactory, adminUrl) = await NewSwapServiceAsync();
        var (server, instance, model) = await SeedAsync(scopeFactory, adminUrl);
        var recipe = await AddRecipeAsync(scopeFactory, server.Id, "default", "main");
        var deployed = await swap.SwapAsync(instance.Id, model.Id, recipe.Id);
        await DriveToTerminalAsync(scopeFactory, applier, adminUrl, deployed.DeploymentId!.Value);

        await swap.StopAsync(instance.Id);

        var row = await GetAsync(scopeFactory, deployed.DeploymentId!.Value);
        Assert.Equal("stopped", row!.Status);
        Assert.Equal("manual", row.StopReason);
        Assert.Equal(0, await CountDeploymentsAsync(scopeFactory, instance.Id, status: "active"));
        Assert.Equal(0, await CountDeploymentsAsync(scopeFactory, instance.Id, status: "pending"));
    }

    [Fact]
    public async Task Cancel_Phase2_GoesIdleAndStopsThePreviousRowToo()
    {
        var (swap, applier, scopeFactory, adminUrl) = await NewSwapServiceAsync();
        var (server, instance, modelA) = await SeedAsync(scopeFactory, adminUrl);
        var recipe = await AddRecipeAsync(scopeFactory, server.Id, "default", "main");
        var first = await swap.SwapAsync(instance.Id, modelA.Id, recipe.Id);
        await DriveToTerminalAsync(scopeFactory, applier, adminUrl, first.DeploymentId!.Value);

        Model modelB;
        using (var scope = scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            modelB = new Model { ServerId = server.Id, Name = "fake-model-b", Path = "/models/hf-cache/fake-model-b" };
            db.ModelsV2.Add(modelB);
            await db.SaveChangesAsync();
        }

        var slowRecipe = await AddRecipeAsync(
            scopeFactory, server.Id, "slow", "main", extraArgsJson: """[{"key":"fake-slow-start","value":"30"}]""");

        // SwapAsync returns as soon as the supervisor accepts the POST --
        // the fake vLLM is still slow-starting at that point, so Cancel
        // lands mid-flight without any extra waiting.
        var swapResult = await swap.SwapAsync(instance.Id, modelB.Id, slowRecipe.Id);
        Assert.Equal("pending", swapResult.Outcome);

        var cancelled = await swap.CancelAsync(instance.Id);
        Assert.Equal("cancelled", cancelled.Outcome);

        var pendingRow = await GetAsync(scopeFactory, cancelled.DeploymentId!.Value);
        Assert.Equal("stopped", pendingRow!.Status);
        Assert.Equal("cancelled", pendingRow.StopReason);

        var firstRow = await GetAsync(scopeFactory, first.DeploymentId!.Value);
        Assert.Equal("stopped", firstRow!.Status);
        Assert.Equal("cancelled", firstRow.StopReason);
        Assert.Equal(0, await CountDeploymentsAsync(scopeFactory, instance.Id, status: "active"));
        Assert.Equal(0, await CountDeploymentsAsync(scopeFactory, instance.Id, status: "pending"));
    }

    [Fact]
    public async Task CrashAfterReady_RowStopped_StopReasonCrash()
    {
        var (swap, applier, scopeFactory, adminUrl) = await NewSwapServiceAsync();
        var (server, instance, model) = await SeedAsync(scopeFactory, adminUrl);
        var recipe = await AddRecipeAsync(
            scopeFactory, server.Id, "default", "main", extraArgsJson: """[{"key":"fake-exit-after","value":"2"}]""");

        var deployed = await swap.SwapAsync(instance.Id, model.Id, recipe.Id);
        var row = await DriveToTerminalAsync(scopeFactory, applier, adminUrl, deployed.DeploymentId!.Value);
        Assert.Equal("active", row.Status);

        // The fake child exits on its own ~2s after reaching ready -- the
        // supervisor's own post-ready watcher (P03a) notices within ~1s and
        // records a "crashed" event; catch-up again (this deployment is no
        // longer "pending" so DriveToTerminalAsync's own loop doesn't apply)
        // picks it up once it's actually happened.
        Deployment? reloaded = null;
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
        while (DateTime.UtcNow < deadline)
        {
            await applier.CatchUpAsync(deployed.DeploymentId.Value, adminUrl, CancellationToken.None);
            reloaded = await GetAsync(scopeFactory, deployed.DeploymentId.Value);
            if (reloaded!.Status == "stopped")
                break;
            await Task.Delay(300);
        }

        Assert.Equal("stopped", reloaded!.Status);
        Assert.Equal("crash", reloaded.StopReason);
    }
}
