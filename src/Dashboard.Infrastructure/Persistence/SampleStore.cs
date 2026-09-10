using System.Collections.Concurrent;
using Dashboard.Core.Abstractions;
using Dashboard.Core.Models;
using Dashboard.Infrastructure.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Dashboard.Infrastructure.Persistence;

/// <summary>
/// Per-source in-memory ring buffer (live charts) + fire-and-forget batched EF insert.
/// A DB failure never blocks the live loop: overflow is queued in memory and the oldest
/// entries are dropped. Inserts are batched (AddRange + one SaveChanges).
/// </summary>
public sealed class SampleStore : ISampleStore
{
    private readonly ConcurrentDictionary<string, ConcurrentQueue<MetricsSample>> _rings = new();
    private readonly ConcurrentQueue<MetricsSample> _outbox = new();
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<SampleStore> _logger;
    private readonly SemaphoreSlim _flushGate = new(1, 1);
    private int _inFlight;

    private const int RingCapacity = 900;      // ~30 min @ 2 s
    private const int OutboxCapacity = 5000;   // drop-oldest beyond this
    private const int FlushThreshold = 20;     // trigger a flush at this many pending
    private const int BatchSize = 1000;        // max samples per SaveChanges

    public SampleStore(IServiceScopeFactory scopeFactory, ILogger<SampleStore> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public void Append(MetricsSample sample)
    {
        var ring = _rings.GetOrAdd(sample.SourceId, _ => new ConcurrentQueue<MetricsSample>());
        ring.Enqueue(sample);
        while (ring.Count > RingCapacity && ring.TryDequeue(out _)) { }

        if (sample.Up)
        {
            _outbox.Enqueue(sample);
            while (_outbox.Count > OutboxCapacity && _outbox.TryDequeue(out _)) { }
            if (Interlocked.Increment(ref _inFlight) >= FlushThreshold)
                _ = Task.Run(() => _ = FlushAsync());
        }
    }

    public IReadOnlyList<MetricsSample> GetRecent(string sourceId, int maxPoints)
    {
        if (!_rings.TryGetValue(sourceId, out var ring))
            return Array.Empty<MetricsSample>();
        var all = ring.ToArray();
        var start = Math.Max(0, all.Length - maxPoints);
        return all[start..];
    }

    /// <summary>Drains the outbox and writes in batches; never throws.</summary>
    public async Task FlushAsync()
    {
        if (!await _flushGate.WaitAsync(0))
            return; // a flush is already in flight
        try
        {
            while (_outbox.TryDequeue(out var first))
            {
                var batch = new List<MetricsSample>(BatchSize) { first };
                while (batch.Count < BatchSize && _outbox.TryDequeue(out var next))
                    batch.Add(next);

                try
                {
                    using var scope = _scopeFactory.CreateScope();
                    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                    var entities = batch.Select(ToEntity).ToArray();
                    db.MetricsSamples.AddRange(entities);
                    await db.SaveChangesAsync();
                    Interlocked.Add(ref _inFlight, -batch.Count);
                }
                catch (Exception ex)
                {
                    Interlocked.Add(ref _inFlight, -batch.Count);
                    _logger.LogWarning(ex, "SampleStore flush failed; {Count} samples dropped.", batch.Count);
                }
            }
        }
        finally
        {
            _flushGate.Release();
        }
    }

    private static MetricsSampleEntity ToEntity(MetricsSample s) => new()
    {
        SourceId = Guid.TryParse(s.SourceId, out var sid) ? sid : (Guid?)null,
        SourceLabel = s.SourceLabel,
        ModelName = s.ModelName,
        Ts = s.Ts,
        Running = s.Running,
        Waiting = s.Waiting,
        KvPct = s.KvPct,
        GenTps = s.GenTps,
        PromptTps = s.PromptTps,
        ReqTps = s.ReqTps,
        TtftAvgSec = s.TtftAvgSec,
        ItlAvgSec = s.ItlAvgSec,
        E2eAvgSec = s.E2eAvgSec,
        QueueAvgSec = s.QueueAvgSec,
        PrefixHitRatio = s.PrefixHitRatio,
        PreemptionsTotal = s.PreemptionsTotal,
        Up = s.Up,
    };
}
