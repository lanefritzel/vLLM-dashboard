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

    /// <summary>stop | releasing_gpu | starting | loading | waiting_health | ready |
    /// crashed | failed | smoke.</summary>
    public string State { get; set; } = "";

    public string? Detail { get; set; }
    public DateTime EventAt { get; set; } = DateTime.UtcNow;

    public Deployment? Deployment { get; set; }
}
