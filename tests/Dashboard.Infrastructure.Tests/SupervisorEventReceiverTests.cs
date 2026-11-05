using Dashboard.Core.Abstractions;
using Dashboard.Core.Supervisor;
using Dashboard.Infrastructure.Identity;
using Dashboard.Infrastructure.Persistence.V2;
using Dashboard.Infrastructure.Supervisor;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Dashboard.Infrastructure.Tests;

/// <summary>"Not from this machine -> 401 and nothing written." Exercises
/// SupervisorEventReceiver directly (the logic Program.cs's minimal API route
/// delegates to) rather than a full web host. Used to gate on a per-instance
/// bearer token; now gates on the caller being loopback (<c>isLoopback</c>),
/// since the supervisor always runs alongside the dashboard on one host and
/// Program.cs derives that flag from the request's own remote IP.</summary>
[Collection("Postgres")]
public class SupervisorEventReceiverTests(PostgresFixture postgres)
{
    private async Task<(SupervisorEventReceiver Receiver, IServiceScopeFactory ScopeFactory)> NewReceiverAsync()
    {
        var connectionString = await postgres.CreateFreshDatabaseAsync();
        var services = new ServiceCollection();
        services.AddDbContext<AppDbContext>(o => o.UseNpgsql(connectionString));
        services.AddHttpClient("supervisor");
        services.AddSignalR();
        services.AddSingleton<IReadOnlyGuard, ReadOnlyGuard>();
        services.AddSingleton<IDeploymentService, DeploymentService>();
        services.AddSingleton<IInstanceService, InstanceService>();
        services.AddSingleton<ISupervisorClient, SupervisorClient>();
        services.AddSingleton<IDeploymentEventApplier, DeploymentEventApplier>();
        services.AddSingleton<SupervisorEventReceiver>();
        var provider = services.BuildServiceProvider();

        using var scope = provider.CreateScope();
        await scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.MigrateAsync();

        return (provider.GetRequiredService<SupervisorEventReceiver>(), provider.GetRequiredService<IServiceScopeFactory>());
    }

    private static async Task<Deployment> SeedPendingDeploymentAsync(IServiceScopeFactory scopeFactory)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var server = new Server { Label = "primary", ModelsDir = "/models" };
        db.ServersV2.Add(server);
        await db.SaveChangesAsync();

        var instance = new Instance { ServerId = server.Id, Label = "main", ProxySlug = "main", AdminUrl = "http://unused.invalid", Port = 9999 };
        var model = new Model { ServerId = server.Id, Name = "fake-model", Path = "/models/hf-cache/fake-model" };
        db.InstancesV2.Add(instance);
        db.ModelsV2.Add(model);
        await db.SaveChangesAsync();

        var recipe = new Recipe { ServerId = server.Id, Name = "default", ServedModelName = "main" };
        db.RecipesV2.Add(recipe);
        await db.SaveChangesAsync();

        var deployment = new Deployment
        {
            InstanceId = instance.Id, ModelId = model.Id, RecipeId = recipe.Id,
            ArgsSnapshot = "[]", Status = "pending", PendingStopReason = "swapped",
        };
        db.DeploymentsV2.Add(deployment);
        await db.SaveChangesAsync();
        return deployment;
    }

    [Fact]
    public async Task NotLoopback_Returns401AndWritesNothing()
    {
        var (receiver, scopeFactory) = await NewReceiverAsync();
        var deployment = await SeedPendingDeploymentAsync(scopeFactory);

        var evt = new SupervisorPushEvent { DeploymentId = deployment.Id.ToString(), Seq = 1, Kind = "state", State = "starting" };
        var result = await receiver.HandleAsync(evt, isLoopback: false);

        Assert.Equal(SupervisorEventReceiverResult.Unauthorized, result);
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(0, await db.DeploymentEventsV2.CountAsync());
    }

    [Fact]
    public async Task Loopback_Returns200AndApplies()
    {
        var (receiver, scopeFactory) = await NewReceiverAsync();
        var deployment = await SeedPendingDeploymentAsync(scopeFactory);

        var evt = new SupervisorPushEvent { DeploymentId = deployment.Id.ToString(), Seq = 1, Kind = "state", State = "starting" };
        var result = await receiver.HandleAsync(evt, isLoopback: true);

        Assert.Equal(SupervisorEventReceiverResult.Ok, result);
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(1, await db.DeploymentEventsV2.CountAsync(e => e.DeploymentId == deployment.Id));
    }

    [Fact]
    public async Task UnknownDeploymentId_Returns404()
    {
        var (receiver, _) = await NewReceiverAsync();

        var evt = new SupervisorPushEvent { DeploymentId = "999999", Seq = 1, Kind = "state", State = "starting" };
        var result = await receiver.HandleAsync(evt, isLoopback: true);

        Assert.Equal(SupervisorEventReceiverResult.NotFound, result);
    }
}
