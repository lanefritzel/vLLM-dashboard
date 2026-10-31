using Dashboard.Infrastructure.Conversion;
using Dashboard.Infrastructure.Identity;
using Microsoft.EntityFrameworkCore;

namespace Dashboard.Infrastructure.Tests;

/// <summary>
/// P01b's final Definition of done bullet: "Against a restored copy of the live
/// DB the routine completes and the report is reviewed by the owner." Not run by
/// the normal gate -- there's no live DB dump available in CI or on a fresh
/// checkout, and this test exists to be re-run by hand whenever that check needs
/// repeating (e.g. right before P16a), not as a standing part of the suite.
///
/// To run: restore a live DB dump into a local Postgres, apply every EF
/// migration up to and including the current one so it has both the legacy and
/// V2 schemas (exactly what a real upgrade starts from), then:
///
///   P01B_LIVE_DB_CONNECTION="Host=localhost;Port=...;Database=dashboard;Username=...;Password=..." \
///     dotnet test tests/Dashboard.Infrastructure.Tests --filter FullyQualifiedName~LiveDbConversionManualCheck
///
/// Prints the full conversion report to stdout for the owner to review.
/// </summary>
public class LiveDbConversionManualCheck
{
    [Fact]
    public async Task RunConversionAgainstARestoredLiveDbCopy_AndPrintTheReport()
    {
        var connectionString = Environment.GetEnvironmentVariable("P01B_LIVE_DB_CONNECTION");
        if (string.IsNullOrWhiteSpace(connectionString))
            return; // not a live-DB check run -- nothing to do

        using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(connectionString).Options);
        Assert.Empty(await db.Database.GetPendingMigrationsAsync()); // must already be fully migrated

        var report = await new LegacyConversionService(db).ConvertAsync();
        Console.WriteLine("=== P01b conversion report (restored live DB copy) ===");
        Console.WriteLine(report.ToText());

        // Running it a second time must be a true no-op -- same guarantee as the
        // fixture-based idempotency test, now against the real data.
        var second = await new LegacyConversionService(db).ConvertAsync();
        Assert.Equal(0, second.ServersConverted);
        Assert.Equal(0, second.InstancesConverted);
        Assert.Equal(0, second.ModelsConverted);
        Assert.Equal(0, second.RecipesCreated);
    }
}
