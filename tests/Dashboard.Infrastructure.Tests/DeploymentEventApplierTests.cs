using Dashboard.Core.Abstractions;
using Dashboard.Core.Supervisor;
using Dashboard.Infrastructure.Identity;
using Dashboard.Infrastructure.Persistence.V2;
using Dashboard.Infrastructure.Supervisor;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Dashboard.Infrastructure.Tests;

/// <summary>P10b's Definition of done, the parts that don't need a real
/// supervisor container: dedupe, report-kind failure persistence, and
/// log-kind never touching Postgres. None of these trigger a gap fetch, so
/// the admin URL passed is never actually dialed -- a real
/// <see cref="SupervisorClient"/> pointed at it would be fine too, but
/// there's no need to spin one up.</summary>
[Collection("Postgres")]
public class DeploymentEventApplierTests(PostgresFixture postgres)
{
    private const string UnusedAdminUrl = "http://unused.invalid";

    private async Task<(IDeploymentEventApplier Applier, IServiceScopeFactory ScopeFactory)> NewApplierAsync()
    {
        var connectionString = await postgres.CreateFreshDatabaseAsync();
        var services = new ServiceCollection();
        services.AddDbContext<AppDbContext>(o => o.UseNpgsql(connectionString));
        services.AddHttpClient("supervisor");
        services.AddSignalR();
        services.AddSingleton<IReadOnlyGuard, ReadOnlyGuard>();
        services.AddSingleton<IDeploymentService, DeploymentService>();
        services.AddSingleton<ISupervisorClient, SupervisorClient>();
        services.AddSingleton<IDeploymentEventApplier, DeploymentEventApplier>();
        var provider = services.BuildServiceProvider();

        using var scope = provider.CreateScope();
        await scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.MigrateAsync();

        return (provider.GetRequiredService<IDeploymentEventApplier>(), provider.GetRequiredService<IServiceScopeFactory>());
    }

    private static async Task<(Server Server, Instance Instance, Model Model, Recipe Recipe, Deployment Deployment)> SeedPendingDeploymentAsync(
        IServiceScopeFactory scopeFactory)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var server = new Server { Label = "primary", ModelsDir = "/models" };
        db.ServersV2.Add(server);
        await db.SaveChangesAsync();

        var instance = new Instance { ServerId = server.Id, Label = "main", ProxySlug = "main", AdminUrl = UnusedAdminUrl, Port = 9999 };
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

        return (server, instance, model, recipe, deployment);
    }

    [Fact]
    public async Task DuplicateStateEventDelivery_IsANoOp()
    {
        var (applier, scopeFactory) = await NewApplierAsync();
        var (_, _, _, _, deployment) = await SeedPendingDeploymentAsync(scopeFactory);

        var evt = new SupervisorPushEvent { DeploymentId = deployment.Id.ToString(), Seq = 1, Kind = "state", State = "starting" };
        await applier.ApplyWithGapCatchUpAsync(deployment.Id, UnusedAdminUrl, evt, CancellationToken.None);
        await applier.ApplyWithGapCatchUpAsync(deployment.Id, UnusedAdminUrl, evt, CancellationToken.None); // redelivered

        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var count = await db.DeploymentEventsV2.CountAsync(e => e.DeploymentId == deployment.Id && e.Seq == 1);
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task ReportKindPush_WritesFailureColumnsOnTheDeploymentRow()
    {
        var (applier, scopeFactory) = await NewApplierAsync();
        var (_, _, _, _, deployment) = await SeedPendingDeploymentAsync(scopeFactory);

        var report = new Dictionary<string, object?> { ["reason"] = "out_of_memory", ["exit_code"] = 137, ["log_tail"] = "CUDA OOM" };
        var evt = new SupervisorPushEvent { DeploymentId = deployment.Id.ToString(), Seq = 1, Kind = "report", Report = report };
        await applier.ApplyWithGapCatchUpAsync(deployment.Id, UnusedAdminUrl, evt, CancellationToken.None);

        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var reloaded = await db.DeploymentsV2.FindAsync(deployment.Id);
        Assert.Equal("out_of_memory", reloaded!.FailureReason);
        Assert.Equal(137, reloaded.ExitCode);
        Assert.Equal("CUDA OOM", reloaded.LogTail);
    }

    [Fact]
    public async Task LogKindPush_NeverWritesADeploymentEventsRow_ButAdvancesLastSeenSeq()
    {
        var (applier, scopeFactory) = await NewApplierAsync();
        var (_, _, _, _, deployment) = await SeedPendingDeploymentAsync(scopeFactory);

        var evt = new SupervisorPushEvent { DeploymentId = deployment.Id.ToString(), Seq = 1, Kind = "log", Lines = ["hello", "world"] };
        await applier.ApplyWithGapCatchUpAsync(deployment.Id, UnusedAdminUrl, evt, CancellationToken.None);

        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(0, await db.DeploymentEventsV2.CountAsync(e => e.DeploymentId == deployment.Id));
        var reloaded = await db.DeploymentsV2.FindAsync(deployment.Id);
        Assert.Equal(1, reloaded!.LastSeenSeq);
    }

    private static async Task<Deployment> SeedActivePreviousAsync(IServiceScopeFactory scopeFactory, Deployment pending)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var previous = new Deployment
        {
            InstanceId = pending.InstanceId, ModelId = pending.ModelId, RecipeId = pending.RecipeId,
            ArgsSnapshot = "[]", Status = "active", StartedAt = DateTime.UtcNow,
        };
        db.DeploymentsV2.Add(previous);
        await db.SaveChangesAsync();
        return previous;
    }

    private static SupervisorPushEvent State(Deployment d, int seq, string state, Dictionary<string, object?>? detail = null) =>
        new() { DeploymentId = d.Id.ToString(), Seq = seq, Kind = "state", State = state, Detail = detail };

    [Fact]
    public async Task FailedDeploy_AfterTheOldProcessWasStopped_MarksThePreviousActiveRowStopped()
    {
        var (applier, scopeFactory) = await NewApplierAsync();
        var (_, _, _, _, deployment) = await SeedPendingDeploymentAsync(scopeFactory);
        var previous = await SeedActivePreviousAsync(scopeFactory, deployment);

        await applier.ApplyWithGapCatchUpAsync(deployment.Id, UnusedAdminUrl, State(deployment, 1, "stopping"), CancellationToken.None);
        await applier.ApplyWithGapCatchUpAsync(deployment.Id, UnusedAdminUrl, State(deployment, 2, "starting"), CancellationToken.None);
        await applier.ApplyWithGapCatchUpAsync(deployment.Id, UnusedAdminUrl, State(deployment, 3, "failed"), CancellationToken.None);

        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal("failed", (await db.DeploymentsV2.FindAsync(deployment.Id))!.Status);
        var reloadedPrevious = (await db.DeploymentsV2.FindAsync(previous.Id))!;
        Assert.Equal("stopped", reloadedPrevious.Status);
        Assert.Equal("swapped", reloadedPrevious.StopReason);
        Assert.NotNull(reloadedPrevious.StoppedAt);
    }

    [Fact]
    public async Task FailedDeploy_ThatNeverTouchedTheOldProcess_LeavesThePreviousRowActive()
    {
        var (applier, scopeFactory) = await NewApplierAsync();
        var (_, _, _, _, deployment) = await SeedPendingDeploymentAsync(scopeFactory);
        var previous = await SeedActivePreviousAsync(scopeFactory, deployment);

        await applier.ApplyWithGapCatchUpAsync(deployment.Id, UnusedAdminUrl, State(deployment, 1, "failed"), CancellationToken.None);

        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal("failed", (await db.DeploymentsV2.FindAsync(deployment.Id))!.Status);
        Assert.Equal("active", (await db.DeploymentsV2.FindAsync(previous.Id))!.Status);
    }

    [Fact]
    public async Task FailedRollback_StopsThePreviousActiveRowWithRollbackReason()
    {
        var (applier, scopeFactory) = await NewApplierAsync();
        var (_, _, _, _, deployment) = await SeedPendingDeploymentAsync(scopeFactory);
        using (var scope = scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            (await db.DeploymentsV2.FindAsync(deployment.Id))!.PendingStopReason = "rollback";
            await db.SaveChangesAsync();
        }
        var previous = await SeedActivePreviousAsync(scopeFactory, deployment);

        await applier.ApplyWithGapCatchUpAsync(deployment.Id, UnusedAdminUrl, State(deployment, 1, "starting"), CancellationToken.None);
        await applier.ApplyWithGapCatchUpAsync(deployment.Id, UnusedAdminUrl, State(deployment, 2, "failed"), CancellationToken.None);

        using var scope2 = scopeFactory.CreateScope();
        var db2 = scope2.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal("rollback", (await db2.DeploymentsV2.FindAsync(previous.Id))!.StopReason);
    }

    [Fact]
    public async Task FailedStateEvent_WritesFailureColumnsFromItsOwnDetail()
    {
        // Catch-up can only recover "state" kind events (GetEventsAsync never
        // returns "report"/"log" pushes) -- the failed state event's own
        // detail must carry enough to populate the failure columns even if
        // the separate "report" push was never delivered/recovered.
        var (applier, scopeFactory) = await NewApplierAsync();
        var (_, _, _, _, deployment) = await SeedPendingDeploymentAsync(scopeFactory);

        var detail = new Dictionary<string, object?> { ["reason"] = "bad_args", ["exit_code"] = 2, ["log_tail"] = "unrecognized arguments" };
        var evt = new SupervisorPushEvent { DeploymentId = deployment.Id.ToString(), Seq = 1, Kind = "state", State = "failed", Detail = detail };
        await applier.ApplyWithGapCatchUpAsync(deployment.Id, UnusedAdminUrl, evt, CancellationToken.None);

        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var reloaded = await db.DeploymentsV2.FindAsync(deployment.Id);
        Assert.Equal("failed", reloaded!.Status);
        Assert.Equal("bad_args", reloaded.FailureReason);
        Assert.Equal(2, reloaded.ExitCode);
        Assert.Equal("unrecognized arguments", reloaded.LogTail);
    }
}
