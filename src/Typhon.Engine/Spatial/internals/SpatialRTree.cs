using System;
using System.Runtime.CompilerServices;
using System.Threading;

namespace Typhon.Engine.Internals;

/// <summary>
/// Maximum tree depth supported. 16 is generous — 100K entities in 2D-f32 (fanout 20) need depth 4-5.
/// </summary>
internal static class SpatialRTreeConstants
{
    internal const int MaxTreeDepth = 16;
}

/// <summary>Stack-allocated buffer for path recording during descent.</summary>
[InlineArray(SpatialRTreeConstants.MaxTreeDepth)]
internal struct PathChunkIdBuffer
{
    private int _element0;
}

/// <summary>Stack-allocated buffer for child indices along the descent path.</summary>
[InlineArray(SpatialRTreeConstants.MaxTreeDepth)]
internal struct PathChildIndexBuffer
{
    private int _element0;
}

/// <summary>Stack-allocated buffer for OLC versions along the descent path.</summary>
[InlineArray(SpatialRTreeConstants.MaxTreeDepth)]
internal struct PathVersionBuffer
{
    private int _element0;
}

/// <summary>
/// Stack-allocated traversal path for a single R-Tree mutation. Records (chunkId, childIndex, olcVersion) at each level during descent, enabling parent
/// access during split propagation and ancestor MBR refit.
/// </summary>
internal ref struct DescentPath
{
    public PathChunkIdBuffer ChunkIds;
    public PathChildIndexBuffer ChildIndices;
    public PathVersionBuffer Versions;
    public int Depth;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Push(int chunkId, int childIndex, int version)
    {
        ChunkIds[Depth] = chunkId;
        ChildIndices[Depth] = childIndex;
        Versions[Depth] = version;
        Depth++;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Clear() => Depth = 0;
}

/// <summary>
/// Page-backed wide R-Tree for spatial indexing. Uses SOA node layout driven by <see cref="SpatialNodeDescriptor"/>. All four variants (2D/3D × f32/f64) are
/// served by a single implementation — descriptor fields are JIT-constant after readonly promotion.
/// </summary>
/// <remarks>
/// Coordinates flow through the tree as <c>double</c> arrays of length <c>CoordCount</c>, ordered as [min0, min1, ..., max0, max1, ...]
/// (e.g., [MinX, MinY, MaxX, MaxY] for 2D). The SOA read/write helpers in <see cref="SpatialNodeHelper"/> handle float↔double conversion at the storage boundary.
/// </remarks>
internal unsafe partial class SpatialRTree<TStore> where TStore : struct, IPageStore
{
    private readonly ChunkBasedSegment<TStore> _segment;
    private readonly SpatialNodeDescriptor _desc;
    private readonly SpatialVariant _variant;

    // Tree metadata (persisted in chunk 0)
    private int _rootChunkId;
    private int _nodeCount;
    private int _entityCount;
    private int _depth;

    /// <summary>Monotonic counter incremented on every Insert/Remove. Used by trigger system for static cache invalidation.</summary>
    private int _mutationVersion;

    /// <summary>Lock protecting SyncMetadata writes to chunk 0 against concurrent mutations.</summary>
    private readonly Lock _metadataLock = new();

    /// <summary>
    /// Back-pointer CBS for O(1) leaf lookup. When set, split scatter updates back-pointers directly
    /// using componentChunkIds stored in leaf entries. Null for standalone unit tests.
    /// </summary>
    internal ChunkBasedSegment<TStore> BackPointerSegment;

    // Chunk 0 metadata layout
    private const int MetaRootOffset = 0;
    private const int MetaNodeCountOffset = 4;
    private const int MetaEntityCountOffset = 8;
    private const int MetaDepthOffset = 12;
    private const int MetaVariantOffset = 16;

    internal ChunkBasedSegment<TStore> Segment => _segment;
    internal SpatialNodeDescriptor Descriptor => _desc;
    internal SpatialVariant Variant => _variant;
    internal int RootChunkId => _rootChunkId;
    internal int NodeCount => _nodeCount;
    internal int EntityCount => _entityCount;
    internal int Depth => _depth;
    internal int MutationVersion => _mutationVersion;

    /// <summary>
    /// Create a new R-Tree or load an existing one from the segment.
    /// </summary>
    /// <param name="segment">Pre-allocated CBS with stride matching the descriptor's Stride</param>
    /// <param name="variant">Spatial variant (determines descriptor and node layout)</param>
    /// <param name="load">True to load existing tree from segment, false to create new</param>
    /// <param name="changeSet">ChangeSet for WAL participation (null for non-WAL)</param>
    internal SpatialRTree(ChunkBasedSegment<TStore> segment, SpatialVariant variant, bool load = false, ChangeSet changeSet = null)
    {
        _segment = segment;
        _variant = variant;
        _desc = SpatialNodeDescriptor.ForVariant(variant);

        var guard = EpochGuard.Enter(_segment.Store.EpochManager);
        try
        {
            if (!load)
            {
                // Reserve chunk 0 for metadata BEFORE creating our accessor (ReserveChunk with clearContent creates its own internal accessor)
                if (!_segment.IsChunkAllocated(0))
                {
                    _segment.ReserveChunk(0, true, changeSet);
                }
            }

            var accessor = _segment.CreateChunkAccessor(changeSet);
            try
            {
                if (!load)
                {
                    _rootChunkId = AllocNode(true, 0, ref accessor, changeSet);
                    _nodeCount = 1;
                    _entityCount = 0;
                    _depth = 1;
                    SyncMetadata(ref accessor);
                }
                else
                {
                    LoadMetadata(ref accessor);
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

    /// <summary>Allocate a new node, initialize its header fields.</summary>
    /// <remarks>
    /// Allocates WITHOUT clearContent to avoid creating a nested ChunkAccessor inside AllocateChunk (the caller already has an active accessor).
    /// We zero and initialize the header manually.
    /// </remarks>
    private int AllocNode(bool isLeaf, int parentChunkId, ref ChunkAccessor<TStore> accessor, ChangeSet changeSet = null)
    {
        int chunkId = _segment.AllocateChunk(false, changeSet);
        byte* nodeBase = accessor.GetChunkAddress(chunkId, true);

        // Zero the entire chunk (stride bytes)
        new Span<byte>(nodeBase, _desc.Stride).Clear();

        // Initialize header
        // OlcVersion must start at version >= 1 (not 0) because ReadVersion() returns 0 as "locked/obsolete"
        // Set to 0b100 = 4 (version=1, lock=0, obsolete=0)
        *(int*)nodeBase = 4;
        SpatialNodeHelper.SetCount(nodeBase, 0);
        SpatialNodeHelper.SetIsLeaf(nodeBase, isLeaf);
        SpatialNodeHelper.SetParentChunkId(nodeBase, parentChunkId);
        return chunkId;
    }

    /// <summary>Write tree metadata to chunk 0. Lock-protected against concurrent mutations.</summary>
    private void SyncMetadata(ref ChunkAccessor<TStore> accessor)
    {
        lock (_metadataLock)
        {
            byte* meta = accessor.GetChunkAddress(0, true);
            *(int*)(meta + MetaRootOffset) = _rootChunkId;
            *(int*)(meta + MetaNodeCountOffset) = _nodeCount;
            *(int*)(meta + MetaEntityCountOffset) = _entityCount;
            *(int*)(meta + MetaDepthOffset) = _depth;
            *(byte*)(meta + MetaVariantOffset) = (byte)_variant;
        }
    }

    /// <summary>Load tree metadata from chunk 0.</summary>
    private void LoadMetadata(ref ChunkAccessor<TStore> accessor)
    {
        byte* meta = accessor.GetChunkAddress(0);
        _rootChunkId = *(int*)(meta + MetaRootOffset);
        _nodeCount = *(int*)(meta + MetaNodeCountOffset);
        _entityCount = *(int*)(meta + MetaEntityCountOffset);
        _depth = *(int*)(meta + MetaDepthOffset);
    }

    // ── OLC helpers ─────────────────────────────────────────────────────────

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static OlcLatch GetLatch(byte* nodeBase) => new(ref SpatialNodeHelper.OlcVersionRef(nodeBase));

    /// <summary>Spin-wait to acquire write lock on a node.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void SpinWriteLock(byte* nodeBase, out OlcLatch latch)
    {
        latch = GetLatch(nodeBase);
        SpinWait spin = default;
        while (!latch.TryWriteLock())
        {
            spin.SpinOnce();
        }
    }

    // ── Category mask helpers ────────────────────────────────────────────────

    /// <summary>
    /// Recompute an internal node's UnionCategoryMask as the bitwise OR of all children's UnionCategoryMasks.
    /// Must be called after RefitInternalMBR whenever category masks may have changed.
    /// </summary>
    private void RefitInternalUnionMask(byte* nodeBase, ref ChunkAccessor<TStore> accessor)
    {
        int count = SpatialNodeHelper.GetCount(nodeBase);
        uint unionMask = 0;
        for (int i = 0; i < count; i++)
        {
            int childId = SpatialNodeHelper.ReadInternalChildId(nodeBase, i, _desc);
            byte* childBase = accessor.GetChunkAddress(childId);
            unionMask |= SpatialNodeHelper.ReadUnionCategoryMask(childBase, _desc);
        }
        SpatialNodeHelper.WriteUnionCategoryMask(nodeBase, unionMask, _desc);
    }

    /// <summary>
    /// Update the category mask of a leaf entry in-place and refit union masks up the ancestor chain.
    /// Called via back-pointer for runtime category changes (e.g., entity dies → clear Alive bit).
    /// </summary>
    internal void SetEntryCategoryMask(int leafChunkId, int slotIndex, uint mask, ref ChunkAccessor<TStore> accessor)
    {
        byte* leafBase = accessor.GetChunkAddress(leafChunkId, true);
        SpinWriteLock(leafBase, out var latch);
        SpatialNodeHelper.WriteLeafCategoryMask(leafBase, slotIndex, mask, _desc);
        SpatialNodeHelper.RefitLeafMBR(leafBase, _desc); // recomputes leaf union mask
        latch.WriteUnlock();
        RefitAncestorsBottomUp(leafChunkId, ref accessor);
    }

    // ── Ancestor refit (bottom-up via ParentChunkId chain) ──────────────────

    /// <summary>
    /// Walk up from a node to the root via ParentChunkId, refitting each ancestor's MBR and UnionCategoryMask.
    /// Used after remove and other mutations that don't have a recorded descent path.
    /// </summary>
    private void RefitAncestorsBottomUp(int startChunkId, ref ChunkAccessor<TStore> accessor)
    {
        int currentChunkId = startChunkId;
        while (true)
        {
            byte* currentBase = accessor.GetChunkAddress(currentChunkId);

            // OLC-validate the child read to avoid chasing a stale parent pointer after concurrent split
            var childLatch = GetLatch(currentBase);
            int childVersion = childLatch.ReadVersion();
            int parentChunkId = SpatialNodeHelper.GetParentChunkId(currentBase);
            if (!childLatch.ValidateVersion(childVersion))
            {
                // Child was concurrently modified (split changed parent pointer) — re-read
                continue;
            }

            if (parentChunkId == 0)
            {
                break;
            }

            byte* parentBase = accessor.GetChunkAddress(parentChunkId, true);
            SpinWriteLock(parentBase, out var parentLatch);

            // Refit the parent's internal entry for this child
            int parentCount = SpatialNodeHelper.GetCount(parentBase);
            for (int i = 0; i < parentCount; i++)
            {
                if (SpatialNodeHelper.ReadInternalChildId(parentBase, i, _desc) == currentChunkId)
                {
                    // Update this child's MBR in the parent
                    for (int c = 0; c < _desc.CoordCount; c++)
                    {
                        SpatialNodeHelper.WriteInternalCoord(parentBase, i, c, SpatialNodeHelper.ReadNodeMBRCoord(currentBase, c, _desc), _desc);
                    }
                    break;
                }
            }

            SpatialNodeHelper.RefitInternalMBR(parentBase, _desc);
            RefitInternalUnionMask(parentBase, ref accessor);
            parentLatch.WriteUnlock();

            currentChunkId = parentChunkId;
        }
    }

    /// <summary>
    /// Walk the recorded descent path upward, refitting each ancestor's internal entry
    /// for the child that was modified, then recomputing the ancestor's own NodeMBR and UnionCategoryMask.
    /// </summary>
    private void RefitAncestors(ref DescentPath path, ref ChunkAccessor<TStore> accessor)
    {
        for (int level = path.Depth - 1; level >= 0; level--)
        {
            int parentChunkId = path.ChunkIds[level];
            int childIdx = path.ChildIndices[level];

            byte* parentBase = accessor.GetChunkAddress(parentChunkId, true);
            SpinWriteLock(parentBase, out var parentLatch);

            // Read child's current NodeMBR and update the parent's entry for that child
            int childChunkId = SpatialNodeHelper.ReadInternalChildId(parentBase, childIdx, _desc);
            byte* childBase = accessor.GetChunkAddress(childChunkId);
            for (int c = 0; c < _desc.CoordCount; c++)
            {
                SpatialNodeHelper.WriteInternalCoord(parentBase, childIdx, c, SpatialNodeHelper.ReadNodeMBRCoord(childBase, c, _desc), _desc);
            }

            SpatialNodeHelper.RefitInternalMBR(parentBase, _desc);
            RefitInternalUnionMask(parentBase, ref accessor);
            parentLatch.WriteUnlock();
        }
    }

    /// <summary>
    /// Read the fat AABB coordinates of a leaf entry at a known position. Used by SpatialMaintainer for containment check.
    /// </summary>
    internal void ReadLeafCoords(int leafChunkId, int slotIndex, Span<double> coords, ref ChunkAccessor<TStore> accessor)
    {
        byte* leafBase = accessor.GetChunkAddress(leafChunkId);
        SpatialNodeHelper.ReadLeafEntryCoords(leafBase, slotIndex, coords, _desc);
    }
}
