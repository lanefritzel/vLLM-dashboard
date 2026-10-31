namespace Dashboard.Infrastructure.Persistence.V2;

/// <summary>A (model, recipe) applied to an <see cref="Instance"/> (table
/// <c>deployments</c>) — immutable at runtime; a swap is a transaction that inserts a
/// new <c>pending</c> row and, once the supervisor confirms it, flips this one
/// <c>active</c> and the previous one <c>stopped</c>. Powers the Deployments page —
/// distinct from "History" (the 30-day metrics page).</summary>
public class Deployment
{
    public int Id { get; set; }
    public int InstanceId { get; set; }
    public int ModelId { get; set; }
    public int RecipeId { get; set; }

    /// <summary>Exact vLLM argv at deploy time (v1: argv only), stored as jsonb.</summary>
    public string ArgsSnapshot { get; set; } = "";

    /// <summary>"pending" | "active" | "stopped" | "failed" — CHECK-constrained. A
    /// partial unique index on (instance_id) WHERE status IN ('pending','active')
    /// makes a second concurrent swap on the same instance impossible at the DB
    /// level, not just in application code.</summary>
    public string Status { get; set; } = "pending";

    /// <summary>"swapped" | "rollback" | "manual" | "crash" | "cancelled" —
    /// CHECK-constrained; null while not yet stopped.</summary>
    public string? StopReason { get; set; }

    /// <summary>"process_exited" | "health_timeout" | "bad_args" | "out_of_memory" —
    /// CHECK-constrained; the supervisor's structured failure report (doc 5), null
    /// unless this deployment failed or crashed.</summary>
    public string? FailureReason { get; set; }

    public int? ExitCode { get; set; }

    /// <summary>Last ~50 lines of the child process's output.</summary>
    public string? LogTail { get; set; }

    public DateTime? StartedAt { get; set; }
    public DateTime? StoppedAt { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public Instance? Instance { get; set; }
    public Model? Model { get; set; }
    public Recipe? Recipe { get; set; }
}
