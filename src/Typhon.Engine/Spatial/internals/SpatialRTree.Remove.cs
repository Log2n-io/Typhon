using System.Threading;

using System.Runtime.CompilerServices;

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
        // Read payloadId before we modify the leaf, so we can carry it in the span payload.
        long entityIdForTrace = 0;
        if (TelemetryConfig.SpatialRTreeRemoveActive)
        {
            byte* leafForTrace = accessor.GetChunkAddress(leafChunkId);
            entityIdForTrace = SpatialNodeHelper.ReadLeafEntityId(leafForTrace, slotIndex, _desc);
        }
        using var removeSpan = TyphonEvent.BeginSpatialRTreeRemove(entityIdForTrace);

        byte* leafBase = accessor.GetChunkAddress(leafChunkId, true);
        SpinWriteLock(leafBase, out var latch);

        int count = SpatialNodeHelper.GetCount(leafBase);
        int lastIndex = count - 1;
        long swappedPayloadId = 0;

        // Read BEFORE the swap overwrites it: the payload being removed owns a handle that must be retired, not left naming a slot another payload is about
        // to occupy — or a chunk that RemoveEmptyLeaf frees and AllocNode later recycles. Writing one side of the array and leaving the other to the caller
        // is how a stale handle survives the very mechanism built to prevent it (#872 step 9).
        var payloadBackPointers = PayloadBackPointers;
        long removedPayloadId = payloadBackPointers != null ? SpatialNodeHelper.ReadLeafEntityId(leafBase, slotIndex, _desc) : 0;

        if (slotIndex != lastIndex)
        {
            SpatialNodeHelper.CopyLeafEntry(leafBase, lastIndex, slotIndex, _desc);
            swappedPayloadId = SpatialNodeHelper.ReadLeafEntityId(leafBase, slotIndex, _desc);

            // Swap-with-last moves exactly one entry, so exactly one OTHER handle goes stale. The method already RETURNS the swapped id for callers that keep
            // back-pointers in a segment; a payload-indexed caller is served here instead, because it is one store and doing it at the call site means every
            // call site has to remember.
            if (payloadBackPointers != null)
            {
                WritePayloadHandle(payloadBackPointers, swappedPayloadId, PackHandle(leafChunkId, slotIndex));
            }
        }

        if (payloadBackPointers != null)
        {
            WritePayloadHandle(payloadBackPointers, removedPayloadId, NullHandle);
        }

        SpatialNodeHelper.SetCount(leafBase, lastIndex);
        SpatialNodeHelper.RefitLeafMBR(leafBase, _desc);
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
        return swappedPayloadId;
    }

    /// <summary>
    /// Remove the entry at a location, refusing unless it actually holds <paramref name="expectedPayloadId"/>.
    /// </summary>
    /// <param name="leafChunkId">Chunk id of the leaf named by the caller's handle.</param>
    /// <param name="slotIndex">Slot index within that leaf.</param>
    /// <param name="expectedPayloadId">The payload the caller believes is there. A mismatch throws rather than removing.</param>
    /// <param name="accessor">ChunkAccessor for page access.</param>
    /// <returns>Same as <see cref="Remove(int, int, ref ChunkAccessor{TStore})"/> — the payload swapped into the vacated slot, or 0.</returns>
    /// <remarks>
    /// For callers holding a handle rather than a descent path. The unchecked <see cref="Remove(int, int, ref ChunkAccessor{TStore})"/> stays for callers that
    /// reached the slot by descending the tree and therefore know what is in it; a caller working from a stored handle does not, and the difference is a
    /// silently deleted stranger (<c>ST-05</c>). Cheap: one read of a field the removal is about to touch anyway.
    /// </remarks>
    internal long RemoveChecked(int leafChunkId, int slotIndex, long expectedPayloadId, ref ChunkAccessor<TStore> accessor)
    {
        byte* leafBase = accessor.GetChunkAddress(leafChunkId);
        if (!SpatialNodeHelper.IsLeaf(leafBase) || slotIndex >= SpatialNodeHelper.GetCount(leafBase))
        {
            ThrowRemoveIdentityMismatch(leafChunkId, slotIndex, expectedPayloadId, "the location is not a live leaf slot");
        }

        long actual = SpatialNodeHelper.ReadLeafEntityId(leafBase, slotIndex, _desc);
        if (actual != expectedPayloadId)
        {
            ThrowRemoveIdentityMismatch(leafChunkId, slotIndex, expectedPayloadId, $"it holds payload {actual}");
        }

        return Remove(leafChunkId, slotIndex, ref accessor);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowRemoveIdentityMismatch(int leafChunkId, int slotIndex, long expectedPayloadId, string why) =>
        ThrowHelper.ThrowInvalidOp(
            $"Refusing to remove payload {expectedPayloadId} at (leaf {leafChunkId}, slot {slotIndex}): {why}. Removing anyway would delete another payload "
            + "and retire ITS back-pointer, which is ST-05's silent failure. The handle came from PayloadBackPointers, so reaching this means that array has "
            + "a gap — a relocation that did not write it, or a concurrent writer, which SpatialRTree does not support (ADR-044, invariant O2).");

    /// <summary>
    /// Remove an empty leaf from its parent. Cascades upward if parent also becomes empty.
    /// Collapses root if it has a single remaining child.
    /// </summary>
    private void RemoveEmptyLeaf(int leafChunkId, ref ChunkAccessor<TStore> accessor)
    {
        byte* leafBase = accessor.GetChunkAddress(leafChunkId);
        int parentChunkId = SpatialNodeHelper.GetParentChunkId(leafBase);

        byte* parentBase = accessor.GetChunkAddress(parentChunkId, true);
        SpinWriteLock(parentBase, out var parentLatch);

        // Find and remove the entry pointing to this leaf
        int parentCount = SpatialNodeHelper.GetCount(parentBase);
        int leafIdx = FindChildIndex(parentBase, leafChunkId, parentCount);

        if (leafIdx >= 0)
        {
            int lastIdx = parentCount - 1;
            if (leafIdx != lastIdx)
            {
                SpatialNodeHelper.CopyInternalEntry(parentBase, lastIdx, leafIdx, _desc);

                // Update the moved child's parent pointer (it stays in the same parent)
                int movedChildId = SpatialNodeHelper.ReadInternalChildId(parentBase, leafIdx, _desc);
                // Parent pointer is unchanged since the child is still in the same parent node
            }
            SpatialNodeHelper.SetCount(parentBase, lastIdx);
            SpatialNodeHelper.RefitInternalMBR(parentBase, _desc);
            RefitInternalUnionMask(parentBase, ref accessor);
        }

        parentLatch.WriteUnlock();
        FreedChunkSink?.Add(leafChunkId);
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
                accessor.GetChunkAddress(parentChunkId), 0, _desc);

            byte* newRootBase = accessor.GetChunkAddress(remainingChild, true);
            SpatialNodeHelper.SetParentChunkId(newRootBase, 0);

            FreedChunkSink?.Add(_rootChunkId);
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
    private int FindChildIndex(byte* nodeBase, int childChunkId, int count)
    {
        for (int i = 0; i < count; i++)
        {
            if (SpatialNodeHelper.ReadInternalChildId(nodeBase, i, _desc) == childChunkId)
            {
                return i;
            }
        }
        return -1;
    }
}
