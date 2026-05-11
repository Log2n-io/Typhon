using Typhon.Workbench.Dtos.Profiler;

namespace Typhon.Workbench.Services;

/// <summary>
/// Per-definition rolling statistics accumulator used by <see cref="QueryCatalogBuilder"/>.
/// Holds wall-time samples in an unbounded list so we can compute p50/p95/p99 via sort at finalize.
/// For the P4 scale target (≤10k executions per definition), allocating an
/// <see cref="List{T}"/> of samples is cheap; if profiles grow beyond that we can switch to a
/// reservoir-sampled approximation.
/// </summary>
internal sealed class StatsAccumulator
{
    private readonly List<long> _wallNsSamples = new();
    private long _totalWallNs;

    public void Record(long wallNs)
    {
        _wallNsSamples.Add(wallNs);
        _totalWallNs += wallNs;
    }

    public QueryAggregateStatsDto Finalize(long totalRowsScanned, long totalRowsReturned)
    {
        var n = _wallNsSamples.Count;
        if (n == 0)
        {
            return new QueryAggregateStatsDto(0, 0, 0, 0, 0, 0, totalRowsScanned, totalRowsReturned, 0.0);
        }

        // Sort once and read percentile indices. For per-definition sample sizes (typically <10k),
        // an inline sort is O(n log n) and well under the 100 ms budget.
        var sorted = _wallNsSamples.ToArray();
        Array.Sort(sorted);

        var p50 = sorted[(int)((n - 1) * 0.50)];
        var p95 = sorted[(int)((n - 1) * 0.95)];
        var p99 = sorted[(int)((n - 1) * 0.99)];
        var avg = _totalWallNs / n;

        var selectivity = totalRowsScanned > 0
            ? 1.0 - ((double)totalRowsReturned / totalRowsScanned)
            : 0.0;

        return new QueryAggregateStatsDto(
            ExecutionCount: n,
            TotalWallNs: _totalWallNs,
            AvgWallNs: avg,
            P50WallNs: p50,
            P95WallNs: p95,
            P99WallNs: p99,
            TotalRowsScanned: totalRowsScanned,
            TotalRowsReturned: totalRowsReturned,
            AvgSelectivity: selectivity);
    }
}
