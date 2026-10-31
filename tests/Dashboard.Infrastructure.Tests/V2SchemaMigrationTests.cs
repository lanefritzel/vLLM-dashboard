using System.Text;
using Dashboard.Infrastructure.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Dashboard.Infrastructure.Tests;

/// <summary>P01a's Definition of done, "dotnet ef database update applies cleanly to
/// a fresh Postgres and to a copy of an existing (old-schema) DB; exits 0 on both.
/// Old-schema copy: legacy tables and Identity rows are byte-for-byte unchanged
/// afterwards." plus the empty-DB bootstrap check.</summary>
[Collection("Postgres")]
public class V2SchemaMigrationTests(PostgresFixture postgres)
{
    // The last migration before AddV2Schema -- used to put a test database in the
    // exact "old schema" state a real upgrade starts from, before applying the rest.
    private const string LastPreV2Migration = "20260914000453_AddAppLogs";

    private static AppDbContext NewContext(string connectionString) =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(connectionString).Options);

    [Fact]
    public async Task Migrate_AppliesCleanlyToAFreshDatabase()
    {
        var connectionString = await postgres.CreateFreshDatabaseAsync();
        using var db = NewContext(connectionString);

        await db.Database.MigrateAsync();

        Assert.Empty(await db.Database.GetPendingMigrationsAsync());
    }

    [Fact]
    public async Task Migrate_UpgradesAnOldSchemaCopyInPlace_WithoutTouchingLegacyData()
    {
        var connectionString = await postgres.CreateFreshDatabaseAsync();
        using var db = NewContext(connectionString);
        var migrator = db.GetInfrastructure().GetRequiredService<IMigrator>();

        // Put the DB in the exact state a real upgrade starts from: old schema only.
        await migrator.MigrateAsync(LastPreV2Migration);

        // Seed one row representative of each legacy table family the DoD calls
        // out: a physical server, a vLLM source (with its launch spec), a legacy
        // model, a metrics sample, an app log, and an Identity user.
        var physicalServer = new Persistence.PhysicalServer
        {
            Label = "tes", DockerHost = "tcp://tes.home:2375", GpuVendor = "intel", GpuVramTotalGb = 12,
            HfCacheHostPath = "/hf-cache", HfToken = "secret-token",
        };
        db.PhysicalServers.Add(physicalServer);
        await db.SaveChangesAsync();

        var source = new Persistence.Source
        {
            Slug = "main", Label = "main vLLM", Type = "vllm",
            MetricsUrl = "http://tes.home:9999/metrics", InferenceUrl = "http://tes.home:9999/v1",
            DockerHost = "tcp://tes.home:2375", PhysicalServerId = physicalServer.Id,
            LaunchSpec = new Persistence.LaunchSpecEntity
            {
                Image = "intel/llm-scaler-vllm:0.26.0-b2", ModelArgTemplate = "{model}", RestartPolicy = "unless-stopped",
            },
        };
        db.Sources.Add(source);
        db.Models.Add(new Persistence.Model { SourceId = source.Id, Name = "qwen3.8-vllm", PhysicalServerId = physicalServer.Id });
        db.AppLogs.Add(new Persistence.AppLogEntity { Level = "Information", Category = "Test", Message = "hello", TimestampUtc = DateTime.UtcNow });
        var user = new ApplicationUser { UserName = "owner", NormalizedUserName = "OWNER", Email = "owner@example.com" };
        db.Users.Add(user);
        await db.SaveChangesAsync();

        var tablesBefore = await DumpAllTablesAsync(connectionString);

        // The actual upgrade: apply every remaining migration, including AddV2Schema.
        await migrator.MigrateAsync();

        Assert.Empty(await db.Database.GetPendingMigrationsAsync());
        var tablesAfter = await DumpAllTablesAsync(connectionString);

        // Byte-for-byte: every column of every row in every table that existed
        // before the upgrade, re-read after it, not just a few fields I remembered
        // to name explicitly.
        foreach (var (table, dumpBefore) in tablesBefore)
        {
            Assert.True(tablesAfter.TryGetValue(table, out var dumpAfter), $"table '{table}' disappeared after the upgrade");
            Assert.Equal(dumpBefore, dumpAfter);
        }
    }

    /// <summary>Reads every row of every user table via plain ADO.NET (bypassing EF's
    /// own type mapping/change tracking) and renders each one as a canonical
    /// column=value string, so two dumps can be compared for a true byte-for-byte
    /// match across every column -- including ones no test author remembered to
    /// assert on by name.</summary>
    private static async Task<Dictionary<string, string>> DumpAllTablesAsync(string connectionString)
    {
        var result = new Dictionary<string, string>();
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync();

        var tableNames = new List<string>();
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT table_name FROM information_schema.tables " +
                "WHERE table_schema = 'public' AND table_name <> '__EFMigrationsHistory' ORDER BY table_name";
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
                tableNames.Add(reader.GetString(0));
        }

        foreach (var table in tableNames)
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $"SELECT * FROM \"{table}\" ORDER BY 1";
            await using var reader = await cmd.ExecuteReaderAsync();
            var sb = new StringBuilder();
            while (await reader.ReadAsync())
            {
                for (var i = 0; i < reader.FieldCount; i++)
                {
                    sb.Append(reader.GetName(i)).Append('=');
                    sb.Append(reader.IsDBNull(i) ? "<null>" : reader.GetValue(i)!.ToString());
                    sb.Append(';');
                }
                sb.Append('\n');
            }
            result[table] = sb.ToString();
        }
        return result;
    }

    [Fact]
    public async Task EmptyDatabase_BootsAgainstTheNewTablesWithoutErrors()
    {
        var connectionString = await postgres.CreateFreshDatabaseAsync();

        // The real boot path, not just a query through AppDbContext directly:
        // Dashboard.Web.DbInitializerHostedService.StartAsync runs the same
        // Database.MigrateAsync() + config/source/backfill seeding the actual app
        // runs on startup. It swallows its own exceptions (logs, doesn't rethrow),
        // so a captured logger is the only way to tell a silent failure from a
        // genuine "fresh install, nothing to do yet".
        var services = new ServiceCollection();
        services.AddDbContext<AppDbContext>(o => o.UseNpgsql(connectionString));
        using var provider = services.BuildServiceProvider();
        var configuration = new ConfigurationBuilder().Build();
        var logger = new CapturingLogger<Dashboard.Web.DbInitializerHostedService>();
        var hosted = new Dashboard.Web.DbInitializerHostedService(
            provider.GetRequiredService<IServiceScopeFactory>(), configuration, logger);

        await hosted.StartAsync(CancellationToken.None);

        Assert.DoesNotContain(logger.Entries, e => e.Level >= LogLevel.Warning);

        using var db = NewContext(connectionString);
        Assert.Empty(await db.Database.GetPendingMigrationsAsync());
        Assert.NotEmpty(await db.Configs.ToListAsync()); // confirms the seed step actually ran, not just the migration
        Assert.Equal(0, await db.ServersV2.CountAsync());
        Assert.Equal(0, await db.InstancesV2.CountAsync());
        Assert.Equal(0, await db.ModelsV2.CountAsync());
        Assert.Equal(0, await db.RecipesV2.CountAsync());
        Assert.Equal(0, await db.DeploymentsV2.CountAsync());
        Assert.Equal(0, await db.DeploymentEventsV2.CountAsync());
    }

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, Exception? Exception, string Message)> Entries { get; } = new();

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Entries.Add((logLevel, exception, formatter(state, exception)));

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose() { }
        }
    }
}
