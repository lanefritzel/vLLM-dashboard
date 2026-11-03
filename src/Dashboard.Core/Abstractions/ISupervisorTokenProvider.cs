namespace Dashboard.Core.Abstractions;

/// <summary>Resolves the bearer token for a given instance's supervisor admin API
/// (P09b). Async (P15b): the token now lives per-instance in the DB, encrypted
/// with ASP.NET Data Protection (<c>Instance.AdminTokenEnc</c>), replacing the
/// single config-wide token <c>ConfigSupervisorTokenProvider</c> used before --
/// decrypting it is I/O (a DB read), so a synchronous signature would have
/// forced a blocking call at every one of this interface's several callers.
/// Null means no token is configured for this instance, which a caller should
/// treat as "cannot authenticate," never as "no auth needed."</summary>
public interface ISupervisorTokenProvider
{
    Task<string?> GetTokenAsync(int instanceId, CancellationToken ct = default);
}
