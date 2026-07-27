using System;
using System.Runtime.CompilerServices;
using System.Threading;

namespace Typhon.Engine.Internals;

internal unsafe partial class SpatialRTree<TStore>
{
    /// <summary>
    /// Insert an entity with its fat AABB coordinates into the tree.
    /// </summary>
    /// <param name="entityId">Raw EntityId value (64-bit)</param>
    /// <param name="componentChunkId">Component CBS chunk ID for back-pointer storage (0 for standalone tests)</param>
    /// <param name="coords">CoordCount doubles ordered [min0, min1, ..., max0, max1, ...]</param>
    /// <param name="accessor">ChunkAccessor for page access</param>
    /// <param name="changeSet">ChangeSet for WAL participation</param>
    /// <param name="categoryMask">Category bitmask for filtering (default: uint.MaxValue = matches all queries)</param>
    /// <returns>(leafChunkId, slotIndex) for back-pointer storage.</returns>
    internal (int leafChunkId, int slotIndex) Insert(long entityId, int componentChunkId, ReadOnlySpan<double> coords, ref ChunkAccessor<TStore> accessor,
        ChangeSet changeSet = null, uint categoryMask = uint.MaxValue)
    {
        using var insertSpan = TyphonEvent.BeginSpatialRTreeInsert(entityId);
        byte restartCount = 0;
        while (true)
        {
            var result = TryInsert(entityId, componentChunkId, coords, ref accessor, changeSet, categoryMask);
            if (result.success)
            {
                if (TelemetryConfig.SpatialRTreeInsertActive)
                {
                    // Note: fields can't be set on `using var` ref-struct — restart count and depth are diagnostic-only and 0 is acceptable here.
                    // (When forensic depth/restart needed, wire as parameters in BeginX like UpdateSlowPath.)
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

    /// <summary>Backward-compatible overload for standalone tree tests (no back-pointer tracking).</summary>
    internal (int leafChunkId, int slotIndex) Insert(long entityId, ReadOnlySpan<double> coords, ref ChunkAccessor<TStore> accessor, ChangeSet changeSet = null,
        uint categoryMask = uint.MaxValue) => Insert(entityId, 0, coords, ref accessor, changeSet, categoryMask);

    private (bool success, int leafChunkId, int slotIndex) TryInsert(long entityId, int componentChunkId, ReadOnlySpan<double> coords,
        ref ChunkAccessor<TStore> accessor, ChangeSet changeSet, uint categoryMask)
    {
        DescentPath path = default;
        int nodeChunkId = _rootChunkId;

        // ── Descent to best leaf ──
        while (true)
        {
            ref byte nodeBase = ref Unsafe.AsRef<byte>(accessor.GetChunkAddress(nodeChunkId));
            if (SpatialNodeHelper.IsLeaf(ref nodeBase))
            {
                break;
            }

            var latch = GetLatch(ref nodeBase);
            int version = latch.ReadVersion();
            if (version == 0)
            {
                return default; // locked/obsolete → restart
            }

            int count = SpatialNodeHelper.GetCount(ref nodeBase);
            int bestChild = ChooseBestChild(ref nodeBase, coords, count);
            int childChunkId = SpatialNodeHelper.ReadInternalChildId(ref nodeBase, bestChild, _desc);

            if (!latch.ValidateVersion(version))
            {
                return default; // concurrent modification → restart
            }

            path.Push(nodeChunkId, bestChild, version);
            nodeChunkId = childChunkId;
        }

        // ── Insert into leaf ──
        ref byte leafBase = ref Unsafe.AsRef<byte>(accessor.GetChunkAddress(nodeChunkId, true));
        SpinWriteLock(ref leafBase, out var leafLatch);

        int leafCount = SpatialNodeHelper.GetCount(ref leafBase);

        if (leafCount < _desc.LeafCapacity)
        {
            // Room available: append at leafCount position
            WriteLeafEntry(ref leafBase, leafCount, entityId, componentChunkId, coords, categoryMask);
            SpatialNodeHelper.SetCount(ref leafBase, leafCount + 1);
            if (leafCount == 0)
            {
                SpatialNodeHelper.RefitLeafMBR(ref leafBase, _desc);
            }
            else
            {
                SpatialNodeHelper.ExpandLeafMBR(ref leafBase, leafCount, categoryMask, _desc);
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
        return InsertWithSplit(entityId, componentChunkId, coords, nodeChunkId, ref path, ref accessor, changeSet, categoryMask);
    }

    /// <summary>
    /// Find the child whose MBR requires minimum enlargement to include the given coords.
    /// Tie-break: prefer child with smallest existing area/volume.
    /// </summary>
    private int ChooseBestChild(ref byte nodeBase, ReadOnlySpan<double> coords, int count)
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
                double cMinX = SpatialNodeHelper.ReadInternalCoord(ref nodeBase, i, 0, _desc);
                double cMinY = SpatialNodeHelper.ReadInternalCoord(ref nodeBase, i, 1, _desc);
                double cMaxX = SpatialNodeHelper.ReadInternalCoord(ref nodeBase, i, 2, _desc);
                double cMaxY = SpatialNodeHelper.ReadInternalCoord(ref nodeBase, i, 3, _desc);

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
                    double cMin = SpatialNodeHelper.ReadInternalCoord(ref nodeBase, i, d, _desc);
                    double cMax = SpatialNodeHelper.ReadInternalCoord(ref nodeBase, i, d + halfCoord, _desc);
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
    private void WriteLeafEntry(ref byte nodeBase, int index, long entityId, int componentChunkId, ReadOnlySpan<double> coords, uint categoryMask = uint.MaxValue)
    {
        SpatialNodeHelper.WriteLeafEntryCoords(ref nodeBase, index, coords, _desc);
        SpatialNodeHelper.WriteLeafEntityId(ref nodeBase, index, entityId, _desc);
        SpatialNodeHelper.WriteLeafCompChunkId(ref nodeBase, index, componentChunkId, _desc);
        SpatialNodeHelper.WriteLeafCategoryMask(ref nodeBase, index, categoryMask, _desc);
    }

    private void WriteInternalEntry(ref byte nodeBase, int index, int childChunkId, ref ChunkAccessor<TStore> accessor)
    {
        ref byte childBase = ref Unsafe.AsRef<byte>(accessor.GetChunkAddress(childChunkId));
        for (int c = 0; c < _desc.CoordCount; c++)
        {
            SpatialNodeHelper.WriteInternalCoord(ref nodeBase, index, c,
                SpatialNodeHelper.ReadNodeMBRCoord(ref childBase, c, _desc), _desc);
        }
        SpatialNodeHelper.WriteInternalChildId(ref nodeBase, index, childChunkId, _desc);
    }
}
