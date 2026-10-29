using Dashboard.Core.Abstractions;
using Dashboard.Core.Models;
using Dashboard.Infrastructure.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Dashboard.Infrastructure.Persistence;

/// <summary>
/// Reads samples from raw MetricsSamples for recent windows and falls back to
/// MetricsRollups for older data. The raw/rollup cutoff is the retention boundary,
/// so the UI stays transparent to storage strategy. Singleton; opens a fresh
/// AppDbContext per operation via IServiceScopeFactory so concurrent calls from
/// different components on the same circuit never share a context instance.
/// </summary>
public sealed class MetricsHistory : IMetricsHistory
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IConfigStore _config;

    public MetricsHistory(IServiceScopeFactory scopeFactory, IConfigStore config)
    {
        _scopeFactory = scopeFactory;
        _config = config;
    }

    public async Task<IReadOnlyList<MetricsSample>> GetAsync(
        DateTime from, DateTime to, string? sourceId, string? modelName, CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var retentionCutoff = DateTime.UtcNow.AddDays(-_config.RetentionDays);

        // Recent window: raw samples (only for data within retention).
        var rawFrom = from < retentionCutoff ? retentionCutoff : from;
        var raw = new List<MetricsSample>();
        if (rawFrom <= to)
        {
            var q = db.MetricsSamples.AsNoTracking().Where(s => s.Ts >= rawFrom && s.Ts <= to);
            if (sourceId is not null && Guid.TryParse(sourceId, out var sid))
                q = q.Where(s => s.SourceId == sid);
            if (modelName is not null)
                q = q.Where(s => s.ModelName == modelName);
            raw = await q.OrderBy(s => s.Ts).Select(s => s.ToInfo()).ToListAsync(ct);
        }

        // Older window: rollups (up to retentionCutoff).
        if (from < retentionCutoff)
            raw.AddRange(await ReadRollupsAsync(db, from, retentionCutoff, sourceId, modelName, ct));

        return raw.OrderBy(s => s.Ts).ToList();
    }

    private static async Task<List<MetricsSample>> ReadRollupsAsync(
        AppDbContext db, DateTime from, DateTime to, string? sourceId, string? modelName, CancellationToken ct)
    {
        var q = db.MetricsRollups.AsNoTracking()
            .Where(r => r.BucketStart >= from && r.BucketStart < to);
        if (sourceId is not null && Guid.TryParse(sourceId, out var sid))
            q = q.Where(r => r.SourceId == sid);
        if (modelName is not null)
            q = q.Where(r => r.ModelName == modelName);

        var rows = await q.OrderBy(r => r.BucketStart).ToListAsync(ct);
        return rows.Select(r => new MetricsSample
        {
            Ts = r.BucketStart,
            SourceId = r.SourceId.ToString(),
            SourceLabel = "",
            ModelName = r.ModelName,
            Running = (int)(r.AvgRunning ?? 0),
            Waiting = (int)(r.AvgWaiting ?? 0),
            KvPct = r.AvgKvPct,
            GenTps = r.AvgGenTps,
            Up = true,
        }).ToList();
    }

    public async Task<IReadOnlyList<HistorySummary>> SummarizeAsync(
        DateTime from, DateTime to, string? sourceId, string? modelName, CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var retentionCutoff = DateTime.UtcNow.AddDays(-_config.RetentionDays);

        var samples = new List<MetricsSample>();
        var rawFrom = from < retentionCutoff ? retentionCutoff : from;
        if (rawFrom <= to)
        {
            var q = db.MetricsSamples.AsNoTracking().Where(s => s.Ts >= rawFrom && s.Ts <= to);
            if (sourceId is not null && Guid.TryParse(sourceId, out var sid))
                q = q.Where(s => s.SourceId == sid);
            if (modelName is not null)
                q = q.Where(s => s.ModelName == modelName);
            samples.AddRange(await q.Select(s => s.ToInfo()).ToListAsync(ct));
        }

        if (from < retentionCutoff)
        {
            var rq = db.MetricsRollups.AsNoTracking()
                .Where(r => r.BucketStart >= from && r.BucketStart < retentionCutoff);
            if (sourceId is not null && Guid.TryParse(sourceId, out var sid))
                rq = rq.Where(r => r.SourceId == sid);
            if (modelName is not null)
                rq = rq.Where(r => r.ModelName == modelName);
            var rollRows = await rq.ToListAsync(ct);
            foreach (var r in rollRows)
            {
                samples.Add(new MetricsSample
                {
                    Ts = r.BucketStart,
                    ModelName = r.ModelName,
                    TtftAvgSec = r.P50TtftSec,
                    E2eAvgSec = r.P50E2eSec,
                    KvPct = r.MaxKvPct,
                    GenTps = r.AvgGenTps,
                    Up = true,
                });
            }
        }

        var grouped = samples
            .GroupBy(s => s.ModelName ?? "(unknown)")
            .Select(g =>
            {
                double? ttft = g.Where(s => s.TtftAvgSec is not null).Select(s => (double?)s.TtftAvgSec).Average();
                double? e2e = g.Where(s => s.E2eAvgSec is not null).Select(s => (double?)s.E2eAvgSec).Average();
                double? peak = g.Where(s => s.KvPct is not null).Select(s => (double?)s.KvPct).Max();
                return new HistorySummary
                {
                    ModelName = g.Key,
                    Requests = g.Count(s => s.Up),
                    TokensGenerated = g.Where(s => s.GenTps is not null).Sum(s => (long)(s.GenTps ?? 0)),
                    TokensPrompt = 0,
                    AvgTtftSec = ttft,
                    AvgE2eSec = e2e,
                    PeakKvPct = peak,
                    Samples = g.Count(),
                };
            })
            .OrderBy(x => x.ModelName)
            .ToList();

        return grouped;
    }

    public async Task<GenerationThroughputStats?> GetActiveGenerationStatsAsync(
        string sourceId, TimeSpan window, CancellationToken ct = default)
    {
        if (!Guid.TryParse(sourceId, out var sid))
            return null;

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var cutoff = DateTime.UtcNow - window;

        // Deliberately raw MetricsSamples only, never MetricsRollups — an hourly
        // AvgGenTps/PeakGenTps already blends in idle 0-throughput scrapes (the
        // scraper reports a real 0, not null, when nothing is generating), which
        // would understate "average while generating" and make "low" meaningless.
        // This only sees as far back as retention keeps raw samples; a window longer
        // than that just yields fewer samples, not an error.
        var agg = await db.MetricsSamples.AsNoTracking()
            .Where(s => s.SourceId == sid && s.Ts >= cutoff && s.GenTps > 0)
            .GroupBy(_ => 1)
            .Select(g => new
            {
                Peak = g.Max(s => s.GenTps!.Value),
                Avg = g.Average(s => s.GenTps!.Value),
                Low = g.Min(s => s.GenTps!.Value),
            })
            .FirstOrDefaultAsync(ct);

        return agg is null ? null : new GenerationThroughputStats { Peak = agg.Peak, Avg = agg.Avg, Low = agg.Low };
    }

    public async Task ClearAsync(string sourceId, CancellationToken ct = default)
    {
        if (!Guid.TryParse(sourceId, out var sid))
            return;

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        await db.MetricsSamples.Where(s => s.SourceId == sid).ExecuteDeleteAsync(ct);
        await db.MetricsRollups.Where(r => r.SourceId == sid).ExecuteDeleteAsync(ct);
    }
}
