namespace Dashboard.Infrastructure.Persistence.V2;

/// <summary>A vLLM instance on a <see cref="Server"/> (table <c>instances</c>) — the
/// new-schema successor to the legacy <see cref="Dashboard.Infrastructure.Persistence.Source"/>.
/// Owns the stable proxy URL (invariant: <c>/proxy/&lt;slug&gt;/v1</c> never changes),
/// not the model lifecycle — that's the supervisor's, reached at <see cref="AdminUrl"/>.</summary>
public class Instance
{
    public int Id { get; set; }
    public int ServerId { get; set; }
    public string Label { get; set; } = "";
    public string ProxySlug { get; set; } = "";

    /// <summary>The supervisor's own admin API base, e.g. http://host:port/admin. Null
    /// for an instance converted from a legacy source until its supervisor is
    /// registered (doc 13) — that instance shows "needs supervisor registration".</summary>
    public string? AdminUrl { get; set; }

    /// <summary>The vLLM-facing host (container name/IP), independent of
    /// <see cref="AdminUrl"/> (P12): "needs supervisor registration" is a
    /// reconcile banner, not a traffic outage, so the proxy must still be
    /// able to forward requests to an instance with no supervisor
    /// registered yet. <c>ModelProxyService.BuildInferenceBaseUrl</c>
    /// prefers this field and only falls back to deriving a host from
    /// <see cref="AdminUrl"/> when it's null, so an already-registered
    /// instance doesn't need it set twice.</summary>
    public string? Host { get; set; }

    /// <summary>The Docker container name running this instance's supervisor
    /// (P12) -- addressed against <see cref="Server.DockerHost"/> for the
    /// minimal Docker client's Start/Hard-reset actions when the supervisor
    /// itself is unreachable. Null until set (e.g. at supervisor registration,
    /// P15b) -- those actions simply aren't offered without it.</summary>
    public string? ContainerName { get; set; }

    /// <summary>This instance's own supervisor token, encrypted at rest with
    /// ASP.NET Data Protection (P15b -- replaces the single config-wide token
    /// <c>ConfigSupervisorTokenProvider</c> used before). Null until
    /// registered; never logged or round-tripped to the page in plaintext --
    /// see <c>InstanceTokenProvider</c>/the Servers page for the only two
    /// places that ever decrypt or set it.</summary>
    public string? AdminTokenEnc { get; set; }

    public int Port { get; set; } = 9999;
    public int PollIntervalMs { get; set; } = 1000;
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public Server? Server { get; set; }
}
