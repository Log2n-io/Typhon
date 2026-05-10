using System;
using System.Collections.Generic;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Internals;

/// <summary>
/// Debug helper that walks an entire R-Tree and asserts all structural invariants (R1–R7).
/// Called after every mutation in unit tests to verify correctness.
/// </summary>
internal static unsafe class TreeValidator
{
    /// <summary>
    /// Validate all structural invariants of the R-Tree.
    /// Throws on any violation with a descriptive message.
    /// </summary>
    internal static void Validate<TStore>(SpatialRTree<TStore> tree) where TStore : struct, IPageStore
    {
        var guard = EpochGuard.Enter(tree.Segment.Store.EpochManager);
        try
        {
            var accessor = tree.Segment.CreateChunkAccessor();
            try
            {
                var desc = tree.Descriptor;
                var entityIds = new HashSet<long>();
                int totalEntities = 0;
                int totalNodes = 0;

                ValidateNode(tree.RootChunkId, 0, desc, ref accessor, entityIds, ref totalEntities, ref totalNodes);

                // R5: each EntityId appears exactly once
                if (entityIds.Count != totalEntities)
                {
                    throw new InvalidOperationException($"R5 violation: {totalEntities - entityIds.Count} duplicate EntityIds in tree");
                }

                // Entity count matches metadata
                if (totalEntities != tree.EntityCount)
                {
                    throw new InvalidOperationException($"EntityCount mismatch: tree has {totalEntities}, metadata says {tree.EntityCount}");
                }

                // Node count matches metadata
                if (totalNodes != tree.NodeCount)
                {
                    throw new InvalidOperationException($"NodeCount mismatch: tree has {totalNodes}, metadata says {tree.NodeCount}");
                }
            }
            finally
            {
                accessor.Dispose();
            }
        }
        finally
        {
            guard.Dispose();
        }
    }

    private static void ValidateNode<TStore>(int chunkId, int expectedParentChunkId, in SpatialNodeDescriptor desc, ref ChunkAccessor<TStore> accessor, 
        HashSet<long> entityIds, ref int totalEntities, ref int totalNodes) where TStore : struct, IPageStore
    {
        totalNodes++;
        byte* nodeBase = accessor.GetChunkAddress(chunkId);
        int count = SpatialNodeHelper.GetCount(nodeBase);
        bool isLeaf = SpatialNodeHelper.IsLeaf(nodeBase);

        // R3: capacity bounds
        int capacity = isLeaf ? desc.LeafCapacity : desc.InternalCapacity;
        if (count < 0 || count > capacity)
        {
            throw new InvalidOperationException($"R3 violation: node {chunkId} count={count}, capacity={capacity}");
        }

        // R6: parent pointer matches
        int storedParent = SpatialNodeHelper.GetParentChunkId(nodeBase);
        if (storedParent != expectedParentChunkId)
        {
            throw new InvalidOperationException($"R6 violation: node {chunkId} parent={storedParent}, expected={expectedParentChunkId}");
        }

        if (isLeaf)
        {
            // R4: EntityIds only in leaf nodes
            for (int i = 0; i < count; i++)
            {
                long eid = SpatialNodeHelper.ReadLeafEntityId(nodeBase, i, desc);
                entityIds.Add(eid);
                totalEntities++;
            }

            // R1: MBR tightness
            ValidateMBRTightness(nodeBase, count, true, desc, chunkId);

            // C2: UnionCategoryMask = OR of all leaf entries' CategoryMasks
            ValidateLeafUnionMask(nodeBase, count, desc, chunkId);
        }
        else
        {
            // R1: MBR tightness
            ValidateMBRTightness(nodeBase, count, false, desc, chunkId);

            // Recurse into children
            for (int i = 0; i < count; i++)
            {
                int childId = SpatialNodeHelper.ReadInternalChildId(nodeBase, i, desc);
                if (childId <= 0)
                {
                    throw new InvalidOperationException($"R6 violation: node {chunkId} child[{i}] has invalid chunkId={childId}");
                }

                ValidateNode(childId, chunkId, desc, ref accessor, entityIds, ref totalEntities, ref totalNodes);
            }

            // C2: UnionCategoryMask = OR of all children's UnionCategoryMasks
            ValidateInternalUnionMask(nodeBase, count, desc, chunkId, ref accessor);
        }
    }

    private static void ValidateMBRTightness(byte* nodeBase, int count, bool isLeaf, in SpatialNodeDescriptor desc, int chunkId)
    {
        if (count == 0)
        {
            return;
        }

        int halfCoord = desc.CoordCount / 2;
        Span<double> recomputed = stackalloc double[desc.CoordCount];

        // Initialize from first entry
        if (isLeaf)
        {
            SpatialNodeHelper.ReadLeafEntryCoords(nodeBase, 0, recomputed, desc);
        }
        else
        {
            SpatialNodeHelper.ReadInternalEntryCoords(nodeBase, 0, recomputed, desc);
        }

        // Expand with remaining entries
        for (int i = 1; i < count; i++)
        {
            for (int c = 0; c < halfCoord; c++)
            {
                double v = isLeaf ? SpatialNodeHelper.ReadLeafCoord(nodeBase, i, c, desc) : SpatialNodeHelper.ReadInternalCoord(nodeBase, i, c, desc);
                if (v < recomputed[c])
                {
                    recomputed[c] = v;
                }
            }
            for (int c = halfCoord; c < desc.CoordCount; c++)
            {
                double v = isLeaf ? SpatialNodeHelper.ReadLeafCoord(nodeBase, i, c, desc) : SpatialNodeHelper.ReadInternalCoord(nodeBase, i, c, desc);
                if (v > recomputed[c])
                {
                    recomputed[c] = v;
                }
            }
        }

        // Compare with stored NodeMBR
        const double epsilon = 1e-6;
        for (int c = 0; c < desc.CoordCount; c++)
        {
            double stored = SpatialNodeHelper.ReadNodeMBRCoord(nodeBase, c, desc);
            if (Math.Abs(stored - recomputed[c]) > epsilon)
            {
                throw new InvalidOperationException($"R1 violation: node {chunkId} MBR coord[{c}] is {stored} but recomputed is {recomputed[c]}");
            }
        }
    }

    private static void ValidateLeafUnionMask(byte* nodeBase, int count, in SpatialNodeDescriptor desc, int chunkId)
    {
        uint recomputed = 0;
        for (int i = 0; i < count; i++)
        {
            recomputed |= SpatialNodeHelper.ReadLeafCategoryMask(nodeBase, i, desc);
        }
        uint stored = SpatialNodeHelper.ReadUnionCategoryMask(nodeBase, desc);
        if (stored != recomputed)
        {
            throw new InvalidOperationException($"C2 violation: leaf node {chunkId} UnionCategoryMask is 0x{stored:X8} but recomputed is 0x{recomputed:X8}");
        }
    }

    private static void ValidateInternalUnionMask<TStore>(byte* nodeBase, int count, in SpatialNodeDescriptor desc, int chunkId,
        ref ChunkAccessor<TStore> accessor) where TStore : struct, IPageStore
    {
        uint recomputed = 0;
        for (int i = 0; i < count; i++)
        {
            int childId = SpatialNodeHelper.ReadInternalChildId(nodeBase, i, desc);
            byte* childBase = accessor.GetChunkAddress(childId);
            recomputed |= SpatialNodeHelper.ReadUnionCategoryMask(childBase, desc);
        }
        uint stored = SpatialNodeHelper.ReadUnionCategoryMask(nodeBase, desc);
        if (stored != recomputed)
        {
            throw new InvalidOperationException($"C2 violation: internal node {chunkId} UnionCategoryMask is 0x{stored:X8} but recomputed is 0x{recomputed:X8}");
        }
    }

    // ── Dual-tree validation (SD invariants) ─────────────────────────────────

    /// <summary>
    /// Validate a <see cref="SpatialIndexState"/> with dual-tree invariants:
    /// SD1 (exclusive membership), SD2 (back-pointer TreeSelector consistency).
    /// Also calls single-tree <see cref="Validate{TStore}"/> on each non-null tree.
    /// </summary>
    internal static void ValidateState(SpatialIndexState state)
    {
        HashSet<long> staticIds = null;
        HashSet<long> dynamicIds = null;

        if (state.StaticTree != null)
        {
            Validate(state.StaticTree);
            staticIds = CollectEntityIds(state.StaticTree);
        }

        if (state.DynamicTree != null)
        {
            Validate(state.DynamicTree);
            dynamicIds = CollectEntityIds(state.DynamicTree);
        }

        // SD1: exclusive membership — no entity appears in both trees
        if (staticIds != null && dynamicIds != null)
        {
            foreach (long id in staticIds)
            {
                if (dynamicIds.Contains(id))
                {
                    throw new InvalidOperationException($"SD1 violation: entity {id} found in both StaticTree and DynamicTree");
                }
            }
        }

        // SD2: back-pointer TreeSelector must match the tree the entity is in
        if (state.BackPointerSegment != null)
        {
            var bpAccessor = state.BackPointerSegment.CreateChunkAccessor();
            try
            {
                ValidateTreeSelectors(state.StaticTree, (byte)SpatialMode.Static, ref bpAccessor);
                ValidateTreeSelectors(state.DynamicTree, (byte)SpatialMode.Dynamic, ref bpAccessor);
            }
            finally
            {
                bpAccessor.Dispose();
            }
        }
    }

    /// <summary>Collect all entityIds in a tree by walking all leaf nodes.</summary>
    private static HashSet<long> CollectEntityIds<TStore>(SpatialRTree<TStore> tree) where TStore : struct, IPageStore
    {
        var ids = new HashSet<long>();
        var guard = EpochGuard.Enter(tree.Segment.Store.EpochManager);
        try
        {
            var accessor = tree.Segment.CreateChunkAccessor();
            try
            {
                CollectEntityIdsRecursive(tree.RootChunkId, tree.Descriptor, ref accessor, ids);
            }
            finally
            {
                accessor.Dispose();
            }
        }
        finally
        {
            guard.Dispose();
        }
        return ids;
    }

    private static void CollectEntityIdsRecursive<TStore>(int chunkId, in SpatialNodeDescriptor desc, ref ChunkAccessor<TStore> accessor,
        HashSet<long> ids) where TStore : struct, IPageStore
    {
        byte* nodeBase = accessor.GetChunkAddress(chunkId);
        int count = SpatialNodeHelper.GetCount(nodeBase);
        bool isLeaf = SpatialNodeHelper.IsLeaf(nodeBase);

        if (isLeaf)
        {
            for (int i = 0; i < count; i++)
            {
                ids.Add(SpatialNodeHelper.ReadLeafEntityId(nodeBase, i, desc));
            }
        }
        else
        {
            for (int i = 0; i < count; i++)
            {
                int childId = SpatialNodeHelper.ReadInternalChildId(nodeBase, i, desc);
                CollectEntityIdsRecursive(childId, desc, ref accessor, ids);
            }
        }
    }

    /// <summary>
    /// For each entity in the given tree, read its back-pointer and verify TreeSelector matches the expected value.
    /// </summary>
    private static void ValidateTreeSelectors<TStore>(SpatialRTree<TStore> tree, byte expectedSelector,
        ref ChunkAccessor<PersistentStore> bpAccessor) where TStore : struct, IPageStore
    {
        if (tree == null || tree.EntityCount == 0)
        {
            return;
        }

        var guard = EpochGuard.Enter(tree.Segment.Store.EpochManager);
        try
        {
            var treeAccessor = tree.Segment.CreateChunkAccessor();
            try
            {
                ValidateTreeSelectorsRecursive(tree.RootChunkId, tree.Descriptor, expectedSelector, ref treeAccessor, ref bpAccessor);
            }
            finally
            {
                treeAccessor.Dispose();
            }
        }
        finally
        {
            guard.Dispose();
        }
    }

    private static void ValidateTreeSelectorsRecursive<TStore>(int chunkId, in SpatialNodeDescriptor desc, byte expectedSelector,
        ref ChunkAccessor<TStore> treeAccessor, ref ChunkAccessor<PersistentStore> bpAccessor) where TStore : struct, IPageStore
    {
        byte* nodeBase = treeAccessor.GetChunkAddress(chunkId);
        int count = SpatialNodeHelper.GetCount(nodeBase);
        bool isLeaf = SpatialNodeHelper.IsLeaf(nodeBase);

        if (isLeaf)
        {
            for (int i = 0; i < count; i++)
            {
                int compChunkId = SpatialNodeHelper.ReadLeafCompChunkId(nodeBase, i, desc);
                if (compChunkId == 0)
                {
                    continue; // standalone test entry, no back-pointer
                }
                var bp = SpatialBackPointerHelper.Read(ref bpAccessor, compChunkId);
                if (bp.TreeSelector != expectedSelector)
                {
                    long entityId = SpatialNodeHelper.ReadLeafEntityId(nodeBase, i, desc);
                    throw new InvalidOperationException(
                        $"SD2 violation: entity {entityId} (compChunkId={compChunkId}) has TreeSelector={bp.TreeSelector} but is in tree with expected selector={expectedSelector}");
                }
            }
        }
        else
        {
            for (int i = 0; i < count; i++)
            {
                int childId = SpatialNodeHelper.ReadInternalChildId(nodeBase, i, desc);
                ValidateTreeSelectorsRecursive(childId, desc, expectedSelector, ref treeAccessor, ref bpAccessor);
            }
        }
    }
}
