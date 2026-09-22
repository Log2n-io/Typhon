using System;
using System.Runtime.CompilerServices;
using System.Threading;

namespace Typhon.Engine.Internals;

/// <summary>
/// Outcome of an in-place leaf update — see <see cref="SpatialRTree{TStore}.TryUpdateLeafEntryInPlace"/>.
/// </summary>
/// <remarks>
/// <b>Three states rather than a bool, because two of them mean opposite things.</b> A bool collapses "the bounds outgrew this leaf" together with "the
/// handle you gave me does not name your payload", and the caller's only sensible response to the first — remove at that location and reinsert — is a silent
/// data-loss bug in response to the second. That is not hypothetical: it is what <c>CellClusterTree.UpdateAt</c> did until #872 step 9 split them.
/// </remarks>
internal enum LeafUpdateResult
{
    /// <summary>The entry was rewritten in place. The leaf's MBR is now a superset of its entries and is owed a refit (<c>ST-07</c>).</summary>
    Updated,

    /// <summary>The new bounds leave the leaf's MBR. The caller must remove and reinsert — the ordinary <c>C5</c> escape.</summary>
    Escaped,

    /// <summary>
    /// The location does not name this payload: not a leaf, slot beyond the live count, or a different payload sitting there.
    /// </summary>
    /// <remarks>
    /// Under a single writer this is UNREACHABLE, because <c>PayloadBackPointers</c> is repaired on every relocation (<c>ST-05</c>) and retired on removal —
    /// so reaching it means <c>ST-05</c> has a gap. That makes these three checks the DETECTOR for such a gap, and the reason the caller must fail loudly
    /// rather than fall through to the escape path: removing at a location just proven not to be ours deletes a stranger and nulls its back-pointer, which is
    /// precisely the silent failure <c>ST-05</c> exists to describe.
    /// </remarks>
    PreconditionFailed,
}

internal unsafe partial class SpatialRTree<TStore>
{
    /// <summary>
    /// Insert a payload with its fat AABB coordinates into the tree. The payload is an entity id for the entity-level trees and a CLUSTER chunk id for the
    /// per-cell cluster trees (#872 step 9) — the tree itself never interprets it.
    /// </summary>
    /// <param name="payloadId">Opaque 64-bit identity for this entry — an EntityId value, or a cluster chunk id. Also the back-pointer key.</param>
    /// <param name="coords">CoordCount doubles ordered [min0, min1, ..., max0, max1, ...]</param>
    /// <param name="accessor">ChunkAccessor for page access</param>
    /// <param name="changeSet">ChangeSet for WAL participation</param>
    /// <param name="categoryMask">Category bitmask for filtering (default: uint.MaxValue = matches all queries)</param>
    /// <returns>(leafChunkId, slotIndex) for back-pointer storage.</returns>
    internal (int leafChunkId, int slotIndex) Insert(long payloadId, ReadOnlySpan<double> coords, ref ChunkAccessor<TStore> accessor, 
        ChangeSet changeSet = null, uint categoryMask = uint.MaxValue)
    {
        using var insertSpan = TyphonEvent.BeginSpatialRTreeInsert(payloadId);
        byte restartCount = 0;
        while (true)
        {
            var result = TryInsert(payloadId, coords, ref accessor, changeSet, categoryMask);
            if (result.success)
            {
                if (TelemetryConfig.SpatialRTreeInsertActive)
                {
                    // Note: fields can't be set on `using var` ref-struct — restart count and depth are diagnostic-only and 0 is acceptable here.
                    // (When forensic depth/restart needed, wire as parameters in BeginX like UpdateSlowPath.)
                }

                // Record the NEW payload's location, so PayloadBackPointers is the single authoritative handle store rather than a repair channel that only
                // covers relocations. Without this the array is silently incomplete: Split repairs entries it moves and Remove repairs the swap, but nothing
                // ever writes an entry that was inserted and never moved. A caller then has to merge this array with Insert's return value, which is two
                // sources for one fact — and the moment it keeps its own copy, a relocation caused by ANOTHER payload's removal makes that copy stale, the
                // identity check in TryUpdateLeafEntryInPlace refuses, and the escape path removes whatever now occupies the old slot. Writing here is done
                // AFTER TryInsert returns, so it names the entry's final position even when the insert split the leaf out from under it.
                var payloadBackPointers = PayloadBackPointers;
                if (payloadBackPointers != null)
                {
                    WritePayloadHandle(payloadBackPointers, payloadId, PackHandle(result.leafChunkId, result.slotIndex));
                }
                return (result.leafChunkId, result.slotIndex);
            }
            // OLC restart — spin briefly then retry descent
            if (restartCount < 255)
            {
                restartCount++;
            }
        }
    }

    /// <summary>
    /// Overwrite a leaf entry's bounds without touching the tree's structure, when the new bounds still fit inside the leaf's existing MBR — the in-place
    /// half of <c>C5</c>'s escape-bound maintenance (#872 step 9).
    /// </summary>
    /// <returns>
    /// <see cref="LeafUpdateResult.Updated"/>, <see cref="LeafUpdateResult.Escaped"/> when the caller must remove and reinsert, or
    /// <see cref="LeafUpdateResult.PreconditionFailed"/> when the location does not name this payload — which the caller must NOT treat as an escape.
    /// The entry is left untouched in both non-updated cases.
    /// </returns>
    /// <remarks>
    /// <para><b>The leaf MBR is deliberately NOT refitted.</b> That is the entire saving: a refit walks eleven entries and then every ancestor, which is the
    /// ~500 ns path this method exists to avoid on a box that merely drifted. The consequence is that the leaf's MBR becomes a strict superset of the union
    /// of its entries, which <c>ST-01</c> states as equality — so the looseness must not outlive the exclusive window. The caller refits the leaves it touched
    /// before the window closes; queries run after it, and see equality.</para>
    /// <para><b>The identity check is not a formality.</b> A handle that no longer names its own payload is exactly the failure the payload back-pointers were
    /// added to prevent, and writing through one would put this cluster's bounds into another cluster's slot — <c>CA-01</c> broken in both directions with no
    /// exception anywhere near it. Refusing here converts a silent corruption into a reinsert, which is merely slower.</para>
    /// <para><b>Category masks widen rather than shrink.</b> A mask carrying a bit the node's union does not have would make <c>ST-02</c> false, so the union
    /// is widened to include it. The reverse — a mask that drops bits — leaves the union too broad, which costs a redundant visit and nothing else, and
    /// tightening it would require re-scanning every sibling entry.</para>
    /// </remarks>
    internal LeafUpdateResult TryUpdateLeafEntryInPlace(int leafChunkId, int slotIndex, long payloadId, ReadOnlySpan<double> coords, uint categoryMask,
        ref ChunkAccessor<TStore> accessor)
    {
        if (leafChunkId <= 0 || (uint)slotIndex >= (uint)_desc.LeafCapacity)
        {
            return LeafUpdateResult.PreconditionFailed;
        }

        byte* leafBase = accessor.GetChunkAddress(leafChunkId, true);
        if (!SpatialNodeHelper.IsLeaf(leafBase) || slotIndex >= SpatialNodeHelper.GetCount(leafBase))
        {
            return LeafUpdateResult.PreconditionFailed;
        }

        if (SpatialNodeHelper.ReadLeafEntityId(leafBase, slotIndex, _desc) != payloadId)
        {
            return LeafUpdateResult.PreconditionFailed;
        }

        // Containment against the leaf's own MBR, on every axis. Any escape sends the caller to the reinsert path.
        int halfCoord = _desc.CoordCount / 2;
        for (int d = 0; d < halfCoord; d++)
        {
            if (coords[d] < SpatialNodeHelper.ReadNodeMBRCoord(leafBase, d, _desc)
                || coords[d + halfCoord] > SpatialNodeHelper.ReadNodeMBRCoord(leafBase, d + halfCoord, _desc))
            {
                // The ONLY geometric exit. Everything else above and below is a broken precondition, and the caller must treat the two differently.
                return LeafUpdateResult.Escaped;
            }
        }

        SpinWriteLock(leafBase, out var latch);
        try
        {
            // Re-checked under the latch: between the containment test above and this point a concurrent split could have moved the entry out from under us.
            // Inside the exclusive window that cannot happen and the latch is free; outside it, this is what keeps the write from landing on a stranger.
            if (!SpatialNodeHelper.IsLeaf(leafBase) || slotIndex >= SpatialNodeHelper.GetCount(leafBase)
                || SpatialNodeHelper.ReadLeafEntityId(leafBase, slotIndex, _desc) != payloadId)
            {
                return LeafUpdateResult.PreconditionFailed;
            }

            SpatialNodeHelper.WriteLeafEntryCoords(leafBase, slotIndex, coords, _desc);
            SpatialNodeHelper.WriteLeafCategoryMask(leafBase, slotIndex, categoryMask, _desc);

            uint union = SpatialNodeHelper.ReadUnionCategoryMask(leafBase, _desc);
            if ((union | categoryMask) != union)
            {
                SpatialNodeHelper.ExpandLeafMBR(leafBase, slotIndex, categoryMask, _desc);
            }
        }
        finally
        {
            latch.WriteUnlock();
        }

        Interlocked.Increment(ref _mutationVersion);
        return LeafUpdateResult.Updated;
    }

    private (bool success, int leafChunkId, int slotIndex) TryInsert(long payloadId, ReadOnlySpan<double> coords, ref ChunkAccessor<TStore> accessor, 
        ChangeSet changeSet, uint categoryMask)
    {
        DescentPath path = default;
        int nodeChunkId = _rootChunkId;

        // ── Descent to best leaf ──
        while (true)
        {
            byte* nodeBase = accessor.GetChunkAddress(nodeChunkId);
            if (SpatialNodeHelper.IsLeaf(nodeBase))
            {
                break;
            }

            var latch = GetLatch(nodeBase);
            int version = latch.ReadVersion();
            if (version == 0)
            {
                return default; // locked/obsolete → restart
            }

            int count = SpatialNodeHelper.GetCount(nodeBase);
            int bestChild = ChooseBestChild(nodeBase, coords, count);
            int childChunkId = SpatialNodeHelper.ReadInternalChildId(nodeBase, bestChild, _desc);

            if (!latch.ValidateVersion(version))
            {
                return default; // concurrent modification → restart
            }

            path.Push(nodeChunkId, bestChild, version);
            nodeChunkId = childChunkId;
        }

        // ── Insert into leaf ──
        byte* leafBase = accessor.GetChunkAddress(nodeChunkId, true);
        SpinWriteLock(leafBase, out var leafLatch);

        int leafCount = SpatialNodeHelper.GetCount(leafBase);

        if (leafCount < _desc.LeafCapacity)
        {
            // Room available: append at leafCount position
            WriteLeafEntry(leafBase, leafCount, payloadId, coords, categoryMask);
            SpatialNodeHelper.SetCount(leafBase, leafCount + 1);
            if (leafCount == 0)
            {
                SpatialNodeHelper.RefitLeafMBR(leafBase, _desc);
            }
            else
            {
                SpatialNodeHelper.ExpandLeafMBR(leafBase, leafCount, categoryMask, _desc);
            }
            leafLatch.WriteUnlock();

            Interlocked.Increment(ref _entityCount);
            Interlocked.Increment(ref _mutationVersion);
            RefitAncestors(ref path, ref accessor);
            SyncMetadata(ref accessor);
            return (true, nodeChunkId, leafCount);
        }

        // Leaf full: need split
        leafLatch.WriteUnlock();
        return InsertWithSplit(payloadId, coords, nodeChunkId, ref path, ref accessor, changeSet, categoryMask);
    }

    /// <summary>
    /// Find the child whose MBR requires minimum enlargement to include the given coords.
    /// Tie-break: prefer child with smallest existing area/volume.
    /// </summary>
    private int ChooseBestChild(byte* nodeBase, ReadOnlySpan<double> coords, int count)
    {
        int bestChild = 0;
        double bestEnlargement = double.MaxValue;
        double bestArea = double.MaxValue;

        if (_desc.CoordCount == 4)
        {
            // 2D fast path: fully unrolled, no inner loop
            double c0 = coords[0], c1 = coords[1], c2 = coords[2], c3 = coords[3];
            for (int i = 0; i < count; i++)
            {
                double cMinX = SpatialNodeHelper.ReadInternalCoord(nodeBase, i, 0, _desc);
                double cMinY = SpatialNodeHelper.ReadInternalCoord(nodeBase, i, 1, _desc);
                double cMaxX = SpatialNodeHelper.ReadInternalCoord(nodeBase, i, 2, _desc);
                double cMaxY = SpatialNodeHelper.ReadInternalCoord(nodeBase, i, 3, _desc);

                double w = cMaxX - cMinX;
                double h = cMaxY - cMinY;
                double area = w * h;
                double ew = Math.Max(cMaxX, c2) - Math.Min(cMinX, c0);
                double eh = Math.Max(cMaxY, c3) - Math.Min(cMinY, c1);
                double enlargement = ew * eh - area;

                if (enlargement < bestEnlargement || (enlargement == bestEnlargement && area < bestArea))
                {
                    bestChild = i;
                    bestEnlargement = enlargement;
                    bestArea = area;
                }
            }
        }
        else
        {
            int halfCoord = _desc.CoordCount / 2;
            for (int i = 0; i < count; i++)
            {
                double area = 1.0;
                double enlargedArea = 1.0;

                for (int d = 0; d < halfCoord; d++)
                {
                    double cMin = SpatialNodeHelper.ReadInternalCoord(nodeBase, i, d, _desc);
                    double cMax = SpatialNodeHelper.ReadInternalCoord(nodeBase, i, d + halfCoord, _desc);
                    double eMin = Math.Min(cMin, coords[d]);
                    double eMax = Math.Max(cMax, coords[d + halfCoord]);
                    area *= (cMax - cMin);
                    enlargedArea *= (eMax - eMin);
                }

                double enlargement = enlargedArea - area;
                if (enlargement < bestEnlargement || (enlargement == bestEnlargement && area < bestArea))
                {
                    bestChild = i;
                    bestEnlargement = enlargement;
                    bestArea = area;
                }
            }
        }

        return bestChild;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void WriteLeafEntry(byte* nodeBase, int index, long payloadId, ReadOnlySpan<double> coords, uint categoryMask = uint.MaxValue)
    {
        SpatialNodeHelper.WriteLeafEntryCoords(nodeBase, index, coords, _desc);
        SpatialNodeHelper.WriteLeafEntityId(nodeBase, index, payloadId, _desc);
        SpatialNodeHelper.WriteLeafCategoryMask(nodeBase, index, categoryMask, _desc);
    }

    private void WriteInternalEntry(byte* nodeBase, int index, int childChunkId, ref ChunkAccessor<TStore> accessor)
    {
        byte* childBase = accessor.GetChunkAddress(childChunkId);
        for (int c = 0; c < _desc.CoordCount; c++)
        {
            SpatialNodeHelper.WriteInternalCoord(nodeBase, index, c, SpatialNodeHelper.ReadNodeMBRCoord(childBase, c, _desc), _desc);
        }
        SpatialNodeHelper.WriteInternalChildId(nodeBase, index, childChunkId, _desc);
    }
}
