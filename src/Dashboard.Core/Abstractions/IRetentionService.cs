namespace Dashboard.Core.Abstractions;

/// <summary>Prunes raw samples older than retention and upserts hourly rollups.</summary>
public interface IRetentionService
{
    Task RunOnceAsync(CancellationToken ct = default);
}
