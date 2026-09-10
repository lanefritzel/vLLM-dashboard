using Dashboard.Core.Abstractions;
using Dashboard.Core.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Dashboard.Application.Startup;

/// <summary>
/// Hosted service: once at app startup, brings up any model that's both the active
/// one for its source AND explicitly flagged PreloadOnStartup — e.g. after a host
/// reboot or an idle-unload left nothing running, so the first real request doesn't
/// have to pay the swap cost itself. Explicit opt-in only: a restart never touches a
/// model that isn't flagged for it. Skips a source entirely if its model already
/// looks like it's running, the same reconciliation DockerOrchestrator's swap path
/// uses, so this never tears down an already-healthy container on every deploy.
/// </summary>
public sealed class PreloadService : IHostedService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<PreloadService> _logger;

    public PreloadService(IServiceScopeFactory scopeFactory, ILogger<PreloadService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        // Fire-and-forget: DbInitializerHostedService (registered earlier) already
        // blocks its own StartAsync until migrations/seeding finish, so the DB is ready
        // by the time this runs — this doesn't need to block the rest of the host too.
        _ = Task.Run(() => RunAsync(cancellationToken), CancellationToken.None);
        return Task.CompletedTask;
    }

    private async Task RunAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var sources = scope.ServiceProvider.GetRequiredService<ISourceCatalog>();
        var models = scope.ServiceProvider.GetRequiredService<IModelCatalog>();
        var launchSpecs = scope.ServiceProvider.GetRequiredService<ILaunchSpecStore>();
        var orchestrator = scope.ServiceProvider.GetRequiredService<IDockerOrchestrator>();

        IReadOnlyList<SourceInfo> activeSources;
        try
        {
            activeSources = await sources.GetActiveAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Startup preload: could not read sources.");
            return;
        }

        foreach (var source in activeSources)
        {
            try
            {
                await PreloadSourceAsync(source, models, launchSpecs, orchestrator, ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Startup preload failed for {Source}.", source.Label);
            }
        }
    }

    private async Task PreloadSourceAsync(
        SourceInfo source, IModelCatalog models, ILaunchSpecStore launchSpecs, IDockerOrchestrator orchestrator, CancellationToken ct)
    {
        var model = await models.GetActiveAsync(source.Id, ct);
        if (model is null)
        {
            // No active model at all — if one is flagged PreloadOnStartup anyway, this
            // is worth a log line: it's the most likely reason preload silently did
            // nothing (bookkeeping drifted, e.g. from an idle-unload that never got a
            // follow-up request to reconcile it back to active).
            var flagged = (await models.GetBySourceAsync(source.Id, ct)).FirstOrDefault(m => m.PreloadOnStartup);
            if (flagged is not null)
                _logger.LogWarning(
                    "Startup preload: {Model} on {Source} is flagged PreloadOnStartup but isn't the active model — skipping. " +
                    "Activate it on the Models page if you want it preloaded.", flagged.Name, source.Label);
            return;
        }
        if (!model.PreloadOnStartup)
            return;

        var actuallyRunning = await orchestrator.GetActuallyRunningModelNameAsync(source, ct);
        if (actuallyRunning is not null && string.Equals(actuallyRunning, model.Name, StringComparison.Ordinal))
        {
            _logger.LogInformation("Startup preload: {Model} on {Source} is already running.", model.Name, source.Label);
            return;
        }

        var spec = await launchSpecs.GetBySourceAsync(source.Id, ct);
        if (spec is null || string.IsNullOrWhiteSpace(spec.Image))
        {
            _logger.LogWarning("Startup preload: {Source} has no server settings (image) configured yet — skipping.", source.Label);
            return;
        }

        _logger.LogInformation("Startup preload: bringing up {Model} on {Source}.", model.Name, source.Label);
        var result = await orchestrator.SwapModelAsync(source, model, spec, NoOpProgress.Instance, ct);
        if (result.Success)
            await models.ActivateAsync(model.Id, ct);
        else
            _logger.LogWarning("Startup preload failed for {Source}: {Error}", source.Label, result.Error);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
