namespace Dashboard.Infrastructure.Persistence;

/// <summary>A captured application log line — see Logging/DbLoggerProvider for how
/// these get here. Deliberately not tied to any other entity (no SourceId FK, etc.):
/// this exists purely as a durable, queryable record of what the app logged, for
/// diagnosing incidents after the fact without relying on `docker logs`, which is
/// tied to the container's lifetime and lost on every redeploy.</summary>
public sealed class AppLogEntity
{
    public long Id { get; set; }
    public DateTime TimestampUtc { get; set; }
    public string Level { get; set; } = "";
    public string Category { get; set; } = "";
    public string Message { get; set; } = "";
    public string? Exception { get; set; }
}
