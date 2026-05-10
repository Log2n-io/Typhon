// unset

using System;
using System.Collections.Generic;

namespace Typhon.Engine.Internals;

/// <summary>
/// Top-K most common values for a single indexed field. Immutable after construction — safe for concurrent reads.
/// </summary>
/// <remarks>
/// <para>
/// Memory: ~1.8 KB per instance (100 entries × 16 bytes + header).
/// Entries are sorted by Value for O(log K) binary search via <see cref="TryGetCount"/>.
/// </para>
/// <para>
/// Built by <see cref="StatisticsRebuilder"/> from a frequency dictionary. When sampling is active, counts are scaled by the sampling ratio so they
/// approximate the full-table counts.
/// </para>
/// </remarks>
internal sealed class MostCommonValues
{
    private const int DefaultCapacity = 100;

    private readonly (long Value, long Count)[] _entries;
    private readonly int _count;

    /// <summary>Total entity count for the field (scaled if sampled).</summary>
    public long TotalEntities { get; }

    /// <summary>Entities NOT in the top-K set: TotalEntities minus sum of all top-K counts.</summary>
    public long RemainingEntries { get; }

    private MostCommonValues((long Value, long Count)[] entries, int count, long totalEntities)
    {
        _entries = entries;
        _count = count;
        TotalEntities = totalEntities;

        long mcvSum = 0;
        for (int i = 0; i < count; i++)
        {
            mcvSum += entries[i].Count;
        }
        RemainingEntries = Math.Max(0, totalEntities - mcvSum);
    }

    /// <summary>
    /// Looks up the count for a specific value via O(log K) binary search.
    /// Returns true if the value is in the top-K set.
    /// </summary>
    public bool TryGetCount(long value, out long count)
    {
        int lo = 0, hi = _count - 1;
        while (lo <= hi)
        {
            int mid = lo + ((hi - lo) >> 1);
            long midVal = _entries[mid].Value;
            if (midVal == value)
            {
                count = _entries[mid].Count;
                return true;
            }
            if (midVal < value)
            {
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }

        count = 0;
        return false;
    }

    /// <summary>
    /// Builds an MCV instance from a frequency dictionary. Selects the top-K values by count, applies <paramref name="scaleFactor"/> to each count,
    /// then sorts by Value for binary search.
    /// </summary>
    /// <param name="frequencies">Raw frequency counts per distinct value (from sampling or full scan).</param>
    /// <param name="totalEntities">Total entity count (already scaled if sampling).</param>
    /// <param name="scaleFactor">Scale multiplier for each frequency count (1.0 for full scan).</param>
    /// <param name="capacity">Maximum number of top values to retain.</param>
    public static MostCommonValues Build(Dictionary<long, int> frequencies, long totalEntities, double scaleFactor = 1.0, int capacity = DefaultCapacity)
    {
        if (frequencies.Count == 0)
        {
            return new MostCommonValues([], 0, totalEntities);
        }

        // Copy to array for sorting
        int k = Math.Min(capacity, frequencies.Count);
        var all = new (long Value, int Count)[frequencies.Count];
        int idx = 0;
        foreach (var kvp in frequencies)
        {
            all[idx++] = (kvp.Key, kvp.Value);
        }

        // Sort by count descending, take top K
        Array.Sort(all, (a, b) => b.Count.CompareTo(a.Count));

        var topK = new (long Value, long Count)[k];
        for (int i = 0; i < k; i++)
        {
            topK[i] = (all[i].Value, Math.Max(1, (long)(all[i].Count * scaleFactor)));
        }

        // Sort result by Value for binary search
        Array.Sort(topK, (a, b) => a.Value.CompareTo(b.Value));

        return new MostCommonValues(topK, k, totalEntities);
    }
}
