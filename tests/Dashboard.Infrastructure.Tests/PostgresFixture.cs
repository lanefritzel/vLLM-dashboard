using Npgsql;
using Testcontainers.PostgreSql;

namespace Dashboard.Infrastructure.Tests;

/// <summary>One throwaway Postgres container shared by every test in the "Postgres"
/// collection. Each test gets its own database inside it via
/// <see cref="CreateFreshDatabaseAsync"/> rather than a second container, so tests
/// stay isolated without paying container-startup cost per test.</summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:16")
        .WithDatabase("fixture")
        .WithUsername("dashboard")
        .WithPassword("dashboard")
        .Build();

    public Task InitializeAsync() => _container.StartAsync();

    public Task DisposeAsync() => _container.DisposeAsync().AsTask();

    public async Task<string> CreateFreshDatabaseAsync()
    {
        var dbName = $"test_{Guid.NewGuid():N}";
        await using var conn = new NpgsqlConnection(_container.GetConnectionString());
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"CREATE DATABASE \"{dbName}\"";
        await cmd.ExecuteNonQueryAsync();

        var builder = new NpgsqlConnectionStringBuilder(_container.GetConnectionString()) { Database = dbName };
        return builder.ConnectionString;
    }
}

[CollectionDefinition("Postgres")]
public sealed class PostgresCollection : ICollectionFixture<PostgresFixture>;
