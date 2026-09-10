namespace Dashboard.Core.Abstractions;

/// <summary>Gate that limits how often a live update may be broadcast per key.</summary>
public interface IBroadcastThrottler
{
    int MinIntervalMs { get; }
    bool Allow(string key);
    void Reset(string key);
}
