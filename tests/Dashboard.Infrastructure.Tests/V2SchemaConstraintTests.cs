using Dashboard.Infrastructure.Identity;
using Dashboard.Infrastructure.Persistence.V2;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Dashboard.Infrastructure.Tests;

/// <summary>P01a's Definition of done: the partial unique index and CHECK
/// constraints reject bad data at the DB level, the seed fixture loads cleanly, and
/// deployment_events' (deployment_id, seq) uniqueness rejects a duplicate event.</summary>
[Collection("Postgres")]
public class V2SchemaConstraintTests(PostgresFixture postgres)
{
    private async Task<AppDbContext> NewMigratedContextAsync()
    {
        var connectionString = await postgres.CreateFreshDatabaseAsync();
        var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(connectionString).Options);
        await db.Database.MigrateAsync();
        return db;
    }

    private static PostgresException AssertPostgresError(Exception ex, string sqlState)
    {
        var pg = ex as PostgresException ?? ex.InnerException as PostgresException;
        Assert.NotNull(pg);
        Assert.Equal(sqlState, pg!.SqlState);
        return pg;
    }

    [Fact]
    public async Task SeedFixture_LoadsCleanly()
    {
        using var db = await NewMigratedContextAsync();

        var server = await V2SeedFixture.SeedAsync(db);

        Assert.Equal(1, await db.ServersV2.CountAsync());
        Assert.Equal(1, await db.InstancesV2.CountAsync(i => i.ServerId == server.Id));
        Assert.Equal(2, await db.ModelsV2.CountAsync(m => m.ServerId == server.Id));
        Assert.Equal(2, await db.RecipesV2.CountAsync(r => r.ServerId == server.Id));
    }

    private async Task<(AppDbContext Db, Instance Instance, Model Model, Recipe Recipe)> SeedForDeploymentsAsync()
    {
        var db = await NewMigratedContextAsync();
        var server = await V2SeedFixture.SeedAsync(db);
        var instance = await db.InstancesV2.FirstAsync(i => i.ServerId == server.Id);
        var model = await db.ModelsV2.FirstAsync(m => m.ServerId == server.Id);
        var recipe = await db.RecipesV2.FirstAsync(r => r.ServerId == server.Id);
        return (db, instance, model, recipe);
    }

    [Fact]
    public async Task PartialUniqueIndex_RejectsASecondPendingOrActiveDeploymentOnTheSameInstance()
    {
        var (db, instance, model, recipe) = await SeedForDeploymentsAsync();
        using var _ = db;

        db.DeploymentsV2.Add(new Deployment
        {
            InstanceId = instance.Id, ModelId = model.Id, RecipeId = recipe.Id,
            ArgsSnapshot = "[]", Status = "active",
        });
        await db.SaveChangesAsync();

        db.DeploymentsV2.Add(new Deployment
        {
            InstanceId = instance.Id, ModelId = model.Id, RecipeId = recipe.Id,
            ArgsSnapshot = "[]", Status = "pending",
        });
        var ex = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        AssertPostgresError(ex, "23505"); // unique_violation
    }

    [Theory]
    [InlineData("Status", "sideways")]
    [InlineData("StopReason", "sideways")]
    [InlineData("FailureReason", "sideways")]
    public async Task CheckConstraints_RejectBadDeploymentEnumValues(string column, string badValue)
    {
        var (db, instance, model, recipe) = await SeedForDeploymentsAsync();
        using var _ = db;

        var deployment = new Deployment
        {
            InstanceId = instance.Id, ModelId = model.Id, RecipeId = recipe.Id,
            ArgsSnapshot = "[]", Status = "pending",
        };
        switch (column)
        {
            case "Status": deployment.Status = badValue; break;
            case "StopReason": deployment.StopReason = badValue; break;
            case "FailureReason": deployment.FailureReason = badValue; break;
        }
        db.DeploymentsV2.Add(deployment);

        var ex = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        AssertPostgresError(ex, "23514"); // check_violation
    }

    [Fact]
    public async Task ModelsCheckConstraint_RejectsBadSourceType()
    {
        using var db = await NewMigratedContextAsync();
        var server = await V2SeedFixture.SeedAsync(db);

        db.ModelsV2.Add(new Model { ServerId = server.Id, Name = "bad", Path = "/x", SourceType = "ftp" });
        var ex = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        AssertPostgresError(ex, "23514");
    }

    [Fact]
    public async Task DeploymentEventUniqueness_RejectsADuplicateSeqForTheSameDeployment()
    {
        var (db, instance, model, recipe) = await SeedForDeploymentsAsync();
        using var _ = db;

        var deployment = new Deployment
        {
            InstanceId = instance.Id, ModelId = model.Id, RecipeId = recipe.Id,
            ArgsSnapshot = "[]", Status = "active",
        };
        db.DeploymentsV2.Add(deployment);
        await db.SaveChangesAsync();

        db.DeploymentEventsV2.Add(new DeploymentEvent { DeploymentId = deployment.Id, Seq = 1, State = "ready" });
        await db.SaveChangesAsync();

        db.DeploymentEventsV2.Add(new DeploymentEvent { DeploymentId = deployment.Id, Seq = 1, State = "ready" });
        var ex = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        AssertPostgresError(ex, "23505");
    }
}
