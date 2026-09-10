using Dashboard.Core.Abstractions;
using Dashboard.Core.Models;

namespace Dashboard.Infrastructure.Scrapers;

/// <summary>
/// Computes rates from counter deltas (reset-safe) and histogram averages from
/// sum/count deltas. The first sample after process start yields null rates
/// (baseline only) until a second sample arrives.
/// </summary>
public sealed class MetricsAggregator : IMetricsAggregator
{
    public MetricsSample Aggregate(MetricsSnapshot? prevRaw, MetricsSnapshot raw, DateTime now)
    {
        if (!raw.Up)
        {
            return new MetricsSample
            {
                Ts = now,
                SourceId = "",
                SourceLabel = "",
                ModelName = raw.ModelName,
                Up = false,
                Error = raw.Error,
            };
        }

        double? Dt = null;
        if (prevRaw is not null && prevRaw.Up)
            Dt = (now - prevRaw.Ts).TotalSeconds;
        bool validDt = Dt is > 0.001;

        return new MetricsSample
        {
            Ts = now,
            SourceId = "",
            SourceLabel = "",
            ModelName = raw.ModelName,
            Running = (int)System.Math.Round(raw.Running ?? 0),
            Waiting = (int)System.Math.Round(raw.Waiting ?? 0),
            KvPct = raw.KvPct,
            GenTps = Rate(prevRaw?.GenTokensTotal, raw.GenTokensTotal, Dt, validDt),
            PromptTps = Rate(prevRaw?.PromptTokensTotal, raw.PromptTokensTotal, Dt, validDt),
            ReqTps = Rate(prevRaw?.ReqCountTotal, raw.ReqCountTotal, Dt, validDt),
            TtftAvgSec = HistAvg(prevRaw?.TtftSum, prevRaw?.TtftCount, raw.TtftSum, raw.TtftCount, Dt, validDt),
            ItlAvgSec = HistAvg(prevRaw?.ItlSum, prevRaw?.ItlCount, raw.ItlSum, raw.ItlCount, Dt, validDt),
            E2eAvgSec = HistAvg(prevRaw?.E2eSum, prevRaw?.E2eCount, raw.E2eSum, raw.E2eCount, Dt, validDt),
            QueueAvgSec = HistAvg(prevRaw?.QueueSum, prevRaw?.QueueCount, raw.QueueSum, raw.QueueCount, Dt, validDt),
            PrefixHitRatio = Ratio(prevRaw?.PrefixHitsTotal, prevRaw?.PrefixQueriesTotal, raw.PrefixHitsTotal, raw.PrefixQueriesTotal, Dt, validDt),
            PreemptionsTotal = raw.PreemptionsTotal is null ? null : (long)raw.PreemptionsTotal.Value,
            Up = true,
        };
    }

    private static double? Rate(double? prev, double? cur, double? dt, bool validDt)
    {
        if (cur is null || prev is null)
            return null;
        if (cur < prev)
            return null; // counter reset -> no rate this interval
        if (!validDt)
            return null;
        return (cur - prev) / dt!.Value;
    }

    // Histogram average over the interval: (sumDelta)/(countDelta); if counts equal,
    // fall back to cumulative average.
    private static double? HistAvg(double? prevSum, double? prevCount, double? curSum, double? curCount, double? dt, bool validDt)
    {
        if (curSum is null)
            return null;
        double sum = curSum.Value;
        double count = curCount ?? 0;

        if (prevSum is null || prevCount is null)
        {
            // First sample: cumulative average (approx).
            return count > 0 ? sum / count : (count == 0 ? 0.0 : null);
        }

        if (sum < prevSum || count < prevCount)
            return null; // reset

        double sumDelta = sum - prevSum!.Value;
        double countDelta = count - prevCount!.Value;
        if (countDelta > 0)
            return sumDelta / countDelta;
        if (countDelta == 0 && count > 0)
            return sum / count; // no new samples; use cumulative average
        return null;
    }

    private static double? Ratio(double? prevHits, double? prevQueries, double? curHits, double? curQueries, double? dt, bool validDt)
    {
        if (curHits is null || curQueries is null)
            return null;
        double hits = curHits.Value;
        double queries = curQueries.Value;

        if (prevHits is null || prevQueries is null)
            return queries > 0 ? hits / queries : null;

        if (hits < prevHits || queries < prevQueries)
            return null; // reset

        double hDelta = hits - prevHits!.Value;
        double qDelta = queries - prevQueries!.Value;
        if (qDelta > 0)
            return hDelta / qDelta;
        if (qDelta == 0 && queries > 0)
            return hits / queries;
        return null;
    }
}
