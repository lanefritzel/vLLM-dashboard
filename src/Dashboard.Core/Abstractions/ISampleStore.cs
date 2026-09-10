using Dashboard.Core.Models;

namespace Dashboard.Core.Abstractions;

/// <summary>
/// In-memory per-source ring buffer (live charts) plus durable append (EF batch).
/// </summary>
public interface ISampleStore
{
    void Append(MetricsSample sample);
    IReadOnlyList<MetricsSample> GetRecent(string sourceId, int maxPoints);
}
