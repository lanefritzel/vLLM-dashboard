namespace Dashboard.Core.Downloads;

public sealed record DiskGuardResult
{
    public required bool Ok { get; init; }

    /// <summary>Set only when <see cref="Ok"/> is false -- the exact "Not enough disk
    /// space ... Required X, Available Y" message the page specifies.</summary>
    public string? Message { get; init; }
}

/// <summary>
/// Pure function (P08): blocks a download before it starts if the models volume
/// doesn't have enough free space. No I/O -- `requiredBytes` comes from the
/// supervisor's own inspect response (summed file sizes), `availableBytes` from
/// that same response's `free_bytes`.
/// </summary>
public static class DiskGuard
{
    public static DiskGuardResult Check(long requiredBytes, long availableBytes)
    {
        if (availableBytes < requiredBytes)
        {
            return new DiskGuardResult
            {
                Ok = false,
                Message = $"Not enough disk space. Required {requiredBytes:N0} bytes, available {availableBytes:N0} bytes.",
            };
        }

        return new DiskGuardResult { Ok = true };
    }
}
