// unset

using System;
using System.Runtime.CompilerServices;

namespace Typhon.Engine.Internals;

/// <summary>
/// Selectivity estimator that leverages MCV, Histogram, and B+Tree statistics in a priority chain:
/// <list type="bullet">
///   <item><b>Equality (==, !=):</b> MCV exact lookup → B+Tree point seek fallback</item>
///   <item><b>Range (&gt;, &gt;=, &lt;, &lt;=):</b> Histogram range estimation → uniform distribution fallback</item>
/// </list>
/// Replaces <see cref="BasicSelectivityEstimator"/> and the former HistogramSelectivityEstimator (removed).
/// Gracefully degrades to B+Tree/uniform when no statistics are available.
/// </summary>
internal sealed class AdvancedSelectivityEstimator : ISelectivityEstimator
{
    public static readonly AdvancedSelectivityEstimator Instance = new();

    private AdvancedSelectivityEstimator() { }

    public long EstimateCardinality(ComponentTable table, int fieldIndex, CompareOp op, long threshold)
    {
        // Phase 7: Query:Estimate span — covers selectivity estimation for one predicate.
        var estimateScope = TyphonEvent.BeginQueryEstimate((ushort)Math.Min(fieldIndex, ushort.MaxValue), 0);
        try
        {
            var result = EstimateCardinalityCore(table, fieldIndex, op, threshold);
            estimateScope.Cardinality = result;
            return result;
        }
        finally
        {
            estimateScope.Dispose();
        }
    }

    private static long EstimateCardinalityCore(ComponentTable table, int fieldIndex, CompareOp op, long threshold)
    {
        var stats = table.IndexStats[fieldIndex];
        var entryCount = stats.EntryCount;
        if (entryCount == 0)
        {
            return 0;
        }

        var index = stats.Index;
        long min = stats.MinValue;
        long max = stats.MaxValue;

        // Total entity count: for AllowMultiple indexes, prefer histogram's TotalCount (entities, not keys)
        var histogram = stats.Histogram;
        int total = (index.AllowMultiple && histogram != null) ? histogram.TotalCount : entryCount;

        var mcv = stats.MostCommonValues;
        var keyType = stats.KeyType;

        switch (op)
        {
            case CompareOp.Equal:
                return EstimateEquality(stats, mcv, threshold);

            case CompareOp.NotEqual:
                return Math.Max(0, total - EstimateEquality(stats, mcv, threshold));

            case CompareOp.GreaterThan:
                // Float/double: skip ±1 adjustment (bit-level ±1 can cross NaN boundaries).
                // The approximation error is at most 1 entity — negligible for selectivity estimation.
                if (keyType is KeyType.Float or KeyType.Double)
                {
                    return EstimateRange(histogram, total, min, max, threshold, max, keyType);
                }
                return threshold == long.MaxValue ? 0 : EstimateRange(histogram, total, min, max, threshold + 1, max, keyType);

            case CompareOp.GreaterThanOrEqual:
                return EstimateRange(histogram, total, min, max, threshold, max, keyType);

            case CompareOp.LessThan:
                if (keyType is KeyType.Float or KeyType.Double)
                {
                    return EstimateRange(histogram, total, min, max, min, threshold, keyType);
                }
                return threshold == long.MinValue ? 0 : EstimateRange(histogram, total, min, max, min, threshold - 1, keyType);

            case CompareOp.LessThanOrEqual:
                return EstimateRange(histogram, total, min, max, min, threshold, keyType);

            default:
                throw new ArgumentOutOfRangeException(nameof(op), op, null);
        }
    }

    /// <summary>
    /// Equality estimation chain: MCV exact lookup → B+Tree point seek.
    /// MCV provides O(log K) lookup with exact frequency; B+Tree seek is O(log N) fallback.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static long EstimateEquality(IndexStatistics stats, MostCommonValues mcv, long threshold)
    {
        // Priority 1: MCV exact frequency
        if (mcv != null && mcv.TryGetCount(threshold, out long mcvCount))
        {
            return mcvCount;
        }

        // Priority 2: B+Tree point seek
        return ExactEqualityCountDispatch(stats.Index, threshold);
    }

    /// <summary>
    /// Range estimation chain: Histogram → uniform distribution.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static long EstimateRange(Histogram histogram, int total, long min, long max, long lo, long hi, KeyType keyType)
    {
        // Priority 1: Histogram (float/double histograms use order-preserving encoding internally)
        if (histogram != null)
        {
            if (keyType is KeyType.Float or KeyType.Double)
            {
                lo = StatisticsRebuilder.ToOrderPreserving(lo, keyType);
                hi = StatisticsRebuilder.ToOrderPreserving(hi, keyType);
            }

            return histogram.EstimateRange(lo, hi);
        }

        // Priority 2: Uniform distribution
        return EstimateUniformRange(total, min, max, lo, hi, keyType);
    }

    /// <summary>
    /// Returns the exact count of entries matching <paramref name="key"/> via B+Tree point lookup.
    /// For unique indexes: 0 or 1. For multi-value indexes: the buffer's TotalCount.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static long ExactEqualityCountDispatch(IBTreeIndex index, long key)
    {
        if (index is BTreeBase<TransientStore> transientIndex)
        {
            return ExactEqualityCount(transientIndex, key);
        }
        return ExactEqualityCount((BTreeBase<PersistentStore>)index, key);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static unsafe long ExactEqualityCount<TStore>(BTreeBase<TStore> index, long key) where TStore : struct, IPageStore
    {
        using var guard = EpochGuard.Enter(index.Segment.Store.EpochManager);
        var accessor = index.Segment.CreateChunkAccessor();
        try
        {
            var buf = stackalloc byte[8];
            *(long*)buf = key;

            if (!index.AllowMultiple)
            {
                var result = index.TryGet(buf, ref accessor);
                return result.IsSuccess ? 1 : 0;
            }

            var multiResult = index.TryGetMultiple(buf, ref accessor);
            if (!multiResult.IsValid)
            {
                return 0;
            }

            var count = multiResult.TotalCount;
            multiResult.Dispose();
            return count;
        }
        finally
        {
            accessor.Dispose();
        }
    }

    /// <summary>
    /// Estimates cardinality assuming uniform distribution in [min, max].
    /// Decodes float/double bit patterns before range arithmetic.
    /// </summary>
    private static long EstimateUniformRange(int total, long min, long max, long lo, long hi, KeyType keyType)
    {
        if (keyType is KeyType.Float or KeyType.Double)
        {
            return EstimateUniformRangeFloat(total, min, max, lo, hi, keyType);
        }

        if (lo > hi || lo > max || hi < min)
        {
            return 0;
        }

        if (min == max)
        {
            return (lo <= min && min <= hi) ? total : 0;
        }

        lo = Math.Max(lo, min);
        hi = Math.Min(hi, max);

        long fullRange = max - min;
        long queryRange = hi - lo;

        return Math.Max(0L, (long)((double)total * queryRange / fullRange));
    }

    /// <summary>
    /// Float/double specialization: decodes IEEE 754 bit patterns back to double before computing range proportions.
    /// </summary>
    private static long EstimateUniformRangeFloat(int total, long minBits, long maxBits, long loBits, long hiBits, KeyType keyType)
    {
        double dMin, dMax, dLo, dHi;

        if (keyType == KeyType.Float)
        {
            var minI = (int)minBits;
            var maxI = (int)maxBits;
            var loI = (int)loBits;
            var hiI = (int)hiBits;
            dMin = Unsafe.As<int, float>(ref minI);
            dMax = Unsafe.As<int, float>(ref maxI);
            dLo = Unsafe.As<int, float>(ref loI);
            dHi = Unsafe.As<int, float>(ref hiI);
        }
        else
        {
            dMin = Unsafe.As<long, double>(ref minBits);
            dMax = Unsafe.As<long, double>(ref maxBits);
            dLo = Unsafe.As<long, double>(ref loBits);
            dHi = Unsafe.As<long, double>(ref hiBits);
        }

        if (dLo > dHi || dLo > dMax || dHi < dMin)
        {
            return 0;
        }

        if (dMin == dMax)
        {
            return (dLo <= dMin && dMin <= dHi) ? total : 0;
        }

        dLo = Math.Max(dLo, dMin);
        dHi = Math.Min(dHi, dMax);

        var fullRange = dMax - dMin;
        var queryRange = dHi - dLo;

        return Math.Max(0L, (long)(total * queryRange / fullRange));
    }
}
