using System.Text.Json;
using Dashboard.Core.Abstractions;
using Dashboard.Infrastructure.Identity;
using Dashboard.Infrastructure.Persistence.V2;
using Dashboard.Infrastructure.Supervisor;
using DotNet.Testcontainers.Containers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Dashboard.Infrastructure.Tests;

/// <summary>P12's Definition of done: reconcile state computation and the
/// Adopt/Fix/Mark-stopped actions, against a REAL supervisor container (same
/// convention as <see cref="SwapServiceTests"/>) -- a seeded mismatch must
/// surface as "mismatch" with both values, and nothing happens without an
/// explicit action.</summary>
[Collection("PostgresAndSupervisor")]
public class BootReconcilerTests : IAsyncDisposable
{
    private readonly PostgresFixture _postgres;
    private readonly SupervisorImageFixture _supervisorImage;
    private IContainer? _container;

    public BootReconcilerTests(PostgresFixture postgres, SupervisorImageFixture supervisorImage)
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

    private async Task<(IBootReconciler Reconciler, IReconcileActions Actions, ISwapService Swap, IDeploymentEventApplier Applier, IServiceScopeFactory ScopeFactory, string AdminUrl)>
        NewReconcilerAsync()
    {
        _container = await _supervisorImage.StartContainerAsync();
        var adminUrl = SupervisorImageFixture.AdminUrl(_container);

        var connectionString = await _postgres.CreateFreshDatabaseAsync();
        var services = new ServiceCollection();
        services.AddDbContext<AppDbContext>(o => o.UseNpgsql(connectionString));
        services.AddHttpClient("supervisor");
        services.AddSignalR();
        services.AddSingleton<ISupervisorTokenProvider, FixedTokenProvider>();
        services.AddSingleton<IReadOnlyGuard, ReadOnlyGuard>();
        services.AddSingleton<IServerService, ServerService>();
        services.AddSingleton<IInstanceService, InstanceService>();
        services.AddSingleton<IModelService, ModelService>();
        services.AddSingleton<IRecipeService, RecipeService>();
        services.AddSingleton<IDeploymentService, DeploymentService>();
        services.AddSingleton<ISupervisorClient, SupervisorClient>();
        services.AddSingleton<IDeploymentEventApplier, DeploymentEventApplier>();
        services.AddSingleton<ISwapService, SwapService>();
        services.AddSingleton<BootReconciler>();
        services.AddSingleton<IBootReconciler>(sp => sp.GetRequiredService<BootReconciler>());
        services.AddSingleton<IReconcileActions>(sp => sp.GetRequiredService<BootReconciler>());
        var provider = services.BuildServiceProvider();

        using var scope = provider.CreateScope();
        await scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.MigrateAsync();

        return (
            provider.GetRequiredService<IBootReconciler>(), provider.GetRequiredService<IReconcileActions>(),
            provider.GetRequiredService<ISwapService>(), provider.GetRequiredService<IDeploymentEventApplier>(),
            provider.GetRequiredService<IServiceScopeFactory>(), adminUrl);
    }

    private static async Task<(Server Server, Instance Instance, Model Model)> SeedAsync(
        IServiceScopeFactory scopeFactory, string? adminUrl)
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

    private static async Task<Recipe> AddRecipeAsync(IServiceScopeFactory scopeFactory, int serverId, string name, string servedModelName)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var recipe = new Recipe { ServerId = serverId, Name = name, ServedModelName = servedModelName };
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

    private static async Task<Deployment> DriveToTerminalAsync(
        IServiceScopeFactory scopeFactory, IDeploymentEventApplier applier, string adminUrl, string token, int deploymentId)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            await applier.CatchUpAsync(deploymentId, adminUrl, token, CancellationToken.None);
            var deployment = await GetAsync(scopeFactory, deploymentId);
            if (deployment!.Status != "pending")
                return deployment;
            await Task.Delay(200);
        }
        throw new TimeoutException($"Deployment {deploymentId} never left 'pending'.");
    }

    [Fact]
    public async Task ReconcileInstance_SupervisorAndDbAgree_Ok()
    {
        var (reconciler, _, swap, applier, scopeFactory, adminUrl) = await NewReconcilerAsync();
        var (server, instance, model) = await SeedAsync(scopeFactory, adminUrl);
        var recipe = await AddRecipeAsync(scopeFactory, server.Id, "default", "main");

        var swapped = await swap.SwapAsync(instance.Id, model.Id, recipe.Id);
        await DriveToTerminalAsync(scopeFactory, applier, adminUrl, SupervisorImageFixture.Token, swapped.DeploymentId!.Value);

        var state = await reconciler.ReconcileInstanceAsync(instance.Id);
        Assert.Equal(ReconcileStatus.Ok, state.Status);
        Assert.Equal(swapped.DeploymentId, state.DbActiveDeploymentId);
        Assert.NotNull(state.SupervisorArgv);
    }

    [Fact]
    public async Task ReconcileInstance_NoAdminUrl_NeedsRegistration()
    {
        var (reconciler, _, _, _, scopeFactory, _) = await NewReconcilerAsync();
        var (_, instance, _) = await SeedAsync(scopeFactory, adminUrl: null);

        var state = await reconciler.ReconcileInstanceAsync(instance.Id);
        Assert.Equal(ReconcileStatus.NeedsRegistration, state.Status);
    }

    [Fact]
    public async Task ReconcileInstance_SupervisorUnreachable_Down()
    {
        var (reconciler, _, _, _, scopeFactory, _) = await NewReconcilerAsync();
        var (_, instance, _) = await SeedAsync(scopeFactory, adminUrl: "http://127.0.0.1:1/admin");

        var state = await reconciler.ReconcileInstanceAsync(instance.Id);
        Assert.Equal(ReconcileStatus.Down, state.Status);
    }

    [Fact]
    public async Task ReconcileInstance_DbActiveButSupervisorIdle_IdleVsActive_AndMarkStoppedClearsIt()
    {
        var (reconciler, actions, swap, applier, scopeFactory, adminUrl) = await NewReconcilerAsync();
        var (server, instance, model) = await SeedAsync(scopeFactory, adminUrl);
        var recipe = await AddRecipeAsync(scopeFactory, server.Id, "default", "main");

        var swapped = await swap.SwapAsync(instance.Id, model.Id, recipe.Id);
        await DriveToTerminalAsync(scopeFactory, applier, adminUrl, SupervisorImageFixture.Token, swapped.DeploymentId!.Value);

        // Stop the supervisor directly (bypassing SwapService) so the DB's
        // own "active" row is left stale -- exactly the scenario the page
        // describes: "supervisor idle but DB says active".
        await swap.StopAsync(instance.Id);
        // StopAsync also clears the DB row via the normal path -- reseed a
        // stale "active" row by hand to simulate the DB not having heard
        // about the stop (the actual bug case this status exists for).
        using (var scope = scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var row = await db.DeploymentsV2.FindAsync(swapped.DeploymentId!.Value);
            row!.Status = "active";
            row.StopReason = null;
            row.StoppedAt = null;
            await db.SaveChangesAsync();
        }

        var state = await reconciler.ReconcileInstanceAsync(instance.Id);
        Assert.Equal(ReconcileStatus.IdleVsActive, state.Status);
        Assert.Equal(swapped.DeploymentId, state.DbActiveDeploymentId);

        await actions.MarkStoppedAsync(instance.Id);
        var markedRow = await GetAsync(scopeFactory, swapped.DeploymentId!.Value);
        Assert.Equal("stopped", markedRow!.Status);
        Assert.Equal("manual", markedRow.StopReason);

        var clearedState = await reconciler.ReconcileInstanceAsync(instance.Id);
        Assert.Equal(ReconcileStatus.Ok, clearedState.Status);
    }

    [Fact]
    public async Task ReconcileInstance_SupervisorServingSomethingDbDoesNotKnow_Mismatch_AndAdoptRecordsIt()
    {
        var (reconciler, actions, swap, applier, scopeFactory, adminUrl) = await NewReconcilerAsync();
        var (server, instance, model) = await SeedAsync(scopeFactory, adminUrl);
        var recipe = await AddRecipeAsync(scopeFactory, server.Id, "default", "main");

        var swapped = await swap.SwapAsync(instance.Id, model.Id, recipe.Id);
        var row = await DriveToTerminalAsync(scopeFactory, applier, adminUrl, SupervisorImageFixture.Token, swapped.DeploymentId!.Value);
        Assert.Equal("active", row.Status);

        // Simulate the DB having lost track entirely -- stale-forget the row
        // without telling the supervisor, which keeps serving it regardless.
        using (var scope = scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var r = await db.DeploymentsV2.FindAsync(swapped.DeploymentId!.Value);
            r!.Status = "stopped";
            r.StopReason = "manual";
            await db.SaveChangesAsync();
        }

        var state = await reconciler.ReconcileInstanceAsync(instance.Id);
        Assert.Equal(ReconcileStatus.Mismatch, state.Status);
        Assert.Null(state.DbActiveDeploymentId);
        Assert.NotNull(state.SupervisorArgv);

        await actions.AdoptAsync(instance.Id, model.Id, recipe.Id, state.SupervisorArgv!);

        var adoptedState = await reconciler.ReconcileInstanceAsync(instance.Id);
        Assert.Equal(ReconcileStatus.Ok, adoptedState.Status);
        Assert.NotNull(adoptedState.DbActiveDeploymentId);

        var adopted = await GetAsync(scopeFactory, adoptedState.DbActiveDeploymentId!.Value);
        Assert.Equal("active", adopted!.Status);
        Assert.Equal(state.SupervisorArgv, JsonSerializer.Deserialize<List<string>>(adopted.ArgsSnapshot));
    }

    [Fact]
    public async Task ReconcileInstance_DbSaysDifferentModelThanSupervisor_Mismatch_AndFixForcesDbsVersion()
    {
        var (reconciler, _, swap, applier, scopeFactory, adminUrl) = await NewReconcilerAsync();
        var (server, instance, modelA) = await SeedAsync(scopeFactory, adminUrl);
        var recipe = await AddRecipeAsync(scopeFactory, server.Id, "default", "main");

        var first = await swap.SwapAsync(instance.Id, modelA.Id, recipe.Id);
        var firstRow = await DriveToTerminalAsync(scopeFactory, applier, adminUrl, SupervisorImageFixture.Token, first.DeploymentId!.Value);
        Assert.Equal("active", firstRow.Status);

        // Give the DB a DIFFERENT "active" row with a DIFFERENT argv (never
        // actually deployed) by direct manipulation -- simulating e.g. a
        // crashed catch-up that left the DB thinking a newer deployment won
        // when it didn't. The argv must genuinely differ from what's really
        // running, since that's what the reconciler now compares (the
        // supervisor clears current_deployment_id the instant a deploy
        // concludes, so a deployment-id comparison can't work once "ready")
        // -- but it must still be a deployable argv (fake_vllm's strict
        // argparse rejects unknown flags), so just change the
        // --served-model-name VALUE rather than adding a new flag.
        var divergentArgv = JsonSerializer.Deserialize<List<string>>(firstRow.ArgsSnapshot)!;
        var servedNameIndex = divergentArgv.IndexOf("main");
        Assert.True(servedNameIndex >= 0, "Expected the built argv to contain the recipe's served-model-name value.");
        divergentArgv[servedNameIndex] = "stale-never-really-running";
        Deployment staleActive;
        using (var scope = scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var old = await db.DeploymentsV2.FindAsync(firstRow.Id);
            old!.Status = "stopped";
            old.StopReason = "manual";

            staleActive = new Deployment
            {
                InstanceId = instance.Id, ModelId = modelA.Id, RecipeId = recipe.Id,
                ArgsSnapshot = JsonSerializer.Serialize(divergentArgv), Status = "active", StartedAt = DateTime.UtcNow,
            };
            db.DeploymentsV2.Add(staleActive);
            await db.SaveChangesAsync();
        }

        var mismatch = await reconciler.ReconcileInstanceAsync(instance.Id);
        Assert.Equal(ReconcileStatus.Mismatch, mismatch.Status);
        Assert.Equal(staleActive.Id, mismatch.DbActiveDeploymentId);
        Assert.NotNull(mismatch.SupervisorArgv);

        var fixResult = await swap.ForceRedeployAsync(instance.Id);
        Assert.Equal("pending", fixResult.Outcome);
        var fixedRow = await DriveToTerminalAsync(scopeFactory, applier, adminUrl, SupervisorImageFixture.Token, fixResult.DeploymentId!.Value);
        Assert.Equal("active", fixedRow.Status);

        var fixedState = await reconciler.ReconcileInstanceAsync(instance.Id);
        Assert.Equal(ReconcileStatus.Ok, fixedState.Status);
        Assert.Equal(fixResult.DeploymentId, fixedState.DbActiveDeploymentId);
    }
}
