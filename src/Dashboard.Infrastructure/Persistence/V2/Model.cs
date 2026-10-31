namespace Dashboard.Infrastructure.Persistence.V2;

/// <summary>A model artifact registered on a <see cref="Server"/>'s shared catalog
/// (table <c>models</c>) — weights on disk, never anything about a running container.
/// Distinct from the legacy <see cref="Dashboard.Infrastructure.Persistence.Model"/>,
/// which the old swap-era code paths still use until P16.</summary>
public class Model
{
    public int Id { get; set; }
    public int ServerId { get; set; }
    public string Name { get; set; } = "";
    public string? DisplayName { get; set; }

    /// <summary>"local" or "huggingface" — CHECK-constrained in the migration.</summary>
    public string SourceType { get; set; } = "local";

    public string? RepoId { get; set; }
    public string Path { get; set; } = "";
    public long? SizeBytes { get; set; }

    /// <summary>Intrinsic, read from config.json when present — never user-set.</summary>
    public string? Quantization { get; set; }

    public string LoadFormat { get; set; } = "auto";

    /// <summary>Parsed config.json (max_position_embeddings, ...), stored as jsonb.</summary>
    public string? Config { get; set; }

    /// <summary>"available" | "downloading" | "download_failed" — CHECK-constrained.
    /// "download_failed", not "failed": a failed download is not a failed swap.</summary>
    public string Status { get; set; } = "available";

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public Server? Server { get; set; }
}
