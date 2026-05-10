// unset

using System;
using System.Runtime.CompilerServices;

namespace Typhon.Engine.Internals;

/// <summary>
/// Equi-width histogram for selectivity estimation. Each histogram has <see cref="BucketCount"/> buckets spanning [<see cref="MinValue"/>,
/// <see cref="MaxValue"/>]. Bucket lookup is O(1).
/// </summary>
/// <remarks>
/// Memory footprint: ~1.6 KB per indexed field (100 × 4-byte ints + metadata).
/// Built during <see cref="IndexStatistics.RebuildHistogram"/> by scanning all leaf entries.
/// </remarks>
internal class Histogram
{
    internal const int BucketCount = 100;

    public long MinValue { get; }
    public long MaxValue { get; }
    public int TotalCount { get; }
    internal int[] BucketCounts { get; }

    /// <summary>Width of each bucket. Zero when all keys have the same value (degenerate case).</summary>
    internal long BucketWidth { get; }

    public Histogram(long min, long max, int[] bucketCounts, int totalCount)
    {
        MinValue = min;
        MaxValue = max;
        BucketCounts = bucketCounts;
        TotalCount = totalCount;
        // Unsigned subtraction handles ranges spanning the signed long boundary
        // (e.g., order-preserving float/double encoding). Result always fits in long
        // because max unsigned range / BucketCount ≤ 2^64 / 100 ≈ 1.8e17 < long.MaxValue.
        BucketWidth = (max == min) ? 0 : Math.Max(1L, (long)(((ulong)max - (ulong)min) / BucketCount));
    }

    /// <summary>Returns the 0-based bucket index for <paramref name="value"/>, clamped to [0, BucketCount-1].</summary>
    public int GetBucket(long value)
    {
        if (BucketWidth == 0)
        {
            return 0;
        }

        // Bounds check using signed comparison (correct for OP-encoded values).
        // Strict inequality: values AT the boundary are computed normally.
        if (value < MinValue)
        {
            return 0;
        }
        if (value > MaxValue)
        {
            return BucketCount - 1;
        }

        // Unsigned subtraction: correctly computes distance even when min is negative
        // and value is positive (signed difference would overflow).
        var bucket = (long)(((ulong)value - (ulong)MinValue) / (ulong)BucketWidth);
        return (int)Math.Min(bucket, BucketCount - 1);
    }

    /// <summary>
    /// Estimates the number of entries in [<paramref name="lo"/>, <paramref name="hi"/>] using linear interpolation
    /// for boundary buckets and full counts for interior buckets.
    /// </summary>
    public long EstimateRange(long lo, long hi)
    {
        if (TotalCount == 0 || lo > hi)
        {
            return 0;
        }

        // Degenerate: all keys have the same value
        if (BucketWidth == 0)
        {
            return (lo <= MinValue && MinValue <= hi) ? TotalCount : 0;
        }

        var loBucket = GetBucket(lo);
        var hiBucket = GetBucket(hi);

        if (loBucket == hiBucket)
        {
            // Both endpoints fall in the same bucket — interpolate the fraction
            var bucketStart = BucketStartValue(loBucket);
            var bucketEnd = BucketStartValue(loBucket + 1);
            var rangeInBucket = SignedDist(SignedMax(lo, bucketStart), SignedMin(hi, bucketEnd));
            return Math.Max(1, BucketCounts[loBucket] * rangeInBucket / BucketWidth);
        }

        long estimate = 0;

        // Partial low bucket
        {
            var bucketStart = BucketStartValue(loBucket);
            var bucketEnd = BucketStartValue(loBucket + 1);
            var overlap = SignedDist(SignedMax(lo, bucketStart), bucketEnd);
            estimate += BucketCounts[loBucket] * overlap / BucketWidth;
        }

        // Full interior buckets
        for (var i = loBucket + 1; i < hiBucket; i++)
        {
            estimate += BucketCounts[i];
        }

        // Partial high bucket
        {
            var bucketStart = BucketStartValue(hiBucket);
            var bucketEnd = BucketStartValue(hiBucket + 1);
            var overlap = SignedDist(bucketStart, SignedMin(hi, bucketEnd));
            estimate += BucketCounts[hiBucket] * overlap / BucketWidth;
        }

        return Math.Max(estimate, 0);
    }

    /// <summary>
    /// Computes the start value of bucket <paramref name="bucket"/> using unsigned arithmetic.
    /// Handles ranges that span the signed long boundary without overflow.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private long BucketStartValue(int bucket) => (long)((ulong)MinValue + (ulong)bucket * (ulong)BucketWidth);

    /// <summary>
    /// Computes the unsigned distance between two values (b - a), cast to signed long.
    /// Safe for within-bucket distances (always ≤ BucketWidth which fits in long).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static long SignedDist(long a, long b) => (long)((ulong)b - (ulong)a);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static long SignedMin(long a, long b) => a < b ? a : b;
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static long SignedMax(long a, long b) => a > b ? a : b;

    /// <summary>
    /// Estimates the number of entries equal to <paramref name="value"/>. Returns the average count per
    /// distinct key in the bucket (BucketCount[i] / BucketWidth), floored to 1 for non-empty buckets.
    /// </summary>
    public long EstimateEquality(long value)
    {
        if (TotalCount == 0)
        {
            return 0;
        }

        // Degenerate: all keys are the same value
        if (BucketWidth == 0)
        {
            return (value == MinValue) ? TotalCount : 0;
        }

        var bucket = GetBucket(value);
        var count = BucketCounts[bucket];
        if (count == 0)
        {
            return 0;
        }

        var estimate = count / BucketWidth;
        return Math.Max(1, estimate);
    }
}
