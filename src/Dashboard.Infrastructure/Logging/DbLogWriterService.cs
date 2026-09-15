using System.Threading.Channels;
using Dashboard.Infrastructure.Identity;
using Dashboard.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Dashboard.Infrastructure.Logging;

/// <summary>Drains the channel DbLoggerProvider writes into and batches entries into
/// the database — the only place a DB write actually happens, kept entirely off the
/// hot path of whatever request triggered the log line. A short delay between the
/// first item in a batch and flushing lets a burst of log lines (e.g. an exception's
/// stack trace plus surrounding context) become one INSERT round-trip instead of one
/// per line, without ever holding a log call itself up waiting for the database.</summary>
public sealed class DbLogWriterService : BackgroundService
{
    private const int MaxBatchSize = 200;
    private static readonly TimeSpan BatchWindow = TimeSpan.FromMilliseconds(500);

    private readonly ChannelReader<AppLogRecord> _reader;
    private readonly IServiceScopeFactory _scopeFactory;

    public DbLogWriterService(ChannelReader<AppLogRecord> reader, IServiceScopeFactory scopeFactory)
    {
        _reader = reader;
        _scopeFactory = scopeFactory;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var batch = new List<AppLogRecord>(MaxBatchSize);
        while (!stoppingToken.IsCancellationRequested)
        {
            AppLogRecord first;
            try
            {
                first = await _reader.ReadAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            batch.Clear();
            batch.Add(first);

            // Let a short burst accumulate before flushing, capped either way.
            try { await Task.Delay(BatchWindow, stoppingToken); } catch (OperationCanceledException) { }
            while (batch.Count < MaxBatchSize && _reader.TryRead(out var more))
                batch.Add(more);

            await FlushAsync(batch);
        }

        // Best-effort drain of whatever's left on shutdown.
        batch.Clear();
        while (_reader.TryRead(out var remaining))
            batch.Add(remaining);
        if (batch.Count > 0)
            await FlushAsync(batch);
    }

    private async Task FlushAsync(List<AppLogRecord> batch)
    {
        if (batch.Count == 0) return;
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            foreach (var r in batch)
            {
                db.AppLogs.Add(new AppLogEntity
                {
                    TimestampUtc = r.TimestampUtc,
                    Level = r.Level,
                    Category = r.Category,
                    Message = r.Message,
                    Exception = r.Exception,
                });
            }
            await db.SaveChangesAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            // A logging failure must never take anything else down, and must not
            // recurse back through this same provider — straight to stderr.
            Console.Error.WriteLine($"[DbLogWriterService] Failed to flush {batch.Count} log entries: {ex}");
        }
    }
}
