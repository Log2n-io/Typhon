using System;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;

namespace Typhon.Engine.Internals;

/// <summary>
/// Per-archetype per-indexed-field zone map for cluster-level query pruning.
/// Maintains min/max bounds per cluster; allows queries to skip clusters entirely when the query range doesn't overlap the cluster's [min, max] interval.
/// </summary>
/// <remarks>
/// <para>Zone maps are NOT persisted — rebuilt from cluster data on reopen/recovery.</para>
/// <para>Maintenance: lazy full recompute at tick fence for dirty clusters; eager widen on spawn.</para>
/// <para>Staleness: between tick fences, bounds may be wider than actual data (destroyed boundary entity lingers).
/// False positives acceptable (cluster checked but no match).</para>
/// <para>
/// <b>False negatives</b> — a cluster pruned out of a query it should have matched — are what this type must not produce, because they lose rows silently.
/// Growth can no longer cause one: the arrays and their capacity are replaced as a single <see cref="Store"/> under a latch that element writers hold shared,
/// so no write can land in a generation that is being copied away (review M5). <b>One window remains, and it is not growth:</b> two concurrent
/// <see cref="Widen"/> calls on the SAME cluster are a plain read-compare-write, so the wider of the two bounds can be lost. That one is bounded by the
/// tick-fence recompute, which re-derives the cluster's true min/max within the tick.
/// </para>
/// </remarks>
internal sealed unsafe class ZoneMapArray
{
    /// <summary>
    /// The three arrays and the capacity that describes them, as one immutable unit. Replaced wholesale on growth, never resized in place.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The four used to be four fields, grown by three <c>Array.Resize</c> calls and a capacity assignment with no lock and no ordering — which broke in two
    /// ways at once (review M5). Publication: a reader could see the new capacity against an old array, or a min from one generation against a max from
    /// another; on arm64 the stores can be observed out of order outright. Worse, and reachable on x64 today: <c>Array.Resize(ref _mins, …)</c> RE-READS the
    /// field, so two concurrent growers could leave the three arrays at different lengths — a thread holding a small <c>newCap</c> shrinks the array the
    /// other one just grew, and the next element write runs off the end of it.
    /// </para>
    /// <para>
    /// Bundling them means a reader's single <see cref="Volatile.Read{T}"/> either sees a generation whole or does not see it at all. Three separately
    /// published fields would need every reader to load them in a fixed order to be sound, and nothing in the type system makes that hold. Same reasoning as
    /// <c>ArchetypeClusterState.EnsureClusterVisibilityCapacity</c>, whose comment cites this finding.
    /// </para>
    /// </remarks>
    private sealed class Store
    {
        internal readonly long[] Mins;      // [clusterChunkId] → min value (ordered long, sign-flipped for float/unsigned ordering)
        internal readonly long[] Maxs;      // [clusterChunkId] → max value (ordered long, sign-flipped for float/unsigned ordering)
        internal readonly bool[] Valid;     // [clusterChunkId] → true if min/max are initialized
        internal readonly int Capacity;

        internal Store(long[] mins, long[] maxs, bool[] valid, int capacity)
        {
            Mins = mins;
            Maxs = maxs;
            Valid = valid;
            Capacity = capacity;
        }
    }

    private Store _store;

    // ═════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════
    // Growth latch. Element writers take it SHARED, the grower takes it EXCLUSIVE — so a generation is only ever copied when no writer is inside one.
    //
    // The snapshot alone is not enough, and the test that proves it is ConcurrentGrowth_KeepsEveryWrittenValueInBounds. Publishing atomically stops a reader
    // seeing a mixed triple, but it does nothing about this:
    //
    //     writer:  resolves store = gen1, about to write Mins[5]
    //     grower:  copies gen1 -> gen2 and publishes gen2
    //     writer:  writes into gen1, which nobody will ever read again
    //
    // — a LOST WIDEN, which is a false negative: the planner prunes a cluster out of an indexed range query and rows silently vanish until the tick fence
    // recomputes. The class contract says false negatives are impossible, so "bounded by the fence" is not good enough. A retry (write, re-read _store, redo if
    // it moved) does NOT close it either: if the grower's publish lands after the writer's re-read, the writer concludes it is current and the write is still
    // lost. Excluding the copy is the only version that makes the contract true.
    //
    // Cost: one uncontended shared acquire per Widen, i.e. per indexed field per commit. That is an Interlocked CAS — tens of cycles — next to the B+Tree
    // descent it accompanies, which costs hundreds of nanoseconds. Padded for the same reason PaddedFinalizeLock is (rule MD-03).
    // ═════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════

    [StructLayout(LayoutKind.Explicit, Size = 64)]
    private struct PaddedGrowLatch
    {
        [FieldOffset(0)] public AccessControlSmall Lock;
    }

    private PaddedGrowLatch _growLatch;
    private readonly int _fieldSize;
    private readonly bool _isFloat;
    private readonly bool _isDouble;
    private readonly bool _isUnsigned;

    internal ZoneMapArray(int initialCapacity, int fieldSize, bool isFloat, bool isDouble, bool isUnsigned = false)
    {
        var capacity = Math.Max(16, initialCapacity);
        _store = new Store(new long[capacity], new long[capacity], new bool[capacity], capacity);
        _fieldSize = fieldSize;
        _isFloat = isFloat;
        _isDouble = isDouble;
        _isUnsigned = isUnsigned;
    }

    /// <summary>
    /// Recompute min/max for a single cluster by scanning all occupied entities.
    /// Called at tick fence for each dirty cluster.
    /// </summary>
    public void Recompute(int clusterChunkId, byte* clusterBase, ArchetypeClusterInfo layout, int compSlot, int fieldOffset)
        => Recompute(clusterChunkId, clusterBase, clusterBase, layout, compSlot, fieldOffset);

    /// <summary>
    /// <see cref="Recompute(int,byte*,ArchetypeClusterInfo,int,int)"/> for a slot whose component bytes do NOT live in the same segment as the occupancy word:
    /// a <see cref="Typhon.Schema.Definition.StorageMode.Transient"/> slot on a mixed archetype (#655).
    /// </summary>
    /// <param name="clusterChunkId">Chunk id, identical in both segments — allocation is lockstep.</param>
    /// <param name="primaryBase">Chunk holding the occupancy word: the cluster segment, or the Transient segment on a pure-Transient archetype.</param>
    /// <param name="dataBase">Chunk holding this slot's component column, in whichever segment matches its storage mode.</param>
    /// <param name="layout">Cluster layout — shared by both segments, which is what makes one set of offsets address either.</param>
    /// <param name="compSlot">Per-archetype component slot to scan.</param>
    /// <param name="fieldOffset">Byte offset of the indexed field within the component.</param>
    public void Recompute(int clusterChunkId, byte* primaryBase, byte* dataBase, ArchetypeClusterInfo layout, int compSlot, int fieldOffset)
    {
        // The scan runs OUTSIDE the latch and only the three stores are inside it: holding shared access across a 64-slot scan would stall every grower for
        // no benefit, since the scan reads cluster memory, not this object.
        ulong occupancy = *(ulong*)primaryBase;
        if (occupancy == 0)
        {
            var empty = AcquireForWrite(clusterChunkId);
            try
            {
                empty.Valid[clusterChunkId] = false;
            }
            finally
            {
                ReleaseAfterWrite();
            }

            return;
        }

        int compSize = layout.ComponentSize(compSlot);
        byte* compBase = dataBase + layout.ComponentOffset(compSlot);

        long min = long.MaxValue;
        long max = long.MinValue;
        ulong bits = occupancy;

        while (bits != 0)
        {
            int slotIndex = BitOperations.TrailingZeroCount(bits);
            bits &= bits - 1;
            byte* fieldPtr = compBase + slotIndex * compSize + fieldOffset;
            long val = ReadFieldAsOrderedLong(fieldPtr);
            if (val < min)
            {
                min = val;
            }
            if (val > max)
            {
                max = val;
            }
        }

        var store = AcquireForWrite(clusterChunkId);
        try
        {
            store.Mins[clusterChunkId] = min;
            store.Maxs[clusterChunkId] = max;
            store.Valid[clusterChunkId] = true;
        }
        finally
        {
            ReleaseAfterWrite();
        }
    }

    /// <summary>
    /// Widen bounds to include a new value (eager, on spawn). Never narrows.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Widen(int clusterChunkId, byte* fieldPtr)
    {
        // Decoded before taking the latch — it dereferences cluster memory, which the latch has nothing to do with.
        long val = ReadFieldAsOrderedLong(fieldPtr);

        var store = AcquireForWrite(clusterChunkId);
        try
        {
            if (!store.Valid[clusterChunkId])
            {
                store.Mins[clusterChunkId] = val;
                store.Maxs[clusterChunkId] = val;
                store.Valid[clusterChunkId] = true;
                return;
            }

            if (val < store.Mins[clusterChunkId])
            {
                store.Mins[clusterChunkId] = val;
            }
            if (val > store.Maxs[clusterChunkId])
            {
                store.Maxs[clusterChunkId] = val;
            }
        }
        finally
        {
            ReleaseAfterWrite();
        }
    }

    /// <summary>
    /// Check if a cluster's zone map overlaps the query range [queryMin, queryMax].
    /// Returns true if the cluster MAY contain matching entities (or if the zone map is not initialized).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool MayContain(int clusterChunkId, long queryMin, long queryMax)
    {
        // One acquire load for the whole check. The capacity and the three arrays come from the same generation by construction, so the bounds check below
        // cannot be validated against one generation and then indexed into another.
        var store = Volatile.Read(ref _store);
        if ((uint)clusterChunkId >= (uint)store.Capacity || !store.Valid[clusterChunkId])
        {
            return true; // Unknown → don't skip (conservative)
        }

        // Standard interval overlap: !(clusterMax < queryMin || clusterMin > queryMax)
        return store.Maxs[clusterChunkId] >= queryMin && store.Mins[clusterChunkId] <= queryMax;
    }

    /// <summary>
    /// Invalidate a cluster's zone map (e.g., when cluster is freed).
    /// </summary>
    public void Invalidate(int clusterChunkId)
    {
        // A write, so it takes the latch like the other two — but never grows: an index past the current capacity has no bounds recorded, which is already
        // what "invalid" means.
        _growLatch.Lock.EnterSharedAccess(ref WaitContext.Null);
        try
        {
            var store = _store;
            if ((uint)clusterChunkId < (uint)store.Capacity)
            {
                store.Valid[clusterChunkId] = false;
            }
        }
        finally
        {
            _growLatch.Lock.ExitSharedAccess();
        }
    }

    /// <summary>
    /// Read a field value as a long that preserves sort order across types.
    /// For floats: sign-flip so that negative floats sort before positive.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private long ReadFieldAsOrderedLong(byte* ptr)
    {
        if (_isFloat)
        {
            return FloatToOrderedLong(*(float*)ptr);
        }

        if (_isDouble)
        {
            return DoubleToOrderedLong(*(double*)ptr);
        }

        if (_isUnsigned)
        {
            // Unsigned types: XOR with sign bit to preserve ordering in signed comparison.
            // Maps unsigned 0 → signed MIN, unsigned MAX → signed MAX.
            return _fieldSize switch
            {
                1 => *ptr,                                             // byte: 0..255 fits, no XOR needed
                2 => *(ushort*)ptr ^ (1L << 15),                       // ushort: XOR bit 15
                4 => *(uint*)ptr ^ (1L << 31),                         // uint: XOR bit 31
                8 => *(long*)ptr ^ long.MinValue,                      // ulong: XOR bit 63
                _ => *(uint*)ptr ^ (1L << 31),
            };
        }

        Debug.Assert(_fieldSize is 1 or 2 or 4 or 8, $"Unexpected zone map field size: {_fieldSize}");
        return _fieldSize switch
        {
            1 => *(sbyte*)ptr,
            2 => *(short*)ptr,
            4 => *(int*)ptr,
            8 => *(long*)ptr,
            _ => *(int*)ptr,
        };
    }

    // Float ordering: flip all bits if negative (sign bit set), else flip only sign bit.
    // This converts IEEE 754 to a representation where memcmp order = numeric order.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static long FloatToOrderedLong(float value)
    {
        // Normalise -0.0f to +0.0f BEFORE reading the bits. They are numerically EQUAL, but their bit patterns are not adjacent-and-equal — they encode one
        // apart (2147483647 vs 2147483648). Zone-map pruning compares encoded values for overlap, so a cluster holding only -0.0f did not overlap the bounds
        // of `field == 0.0f` and was skipped entirely, dropping rows that match. The B+Tree itself is unaffected: it compares floats, where the two ARE equal.
        int bits = value == 0f ? 0 : BitConverter.SingleToInt32Bits(value);

        // Cast to long BEFORE XOR/NOT to avoid sign-extension of int result to long.
        // Without cast: (0 ^ int.MinValue) = int -2147483648, sign-extends to long -2147483648 (wrong ordering).
        // With cast: (0L ^ (long)(uint)int.MinValue) = long 2147483648 (correct ordering).
        return bits < 0 ? ~(long)bits : (uint)(bits ^ int.MinValue);
    }

    // Double ordering: the "flip all bits if negative, else flip the sign bit" trick above CANNOT be reused here, and using it was a wrong-answer bug.
    //
    // That trick produces a value that is ordered under UNSIGNED comparison. FloatToOrderedLong gets away with it because a 32-bit result widens into the
    // positive half of a long, where signed and unsigned order coincide — which is exactly what the cast in its body is for. A 64-bit result has nowhere to
    // widen into: `bits ^ long.MinValue` sets the sign bit of every POSITIVE double, so positives came out negative and sorted BELOW every negative value.
    //
    // The consequence was silent and not small. Zone-map pruning compares these as signed longs, so a cluster holding only positive doubles failed to overlap
    // the query range for `d > negative` and was skipped entirely — the SoA scan dropped 93 of 143 matching rows in the fixture that found this. The K-way
    // merge uses the same encoding to order its streams, so an OrderBy on a double column was mis-sorted the same way. Any double column whose values straddle
    // zero was affected; the suite had no double-indexed cluster query, so nothing caught it.
    //
    // This mapping is monotone under SIGNED comparison, which is what every consumer actually does: positive doubles keep their bit pattern (already ascending
    // in [0, 2^63)), and negative doubles map to [long.MinValue + 1, 0] ascending, because their bit patterns run DESCENDING as the value grows.
    // -0.0 maps to 0, the same as +0.0, which is correct — they compare equal.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static long DoubleToOrderedLong(double value)
    {
        var bits = BitConverter.DoubleToInt64Bits(value);
        return bits >= 0 ? bits : long.MinValue - bits;
    }

    /// <summary>
    /// Convert a <see cref="FieldEvaluator"/> into zone map query bounds [min, max] as ordered longs.
    /// The bounds define the interval that must overlap the zone map for a potential match.
    /// Returns false if the evaluator's CompareOp cannot be expressed as a range (e.g., NotEqual).
    /// </summary>
    internal static bool TryGetQueryBounds(ref FieldEvaluator eval, out long queryMin, out long queryMax)
    {
        long orderedThreshold = ThresholdToOrdered(eval.Threshold, eval.KeyType);

        switch (eval.CompareOp)
        {
            case CompareOp.Equal:
                queryMin = orderedThreshold;
                queryMax = orderedThreshold;
                return true;
            case CompareOp.GreaterThan:
                queryMin = orderedThreshold + 1;
                queryMax = long.MaxValue;
                return true;
            case CompareOp.GreaterThanOrEqual:
                queryMin = orderedThreshold;
                queryMax = long.MaxValue;
                return true;
            case CompareOp.LessThan:
                queryMin = long.MinValue;
                queryMax = orderedThreshold - 1;
                return true;
            case CompareOp.LessThanOrEqual:
                queryMin = long.MinValue;
                queryMax = orderedThreshold;
                return true;
            case CompareOp.NotEqual:
            default:
                queryMin = long.MinValue;
                queryMax = long.MaxValue;
                return false; // Cannot prune with NotEqual
        }
    }

    /// <summary>
    /// Convert a <see cref="FieldEvaluator.Threshold"/> to the ordered long encoding used by zone maps.
    /// Same sign-flip logic as <see cref="ReadFieldAsOrderedLong"/>.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static long ThresholdToOrdered(long threshold, KeyType keyType)
    {
        switch (keyType)
        {
            case KeyType.Float:
            {
                int bits = (int)threshold;
                float value = Unsafe.As<int, float>(ref bits);
                return FloatToOrderedLong(value);
            }
            case KeyType.Double:
            {
                double value = Unsafe.As<long, double>(ref threshold);
                return DoubleToOrderedLong(value);
            }
            // Unsigned types: XOR with sign bit (must match ReadFieldAsOrderedLong encoding)
            case KeyType.Byte:
                return threshold; // 0..255 fits in signed long, no XOR needed
            case KeyType.UShort:
                return threshold ^ (1L << 15);
            case KeyType.UInt:
                return threshold ^ (1L << 31);
            case KeyType.ULong:
                return threshold ^ long.MinValue;
            default:
                // Signed integers are already in sort order as longs.
                return threshold;
        }
    }

    /// <summary>
    /// Takes SHARED access and returns the generation to write through, growing first if <paramref name="index"/> does not fit. The caller <b>must</b> call
    /// <see cref="ReleaseAfterWrite"/> when it has finished writing, in a <c>finally</c>.
    /// </summary>
    /// <remarks>
    /// Growth cannot happen while shared access is held, so the returned generation is guaranteed to still be current when the caller writes into it. The
    /// grow releases shared first — taking exclusive while holding shared would deadlock against its own wait for the shared count to drain — and then
    /// loops, because another grower may have covered the index in between.
    /// </remarks>
    private Store AcquireForWrite(int index)
    {
        while (true)
        {
            _growLatch.Lock.EnterSharedAccess(ref WaitContext.Null);
            var store = _store;
            if (index < store.Capacity)
            {
                return store;
            }

            _growLatch.Lock.ExitSharedAccess();
            Grow(index);
        }
    }

    private void ReleaseAfterWrite() => _growLatch.Lock.ExitSharedAccess();

    /// <summary>
    /// Replaces the current generation with one large enough for <paramref name="index"/>, under EXCLUSIVE access so no element write is in flight.
    /// </summary>
    private void Grow(int index)
    {
        _growLatch.Lock.EnterExclusiveAccess(ref WaitContext.Null);
        try
        {
            // Re-check under the latch: another grower may have covered this index while we waited.
            var store = _store;
            if (index < store.Capacity)
            {
                return;
            }

            var newCap = Math.Max(store.Capacity * 2, index + 1);
            var mins = new long[newCap];
            var maxs = new long[newCap];
            var valid = new bool[newCap];
            Array.Copy(store.Mins, mins, store.Capacity);
            Array.Copy(store.Maxs, maxs, store.Capacity);
            Array.Copy(store.Valid, valid, store.Capacity);

            // Release: a reader acquires this reference without the latch, so it must not be able to observe the object before its arrays are copied — it
            // would read a default 0 as a real bound and prune a cluster that matches.
            Volatile.Write(ref _store, new Store(mins, maxs, valid, newCap));
        }
        finally
        {
            _growLatch.Lock.ExitExclusiveAccess();
        }
    }
}
