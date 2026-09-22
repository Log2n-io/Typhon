using System;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace Typhon.Engine.Internals;

/// <summary>
/// Key extraction for <see cref="RadixSort"/>: the unsigned 64-bit key whose ascending order is the order wanted. Implemented by a stateless struct per
/// call site, so the JIT specialises the sort on it and the key read inlines — no delegate, no interface dispatch, which is the whole reason the sort
/// beats <c>Array.Sort</c> through an <c>IComparer</c>.
/// </summary>
/// <remarks>
/// The key is UNSIGNED. A site whose natural key is signed flips its sign bit through <see cref="RadixSort.SignedKey(int)"/> or
/// <see cref="RadixSort.SignedKey(long)"/>; a floating-point site goes through <c>ZoneMapArray.FloatToOrderedLong</c> first. Nothing in the sort
/// interprets the key beyond its bits.
/// </remarks>
internal interface IRadixKey<T> where T : struct
{
    static abstract ulong Key(in T item);
}

/// <summary>
/// Stable LSD radix sort over 11-bit digits, for the fence's hot-path sorts of small structs by an integer field (#889, generalised).
/// </summary>
/// <remarks>
/// <para><b>Why not <c>Array.Sort</c>.</b> Introsort is <c>n log n</c> comparisons, each through the comparer and each moving the whole element; on the
/// migration queue it measured 150–175 ns a request for ~5 000 20-byte requests through an <c>IComparer</c>, 931 µs a tick on the fence's serial path.
/// The keys at every site this serves are cell ids, bucket indices, cluster ids or index keys of which a live world uses a narrow range, so a radix sort
/// is <c>O(n)</c> with a small constant: at most <c>ceil(bits / digit)</c> passes, and a digit that is the same for every key costs no pass at all —
/// low, high or in the middle — so a 32-bit key that spans a few thousand values costs one or two passes rather than three. The same queue measured
/// 63 µs.</para>
/// <para><b>Stable.</b> The scatter writes equal digits in input order, so items sharing a key keep the order they arrived in — a property
/// <c>Array.Sort</c> does not have and that <c>RP-02</c> now relies on. A site that needs a total order over TWO fields sorts by the minor key first and
/// the major key second, both stable, and gets exactly the lexicographic order.</para>
/// <para><b>Caller-owned scratch, no allocation here.</b> <c>scratch</c> is the ping-pong partner and must hold at least <c>items.Length</c> elements;
/// <c>counts</c> is the histogram and must hold <see cref="Buckets"/> ints. Every site has a natural owner for both — the cluster state, the staging
/// buffer, the exec system's per-chunk arrays — and pooling them here would put a lock or a thread-static on a path that is otherwise free of both. The
/// result is always left in <c>items</c>; an odd number of passes ends in the scratch and is copied back.</para>
/// <para><b>Which digits get a pass.</b> One pass over the keys ORs them and ANDs them; <c>or ^ and</c> is exactly the set of bits that vary across the
/// run — a bit is set in it iff some key has it set and some key has it clear. A digit with no varying bit is skipped before its histogram, wherever it
/// sits. The first version derived this from <c>min ^ max</c>, which is NOT exact: the two extremes can agree on their low bits while a key between
/// them differs there (<c>{0, 0x100, 0x101, 0x200}</c> — review caught it, <c>RadixSortTests</c> pins it). Only the extremes' shared HIGH bits are
/// shared by every key between them; their shared low bits prove nothing.</para>
/// <para><b>Adaptive digit width.</b> Every pass pays a fixed cost proportional to the bucket count — the histogram clear and, above all, the prefix sum
/// over every bucket — and a per-item cost proportional to the count. At 2 048 buckets the fixed part is ~1.3 µs a pass, which is more than a 128-item
/// run's whole introsort; at 256 buckets it is ~0.15 µs. So a run below <see cref="WideDigitMinCount"/> items takes 8-bit digits (up to eight passes,
/// cheap ones) and a larger run takes 11-bit digits (up to six). Measured in <c>RadixSortBenchmarks</c>, before and after, per shape and size.</para>
/// </remarks>
internal static class RadixSort
{
    private const int WideDigitBits = 11;
    private const int NarrowDigitBits = 8;

    /// <summary>Histogram length <c>counts</c> must provide: <c>1 &lt;&lt; 11</c>, the wide digit's bucket count.</summary>
    public const int Buckets = 1 << WideDigitBits;

    /// <summary>Runs of at least this many items take 11-bit digits; smaller runs take 8-bit ones. See the remarks on the class.</summary>
    public const int WideDigitMinCount = 1024;

    /// <summary>Maps a signed 32-bit key onto an unsigned key with the same order: the sign bit flipped, so negatives sort first.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong SignedKey(int value) => (uint)value ^ 0x8000_0000u;

    /// <summary>Maps a signed 64-bit key onto an unsigned key with the same order.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong SignedKey(long value) => (ulong)value ^ (1UL << 63);

    /// <summary>
    /// Sorts <paramref name="items"/> in place, ascending by <typeparamref name="TKey"/>'s key, stably.
    /// </summary>
    /// <param name="items">What to sort. Left sorted whichever side of the ping-pong the last pass landed on.</param>
    /// <param name="scratch">Ping-pong partner, at least <c>items.Length</c> long. Its contents on return are unspecified.</param>
    /// <param name="counts">Histogram scratch, at least <see cref="Buckets"/> long. Its contents on return are unspecified.</param>
    public static void Sort<T, TKey>(Span<T> items, Span<T> scratch, Span<int> counts)
        where T : struct
        where TKey : struct, IRadixKey<T>
    {
        var count = items.Length;
        if (count < 2)
        {
            return;
        }

        if (scratch.Length < count)
        {
            ThrowHelper.ThrowArgument($"RadixSort: scratch holds {scratch.Length} items for {count} to sort.");
        }

        if (counts.Length < Buckets)
        {
            ThrowHelper.ThrowArgument($"RadixSort: counts holds {counts.Length} ints; {Buckets} are needed.");
        }

        // One pass over the keys for the exact set of bits that vary across the run (see the remarks): a digit with none of them needs no pass, and a run
        // whose keys are all equal is already sorted — stable means untouched.
        var orAll = 0UL;
        var andAll = ulong.MaxValue;
        for (var i = 0; i < count; i++)
        {
            var k = TKey.Key(in items[i]);
            orAll |= k;
            andAll &= k;
        }

        var differing = orAll ^ andAll;
        if (differing == 0)
        {
            return;
        }

        var digitBits = count >= WideDigitMinCount ? WideDigitBits : NarrowDigitBits;
        var buckets = 1 << digitBits;
        var digitMask = (ulong)(buckets - 1);
        var highestDifferingBit = 64 - BitOperations.LeadingZeroCount(differing);
        var firstShift = BitOperations.TrailingZeroCount(differing) / digitBits * digitBits;
        var histogram = counts.Slice(0, buckets);
        var src = items;
        var dst = scratch.Slice(0, count);
        var passes = 0;
        for (var shift = firstShift; shift < highestDifferingBit; shift += digitBits)
        {
            if (((differing >> shift) & digitMask) == 0)
            {
                continue;   // every key shares this digit: the pass would copy the run unchanged
            }

            histogram.Clear();
            for (var i = 0; i < count; i++)
            {
                histogram[(int)((TKey.Key(in src[i]) >> shift) & digitMask)]++;
            }

            var running = 0;
            for (var b = 0; b < buckets; b++)
            {
                var c = histogram[b];
                histogram[b] = running;
                running += c;
            }

            for (var i = 0; i < count; i++)
            {
                var d = (int)((TKey.Key(in src[i]) >> shift) & digitMask);
                dst[histogram[d]++] = src[i];
            }

            // Tuple swap is unavailable for ref structs (CS9244); three assignments it is.
            var swap = src;
            src = dst;
            dst = swap;
            passes++;
        }

        if ((passes & 1) != 0)
        {
            src.CopyTo(items);   // an odd number of passes left the result in the scratch
        }
    }
}
