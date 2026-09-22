// unset

using System;
using System.Collections.Generic;

namespace Typhon.Engine.Internals;

internal abstract partial class BTree<TKey, TStore>
{
    public abstract class BaseNodeStorage
    {
        protected internal BTree<TKey, TStore> Owner;

        protected internal ChunkBasedSegment<TStore> Segment;

        internal virtual void Initialize(BTree<TKey, TStore> owner, ChunkBasedSegment<TStore> segment)
        {
            Owner = owner;
            Segment = segment;
        }

        public void CommitChanges(ref ChunkAccessor<TStore> accessor) => accessor.CommitChanges();

        #region Chunk Properties Access

        public abstract void InitializeNode(NodeWrapper node, NodeStates states, ref ChunkAccessor<TStore> accessor);
        public NodeWrapper LoadNode(int nodeId) => new(this, nodeId);
        public abstract int GetNodeCapacity();
        public abstract NodeWrapper GetLeftNode(NodeWrapper node, ref ChunkAccessor<TStore> accessor);
        public abstract void SetLeftNode(NodeWrapper node, int leftNodeId, ref ChunkAccessor<TStore> accessor);
        public abstract NodeWrapper GetPreviousNode(NodeWrapper node, ref ChunkAccessor<TStore> accessor);
        public abstract void SetPreviousNode(NodeWrapper node, int previousNodeId, ref ChunkAccessor<TStore> accessor);
        public abstract NodeWrapper GetNextNode(NodeWrapper node, ref ChunkAccessor<TStore> accessor);
        public abstract void SetNextNode(NodeWrapper node, int nextNodeId, ref ChunkAccessor<TStore> accessor);
        public abstract KeyValueItem GetItem(NodeWrapper node, int index, bool adjust, ref ChunkAccessor<TStore> accessor);
        public abstract void SetItem(NodeWrapper node, int index, KeyValueItem value, bool adjust, ref ChunkAccessor<TStore> accessor);

        /// <summary>
        /// Overwrite ONLY the value at <paramref name="index"/>, leaving the key, the count, the start offset and every separator untouched.
        /// </summary>
        /// <remarks>
        /// Deliberately not <see cref="SetItem"/> with a rebuilt <c>KeyValueItem</c>. Node chunks are SoA — keys and values are separate arrays — so a
        /// value-only update is one 4-byte store, and routing it through <c>SetItem</c> would add a redundant write to the key array. That write is
        /// value-preserving, so nothing observable changes, but it puts a store on a cache line an optimistic READER may be validating, and it makes
        /// "this operation cannot alter tree structure" an argument about the value written rather than about which bytes were touched (#872 AC-4.2).
        /// <para>
        /// The store is a <c>Volatile.Write</c>: release ordering, which is free on x64 and an <c>stlr</c> on arm64. It is what lets a concurrent reader see
        /// the old value or the new one and never a torn one — and, unlike Remove+Add, never an absent entry.
        /// </para>
        /// </remarks>
        public abstract void SetValueOnly(NodeWrapper node, int index, int value, ref ChunkAccessor<TStore> accessor);
        public abstract int GetCount(NodeWrapper node, ref ChunkAccessor<TStore> accessor);
        public abstract void SetCount(NodeWrapper node, int value, ref ChunkAccessor<TStore> accessor);
        public abstract int GetStart(NodeWrapper node, ref ChunkAccessor<TStore> accessor);
        public abstract void SetStart(NodeWrapper node, int value, ref ChunkAccessor<TStore> accessor);
        public abstract int GetEnd(NodeWrapper node, ref ChunkAccessor<TStore> accessor);
        public abstract NodeStates GetNodeStates(NodeWrapper node, ref ChunkAccessor<TStore> accessor);

        public abstract int GetContentionHint(NodeWrapper node, ref ChunkAccessor<TStore> accessor);
        public abstract void SetContentionHint(NodeWrapper node, int value, ref ChunkAccessor<TStore> accessor);

        /// <summary>
        /// Returns a ref to the node's OlcVersion field for optimistic lock coupling.
        /// Uses dirty=false because optimistic readers never dirty pages; writers must separately call GetChunk(id, true) before mutating data.
        /// </summary>
        public abstract ref int GetOlcVersionRef(int chunkId, ref ChunkAccessor<TStore> accessor);

        #endregion

        #region Chunk Operations

        public abstract void PushFirst(NodeWrapper node, KeyValueItem item, ref ChunkAccessor<TStore> accessor);
        public abstract void PushLast(NodeWrapper node, KeyValueItem item, ref ChunkAccessor<TStore> accessor);
        public abstract int Append(int bufferId, int value, ref ChunkAccessor<TStore> bufferAccessor);
        public abstract void Insert(NodeWrapper node, int index, KeyValueItem item, ref ChunkAccessor<TStore> accessor);
        public abstract int CreateBuffer(ref ChunkAccessor<TStore> bufferAccessor);
        public abstract VariableSizedBufferAccessor<int, TStore> GetBufferReadOnlyAccessor(int bufferId, ref ChunkAccessor<TStore> accessor);
        public abstract VariableSizedBufferAccessor<int, TStore> GetBufferReadOnlyAccessor(int bufferId);
        public abstract int RemoveFromBuffer(int bufferId, int elementId, int value, ref ChunkAccessor<TStore> bufferAccessor);

        /// <summary>
        /// How many elements an AllowMultiple key's buffer holds right now; 0 for a unique index, which has no buffers.
        /// </summary>
        /// <remarks>
        /// Exists for one decision: whether a key whose buffer a caller just emptied may still be removed. Read it under the write latch of the leaf that holds
        /// the key — an appender only ever adds to a buffer under that same latch, so a zero read there is a zero that will hold until the latch is released,
        /// and a non-zero one means the key was repopulated since the caller emptied it and must stay (IXW-06).
        /// </remarks>
        public abstract int BufferElementCount(int bufferId, ref ChunkAccessor<TStore> bufferAccessor);

        /// <summary>
        /// Replace one element's value inside an AllowMultiple key's buffer, in place. Returns <c>false</c> for a unique index, which has no buffers.
        /// </summary>
        /// <remarks>
        /// Takes <paramref name="oldValue"/> because elements are addressed BY VALUE within their chunk, not by position — the same reason
        /// <see cref="RemoveFromBuffer"/> takes the value it is removing. Nothing shifts and no count changes, so <paramref name="elementId"/> and every
        /// sibling's position survive the update (#872 AC-4.3).
        /// </remarks>
        public abstract bool UpdateInBuffer(int bufferId, int elementId, int oldValue, int newValue, ref ChunkAccessor<TStore> bufferAccessor);
        public abstract void DeleteBuffer(int bufferId, ref ChunkAccessor<TStore> bufferAccessor);
        public abstract NodeWrapper GetLastChild(NodeWrapper node, ref ChunkAccessor<TStore> accessor);
        public abstract NodeWrapper GetFirstChild(NodeWrapper node, ref ChunkAccessor<TStore> accessor);
        public NodeWrapper GetChild(NodeWrapper node, int index, ref ChunkAccessor<TStore> accessor)
        {
            if (node.GetIsLeaf(ref accessor))
            {
                return default;
            }

            // CAUTION: do NOT dereference the child chunk here (e.g., to prefetch isLeaf). OLC readers call this on a parent whose version has not yet been
            // validated (Find→GetChild happens before ValidateVersion in the descent loops). If the parent is concurrently mid-modification, the child chunk-id
            // we read can be torn/stale, and dereferencing it would crash before validation can signal "restart". Only read from the parent here; let callers
            // access the child after they validate the parent's version. Issue #297.
            return index < 0 ? GetLeftNode(node, ref accessor) : new NodeWrapper(this, GetItem(node, index, true, ref accessor).Value);
        }

        /// <summary>
        /// Finds <paramref name="key"/> in a leaf and returns the value stored against it — a bufferId, on an <c>AllowMultiple</c> index — in one resolution.
        /// </summary>
        /// <remarks>
        /// The <c>AllowMultiple</c> counterpart of the search half of <see cref="ApplyValuesInLeaf"/>. Reaching the same fact through
        /// <see cref="BinarySearch"/> and then <see cref="GetItem"/> resolves the SAME chunk id twice, once per distinct key in the batch.
        /// </remarks>
        public virtual bool TryFindValueInLeaf(NodeWrapper node, TKey key, IComparer<TKey> comparer, out int value, ref ChunkAccessor<TStore> accessor)
        {
            var index = BinarySearch(node, key, comparer, ref accessor);
            if (index < 0)
            {
                value = 0;
                return false;
            }

            value = GetItem(node, index, true, ref accessor).Value;
            return true;
        }

        /// <summary>Reads a node's leaf flag and item count together. Overridden per chunk width to do it in one chunk resolution instead of two.</summary>
        public virtual void ReadNodeHeader(NodeWrapper node, out bool isLeaf, out int count, ref ChunkAccessor<TStore> accessor)
        {
            isLeaf = (GetNodeStates(node, ref accessor) & NodeStates.IsLeaf) != 0;
            count = GetCount(node, ref accessor);
        }

        /// <summary>
        /// Locates the child owning <paramref name="key"/> and, in the same chunk resolution, the separator that bounds that child on the right.
        /// </summary>
        /// <returns>The child index, in <see cref="GetChild"/>'s numbering where −1 is the left node.</returns>
        /// <remarks>
        /// The two belong together because the bulk partition needs both for every child it descends into, and asking for them separately costs two
        /// resolutions of the same chunk id — per child, at every level, on every batch.
        /// </remarks>
        public virtual int FindChildAndBound(NodeWrapper node, TKey key, int count, IComparer<TKey> comparer, out int childChunkId, out TKey rightBound,
            out bool hasRightBound, ref ChunkAccessor<TStore> accessor)
        {
            var childIndex = BinarySearch(node, key, comparer, ref accessor);
            if (childIndex < 0)
            {
                childIndex = ~childIndex - 1;
            }

            childChunkId = GetChild(node, childIndex, ref accessor).ChunkId;
            hasRightBound = childIndex + 1 < count;
            rightBound = hasRightBound ? GetItem(node, childIndex + 1, true, ref accessor).Key : default;
            return childIndex;
        }

        /// <summary>
        /// Applies a whole leaf's worth of unique-index value updates. Overridden per chunk width to resolve the chunk ONCE for the entire sub-batch.
        /// </summary>
        /// <remarks>
        /// The default is the obvious per-entry loop and is correct everywhere; it exists so a storage type without an override stays right rather than fast.
        /// It also documents what the overrides are for: every one of <see cref="BinarySearch"/> and <see cref="SetValueOnly"/> resolves the chunk id to an
        /// address on its own, so the default pays TWO resolutions per entry. On a batch whose keys cluster into few leaves — which is exactly what
        /// re-clustering produces — that per-entry overhead is the dominant cost once the partitioning descent has removed the node visits (#872 AC-5.5).
        /// </remarks>
        public virtual int ApplyValuesInLeaf(NodeWrapper node, ReadOnlySpan<BTreeValueUpdate<TKey>> batch, IComparer<TKey> comparer,
            ref ChunkAccessor<TStore> accessor)
        {
            var applied = 0;
            for (var i = 0; i < batch.Length; i++)
            {
                var index = BinarySearch(node, batch[i].Key, comparer, ref accessor);
                if (index < 0)
                {
                    continue;
                }

                SetValueOnly(node, index, batch[i].NewValue, ref accessor);
                applied++;
            }

            return applied;
        }

        public abstract void IncrementStart(NodeWrapper node, ref ChunkAccessor<TStore> accessor);
        public abstract void DecrementStart(NodeWrapper node, ref ChunkAccessor<TStore> accessor);
        public abstract bool IsRotated(NodeWrapper node, ref ChunkAccessor<TStore> accessor);
        public abstract int BinarySearch(NodeWrapper node, TKey key, IComparer<TKey> comparer, ref ChunkAccessor<TStore> accessor);

        #endregion

        public abstract NodeWrapper SplitRight(NodeWrapper node, NodeStates nodeStates, ref ChunkAccessor<TStore> accessor);
        public abstract KeyValueItem RemoveAt(NodeWrapper node, int index, ref ChunkAccessor<TStore> accessor);
        public abstract void MergeLeft(NodeWrapper left, NodeWrapper right, ref ChunkAccessor<TStore> accessor);

        /// <summary>
        /// Returns the high key (upper bound) for B-link tree range checks.
        /// Default returns the last key in the node. Overridden by L16/L32/L64 to read the explicit HighKey field.
        /// </summary>
        public virtual TKey GetHighKey(NodeWrapper node, ref ChunkAccessor<TStore> accessor) => GetItem(node, GetCount(node, ref accessor) - 1, true, ref accessor).Key;

        /// <summary>
        /// Sets the high key for the node. Default is a no-op (for types without explicit HighKey like String64).
        /// </summary>
        public virtual void SetHighKey(NodeWrapper node, TKey key, ref ChunkAccessor<TStore> accessor) { }
    }
}