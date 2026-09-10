namespace Dashboard.Infrastructure.Persistence;

public class MetricsRollupEntity
{
    public long Id { get; set; }
    public DateTime BucketStart { get; set; }
    public Guid SourceId { get; set; }
    public string ModelName { get; set; } = "";
    public int Samples { get; set; }
    public double? AvgRunning { get; set; }
    public double? AvgWaiting { get; set; }
    public double? AvgKvPct { get; set; }
    public double? MaxKvPct { get; set; }
    public long TokensGenerated { get; set; }
    public long TokensPrompt { get; set; }
    public long RequestsCompleted { get; set; }
    public double? P50E2eSec { get; set; }
    public double? P95E2eSec { get; set; }
    public double? P99E2eSec { get; set; }
    public double? P50TtftSec { get; set; }
    public double? P95TtftSec { get; set; }
    public double? AvgGenTps { get; set; }
    public double? PeakGenTps { get; set; }

    public Source? Source { get; set; }
}
