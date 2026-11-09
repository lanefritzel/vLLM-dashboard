namespace Dashboard.Infrastructure.Persistence.V2;

/// <summary>A lifecycle event for a <see cref="Deployment"/> (table
/// <c>deployment_events</c>), written by the dashboard from events the supervisor
/// pushes. <see cref="Seq"/> is supervisor-assigned and increasing per deployment —
/// the (deployment_id, seq) uniqueness makes a re-delivered or caught-up event a
/// no-op. Powers the Deployments page's expandable rows.</summary>
public class DeploymentEvent
{
    public long Id { get; set; }
    public int DeploymentId { get; set; }
    public int Seq { get; set; }

    /// <summary>stopping | releasing_gpu | starting | loading | warming_up | waiting_health |
    /// ready | crashed | failed | smoke. loading/warming_up/starting carry vLLM's own reported
    /// progress in <see cref="Detail"/> (weights_pct, shards, graphs_pct, note).</summary>
    public string State { get; set; } = "";

    public string? Detail { get; set; }
    public DateTime EventAt { get; set; } = DateTime.UtcNow;

    public Deployment? Deployment { get; set; }
}
