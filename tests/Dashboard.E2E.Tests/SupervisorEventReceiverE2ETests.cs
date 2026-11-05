using System.Text;
using Dashboard.Infrastructure.Persistence.V2;
using Microsoft.EntityFrameworkCore;

namespace Dashboard.E2E.Tests;

/// <summary>Exercises the real wire boundary <see cref="SupervisorEventReceiverTests"/>
/// (in Dashboard.Infrastructure.Tests) deliberately does not: an actual HTTP POST
/// of the supervisor's real raw JSON text against the real minimal-API route in
/// Program.cs, through real ASP.NET model binding -- not a hand-constructed
/// SupervisorPushEvent object handed straight to the handler. P16a finding: the
/// handler-level tests all passed while every real push from a real supervisor
/// 400'd before ever reaching the handler, because the wire's snake_case field
/// names (confirmed here with a literal "deployment_id" key, exactly as the
/// Python supervisor sends it -- never an object literal with a C#-cased
/// property) didn't match the endpoint's actual (un-configured, default)
/// JSON binding options. No existing test sent real bytes over the wire for
/// this endpoint, so nothing caught it.</summary>
[Collection("E2E")]
public class SupervisorEventReceiverE2ETests
{
    private readonly DashboardAppFixture _fixture;

    public SupervisorEventReceiverE2ETests(DashboardAppFixture fixture) => _fixture = fixture;

    private async Task<(Instance Instance, Deployment Deployment)> SeedAsync()
    {
        using var db = _fixture.NewDbContext();
        await db.DeploymentsV2.ExecuteDeleteAsync();
        await db.RecipesV2.ExecuteDeleteAsync();
        await db.ModelsV2.ExecuteDeleteAsync();
        await db.InstancesV2.ExecuteDeleteAsync();
        await db.ServersV2.ExecuteDeleteAsync();

        var server = new Server { Label = $"srv-{Guid.NewGuid():N}", ModelsDir = "/models", GpuMemGb = 80 };
        db.ServersV2.Add(server);
        await db.SaveChangesAsync();

        var instance = new Instance
        {
            ServerId = server.Id, Label = "primary", ProxySlug = $"slug-{Guid.NewGuid():N}",
            AdminUrl = "http://127.0.0.1:1",
        };
        db.InstancesV2.Add(instance);

        var model = new Model { ServerId = server.Id, Name = "fake-model", Path = "/models/fake-model", SourceType = "local", LoadFormat = "auto" };
        db.ModelsV2.Add(model);
        var recipe = new Recipe { ServerId = server.Id, Name = "recipe-ok", ServedModelName = "main" };
        db.RecipesV2.Add(recipe);
        await db.SaveChangesAsync();

        var deployment = new Deployment
        {
            InstanceId = instance.Id, ModelId = model.Id, RecipeId = recipe.Id,
            ArgsSnapshot = """["vllm","serve","/models/fake"]""", Status = "pending",
        };
        db.DeploymentsV2.Add(deployment);
        await db.SaveChangesAsync();

        return (instance, deployment);
    }

    private static StringContent RawJson(string json) =>
        new(json, Encoding.UTF8, "application/json");

    [Fact]
    public async Task StatePush_WithTheSupervisorsRealSnakeCaseWireShape_Returns200AndApplies()
    {
        var (_, deployment) = await SeedAsync();

        using var client = new HttpClient { BaseAddress = new Uri(_fixture.BaseUrl) };

        // Deliberately a raw string literal, not a C# object serialized through
        // any .NET JSON options -- this is exactly the bytes the real Python
        // supervisor's event_push.py puts on the wire (json.dumps(item.to_dict())).
        var json = $$"""
            {"deployment_id": "{{deployment.Id}}", "seq": 1, "ts": 1700000000.0, "kind": "state", "state": "starting", "detail": null}
            """;

        var response = await client.PostAsync("/internal/supervisor-events", RawJson(json));

        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);

        using var db = _fixture.NewDbContext();
        var applied = await db.DeploymentEventsV2.FirstOrDefaultAsync(e => e.DeploymentId == deployment.Id && e.Seq == 1);
        Assert.NotNull(applied);
        Assert.Equal("starting", applied!.State);
    }

    [Fact]
    public async Task LogPush_WithTheSupervisorsRealSnakeCaseWireShape_Returns200()
    {
        var (_, deployment) = await SeedAsync();

        using var client = new HttpClient { BaseAddress = new Uri(_fixture.BaseUrl) };

        var json = $$"""
            {"deployment_id": "{{deployment.Id}}", "seq": 1, "ts": 1700000000.0, "kind": "log", "lines": ["hello from vllm"]}
            """;

        var response = await client.PostAsync("/internal/supervisor-events", RawJson(json));

        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
    }
}
