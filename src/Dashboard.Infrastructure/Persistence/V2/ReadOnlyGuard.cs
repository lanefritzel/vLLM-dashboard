using Dashboard.Core.Abstractions;

namespace Dashboard.Infrastructure.Persistence.V2;

/// <summary>Plain mutable state holder for P12's write-guard -- flipped only
/// by <see cref="DatabaseHealthMonitorService"/>, read by every V2 service's
/// write methods via <see cref="EnsureWritable"/>. Starts writable: at
/// process start, the health monitor hasn't run its first check yet, and
/// the DbInitializerHostedService's own startup migration already proves
/// the DB is reachable before anything else gets a chance to write.</summary>
public sealed class ReadOnlyGuard : IReadOnlyGuard
{
    private volatile bool _isReadOnly;

    public bool IsReadOnly => _isReadOnly;

    public event EventHandler? Changed;

    public void SetReadOnly(bool isReadOnly)
    {
        if (_isReadOnly == isReadOnly)
            return;
        _isReadOnly = isReadOnly;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void EnsureWritable()
    {
        if (_isReadOnly)
            throw new ReadOnlyModeException();
    }
}
