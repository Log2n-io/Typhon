// unset

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