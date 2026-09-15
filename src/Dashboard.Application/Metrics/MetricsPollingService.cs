using Dashboard.Core.Abstractions;
using Dashboard.Core.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.AspNetCore.SignalR;

namespace Dashboard.Application.Metrics;

/// <summary>
/// Hosted service. Runs one scrape loop per active source on its own timer and
/// broadcasts new samples to the source's SignalR group (throttled). Re-binds
/// whenever the source catalog changes.
/// </summary>
public sealed class MetricsPollingService : IHostedService, IDisposable
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IMetricsService _metrics;
    private readonly IHubContext<MetricsHub> _hub;
    private readonly IBroadcastThrottler _throttler;
    private readonly IConfigStore _config;
    private readonly ILogger<MetricsPollingService> _logger;

    private readonly Dictionary<string, CancellationTokenSource> _loops = new();
    private readonly object _lock = new();
    private volatile bool _disposed;

    public MetricsPollingService(
        IServiceScopeFactory scopeFactory,
        IMetricsService metrics,
        IHubContext<MetricsHub> hub,
        IBroadcastThrottler throttler,
        IConfigStore config,
        ILogger<MetricsPollingService> logger)
    {
        _scopeFactory = scopeFactory;
        _metrics = metrics;
        _hub = hub;
        _throttler = throttler;
        _config = config;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _config.Changed += OnConfigChanged;
        await RebindAsync();
        _logger.LogInformation("Metrics polling started.");
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _config.Changed -= OnConfigChanged;
        StopAllLoops();
        _logger.LogInformation("Metrics polling stopped.");
        return Task.CompletedTask;
    }

    private void OnConfigChanged(object? sender, EventArgs e)
    {
        _ = Task.Run(() => RebindAsync());
    }

    private async Task RebindAsync()
    {
        if (_disposed) return;

        IReadOnlyList<SourceInfo> active;
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var sources = scope.ServiceProvider.GetRequiredService<ISourceCatalog>();
            active = await sources.GetActiveAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not read sources; keeping existing loops.");
            return;
        }

        // At MVP we simply restart all loops: cheap, and guarantees timers and
        // intervals reflect the latest catalog state.
        StopAllLoops();

        lock (_lock)
        {
            foreach (var src in active)
            {
                var cts = new CancellationTokenSource();
                _loops[src.Id] = cts;
                _ = Task.Run(() => RunLoopAsync(src, cts.Token));
                _logger.LogInformation("Scrape loop started for {Source} every {Interval}s.", src.Label, src.ScrapeIntervalSeconds);
            }
        }

        // Tell any connected islands to re-read sources/models/config.
        await _hub.Clients.All.SendAsync(MetricsHub.ConfigUpdatedEvent);
    }

    private void StopAllLoops()
    {
        lock (_lock)
        {
            foreach (var cts in _loops.Values)
                cts.Cancel();
            _loops.Clear();
        }
    }

    private async Task RunLoopAsync(SourceInfo src, CancellationToken token)
    {
        var interval = TimeSpan.FromSeconds(Math.Max(1, src.ScrapeIntervalSeconds));
        while (!token.IsCancellationRequested)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                var sample = await _metrics.ScrapeAndStoreAsync(src, token);
                if (_throttler.Allow(sample.SourceId))
                {
                    await _hub.Clients
                        .Group(MetricsHub.GroupFor(sample.SourceId))
                        .SendAsync(MetricsHub.SampleEvent, sample, token);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Scrape failed for {Source}.", src.Label);
            }

            sw.Stop();
            var delay = interval - sw.Elapsed;
            if (delay > TimeSpan.Zero)
            {
                try
                {
                    await Task.Delay(delay, token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
            else
            {
                // The scrape itself took as long as (or longer than) the requested
                // interval, so the loop can't actually go any faster than the scrape's
                // own latency — the configured interval becomes a no-op floor rather
                // than the real cadence. Without this, a user picking a faster poll
                // interval sees no error, just the UI silently settling back to
                // whatever cadence the backend's response time allows.
                _logger.LogWarning(
                    "Scrape for {Source} took {Elapsed}ms, >= the configured {Interval}s interval — actual cadence is bounded by scrape latency, not the configured interval.",
                    src.Label, sw.ElapsedMilliseconds, src.ScrapeIntervalSeconds);
            }
        }
    }

    public void Dispose()
    {
        _disposed = true;
        StopAllLoops();
    }
}
