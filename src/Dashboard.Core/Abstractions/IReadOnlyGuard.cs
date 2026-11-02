namespace Dashboard.Core.Abstractions;

/// <summary>Shared write-guard (P12): "Postgres down -> the dashboard goes
/// read-only ... writes refused; reads that don't need the DB (supervisor
/// status, playground, logs) still work." Checked at the top of every
/// write method on the V2 services -- reads are never guarded, since
/// nothing here should (or needs to) block them. Backed by a periodic DB
/// health check (<c>DatabaseHealthMonitorService</c>, Infrastructure),
/// not by catching a write's own failure after the fact.</summary>
public interface IReadOnlyGuard
{
    bool IsReadOnly { get; }

    /// <summary>Flips the guard's own state -- called only by the health
    /// monitor; everything else should only ever call <see cref="EnsureWritable"/>.</summary>
    void SetReadOnly(bool isReadOnly);

    /// <summary>Throws <see cref="ReadOnlyModeException"/> if currently
    /// read-only. Called first thing in every Create/Update/Delete/
    /// Transition method on the V2 services.</summary>
    void EnsureWritable();

    event EventHandler? Changed;
}

/// <summary>Thrown by <see cref="IReadOnlyGuard.EnsureWritable"/> -- a future
/// UI (P14/P15) catches this to show the read-only banner; this phase only
/// needs the throw itself to exist and be test-visible.</summary>
public sealed class ReadOnlyModeException : Exception
{
    public ReadOnlyModeException() : base("The dashboard is read-only right now (database unreachable).")
    {
    }
}
