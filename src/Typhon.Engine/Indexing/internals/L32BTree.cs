// CS1591: this file declares public-accessibility types that live in the internal namespace (Phase 2b entanglement, see
// claude/research/PublicVsInternalApiClassification.md). They are excluded from the published API reference, so consumer-facing
// doc coverage is not enforced here.
#pragma warning disable 1591

// unset

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Threading;

namespace Typhon.Engine.Internals;

[DebuggerTypeProxy(typeof(DebugView))]
[DebuggerDisplay("Count: {Count}, Start: {Start}, Flags: {StateFlags}")]
[StructLayout(LayoutKind.Sequential, Pack = 4)]
unsafe public struct Index32Chunk
{
    // 256 bytes — fills four cache lines. Adjacent Line Prefetcher (ALP) on Zen 4+/recent Intel automatically
    // fetches paired 64-byte lines within 128-byte regions, so two ALP triggers cover the full node.

    public const int Capacity = 29;

    // State flags (LSW 16bits), Position of the first Item, aka Start (8bits), stored Item Count (8bits)
    public int Control;
    public int OlcVersion;    // OLC latch: bit 0 = locked, bit 1 = obsolete, bits 2-31 = version (30 bits)
    public int PrevChunk;
    public int NextChunk;
    public int LeftValue;
    public int HighKey;                             // B-link upper bound — co-located with OlcVersion in first 128B region
    public fixed int Values[Capacity];              // 29 × 4 = 116 bytes
    public fixed int Keys[Capacity];                // 29 × 4 = 116 bytes

    public Span<int> KeysAsSpan
    {
        get
        {
            fixed (int* k = Keys)
            {
                return new Span<int>(k, Capacity);
            }
        }
    }

    public Span<int> ValuesAsSpan
    {
        get
        {
            fixed (int* v = Values)
            {
                return new Span<int>(v, Capacity);
            }
        }
    }

    public int Count
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
        get
        {
            fixed (int* c = &Control)
            {
                return ((byte*)c)[3];
            }
        }
        [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
        set
        {
            fixed (int* c = &Control)
            {
                ((byte*)c)[3] = (byte)value;
            }
        }
    }

    public int Start
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
        get
        {
            fixed (int* c = &Control)
            {
                return ((byte*)c)[2];
            }
        }
        [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
        set
        {
            fixed (int* c = &Control)
            {
                ((byte*)c)[2] = (byte)value;
            }
        }
    }

    public int ContentionHint
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
        get
        {
            fixed (int* c = &Control)
            {
                return ((byte*)c)[1];
            }
        }
        [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
        set
        {
            fixed (int* c = &Control)
            {
                ((byte*)c)[1] = (byte)value;
            }
        }
    }

    public int End => Adjust(Start + Count);
    public NodeStates StateFlags
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
        get
        {
            fixed (int* c = &Control)
            {
                return (NodeStates)((short*)c)[0];
            }
        }
        [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
        set
        {
            fixed (int* c = &Control)
            {
                ((short*)c)[0] = (short)value;
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    public bool TryLock() => (Interlocked.Or(ref OlcVersion, 1) & 1) == 0;
    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    public void FreeLock() => Interlocked.And(ref OlcVersion, ~1);
    public bool IsLocked => (OlcVersion & 1) != 0;
    public bool IsRotated => (Start + Count) > Capacity;

    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    public static int Adjust(int index) => (index < 0 || index >= Capacity) ? (index + Capacity * (-index).Sign()) : index;


    [ExcludeFromCodeCoverage]
    [DebuggerNonUserCode]
    private sealed class DebugView
    {
        private readonly Index32Chunk _chunk;

        public DebugView(Index32Chunk chunk)
        {
            _chunk = chunk;
        }

        public int Previous => _chunk.PrevChunk;
        public int Next => _chunk.NextChunk;
        public int LeftValue => _chunk.LeftValue;

        public ValueTuple<int, int>[] KeyValuePairs
        {
            get
            {
                var k = _chunk.KeysAsSpan;
                var v = _chunk.ValuesAsSpan;

                var s = _chunk.Start;
                var count = _chunk.Count;
                var res = new ValueTuple<int, int>[count];
                for (int i = 0; i < count; i++)
                {
                    var ii = Adjust(s + i);
                    res[i] = new ValueTuple<int, int>(k[ii], v[ii]);
                }

                return res;
            }
        }
    }
}

internal abstract class L32BTree<TKey, TStore> : BTree<TKey, TStore> where TStore : struct, IPageStore where TKey : unmanaged
{
    unsafe public class L32NodeStorage : BaseNodeStorage
    {
        internal override void Initialize(BTree<TKey, TStore> owner, ChunkBasedSegment<TStore> segment)
        {
            base.Initialize(owner, segment);
            Debug.Assert(sizeof(Index32Chunk) == 256);
            Debug.Assert(segment.Stride == sizeof(Index32Chunk));
        }

        #region Chunk Properties Access

        public override void InitializeNode(NodeWrapper node, NodeStates states, ref ChunkAccessor<TStore> accessor)
        {
            ref var chunk = ref accessor.GetChunk<Index32Chunk>(node.ChunkId, true);
            chunk.Control = (int)states;  // Atomically sets StateFlags + Start=0 + Count=0
            chunk.OlcVersion = 4;         // version=1 (bits 2-31), locked=false, obsolete=false — must be non-zero so OLC readers don't see it as locked
            chunk.PrevChunk = 0;
            chunk.NextChunk = 0;
            chunk.LeftValue = 0;
            chunk.HighKey = int.MaxValue;  // B-link upper bound: sentinel for newly allocated nodes
        }
        
        public override int GetNodeCapacity() => Index32Chunk.Capacity;

        public override ref int GetOlcVersionRef(int chunkId, ref ChunkAccessor<TStore> accessor)
        {
            ref var chunk = ref accessor.GetChunk<Index32Chunk>(chunkId);
            return ref chunk.OlcVersion;
        }

        public override NodeWrapper GetLeftNode(NodeWrapper node, ref ChunkAccessor<TStore> accessor)
        {
            ref readonly var chunk = ref accessor.GetChunkReadOnly<Index32Chunk>(node.ChunkId);
            return new NodeWrapper(this, chunk.LeftValue);
        }

        public override void SetLeftNode(NodeWrapper node, int previousNodeId, ref ChunkAccessor<TStore> accessor)
        {
            ref var chunk = ref accessor.GetChunk<Index32Chunk>(node.ChunkId, true);
            chunk.LeftValue = previousNodeId;
        }

        public override NodeWrapper GetPreviousNode(NodeWrapper node, ref ChunkAccessor<TStore> accessor)
        {
            ref readonly var chunk = ref accessor.GetChunkReadOnly<Index32Chunk>(node.ChunkId);
            return new NodeWrapper(this, chunk.PrevChunk);
        }

        public override void SetPreviousNode(NodeWrapper node, int previousNodeId, ref ChunkAccessor<TStore> accessor)
        {
            ref var chunk = ref accessor.GetChunk<Index32Chunk>(node.ChunkId, true);
            chunk.PrevChunk = previousNodeId;
        }

        public override NodeWrapper GetNextNode(NodeWrapper node, ref ChunkAccessor<TStore> accessor)
        {
            ref readonly var chunk = ref accessor.GetChunkReadOnly<Index32Chunk>(node.ChunkId);
            return new NodeWrapper(this, chunk.NextChunk);
        }

        public override void SetNextNode(NodeWrapper node, int nextNodeId, ref ChunkAccessor<TStore> accessor)
        {
            ref var chunk = ref accessor.GetChunk<Index32Chunk>(node.ChunkId, true);
            chunk.NextChunk = nextNodeId;
        }

        public override KeyValueItem GetItem(NodeWrapper node, int index, bool adjust, ref ChunkAccessor<TStore> accessor)
        {
            ref readonly var chunk = ref accessor.GetChunkReadOnly<Index32Chunk>(node.ChunkId);
            var i = adjust ? Index32Chunk.Adjust(chunk.Start + index) : index;
            var key = chunk.Keys[i];
            return new KeyValueItem(*(TKey*)&key, chunk.Values[i]);
        }

        public override void SetValueOnly(NodeWrapper node, int index, int value, ref ChunkAccessor<TStore> accessor)
        {
            ref var chunk = ref accessor.GetChunk<Index32Chunk>(node.ChunkId, true);
            Volatile.Write(ref chunk.Values[Index32Chunk.Adjust(chunk.Start + index)], value);
        }

        public override bool TryFindValueInLeaf(NodeWrapper node, TKey key, IComparer<TKey> comparer, out int value, ref ChunkAccessor<TStore> accessor)
        {
            if (typeof(TKey) != typeof(int) && typeof(TKey) != typeof(uint))
            {
                return base.TryFindValueInLeaf(node, key, comparer, out value, ref accessor);
            }

            ref readonly var chunk = ref accessor.GetChunkReadOnly<Index32Chunk>(node.ChunkId);
            var start = chunk.Start;
            int index;
            fixed (int* keys = chunk.Keys)
            {
                index = typeof(TKey) == typeof(int)
                    ? SimdSearch(keys, start, chunk.Count, *(int*)&key)
                    : SimdSearchUnsigned((uint*)keys, start, chunk.Count, *(uint*)&key);
            }

            if (index < 0)
            {
                value = 0;
                return false;
            }

            value = chunk.Values[Index32Chunk.Adjust(start + index)];
            return true;
        }

        public override void ReadNodeHeader(NodeWrapper node, out bool isLeaf, out int count, ref ChunkAccessor<TStore> accessor)
        {
            ref readonly var chunk = ref accessor.GetChunkReadOnly<Index32Chunk>(node.ChunkId);
            isLeaf = (chunk.StateFlags & NodeStates.IsLeaf) != 0;
            count = chunk.Count;
        }

        public override int FindChildAndBound(NodeWrapper node, TKey key, int count, IComparer<TKey> comparer, out int childChunkId, out TKey rightBound,
            out bool hasRightBound, ref ChunkAccessor<TStore> accessor)
        {
            if (typeof(TKey) != typeof(int) && typeof(TKey) != typeof(uint))
            {
                return base.FindChildAndBound(node, key, count, comparer, out childChunkId, out rightBound, out hasRightBound, ref accessor);
            }

            // The search, the child pointer and the right bound all live in this one chunk. Reaching them through BinarySearch, GetChild and GetItem costs
            // THREE resolutions of the same chunk id, per child descended into, at every level - which is most of what the bulk descent does.
            ref readonly var chunk = ref accessor.GetChunkReadOnly<Index32Chunk>(node.ChunkId);
            // GetChild returns an INVALID node for a leaf, and this override must not quietly lose that: a leaf's value slots hold ClusterLocations, so
            // without the check a caller that reached a leaf would be handed one as a chunk id and descend into it. The flag is in the chunk already being
            // read, so the guard costs one AND and a predictable branch. Chunk id 0 is not a valid node, so a caller that ignores the -1 still fails loudly.
            if ((chunk.StateFlags & NodeStates.IsLeaf) != 0)
            {
                childChunkId = 0;
                rightBound = default;
                hasRightBound = false;
                return -1;
            }

            var start = chunk.Start;
            int childIndex;
            fixed (int* keys = chunk.Keys)
            {
                childIndex = typeof(TKey) == typeof(int)
                    ? SimdSearch(keys, start, chunk.Count, *(int*)&key)
                    : SimdSearchUnsigned((uint*)keys, start, chunk.Count, *(uint*)&key);
            }

            if (childIndex < 0)
            {
                childIndex = ~childIndex - 1;
            }

            childChunkId = childIndex < 0 ? chunk.LeftValue : chunk.Values[Index32Chunk.Adjust(start + childIndex)];

            hasRightBound = childIndex + 1 < count;
            if (hasRightBound)
            {
                var bound = chunk.Keys[Index32Chunk.Adjust(start + childIndex + 1)];
                rightBound = *(TKey*)&bound;
            }
            else
            {
                rightBound = default;
            }

            return childIndex;
        }

        /// <summary>
        /// #872 AC-5.5 — one chunk resolution, one <c>fixed</c>, and one pass over the sub-batch, instead of two resolutions per entry.
        /// </summary>
        /// <remarks>
        /// <c>Start</c> and <c>Count</c> are read once and are safe to hoist: this operation writes only the value array, so nothing in the loop can move an
        /// item, change the count or rotate the node. Non-SIMD key types fall back to the base implementation rather than duplicating the rotated-node
        /// comparer search, which is the part of <see cref="BinarySearch"/> most easily got wrong.
        /// </remarks>
        public override int ApplyValuesInLeaf(
            NodeWrapper node,
            ReadOnlySpan<BTreeValueUpdate<TKey>> batch,
            IComparer<TKey> comparer,
            ref ChunkAccessor<TStore> accessor)
        {
            if (typeof(TKey) != typeof(int) && typeof(TKey) != typeof(uint))
            {
                return base.ApplyValuesInLeaf(node, batch, comparer, ref accessor);
            }

            // Resolved WITHOUT the dirty flag, and the page is marked only when a value is actually written. GetChunkAddress(id, true) marks the slot dirty
            // unconditionally, so dirtying here would charge a page write and an ACW (which blocks the checkpoint on that page) to a leaf the batch turns out
            // not to touch at all - the documented "absent keys are skipped" case. The base implementation dirties through SetValueOnly, i.e. only on a real
            // write, and an override of the same virtual must not have a different side effect. PreDirtyChunk re-resolves the same id in the same accessor,
            // so `chunk` stays valid across it.
            ref var chunk = ref accessor.GetChunk<Index32Chunk>(node.ChunkId);
            var start = chunk.Start;
            var count = chunk.Count;
            if (count == 0)
            {
                return 0;
            }

            var applied = 0;
            var dirtied = false;

            fixed (int* keys = chunk.Keys)
            {
                // DENSE sub-batch: merge. Both sides are sorted ascending, so a cursor walked through the leaf costs about one comparison per entry, against
                // a full search per entry. A re-clustering batch lands its whole cell in a handful of leaves, which is precisely this case (#872 AC-5.5).
                //
                // SPARSE sub-batch: search. The merge's cursor has to scan PAST every leaf slot it skips, so one entry landing in a 29-slot leaf would cost 29
                // comparisons where the vectorised search costs about three. The scattered case is already the weaker one; do not make it worse to help the
                // clustered one. The 4x threshold is where the two costs cross for this capacity, not a tuned constant.
                if (batch.Length * 4 >= count)
                {
                    var j = 0;
                    for (var i = 0; i < batch.Length; i++)
                    {
                        var probe = batch[i].Key;
                        int phys;

                        if (typeof(TKey) == typeof(int))
                        {
                            var key = *(int*)&probe;
                            while (j < count && keys[Wrap(start + j)] < key)
                            {
                                j++;
                            }

                            if (j >= count)
                            {
                                break;   // every remaining entry sorts at or above this one, and the leaf is exhausted: all absent
                            }

                            phys = Wrap(start + j);
                            if (keys[phys] != key)
                            {
                                continue;
                            }
                        }
                        else
                        {
                            var key = *(uint*)&probe;
                            while (j < count && (uint)keys[Wrap(start + j)] < key)
                            {
                                j++;
                            }

                            if (j >= count)
                            {
                                break;
                            }

                            phys = Wrap(start + j);
                            if ((uint)keys[phys] != key)
                            {
                                continue;
                            }
                        }

                        // The cursor is NOT advanced past a match: a batch may legitimately carry the same key twice, and both entries address this slot.
                        if (!dirtied)
                        {
                            accessor.PreDirtyChunk(node.ChunkId);
                            dirtied = true;
                        }

                        Volatile.Write(ref chunk.Values[phys], batch[i].NewValue);
                        applied++;
                    }

                    return applied;
                }

                for (var i = 0; i < batch.Length; i++)
                {
                    var key = batch[i].Key;
                    var index = typeof(TKey) == typeof(int)
                        ? SimdSearch(keys, start, count, *(int*)&key)
                        : SimdSearchUnsigned((uint*)keys, start, count, *(uint*)&key);

                    if (index < 0)
                    {
                        continue;
                    }

                    if (!dirtied)
                    {
                        accessor.PreDirtyChunk(node.ChunkId);
                        dirtied = true;
                    }

                    Volatile.Write(ref chunk.Values[Index32Chunk.Adjust(start + index)], batch[i].NewValue);
                    applied++;
                }
            }

            return applied;
        }

        /// <summary>Ring-buffer wrap for an index already known to be in <c>[0, 2 * Capacity)</c> — one compare and a subtract, no sign handling.</summary>
        /// <remarks>
        /// <see cref="Index32Chunk.Adjust"/> is the general form and handles negatives, which costs it a <c>Sign()</c> call and a second branch. Inside the
        /// merge the index only ever walks forward from <c>Start</c>, so the general form is paid for nothing on every slot touched.
        /// </remarks>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int Wrap(int index) => index >= Index32Chunk.Capacity ? index - Index32Chunk.Capacity : index;

        public override void SetItem(NodeWrapper node, int index, KeyValueItem value, bool adjust, ref ChunkAccessor<TStore> accessor)
        {
            ref var chunk = ref accessor.GetChunk<Index32Chunk>(node.ChunkId, true);
            Set(ref chunk, index, value, adjust);
        }

        public override int GetCount(NodeWrapper node, ref ChunkAccessor<TStore> accessor)
        {
            ref readonly var chunk = ref accessor.GetChunkReadOnly<Index32Chunk>(node.ChunkId);
            return chunk.Count;
        }

        public override void SetCount(NodeWrapper node, int value, ref ChunkAccessor<TStore> accessor)
        {
            ref var chunk = ref accessor.GetChunk<Index32Chunk>(node.ChunkId, true);
            chunk.Count = value;
        }

        public override int GetStart(NodeWrapper node, ref ChunkAccessor<TStore> accessor)
        {
            ref readonly var chunk = ref accessor.GetChunkReadOnly<Index32Chunk>(node.ChunkId);
            return chunk.Start;
        }

        public override void SetStart(NodeWrapper node, int value, ref ChunkAccessor<TStore> accessor)
        {
            ref var chunk = ref accessor.GetChunk<Index32Chunk>(node.ChunkId, true);
            chunk.Start = value;
        }

        public override int GetEnd(NodeWrapper node, ref ChunkAccessor<TStore> accessor)
        {
            ref readonly var chunk = ref accessor.GetChunkReadOnly<Index32Chunk>(node.ChunkId);
            return Index32Chunk.Adjust(chunk.Start + chunk.Count);
        }

        public override NodeStates GetNodeStates(NodeWrapper node, ref ChunkAccessor<TStore> accessor)
        {
            ref readonly var chunk = ref accessor.GetChunkReadOnly<Index32Chunk>(node.ChunkId);
            return chunk.StateFlags;
        }

        public override int GetContentionHint(NodeWrapper node, ref ChunkAccessor<TStore> accessor)
        {
            ref readonly var chunk = ref accessor.GetChunkReadOnly<Index32Chunk>(node.ChunkId);
            return chunk.ContentionHint;
        }

        public override void SetContentionHint(NodeWrapper node, int value, ref ChunkAccessor<TStore> accessor)
        {
            ref var chunk = ref accessor.GetChunk<Index32Chunk>(node.ChunkId, true);
            chunk.ContentionHint = value;
        }

        public override TKey GetHighKey(NodeWrapper node, ref ChunkAccessor<TStore> accessor)
        {
            ref readonly var chunk = ref accessor.GetChunkReadOnly<Index32Chunk>(node.ChunkId);
            int hk = chunk.HighKey;
            return *(TKey*)&hk;
        }

        public override void SetHighKey(NodeWrapper node, TKey key, ref ChunkAccessor<TStore> accessor)
        {
            ref var chunk = ref accessor.GetChunk<Index32Chunk>(node.ChunkId, true);
            chunk.HighKey = *(int*)&key;
        }

        #endregion

        #region Chunk Operations

        public override void PushFirst(NodeWrapper node, KeyValueItem item, ref ChunkAccessor<TStore> accessor)
        {
            ref var chunk = ref accessor.GetChunk<Index32Chunk>(node.ChunkId, true);

            DecrementStart(ref chunk);

            var start = chunk.Start;
            chunk.Keys[start] = *(int*)&item.Key;
            chunk.Values[start] = item.Value;

            ++chunk.Count;
        }

        public override void PushLast(NodeWrapper node, KeyValueItem item, ref ChunkAccessor<TStore> accessor)
        {
            ref var chunk = ref accessor.GetChunk<Index32Chunk>(node.ChunkId, true);
            var c = chunk.Count++;
            Set(ref chunk, c, item, true);
        }

        public override int Append(int bufferId, int value, ref ChunkAccessor<TStore> bufferAccessor) => throw new Exception("Shouldn't be called as key replace is not supported and multi-value neither");

        public override void Insert(NodeWrapper node, int index, KeyValueItem item, ref ChunkAccessor<TStore> accessor)
        {
            ref var chunk = ref accessor.GetChunk<Index32Chunk>(node.ChunkId, true);
            var lsh = index; // length of left shift
            var rsh = chunk.Count - index; // length of right shift

            if (lsh < rsh) // choose least shifts required
            {
                LeftShift(ref chunk, chunk.Start, lsh); // move Start to Start-1
                Set(ref chunk, index - 1, item, true);
                DecrementStart(ref chunk);
            }
            else
            {
                RightShift(ref chunk, Index32Chunk.Adjust(chunk.Start + index), rsh); // move End to End+1
                Set(ref chunk, index, item, true);
            }

            chunk.Count++;
        }

        public override int CreateBuffer(ref ChunkAccessor<TStore> bufferAccessor) => 0;

        public override VariableSizedBufferAccessor<int, TStore> GetBufferReadOnlyAccessor(int bufferId, ref ChunkAccessor<TStore> accessor) => default;
        public override VariableSizedBufferAccessor<int, TStore> GetBufferReadOnlyAccessor(int bufferId) => default;
        public override int RemoveFromBuffer(int bufferId, int elementId, int value, ref ChunkAccessor<TStore> bufferAccessor) => 0;
        public override bool UpdateInBuffer(int bufferId, int elementId, int oldValue, int newValue, ref ChunkAccessor<TStore> bufferAccessor)
            => false;   // unique index: values live in the node, not in a buffer
        public override int BufferElementCount(int bufferId, ref ChunkAccessor<TStore> bufferAccessor) => 0;
        public override void DeleteBuffer(int bufferId, ref ChunkAccessor<TStore> bufferAccessor) { }

        public override NodeWrapper GetFirstChild(NodeWrapper node, ref ChunkAccessor<TStore> accessor)
        {
            ref readonly var chunk = ref accessor.GetChunkReadOnly<Index32Chunk>(node.ChunkId);
            return new NodeWrapper(this, chunk.LeftValue);
        }

        public override bool IsRotated(NodeWrapper node, ref ChunkAccessor<TStore> accessor)
        {
            ref readonly var chunk = ref accessor.GetChunkReadOnly<Index32Chunk>(node.ChunkId);
            return (chunk.Start + chunk.Count) > Index32Chunk.Capacity;
        }

        public override int BinarySearch(NodeWrapper node, TKey key, IComparer<TKey> comparer, ref ChunkAccessor<TStore> accessor)
        {
            ref readonly var chunk = ref accessor.GetChunkReadOnly<Index32Chunk>(node.ChunkId);
            fixed (int* keys = chunk.Keys)
            {
                // SIMD fast path for int keys
                if (typeof(TKey) == typeof(int))
                {
                    return SimdSearch(keys, chunk.Start, chunk.Count, *(int*)&key);
                }

                // SIMD fast path for uint keys (unsigned comparison)
                if (typeof(TKey) == typeof(uint))
                {
                    return SimdSearchUnsigned((uint*)keys, chunk.Start, chunk.Count, *(uint*)&key);
                }

                // Fallback to comparer-based binary search for other types (float)
                bool rotated = (chunk.Start + chunk.Count) > Index32Chunk.Capacity;
                if (rotated)
                {
                    int chunkKey = chunk.Keys[Index32Chunk.Capacity - 1];
                    if (comparer.Compare(key, *(TKey*)&chunkKey) <= 0)
                    {
                        var find = BTreeExtensions.BinarySearch((TKey*)keys, chunk.Start, Index32Chunk.Capacity - chunk.Start, key, comparer, sizeof(int));
                        return find - chunk.Start * find.Sign();
                    }
                    else
                    {
                        var find = BTreeExtensions.BinarySearch((TKey*)keys, 0, chunk.End, key, comparer, sizeof(int));
                        return find + (Index32Chunk.Capacity - chunk.Start) * find.Sign();
                    }
                }
                else
                {
                    var find = BTreeExtensions.BinarySearch((TKey*)keys, chunk.Start, chunk.Count, key, comparer, sizeof(int));
                    return find - chunk.Start * find.Sign();
                }
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
        private static int SimdSearch(int* keys, int start, int count, int searchKey)
        {
            int pos;
            if ((start + count) <= Index32Chunk.Capacity)
            {
                pos = CountLessThan(keys + start, count, searchKey);
                if (pos < count && keys[start + pos] == searchKey)
                {
                    return pos;
                }
            }
            else
            {
                int rightCount = Index32Chunk.Capacity - start;
                pos = CountLessThan(keys + start, rightCount, searchKey)
                    + CountLessThan(keys, count - rightCount, searchKey);
                if (pos < count)
                {
                    int physIdx = start + pos;
                    if (physIdx >= Index32Chunk.Capacity)
                    {
                        physIdx -= Index32Chunk.Capacity;
                    }
                    if (keys[physIdx] == searchKey)
                    {
                        return pos;
                    }
                }
            }
            return ~pos;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
        private static int SimdSearchUnsigned(uint* keys, int start, int count, uint searchKey)
        {
            int pos;
            if ((start + count) <= Index32Chunk.Capacity)
            {
                pos = CountLessThanUnsigned(keys + start, count, searchKey);
                if (pos < count && keys[start + pos] == searchKey)
                {
                    return pos;
                }
            }
            else
            {
                int rightCount = Index32Chunk.Capacity - start;
                pos = CountLessThanUnsigned(keys + start, rightCount, searchKey)
                    + CountLessThanUnsigned(keys, count - rightCount, searchKey);
                if (pos < count)
                {
                    int physIdx = start + pos;
                    if (physIdx >= Index32Chunk.Capacity)
                    {
                        physIdx -= Index32Chunk.Capacity;
                    }
                    if (keys[physIdx] == searchKey)
                    {
                        return pos;
                    }
                }
            }
            return ~pos;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
        private static int CountLessThan(int* keys, int count, int searchKey)
        {
            int pos = 0;
            int i = 0;
            if (Vector256.IsHardwareAccelerated)
            {
                var needle = Vector256.Create(searchKey);
                for (; i + 8 <= count; i += 8)
                {
                    var cmp = Vector256.LessThan(Vector256.Load(keys + i), needle);
                    pos += BitOperations.PopCount(cmp.ExtractMostSignificantBits());
                }
            }
            if (Vector128.IsHardwareAccelerated)
            {
                var needle128 = Vector128.Create(searchKey);
                for (; i + 4 <= count; i += 4)
                {
                    var cmp = Vector128.LessThan(Vector128.Load(keys + i), needle128);
                    pos += BitOperations.PopCount(cmp.ExtractMostSignificantBits());
                }
            }
            for (; i < count; i++)
            {
                pos += keys[i] < searchKey ? 1 : 0;
            }
            return pos;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
        private static int CountLessThanUnsigned(uint* keys, int count, uint searchKey)
        {
            int pos = 0;
            int i = 0;
            if (Vector256.IsHardwareAccelerated)
            {
                var needle = Vector256.Create(searchKey);
                for (; i + 8 <= count; i += 8)
                {
                    var cmp = Vector256.LessThan(Vector256.Load(keys + i), needle);
                    pos += BitOperations.PopCount(cmp.ExtractMostSignificantBits());
                }
            }
            if (Vector128.IsHardwareAccelerated)
            {
                var needle128 = Vector128.Create(searchKey);
                for (; i + 4 <= count; i += 4)
                {
                    var cmp = Vector128.LessThan(Vector128.Load(keys + i), needle128);
                    pos += BitOperations.PopCount(cmp.ExtractMostSignificantBits());
                }
            }
            for (; i < count; i++)
            {
                pos += keys[i] < searchKey ? 1 : 0;
            }
            return pos;
        }

        public override NodeWrapper SplitRight(NodeWrapper node, NodeStates states, ref ChunkAccessor<TStore> accessor) 
            => SplitRight(node.ChunkId, states, ref accessor);

        public override KeyValueItem RemoveAt(NodeWrapper node, int index, ref ChunkAccessor<TStore> accessor)
        {
            ref var chunk = ref accessor.GetChunk<Index32Chunk>(node.ChunkId, true);
            var item = GetItem(node, index, true, ref accessor);

            var lsh = chunk.Count - index - 1; // length of left shift
            var rsh = index; // length of right shift

            if (rsh < lsh) // choose least shifts required
            {
                RightShift(ref chunk, chunk.Start, rsh); // move Start to Start+1
                Set(ref chunk, chunk.Start, default, false);
                IncrementStart(ref chunk);
            }
            else
            {
                LeftShift(ref chunk, Index32Chunk.Adjust(chunk.Start + index + 1), lsh); // move End to End-1
                Set(ref chunk, chunk.Count - 1, default, true); // remove last item
            }

            chunk.Count--;
            return item;
        }

        public override void MergeLeft(NodeWrapper left, NodeWrapper right, ref ChunkAccessor<TStore> accessor)
        {
            ref var leftChunk = ref accessor.GetChunk<Index32Chunk>(left.ChunkId, true);
            ref var rightChunk = ref accessor.GetChunk<Index32Chunk>(right.ChunkId, true);

            var lk = leftChunk.KeysAsSpan;
            var lv = leftChunk.ValuesAsSpan;
            var rk = rightChunk.KeysAsSpan;
            var rv = rightChunk.ValuesAsSpan;

            if (leftChunk.Count + right.GetCount(ref accessor) > Index32Chunk.Capacity)
            {
                throw new InvalidOperationException("can not merge, there is not enough capacity for this array.");
            }

            var end = leftChunk.Start + leftChunk.Count;

            if (leftChunk.IsRotated)
            {
                var start = end - Index32Chunk.Capacity;

                if (!rightChunk.IsRotated)
                {
                    rk.Slice(right.GetStart(ref accessor), right.GetCount(ref accessor)).CopyTo(lk.Slice(start, right.GetCount(ref accessor)));
                    rv.Slice(right.GetStart(ref accessor), right.GetCount(ref accessor)).CopyTo(lv.Slice(start, right.GetCount(ref accessor)));
                }
                else
                {
                    var srLen = right.GetCapacity() - right.GetStart(ref accessor); // right length
                    var slLen = right.GetCount(ref accessor) - srLen; // left length (remaining)

                    rk.Slice(right.GetStart(ref accessor), srLen).CopyTo(lk.Slice(start, srLen));
                    rv.Slice(right.GetStart(ref accessor), srLen).CopyTo(lv.Slice(start, srLen));

                    rk.Slice(0, slLen).CopyTo(lk.Slice(start + srLen, slLen));
                    rv.Slice(0, slLen).CopyTo(lv.Slice(start + srLen, slLen));
                }
            }
            else
            {
                bool copyIsOnePiece = end + right.GetCount(ref accessor) <= Index32Chunk.Capacity;

                if (!rightChunk.IsRotated)
                {
                    if (copyIsOnePiece)
                    {
                        rk.Slice(right.GetStart(ref accessor), right.GetCount(ref accessor)).CopyTo(lk.Slice(end, right.GetCount(ref accessor)));
                        rv.Slice(right.GetStart(ref accessor), right.GetCount(ref accessor)).CopyTo(lv.Slice(end, right.GetCount(ref accessor)));
                    }
                    else
                    {
                        var length = Index32Chunk.Capacity - end;
                        var remaining = right.GetCount(ref accessor) - length;

                        rk.Slice(right.GetStart(ref accessor), length).CopyTo(lk.Slice(end, length));
                        rk.Slice(right.GetStart(ref accessor) + length, remaining).CopyTo(lk.Slice(0, remaining));

                        rv.Slice(right.GetStart(ref accessor), length).CopyTo(lv.Slice(end, length));
                        rv.Slice(right.GetStart(ref accessor) + length, remaining).CopyTo(lv.Slice(0, remaining));
                    }
                }
                else
                {
                    var srLen = right.GetCapacity() - right.GetStart(ref accessor); // right length
                    var slLen = right.GetCount(ref accessor) - srLen; // left length (remaining)

                    if (copyIsOnePiece)
                    {
                        rk.Slice(right.GetStart(ref accessor), srLen).CopyTo(lk.Slice(end, srLen));
                        rk.Slice(0, slLen).CopyTo(lk.Slice(end + srLen, slLen));

                        rv.Slice(right.GetStart(ref accessor), srLen).CopyTo(lv.Slice(end, srLen));
                        rv.Slice(0, slLen).CopyTo(lv.Slice(end + srLen, slLen));
                    }
                    else
                    {
                        var mergeEnd = end + srLen;

                        if (mergeEnd <= Index32Chunk.Capacity)
                        {
                            var secondCopyFirstLength = Index32Chunk.Capacity - mergeEnd;
                            var secondCopySecondLength = slLen - secondCopyFirstLength;

                            rk.Slice(right.GetStart(ref accessor), srLen).CopyTo(lk.Slice(end, srLen));
                            rv.Slice(right.GetStart(ref accessor), srLen).CopyTo(lv.Slice(end, srLen));

                            rk.Slice(0, secondCopyFirstLength).CopyTo(lk.Slice(mergeEnd, secondCopyFirstLength));
                            rv.Slice(0, secondCopyFirstLength).CopyTo(lv.Slice(mergeEnd, secondCopyFirstLength));
                            rk.Slice(secondCopyFirstLength, secondCopySecondLength).CopyTo(lk.Slice(0, secondCopySecondLength));
                            rv.Slice(secondCopyFirstLength, secondCopySecondLength).CopyTo(lv.Slice(0, secondCopySecondLength));
                        }
                        else
                        {
                            var firstCopyFirstLength = Index32Chunk.Capacity - end;
                            var firstCopySecondLength = srLen - firstCopyFirstLength;
                            var firstCopySecondStart = right.GetStart(ref accessor) + firstCopyFirstLength;

                            rk.Slice(right.GetStart(ref accessor), firstCopyFirstLength).CopyTo(lk.Slice(end, firstCopyFirstLength));
                            rk.Slice(firstCopySecondStart, firstCopySecondLength).CopyTo(lk.Slice(0, firstCopySecondLength));
                            rv.Slice(right.GetStart(ref accessor), firstCopyFirstLength).CopyTo(lv.Slice(end, firstCopyFirstLength));
                            rv.Slice(firstCopySecondStart, firstCopySecondLength).CopyTo(lv.Slice(0, firstCopySecondLength));

                            rk.Slice(0, slLen).CopyTo(lk.Slice(firstCopySecondLength, slLen));
                            rv.Slice(0, slLen).CopyTo(lv.Slice(firstCopySecondLength, slLen));
                        }
                    }
                }
            }

            leftChunk.Count += right.GetCount(ref accessor); // correct array length.
            leftChunk.HighKey = rightChunk.HighKey; // merged node inherits right's upper bound
        }

        public override NodeWrapper GetLastChild(NodeWrapper node, ref ChunkAccessor<TStore> accessor)
        {
            ref readonly var chunk = ref accessor.GetChunkReadOnly<Index32Chunk>(node.ChunkId);
            var index = Index32Chunk.Adjust(chunk.Start + chunk.Count - 1);
            return new NodeWrapper(this, chunk.Values[index]);
        }

        public override void IncrementStart(NodeWrapper node, ref ChunkAccessor<TStore> accessor)
        {
            ref var chunk = ref accessor.GetChunk<Index32Chunk>(node.ChunkId, true);
            IncrementStart(ref chunk);
        }

        public override void DecrementStart(NodeWrapper node, ref ChunkAccessor<TStore> accessor)
        {
            ref var chunk = ref accessor.GetChunk<Index32Chunk>(node.ChunkId, true);
            DecrementStart(ref chunk);
        }

        #endregion

        #region Chunk Direct Access Wrappers

        private static void Set(ref Index32Chunk chunk, int index, KeyValueItem item, bool adjust)
        {
            var i = adjust ? Index32Chunk.Adjust(chunk.Start + index) : index;
            chunk.Keys[i] = *(int*)&item.Key;
            chunk.Values[i] = item.Value;
        }

        private static void DecrementStart(ref Index32Chunk chunk)
        {
            if (chunk.Start == 0)
            {
                chunk.Start = Index32Chunk.Capacity - 1;
            }
            else
            {
                --chunk.Start;
            }
        }

        private static void IncrementStart(ref Index32Chunk chunk)
        {
            if (chunk.Start == (Index32Chunk.Capacity - 1))
            {
                chunk.Start = 0;
            }
            else
            {
                ++chunk.Start;
            }
        }

        private void LeftShift(ref Index32Chunk chunk, int index, int length)
        {
            if (length == 0)
            {
                return;
            }

            if (length < 0 || length > Index32Chunk.Capacity)
            {
                throw new ArgumentOutOfRangeException(nameof(length));
            }

            if (index < 0 || index >= Index32Chunk.Capacity)
            {
                throw new ArgumentOutOfRangeException(nameof(length));
            }

            var k = chunk.KeysAsSpan;
            var v = chunk.ValuesAsSpan;

            if (index == 0)
            {
                var first = k[0];
                k.Slice(1, length - 1).CopyTo(k);
                k[^1] = first;

                first = v[0];
                v.Slice(1, length - 1).CopyTo(v);
                v[^1] = first;
            }
            else if (index + length > k.Length)
            {
                var l = index + length - k.Length - 1;
                var remaining = length - l - 1;
                var first = k[0];
                k.Slice(1, l).CopyTo(k.Slice(0, l));
                k.Slice(index, remaining).CopyTo(k.Slice(index - 1, remaining));
                k[^1] = first;

                first = v[0];
                v.Slice(1, l).CopyTo(v.Slice(0, l));
                v.Slice(index, remaining).CopyTo(v.Slice(index - 1, remaining));
                v[^1] = first;
            }
            else
            {
                k.Slice(index, length).CopyTo(k.Slice(index - 1, length));
                v.Slice(index, length).CopyTo(v.Slice(index - 1, length));
            }
        }

        private void RightShift(ref Index32Chunk chunk, int index, int length)
        {
            if (length == 0)
            {
                return;
            }

            if (length < 0 || length > Index32Chunk.Capacity)
            {
                throw new ArgumentOutOfRangeException(nameof(length));
            }

            if (index < 0 || index >= Index32Chunk.Capacity)
            {
                throw new ArgumentOutOfRangeException(nameof(length));
            }

            var k = chunk.KeysAsSpan;
            var v = chunk.ValuesAsSpan;

            var lastInd = Index32Chunk.Capacity - 1;
            if (index + length > lastInd) // if overflows, rotate.
            {
                var last = k[lastInd];
                var rl = lastInd - index;
                var remaining = length - rl - 1;
                k.Slice(index, rl).CopyTo(k.Slice(index + 1, rl));
                k.Slice(0, remaining).CopyTo(k.Slice(1, remaining));
                k[0] = last;

                last = v[lastInd];
                v.Slice(index, rl).CopyTo(v.Slice(index + 1, rl));
                v.Slice(0, remaining).CopyTo(v.Slice(1, remaining));
                v[0] = last;
            }
            else
            {
                k.Slice(index, length).CopyTo(k.Slice(index + 1, length));
                v.Slice(index, length).CopyTo(v.Slice(index + 1, length));
            }
        }

        private NodeWrapper SplitRight(int leftChunkId, NodeStates states, ref ChunkAccessor<TStore> accessor)
        {
            ref var left = ref accessor.GetChunk<Index32Chunk>(leftChunkId, true);
            var oldHighKey = left.HighKey; // save before split — right inherits original upper bound

            var rightNode = Owner.AllocNode(states, ref accessor);

            // Re-obtain refs after allocation — AllocNode may trigger page cache eviction
            // (slot eviction in ChunkAccessor<TStore> or page eviction in PagedMMF), invalidating
            // previously cached pointers held as managed refs.
            ref var right = ref accessor.GetChunk<Index32Chunk>(rightNode.ChunkId, true);
            left = ref accessor.GetChunk<Index32Chunk>(leftChunkId, true);

            var lr = left.Count / 2; // length of right side
            var lrc = 1 + ((left.Count - 1) / 2); // length of right (ceiling of Length/2)
            var sr = Index32Chunk.Adjust(left.Start + lrc); // start of right side

            right.Count = lr;
            left.Count -= right.Count;

            var lk = left.KeysAsSpan;
            var lv = left.ValuesAsSpan;
            var rk = right.KeysAsSpan;
            var rv = right.ValuesAsSpan;

            var capacity = Index32Chunk.Capacity;

            if (sr + lr <= capacity) // if right side is one piece
            {
                lk.Slice(sr, lr).CopyTo(rk.Slice(0, lr));
                lk.Slice(sr, lr).Clear();
                lv.Slice(sr, lr).CopyTo(rv.Slice(0, lr));
                lv.Slice(sr, lr).Clear();
            }
            else
            {
                var length = capacity - sr;

                lk.Slice(sr, length).CopyTo(rk.Slice(0, length));
                lk.Slice(sr, length).Clear();
                lv.Slice(sr, length).CopyTo(rv.Slice(0, length));
                lv.Slice(sr, length).Clear();

                var remaining = lr - length;
                lk.Slice(0, remaining).CopyTo(rk.Slice(length, remaining));
                lk.Slice(0, remaining).Clear();
                lv.Slice(0, remaining).CopyTo(rv.Slice(length, remaining));
                lv.Slice(0, remaining).Clear();
            }

            // Update high keys: right inherits old upper bound, left gets separator (right's first key)
            right.HighKey = oldHighKey;
            left.HighKey = right.Keys[0]; // right.Start is 0 after AllocNode

            return rightNode;
        }

        #endregion
    }

    protected override BaseNodeStorage GetStorage() => new L32NodeStorage();
    public override bool AllowMultiple => false;
    protected L32BTree(ChunkBasedSegment<TStore> segment, bool load = false, BTreeStableKey key = default, ChangeSet changeSet = null)
        : base(segment, load, key, changeSet)
    {
    }
}

internal class L32MultipleBTree<TKey, TStore> : L32BTree<TKey, TStore> where TStore : struct, IPageStore where TKey : unmanaged
{
    protected L32MultipleBTree(ChunkBasedSegment<TStore> segment, bool load = false, BTreeStableKey key = default, ChangeSet changeSet = null)
        : base(segment, load, key, changeSet)
    {
    }

    public override bool AllowMultiple => true;
    protected override BaseNodeStorage GetStorage() => new L32MultipleNodeStorage();

    private sealed class L32MultipleNodeStorage : L32NodeStorage
    {
        private VariableSizedBufferSegment<int, TStore> _valueStore;

        internal override void Initialize(BTree<TKey, TStore> owner, ChunkBasedSegment<TStore> segment)
        {
            base.Initialize(owner, segment);
            _valueStore = new VariableSizedBufferSegment<int, TStore>(segment);

        }

        public override int Append(int bufferId, int value, ref ChunkAccessor<TStore> bufferAccessor) => _valueStore.AddElement(bufferId, value, ref bufferAccessor);
        public override VariableSizedBufferAccessor<int, TStore> GetBufferReadOnlyAccessor(int bufferId, ref ChunkAccessor<TStore> accessor) => _valueStore.GetReadOnlyAccessor(bufferId);
        public override VariableSizedBufferAccessor<int, TStore> GetBufferReadOnlyAccessor(int bufferId) => _valueStore.GetReadOnlyAccessor(bufferId);

        public override int CreateBuffer(ref ChunkAccessor<TStore> bufferAccessor) => _valueStore.AllocateBuffer(ref bufferAccessor);

        public override int RemoveFromBuffer(int bufferId, int elementId, int value, ref ChunkAccessor<TStore> bufferAccessor)
            => _valueStore.DeleteElement(bufferId, elementId, value, ref bufferAccessor);
        public override bool UpdateInBuffer(int bufferId, int elementId, int oldValue, int newValue, ref ChunkAccessor<TStore> bufferAccessor)
            => _valueStore.UpdateElement(bufferId, elementId, oldValue, newValue, ref bufferAccessor);

        public override int BufferElementCount(int bufferId, ref ChunkAccessor<TStore> bufferAccessor)
            => _valueStore.GetElementCount(bufferId, ref bufferAccessor);
        public override void DeleteBuffer(int bufferId, ref ChunkAccessor<TStore> bufferAccessor) => _valueStore.DeleteBuffer(bufferId, ref bufferAccessor);
    }
}

internal class IntSingleBTree<TStore> : L32BTree<int, TStore> where TStore : struct, IPageStore
{
    public IntSingleBTree(ChunkBasedSegment<TStore> segment, bool load = false, BTreeStableKey key = default, ChangeSet changeSet = null)
        : base(segment, load, key, changeSet)
    {
    }
}

internal class IntMultipleBTree<TStore> : L32MultipleBTree<int, TStore> where TStore : struct, IPageStore
{
    public IntMultipleBTree(ChunkBasedSegment<TStore> segment, bool load = false, BTreeStableKey key = default, ChangeSet changeSet = null)
        : base(segment, load, key, changeSet)
    {
    }
}

internal class UIntSingleBTree<TStore> : L32BTree<uint, TStore> where TStore : struct, IPageStore
{
    public UIntSingleBTree(ChunkBasedSegment<TStore> segment, bool load = false, BTreeStableKey key = default, ChangeSet changeSet = null)
        : base(segment, load, key, changeSet)
    {
    }

}

internal class UIntMultipleBTree<TStore> : L32MultipleBTree<uint, TStore> where TStore : struct, IPageStore
{
    public UIntMultipleBTree(ChunkBasedSegment<TStore> segment, bool load = false, BTreeStableKey key = default, ChangeSet changeSet = null)
        : base(segment, load, key, changeSet)
    {
    }
}

internal class FloatSingleBTree<TStore> : L32BTree<float, TStore> where TStore : struct, IPageStore
{
    public FloatSingleBTree(ChunkBasedSegment<TStore> segment, bool load = false, BTreeStableKey key = default, ChangeSet changeSet = null)
        : base(segment, load, key, changeSet)
    {
    }
}

internal class FloatMultipleBTree<TStore> : L32MultipleBTree<float, TStore> where TStore : struct, IPageStore
{
    public FloatMultipleBTree(ChunkBasedSegment<TStore> segment, bool load = false, BTreeStableKey key = default, ChangeSet changeSet = null)
        : base(segment, load, key, changeSet)
    {
    }
}