using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading;

namespace Typhon.Engine.Internals;

/// <summary>
/// One index entry the tick fence moves: the entity at cluster location <see cref="Value"/> leaves <see cref="OldKey"/> for <see cref="NewKey"/> (#1346).
/// </summary>
/// <remarks>
/// Key-erased: a fence-maintained index key is at most 8 bytes (a String64 index on an in-place storage mode is refused at registration), held in its low
/// bytes as <see cref="KeyBytes8.FromPointer"/> reads it. <see cref="ElementId"/> is the entity's element on an AllowMultiple index: the chunk of
/// <see cref="OldKey"/>'s buffer that holds it on the way in, the one of <see cref="NewKey"/>'s on the way out. Unused on a unique index.
/// </remarks>
internal struct IndexMove
{
    public KeyBytes8 OldKey;
    public KeyBytes8 NewKey;
    public int Value;
    public int ElementId;
}

/// <summary>What <see cref="BTreeBase{TStore}.ApplyMoves"/> did not do.</summary>
internal struct IndexMoveOutcome
{
    /// <summary>Moves whose old key or element was not where the move said: the index already disagreed with the data. The entity is indexed under its
    /// new key anyway.</summary>
    public int Misses;

    /// <summary>Unique moves whose new key another entity holds after the batch: the entity leaves its old key and is not indexed under the new one.</summary>
    public int Collisions;

    /// <summary>The first collided move's index in the batch, or -1.</summary>
    public int FirstCollision;
}

/// <summary>
/// One key range of a prepared move batch (<see cref="BTreeBase{TStore}.PrepareMoveBatch"/>): an interval of keys whose leaves no other range reaches, the
/// events it gathered from the batch, and what its bulk pass left for the serial finish.
/// </summary>
internal struct IndexMoveRange
{
    /// <summary>The tree's per-range state this range uses: its key interval and its events.</summary>
    public int Slot;

    /// <summary>The events the range should gather, estimated from the sample the cut was chosen on: the work plan's cost.</summary>
    public int EstimatedEvents;

    /// <summary>The events <see cref="BTreeBase{TStore}.ApplyMoveRange"/> gathered.</summary>
    public int Events;

    /// <summary>Events the bulk pass left to the single-entry path, listed from the start of the range's deferred list.</summary>
    public int Deferred;

    /// <summary>AllowMultiple: keys the bulk pass emptied, listed back from <see cref="Events"/> in the range's deferred list.</summary>
    public int Emptied;

    /// <summary>Whether <see cref="BTreeBase{TStore}.ApplyMoveRange"/> ran to the end on this range.</summary>
    public bool Applied;

    public IndexMoveOutcome Outcome;
}

internal static class IndexMoveBatch
{
    /// <summary>
    /// The fewest events a range of a partitioned move batch is cut to (<see cref="BTreeBase{TStore}.PrepareMoveBatch"/>): below it, a range's dispatch
    /// costs more than its share of the walk saves. Settable for tests, which partition batches far smaller than a fence's.
    /// </summary>
    internal static int MinEventsPerRange = 4_096;

    /// <summary>Ranges <see cref="BTreeBase{TStore}.FinishMoveBatch"/> had to apply itself because no worker had, process-wide. Diagnostic: on the
    /// parallel fence it stays zero unless a range's work item failed or never ran.</summary>
    internal static long RangesAppliedAtFinish;
}

internal abstract partial class BTree<TKey, TStore>
{
    /// <summary>One side of a move: an entity leaving <see cref="Key"/> (<see cref="Arrives"/> false) or arriving at it.</summary>
    private struct MoveEvent
    {
        public TKey Key;
        public int Move;
        public int ElementId;   // a departure's element under Key; unused on an arrival
        public int Value;
        public bool Arrives;
    }

    private readonly struct MoveEventRadixKey : IRadixKey<MoveEvent>
    {
        public static ulong Key(in MoveEvent e) => RadixKeyOf(e.Key);
    }

    private readonly struct MoveEventComparer(IComparer<TKey> comparer) : IComparer<MoveEvent>
    {
        public int Compare(MoveEvent a, MoveEvent b)
        {
            var c = CompareKeys(a.Key, b.Key, comparer);
            return c != 0 ? c : a.Arrives != b.Arrives ? (a.Arrives ? 1 : -1) : a.Move.CompareTo(b.Move);
        }
    }

    /// <summary>One range's key interval, gathered events, sort scratch and deferred list: written only by the thread applying that range.</summary>
    private sealed class MoveRangeState
    {
        public bool HasLow;
        public bool HasHigh;
        public TKey Low;
        public TKey High;
        public MoveEvent[] Events = [];
        public MoveEvent[] Scratch = [];
        public int[] Deferred = [];
        public int[] RadixCounts;
        public int Count;

        // The bounds as radix keys, when the key type has them: the gather's interval test is then two ulong compares, in the order the sort already
        // relies on (RadixKeyOf agrees with CompareKeys, NaN and -0.0 included).
        public ulong LowRadix;
        public ulong HighRadix;

        /// <summary>Whether <paramref name="key"/> falls in [<see cref="Low"/>, <see cref="High"/>), an absent bound bounding nothing.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool Holds(TKey key, IComparer<TKey> comparer) =>
            (!HasLow || CompareKeys(key, Low, comparer) >= 0) && (!HasHigh || CompareKeys(key, High, comparer) < 0);
    }

    private MoveRangeState[] _moveRanges = [];   // allocated by the first batch: most trees never take one
    private TKey[] _moveSample = [];
    private int _moveCountBefore;

    // AllowMultiple: each arrival's element id under its new key, by move, written by the range that applied it. Not straight into the move's ElementId:
    // that is where another range, gathering concurrently, reads the departure's element id from. FinishMoveBatch copies them over, serially.
    private int[] _moveNewElementIds = [];

    // MergeLeaf's scratch, one per thread: the ranges of one batch run on several workers at once.
    [ThreadStatic] private static KeyValueItem[] t_leafMerge;

    /// <inheritdoc/>
    /// <remarks>
    /// <para>
    /// <b>Why it is faster than one <c>Move</c> / <c>MoveValue</c> per entity.</b> Each of those descends from the root, latches a leaf,
    /// re-finds both keys and rents and returns its accessors: ~350–450 ns, nearly all of it per call rather than per byte moved (measured, #1346). Here
    /// the moves are turned into departure and arrival events, sorted by key (radix, stable) — per key range, each range on its own thread on the parallel
    /// fence — and walked in key order with a leaf cursor that steps to the next leaf instead of descending again.
    /// </para>
    /// <para>
    /// <b>AllowMultiple: arrivals take the departures' places.</b> Within one key, a departing entity's element is overwritten in place with an arriving
    /// entity's location (<c>UpdateInBuffer</c>): the buffer's membership is what it must be, no element shifts, nothing is allocated, and the arriving entity
    /// takes over the departing one's element id. Only the surplus is removed or appended. A key that does not exist yet, or one its last element leaves, is
    /// a structural change and goes through the latched single-entry path afterwards.
    /// </para>
    /// <para>
    /// <b>Unique: decided on the final state.</b> For equal keys, departures sort before arrivals, so a swap (A: 1→2, B: 2→1) frees each key before it is
    /// taken again; a new key collides only when it is taken twice in the batch or held by an entity that is not leaving it (#1342), and a collided move
    /// still leaves its old key. A leaf whose entries can change in place — it stays between half full and full — is rewritten in one merge under its latch;
    /// any other leaf's moves go through the single-entry path.
    /// </para>
    /// <para>
    /// No structural change happens on the bulk path, so the leaf boundaries the cursor walks never move under it. Every leaf it changes is latched for the
    /// change, which bumps its version (IXS-02 / IXS-09 readers revalidate), and nothing is allocated under a latch (IXW-07): an append that grows a buffer
    /// runs before the leaf's latch is taken.
    /// </para>
    /// </remarks>
    public override IndexMoveOutcome ApplyMoves(Span<IndexMove> moves, ref ChunkAccessor<TStore> accessor)
    {
        if (moves.Length == 0)
        {
            ThrowIfFaulted();
            return new IndexMoveOutcome { FirstCollision = -1 };
        }

        Span<IndexMoveRange> whole = stackalloc IndexMoveRange[1];
        PrepareMoveBatch(moves, 1, whole, ref accessor);
        ApplyMoveRange(moves, ref whole[0], ref accessor);
        return FinishMoveBatch(moves, whole, ref accessor);
    }

    /// <inheritdoc/>
    /// <remarks>
    /// <para>The cuts are keys, not positions in a sorted batch: nothing is sorted here, so this serial step costs a sample, not the batch. A sample of the
    /// batch's keys, sorted, gives the quantiles; each is snapped up to the <c>HighKey</c> of the leaf that owns it — a leaf edge, the bound
    /// <see cref="LeafCursor"/> uses — so every key one leaf owns, and every event of one key, falls in one range, and since the bulk path changes no
    /// leaf's bounds no two ranges reach the same leaf. Each range then gathers its events from the batch and sorts them itself
    /// (<see cref="ApplyMoveRange"/>), on the workers.</para>
    /// </remarks>
    public override int PrepareMoveBatch(Span<IndexMove> moves, int desiredParts, Span<IndexMoveRange> ranges, ref ChunkAccessor<TStore> accessor)
    {
        ThrowIfFaulted();
        _moveCountBefore = _count;
        _hasCachedLastKey = false;   // a key may leave the tree's end (#1263)
        var ids = _moveNewElementIds.Length;
        if (AllowMultiple && (ids < moves.Length || (ids > 131_072 && ids > 8L * moves.Length)))
        {
            _moveNewElementIds = new int[ids < moves.Length ? Math.Max(moves.Length, Math.Max(256, ids * 2)) : moves.Length];
        }

        var events = 2L * moves.Length;
        desiredParts = (int)Math.Max(1, Math.Min(Math.Min(desiredParts, ranges.Length), events / Math.Max(1, IndexMoveBatch.MinEventsPerRange)));
        var sampled = desiredParts > 1 ? SampleMoveKeys(moves, desiredParts) : 0;
        var parts = 0;
        TKey low = default;
        var hasLow = false;
        for (var p = 1; p < desiredParts; p++)
        {
            var quantile = _moveSample[(int)((long)sampled * p / desiredParts)];
            if (hasLow && CompareKeys(quantile, low, Comparer) < 0)
            {
                continue;   // below the previous cut: the sample is dense there, and that range already holds it
            }

            var leaf = FindLeaf(quantile, out _, ref accessor, followRightLink: false);
            if (!leaf.IsValid || !leaf.GetNext(ref accessor).IsValid)
            {
                break;   // an empty tree, or the rightmost leaf: it owns every key from here on
            }

            // Above the quantile, which its leaf owns, so above the previous cut: the intervals ascend and tile the key space.
            var high = leaf.GetHighKey(ref accessor);
            SetMoveRange(ranges, parts++, hasLow, low, true, high, sampled, events);
            low = high;
            hasLow = true;
        }

        SetMoveRange(ranges, parts++, hasLow, low, false, default, sampled, events);
        return parts;
    }

    /// <summary>Sorted keys of up to 64 moves per part, evenly strided over the batch, both keys of each: the cut's quantiles.</summary>
    private int SampleMoveKeys(Span<IndexMove> moves, int parts)
    {
        var picks = Math.Min(moves.Length, 64 * parts);
        var n = 2 * picks;
        if (_moveSample.Length < n)
        {
            _moveSample = new TKey[n];
        }

        for (var k = 0; k < picks; k++)
        {
            ref var m = ref moves[(int)((long)k * moves.Length / picks)];
            _moveSample[2 * k] = KeyOf(m.OldKey);
            _moveSample[2 * k + 1] = KeyOf(m.NewKey);
        }

        Array.Sort(_moveSample, 0, n, Comparer);
        return n;
    }

    private void SetMoveRange(Span<IndexMoveRange> ranges, int slot, bool hasLow, TKey low, bool hasHigh, TKey high, int sampled, long events)
    {
        if (_moveRanges.Length <= slot)
        {
            Array.Resize(ref _moveRanges, Math.Max(slot + 1, _moveRanges.Length * 2));
        }

        var state = _moveRanges[slot] ??= new MoveRangeState();
        state.HasLow = hasLow;
        state.Low = low;
        state.HasHigh = hasHigh;
        state.High = high;
        state.Count = 0;
        if (RadixSortableKey)
        {
            state.LowRadix = hasLow ? RadixKeyOf(low) : 0;
            state.HighRadix = hasHigh ? RadixKeyOf(high) : 0;
        }
        var estimate = events;
        if (sampled > 0)
        {
            var inRange = 0;
            for (var k = 0; k < sampled; k++)
            {
                if (state.Holds(_moveSample[k], Comparer))
                {
                    inRange++;
                }
            }

            estimate = events * inRange / sampled;
        }

        ranges[slot] = new IndexMoveRange { Slot = slot, EstimatedEvents = (int)Math.Min(estimate, int.MaxValue) };
    }

    /// <inheritdoc/>
    public override void ApplyMoveRange(Span<IndexMove> moves, ref IndexMoveRange range, ref ChunkAccessor<TStore> accessor)
    {
        ThrowIfFaulted();
        range.Outcome = new IndexMoveOutcome { FirstCollision = -1 };
        range.Events = 0;
        range.Deferred = 0;
        range.Emptied = 0;
        var state = _moveRanges[range.Slot];
        state.Count = 0;
        if (moves.Length == 0)
        {
            range.Applied = true;
            return;
        }

        _fenceWindow?.NoteMutation("BTree.ApplyMoveRange");
        var held = OlcLatchHeld.Count;
        ref var opAccessor = ref _segment.RentWarmAccessor(accessor.ChangeSet);
        try
        {
            ref var sibAccessor = ref _segment.RentWarmSiblingAccessor(accessor.ChangeSet);
            try
            {
                GatherMoveEvents(moves, state, range.EstimatedEvents);
                range.Events = state.Count;
                if (AllowMultiple)
                {
                    BulkMultiMoves(state, ref range, ref opAccessor, ref sibAccessor);
                }
                else
                {
                    BulkUniqueMoves(state, ref range, ref opAccessor);
                }

                range.Applied = true;
            }
            catch (Exception e)
            {
                OnWriteException(e, held);   // #1321
                FaultBatch(e);
                throw;
            }
            finally
            {
                _segment.ReturnWarmSiblingAccessor();
            }
        }
        finally
        {
            _segment.ReturnWarmAccessor();
        }
    }

    /// <inheritdoc/>
    public override IndexMoveOutcome FinishMoveBatch(Span<IndexMove> moves, Span<IndexMoveRange> ranges, ref ChunkAccessor<TStore> accessor)
    {
        var outcome = new IndexMoveOutcome { FirstCollision = -1 };
        var held = OlcLatchHeld.Count;
        try
        {
            // A range whose bulk pass never ran — its chunk was not dispatched — runs here, before any structural change moves the leaves it was cut on.
            for (var r = 0; r < ranges.Length; r++)
            {
                if (!ranges[r].Applied)
                {
                    Interlocked.Increment(ref IndexMoveBatch.RangesAppliedAtFinish);
                    ApplyMoveRange(moves, ref ranges[r], ref accessor);
                }
            }

            ThrowIfFaulted();
            _fenceWindow?.NoteMutation("BTree.FinishMoveBatch");
            for (var r = 0; r < ranges.Length; r++)
            {
                ref var o = ref ranges[r].Outcome;
                outcome.Misses += o.Misses;
                if (o.Collisions > 0 && outcome.Collisions == 0)
                {
                    outcome.FirstCollision = o.FirstCollision;
                }

                outcome.Collisions += o.Collisions;
            }

            _hasCachedLastKey = false;
            if (_count != _moveCountBefore)
            {
                // The directory entry holds the key count; the deferred single-entry paths sync their own changes.
                ref var opAccessor = ref _segment.RentWarmAccessor(accessor.ChangeSet);
                try
                {
                    SyncHeader(ref opAccessor);
                }
                finally
                {
                    _segment.ReturnWarmAccessor();
                }
            }

            // The structural remainder, through the latched single-entry paths, which rent this thread's warm accessors themselves.
            if (AllowMultiple)
            {
                DeferredMultiMoves(moves, ranges, ref outcome, ref accessor);
            }
            else
            {
                DeferredUniqueMoves(ranges, ref outcome, ref accessor);
            }

            return outcome;
        }
        catch (Exception e)
        {
            if (_fault == null)
            {
                OnWriteException(e, held);   // #1321
                FaultBatch(e);
            }

            throw;
        }
    }

    /// <summary>
    /// A batch cut short is worse than one write cut short: paired arrivals hold departing elements whose ids the caller never wrote back, and leaves are half
    /// applied. The tree is faulted, as a write that throws under its latches is (IXW-09).
    /// </summary>
    private void FaultBatch(Exception e) => Interlocked.CompareExchange(ref _fault, e, null);

    /// <summary>
    /// A range's events, gathered from the whole batch and sorted by key: departures first, then arrivals, each in move order, so the stable sort keeps a
    /// key's departures ahead of its arrivals and every key's events in the order a sort of the whole batch gives them.
    /// </summary>
    /// <remarks>Reads the batch, writes only the range's own state: the ranges of one batch gather concurrently.</remarks>
    private void GatherMoveEvents(Span<IndexMove> moves, MoveRangeState state, int estimate)
    {
        // The whole batch's count is exact; a range's is a sample's estimate, given some slack.
        var target = Math.Max(1_024, estimate >= 2L * moves.Length ? 2 * moves.Length : estimate + estimate / 4);
        if (state.Events.Length < target || (state.Events.Length > 131_072 && state.Events.Length > 8L * target))   // grown, or a burst passed
        {
            state.Events = new MoveEvent[target];
            state.Scratch = new MoveEvent[target];
        }

        var departures = state.Events;
        var arrivals = state.Scratch;   // the arrivals, until they are appended to the departures; then the sort's scratch
        var d = 0;
        var a = 0;
        var comparer = Comparer;
        var radix = RadixSortableKey;
        var hasLow = state.HasLow;
        var hasHigh = state.HasHigh;
        var lowRadix = state.LowRadix;
        var highRadix = state.HighRadix;
        for (var i = 0; i < moves.Length; i++)
        {
            ref var m = ref moves[i];
            var oldKey = KeyOf(m.OldKey);
            if (radix ? InRadix(RadixKeyOf(oldKey), hasLow, lowRadix, hasHigh, highRadix) : state.Holds(oldKey, comparer))
            {
                if (d == departures.Length)
                {
                    Array.Resize(ref state.Events, d * 2);
                    departures = state.Events;
                }

                departures[d++] = new MoveEvent { Key = oldKey, Move = i, ElementId = m.ElementId, Value = m.Value, Arrives = false };
            }

            var newKey = KeyOf(m.NewKey);
            if (radix ? InRadix(RadixKeyOf(newKey), hasLow, lowRadix, hasHigh, highRadix) : state.Holds(newKey, comparer))
            {
                if (a == arrivals.Length)
                {
                    Array.Resize(ref state.Scratch, a * 2);
                    arrivals = state.Scratch;
                }

                arrivals[a++] = new MoveEvent { Key = newKey, Move = i, Value = m.Value, Arrives = true };
            }
        }

        var n = d + a;
        if (state.Events.Length < n)
        {
            Array.Resize(ref state.Events, n);
        }

        Array.Copy(state.Scratch, 0, state.Events, d, a);
        if (state.Scratch.Length < n)
        {
            state.Scratch = new MoveEvent[state.Events.Length];
        }

        if (state.Deferred.Length < n || (state.Deferred.Length > 131_072 && state.Deferred.Length > 8L * n))
        {
            state.Deferred = new int[Math.Max(n, 1_024)];
        }

        state.Count = n;
        var events = state.Events.AsSpan(0, n);
        if (RadixSortableKey)
        {
            RadixSort.Sort<MoveEvent, MoveEventRadixKey>(events, state.Scratch.AsSpan(0, n), state.RadixCounts ??= new int[RadixSort.Buckets]);
        }
        else
        {
            // No radix order for this key type. A stable order is required — departures before arrivals within a key — so the comparer breaks ties on
            // that, then on the move, which makes it total.
            events.Sort(new MoveEventComparer(Comparer));
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool InRadix(ulong key, bool hasLow, ulong low, bool hasHigh, ulong high) => (!hasLow || key >= low) && (!hasHigh || key < high);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowRangeCrossesItsCut() =>
        throw new InvalidOperationException("ApplyMoveRange reached a leaf beyond its range's cut: the batch's ranges were not cut at leaf edges.");

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static TKey KeyOf(KeyBytes8 key) => Unsafe.As<KeyBytes8, TKey>(ref key);

    /// <summary>
    /// The leaf owning an ascending sequence of keys, with its upper bound read once per leaf instead of once per key. Exact without validation, because no
    /// other writer runs (EW-01) and nothing on the bulk path changes a leaf's bounds.
    /// </summary>
    /// <remarks>
    /// Keys only ascend, so the lower bound never needs asking: the first key of a leaf is reached by a descent, which lands on its owner, or by a step right
    /// from the leaf whose upper bound it reached, which is the next leaf's lower bound. The upper bound is <c>KeyAboveLeafUpperBound</c>'s: the
    /// <c>HighKey</c> of a non-empty leaf with a right sibling; the rightmost leaf bounds nothing.
    /// </remarks>
    private struct LeafCursor
    {
        public NodeWrapper Leaf;
        private bool _bounded;
        private TKey _high;

        /// <summary>Whether <see cref="Leaf"/> owns <paramref name="key"/>, a key at or above every key it was asked for before.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public readonly bool Owns(TKey key, IComparer<TKey> comparer) => !_bounded || CompareKeys(key, _high, comparer) < 0;

        /// <summary>Whether every key <see cref="Leaf"/> owns is below <paramref name="high"/>: the leaf ends at or before that bound.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public readonly bool EndsBy(TKey high, IComparer<TKey> comparer) => _bounded && CompareKeys(_high, high, comparer) <= 0;

        /// <summary>Moves to the leaf owning <paramref name="key"/>: this one, one or two steps right, or a fresh descent. Invalid on an empty tree.</summary>
        public void Seek(BTree<TKey, TStore> tree, TKey key, ref ChunkAccessor<TStore> accessor)
        {
            if (Leaf.IsValid)
            {
                if (Owns(key, tree.Comparer))
                {
                    return;
                }

                for (var step = 0; step < 2; step++)
                {
                    var next = Leaf.GetNext(ref accessor);
                    if (!next.IsValid)
                    {
                        break;
                    }

                    Load(next, ref accessor);
                    if (Owns(key, tree.Comparer))
                    {
                        return;
                    }
                }
            }

            Load(tree.FindLeaf(key, out _, ref accessor, followRightLink: false), ref accessor);
        }

        private void Load(NodeWrapper leaf, ref ChunkAccessor<TStore> accessor)
        {
            Leaf = leaf;
            // A leaf with a right sibling is bounded by its HighKey even when empty: KeyAboveLeafUpperBound's count test would make an empty leaf own every
            // later key. None exists outside a concurrent merge, which EW-01 rules out; bounded, the cursor steps past one.
            _bounded = leaf.IsValid && leaf.GetNext(ref accessor).IsValid;
            _high = _bounded ? leaf.GetHighKey(ref accessor) : default;
        }
    }

    /// <remarks>Touches only the leaves its range owns, and writes only its range's state and its arrivals' new element ids, so ranges of one batch run
    /// concurrently. The new ids go to <see cref="_moveNewElementIds"/>, not to the moves: another range may still be gathering its departures' element ids
    /// from them.</remarks>
    private void BulkMultiMoves(MoveRangeState state, ref IndexMoveRange range, ref ChunkAccessor<TStore> opAccessor, ref ChunkAccessor<TStore> sibAccessor)
    {
        var events = state.Events.AsSpan(0, state.Count);
        var deferredList = state.Deferred;
        var newElementIds = _moveNewElementIds;
        ref var outcome = ref range.Outcome;
        var deferred = 0;             // events (by index) for the single-entry path: arrivals at a key that does not exist, departures from one that does not
        var emptied = state.Count;    // keys whose buffer the batch emptied: the run's first event, listed back from the list's end
        var cursor = new LeafCursor();
        NodeWrapper leaf = default;
        var leafChanged = false;
        var i = 0;
        while (i < events.Length)
        {
            var key = events[i].Key;
            var end = i + 1;
            while (end < events.Length && CompareKeys(events[end].Key, key, Comparer) == 0)
            {
                end++;
            }

            cursor.Seek(this, key, ref opAccessor);
            var target = cursor.Leaf;
            if (!target.IsValid)
            {
                // An empty tree: every arrival creates its key, every departure finds nothing.
                for (var e = i; e < end; e++)
                {
                    deferredList[deferred++] = e;
                }

                i = end;
                continue;
            }

            if (target.ChunkId != leaf.ChunkId)
            {
                if (state.HasHigh && !cursor.EndsBy(state.High, Comparer))
                {
                    ThrowRangeCrossesItsCut();
                }

                BumpLeafVersion(leaf, leafChanged, ref opAccessor);
                leaf = target;
                leafChanged = false;
            }

            if (!_storage.TryFindValueInLeaf(leaf, key, Comparer, out var bufferId, ref opAccessor))
            {
                for (var e = i; e < end; e++)
                {
                    deferredList[deferred++] = e;
                }

                i = end;
                continue;
            }

            // Departures come first in the run (stable sort): [i, firstArrival) leave, [firstArrival, end) arrive.
            var firstArrival = i;
            while (firstArrival < end && !events[firstArrival].Arrives)
            {
                firstArrival++;
            }

            var d = i;
            var a = firstArrival;
            for (; d < firstArrival && a < end; d++, a++)
            {
                ref var dep = ref events[d];
                ref var arr = ref events[a];
                if (_storage.UpdateInBuffer(bufferId, dep.ElementId, dep.Value, arr.Value, ref sibAccessor))
                {
                    newElementIds[arr.Move] = dep.ElementId;   // the arriving entity takes the departing one's element
                }
                else
                {
                    outcome.Misses++;   // the departing element is not where its id says
                    newElementIds[arr.Move] = _storage.Append(bufferId, arr.Value, ref sibAccessor);
                }

                leafChanged = true;
            }

            var remaining = -1;
            for (; d < firstArrival; d++)
            {
                ref var dep = ref events[d];
                var left = _storage.RemoveFromBuffer(bufferId, dep.ElementId, dep.Value, ref sibAccessor);
                if (left < 0)
                {
                    outcome.Misses++;
                }
                else
                {
                    remaining = left;
                }

                leafChanged = true;
            }

            for (; a < end; a++)
            {
                newElementIds[events[a].Move] = _storage.Append(bufferId, events[a].Value, ref sibAccessor);
                leafChanged = true;
            }

            if (remaining == 0)
            {
                deferredList[--emptied] = i;   // the run's first event names the key
            }

            i = end;
        }

        BumpLeafVersion(leaf, leafChanged, ref opAccessor);
        range.Deferred = deferred;
        range.Emptied = state.Count - emptied;
    }

    /// <summary>
    /// After <see cref="BulkMultiMoves"/>: each emptied key leaves the tree if still empty, then each arrival at a key that did not exist creates it — or
    /// joins the one an earlier arrival created — and a departure from a key that does not exist is a miss.
    /// </summary>
    private void DeferredMultiMoves(Span<IndexMove> moves, Span<IndexMoveRange> ranges, ref IndexMoveOutcome outcome, ref ChunkAccessor<TStore> accessor)
    {
        // Every range has applied: no one gathers from the moves any more. An arrival the bulk pass deferred is overwritten by its Add below.
        var newElementIds = _moveNewElementIds;
        for (var m = 0; m < moves.Length; m++)
        {
            moves[m].ElementId = newElementIds[m];
        }

        var anyEmptied = false;
        for (var r = 0; r < ranges.Length; r++)
        {
            anyEmptied |= ranges[r].Emptied > 0;
        }

        if (anyEmptied)
        {
            ref var opAccessor = ref _segment.RentWarmAccessor(accessor.ChangeSet);
            ref var sibAccessor = ref _segment.RentWarmSiblingAccessor(accessor.ChangeSet);
            try
            {
                for (var r = 0; r < ranges.Length; r++)
                {
                    var state = _moveRanges[ranges[r].Slot];
                    for (var k = 0; k < ranges[r].Emptied; k++)
                    {
                        RemoveKeyIfBufferStillEmpty(state.Events[state.Deferred[state.Count - 1 - k]].Key, ref opAccessor, ref sibAccessor);
                    }
                }
            }
            finally
            {
                _segment.ReturnWarmSiblingAccessor();
                _segment.ReturnWarmAccessor();
            }
        }

        for (var r = 0; r < ranges.Length; r++)
        {
            var state = _moveRanges[ranges[r].Slot];
            for (var k = 0; k < ranges[r].Deferred; k++)
            {
                ref var ev = ref state.Events[state.Deferred[k]];
                if (ev.Arrives)
                {
                    moves[ev.Move].ElementId = Add(ev.Key, ev.Value, ref accessor);
                }
                else
                {
                    outcome.Misses++;
                }
            }
        }
    }

    /// <remarks>Decides and applies its range alone: the range owns its leaves, and every event of one key falls in one range, so the decision on the final
    /// state needs nothing outside it. Ranges of one batch run concurrently.</remarks>
    private void BulkUniqueMoves(MoveRangeState state, ref IndexMoveRange range, ref ChunkAccessor<TStore> opAccessor)
    {
        var events = state.Events.AsSpan(0, state.Count);
        var deferredList = state.Deferred;
        var deferred = 0;   // events of leaves the bulk path cannot rewrite in place, for the single-entry path
        DecideUniqueCollisions(events, ref range.Outcome, ref opAccessor);

        var capacity = _storage.GetNodeCapacity();
        var leafMerge = t_leafMerge;
        if (leafMerge == null || leafMerge.Length < capacity)
        {
            t_leafMerge = leafMerge = new KeyValueItem[capacity];
        }

        var cursor = new LeafCursor();
        var i = 0;
        while (i < events.Length)
        {
            cursor.Seek(this, events[i].Key, ref opAccessor);
            var leaf = cursor.Leaf;
            if (!leaf.IsValid)
            {
                for (; i < events.Length; i++)
                {
                    if (events[i].Move >= 0)
                    {
                        deferredList[deferred++] = i;   // an empty tree: arrivals insert
                    }
                }

                break;
            }

            if (state.HasHigh && !cursor.EndsBy(state.High, Comparer))
            {
                ThrowRangeCrossesItsCut();
            }

            var end = i + 1;
            while (end < events.Length && cursor.Owns(events[end].Key, Comparer))
            {
                end++;
            }

            var removes = 0;
            var inserts = 0;
            for (var e = i; e < end; e++)
            {
                if (events[e].Move >= 0)
                {
                    if (events[e].Arrives)
                    {
                        inserts++;
                    }
                    else
                    {
                        removes++;
                    }
                }
            }

            var count = leaf.GetCount(ref opAccessor);
            var after = count - removes + inserts;
            var inPlace = after <= capacity && (after >= capacity / 2 || Root == leaf) && after > 0;
            var latch = leaf.GetLatch(ref opAccessor);
            if (inPlace)
            {
                leaf.PreDirtyForWrite(ref opAccessor);   // faulted in and dirty before the latch, as every write path (#1321)
            }

            if (inPlace && latch.TryWriteLock())
            {
                if (MergeLeaf(leaf, count, events.Slice(i, end - i), after, leafMerge.AsSpan(0, capacity), ref opAccessor))
                {
                    latch.WriteUnlock();
                    Interlocked.Add(ref _count, inserts - removes);
                }
                else
                {
                    latch.AbortWriteLock();   // nothing written: the leaf disagreed with the decision; its events take the single-entry path
                    for (var e = i; e < end; e++)
                    {
                        if (events[e].Move >= 0)
                        {
                            deferredList[deferred++] = e;
                        }
                    }
                }
            }
            else
            {
                for (var e = i; e < end; e++)
                {
                    if (events[e].Move >= 0)
                    {
                        deferredList[deferred++] = e;
                    }
                }
            }

            i = end;
        }

        range.Deferred = deferred;
    }

    /// <summary>
    /// Decides, before anything changes, every unique move against the batch's final state, in one ascending pass, and marks the events to skip
    /// (<c>Move</c> complemented): a departure that does not hold its key — the key absent, or held by another entity — is a miss (its arrival still
    /// applies); an arrival at a key taken twice in the batch, or held by an entity that stays, collides.
    /// </summary>
    /// <remarks>
    /// <para>Only the departure whose value is the key's holder vacates it. Two departures from one key are reachable once an index disagrees with its data,
    /// and treating both as vacating re-inserted one of them in the leaf merge (review 2).</para>
    /// <para><b>A collided move still leaves its old key</b> (#1346): its data no longer holds that key, so an index naming it there would answer a query
    /// with a row its value fails. It is not indexed under the new key either, which the holder keeps — a unique index holds one entity per key, and the
    /// data holding two is the violation #675 raises. It is indexed again by its next key-changing write. Keeping the old key instead (what a refused
    /// single <c>Move</c> left) would also un-vacate it for any arrival there — a cascade — and leave a stale entry that only a re-marked record could
    /// cover until the fence after; re-marking proved worse (review 3: a record left on a slot its entity migrates away from in the same fence, and a second
    /// move of a Commit-maintained slot, #711).</para>
    /// </remarks>
    private void DecideUniqueCollisions(Span<MoveEvent> events, ref IndexMoveOutcome outcome, ref ChunkAccessor<TStore> opAccessor)
    {
        var cursor = new LeafCursor();
        var i = 0;
        while (i < events.Length)
        {
            var key = events[i].Key;
            var end = i + 1;
            while (end < events.Length && CompareKeys(events[end].Key, key, Comparer) == 0)
            {
                end++;
            }

            cursor.Seek(this, key, ref opAccessor);
            var holder = -1;   // the value the tree holds under the key, or -1 when it is absent (values are cluster locations, never negative)
            if (cursor.Leaf.IsValid)
            {
                var at = cursor.Leaf.Find(key, Comparer, ref opAccessor);
                if (at >= 0)
                {
                    holder = cursor.Leaf.GetItem(at, ref opAccessor).Value;
                }
            }

            // Departures first in the run (stable sort). The key is free when absent, or vacated by its holder's departure; the first arrival takes it.
            var free = holder < 0;
            var e = i;
            for (; e < end && !events[e].Arrives; e++)
            {
                if (holder >= 0 && events[e].Value == holder)
                {
                    free = true;
                }
                else
                {
                    events[e].Move = ~events[e].Move;   // not the key's holder: nothing of this entity to remove — a miss
                    outcome.Misses++;
                }
            }

            for (; e < end; e++)
            {
                if (free)
                {
                    free = false;   // taken by this arrival
                    continue;
                }

                NoteCollision(ref outcome, events[e].Move);
                events[e].Move = ~events[e].Move;
            }

            i = end;
        }
    }

    /// <summary>
    /// After <see cref="BulkUniqueMoves"/>, the leaves that could not change in place: their departures first, then their arrivals — the same final-state
    /// order the bulk path keeps.
    /// </summary>
    private void DeferredUniqueMoves(Span<IndexMoveRange> ranges, ref IndexMoveOutcome outcome, ref ChunkAccessor<TStore> accessor)
    {
        for (var r = 0; r < ranges.Length; r++)
        {
            var state = _moveRanges[ranges[r].Slot];
            for (var k = 0; k < ranges[r].Deferred; k++)
            {
                ref var ev = ref state.Events[state.Deferred[k]];
                if (!ev.Arrives && !Remove(ev.Key, out _, ref accessor))
                {
                    outcome.Misses++;
                }
            }
        }

        for (var r = 0; r < ranges.Length; r++)
        {
            var state = _moveRanges[ranges[r].Slot];
            for (var k = 0; k < ranges[r].Deferred; k++)
            {
                ref var ev = ref state.Events[state.Deferred[k]];
                if (!ev.Arrives)
                {
                    continue;
                }

                try
                {
                    Add(ev.Key, ev.Value, ref accessor);
                }
                catch (UniqueConstraintViolationException)
                {
                    NoteCollision(ref outcome, ev.Move);
                }
            }
        }
    }

    private static void NoteCollision(ref IndexMoveOutcome outcome, int move)
    {
        if (outcome.Collisions++ == 0)
        {
            outcome.FirstCollision = move;
        }
    }

    /// <summary>
    /// Rewrites <paramref name="leaf"/>'s items in one merge of what it holds with its run of events (ascending; for one key the departure first): an item
    /// whose key departs is dropped, an arrival is inserted in order. One read and one write per item, where a find and an item shift per event cost several.
    /// The leaf is latched and stays within its capacity and above half full: nothing structural changes.
    /// </summary>
    /// <returns>False, with nothing written, when a departure finds no item holding it, the merge would overflow, or the count differs from the one the
    /// decision computed: the leaf disagrees with the decision, and the caller sends its events through the single-entry path. An arrival onto a held key
    /// is not detected here — the decision rules it out.</returns>
    private bool MergeLeaf(NodeWrapper leaf, int count, Span<MoveEvent> run, int after, Span<KeyValueItem> merged, ref ChunkAccessor<TStore> accessor)
    {
        var capacity = merged.Length;
        var n = 0;
        var e = 0;
        for (var idx = 0; idx < count; idx++)
        {
            var item = leaf.GetItem(idx, ref accessor);
            for (; e < run.Length && (run[e].Move < 0 || CompareKeys(run[e].Key, item.Key, Comparer) < 0); e++)
            {
                if (run[e].Move >= 0)
                {
                    if (!run[e].Arrives || n == capacity)
                    {
                        return false;   // a departure below this item found nothing, or the leaf would overflow
                    }

                    merged[n++] = new KeyValueItem(run[e].Key, run[e].Value);   // an arrival below this item
                }
            }

            if (e < run.Length && !run[e].Arrives && CompareKeys(run[e].Key, item.Key, Comparer) == 0)
            {
                if (run[e].Value != item.Value)
                {
                    return false;   // decided the holder's departure, but the item holds another entity
                }

                e++;   // this item departs; an arrival at its key, if any, follows and is merged before the next item
                continue;
            }

            if (n == capacity)
            {
                return false;
            }

            merged[n++] = item;
        }

        for (; e < run.Length; e++)
        {
            if (run[e].Move >= 0)
            {
                if (!run[e].Arrives || n == capacity)
                {
                    return false;
                }

                merged[n++] = new KeyValueItem(run[e].Key, run[e].Value);
            }
        }

        if (n != after)
        {
            return false;
        }

        _storage.SetStart(leaf, 0, ref accessor);
        for (var k = 0; k < n; k++)
        {
            _storage.SetItem(leaf, k, merged[k], false, ref accessor);
        }

        _storage.SetCount(leaf, n, ref accessor);
        return true;
    }

    /// <summary>Latches and releases a leaf the bulk path changed, so its version moves for readers that revalidate against it (IXS-02, IXS-09).</summary>
    private void BumpLeafVersion(NodeWrapper leaf, bool changed, ref ChunkAccessor<TStore> accessor)
    {
        if (!changed || !leaf.IsValid)
        {
            return;
        }

        var latch = leaf.GetLatch(ref accessor);
        if (latch.TryWriteLock())
        {
            latch.WriteUnlock();
        }
    }
}
