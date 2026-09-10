namespace Dashboard.Core.Abstractions;

/// <summary>Tracks the last time a source was actually used by a proxied request —
/// the signal <c>IdleUnloadService</c> compares against a model's idle TTL. In-memory
/// only (a restart resets it, which is fine: nothing was "idle" before the app was
/// even running to observe it).</summary>
public interface IActivityTracker
{
    void Touch(string sourceId);
    DateTime? LastActivityUtc(string sourceId);
}
