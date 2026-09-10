using Dashboard.Core.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Dashboard.Application.Idle;

/// <summary>
/// Hosted service: every 30s, checks each active source's active model against its
/// own IdleTtlSeconds and the source's last-served-request time (from
/// IActivityTracker, updated by ModelProxyService on every successfully forwarded
/// request), and stops the container if it's been idle past that TTL. Mirrors
/// RollupService's PeriodicTimer pattern.
/// </summary>
public sealed class IdleUnloadService : IHostedService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IActivityTracker _activity;
    private readonly ILogger<IdleUnloadService> _logger;
    private PeriodicTimer _timer = null!;

    public IdleUnloadService(IServiceScopeFactory scopeFactory, IActivityTracker activity, ILogger<IdleUnloadService> logger)
    {
        _scopeFactory = scopeFactory;
        _activity = activity;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
        _ = Task.Run(() => LoopAsync(cancellationToken), CancellationToken.None);
        return Task.CompletedTask;
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        try
        {
            while (await _timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
                await SafeRunAsync(ct);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task SafeRunAsync(CancellationToken ct)
    {
        try
        {
            await RunOnceAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Idle-unload check failed.");
        }
    }

    public async Task RunOnceAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var sources = scope.ServiceProvider.GetRequiredService<ISourceCatalog>();
        var models = scope.ServiceProvider.GetRequiredService<IModelCatalog>();
        var orchestrator = scope.ServiceProvider.GetRequiredService<IDockerOrchestrator>();

        var activeSources = await sources.GetActiveAsync(ct);
        foreach (var source in activeSources)
        {
            var model = await models.GetActiveAsync(source.Id, ct);
            if (model is null || model.IdleTtlSeconds is not > 0)
                continue;

            // Never seen a proxied request for this source since the app started —
            // could just mean nobody has hit it yet, not that it's actually been idle
            // for the TTL, so don't guess; only the next real request will establish
            // a baseline.
            var lastActivity = _activity.LastActivityUtc(source.Id);
            if (lastActivity is null)
                continue;

            var idleFor = DateTime.UtcNow - lastActivity.Value;
            if (idleFor.TotalSeconds < model.IdleTtlSeconds.Value)
                continue;

            _logger.LogInformation(
                "Idle-unloading {Model} on {Source} after {Seconds}s idle (TTL {Ttl}s).",
                model.Name, source.Label, (int)idleFor.TotalSeconds, model.IdleTtlSeconds.Value);

            var result = await orchestrator.UnloadAsync(source, ct);
            if (result.Success)
                await models.DeactivateAsync(model.Id, ct);
            else
                _logger.LogWarning("Idle-unload failed for {Source}: {Error}", source.Label, result.Error);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _timer?.Dispose();
        return Task.CompletedTask;
    }
}
