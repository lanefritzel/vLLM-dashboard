namespace Dashboard.Core.Abstractions;

/// <summary>Resolves the bearer token for a given instance's supervisor admin API
/// (P09b). Where/how the token is actually stored per-instance is the open item
/// decided in P15b (likely a Servers page letting the operator paste one in) --
/// this interface exists now so callers (P10a's dispatch) don't need to change
/// once that's decided. Null means no token is configured for this instance,
/// which a caller should treat as "cannot authenticate," never as "no auth
/// needed."</summary>
public interface ISupervisorTokenProvider
{
    string? GetToken(int instanceId);
}
