// unset

using System;
using System.Runtime.CompilerServices;

namespace Typhon.Engine.Internals;

/// <summary>
/// Selectivity estimator using uniform distribution assumption for range predicates and exact B+Tree point lookups for equality predicates.
/// </summary>
internal class BasicSelectivityEstimator : ISelectivityEstimator
{
    public static readonly BasicSelectivityEstimator Instance = new();

    private BasicSelectivityEstimator() { }

    public long EstimateCardinality(ComponentTable table, int fieldIndex, CompareOp op, long threshold)
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

        // For range estimation we need the total entity count, not just distinct keys.
        // For unique indexes EntryCount == entity count. For AllowMultiple, use the histogram's
        // TotalCount if available (accurate from last rebuild), otherwise fall back to EntryCount
        // (distinct keys — underestimates, but better than nothing).
        var histogram = stats.Histogram;
        int total = (index.AllowMultiple && histogram != null) ? histogram.TotalCount : entryCount;

        var keyType = stats.KeyType;

        switch (op)
        {
            case CompareOp.Equal:               return ExactEqualityCountDispatch(stats.Index, threshold);
            case CompareOp.NotEqual:            return Math.Max(0, total - ExactEqualityCountDispatch(stats.Index, threshold));
            case CompareOp.GreaterThan:         return EstimateUniformRange(total, min, max, threshold + 1, max, keyType);
            case CompareOp.GreaterThanOrEqual:  return EstimateUniformRange(total, min, max, threshold, max, keyType);
            case CompareOp.LessThan:            return EstimateUniformRange(total, min, max, min, threshold - 1, keyType);
            case CompareOp.LessThanOrEqual:     return EstimateUniformRange(total, min, max, min, threshold, keyType);
            default:                            throw new ArgumentOutOfRangeException(nameof(op), op, null);
        }
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
            // stackalloc 8 bytes — on little-endian x64 the lower bytes contain the correct
            // key value for all key sizes (BTree reads only sizeof(TKey) bytes)
            var buf = stackalloc byte[8];
            *(long*)buf = key;

            if (!index.AllowMultiple)
            {
                var result = index.TryGet(buf, ref accessor);
                return result.IsSuccess ? 1 : 0;
            }

            // Multi-value index: TryGetMultiple returns all values for the key
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
    /// Degenerate case (min == max): returns total if the single value is in [lo, hi], else 0.
    /// </summary>
    /// <remarks>
    /// For <see cref="KeyType.Float"/> and <see cref="KeyType.Double"/>, the long-encoded values are IEEE 754 bit patterns
    /// (via <see cref="Unsafe.As{TFrom, TTo}"/>). Bit-pattern distances are NOT proportional to numeric distances (floats are exponentially sparser at
    /// larger magnitudes), so we decode back to <see langword="double"/> before range arithmetic.
    /// </remarks>
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

        // Clamp to actual range
        lo = Math.Max(lo, min);
        hi = Math.Min(hi, max);

        long fullRange = max - min;
        long queryRange = hi - lo;

        // Use double to avoid long overflow on large ranges
        return Math.Max(0L, (long)((double)total * queryRange / fullRange));
    }

    /// <summary>
    /// Float/double specialization of <see cref="EstimateUniformRange"/>: decodes IEEE 754 bit patterns back to <see langword="double"/> before computing
    /// range proportions.
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

        // Clamp to actual range
        dLo = Math.Max(dLo, dMin);
        dHi = Math.Min(dHi, dMax);

        var fullRange = dMax - dMin;
        var queryRange = dHi - dLo;

        return Math.Max(0L, (long)(total * queryRange / fullRange));
    }
}
