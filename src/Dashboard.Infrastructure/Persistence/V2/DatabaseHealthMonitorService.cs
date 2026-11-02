using Dashboard.Core.Abstractions;
using Dashboard.Infrastructure.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Dashboard.Infrastructure.Persistence.V2;

/// <summary>Drives P12's read-only mode: "Postgres down -> the dashboard
/// goes read-only ... restoring Postgres clears read-only." Every few
/// seconds, tries a trivial connection against Postgres and flips the
/// shared <see cref="IReadOnlyGuard"/> accordingly. Deliberately the ONLY
/// thing that ever calls <see cref="IReadOnlyGuard.SetReadOnly"/> -- every
/// V2 service write just reads the guard's current state.</summary>
public sealed class DatabaseHealthMonitorService : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IReadOnlyGuard _guard;
    private readonly ILogger<DatabaseHealthMonitorService> _logger;

    public DatabaseHealthMonitorService(IServiceScopeFactory scopeFactory, IReadOnlyGuard guard, ILogger<DatabaseHealthMonitorService> logger)
    {
        _scopeFactory = scopeFactory;
        _guard = guard;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var healthy = await db.Database.CanConnectAsync(stoppingToken);
                _guard.SetReadOnly(!healthy);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                // Can't even tell -- treat "can't check" the same as "down".
                _guard.SetReadOnly(true);
                _logger.LogWarning(ex, "Database health check failed; entering read-only mode.");
            }

            try
            {
                await Task.Delay(PollInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }
}
