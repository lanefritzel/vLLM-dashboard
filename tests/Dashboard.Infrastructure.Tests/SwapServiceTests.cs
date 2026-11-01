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

/// <summary>P10a's Definition of done: the first end-to-end swap, driven by
/// SwapService against a REAL supervisor container (not a C# fake) and the
/// fake vLLM test double it already carries. Each test gets its own fresh
/// Postgres database AND its own fresh supervisor container (from the image
/// built once by SupervisorImageFixture) -- see that file's own comment for
/// why a shared container isn't safe across tests.</summary>
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

    private sealed class FixedTokenProvider : ISupervisorTokenProvider
    {
        public string? GetToken(int instanceId) => SupervisorImageFixture.Token;
    }

    private async Task<(ISwapService Swap, IServiceScopeFactory ScopeFactory, string AdminUrl)> NewSwapServiceAsync()
    {
        _container = await _supervisorImage.StartContainerAsync();
        var adminUrl = SupervisorImageFixture.AdminUrl(_container);

        var connectionString = await _postgres.CreateFreshDatabaseAsync();
        var services = new ServiceCollection();
        services.AddDbContext<AppDbContext>(o => o.UseNpgsql(connectionString));
        services.AddHttpClient("supervisor");
        services.AddSingleton<ISupervisorTokenProvider, FixedTokenProvider>();
        services.AddSingleton<IServerService, ServerService>();
        services.AddSingleton<IInstanceService, InstanceService>();
        services.AddSingleton<IModelService, ModelService>();
        services.AddSingleton<IRecipeService, RecipeService>();
        services.AddSingleton<IDeploymentService, DeploymentService>();
        services.AddSingleton<ISupervisorClient, SupervisorClient>();
        services.AddSingleton<IDeploymentTracker, PollingDeploymentTracker>();
        services.AddSingleton<ISwapService, SwapService>();
        var provider = services.BuildServiceProvider();

        using var scope = provider.CreateScope();
        await scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.MigrateAsync();

        return (provider.GetRequiredService<ISwapService>(), provider.GetRequiredService<IServiceScopeFactory>(), adminUrl);
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

    [Fact]
    public async Task Swap_AToB_FlipsRowsAndFreezesArgsSnapshot()
    {
        var (swap, scopeFactory, adminUrl) = await NewSwapServiceAsync();
        var (server, instance, modelA) = await SeedAsync(scopeFactory, adminUrl);
        var recipeA = await AddRecipeAsync(scopeFactory, server.Id, "recipe-a", "main");

        var first = await swap.SwapAsync(instance.Id, modelA.Id, recipeA.Id);
        Assert.Equal("swapped", first.Outcome);
        var firstRow = await GetAsync(scopeFactory, first.DeploymentId!.Value);
        Assert.Equal("active", firstRow!.Status);
        var argv = JsonSerializer.Deserialize<List<string>>(firstRow.ArgsSnapshot)!;
        Assert.Contains("--served-model-name", argv);
        Assert.Contains("main", argv);

        using (var scope = scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var modelB = new Model { ServerId = server.Id, Name = "fake-model-b", Path = "/models/hf-cache/fake-model-b" };
            db.ModelsV2.Add(modelB);
            await db.SaveChangesAsync();

            var second = await swap.SwapAsync(instance.Id, modelB.Id, recipeA.Id);
            Assert.Equal("swapped", second.Outcome);

            var secondRow = await db.DeploymentsV2.FindAsync(second.DeploymentId!.Value);
            Assert.Equal("active", secondRow!.Status);

            var reloadedFirst = await db.DeploymentsV2.FindAsync(firstRow.Id);
            Assert.Equal("stopped", reloadedFirst!.Status);
            Assert.Equal("swapped", reloadedFirst.StopReason);
        }
    }

    [Fact]
    public async Task Swap_DifferentServedModelNameWithoutAcknowledge_ThrowsWithBothNames()
    {
        var (swap, scopeFactory, adminUrl) = await NewSwapServiceAsync();
        var (server, instance, model) = await SeedAsync(scopeFactory, adminUrl);
        var recipeA = await AddRecipeAsync(scopeFactory, server.Id, "recipe-a", "main");
        var recipeB = await AddRecipeAsync(scopeFactory, server.Id, "recipe-b", "other");

        await swap.SwapAsync(instance.Id, model.Id, recipeA.Id);

        var ex = await Assert.ThrowsAsync<ServedNameChangeRequiresAcknowledgementException>(
            () => swap.SwapAsync(instance.Id, model.Id, recipeB.Id));
        Assert.Equal("main", ex.OldServedModelName);
        Assert.Equal("other", ex.NewServedModelName);

        var acknowledged = await swap.SwapAsync(instance.Id, model.Id, recipeB.Id, acknowledgeServedNameChange: true);
        Assert.Equal("swapped", acknowledged.Outcome);
    }

    [Fact]
    public async Task Swap_ToACrashingModel_RowFailed_PreviousRecordIntactAndNotRestored()
    {
        var (swap, scopeFactory, adminUrl) = await NewSwapServiceAsync();
        var (server, instance, model) = await SeedAsync(scopeFactory, adminUrl);
        var goodRecipe = await AddRecipeAsync(scopeFactory, server.Id, "good", "main");
        var crashRecipe = await AddRecipeAsync(
            scopeFactory, server.Id, "crash", "main", extraArgsJson: """[{"key":"fake-crash","value":null}]""");

        var first = await swap.SwapAsync(instance.Id, model.Id, goodRecipe.Id);
        Assert.Equal("swapped", first.Outcome);

        var second = await swap.SwapAsync(instance.Id, model.Id, crashRecipe.Id);
        Assert.Equal("failed", second.Outcome);
        Assert.Equal("process_exited", second.FailureReason);

        var secondRow = await GetAsync(scopeFactory, second.DeploymentId!.Value);
        Assert.Equal("failed", secondRow!.Status);

        // The previous (good) deployment's row must still be intact/active --
        // the old model is NOT silently restored (no new row for it), but the
        // dashboard's own bookkeeping for it was never touched by the failed attempt.
        var firstRow = await GetAsync(scopeFactory, first.DeploymentId!.Value);
        Assert.Equal("active", firstRow!.Status);
    }

    [Fact]
    public async Task Swap_SameModelAndRecipeAlreadyActive_ReturnsAlreadyActiveWithNoNewRow()
    {
        var (swap, scopeFactory, adminUrl) = await NewSwapServiceAsync();
        var (server, instance, model) = await SeedAsync(scopeFactory, adminUrl);
        var recipe = await AddRecipeAsync(scopeFactory, server.Id, "default", "main");

        var first = await swap.SwapAsync(instance.Id, model.Id, recipe.Id);
        Assert.Equal("swapped", first.Outcome);

        var countBefore = await CountDeploymentsAsync(scopeFactory, instance.Id);
        var second = await swap.SwapAsync(instance.Id, model.Id, recipe.Id);
        Assert.Equal("already_active", second.Outcome);
        Assert.Null(second.DeploymentId);
        Assert.Equal(countBefore, await CountDeploymentsAsync(scopeFactory, instance.Id));
    }

    [Fact]
    public async Task Rollback_ReplaysThePreviousArgsSnapshotByteForByte()
    {
        var (swap, scopeFactory, adminUrl) = await NewSwapServiceAsync();
        var (server, instance, modelA) = await SeedAsync(scopeFactory, adminUrl);
        var recipe = await AddRecipeAsync(scopeFactory, server.Id, "default", "main");

        var first = await swap.SwapAsync(instance.Id, modelA.Id, recipe.Id);
        var firstRow = await GetAsync(scopeFactory, first.DeploymentId!.Value);

        SwapResult second;
        using (var scope = scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var modelB = new Model { ServerId = server.Id, Name = "fake-model-b", Path = "/models/hf-cache/fake-model-b" };
            db.ModelsV2.Add(modelB);
            await db.SaveChangesAsync();
            second = await swap.SwapAsync(instance.Id, modelB.Id, recipe.Id);
        }

        var rollback = await swap.RollbackAsync(instance.Id);
        Assert.Equal("swapped", rollback.Outcome);
        var rollbackRow = await GetAsync(scopeFactory, rollback.DeploymentId!.Value);
        Assert.Equal(firstRow!.ArgsSnapshot, rollbackRow!.ArgsSnapshot);

        // The row rollback replaced (modelB's, the one that was active just
        // before the rollback) gets stop_reason="rollback".
        var replacedRow = await GetAsync(scopeFactory, second.DeploymentId!.Value);
        Assert.Equal("rollback", replacedRow!.StopReason);
    }

    [Fact]
    public async Task Stop_LeavesInstanceIdleWithNoPendingOrActiveRow()
    {
        var (swap, scopeFactory, adminUrl) = await NewSwapServiceAsync();
        var (server, instance, model) = await SeedAsync(scopeFactory, adminUrl);
        var recipe = await AddRecipeAsync(scopeFactory, server.Id, "default", "main");
        var deployed = await swap.SwapAsync(instance.Id, model.Id, recipe.Id);

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
        var (swap, scopeFactory, adminUrl) = await NewSwapServiceAsync();
        var (server, instance, modelA) = await SeedAsync(scopeFactory, adminUrl);
        var recipe = await AddRecipeAsync(scopeFactory, server.Id, "default", "main");
        var first = await swap.SwapAsync(instance.Id, modelA.Id, recipe.Id);
        Assert.Equal("swapped", first.Outcome);

        int modelBId;
        using (var scope = scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var modelB = new Model { ServerId = server.Id, Name = "fake-model-b", Path = "/models/hf-cache/fake-model-b" };
            db.ModelsV2.Add(modelB);
            await db.SaveChangesAsync();
            modelBId = modelB.Id;
        }

        var slowRecipe = await AddRecipeAsync(
            scopeFactory, server.Id, "slow", "main", extraArgsJson: """[{"key":"fake-slow-start","value":"30"}]""");

        // Fire the swap, but don't await it to terminal -- cancel it mid-flight.
        var swapTask = swap.SwapAsync(instance.Id, modelBId, slowRecipe.Id);
        await WaitUntilAsync(async () => (await GetPendingIdAsync(scopeFactory, instance.Id)) is not null, TimeSpan.FromSeconds(10));

        var cancelled = await swap.CancelAsync(instance.Id);
        Assert.Equal("cancelled", cancelled.Outcome);

        try { await swapTask; } catch { /* the in-flight SwapAsync call surfaces its own tracking exception once cancelled underneath it -- irrelevant here */ }

        var pendingRow = await GetAsync(scopeFactory, cancelled.DeploymentId!.Value);
        Assert.Equal("stopped", pendingRow!.Status);
        Assert.Equal("cancelled", pendingRow.StopReason);

        var firstRow = await GetAsync(scopeFactory, first.DeploymentId!.Value);
        Assert.Equal("stopped", firstRow!.Status);
        Assert.Equal("cancelled", firstRow.StopReason);
        Assert.Equal(0, await CountDeploymentsAsync(scopeFactory, instance.Id, status: "active"));
        Assert.Equal(0, await CountDeploymentsAsync(scopeFactory, instance.Id, status: "pending"));
    }

    private static async Task<int?> GetPendingIdAsync(IServiceScopeFactory scopeFactory, int instanceId)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return (await db.DeploymentsV2.FirstOrDefaultAsync(d => d.InstanceId == instanceId && d.Status == "pending"))?.Id;
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
        throw new TimeoutException("Condition was never met.");
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
}
