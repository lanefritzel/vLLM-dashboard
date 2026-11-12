using Dashboard.Application.Metrics;
using Dashboard.Core.Abstractions;
using Dashboard.Core.Models;
using Dashboard.Infrastructure.Identity;
using Dashboard.Infrastructure.Persistence.V2;
using Dashboard.Infrastructure.Proxy;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Dashboard.Web.Metrics;

/// <summary>P14's own gap: the entire metrics-scraping pipeline (<see cref="IMetricsService"/>,
/// <see cref="MetricsHub"/>, <see cref="ISampleStore"/>) was built against the OLD
/// legacy <c>Source</c> table and has no connection to the new V2 <see cref="Instance"/>
/// table at all. Rather than build a second scraping/storage stack, this reuses the
/// existing one unchanged: it builds a synthetic <see cref="SourceInfo"/> per active
/// V2 instance (id prefixed "instance-" so it can never collide with a real legacy
/// source's own guid-shaped id) and feeds it through the same <see cref="IMetricsService"/>
/// + <see cref="MetricsHub"/> the old Overview page used. Same "stop all, rebind"
/// loop-management shape as <see cref="MetricsPollingService"/>, just periodic instead
/// of event-driven (V2 instances have no "changed" notification to subscribe to).</summary>
public sealed class InstanceMetricsPollingService : IHostedService, IDisposable
{
    private static readonly TimeSpan RebindInterval = TimeSpan.FromSeconds(15);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IMetricsService _metrics;
    private readonly IHubContext<MetricsHub> _hub;
    private readonly IBroadcastThrottler _throttler;
    private readonly ILogger<InstanceMetricsPollingService> _logger;

    private readonly Dictionary<int, CancellationTokenSource> _loops = new();
    private readonly object _lock = new();
    private CancellationTokenSource? _rebindLoopCts;
    private volatile bool _disposed;

    public InstanceMetricsPollingService(
        IServiceScopeFactory scopeFactory, IMetricsService metrics, IHubContext<MetricsHub> hub,
        IBroadcastThrottler throttler, ILogger<InstanceMetricsPollingService> logger)
    {
        _scopeFactory = scopeFactory;
        _metrics = metrics;
        _hub = hub;
        _throttler = throttler;
        _logger = logger;
    }

    public static string SourceIdFor(int instanceId) => InstanceSourceId.For(instanceId);

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _rebindLoopCts = new CancellationTokenSource();
        _ = Task.Run(() => RebindLoopAsync(_rebindLoopCts.Token));
        _logger.LogInformation("Instance metrics polling started.");
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _rebindLoopCts?.Cancel();
        StopAllLoops();
        _logger.LogInformation("Instance metrics polling stopped.");
        return Task.CompletedTask;
    }

    private async Task RebindLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            await RebindAsync(token);
            try
            {
                await Task.Delay(RebindInterval, token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task RebindAsync(CancellationToken token)
    {
        if (_disposed) return;

        List<Instance> instances;
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            instances = await db.InstancesV2.Where(i => i.IsActive).ToListAsync(token);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not read V2 instances; keeping existing scrape loops.");
            return;
        }

        StopAllLoops();

        lock (_lock)
        {
            foreach (var instance in instances)
            {
                var baseUrl = ModelProxyService.BuildInferenceBaseUrl(instance);
                if (baseUrl is null) continue; // no host configured yet -- nothing to scrape

                var source = InstanceSourceMapper.ToSourceInfo(instance, SourceIdFor(instance.Id), baseUrl);

                var cts = new CancellationTokenSource();
                _loops[instance.Id] = cts;
                _ = Task.Run(() => RunLoopAsync(source, cts.Token));
            }
        }
    }

    private async Task RunLoopAsync(SourceInfo source, CancellationToken token)
    {
        var interval = TimeSpan.FromSeconds(Math.Max(1, source.ScrapeIntervalSeconds));
        while (!token.IsCancellationRequested)
        {
            try
            {
                var sample = await _metrics.ScrapeAndStoreAsync(source, token);
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
                _logger.LogWarning(ex, "Scrape failed for instance {Label}.", source.Label);
            }

            try
            {
                await Task.Delay(interval, token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
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

    public void Dispose()
    {
        _disposed = true;
        _rebindLoopCts?.Cancel();
        StopAllLoops();
    }
}
