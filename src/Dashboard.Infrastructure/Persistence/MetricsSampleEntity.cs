namespace Dashboard.Infrastructure.Persistence;

public class MetricsSampleEntity
{
    public long Id { get; set; }
    public DateTime Ts { get; set; }
    // Was Guid? (a hard FK to Sources) -- widened to a plain string (P16a
    // finding #21): Sources rows only exist for legacy sources, so a V2
    // instance's own id ("instance-N") could never satisfy that FK at all,
    // silently dropping to null on every write and excluding every V2
    // instance from rollups/retention/generation-stats/Clear -- all of
    // which already took sourceId as a plain string at their own public
    // API boundary and only reintroduced a Guid internally to query this
    // column. Legacy ids still work unchanged: they're stored as
    // Guid.ToString()'s default format, same as every other boundary in
    // the app already converts them.
    public string? SourceId { get; set; }
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

    public Core.Models.MetricsSample ToInfo() => new()
    {
        Ts = Ts,
        SourceId = SourceId ?? "",
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
