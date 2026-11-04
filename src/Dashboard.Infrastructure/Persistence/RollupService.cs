using Dashboard.Core.Abstractions;
using Dashboard.Infrastructure.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Dashboard.Infrastructure.Persistence;

/// <summary>
/// Hourly job: upserts hourly rollups from raw samples and prunes raw samples older
/// than retention. Idempotent (upsert by SourceId/ModelName/BucketStart).
/// </summary>
public sealed class RollupService : IRetentionService, IHostedService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IConfigStore _config;
    private readonly ILogger<RollupService> _logger;
    private PeriodicTimer _timer = null!;

    public RollupService(IServiceScopeFactory scopeFactory, IConfigStore config, ILogger<RollupService> logger)
    {
        _scopeFactory = scopeFactory;
        _config = config;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _timer = new PeriodicTimer(TimeSpan.FromHours(1));
        _ = Task.Run(() => LoopAsync(cancellationToken), CancellationToken.None);
        return Task.CompletedTask;
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        try { await Task.Delay(TimeSpan.FromSeconds(30), ct); } catch (TaskCanceledException) { return; }
        await SafeRunAsync(ct);
        try
        {
            while (await _timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
                await SafeRunAsync(ct);
        }
        catch (OperationCanceledException) { }
    }

    private async Task SafeRunAsync(CancellationToken ct)
    {
        try { await RunOnceAsync(ct); }
        catch (Exception ex) { _logger.LogWarning(ex, "Retention/rollup run failed."); }
    }

    public async Task RunOnceAsync(CancellationToken ct)
    {
        var retentionDays = _config.RetentionDays;
        var rollupsEnabled = _config.RollupsEnabled;

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        if (rollupsEnabled)
        {
            var now = DateTime.UtcNow;
            var from = now.AddHours(-2);

            var raw = await db.MetricsSamples.AsNoTracking()
                .Where(s => s.Ts >= from && s.Ts < now && s.SourceId != null)
                .ToListAsync(ct);

            foreach (var grp in raw.GroupBy(s => (s.SourceId!, s.ModelName ?? "")))
            {
                var (sid, model) = grp.Key;
                var bucket = new DateTime(grp.Min(s => s.Ts).Year, grp.Min(s => s.Ts).Month, grp.Min(s => s.Ts).Day,
                    grp.Min(s => s.Ts).Hour, 0, 0, DateTimeKind.Utc);

                var up = grp.Where(s => s.Up).ToList();
                double? Mean(Func<MetricsSampleEntity, double?> f)
                {
                    var vals = grp.Select(f).Where(v => v is not null).Select(v => v!.Value).ToList();
                    return vals.Count == 0 ? null : vals.Average();
                }

                var existing = await db.MetricsRollups
                    .FirstOrDefaultAsync(x => x.SourceId == sid && x.ModelName == model && x.BucketStart == bucket, ct);

                var values = new
                {
                    Samples = grp.Count(),
                    AvgRunning = Mean(s => s.Running),
                    AvgWaiting = Mean(s => s.Waiting),
                    AvgKvPct = Mean(s => s.KvPct),
                    MaxKvPct = grp.Select(s => s.KvPct).Where(v => v is not null).Select(v => v!.Value).Max(),
                    RequestsCompleted = up.Count,
                    AvgGenTps = Mean(s => s.GenTps),
                    PeakGenTps = grp.Select(s => s.GenTps).Where(v => v is not null).Select(v => v!.Value).Max(),
                };

                if (existing is null)
                {
                    db.MetricsRollups.Add(new MetricsRollupEntity
                    {
                        BucketStart = bucket,
                        SourceId = sid,
                        ModelName = model,
                        Samples = values.Samples,
                        AvgRunning = values.AvgRunning,
                        AvgWaiting = values.AvgWaiting,
                        AvgKvPct = values.AvgKvPct,
                        MaxKvPct = values.MaxKvPct,
                        RequestsCompleted = values.RequestsCompleted,
                        AvgGenTps = values.AvgGenTps,
                        PeakGenTps = values.PeakGenTps,
                    });
                }
                else
                {
                    existing.Samples = values.Samples;
                    existing.AvgRunning = values.AvgRunning;
                    existing.AvgWaiting = values.AvgWaiting;
                    existing.AvgKvPct = values.AvgKvPct;
                    existing.MaxKvPct = values.MaxKvPct;
                    existing.RequestsCompleted = values.RequestsCompleted;
                    existing.AvgGenTps = values.AvgGenTps;
                    existing.PeakGenTps = values.PeakGenTps;
                }
            }
            await db.SaveChangesAsync(ct);
            _logger.LogInformation("Rollups upserted (sources scanned).");
        }

        var cutoff = DateTime.UtcNow.AddDays(-retentionDays);
        const int batchSize = 10_000;
        int deleted;
        do
        {
            deleted = await db.Database.ExecuteSqlInterpolatedAsync(
                $"DELETE FROM \"MetricsSamples\" WHERE \"Id\" IN (SELECT \"Id\" FROM \"MetricsSamples\" WHERE \"Ts\" < {cutoff} LIMIT {batchSize})", ct);
            await Task.Delay(50, ct);
        } while (deleted > 0);

        // Same retention window as metrics — DbLoggerProvider writes here continuously,
        // so without pruning this would grow forever, exactly the "physical files that
        // fill up the disk" problem moved into the database instead of solved.
        int deletedLogs;
        do
        {
            deletedLogs = await db.Database.ExecuteSqlInterpolatedAsync(
                $"DELETE FROM \"AppLogs\" WHERE \"Id\" IN (SELECT \"Id\" FROM \"AppLogs\" WHERE \"TimestampUtc\" < {cutoff} LIMIT {batchSize})", ct);
            await Task.Delay(50, ct);
        } while (deletedLogs > 0);

        _logger.LogInformation("Retention run complete (cutoff {Cutoff}, retention {Days}d, pruned ~done).", cutoff, retentionDays);
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _timer?.Dispose();
        return Task.CompletedTask;
    }
}
