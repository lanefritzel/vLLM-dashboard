namespace Dashboard.Infrastructure.Persistence;

public class MetricsSampleEntity
{
    public long Id { get; set; }
    public DateTime Ts { get; set; }
    public Guid? SourceId { get; set; }
    public string SourceLabel { get; set; } = "";
    public string? ModelName { get; set; }

    public int Running { get; set; }
    public int Waiting { get; set; }
    public double? KvPct { get; set; }
    public double? GenTps { get; set; }
    public double? PromptTps { get; set; }
    public double? ReqTps { get; set; }
    public double? TtftAvgSec { get; set; }
    public double? ItlAvgSec { get; set; }
    public double? E2eAvgSec { get; set; }
    public double? QueueAvgSec { get; set; }
    public double? PrefixHitRatio { get; set; }
    public long? PreemptionsTotal { get; set; }
    public bool Up { get; set; }

    public Source? Source { get; set; }

    public Core.Models.MetricsSample ToInfo() => new()
    {
        Ts = Ts,
        SourceId = SourceId?.ToString() ?? "",
        SourceLabel = SourceLabel,
        ModelName = ModelName,
        Running = Running,
        Waiting = Waiting,
        KvPct = KvPct,
        GenTps = GenTps,
        PromptTps = PromptTps,
        ReqTps = ReqTps,
        TtftAvgSec = TtftAvgSec,
        ItlAvgSec = ItlAvgSec,
        E2eAvgSec = E2eAvgSec,
        QueueAvgSec = QueueAvgSec,
        PrefixHitRatio = PrefixHitRatio,
        PreemptionsTotal = PreemptionsTotal,
        Up = Up,
    };
}
