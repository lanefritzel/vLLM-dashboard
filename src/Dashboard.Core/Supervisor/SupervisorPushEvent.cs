namespace Dashboard.Core.Supervisor;

/// <summary>The shape of a single push the supervisor POSTs to the dashboard's
/// receiver (P10b, doc 5): <c>{deployment_id, seq, ts, kind, ...}</c> where
/// the trailing fields vary by <see cref="Kind"/> -- "state" carries
/// <see cref="State"/>/<see cref="Detail"/>, "report" carries
/// <see cref="Report"/>, "log" carries <see cref="Lines"/>. Deserialized with
/// <c>JsonNamingPolicy.SnakeCaseLower</c>, so unused fields for a given kind
/// are simply absent/null, not an error.</summary>
public sealed record SupervisorPushEvent
{
    /// <summary>The dashboard's own deployments.id, as a string (P10a passes
    /// it to the supervisor; the supervisor only ever echoes it back).</summary>
    public required string DeploymentId { get; init; }

    public int Seq { get; init; }
    public double Ts { get; init; }

    /// <summary>"state" | "report" | "log".</summary>
    public required string Kind { get; init; }

    public string? State { get; init; }
    public Dictionary<string, object?>? Detail { get; init; }
    public Dictionary<string, object?>? Report { get; init; }
    public List<string>? Lines { get; init; }
}
