using Dashboard.Core.Abstractions;
using Dashboard.Core.Supervisor;
using Dashboard.Infrastructure.Events;
using Dashboard.Infrastructure.Identity;
using Dashboard.Infrastructure.Persistence.V2;
using Dashboard.Infrastructure.Supervisor;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Dashboard.Infrastructure.Tests;

/// <summary>P10b's Definition of done: "A SignalR test client in the
/// deployment group receives the state events and log lines in order."
/// A real <see cref="HubConnection"/> against a real in-memory ASP.NET Core
/// host (TestServer) -- not a bare in-process method call -- so this
/// actually exercises the hub/group/transport, not just the applier's own
/// broadcast call.</summary>
[Collection("Postgres")]
public class DeploymentHubTests(PostgresFixture postgres) : IAsyncDisposable
{
    private IHost? _host;

    public async ValueTask DisposeAsync()
    {
        if (_host is not null)
            await _host.StopAsync();
    }

    private async Task<(IDeploymentEventApplier Applier, HubConnection Connection, List<(string Kind, object Payload)> Received)> StartAsync()
    {
        var connectionString = await postgres.CreateFreshDatabaseAsync();

        var builder = new HostBuilder()
            .ConfigureWebHost(webBuilder =>
            {
                webBuilder.UseTestServer();
                webBuilder.ConfigureServices(services =>
                {
                    services.AddSignalR();
                    services.AddDbContext<AppDbContext>(o => o.UseNpgsql(connectionString));
                    services.AddHttpClient("supervisor");
                    services.AddSingleton<IReadOnlyGuard, ReadOnlyGuard>();
                    services.AddSingleton<IDeploymentService, DeploymentService>();
                    services.AddSingleton<ISupervisorClient, SupervisorClient>();
                    services.AddSingleton<IDeploymentEventApplier, DeploymentEventApplier>();
                });
                webBuilder.Configure(app =>
                {
                    app.UseRouting();
                    app.UseEndpoints(endpoints => endpoints.MapHub<DeploymentHub>("/deployment-hub"));
                });
            });

        _host = await builder.StartAsync();
        await _host.Services.GetRequiredService<AppDbContext>().Database.MigrateAsync();

        var testServer = _host.GetTestServer();
        var connection = new HubConnectionBuilder()
            .WithUrl("http://localhost/deployment-hub", o => o.HttpMessageHandlerFactory = _ => testServer.CreateHandler())
            .Build();

        var received = new List<(string Kind, object Payload)>();
        connection.On<object>(DeploymentHub.StateEvent, payload => received.Add(("state", payload)));
        connection.On<List<string>>(DeploymentHub.LogEvent, payload => received.Add(("log", payload)));

        await connection.StartAsync();
        return (_host.Services.GetRequiredService<IDeploymentEventApplier>(), connection, received);
    }

    private async Task<Deployment> SeedPendingDeploymentAsync()
    {
        var db = _host!.Services.GetRequiredService<AppDbContext>();

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
    public async Task ClientInTheDeploymentGroup_ReceivesStateAndLogEventsInOrder()
    {
        var (applier, connection, received) = await StartAsync();
        var deployment = await SeedPendingDeploymentAsync();

        await connection.InvokeAsync("JoinDeployment", deployment.Id);

        await applier.ApplyWithGapCatchUpAsync(deployment.Id, "http://unused.invalid", "unused",
            new SupervisorPushEvent { DeploymentId = deployment.Id.ToString(), Seq = 1, Kind = "state", State = "starting" },
            CancellationToken.None);
        await applier.ApplyWithGapCatchUpAsync(deployment.Id, "http://unused.invalid", "unused",
            new SupervisorPushEvent { DeploymentId = deployment.Id.ToString(), Seq = 2, Kind = "log", Lines = ["hello"] },
            CancellationToken.None);
        await applier.ApplyWithGapCatchUpAsync(deployment.Id, "http://unused.invalid", "unused",
            new SupervisorPushEvent { DeploymentId = deployment.Id.ToString(), Seq = 3, Kind = "state", State = "loading" },
            CancellationToken.None);

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (received.Count < 3 && DateTime.UtcNow < deadline)
            await Task.Delay(50);

        Assert.Equal(3, received.Count);
        Assert.Equal("state", received[0].Kind);
        Assert.Equal("log", received[1].Kind);
        Assert.Equal("state", received[2].Kind);

        await connection.DisposeAsync();
    }
}
