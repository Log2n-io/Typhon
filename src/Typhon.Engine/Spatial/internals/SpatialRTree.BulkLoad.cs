using System;
using System.Buffers;

namespace Typhon.Engine.Internals;

internal unsafe partial class SpatialRTree<TStore>
{
    /// <summary>
    /// Build an R-Tree from scratch using the Sort-Tile-Recursive (STR) algorithm.
    /// Produces near-optimal MBRs at ~90% fill factor. O(n log n) construction.
    /// </summary>
    /// <param name="segment">Pre-allocated CBS with stride matching the variant's descriptor</param>
    /// <param name="variant">Spatial variant (2D/3D × f32/f64)</param>
    /// <param name="entityIds">EntityId for each entry</param>
    /// <param name="componentChunkIds">Component CBS chunk ID for each entry</param>
    /// <param name="coords">Flat array: entityCount × coordCount doubles, ordered [min0, min1, ..., max0, max1, ...] per entity</param>
    /// <param name="categoryMasks">Category bitmask for each entry</param>
    /// <param name="changeSet">ChangeSet for WAL participation (null for non-WAL)</param>
    /// <returns>A fully constructed tree. Returns an empty tree if input is empty.</returns>
    internal static SpatialRTree<TStore> BulkLoad(ChunkBasedSegment<TStore> segment, SpatialVariant variant, ReadOnlySpan<long> entityIds,
        ReadOnlySpan<int> componentChunkIds, ReadOnlySpan<double> coords, ReadOnlySpan<uint> categoryMasks, ChangeSet changeSet = null)
    {
        // Phase 3: Spatial:RTree:BulkLoad span. EntityCount/LeafCount filled at exit.
        var bulkScope = TyphonEvent.BeginSpatialRTreeBulkLoad(entityIds.Length);
        try
        {

            // Create an empty tree first (reserves chunk 0 for metadata)
            var tree = new SpatialRTree<TStore>(segment, variant, false, changeSet);

            int entityCount = entityIds.Length;
            if (entityCount == 0)
            {
                bulkScope.LeafCount = 0;
                return tree;
            }

            var desc = tree._desc;
            int coordCount = desc.CoordCount;
            int halfCoord = coordCount / 2;
            int fillFactor = Math.Max(1, (int)(desc.LeafCapacity * 0.9));

            // Build sort index array (indirect sort to avoid copying large coord arrays)
            int[] sortIndex = ArrayPool<int>.Shared.Rent(entityCount);
            double[] centers = ArrayPool<double>.Shared.Rent(entityCount * halfCoord);
            try
            {
                // Compute centers for sorting
                for (int i = 0; i < entityCount; i++)
                {
                    sortIndex[i] = i;
                    int coordBase = i * coordCount;
                    for (int d = 0; d < halfCoord; d++)
                    {
                        centers[i * halfCoord + d] = (coords[coordBase + d] + coords[coordBase + halfCoord + d]) * 0.5;
                    }
                }

                // STR sort: sort by X, then within X-slabs sort by Y (and Z for 3D)
                STRSort(sortIndex.AsSpan(0, entityCount), centers, halfCoord, fillFactor);

                // Pack sorted entries into leaf nodes
                var accessor = segment.CreateChunkAccessor(changeSet);
                try
                {
                    int leafCount = (entityCount + fillFactor - 1) / fillFactor;
                    int[] leafChunkIds = ArrayPool<int>.Shared.Rent(leafCount);
                    try
                    {
                        // Create leaf nodes
                        for (int leafIdx = 0; leafIdx < leafCount; leafIdx++)
                        {
                            int start = leafIdx * fillFactor;
                            int end = Math.Min(start + fillFactor, entityCount);
                            int count = end - start;

                            int leafChunkId = tree.AllocNode(true, 0, ref accessor, changeSet);
                            leafChunkIds[leafIdx] = leafChunkId;

                            byte* leafBase = accessor.GetChunkAddress(leafChunkId, true);

                            for (int j = 0; j < count; j++)
                            {
                                int srcIdx = sortIndex[start + j];
                                tree.WriteLeafEntry(leafBase, j, entityIds[srcIdx], componentChunkIds[srcIdx],
                                    coords.Slice(srcIdx * coordCount, coordCount), categoryMasks[srcIdx]);
                            }

                            SpatialNodeHelper.SetCount(leafBase, count);
                            SpatialNodeHelper.RefitLeafMBR(leafBase, desc);
                        }

                        // The constructor created an initial empty root node that we won't use.
                        // Start fresh with our leaf count; the orphaned chunk is wasted but harmless.
                        tree._nodeCount = leafCount;
                        tree._entityCount = entityCount;

                        // Build internal levels bottom-up using two alternating buffers
                        int currentLevelCount = leafCount;
                        int[] currentBuf = ArrayPool<int>.Shared.Rent(leafCount);
                        leafChunkIds.AsSpan(0, leafCount).CopyTo(currentBuf);
                        int[] nextBuf = null;
                        try
                        {
                            int depth = 1;
                            while (currentLevelCount > 1)
                            {
                                int internalFill = Math.Max(1, (int)(desc.InternalCapacity * 0.9));
                                int nextLevelCount = (currentLevelCount + internalFill - 1) / internalFill;

                                if (nextBuf == null || nextBuf.Length < nextLevelCount)
                                {
                                    if (nextBuf != null) ArrayPool<int>.Shared.Return(nextBuf);
                                    nextBuf = ArrayPool<int>.Shared.Rent(nextLevelCount);
                                }

                                for (int nodeIdx = 0; nodeIdx < nextLevelCount; nodeIdx++)
                                {
                                    int start = nodeIdx * internalFill;
                                    int end = Math.Min(start + internalFill, currentLevelCount);
                                    int count = end - start;

                                    int internalChunkId = tree.AllocNode(false, 0, ref accessor, changeSet);
                                    nextBuf[nodeIdx] = internalChunkId;
                                    tree._nodeCount++;

                                    byte* internalBase = accessor.GetChunkAddress(internalChunkId, true);

                                    for (int j = 0; j < count; j++)
                                    {
                                        int childChunkId = currentBuf[start + j];
                                        tree.WriteInternalEntry(internalBase, j, childChunkId, ref accessor);

                                        // Set parent pointer on child
                                        byte* childBase = accessor.GetChunkAddress(childChunkId, true);
                                        SpatialNodeHelper.SetParentChunkId(childBase, internalChunkId);
                                    }

                                    SpatialNodeHelper.SetCount(internalBase, count);
                                    SpatialNodeHelper.RefitInternalMBR(internalBase, desc);
                                    tree.RefitInternalUnionMask(internalBase, ref accessor);
                                }

                                // Swap buffers
                                (currentBuf, nextBuf) = (nextBuf, currentBuf);
                                currentLevelCount = nextLevelCount;
                                depth++;
                            }

                            // The last remaining node is the root (parentChunkId already 0 from AllocNode)
                            tree._rootChunkId = currentBuf[0];
                            tree._depth = depth;

                            tree.SyncMetadata(ref accessor);
                            tree._mutationVersion = 1;
                        }
                        finally
                        {
                            ArrayPool<int>.Shared.Return(currentBuf);
                            if (nextBuf != null) ArrayPool<int>.Shared.Return(nextBuf);
                        }
                    }
                    finally
                    {
                        ArrayPool<int>.Shared.Return(leafChunkIds);
                    }
                }
                finally
                {
                    accessor.Dispose();
                }
            }
            finally
            {
                ArrayPool<double>.Shared.Return(centers);
                ArrayPool<int>.Shared.Return(sortIndex);
            }

            bulkScope.LeafCount = (entityCount + fillFactor - 1) / fillFactor;
            return tree;
        }
        finally
        {
            bulkScope.Dispose();
        }
    }

    /// <summary>
    /// Sort-Tile-Recursive: sort the index array so that when packed sequentially into leaf nodes, spatially nearby entities share leaves — producing tight MBRs.
    /// For 2D: sort by X, then within X-slabs sort by Y.
    /// For 3D: sort by X, then Y-slabs, then Z.
    /// </summary>
    private static void STRSort(Span<int> indices, double[] centers, int dimCount, int fillFactor)
    {
        int n = indices.Length;
        if (n <= fillFactor)
        {
            return; // Fits in one leaf — no sorting needed
        }

        int leafCount = (n + fillFactor - 1) / fillFactor;

        if (dimCount == 2)
        {
            // 2D: Sort by X, then within X-slabs sort by Y
            int slabSize = (int)Math.Ceiling(Math.Sqrt(leafCount));
            int entriesPerSlab = slabSize * fillFactor;

            // Sort all by center-X (dimension 0)
            SortByDimension(indices, centers, dimCount, 0);

            // Within each X-slab, sort by center-Y (dimension 1)
            for (int slabStart = 0; slabStart < n; slabStart += entriesPerSlab)
            {
                int slabEnd = Math.Min(slabStart + entriesPerSlab, n);
                SortByDimension(indices.Slice(slabStart, slabEnd - slabStart), centers, dimCount, 1);
            }
        }
        else
        {
            // 3D: Sort by X, then Y-slabs, then Z
            int slabSizeX = (int)Math.Ceiling(Math.Pow(leafCount, 1.0 / 3.0));
            int entriesPerXSlab = slabSizeX * slabSizeX * fillFactor;
            int entriesPerYSlab = slabSizeX * fillFactor;

            // Sort all by center-X
            SortByDimension(indices, centers, dimCount, 0);

            // Within each X-slab, sort by center-Y
            for (int xStart = 0; xStart < n; xStart += entriesPerXSlab)
            {
                int xEnd = Math.Min(xStart + entriesPerXSlab, n);
                var xSlab = indices.Slice(xStart, xEnd - xStart);
                SortByDimension(xSlab, centers, dimCount, 1);

                // Within each Y-slab, sort by center-Z
                for (int yStart = 0; yStart < xSlab.Length; yStart += entriesPerYSlab)
                {
                    int yEnd = Math.Min(yStart + entriesPerYSlab, xSlab.Length);
                    SortByDimension(xSlab.Slice(yStart, yEnd - yStart), centers, dimCount, 2);
                }
            }
        }
    }

    /// <summary>Sort indices by center value along the given dimension.</summary>
    private static void SortByDimension(Span<int> indices, double[] centers, int dimCount, int dim)
    {
        // Insertion sort for small arrays, Array.Sort for larger ones
        if (indices.Length <= 32)
        {
            for (int i = 1; i < indices.Length; i++)
            {
                int key = indices[i];
                double keyVal = centers[key * dimCount + dim];
                int j = i - 1;
                while (j >= 0 && centers[indices[j] * dimCount + dim] > keyVal)
                {
                    indices[j + 1] = indices[j];
                    j--;
                }
                indices[j + 1] = key;
            }
        }
        else
        {
            // Convert to array for Array.Sort (which doesn't have Span overload with custom comparer)
            int[] temp = ArrayPool<int>.Shared.Rent(indices.Length);
            try
            {
                indices.CopyTo(temp);
                Array.Sort(temp, 0, indices.Length, new CenterComparer(centers, dimCount, dim));
                temp.AsSpan(0, indices.Length).CopyTo(indices);
            }
            finally
            {
                ArrayPool<int>.Shared.Return(temp);
            }
        }
    }

    /// <summary>Comparer that sorts indices by center value along a given dimension.</summary>
    private sealed class CenterComparer : System.Collections.Generic.IComparer<int>
    {
        private readonly double[] _centers;
        private readonly int _dimCount;
        private readonly int _dim;

        internal CenterComparer(double[] centers, int dimCount, int dim)
        {
            _centers = centers;
            _dimCount = dimCount;
            _dim = dim;
        }

        public int Compare(int a, int b) => _centers[a * _dimCount + _dim].CompareTo(_centers[b * _dimCount + _dim]);
    }
}
