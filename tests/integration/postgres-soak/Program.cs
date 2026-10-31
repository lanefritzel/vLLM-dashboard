// P01c: does the dashboard <-> Postgres connection survive idle gaps across a
// Docker bridge network? Production runs host-networked because a pooled
// connection, reached through Docker's published-port/NAT path, was once
// silently reset while idle between queries (an Npgsql "operation cancelled" /
// connection-corruption error that crashed a Blazor circuit mid-request). This
// harness holds ONE connection open for the whole run -- the same shape as a
// Blazor Server circuit's DbContext scope: opened once, touched only
// occasionally -- and checks it still works after each idle gap, over a bridge
// network with no published port anywhere in the path.
using System.Globalization;
using Npgsql;

var idleMinutes = GetDoubleArg(args, "--idle-minutes", 10);
var cycles = GetIntArg(args, "--cycles", 3);
// Npgsql's own idle-connection pruning (default: after 300s unused IN THE POOL)
// never applies here -- this harness keeps its NpgsqlConnection open the whole
// run rather than closing/reopening between cycles, so there's nothing for the
// pool to prune. A silent reset during an idle gap has to come from the network
// path itself, which is exactly what this test is checking.
var connectionString = Environment.GetEnvironmentVariable("SOAK_DB_CONNECTION")
    ?? "Host=postgres;Port=5432;Database=dashboard;Username=dashboard;Password=dashboard";

var hostForLog = new NpgsqlConnectionStringBuilder(connectionString).Host;
Console.WriteLine($"postgres-soak: {cycles} cycle(s) x {idleMinutes} minute(s) idle, connecting to host '{hostForLog}'.");

var errors = new List<string>();
var conn = new NpgsqlConnection(connectionString);
await conn.OpenAsync();
Console.WriteLine("postgres-soak: opened the long-lived connection (Blazor-circuit-like -- held open for the whole run).");
await RunQueryAsync(conn, "initial query, before any idling");

for (var cycle = 1; cycle <= cycles; cycle++)
{
    Console.WriteLine($"postgres-soak: cycle {cycle}/{cycles} -- idling {idleMinutes} minute(s)...");
    await Task.Delay(TimeSpan.FromMinutes(idleMinutes));

    try
    {
        await RunQueryAsync(conn, $"cycle {cycle} post-idle query");
    }
    catch (Exception ex)
    {
        var message = $"cycle {cycle}: query failed after idling {idleMinutes} minute(s): {ex}";
        Console.WriteLine("postgres-soak: FAIL -- " + message);
        errors.Add(message);

        // Recover with a fresh connection so a broken cycle doesn't mask
        // whether LATER cycles also fail -- we want the full error picture.
        try { await conn.DisposeAsync(); } catch { /* already broken */ }
        conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync();
    }
}

await conn.DisposeAsync();

if (errors.Count > 0)
{
    Console.WriteLine($"postgres-soak: FAILED -- {errors.Count} error(s) across {cycles} cycle(s).");
    foreach (var e in errors)
        Console.WriteLine("  " + e);
    return 1;
}

Console.WriteLine($"postgres-soak: PASSED -- zero connection errors across {cycles} cycle(s), idle gap {idleMinutes} minute(s) each.");
return 0;

static async Task RunQueryAsync(NpgsqlConnection conn, string label)
{
    using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT 1";
    var result = await cmd.ExecuteScalarAsync();
    Console.WriteLine($"postgres-soak: {label} -> {result}");
}

static double GetDoubleArg(string[] args, string name, double defaultValue)
{
    var i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length && double.TryParse(args[i + 1], CultureInfo.InvariantCulture, out var value)
        ? value : defaultValue;
}

static int GetIntArg(string[] args, string name, int defaultValue)
{
    var i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length && int.TryParse(args[i + 1], CultureInfo.InvariantCulture, out var value)
        ? value : defaultValue;
}
