// unset

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using Typhon.Schema.Definition;

namespace Typhon.Engine;

[DebuggerTypeProxy(typeof(DebugView))]
[DebuggerDisplay("Count: {Count}, Start: {Start}, Flags: {StateFlags}")]
[StructLayout(LayoutKind.Sequential)]
unsafe public struct IndexString64Chunk
{
    // What we want here, is to keep this struct 64 bytes to align it with a cache line

    public const int Capacity = 4;

    // State flags (LSW 16bits), Position of the first Item, aka Start (8bits), stored Item Count (8bits)
    public int Control;
    public int OlcVersion;    // OLC latch: bit 0 = locked, bit 1 = obsolete, bits 2-31 = version (30 bits)
    public int PrevChunk;
    public int NextChunk;
    public int LeftValue;
    public fixed byte HighKey[64];                  // B-link upper bound (issue #297) — exclusive; sentinel = all 0xFF
    public fixed int Values[Capacity];
    public fixed byte Keys[64*Capacity];

    public Span<String64> KeysAsSpan
    {
        get
        {
            fixed (byte* k = Keys)
            {
                var strings = (String64*)k;
                return new Span<String64>(strings, Capacity);
            }
        }
    }

    /// <summary>Reads the explicit B-link HighKey (issue #297). Sentinel "infinity" is all 0xFF bytes.</summary>
    public readonly String64 GetHighKey()
    {
        fixed (byte* h = HighKey)
        {
            return new String64(h);
        }
    }

    /// <summary>Writes the explicit B-link HighKey (issue #297).</summary>
    public void SetHighKey(String64 key)
    {
        fixed (byte* h = HighKey)
        {
            key.AsSpan().CopyTo(new Span<byte>(h, 64));
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

    public void SetKey(int index, String64 str)
    {
        Debug.Assert(index < Capacity);
        fixed (byte* k = Keys)
        {
            str.AsSpan().CopyTo(new Span<byte>(k+64*index, 64));
        }
    }

    public readonly String64 GetKey(int index)
    {
        Debug.Assert(index < Capacity);
        fixed (byte* k = Keys)
        {
            return (new String64(k + 64 * index));
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
        private readonly IndexString64Chunk _chunk;

        public DebugView(IndexString64Chunk chunk)
        {
            _chunk = chunk;
        }

        public int Previous => _chunk.PrevChunk;
        public int Next => _chunk.NextChunk;
        public int LeftValue => _chunk.LeftValue;

        public ValueTuple<String64, int>[] KeyValuePairs
        {
            get
            {
                var k = _chunk.KeysAsSpan;
                var v = _chunk.ValuesAsSpan;

                var s = _chunk.Start;
                var count = _chunk.Count;
                var res = new ValueTuple<String64, int>[count];
                for (int i = 0; i < count; i++)
                {
                    var ii = Adjust(s + i);
                    res[i] = new ValueTuple<String64, int>(k[ii], v[ii]);
                }

                return res;
            }
        }
    }
}

public abstract class String64BTree<TStore> : BTree<String64, TStore> where TStore : struct, IPageStore
{
    protected unsafe class String64NodeStorage : BaseNodeStorage
    {
        internal override void Initialize(BTree<String64, TStore> owner, ChunkBasedSegment<TStore> segment)
        {
            base.Initialize(owner, segment);
            Debug.Assert(segment.Stride == sizeof(IndexString64Chunk));
        }

        #region Chunk Properties Access

        public override void InitializeNode(NodeWrapper node, NodeStates states, ref ChunkAccessor<TStore> accessor)
        {
            ref var chunk = ref accessor.GetChunk<IndexString64Chunk>(node.ChunkId, true);
            chunk.Control = (int)states;  // Atomically sets StateFlags + Start=0 + Count=0
            chunk.OlcVersion = 4;         // version=1 (bits 2-31), locked=false, obsolete=false — must be non-zero so OLC readers don't see it as locked
            chunk.PrevChunk = 0;
            chunk.NextChunk = 0;
            chunk.LeftValue = 0;
            // Issue #297: HighKey = all 0xFF (sentinel "infinity") so move-right gap-check
            // does not false-fire on freshly allocated rightmost-leaf with no keys yet.
            fixed (byte* h = chunk.HighKey)
            {
                new Span<byte>(h, 64).Fill(0xFF);
            }
        }

        public override int GetNodeCapacity() => IndexString64Chunk.Capacity;

        public override ref int GetOlcVersionRef(int chunkId, ref ChunkAccessor<TStore> accessor)
        {
            ref var chunk = ref accessor.GetChunk<IndexString64Chunk>(chunkId);
            return ref chunk.OlcVersion;
        }

        public override NodeWrapper GetLeftNode(NodeWrapper node, ref ChunkAccessor<TStore> accessor)
        {
            ref readonly var chunk = ref accessor.GetChunkReadOnly<IndexString64Chunk>(node.ChunkId);
            return new NodeWrapper(this, chunk.LeftValue);
        }

        public override void SetLeftNode(NodeWrapper node, int previousNodeId, ref ChunkAccessor<TStore> accessor)
        {
            ref var chunk = ref accessor.GetChunk<IndexString64Chunk>(node.ChunkId, true);
            chunk.LeftValue = previousNodeId;
        }

        public override NodeWrapper GetPreviousNode(NodeWrapper node, ref ChunkAccessor<TStore> accessor)
        {
            ref readonly var chunk = ref accessor.GetChunkReadOnly<IndexString64Chunk>(node.ChunkId);
            return new NodeWrapper(this, chunk.PrevChunk);
        }

        public override void SetPreviousNode(NodeWrapper node, int previousNodeId, ref ChunkAccessor<TStore> accessor)
        {
            ref var chunk = ref accessor.GetChunk<IndexString64Chunk>(node.ChunkId, true);
            chunk.PrevChunk = previousNodeId;
        }

        public override NodeWrapper GetNextNode(NodeWrapper node, ref ChunkAccessor<TStore> accessor)
        {
            ref readonly var chunk = ref accessor.GetChunkReadOnly<IndexString64Chunk>(node.ChunkId);
            return new NodeWrapper(this, chunk.NextChunk);
        }

        public override void SetNextNode(NodeWrapper node, int nextNodeId, ref ChunkAccessor<TStore> accessor)
        {
            ref var chunk = ref accessor.GetChunk<IndexString64Chunk>(node.ChunkId, true);
            chunk.NextChunk = nextNodeId;
        }

        public override KeyValueItem GetItem(NodeWrapper node, int index, bool adjust, ref ChunkAccessor<TStore> accessor)
        {
            ref readonly var chunk = ref accessor.GetChunkReadOnly<IndexString64Chunk>(node.ChunkId);
            var i = adjust ? IndexString64Chunk.Adjust(chunk.Start + index) : index;

            return new KeyValueItem(chunk.GetKey(i), chunk.Values[i]);
        }

        public override void SetItem(NodeWrapper node, int index, KeyValueItem value, bool adjust, ref ChunkAccessor<TStore> accessor)
        {
            ref var chunk = ref accessor.GetChunk<IndexString64Chunk>(node.ChunkId, true);
            Set(ref chunk, index, value, adjust);
        }

        public override int GetCount(NodeWrapper node, ref ChunkAccessor<TStore> accessor)
        {
            ref readonly var chunk = ref accessor.GetChunkReadOnly<IndexString64Chunk>(node.ChunkId);
            return chunk.Count;
        }

        public override void SetCount(NodeWrapper node, int value, ref ChunkAccessor<TStore> accessor)
        {
            ref var chunk = ref accessor.GetChunk<IndexString64Chunk>(node.ChunkId, true);
            chunk.Count = value;
        }

        public override int GetStart(NodeWrapper node, ref ChunkAccessor<TStore> accessor)
        {
            ref readonly var chunk = ref accessor.GetChunkReadOnly<IndexString64Chunk>(node.ChunkId);
            return chunk.Start;
        }

        public override void SetStart(NodeWrapper node, int value, ref ChunkAccessor<TStore> accessor)
        {
            ref var chunk = ref accessor.GetChunk<IndexString64Chunk>(node.ChunkId, true);
            chunk.Start = value;
        }

        public override int GetEnd(NodeWrapper node, ref ChunkAccessor<TStore> accessor)
        {
            ref readonly var chunk = ref accessor.GetChunkReadOnly<IndexString64Chunk>(node.ChunkId);
            return IndexString64Chunk.Adjust(chunk.Start + chunk.Count);
        }

        public override NodeStates GetNodeStates(NodeWrapper node, ref ChunkAccessor<TStore> accessor)
        {
            ref readonly var chunk = ref accessor.GetChunkReadOnly<IndexString64Chunk>(node.ChunkId);
            return chunk.StateFlags;
        }

        public override int GetContentionHint(NodeWrapper node, ref ChunkAccessor<TStore> accessor)
        {
            ref readonly var chunk = ref accessor.GetChunkReadOnly<IndexString64Chunk>(node.ChunkId);
            return chunk.ContentionHint;
        }

        public override void SetContentionHint(NodeWrapper node, int value, ref ChunkAccessor<TStore> accessor)
        {
            ref var chunk = ref accessor.GetChunk<IndexString64Chunk>(node.ChunkId, true);
            chunk.ContentionHint = value;
        }

        // Issue #297: explicit HighKey override (B-link upper bound). Without this override,
        // BaseNodeStorage.GetHighKey defaults to "last key in node", which makes
        // node.HighKey != nextNode.firstKey by design — creating a permanent "gap" that
        // breaks the move-right gap-check restart. With the override, the invariant
        // node.HighKey == nextNode.firstKey holds across splits, matching L16/L32/L64.
        public override String64 GetHighKey(NodeWrapper node, ref ChunkAccessor<TStore> accessor)
        {
            ref readonly var chunk = ref accessor.GetChunkReadOnly<IndexString64Chunk>(node.ChunkId);
            return chunk.GetHighKey();
        }

        public override void SetHighKey(NodeWrapper node, String64 key, ref ChunkAccessor<TStore> accessor)
        {
            ref var chunk = ref accessor.GetChunk<IndexString64Chunk>(node.ChunkId, true);
            chunk.SetHighKey(key);
        }

        #endregion

        #region Chunk Operations

        public override void PushFirst(NodeWrapper node, KeyValueItem item, ref ChunkAccessor<TStore> accessor)
        {
            ref var chunk = ref accessor.GetChunk<IndexString64Chunk>(node.ChunkId, true);

            DecrementStart(ref chunk);

            var start = chunk.Start;
            chunk.SetKey(start, item.Key);
            chunk.Values[start] = item.Value;

            ++chunk.Count;
        }

        public override void PushLast(NodeWrapper node, KeyValueItem item, ref ChunkAccessor<TStore> accessor)
        {
            ref var chunk = ref accessor.GetChunk<IndexString64Chunk>(node.ChunkId, true);
            var c = chunk.Count++;
            Set(ref chunk, c, item, true);
        }

        public override int Append(int bufferId, int value, ref ChunkAccessor<TStore> bufferAccessor) => throw new Exception("Shouldn't be called as key replace is not supported and multi-value neither");

        public override void Insert(NodeWrapper node, int index, KeyValueItem item, ref ChunkAccessor<TStore> accessor)
        {
            ref var chunk = ref accessor.GetChunk<IndexString64Chunk>(node.ChunkId, true);
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
                RightShift(ref chunk, IndexString64Chunk.Adjust(chunk.Start + index), rsh); // move End to End+1
                Set(ref chunk, index, item, true);
            }

            chunk.Count++;
        }

        public override int CreateBuffer(ref ChunkAccessor<TStore> bufferAccessor) => 0;

        public override VariableSizedBufferAccessor<int, TStore> GetBufferReadOnlyAccessor(int bufferId, ref ChunkAccessor<TStore> accessor) => default;
        public override VariableSizedBufferAccessor<int, TStore> GetBufferReadOnlyAccessor(int bufferId) => default;
        public override int RemoveFromBuffer(int bufferId, int elementId, int value, ref ChunkAccessor<TStore> bufferAccessor) => 0;
        public override void DeleteBuffer(int bufferId, ref ChunkAccessor<TStore> bufferAccessor) { }

        public override NodeWrapper GetFirstChild(NodeWrapper node, ref ChunkAccessor<TStore> accessor)
        {
            ref readonly var chunk = ref accessor.GetChunkReadOnly<IndexString64Chunk>(node.ChunkId);
            return new NodeWrapper(this, chunk.LeftValue);
        }

        public override bool IsRotated(NodeWrapper node, ref ChunkAccessor<TStore> accessor)
        {
            ref readonly var chunk = ref accessor.GetChunkReadOnly<IndexString64Chunk>(node.ChunkId);
            return (chunk.Start + chunk.Count) > IndexString64Chunk.Capacity;
        }

        public override int BinarySearch(NodeWrapper node, String64 key, IComparer<String64> comparer, ref ChunkAccessor<TStore> accessor)
        {
            ref readonly var chunk = ref accessor.GetChunkReadOnly<IndexString64Chunk>(node.ChunkId);
            fixed (void* keys = chunk.Keys)
            {
                if (IsRotated(node, ref accessor))
                {
                    if (comparer.Compare(key, chunk.GetKey(IndexString64Chunk.Capacity - 1)) <= 0) // search right side if item is smaller than last item in array.
                    {
                        var find = BTreeExtensions.BinarySearch((String64*)keys, chunk.Start, IndexString64Chunk.Capacity - chunk.Start, key, comparer, 64);
                        return find - chunk.Start * find.Sign();
                    }
                    else // search left side
                    {
                        var find = BTreeExtensions.BinarySearch((String64*)keys, 0, chunk.End, key, comparer, 64);
                        return find + (IndexString64Chunk.Capacity - chunk.Start) * find.Sign();
                    }
                }
                else
                {
                    var find = BTreeExtensions.BinarySearch((String64*)keys, chunk.Start, chunk.Count, key, comparer, 64);
                    return find - chunk.Start * find.Sign();
                }
            }
        }

        public override NodeWrapper SplitRight(NodeWrapper node, NodeStates states, ref ChunkAccessor<TStore> accessor) => 
            SplitRight(node.ChunkId, states, ref accessor);

        public override KeyValueItem RemoveAt(NodeWrapper node, int index, ref ChunkAccessor<TStore> accessor)
        {
            ref var chunk = ref accessor.GetChunk<IndexString64Chunk>(node.ChunkId, true);
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
                LeftShift(ref chunk, IndexString64Chunk.Adjust(chunk.Start + index + 1), lsh); // move End to End-1
                Set(ref chunk, chunk.Count - 1, default, true); // remove last item
            }

            chunk.Count--;
            return item;
        }

        public override void MergeLeft(NodeWrapper left, NodeWrapper right, ref ChunkAccessor<TStore> accessor)
        {
            ref var leftChunk = ref accessor.GetChunk<IndexString64Chunk>(left.ChunkId, true);
            ref var rightChunk = ref accessor.GetChunk<IndexString64Chunk>(right.ChunkId, true);

            var lk = leftChunk.KeysAsSpan;
            var lv = leftChunk.ValuesAsSpan;
            var rk = rightChunk.KeysAsSpan;
            var rv = rightChunk.ValuesAsSpan;

            if (leftChunk.Count + right.GetCount(ref accessor) > IndexString64Chunk.Capacity)
            {
                throw new InvalidOperationException("can not merge, there is not enough capacity for this array.");
            }

            var end = leftChunk.Start + leftChunk.Count;

            if (leftChunk.IsRotated)
            {
                var start = end - IndexString64Chunk.Capacity;

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
                bool copyIsOnePiece = end + right.GetCount(ref accessor) <= IndexString64Chunk.Capacity;

                if (!rightChunk.IsRotated)
                {
                    if (copyIsOnePiece)
                    {
                        rk.Slice(right.GetStart(ref accessor), right.GetCount(ref accessor)).CopyTo(lk.Slice(end, right.GetCount(ref accessor)));
                        rv.Slice(right.GetStart(ref accessor), right.GetCount(ref accessor)).CopyTo(lv.Slice(end, right.GetCount(ref accessor)));
                    }
                    else
                    {
                        var length = IndexString64Chunk.Capacity - end;
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

                        if (mergeEnd <= IndexString64Chunk.Capacity)
                        {
                            var secondCopyFirstLength = IndexString64Chunk.Capacity - mergeEnd;
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
                            var firstCopyFirstLength = IndexString64Chunk.Capacity - end;
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
            // Issue #297: merged node inherits right's upper bound (mirrors L32 SplitRight invariant).
            leftChunk.SetHighKey(rightChunk.GetHighKey());
        }

        public override NodeWrapper GetLastChild(NodeWrapper node, ref ChunkAccessor<TStore> accessor)
        {
            ref readonly var chunk = ref accessor.GetChunkReadOnly<IndexString64Chunk>(node.ChunkId);
            var index = IndexString64Chunk.Adjust(chunk.Start + chunk.Count - 1);
            return new NodeWrapper(this, chunk.Values[index]);
        }

        public override void IncrementStart(NodeWrapper node, ref ChunkAccessor<TStore> accessor)
        {
            ref var chunk = ref accessor.GetChunk<IndexString64Chunk>(node.ChunkId, true);
            IncrementStart(ref chunk);
        }

        public override void DecrementStart(NodeWrapper node, ref ChunkAccessor<TStore> accessor)
        {
            ref var chunk = ref accessor.GetChunk<IndexString64Chunk>(node.ChunkId, true);
            DecrementStart(ref chunk);
        }

        #endregion

        #region Chunk Direct Access Wrappers

        private static void Set(ref IndexString64Chunk chunk, int index, KeyValueItem item, bool adjust)
        {
            var i = adjust ? IndexString64Chunk.Adjust(chunk.Start + index) : index;
            chunk.SetKey(i, item.Key);
            chunk.Values[i] = item.Value;
        }

        private static void DecrementStart(ref IndexString64Chunk chunk)
        {
            if (chunk.Start == 0)
            {
                chunk.Start = IndexString64Chunk.Capacity - 1;
            }
            else
            {
                --chunk.Start;
            }
        }

        private static void IncrementStart(ref IndexString64Chunk chunk)
        {
            if (chunk.Start == (IndexString64Chunk.Capacity - 1))
            {
                chunk.Start = 0;
            }
            else
            {
                ++chunk.Start;
            }
        }

        private void LeftShift(ref IndexString64Chunk chunk, int index, int length)
        {
            if (length == 0)
            {
                return;
            }

            if (length < 0 || length > IndexString64Chunk.Capacity)
            {
                throw new ArgumentOutOfRangeException(nameof(length));
            }

            if (index < 0 || index >= IndexString64Chunk.Capacity)
            {
                throw new ArgumentOutOfRangeException(nameof(length));
            }

            var k = chunk.KeysAsSpan;
            var v = chunk.ValuesAsSpan;

            if (index == 0)
            {
                var firstK = k[0];
                k.Slice(1, length - 1).CopyTo(k);
                k[^1] = firstK;

                var firstV = v[0];
                v.Slice(1, length - 1).CopyTo(v);
                v[^1] = firstV;
            }
            else if (index + length > k.Length)
            {
                var l = index + length - k.Length - 1;
                var remaining = length - l - 1;
                var firstK = k[0];
                k.Slice(1, l).CopyTo(k.Slice(0, l));
                k.Slice(index, remaining).CopyTo(k.Slice(index - 1, remaining));
                k[^1] = firstK;

                var firstV = v[0];
                v.Slice(1, l).CopyTo(v.Slice(0, l));
                v.Slice(index, remaining).CopyTo(v.Slice(index - 1, remaining));
                v[^1] = firstV;
            }
            else
            {
                k.Slice(index, length).CopyTo(k.Slice(index - 1, length));
                v.Slice(index, length).CopyTo(v.Slice(index - 1, length));
            }
        }

        private void RightShift(ref IndexString64Chunk chunk, int index, int length)
        {
            if (length == 0)
            {
                return;
            }

            if (length < 0 || length > IndexString64Chunk.Capacity)
            {
                throw new ArgumentOutOfRangeException(nameof(length));
            }

            if (index < 0 || index >= IndexString64Chunk.Capacity)
            {
                throw new ArgumentOutOfRangeException(nameof(length));
            }

            var k = chunk.KeysAsSpan;
            var v = chunk.ValuesAsSpan;

            var lastInd = IndexString64Chunk.Capacity - 1;
            if (index + length > lastInd) // if overflows, rotate.
            {
                var lastK = k[lastInd];
                var rl = lastInd - index;
                var remaining = length - rl - 1;
                k.Slice(index, rl).CopyTo(k.Slice(index + 1, rl));
                k.Slice(0, remaining).CopyTo(k.Slice(1, remaining));
                k[0] = lastK;

                var lastV = v[lastInd];
                v.Slice(index, rl).CopyTo(v.Slice(index + 1, rl));
                v.Slice(0, remaining).CopyTo(v.Slice(1, remaining));
                v[0] = lastV;
            }
            else
            {
                k.Slice(index, length).CopyTo(k.Slice(index + 1, length));
                v.Slice(index, length).CopyTo(v.Slice(index + 1, length));
            }
        }

        private NodeWrapper SplitRight(int leftChunkId, NodeStates states, ref ChunkAccessor<TStore> accessor)
        {
            ref var left = ref accessor.GetChunk<IndexString64Chunk>(leftChunkId, true);
            // Issue #297: snapshot left's old HighKey BEFORE the split, so right can inherit it.
            // Capture in a local because span/ref operations below may invalidate inline reads.
            var oldHighKey = left.GetHighKey();

            var rightNode = Owner.AllocNode(states, ref accessor);

            // Re-obtain refs after allocation — AllocNode may trigger page cache eviction
            // (slot eviction in ChunkAccessor<TStore> or page eviction in PagedMMF), invalidating
            // previously cached pointers held as managed refs.
            ref var right = ref accessor.GetChunk<IndexString64Chunk>(rightNode.ChunkId, true);
            left = ref accessor.GetChunk<IndexString64Chunk>(leftChunkId, true);

            var lr = left.Count / 2; // length of right side
            var lrc = 1 + ((left.Count - 1) / 2); // length of right (ceiling of Length/2)
            var sr = IndexString64Chunk.Adjust(left.Start + lrc); // start of right side

            right.Count = lr;
            left.Count -= right.Count;

            var lk = left.KeysAsSpan;
            var lv = left.ValuesAsSpan;
            var rk = right.KeysAsSpan;
            var rv = right.ValuesAsSpan;

            var capacity = IndexString64Chunk.Capacity;

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

            // Issue #297: update HighKeys to maintain the B-link invariant
            // (left.HighKey == right.firstKey, right.HighKey == old left.HighKey).
            // Right.Start is 0 after AllocNode, so right.GetKey(0) is its first key.
            right.SetHighKey(oldHighKey);
            left.SetHighKey(right.GetKey(0));

            return rightNode;
        }

        #endregion
    }

    protected override BaseNodeStorage GetStorage() => new String64NodeStorage();
    public override bool AllowMultiple => false;
    protected String64BTree(ChunkBasedSegment<TStore> segment, bool load = false, short stableId = 0, ChangeSet changeSet = null) : base(segment, load, stableId, changeSet)
    {
    }
}

public class String64MultipleBTree<TStore> : String64BTree<TStore> where TStore : struct, IPageStore
{
    public String64MultipleBTree(ChunkBasedSegment<TStore> segment, bool load = false, short stableId = 0, ChangeSet changeSet = null) : base(segment, load, stableId, changeSet)
    {
    }

    public override bool AllowMultiple => true;
    protected override BaseNodeStorage GetStorage() => new String64MultipleNodeStorage();

    private sealed class String64MultipleNodeStorage : String64NodeStorage
    {
        private VariableSizedBufferSegment<int, TStore> _valueStore;

        internal override void Initialize(BTree<String64, TStore> owner, ChunkBasedSegment<TStore> segment)
        {
            base.Initialize(owner, segment);
            _valueStore = new VariableSizedBufferSegment<int, IndexBufferExtraHeader, TStore>(segment);

        }

        public override int Append(int bufferId, int value, ref ChunkAccessor<TStore> bufferAccessor) => _valueStore.AddElement(bufferId, value, ref bufferAccessor);
        public override VariableSizedBufferAccessor<int, TStore> GetBufferReadOnlyAccessor(int bufferId, ref ChunkAccessor<TStore> accessor) => _valueStore.GetReadOnlyAccessor(bufferId);
        public override VariableSizedBufferAccessor<int, TStore> GetBufferReadOnlyAccessor(int bufferId) => _valueStore.GetReadOnlyAccessor(bufferId);

        public override int CreateBuffer(ref ChunkAccessor<TStore> bufferAccessor) => _valueStore.AllocateBuffer(ref bufferAccessor);

        public override int RemoveFromBuffer(int bufferId, int elementId, int value, ref ChunkAccessor<TStore> bufferAccessor)
            => _valueStore.DeleteElement(bufferId, elementId, value, ref bufferAccessor);
        public override void DeleteBuffer(int bufferId, ref ChunkAccessor<TStore> bufferAccessor) => _valueStore.DeleteBuffer(bufferId, ref bufferAccessor);
    }
}

public class String64SingleBTree<TStore> : String64BTree<TStore> where TStore : struct, IPageStore
{
    public String64SingleBTree(ChunkBasedSegment<TStore> segment, bool load = false, short stableId = 0, ChangeSet changeSet = null) : base(segment, load, stableId, changeSet)
    {
    }
}