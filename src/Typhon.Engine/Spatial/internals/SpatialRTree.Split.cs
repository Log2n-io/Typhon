using System;
using System.Threading;

namespace Typhon.Engine.Internals;

internal unsafe partial class SpatialRTree<TStore>
{
    /// <summary>
    /// Insert into a full leaf, triggering an R*-overlap-minimizing split.
    /// Handles cascading splits up to the root.
    /// </summary>
    private (bool success, int leafChunkId, int slotIndex) InsertWithSplit(long entityId, int componentChunkId, ReadOnlySpan<double> coords,
        int fullLeafChunkId, ref DescentPath path, ref ChunkAccessor<TStore> accessor, ChangeSet changeSet, uint categoryMask)
    {
        // Phase 3: Spatial:RTree:NodeSplit span (leaf-level). LeftCount/RightCount filled after split. Depth=0 (leaf), SplitAxis=0 (FindBestSplit doesn't expose axis).
        var splitScope = TyphonEvent.BeginSpatialRTreeNodeSplit(0);
        try
        {
            byte* leafBase = accessor.GetChunkAddress(fullLeafChunkId, true);
            SpinWriteLock(leafBase, out var leafLatch);

            int leafCount = SpatialNodeHelper.GetCount(leafBase);
            int totalEntries = leafCount + 1;

            // Gather all entries (existing + new) into temporary stackalloc buffers
            int maxCoords = totalEntries * _desc.CoordCount;
            Span<double> tempCoords = stackalloc double[maxCoords];
            Span<long> tempIds = stackalloc long[totalEntries];
            Span<int> tempCompChunkIds = stackalloc int[totalEntries];
            Span<uint> tempCategoryMasks = stackalloc uint[totalEntries];
            Span<int> sortIndices = stackalloc int[totalEntries];
            Span<int> bestPerm = stackalloc int[totalEntries];

            for (int i = 0; i < leafCount; i++)
            {
                SpatialNodeHelper.ReadLeafEntryCoords(leafBase, i, tempCoords.Slice(i * _desc.CoordCount, _desc.CoordCount), _desc);
                tempIds[i] = SpatialNodeHelper.ReadLeafEntityId(leafBase, i, _desc);
                tempCompChunkIds[i] = SpatialNodeHelper.ReadLeafCompChunkId(leafBase, i, _desc);
                tempCategoryMasks[i] = SpatialNodeHelper.ReadLeafCategoryMask(leafBase, i, _desc);
            }
            coords.CopyTo(tempCoords.Slice(leafCount * _desc.CoordCount, _desc.CoordCount));
            tempIds[leafCount] = entityId;
            tempCompChunkIds[leafCount] = componentChunkId;
            tempCategoryMasks[leafCount] = categoryMask;

            int splitPos = FindBestSplit(tempCoords, totalEntries, sortIndices, bestPerm);

            // Allocate right sibling — NOTE: AllocNode may trigger segment Grow(),
            // invalidating all previously obtained byte* pointers. Re-obtain after.
            int parentChunkId = SpatialNodeHelper.GetParentChunkId(leafBase);
            int rightChunkId = AllocNode(true, parentChunkId, ref accessor, changeSet);
            Interlocked.Increment(ref _nodeCount);

            // Re-obtain pointers after potential segment growth
            leafBase = accessor.GetChunkAddress(fullLeafChunkId, true);
            leafLatch = GetLatch(leafBase);
            byte* rightBase = accessor.GetChunkAddress(rightChunkId, true);

            // Scatter entries to left and right using bestPerm (includes componentChunkIds and categoryMasks)
            ScatterLeafEntries(leafBase, fullLeafChunkId, tempCoords, tempIds, tempCompChunkIds, tempCategoryMasks, bestPerm, 0, splitPos);
            SpatialNodeHelper.SetCount(leafBase, splitPos);
            SpatialNodeHelper.RefitLeafMBR(leafBase, _desc);

            int rightCount = totalEntries - splitPos;
            ScatterLeafEntries(rightBase, rightChunkId, tempCoords, tempIds, tempCompChunkIds, tempCategoryMasks, bestPerm, splitPos, totalEntries);
            SpatialNodeHelper.SetCount(rightBase, rightCount);
            SpatialNodeHelper.RefitLeafMBR(rightBase, _desc);

            leafLatch.WriteUnlock();

            // Find where the new entity ended up
            int newEntityLeaf = fullLeafChunkId;
            int newEntitySlot = FindEntitySlot(tempIds, bestPerm, entityId, 0, splitPos);
            if (newEntitySlot < 0)
            {
                newEntityLeaf = rightChunkId;
                newEntitySlot = FindEntitySlot(tempIds, bestPerm, entityId, splitPos, totalEntries) - splitPos;
            }

            // Propagate split upward
            PropagateSplit(fullLeafChunkId, rightChunkId, ref path, ref accessor, changeSet);

            _entityCount++;
            _mutationVersion++;
            SyncMetadata(ref accessor);
            splitScope.LeftCount = (byte)Math.Min(splitPos, byte.MaxValue);
            splitScope.RightCount = (byte)Math.Min(rightCount, byte.MaxValue);
            return (true, newEntityLeaf, newEntitySlot);
        }
        finally
        {
            splitScope.Dispose();
        }
    }

    /// <summary>
    /// Scatter leaf entries from temp buffers into a node using the permutation.
    /// Writes coords, entityIds, and componentChunkIds. If <see cref="BackPointerSegment"/> is set,
    /// updates back-pointers directly using the stored componentChunkIds (O(1) per entry, no EntityMap lookup).
    /// </summary>
    private void ScatterLeafEntries(byte* nodeBase, int leafChunkId, Span<double> allCoords, Span<long> allIds, Span<int> allCompChunkIds,
        Span<uint> allCategoryMasks, Span<int> perm, int permStart, int permEnd)
    {
        for (int i = permStart; i < permEnd; i++)
        {
            int src = perm[i];
            int dst = i - permStart;
            SpatialNodeHelper.WriteLeafEntryCoords(nodeBase, dst,
                allCoords.Slice(src * _desc.CoordCount, _desc.CoordCount), _desc);
            SpatialNodeHelper.WriteLeafEntityId(nodeBase, dst, allIds[src], _desc);
            SpatialNodeHelper.WriteLeafCompChunkId(nodeBase, dst, allCompChunkIds[src], _desc);
            SpatialNodeHelper.WriteLeafCategoryMask(nodeBase, dst, allCategoryMasks[src], _desc);
        }

        // Update back-pointers for all scattered entries using stored componentChunkIds
        if (BackPointerSegment != null)
        {
            var bpAccessor = BackPointerSegment.CreateChunkAccessor();
            try
            {
                // Read TreeSelector from the first valid entry (same for all entries in this tree)
                byte treeSelector = 0;
                for (int i = permStart; i < permEnd; i++)
                {
                    int compChunkId = allCompChunkIds[perm[i]];
                    if (compChunkId != 0)
                    {
                        treeSelector = SpatialBackPointerHelper.Read(ref bpAccessor, compChunkId).TreeSelector;
                        break;
                    }
                }

                for (int i = permStart; i < permEnd; i++)
                {
                    int src = perm[i];
                    int dst = i - permStart;
                    int compChunkId = allCompChunkIds[src];
                    if (compChunkId != 0)
                    {
                        SpatialBackPointerHelper.Write(ref bpAccessor, compChunkId, leafChunkId, (short)dst, treeSelector);
                    }
                }
            }
            finally
            {
                bpAccessor.Dispose();
            }
        }
    }

    private static int FindEntitySlot(Span<long> ids, Span<int> perm, long entityId, int start, int end)
    {
        for (int i = start; i < end; i++)
        {
            if (ids[perm[i]] == entityId)
            {
                return i;
            }
        }
        return -1;
    }

    /// <summary>
    /// R*-overlap-minimizing split heuristic. For each axis × (lower, upper bound), sort entries and evaluate all valid split positions. Choose the split with
    /// minimum overlap; tie-break by minimum total area.
    /// </summary>
    /// <returns>The split position in bestPerm (left gets [0..splitPos-1], right gets [splitPos..total-1]).</returns>
    private int FindBestSplit(Span<double> coords, int totalEntries, Span<int> sortIndices, Span<int> bestPerm)
    {
        int halfCoord = _desc.CoordCount / 2;
        int minFill = _desc.MinFill;
        int maxSplitPos = totalEntries - minFill;

        double bestOverlap = double.MaxValue;
        double bestArea = double.MaxValue;
        int bestSplitPos = minFill;

        // For each spatial axis
        for (int axis = 0; axis < halfCoord; axis++)
        {
            // Test sort by lower bound and upper bound
            for (int pass = 0; pass < 2; pass++)
            {
                int coordIdx = pass == 0 ? axis : axis + halfCoord;

                // Initialize sort indices
                for (int i = 0; i < totalEntries; i++)
                {
                    sortIndices[i] = i;
                }

                // Insertion sort (optimal for N ≤ 25)
                InsertionSortByCoord(sortIndices, coords, coordIdx, totalEntries);

                // Evaluate each valid split position
                for (int k = minFill; k <= maxSplitPos; k++)
                {
                    double overlap = ComputeSplitOverlap(coords, sortIndices, k, totalEntries);
                    double area = ComputeSplitTotalArea(coords, sortIndices, k, totalEntries);

                    if (overlap < bestOverlap || (overlap == bestOverlap && area < bestArea))
                    {
                        bestOverlap = overlap;
                        bestArea = area;
                        bestSplitPos = k;
                        sortIndices.Slice(0, totalEntries).CopyTo(bestPerm);
                    }
                }
            }
        }

        return bestSplitPos;
    }

    private void InsertionSortByCoord(Span<int> indices, Span<double> coords, int coordIdx, int count)
    {
        int cc = _desc.CoordCount;
        for (int i = 1; i < count; i++)
        {
            int key = indices[i];
            double keyVal = coords[key * cc + coordIdx];
            int j = i - 1;
            while (j >= 0 && coords[indices[j] * cc + coordIdx] > keyVal)
            {
                indices[j + 1] = indices[j];
                j--;
            }
            indices[j + 1] = key;
        }
    }

    /// <summary>Compute the overlap area/volume between group A's MBR and group B's MBR.</summary>
    private double ComputeSplitOverlap(Span<double> coords, Span<int> indices, int splitPos, int total)
    {
        int halfCoord = _desc.CoordCount / 2;
        int cc = _desc.CoordCount;

        // Compute MBR for group A [0..splitPos-1]
        Span<double> aMbr = stackalloc double[cc];
        InitMBRFromEntry(aMbr, coords, indices[0], cc);
        for (int i = 1; i < splitPos; i++)
        {
            ExpandMBR(aMbr, coords, indices[i], cc, halfCoord);
        }

        // Compute MBR for group B [splitPos..total-1]
        Span<double> bMbr = stackalloc double[cc];
        InitMBRFromEntry(bMbr, coords, indices[splitPos], cc);
        for (int i = splitPos + 1; i < total; i++)
        {
            ExpandMBR(bMbr, coords, indices[i], cc, halfCoord);
        }

        // Compute overlap: product of max(0, min(aMax,bMax) - max(aMin,bMin)) per dimension
        double overlap = 1.0;
        for (int d = 0; d < halfCoord; d++)
        {
            double lo = Math.Max(aMbr[d], bMbr[d]);
            double hi = Math.Min(aMbr[d + halfCoord], bMbr[d + halfCoord]);
            if (hi <= lo)
            {
                return 0.0; // No overlap
            }
            overlap *= (hi - lo);
        }
        return overlap;
    }

    /// <summary>Compute the total area/volume of group A's MBR + group B's MBR.</summary>
    private double ComputeSplitTotalArea(Span<double> coords, Span<int> indices, int splitPos, int total)
    {
        int halfCoord = _desc.CoordCount / 2;
        int cc = _desc.CoordCount;

        Span<double> aMbr = stackalloc double[cc];
        InitMBRFromEntry(aMbr, coords, indices[0], cc);
        for (int i = 1; i < splitPos; i++)
        {
            ExpandMBR(aMbr, coords, indices[i], cc, halfCoord);
        }

        Span<double> bMbr = stackalloc double[cc];
        InitMBRFromEntry(bMbr, coords, indices[splitPos], cc);
        for (int i = splitPos + 1; i < total; i++)
        {
            ExpandMBR(bMbr, coords, indices[i], cc, halfCoord);
        }

        return ComputeArea(aMbr, halfCoord) + ComputeArea(bMbr, halfCoord);
    }

    private static void InitMBRFromEntry(Span<double> mbr, Span<double> coords, int entryIdx, int coordCount) => 
        coords.Slice(entryIdx * coordCount, coordCount).CopyTo(mbr);

    private static void ExpandMBR(Span<double> mbr, Span<double> coords, int entryIdx, int cc, int halfCoord)
    {
        int offset = entryIdx * cc;
        for (int c = 0; c < halfCoord; c++)
        {
            double v = coords[offset + c];
            if (v < mbr[c])
            {
                mbr[c] = v;
            }
        }
        for (int c = halfCoord; c < cc; c++)
        {
            double v = coords[offset + c];
            if (v > mbr[c])
            {
                mbr[c] = v;
            }
        }
    }

    private static double ComputeArea(Span<double> mbr, int halfCoord)
    {
        double area = 1.0;
        for (int d = 0; d < halfCoord; d++)
        {
            area *= (mbr[d + halfCoord] - mbr[d]);
        }
        return area;
    }

    /// <summary>
    /// Propagate a split upward: insert the right sibling into the parent.
    /// If the parent is also full, cascade the split. If we reach the root, create a new root.
    /// </summary>
    private void PropagateSplit(int leftChunkId, int rightChunkId, ref DescentPath path, ref ChunkAccessor<TStore> accessor, ChangeSet changeSet)
    {
        int childToInsert = rightChunkId;

        for (int level = path.Depth - 1; level >= 0; level--)
        {
            int parentChunkId = path.ChunkIds[level];
            int leftIdx = path.ChildIndices[level];

            byte* parentBase = accessor.GetChunkAddress(parentChunkId, true);
            SpinWriteLock(parentBase, out var parentLatch);

            // Update left child's MBR in parent
            byte* leftBase = accessor.GetChunkAddress(leftChunkId);
            for (int c = 0; c < _desc.CoordCount; c++)
            {
                SpatialNodeHelper.WriteInternalCoord(parentBase, leftIdx, c, SpatialNodeHelper.ReadNodeMBRCoord(leftBase, c, _desc), _desc);
            }

            int parentCount = SpatialNodeHelper.GetCount(parentBase);

            if (parentCount < _desc.InternalCapacity)
            {
                // Room in parent: insert right child at end
                WriteInternalEntry(parentBase, parentCount, childToInsert, ref accessor);
                SpatialNodeHelper.SetCount(parentBase, parentCount + 1);

                // Update right child's parent pointer
                byte* rightBase = accessor.GetChunkAddress(childToInsert, true);
                SpatialNodeHelper.SetParentChunkId(rightBase, parentChunkId);

                SpatialNodeHelper.RefitInternalMBR(parentBase, _desc);
                RefitInternalUnionMask(parentBase, ref accessor);
                parentLatch.WriteUnlock();

                // Refit remaining ancestors
                for (int upper = level - 1; upper >= 0; upper--)
                {
                    int ancestorChunkId = path.ChunkIds[upper];
                    byte* ancestorBase = accessor.GetChunkAddress(ancestorChunkId, true);
                    SpinWriteLock(ancestorBase, out var ancestorLatch);
                    SpatialNodeHelper.RefitInternalMBR(ancestorBase, _desc);
                    RefitInternalUnionMask(ancestorBase, ref accessor);
                    ancestorLatch.WriteUnlock();
                }
                return;
            }

            // Parent also full: split the internal node
            parentLatch.WriteUnlock();
            int newInternalChunkId = SplitInternalNode(parentChunkId, childToInsert, ref accessor, changeSet);

            leftChunkId = parentChunkId;
            childToInsert = newInternalChunkId;
        }

        // Reached root without absorbing the split: create new root
        CreateNewRoot(leftChunkId, childToInsert, ref accessor, changeSet);
    }

    /// <summary>Split an internal node, returning the new right sibling's chunk ID.</summary>
    private int SplitInternalNode(int nodeChunkId, int newChildChunkId, ref ChunkAccessor<TStore> accessor, ChangeSet changeSet)
    {
        byte* nodeBase = accessor.GetChunkAddress(nodeChunkId, true);
        SpinWriteLock(nodeBase, out var nodeLatch);

        int nodeCount = SpatialNodeHelper.GetCount(nodeBase);
        int totalEntries = nodeCount + 1;
        int cc = _desc.CoordCount;

        Span<double> tempCoords = stackalloc double[totalEntries * cc];
        Span<int> tempChildIds = stackalloc int[totalEntries];
        Span<int> sortIndices = stackalloc int[totalEntries];
        Span<int> bestPerm = stackalloc int[totalEntries];

        // Gather existing internal entries
        for (int i = 0; i < nodeCount; i++)
        {
            SpatialNodeHelper.ReadInternalEntryCoords(nodeBase, i, tempCoords.Slice(i * cc, cc), _desc);
            tempChildIds[i] = SpatialNodeHelper.ReadInternalChildId(nodeBase, i, _desc);
        }

        // Add the new child entry
        byte* newChildBase = accessor.GetChunkAddress(newChildChunkId);
        for (int c = 0; c < cc; c++)
        {
            tempCoords[nodeCount * cc + c] = SpatialNodeHelper.ReadNodeMBRCoord(newChildBase, c, _desc);
        }
        tempChildIds[nodeCount] = newChildChunkId;

        int splitPos = FindBestSplit(tempCoords, totalEntries, sortIndices, bestPerm);

        // Allocate right sibling — NOTE: AllocNode may trigger segment Grow(),
        // invalidating all previously obtained byte* pointers. Re-obtain after.
        int parentChunkId = SpatialNodeHelper.GetParentChunkId(nodeBase);
        int rightChunkId = AllocNode(false, parentChunkId, ref accessor, changeSet);
        Interlocked.Increment(ref _nodeCount);

        // Re-obtain pointers after potential segment growth
        nodeBase = accessor.GetChunkAddress(nodeChunkId, true);
        nodeLatch = GetLatch(nodeBase);
        byte* rightBase = accessor.GetChunkAddress(rightChunkId, true);

        // Scatter entries to left (existing node) and right (new sibling)
        for (int i = 0; i < splitPos; i++)
        {
            int src = bestPerm[i];
            SpatialNodeHelper.WriteInternalEntryCoords(nodeBase, i, tempCoords.Slice(src * cc, cc), _desc);
            SpatialNodeHelper.WriteInternalChildId(nodeBase, i, tempChildIds[src], _desc);
        }
        SpatialNodeHelper.SetCount(nodeBase, splitPos);
        SpatialNodeHelper.RefitInternalMBR(nodeBase, _desc);
        RefitInternalUnionMask(nodeBase, ref accessor);

        int rightCount = totalEntries - splitPos;
        for (int i = 0; i < rightCount; i++)
        {
            int src = bestPerm[splitPos + i];
            SpatialNodeHelper.WriteInternalEntryCoords(rightBase, i, tempCoords.Slice(src * cc, cc), _desc);
            SpatialNodeHelper.WriteInternalChildId(rightBase, i, tempChildIds[src], _desc);
        }
        SpatialNodeHelper.SetCount(rightBase, rightCount);
        SpatialNodeHelper.RefitInternalMBR(rightBase, _desc);
        RefitInternalUnionMask(rightBase, ref accessor);

        // Update children's parent pointers for entries that moved to the right node
        for (int i = 0; i < rightCount; i++)
        {
            int childId = SpatialNodeHelper.ReadInternalChildId(rightBase, i, _desc);
            byte* childBase = accessor.GetChunkAddress(childId, true);
            SpatialNodeHelper.SetParentChunkId(childBase, rightChunkId);
        }

        nodeLatch.WriteUnlock();
        return rightChunkId;
    }

    /// <summary>Create a new root with two children (old root + new sibling).</summary>
    private void CreateNewRoot(int leftChunkId, int rightChunkId, ref ChunkAccessor<TStore> accessor, ChangeSet changeSet)
    {
        int newRootChunkId = AllocNode(false, 0, ref accessor, changeSet);
        Interlocked.Increment(ref _nodeCount);
        byte* newRootBase = accessor.GetChunkAddress(newRootChunkId, true);

        // Insert old root as child 0
        WriteInternalEntry(newRootBase, 0, leftChunkId, ref accessor);

        // Insert new sibling as child 1
        WriteInternalEntry(newRootBase, 1, rightChunkId, ref accessor);

        SpatialNodeHelper.SetCount(newRootBase, 2);
        SpatialNodeHelper.RefitInternalMBR(newRootBase, _desc);
        RefitInternalUnionMask(newRootBase, ref accessor);

        // Update parent pointers
        byte* leftBase = accessor.GetChunkAddress(leftChunkId, true);
        SpatialNodeHelper.SetParentChunkId(leftBase, newRootChunkId);

        byte* rightBase = accessor.GetChunkAddress(rightChunkId, true);
        SpatialNodeHelper.SetParentChunkId(rightBase, newRootChunkId);

        _rootChunkId = newRootChunkId;
        _depth++;
    }
}
