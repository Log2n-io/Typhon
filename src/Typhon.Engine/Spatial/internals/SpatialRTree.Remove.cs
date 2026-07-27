using System.Runtime.CompilerServices;
using System.Threading;

namespace Typhon.Engine.Internals;

internal unsafe partial class SpatialRTree<TStore>
{
    /// <summary>
    /// Remove an entity from a known leaf position using swap-with-last.
    /// </summary>
    /// <param name="leafChunkId">Chunk ID of the leaf containing the entity</param>
    /// <param name="slotIndex">Slot index within the leaf</param>
    /// <param name="accessor">ChunkAccessor for page access</param>
    /// <returns>
    /// The EntityId that was swapped into slotIndex (for back-pointer update by Phase 2), or 0 if no swap occurred (the removed entry was the last one).
    /// </returns>
    internal long Remove(int leafChunkId, int slotIndex, ref ChunkAccessor<TStore> accessor)
    {
        // Read entityId before we modify the leaf, so we can carry it in the span payload.
        long entityIdForTrace = 0;
        if (TelemetryConfig.SpatialRTreeRemoveActive)
        {
            ref byte leafForTrace = ref Unsafe.AsRef<byte>(accessor.GetChunkAddress(leafChunkId));
            entityIdForTrace = SpatialNodeHelper.ReadLeafEntityId(ref leafForTrace, slotIndex, _desc);
        }
        using var removeSpan = TyphonEvent.BeginSpatialRTreeRemove(entityIdForTrace);

        ref byte leafBase = ref Unsafe.AsRef<byte>(accessor.GetChunkAddress(leafChunkId, true));
        SpinWriteLock(ref leafBase, out var latch);

        int count = SpatialNodeHelper.GetCount(ref leafBase);
        int lastIndex = count - 1;
        long swappedEntityId = 0;

        if (slotIndex != lastIndex)
        {
            SpatialNodeHelper.CopyLeafEntry(ref leafBase, lastIndex, slotIndex, _desc);
            swappedEntityId = SpatialNodeHelper.ReadLeafEntityId(ref leafBase, slotIndex, _desc);
        }

        SpatialNodeHelper.SetCount(ref leafBase, lastIndex);
        SpatialNodeHelper.RefitLeafMBR(ref leafBase, _desc);
        latch.WriteUnlock();

        Interlocked.Decrement(ref _entityCount);
        Interlocked.Increment(ref _mutationVersion);

        if (lastIndex == 0)
        {
            // Leaf is now empty
            if (leafChunkId != _rootChunkId)
            {
                RemoveEmptyLeaf(leafChunkId, ref accessor);
            }
        }
        else
        {
            RefitAncestorsBottomUp(leafChunkId, ref accessor);
        }

        SyncMetadata(ref accessor);
        return swappedEntityId;
    }

    /// <summary>
    /// Remove an empty leaf from its parent. Cascades upward if parent also becomes empty.
    /// Collapses root if it has a single remaining child.
    /// </summary>
    private void RemoveEmptyLeaf(int leafChunkId, ref ChunkAccessor<TStore> accessor)
    {
        ref byte leafBase = ref Unsafe.AsRef<byte>(accessor.GetChunkAddress(leafChunkId));
        int parentChunkId = SpatialNodeHelper.GetParentChunkId(ref leafBase);

        ref byte parentBase = ref Unsafe.AsRef<byte>(accessor.GetChunkAddress(parentChunkId, true));
        SpinWriteLock(ref parentBase, out var parentLatch);

        // Find and remove the entry pointing to this leaf
        int parentCount = SpatialNodeHelper.GetCount(ref parentBase);
        int leafIdx = FindChildIndex(ref parentBase, leafChunkId, parentCount);

        if (leafIdx >= 0)
        {
            int lastIdx = parentCount - 1;
            if (leafIdx != lastIdx)
            {
                SpatialNodeHelper.CopyInternalEntry(ref parentBase, lastIdx, leafIdx, _desc);

                // Update the moved child's parent pointer (it stays in the same parent)
                int movedChildId = SpatialNodeHelper.ReadInternalChildId(ref parentBase, leafIdx, _desc);
                // Parent pointer is unchanged since the child is still in the same parent node
            }
            SpatialNodeHelper.SetCount(ref parentBase, lastIdx);
            SpatialNodeHelper.RefitInternalMBR(ref parentBase, _desc);
            RefitInternalUnionMask(ref parentBase, ref accessor);
        }

        parentLatch.WriteUnlock();
        _segment.FreeChunk(leafChunkId);
        Interlocked.Decrement(ref _nodeCount);

        int newParentCount = parentCount - 1;

        if (newParentCount == 0 && parentChunkId != _rootChunkId)
        {
            // Parent is now empty and isn't root: cascade removal
            RemoveEmptyLeaf(parentChunkId, ref accessor);
        }
        else if (newParentCount == 1 && parentChunkId == _rootChunkId)
        {
            // Root has single child: collapse (promote child to root)
            int remainingChild = SpatialNodeHelper.ReadInternalChildId(
                ref Unsafe.AsRef<byte>(accessor.GetChunkAddress(parentChunkId)), 0, _desc);

            ref byte newRootBase = ref Unsafe.AsRef<byte>(accessor.GetChunkAddress(remainingChild, true));
            SpatialNodeHelper.SetParentChunkId(ref newRootBase, 0);

            _segment.FreeChunk(_rootChunkId);
            Interlocked.Decrement(ref _nodeCount);
            _rootChunkId = remainingChild;
            _depth--;
        }
        else
        {
            // Refit ancestors above the parent
            RefitAncestorsBottomUp(parentChunkId, ref accessor);
        }
    }

    /// <summary>Find the index of a child chunk ID in an internal node's entries.</summary>
    private int FindChildIndex(ref byte nodeBase, int childChunkId, int count)
    {
        for (int i = 0; i < count; i++)
        {
            if (SpatialNodeHelper.ReadInternalChildId(ref nodeBase, i, _desc) == childChunkId)
            {
                return i;
            }
        }
        return -1;
    }
}
