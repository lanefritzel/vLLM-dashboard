using Dashboard.Core.Abstractions;
using Dashboard.Infrastructure.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Dashboard.Infrastructure.Persistence;

/// <summary>
/// Typed config backed by the Config table. Singleton; opens a DB scope per
/// operation so the hosted polling service can subscribe to <see cref="Changed"/>
/// without a captive dependency.
/// </summary>
public sealed class ConfigStore : IConfigStore
{
    private readonly IServiceScopeFactory _scopeFactory;

    public ConfigStore(IServiceScopeFactory scopeFactory)
    {
        _scopeFactory = scopeFactory;
    }

    public event EventHandler? Changed;

    public int RetentionDays => GetInt("retention.days", 30);
    public bool RollupsEnabled => Get("rollups.enabled", "true") == "true";
    public int UiUpdateRateMs => GetInt("ui.updateRateMs", 1000);
    public int DefaultHistoryWindowMinutes => GetInt("default.history.windowMinutes", 60);

    public int GetInt(string key, int defaultValue)
    {
        var raw = Get(key, defaultValue.ToString());
        return int.TryParse(raw, out var v) ? v : defaultValue;
    }

    public string Get(string key, string defaultValue)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var row = db.Configs.AsNoTracking().FirstOrDefault(c => c.Key == key);
        return row is null ? defaultValue : row.Value;
    }

    public async Task SetAsync(string key, string value, string? updatedBy = null, CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var row = await db.Configs.FirstOrDefaultAsync(c => c.Key == key, ct);
        if (row is null)
        {
            db.Configs.Add(new AppConfig { Key = key, Value = value, UpdatedAt = DateTime.UtcNow, UpdatedBy = updatedBy });
        }
        else
        {
            row.Value = value;
            row.UpdatedAt = DateTime.UtcNow;
            row.UpdatedBy = updatedBy;
        }
        await db.SaveChangesAsync(ct);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Notifies listeners (e.g. the polling service) that sources/config changed.</summary>
    public void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);
}
