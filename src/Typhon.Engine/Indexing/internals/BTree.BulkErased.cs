using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Typhon.Engine.Internals;

/// <summary>
/// The key-erased half of the bulk value update: what the tick fence's IndexMassUpdate phase calls, and the single place the raw staging bytes become typed
/// entries (#872 step 6).
/// </summary>
internal abstract partial class BTree<TKey, TStore>
{
    public override int BulkEntryStride(bool multi) => multi ? Unsafe.SizeOf<BTreeMultiValueUpdate<TKey>>() : Unsafe.SizeOf<BTreeValueUpdate<TKey>>();

    public override unsafe void WriteBulkEntry(Span<byte> dest, void* keyAddr, int newValue)
    {
        AssertWholeEntries(dest.Length, false, nameof(dest));
        MemoryMarshal.Cast<byte, BTreeValueUpdate<TKey>>(dest)[0] = new BTreeValueUpdate<TKey>(Unsafe.Read<TKey>(keyAddr), newValue);
    }

    public override unsafe void WriteBulkMultiEntry(Span<byte> dest, void* keyAddr, int elementId, int oldValue, int newValue)
    {
        AssertWholeEntries(dest.Length, true, nameof(dest));
        MemoryMarshal.Cast<byte, BTreeMultiValueUpdate<TKey>>(dest)[0]
            = new BTreeMultiValueUpdate<TKey>(Unsafe.Read<TKey>(keyAddr), elementId, oldValue, newValue);
    }

    /// <summary>
    /// Asserts that a staging span holds a whole number of entries at this tree's stride.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b><see cref="MemoryMarshal.Cast{TFrom,TTo}(Span{TFrom})"/> truncates, silently.</b> A span one byte short of a whole entry casts to a span with one
    /// FEWER entry and no error anywhere, so a stride the caller got wrong turns into entries this surface never sees: the sort leaves a trailing entry
    /// unsorted in a buffer it then declares sorted, the partitioning descent asserts on that order and applies to the wrong leaf, and the merge writes fewer
    /// entries than the caller sized its destination for. <see cref="BulkEntryStride"/> exists precisely because producers get the layout wrong — a 4-byte
    /// key packs to 8 with its value while an 8-byte key pads to 16 — and until now the surface consuming that stride never checked the arithmetic came back.
    /// </para>
    /// <para>
    /// <b>Debug-only, unlike <c>ExclusiveWindow</c>, and the difference is the threat model.</b> That guard is always compiled because what it catches is a
    /// foreign thread the engine cannot see coming. Every span reaching here is assembled by engine code in <c>IndexUpdateStaging</c> and
    /// <c>FenceExecSystem</c> from a stride this same tree object handed out, with no user-input path — so a mismatch is a programming error a Debug suite
    /// run catches, not a runtime condition. Checked at all six entry points rather than the two that write, because a half-checked erased surface reads as
    /// if the unchecked half had been considered safe.
    /// </para>
    /// </remarks>
    [Conditional("DEBUG")]
    private void AssertWholeEntries(int byteLength, bool multi, string paramName)
    {
        var stride = BulkEntryStride(multi);
        Debug.Assert(byteLength % stride == 0,
            $"{paramName}: {byteLength} bytes is not a whole number of {(multi ? "multi" : "unique")} entries at stride {stride}. MemoryMarshal.Cast would "
            + $"silently drop the partial tail — see AssertWholeEntries.");
    }

    /// <remarks>
    /// <para>
    /// <b>The sort need not be stable, and the reason is worth stating because the opposite is the usual assumption.</b> Two entries sharing a key name
    /// different elements of that key's buffer and carry their own <c>OldValue</c>, so they update disjoint slots and the order between them cannot change
    /// the result. On a unique index a key appears at most once per batch to begin with. That also means <c>AC-6.4</c>'s determinism survives a change in W
    /// reordering equal keys: the applied set is the same whichever order they arrive in.
    /// </para>
    /// <para>
    /// <b><see cref="RadixSort"/> for every integer and floating-point key, the struct-comparer introsort for the rest.</b> The introsort was already
    /// monomorphised — <see cref="CompareKeys"/> folds to a direct <c>CompareTo</c> — so what the radix sort removes is the <c>log n</c> factor: three
    /// 11-bit passes at most for a 32-bit key, six for a 64-bit one, fewer when the batch's keys share their high digits, each moving the 16-byte entry
    /// once. <see cref="RadixKeyOf"/> is the order-preserving map; <c>String64</c> has none and keeps the comparer. <c>RadixSortBenchmarks</c> carries the
    /// numbers per key width and run size.
    /// </para>
    /// </remarks>
    public override void SortBulkEntries(Span<byte> entries, Span<byte> scratch, Span<int> radixCounts, bool multi)
    {
        AssertWholeEntries(entries.Length, multi, nameof(entries));
        if (multi)
        {
            var span = MemoryMarshal.Cast<byte, BTreeMultiValueUpdate<TKey>>(entries);
            if (RadixSortableKey)
            {
                RadixSort.Sort<BTreeMultiValueUpdate<TKey>, MultiEntryRadixKey>(span, MemoryMarshal.Cast<byte, BTreeMultiValueUpdate<TKey>>(scratch), radixCounts);
            }
            else
            {
                span.Sort(new MultiEntryComparer(Comparer));
            }
        }
        else
        {
            var span = MemoryMarshal.Cast<byte, BTreeValueUpdate<TKey>>(entries);
            if (RadixSortableKey)
            {
                RadixSort.Sort<BTreeValueUpdate<TKey>, UniqueEntryRadixKey>(span, MemoryMarshal.Cast<byte, BTreeValueUpdate<TKey>>(scratch), radixCounts);
            }
            else
            {
                span.Sort(new UniqueEntryComparer(Comparer));
            }
        }
    }

    /// <summary>Whether <typeparamref name="TKey"/> has a radix order: every integer width and both floating-point types. <c>String64</c> does not.</summary>
    /// <remarks>
    /// <c>typeof(TKey)</c> against a concrete type is a JIT intrinsic for value types, so this folds to a constant per instantiation, exactly as
    /// <see cref="CompareKeys"/> does.
    /// </remarks>
    private static bool RadixSortableKey
        => typeof(TKey) == typeof(int) || typeof(TKey) == typeof(long) || typeof(TKey) == typeof(short) || typeof(TKey) == typeof(sbyte)
            || typeof(TKey) == typeof(uint) || typeof(TKey) == typeof(ulong) || typeof(TKey) == typeof(ushort) || typeof(TKey) == typeof(byte)
            || typeof(TKey) == typeof(float) || typeof(TKey) == typeof(double);

    /// <summary>
    /// The unsigned 64-bit radix key with <typeparamref name="TKey"/>'s own order — the order <see cref="CompareKeys"/> defines, since the partitioning
    /// descent asserts the batch against THAT. Sign-flipped for the signed integers, widened for the unsigned ones, and the zone maps' ordered encodings for
    /// float and double, with one amendment: <c>CompareTo</c> puts NaN below every other value (and all NaNs equal), where the zone-map encoding scatters
    /// NaN bit patterns above +∞ and below -∞, so NaN is pinned to the lowest key here. -0.0 and +0.0 compare equal under both.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static ulong RadixKeyOf(TKey key)
    {
        if (typeof(TKey) == typeof(int))
        {
            return RadixSort.SignedKey(Unsafe.As<TKey, int>(ref key));
        }

        if (typeof(TKey) == typeof(long))
        {
            return RadixSort.SignedKey(Unsafe.As<TKey, long>(ref key));
        }

        if (typeof(TKey) == typeof(short))
        {
            return RadixSort.SignedKey(Unsafe.As<TKey, short>(ref key));
        }

        if (typeof(TKey) == typeof(sbyte))
        {
            return RadixSort.SignedKey(Unsafe.As<TKey, sbyte>(ref key));
        }

        if (typeof(TKey) == typeof(uint))
        {
            return Unsafe.As<TKey, uint>(ref key);
        }

        if (typeof(TKey) == typeof(ulong))
        {
            return Unsafe.As<TKey, ulong>(ref key);
        }

        if (typeof(TKey) == typeof(ushort))
        {
            return Unsafe.As<TKey, ushort>(ref key);
        }

        if (typeof(TKey) == typeof(byte))
        {
            return Unsafe.As<TKey, byte>(ref key);
        }

        if (typeof(TKey) == typeof(float))
        {
            var f = Unsafe.As<TKey, float>(ref key);
            // No non-NaN value encodes to 0: the lowest, -∞, encodes to 0x007F_FFFF, and every pattern below that is a NaN.
            return float.IsNaN(f) ? 0UL : (ulong)ZoneMapArray.FloatToOrderedLong(f);
        }

        if (typeof(TKey) == typeof(double))
        {
            var d = Unsafe.As<TKey, double>(ref key);
            // long.MinValue is unreachable for a non-NaN double (-∞ encodes to long.MinValue + 0x0010_0000_0000_0000), so it is NaN's alone.
            return double.IsNaN(d) ? 0UL : RadixSort.SignedKey(ZoneMapArray.DoubleToOrderedLong(d));
        }

        return 0;
    }

    private readonly struct UniqueEntryRadixKey : IRadixKey<BTreeValueUpdate<TKey>>
    {
        public static ulong Key(in BTreeValueUpdate<TKey> entry) => RadixKeyOf(entry.Key);
    }

    private readonly struct MultiEntryRadixKey : IRadixKey<BTreeMultiValueUpdate<TKey>>
    {
        public static ulong Key(in BTreeMultiValueUpdate<TKey> entry) => RadixKeyOf(entry.Key);
    }

    public override void MergeBulkRuns(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b, Span<byte> dest, bool multi)
    {
        AssertWholeEntries(a.Length, multi, nameof(a));
        AssertWholeEntries(b.Length, multi, nameof(b));

        // The destination must hold exactly the two runs. Sized by the caller from lengths it tracks itself, so a truncating cast here would not merely drop
        // a tail — it would leave the caller believing it had gathered entries that are no longer anywhere.
        Debug.Assert(dest.Length == a.Length + b.Length,
            $"MergeBulkRuns destination is {dest.Length} bytes for runs of {a.Length} + {b.Length}.");

        if (multi)
        {
            MergeRuns<BTreeMultiValueUpdate<TKey>, MultiApplier>(a, b, dest);
        }
        else
        {
            MergeRuns<BTreeValueUpdate<TKey>, UniqueApplier>(a, b, dest);
        }
    }

    /// <remarks>
    /// <b>Stable, and deliberately so even though the apply does not need it.</b> Taking from the left run on a tie keeps entries that share a key in the
    /// order the producer emitted them, which makes the merged batch a pure function of the per-chunk runs and their order — so the same migration set
    /// produces the same bytes whatever W was, which is what <c>AC-6.4</c> asks for. An unstable tie-break would still be correct (entries sharing a key
    /// name different elements) but would make the batch, and therefore the leaf-snapped partition computed from it, depend on the chunk count.
    /// </remarks>
    private void MergeRuns<TEntry, TApplier>(ReadOnlySpan<byte> aBytes, ReadOnlySpan<byte> bBytes, Span<byte> destBytes)
        where TEntry : struct
        where TApplier : struct, ILeafApplier<TEntry>
    {
        var a = MemoryMarshal.Cast<byte, TEntry>(aBytes);
        var b = MemoryMarshal.Cast<byte, TEntry>(bBytes);
        var dest = MemoryMarshal.Cast<byte, TEntry>(destBytes);
        TApplier applier = default;

        var i = 0;
        var j = 0;
        var k = 0;
        while (i < a.Length && j < b.Length)
        {
            if (CompareKeys(applier.KeyOf(a[i]), applier.KeyOf(b[j]), Comparer) <= 0)
            {
                dest[k++] = a[i++];
            }
            else
            {
                dest[k++] = b[j++];
            }
        }

        while (i < a.Length)
        {
            dest[k++] = a[i++];
        }

        while (j < b.Length)
        {
            dest[k++] = b[j++];
        }
    }

    public override int PartitionBulkEntries(ReadOnlySpan<byte> entries, bool multi, int desiredParts, Span<int> boundaries,
        ref ChunkAccessor<TStore> accessor)
    {
        AssertWholeEntries(entries.Length, multi, nameof(entries));
        return multi
            ? PartitionByLeafBoundaries(MemoryMarshal.Cast<byte, BTreeMultiValueUpdate<TKey>>(entries), desiredParts, boundaries, ref accessor)
            : PartitionByLeafBoundaries(MemoryMarshal.Cast<byte, BTreeValueUpdate<TKey>>(entries), desiredParts, boundaries, ref accessor);
    }

    public override int ApplyBulkEntries(ReadOnlySpan<byte> entries, bool multi, ref ChunkAccessor<TStore> accessor, out BulkUpdateStats stats)
    {
        AssertWholeEntries(entries.Length, multi, nameof(entries));
        return multi
            ? UpdateValues(MemoryMarshal.Cast<byte, BTreeMultiValueUpdate<TKey>>(entries), ref accessor, out stats)
            : UpdateValues(MemoryMarshal.Cast<byte, BTreeValueUpdate<TKey>>(entries), ref accessor, out stats);
    }

    /// <remarks>
    /// A struct comparer rather than a <see cref="Comparison{T}"/>: the JIT monomorphises <c>Span.Sort</c> on it, so the key comparison inlines and nothing
    /// allocates. A lambda would allocate a delegate per call and dispatch it once per comparison, on a path that runs once per indexed field per tick.
    /// </remarks>
    private readonly struct UniqueEntryComparer : IComparer<BTreeValueUpdate<TKey>>
    {
        private readonly IComparer<TKey> _comparer;

        public UniqueEntryComparer(IComparer<TKey> comparer) => _comparer = comparer;

        public int Compare(BTreeValueUpdate<TKey> x, BTreeValueUpdate<TKey> y) => CompareKeys(x.Key, y.Key, _comparer);
    }

    /// <inheritdoc cref="UniqueEntryComparer"/>
    private readonly struct MultiEntryComparer : IComparer<BTreeMultiValueUpdate<TKey>>
    {
        private readonly IComparer<TKey> _comparer;

        public MultiEntryComparer(IComparer<TKey> comparer) => _comparer = comparer;

        public int Compare(BTreeMultiValueUpdate<TKey> x, BTreeMultiValueUpdate<TKey> y) => CompareKeys(x.Key, y.Key, _comparer);
    }
}
