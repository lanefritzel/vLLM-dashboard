namespace Dashboard.Core.Models;

/// <summary>The per-chunk fields P7 needs out of llama-server's own <c>timings</c>
/// object -- the subset of a much larger object the engine sends, not everything it
/// carries. Pure data: produced by <see
/// cref="Dashboard.Infrastructure.Proxy.LlamaCppTimingsParser"/> (Infrastructure, since
/// it's about parsing a proxy-observed HTTP stream chunk) but kept here in Core so both
/// that parser and <see cref="Dashboard.Core.Abstractions.ILlamaCppLiveMetricsPublisher"/>
/// can depend on the same plain data without Core depending on Infrastructure.</summary>
public sealed record LlamaCppChunkTimings
{
    /// <summary>The engine's own live generation-throughput figure for this request so
    /// far -- not computed by this app, read straight off the chunk.</summary>
    public required double PredictedPerSecond { get; init; }

    public required int PromptN { get; init; }
    public required int CacheN { get; init; }
    public required int PredictedN { get; init; }

    /// <summary>(PromptN + CacheN + PredictedN) / ctxSize, as a 0-100 percentage -- the
    /// current sequence length occupying this request's KV-cache slot, divided by the
    /// recipe's context size. Confirmed against llama.cpp's own bundled web UI source,
    /// which computes its live "KV cache" panel the same way (prompt_n + cache_n +
    /// predicted_n). Clamped to [0, 100]: ctxSize is the recipe's own --ctx-size, so the
    /// numerator should never exceed it in practice, but a stale/mismatched recipe value
    /// shouldn't be able to render a chart past its own axis.</summary>
    public double ToKvCachePercent(int ctxSize)
    {
        if (ctxSize <= 0)
            return 0.0;
        var used = PromptN + CacheN + PredictedN;
        return Math.Clamp(used * 100.0 / ctxSize, 0.0, 100.0);
    }
}
