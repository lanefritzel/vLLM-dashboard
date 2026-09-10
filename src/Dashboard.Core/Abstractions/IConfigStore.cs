namespace Dashboard.Core.Abstractions;

/// <summary>Typed app-level configuration with a change event (key/value in Postgres).</summary>
public interface IConfigStore
{
    int GetInt(string key, int defaultValue);
    string Get(string key, string defaultValue);
    Task SetAsync(string key, string value, string? updatedBy = null, CancellationToken ct = default);

    int RetentionDays { get; }
    bool RollupsEnabled { get; }
    int UiUpdateRateMs { get; }
    int DefaultHistoryWindowMinutes { get; }

    /// <summary>Raised after any source/model/config change; listeners re-bind.</summary>
    event EventHandler? Changed;

    /// <summary>Signals that configuration changed so listeners re-bind.</summary>
    void RaiseChanged();
}
