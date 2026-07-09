using System;
using System.Runtime.CompilerServices;

namespace Typhon.Engine.Internals;

/// <summary>AABB query result: the EntityId and ComponentChunkId of an entity whose fat AABB overlaps the query box.
/// ComponentChunkId enables direct CBS access for two-pass compound queries without an EntityMap lookup.</summary>
internal readonly struct SpatialQueryResult
{
    public readonly long EntityId;
    public readonly int ComponentChunkId;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public SpatialQueryResult(long entityId, int componentChunkId)
    {
        EntityId = entityId;
        ComponentChunkId = componentChunkId;
    }
}

/// <summary>Query result that includes both EntityId and ComponentChunkId. Used by trigger system to populate occupant bitmaps without a second lookup.</summary>
internal readonly struct SpatialOccupantResult
{
    public readonly long EntityId;
    public readonly int ComponentChunkId;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public SpatialOccupantResult(long entityId, int componentChunkId)
    {
        EntityId = entityId;
        ComponentChunkId = componentChunkId;
    }
}

/// <summary>Stack buffer for DFS traversal of the R-Tree during AABB queries.</summary>
[InlineArray(256)]
internal struct QueryStackBuffer
{
    private int _element0;
}

internal unsafe partial class SpatialRTree<TStore>
{
    /// <summary>
    /// Query all entities whose fat AABB overlaps the given query box.
    /// Returns a ref struct enumerator suitable for foreach.
    /// </summary>
    /// <param name="queryCoords">CoordCount doubles: [min0, min1, ..., max0, max1, ...]</param>
    /// <param name="changeSet">ChangeSet for page access tracking</param>
    /// <param name="categoryMask">
    /// Category bitmask; when non-zero, only entities whose category mask contains all of these bits match. Pass <c>0</c> (default) to disable category filtering.
    /// </param>
    internal AABBQueryEnumerator QueryAABB(ReadOnlySpan<double> queryCoords, ChangeSet changeSet = null, uint categoryMask = 0)
        => new(this, queryCoords, changeSet, categoryMask);

    /// <summary>
    /// Ref struct enumerator for AABB overlap queries. Uses stack-based DFS with OLC read validation per node. Zero heap allocations.
    /// </summary>
    internal ref struct AABBQueryEnumerator
    {
        private readonly SpatialRTree<TStore> _tree;
        private ChunkAccessor<TStore> _accessor;
        private readonly SpatialNodeDescriptor _desc;

        // Query bounds stored inline (max 6 doubles for 3D)
        private fixed double _queryCoords[6];
        private readonly int _coordCount;
        private readonly uint _categoryMask;

        // DFS stack of chunk IDs to visit
        private QueryStackBuffer _stack;
        private int _stackTop;

        // Current leaf iteration
        private int _currentLeafChunkId;
        private int _currentLeafIndex;
        private int _currentLeafCount;

        private SpatialQueryResult _current;
        private bool _disposed;

        // Phase 3: Spatial:Query:Aabb span (Tier-2 gated). ResultCount/RestartCount filled during enumeration.
        private SpatialQueryAabbEvent _span;

        internal AABBQueryEnumerator(SpatialRTree<TStore> tree, ReadOnlySpan<double> queryCoords, ChangeSet changeSet, uint categoryMask = 0)
        {
            _tree = tree;
            _desc = tree._desc;
            _coordCount = _desc.CoordCount;
            _accessor = tree._segment.CreateChunkAccessor(changeSet);
            _stackTop = 0;
            _currentLeafChunkId = 0;
            _currentLeafIndex = -1;
            _currentLeafCount = 0;
            _current = default;
            _disposed = false;
            _categoryMask = categoryMask;

            int len = Math.Min(queryCoords.Length, 6);
            for (int i = 0; i < len; i++)
            {
                _queryCoords[i] = queryCoords[i];
            }

            // Push root
            if (tree._rootChunkId != 0)
            {
                _stack[0] = tree._rootChunkId;
                _stackTop = 1;
            }

            _span = TyphonEvent.BeginSpatialQueryAabb(categoryMask);
        }

        public SpatialQueryResult Current => _current;

        public AABBQueryEnumerator GetEnumerator() => this;

        public bool MoveNext()
        {
            // Resume leaf scan if in progress
            while (_currentLeafChunkId != 0)
            {
                _currentLeafIndex++;
                if (_currentLeafIndex >= _currentLeafCount)
                {
                    _currentLeafChunkId = 0;
                    break;
                }

                byte* leafBase = _accessor.GetChunkAddress(_currentLeafChunkId);
                if (LeafEntryOverlapsQuery(leafBase, _currentLeafIndex))
                {
                    if (_categoryMask != 0 && (SpatialNodeHelper.ReadLeafCategoryMask(leafBase, _currentLeafIndex, _desc) & _categoryMask) != _categoryMask)
                    {
                        continue;
                    }
                    _current = new SpatialQueryResult(
                        SpatialNodeHelper.ReadLeafEntityId(leafBase, _currentLeafIndex, _desc),
                        SpatialNodeHelper.ReadLeafCompChunkId(leafBase, _currentLeafIndex, _desc));
                    if (TelemetryConfig.SpatialQueryAabbActive && _span.ResultCount < ushort.MaxValue)
                    {
                        _span.ResultCount++;
                    }

                    return true;
                }
            }

            // DFS traversal
            while (_stackTop > 0)
            {
                int chunkId = _stack[--_stackTop];
                byte* nodeBase = _accessor.GetChunkAddress(chunkId);

                var latch = GetLatch(nodeBase);
                int version = latch.ReadVersion();
                if (version == 0)
                {
                    // Locked or obsolete: restart from root
                    RestartFromRoot();
                    if (TelemetryConfig.SpatialQueryAabbActive && _span.RestartCount < byte.MaxValue)
                    {
                        _span.RestartCount++;
                    }

                    continue;
                }

                bool isLeaf = SpatialNodeHelper.IsLeaf(nodeBase);
                int count = SpatialNodeHelper.GetCount(nodeBase);

                if (!latch.ValidateVersion(version))
                {
                    RestartFromRoot();
                    if (TelemetryConfig.SpatialQueryAabbActive && _span.RestartCount < byte.MaxValue)
                    {
                        _span.RestartCount++;
                    }

                    continue;
                }

                // Node-level category mask pruning: skip entire node if no entries match
                if (_categoryMask != 0 && (SpatialNodeHelper.ReadUnionCategoryMask(nodeBase, _desc) & _categoryMask) == 0)
                {
                    continue;
                }

                if (isLeaf)
                {
                    // Start scanning this leaf
                    _currentLeafChunkId = chunkId;
                    _currentLeafIndex = -1;
                    _currentLeafCount = count;
                    if (TelemetryConfig.SpatialQueryAabbActive && _span.LeavesEntered < ushort.MaxValue)
                    {
                        _span.LeavesEntered++;
                    }

                    return MoveNext(); // Re-enter to scan leaf entries
                }

                // Internal node: push overlapping children (reverse order for DFS)
                for (int i = count - 1; i >= 0; i--)
                {
                    if (InternalEntryOverlapsQuery(nodeBase, i))
                    {
                        int childId = SpatialNodeHelper.ReadInternalChildId(nodeBase, i, _desc);
                        if (_stackTop < 256)
                        {
                            _stack[_stackTop++] = childId;
                        }
                        else
                        {
                            // Tier-0 always-on record (#422): latch-safe — never throw here (we hold an OLC read latch).
                            SpatialRTreeDiagnostics.RecordDfsStackOverflow("AABB");
                        }
                    }
                }
                if (TelemetryConfig.SpatialQueryAabbActive && _span.NodesVisited < ushort.MaxValue)
                {
                    _span.NodesVisited++;
                }

                if (!latch.ValidateVersion(version))
                {
                    RestartFromRoot();
                    if (TelemetryConfig.SpatialQueryAabbActive && _span.RestartCount < byte.MaxValue)
                    {
                        _span.RestartCount++;
                    }
                }
            }

            return false;
        }

        private void RestartFromRoot()
        {
            _stackTop = 0;
            _currentLeafChunkId = 0;
            if (_tree._rootChunkId != 0)
            {
                _stack[0] = _tree._rootChunkId;
                _stackTop = 1;
            }
        }

        /// <summary>Separating-axis AABB overlap test for leaf entries.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private bool LeafEntryOverlapsQuery(byte* nodeBase, int index)
        {
            if (_coordCount == 4)
            {
                // 2D fast path: fully unrolled, no loop
                return SpatialNodeHelper.ReadLeafCoord(nodeBase, index, 2, _desc) >= _queryCoords[0]
                    && SpatialNodeHelper.ReadLeafCoord(nodeBase, index, 0, _desc) <= _queryCoords[2]
                    && SpatialNodeHelper.ReadLeafCoord(nodeBase, index, 3, _desc) >= _queryCoords[1]
                    && SpatialNodeHelper.ReadLeafCoord(nodeBase, index, 1, _desc) <= _queryCoords[3];
            }

            // 3D fast path: fully unrolled
            return SpatialNodeHelper.ReadLeafCoord(nodeBase, index, 3, _desc) >= _queryCoords[0]
                && SpatialNodeHelper.ReadLeafCoord(nodeBase, index, 0, _desc) <= _queryCoords[3]
                && SpatialNodeHelper.ReadLeafCoord(nodeBase, index, 4, _desc) >= _queryCoords[1]
                && SpatialNodeHelper.ReadLeafCoord(nodeBase, index, 1, _desc) <= _queryCoords[4]
                && SpatialNodeHelper.ReadLeafCoord(nodeBase, index, 5, _desc) >= _queryCoords[2]
                && SpatialNodeHelper.ReadLeafCoord(nodeBase, index, 2, _desc) <= _queryCoords[5];
        }

        /// <summary>Separating-axis AABB overlap test for internal node entries.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private bool InternalEntryOverlapsQuery(byte* nodeBase, int index)
        {
            if (_coordCount == 4)
            {
                // 2D fast path: fully unrolled
                return SpatialNodeHelper.ReadInternalCoord(nodeBase, index, 2, _desc) >= _queryCoords[0]
                    && SpatialNodeHelper.ReadInternalCoord(nodeBase, index, 0, _desc) <= _queryCoords[2]
                    && SpatialNodeHelper.ReadInternalCoord(nodeBase, index, 3, _desc) >= _queryCoords[1]
                    && SpatialNodeHelper.ReadInternalCoord(nodeBase, index, 1, _desc) <= _queryCoords[3];
            }

            // 3D fast path: fully unrolled
            return SpatialNodeHelper.ReadInternalCoord(nodeBase, index, 3, _desc) >= _queryCoords[0]
                && SpatialNodeHelper.ReadInternalCoord(nodeBase, index, 0, _desc) <= _queryCoords[3]
                && SpatialNodeHelper.ReadInternalCoord(nodeBase, index, 4, _desc) >= _queryCoords[1]
                && SpatialNodeHelper.ReadInternalCoord(nodeBase, index, 1, _desc) <= _queryCoords[4]
                && SpatialNodeHelper.ReadInternalCoord(nodeBase, index, 5, _desc) >= _queryCoords[2]
                && SpatialNodeHelper.ReadInternalCoord(nodeBase, index, 2, _desc) <= _queryCoords[5];
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                _disposed = true;
                _span.Dispose();
                _accessor.Dispose();
            }
        }
    }

    // ── Occupant Query (EntityId + ComponentChunkId) ─────────────────────

    /// <summary>
    /// Query all entities whose fat AABB overlaps the given query box, returning both EntityId and ComponentChunkId per hit.
    /// Used by the trigger system to populate occupant bitmaps indexed by componentChunkId without a second lookup.
    /// </summary>
    internal OccupantQueryEnumerator QueryAABBOccupants(ReadOnlySpan<double> queryCoords, ChangeSet changeSet = null, uint categoryMask = 0)
        => new(this, queryCoords, changeSet, categoryMask);

    /// <summary>
    /// Ref struct enumerator identical to <see cref="AABBQueryEnumerator"/> except it yields <see cref="SpatialOccupantResult"/>
    /// (EntityId + ComponentChunkId). The additional componentChunkId read is from an adjacent SOA array — same cache line.
    /// </summary>
    internal ref struct OccupantQueryEnumerator
    {
        private readonly SpatialRTree<TStore> _tree;
        private ChunkAccessor<TStore> _accessor;
        private readonly SpatialNodeDescriptor _desc;
        private fixed double _queryCoords[6];
        private readonly int _coordCount;
        private readonly uint _categoryMask;
        private QueryStackBuffer _stack;
        private int _stackTop;
        private int _currentLeafChunkId;
        private int _currentLeafIndex;
        private int _currentLeafCount;
        private SpatialOccupantResult _current;
        private bool _disposed;

        internal OccupantQueryEnumerator(SpatialRTree<TStore> tree, ReadOnlySpan<double> queryCoords, ChangeSet changeSet, uint categoryMask = 0)
        {
            _tree = tree;
            _desc = tree._desc;
            _coordCount = _desc.CoordCount;
            _accessor = tree._segment.CreateChunkAccessor(changeSet);
            _stackTop = 0;
            _currentLeafChunkId = 0;
            _currentLeafIndex = -1;
            _currentLeafCount = 0;
            _current = default;
            _disposed = false;
            _categoryMask = categoryMask;

            int len = Math.Min(queryCoords.Length, 6);
            for (int i = 0; i < len; i++)
            {
                _queryCoords[i] = queryCoords[i];
            }

            if (tree._rootChunkId != 0)
            {
                _stack[0] = tree._rootChunkId;
                _stackTop = 1;
            }
        }

        public SpatialOccupantResult Current => _current;

        public OccupantQueryEnumerator GetEnumerator() => this;

        public bool MoveNext()
        {
            while (_currentLeafChunkId != 0)
            {
                _currentLeafIndex++;
                if (_currentLeafIndex >= _currentLeafCount)
                {
                    _currentLeafChunkId = 0;
                    break;
                }

                byte* leafBase = _accessor.GetChunkAddress(_currentLeafChunkId);
                if (LeafEntryOverlapsQuery(leafBase, _currentLeafIndex))
                {
                    if (_categoryMask != 0
                        && (SpatialNodeHelper.ReadLeafCategoryMask(leafBase, _currentLeafIndex, _desc) & _categoryMask) != _categoryMask)
                    {
                        continue;
                    }
                    _current = new SpatialOccupantResult(
                        SpatialNodeHelper.ReadLeafEntityId(leafBase, _currentLeafIndex, _desc),
                        SpatialNodeHelper.ReadLeafCompChunkId(leafBase, _currentLeafIndex, _desc));
                    return true;
                }
            }

            while (_stackTop > 0)
            {
                int chunkId = _stack[--_stackTop];
                byte* nodeBase = _accessor.GetChunkAddress(chunkId);

                var latch = GetLatch(nodeBase);
                int version = latch.ReadVersion();
                if (version == 0)
                {
                    RestartFromRoot();
                    continue;
                }

                bool isLeaf = SpatialNodeHelper.IsLeaf(nodeBase);
                int count = SpatialNodeHelper.GetCount(nodeBase);

                if (!latch.ValidateVersion(version))
                {
                    RestartFromRoot();
                    continue;
                }

                if (_categoryMask != 0 && (SpatialNodeHelper.ReadUnionCategoryMask(nodeBase, _desc) & _categoryMask) == 0)
                {
                    continue;
                }

                if (isLeaf)
                {
                    _currentLeafChunkId = chunkId;
                    _currentLeafIndex = -1;
                    _currentLeafCount = count;
                    return MoveNext();
                }

                for (int i = count - 1; i >= 0; i--)
                {
                    if (InternalEntryOverlapsQuery(nodeBase, i))
                    {
                        int childId = SpatialNodeHelper.ReadInternalChildId(nodeBase, i, _desc);
                        if (_stackTop < 256)
                        {
                            _stack[_stackTop++] = childId;
                        }
                        else
                        {
                            // Tier-0 always-on record (#422): latch-safe — never throw here (we hold an OLC read latch).
                            SpatialRTreeDiagnostics.RecordDfsStackOverflow("occupant");
                        }
                    }
                }

                if (!latch.ValidateVersion(version))
                {
                    RestartFromRoot();
                }
            }

            return false;
        }

        private void RestartFromRoot()
        {
            _stackTop = 0;
            _currentLeafChunkId = 0;
            if (_tree._rootChunkId != 0)
            {
                _stack[0] = _tree._rootChunkId;
                _stackTop = 1;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private bool LeafEntryOverlapsQuery(byte* nodeBase, int index)
        {
            if (_coordCount == 4)
            {
                return SpatialNodeHelper.ReadLeafCoord(nodeBase, index, 2, _desc) >= _queryCoords[0]
                    && SpatialNodeHelper.ReadLeafCoord(nodeBase, index, 0, _desc) <= _queryCoords[2]
                    && SpatialNodeHelper.ReadLeafCoord(nodeBase, index, 3, _desc) >= _queryCoords[1]
                    && SpatialNodeHelper.ReadLeafCoord(nodeBase, index, 1, _desc) <= _queryCoords[3];
            }

            return SpatialNodeHelper.ReadLeafCoord(nodeBase, index, 3, _desc) >= _queryCoords[0]
                && SpatialNodeHelper.ReadLeafCoord(nodeBase, index, 0, _desc) <= _queryCoords[3]
                && SpatialNodeHelper.ReadLeafCoord(nodeBase, index, 4, _desc) >= _queryCoords[1]
                && SpatialNodeHelper.ReadLeafCoord(nodeBase, index, 1, _desc) <= _queryCoords[4]
                && SpatialNodeHelper.ReadLeafCoord(nodeBase, index, 5, _desc) >= _queryCoords[2]
                && SpatialNodeHelper.ReadLeafCoord(nodeBase, index, 2, _desc) <= _queryCoords[5];
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private bool InternalEntryOverlapsQuery(byte* nodeBase, int index)
        {
            if (_coordCount == 4)
            {
                return SpatialNodeHelper.ReadInternalCoord(nodeBase, index, 2, _desc) >= _queryCoords[0]
                    && SpatialNodeHelper.ReadInternalCoord(nodeBase, index, 0, _desc) <= _queryCoords[2]
                    && SpatialNodeHelper.ReadInternalCoord(nodeBase, index, 3, _desc) >= _queryCoords[1]
                    && SpatialNodeHelper.ReadInternalCoord(nodeBase, index, 1, _desc) <= _queryCoords[3];
            }

            return SpatialNodeHelper.ReadInternalCoord(nodeBase, index, 3, _desc) >= _queryCoords[0]
                && SpatialNodeHelper.ReadInternalCoord(nodeBase, index, 0, _desc) <= _queryCoords[3]
                && SpatialNodeHelper.ReadInternalCoord(nodeBase, index, 4, _desc) >= _queryCoords[1]
                && SpatialNodeHelper.ReadInternalCoord(nodeBase, index, 1, _desc) <= _queryCoords[4]
                && SpatialNodeHelper.ReadInternalCoord(nodeBase, index, 5, _desc) >= _queryCoords[2]
                && SpatialNodeHelper.ReadInternalCoord(nodeBase, index, 2, _desc) <= _queryCoords[5];
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                _disposed = true;
                _accessor.Dispose();
            }
        }
    }

    // ── Radius Query ─────────────────────────────────────────────────────

    /// <summary>
    /// Query all entities whose fat AABB overlaps a sphere defined by center + radius.
    /// Converts to AABB query internally. False positive rate: ~21% (2D), ~48% (3D) — caller post-filters.
    /// </summary>
    internal RadiusEnumerator QueryRadius(ReadOnlySpan<double> center, double radius, ChangeSet changeSet = null, uint categoryMask = 0)
        => new(this, center, radius, changeSet, categoryMask);

    internal ref struct RadiusEnumerator
    {
        private AABBQueryEnumerator _inner;
        private SpatialQueryRadiusEvent _span;
        private bool _disposed;

        internal RadiusEnumerator(SpatialRTree<TStore> tree, ReadOnlySpan<double> center, double radius, ChangeSet changeSet, uint categoryMask = 0)
        {
            radius = Math.Max(radius, 0); // Clamp negative radius to empty query
            int halfCoord = tree._desc.CoordCount / 2;
            Span<double> aabb = stackalloc double[tree._desc.CoordCount];
            for (int d = 0; d < halfCoord; d++)
            {
                aabb[d] = center[d] - radius;
                aabb[d + halfCoord] = center[d] + radius;
            }
#pragma warning disable CS9080 // Use of variable in this context may expose referenced variables outside of their declaration scope
            _inner = new AABBQueryEnumerator(tree, aabb, changeSet, categoryMask);
#pragma warning restore CS9080 // Use of variable in this context may expose referenced variables outside of their declaration scope
            _disposed = false;
            _span = TyphonEvent.BeginSpatialQueryRadius((float)radius);
        }

        public SpatialQueryResult Current => _inner.Current;
        public RadiusEnumerator GetEnumerator() => this;

        public bool MoveNext()
        {
            var hit = _inner.MoveNext();
            if (hit && TelemetryConfig.SpatialQueryRadiusActive && _span.ResultCount < ushort.MaxValue)
            {
                _span.ResultCount++;
            }

            return hit;
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                _disposed = true;
                _span.Dispose();
                _inner.Dispose();
            }
        }
    }

    // ── Ray Query ────────────────────────────────────────────────────────

    /// <summary>
    /// Query entities whose fat AABB intersects a ray, yielding results in front-to-back order.
    /// Uses a min-heap sorted by ray entry distance for priority traversal.
    /// </summary>
    internal RayEnumerator QueryRay(ReadOnlySpan<double> origin, ReadOnlySpan<double> direction, double maxDist, ChangeSet changeSet = null,
        uint categoryMask = 0) => new(this, origin, direction, maxDist, changeSet, categoryMask);

    /// <summary>Inline min-heap buffer for ray query priority queue (64 entries).</summary>
    [InlineArray(64)]
    internal struct RayHeapChunkIds { private int _element0; }

    [InlineArray(64)]
    internal struct RayHeapDistances { private double _element0; }

    internal ref struct RayEnumerator
    {
        private readonly SpatialRTree<TStore> _tree;
        private ChunkAccessor<TStore> _accessor;
        private readonly SpatialNodeDescriptor _desc;
        private readonly double _maxDist;
        private readonly uint _categoryMask;

        // Ray parameters stored as fixed arrays (origin + inverse direction, max 3 dimensions)
        private fixed double _origin[3];
        private fixed double _invDir[3];
        private readonly int _coordCount;

        // Min-heap of (chunkId, tEntry)
        private RayHeapChunkIds _heapChunkIds;
        private RayHeapDistances _heapDists;
        private int _heapSize;

        // Current leaf iteration
        private int _currentLeafChunkId;
        private int _currentLeafIndex;
        private int _currentLeafCount;

        private SpatialQueryResult _current;
        private bool _disposed;

        // Phase 3: Spatial:Query:Ray span (Tier-2 gated).
        private SpatialQueryRayEvent _span;

        internal RayEnumerator(SpatialRTree<TStore> tree, ReadOnlySpan<double> origin, ReadOnlySpan<double> direction, double maxDist,
            ChangeSet changeSet, uint categoryMask = 0)
        {
            _tree = tree;
            _desc = tree._desc;
            _coordCount = _desc.CoordCount;
            _accessor = tree._segment.CreateChunkAccessor(changeSet);
            _maxDist = maxDist;
            _heapSize = 0;
            _currentLeafChunkId = 0;
            _currentLeafIndex = -1;
            _currentLeafCount = 0;
            _current = default;
            _disposed = false;
            _categoryMask = categoryMask;
            _span = TyphonEvent.BeginSpatialQueryRay((float)maxDist);

            int halfCoordInit = _desc.CoordCount / 2;
            bool degenerate = double.IsNaN(maxDist) || maxDist < 0;
            for (int d = 0; d < halfCoordInit; d++)
            {
                _origin[d] = d < origin.Length ? origin[d] : 0;
                double dir = d < direction.Length ? direction[d] : 0;
                _invDir[d] = dir != 0 ? 1.0 / dir : double.MaxValue;
                degenerate |= double.IsNaN(_origin[d]) || double.IsNaN(_invDir[d]);
            }

            if (tree._rootChunkId != 0 && !degenerate)
            {
                HeapPush(tree._rootChunkId, 0.0);
            }
        }

        public SpatialQueryResult Current => _current;
        public RayEnumerator GetEnumerator() => this;

        public bool MoveNext()
        {
            // Hoist stackalloc buffers outside all loops
            int halfCoord = _coordCount / 2;
            Span<double> coords = stackalloc double[_coordCount];

            // Pin fixed arrays directly — avoids stackalloc + copy per MoveNext() call
            fixed (double* pOrigin = _origin)
            fixed (double* pInvDir = _invDir)
            {
                var origin = new ReadOnlySpan<double>(pOrigin, halfCoord);
                var invDir = new ReadOnlySpan<double>(pInvDir, halfCoord);

                // Resume leaf scan if in progress
                while (_currentLeafChunkId != 0)
                {
                    _currentLeafIndex++;
                    if (_currentLeafIndex >= _currentLeafCount)
                    {
                        _currentLeafChunkId = 0;
                        break;
                    }

                    byte* leafBase = _accessor.GetChunkAddress(_currentLeafChunkId);
                    SpatialNodeHelper.ReadLeafEntryCoords(leafBase, _currentLeafIndex, coords, _desc);

                    var (hit, t) = SpatialGeometry.RayAABBIntersect(origin, invDir, coords, _coordCount);
                    if (hit && t <= _maxDist)
                    {
                        if (_categoryMask != 0 && (SpatialNodeHelper.ReadLeafCategoryMask(leafBase, _currentLeafIndex, _desc) & _categoryMask) != _categoryMask)
                        {
                            continue;
                        }
                        _current = new SpatialQueryResult(
                            SpatialNodeHelper.ReadLeafEntityId(leafBase, _currentLeafIndex, _desc),
                            SpatialNodeHelper.ReadLeafCompChunkId(leafBase, _currentLeafIndex, _desc));
                        if (TelemetryConfig.SpatialQueryRayActive && _span.ResultCount < ushort.MaxValue)
                        {
                            _span.ResultCount++;
                        }

                        return true;
                    }
                }

                // Priority queue traversal
                while (_heapSize > 0)
                {
                    double nextDist = _heapDists[0];
                    if (nextDist > _maxDist)
                    {
                        break; // Early termination
                    }

                    int chunkId = HeapPop();
                    byte* nodeBase = _accessor.GetChunkAddress(chunkId);

                    var latch = GetLatch(nodeBase);
                    int version = latch.ReadVersion();
                    if (version == 0)
                    {
                        RestartFromRoot();
                        if (TelemetryConfig.SpatialQueryRayActive && _span.RestartCount < byte.MaxValue)
                        {
                            _span.RestartCount++;
                        }

                        continue;
                    }

                    bool isLeaf = SpatialNodeHelper.IsLeaf(nodeBase);
                    int count = SpatialNodeHelper.GetCount(nodeBase);

                    if (!latch.ValidateVersion(version))
                    {
                        RestartFromRoot();
                        if (TelemetryConfig.SpatialQueryRayActive && _span.RestartCount < byte.MaxValue)
                        {
                            _span.RestartCount++;
                        }

                        continue;
                    }

                    // Node-level category mask pruning
                    if (_categoryMask != 0 && (SpatialNodeHelper.ReadUnionCategoryMask(nodeBase, _desc) & _categoryMask) == 0)
                    {
                        continue;
                    }

                    if (TelemetryConfig.SpatialQueryRayActive && _span.NodesVisited < ushort.MaxValue)
                    {
                        _span.NodesVisited++;
                    }

                    if (isLeaf)
                    {
                        _currentLeafChunkId = chunkId;
                        _currentLeafIndex = -1;
                        _currentLeafCount = count;
                        return MoveNext();
                    }

                    // Internal node: push children with their ray entry distances
                    for (int i = 0; i < count; i++)
                    {
                        SpatialNodeHelper.ReadInternalEntryCoords(nodeBase, i, coords, _desc);
                        var (hit, t) = SpatialGeometry.RayAABBIntersect(origin, invDir, coords, _coordCount);
                        if (hit && t <= _maxDist && _heapSize < 64)
                        {
                            int childId = SpatialNodeHelper.ReadInternalChildId(nodeBase, i, _desc);
                            HeapPush(childId, t);
                        }
                    }

                    if (!latch.ValidateVersion(version))
                    {
                        RestartFromRoot();
                        if (TelemetryConfig.SpatialQueryRayActive && _span.RestartCount < byte.MaxValue)
                        {
                            _span.RestartCount++;
                        }
                    }
                }

                return false;
            } // fixed (_origin, _invDir)
        }

        private void RestartFromRoot()
        {
            _heapSize = 0;
            _currentLeafChunkId = 0;
            if (_tree._rootChunkId != 0)
            {
                HeapPush(_tree._rootChunkId, 0.0);
            }
        }

        private void HeapPush(int chunkId, double dist)
        {
            int i = _heapSize++;
            _heapChunkIds[i] = chunkId;
            _heapDists[i] = dist;
            // Sift up
            while (i > 0)
            {
                int parent = (i - 1) / 2;
                if (_heapDists[parent] <= _heapDists[i])
                {
                    break;
                }
                (_heapChunkIds[parent], _heapChunkIds[i]) = (_heapChunkIds[i], _heapChunkIds[parent]);
                (_heapDists[parent], _heapDists[i]) = (_heapDists[i], _heapDists[parent]);
                i = parent;
            }
        }

        private int HeapPop()
        {
            int result = _heapChunkIds[0];
            _heapSize--;
            if (_heapSize > 0)
            {
                _heapChunkIds[0] = _heapChunkIds[_heapSize];
                _heapDists[0] = _heapDists[_heapSize];
                // Sift down
                int i = 0;
                while (true)
                {
                    int left = 2 * i + 1;
                    int right = 2 * i + 2;
                    int smallest = i;
                    if (left < _heapSize && _heapDists[left] < _heapDists[smallest])
                    {
                        smallest = left;
                    }
                    if (right < _heapSize && _heapDists[right] < _heapDists[smallest])
                    {
                        smallest = right;
                    }
                    if (smallest == i)
                    {
                        break;
                    }
                    (_heapChunkIds[i], _heapChunkIds[smallest]) = (_heapChunkIds[smallest], _heapChunkIds[i]);
                    (_heapDists[i], _heapDists[smallest]) = (_heapDists[smallest], _heapDists[i]);
                    i = smallest;
                }
            }
            return result;
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                _disposed = true;
                _span.Dispose();
                _accessor.Dispose();
            }
        }
    }

    // ── Frustum Query ────────────────────────────────────────────────────

    /// <summary>
    /// Query entities whose fat AABB intersects a frustum defined by a set of half-space planes.
    /// Optimizes with INSIDE subtree yields (entire subtree visible → skip per-entry plane tests).
    /// Planes packed as (normalX, normalY, [normalZ,] distance), dimCount+1 doubles per plane.
    /// </summary>
    internal FrustumEnumerator QueryFrustum(ReadOnlySpan<double> planes, int planeCount, ChangeSet changeSet = null, uint categoryMask = 0)
        => new(this, planes, planeCount, changeSet, categoryMask);

    /// <summary>Stack buffer for frustum DFS — encodes (chunkId, fullyInside) via sign bit.</summary>
    [InlineArray(256)]
    internal struct FrustumStackBuffer { private int _element0; }

    internal ref struct FrustumEnumerator
    {
        private readonly SpatialRTree<TStore> _tree;
        private ChunkAccessor<TStore> _accessor;
        private readonly SpatialNodeDescriptor _desc;
        private readonly int _planeCount;
        private readonly int _dimCount;
        private readonly int _planeDataLen; // _planeCount * (_dimCount + 1)
        private readonly uint _categoryMask;

        // Planes stored inline: max 6 planes × 4 doubles = 24 doubles
        private fixed double _planes[24];

        // DFS stack — sign bit encodes fullyInside flag
        private FrustumStackBuffer _stack;
        private int _stackTop;

        // Current leaf iteration
        private int _currentLeafChunkId;
        private int _currentLeafIndex;
        private int _currentLeafCount;
        private bool _currentLeafFullyInside;

        private SpatialQueryResult _current;
        private bool _disposed;

        // Phase 3: Spatial:Query:Frustum span (Tier-2 gated).
        private SpatialQueryFrustumEvent _span;

        internal FrustumEnumerator(SpatialRTree<TStore> tree, ReadOnlySpan<double> planes, int planeCount, ChangeSet changeSet, uint categoryMask = 0)
        {
            _tree = tree;
            _desc = tree._desc;
            _dimCount = _desc.CoordCount / 2;
            _planeCount = planeCount;
            _planeDataLen = planeCount * (_dimCount + 1);
            _accessor = tree._segment.CreateChunkAccessor(changeSet);
            _stackTop = 0;
            _currentLeafChunkId = 0;
            _currentLeafIndex = -1;
            _currentLeafCount = 0;
            _currentLeafFullyInside = false;
            _current = default;
            _disposed = false;
            _categoryMask = categoryMask;
            _span = TyphonEvent.BeginSpatialQueryFrustum((byte)Math.Min(planeCount, byte.MaxValue));

            int len = Math.Min(planes.Length, 24);
            for (int i = 0; i < len; i++)
            {
                _planes[i] = planes[i];
            }

            if (tree._rootChunkId != 0)
            {
                _stack[0] = tree._rootChunkId; // bit 31 clear = needs testing
                _stackTop = 1;
            }
        }

        public SpatialQueryResult Current => _current;
        public FrustumEnumerator GetEnumerator() => this;

        public bool MoveNext()
        {
            // Hoist reusable coord buffer outside all loops
            Span<double> coords = stackalloc double[_desc.CoordCount];

            // Pin fixed plane array directly — avoids stackalloc + copy per MoveNext() call
            fixed (double* p = _planes)
            {
                var planeSpan = new ReadOnlySpan<double>(p, _planeDataLen);

                // Resume leaf scan
                while (_currentLeafChunkId != 0)
                {
                    _currentLeafIndex++;
                    if (_currentLeafIndex >= _currentLeafCount)
                    {
                        _currentLeafChunkId = 0;
                        break;
                    }

                    if (_currentLeafFullyInside)
                    {
                        // INSIDE optimization: yield without plane tests (but still check category mask)
                        byte* leafBase = _accessor.GetChunkAddress(_currentLeafChunkId);
                        if (_categoryMask != 0 && (SpatialNodeHelper.ReadLeafCategoryMask(leafBase, _currentLeafIndex, _desc) & _categoryMask) != _categoryMask)
                        {
                            continue;
                        }
                        _current = new SpatialQueryResult(
                            SpatialNodeHelper.ReadLeafEntityId(leafBase, _currentLeafIndex, _desc),
                            SpatialNodeHelper.ReadLeafCompChunkId(leafBase, _currentLeafIndex, _desc));
                        if (TelemetryConfig.SpatialQueryFrustumActive && _span.ResultCount < ushort.MaxValue)
                        {
                            _span.ResultCount++;
                        }

                        return true;
                    }

                    // Test individual entry against frustum
                    byte* lb = _accessor.GetChunkAddress(_currentLeafChunkId);
                    SpatialNodeHelper.ReadLeafEntryCoords(lb, _currentLeafIndex, coords, _desc);

                    int cls = SpatialGeometry.ClassifyAABBAgainstPlanes(coords, planeSpan, _planeCount, _dimCount);
                    if (cls != SpatialGeometry.FrustumOutside)
                    {
                        if (_categoryMask != 0 && (SpatialNodeHelper.ReadLeafCategoryMask(lb, _currentLeafIndex, _desc) & _categoryMask) != _categoryMask)
                        {
                            continue;
                        }
                        _current = new SpatialQueryResult(
                            SpatialNodeHelper.ReadLeafEntityId(lb, _currentLeafIndex, _desc),
                            SpatialNodeHelper.ReadLeafCompChunkId(lb, _currentLeafIndex, _desc));
                        if (TelemetryConfig.SpatialQueryFrustumActive && _span.ResultCount < ushort.MaxValue)
                        {
                            _span.ResultCount++;
                        }

                        return true;
                    }
                }

                // DFS traversal
                while (_stackTop > 0)
                {
                    int encoded = _stack[--_stackTop];
                    bool fullyInside = (encoded & unchecked((int)0x80000000)) != 0;
                    int chunkId = encoded & 0x7FFFFFFF;

                    byte* nodeBase = _accessor.GetChunkAddress(chunkId);

                    var latch = GetLatch(nodeBase);
                    int version = latch.ReadVersion();
                    if (version == 0)
                    {
                        RestartFromRoot();
                        if (TelemetryConfig.SpatialQueryFrustumActive && _span.RestartCount < byte.MaxValue)
                        {
                            _span.RestartCount++;
                        }

                        continue;
                    }

                    bool isLeaf = SpatialNodeHelper.IsLeaf(nodeBase);
                    int count = SpatialNodeHelper.GetCount(nodeBase);

                    if (!latch.ValidateVersion(version))
                    {
                        RestartFromRoot();
                        if (TelemetryConfig.SpatialQueryFrustumActive && _span.RestartCount < byte.MaxValue)
                        {
                            _span.RestartCount++;
                        }

                        continue;
                    }

                    // Node-level category mask pruning
                    if (_categoryMask != 0 && (SpatialNodeHelper.ReadUnionCategoryMask(nodeBase, _desc) & _categoryMask) == 0)
                    {
                        continue;
                    }

                    if (TelemetryConfig.SpatialQueryFrustumActive && _span.NodesVisited < ushort.MaxValue)
                    {
                        _span.NodesVisited++;
                    }

                    if (isLeaf)
                    {
                        _currentLeafChunkId = chunkId;
                        _currentLeafIndex = -1;
                        _currentLeafCount = count;
                        _currentLeafFullyInside = fullyInside;
                        return MoveNext();
                    }

                    if (fullyInside)
                    {
                        // All children are fully inside — push with fullyInside flag
                        for (int i = count - 1; i >= 0; i--)
                        {
                            int childId = SpatialNodeHelper.ReadInternalChildId(nodeBase, i, _desc);
                            if (_stackTop < 256)
                            {
                                _stack[_stackTop++] = childId | unchecked((int)0x80000000); // bit 31 = fully inside
                            }
                            else
                            {
                                // Tier-0 always-on record (#422): latch-safe — never throw under the OLC read latch.
                                SpatialRTreeDiagnostics.RecordDfsStackOverflow("frustum");
                            }
                        }
                    }
                    else
                    {
                        // Classify each child
                        for (int i = count - 1; i >= 0; i--)
                        {
                            SpatialNodeHelper.ReadInternalEntryCoords(nodeBase, i, coords, _desc);
                            int cls = SpatialGeometry.ClassifyAABBAgainstPlanes(coords, planeSpan, _planeCount, _dimCount);
                            if (cls == SpatialGeometry.FrustumOutside)
                            {
                                continue;
                            }
                            int childId = SpatialNodeHelper.ReadInternalChildId(nodeBase, i, _desc);
                            if (_stackTop < 256)
                            {
                                _stack[_stackTop++] = cls == SpatialGeometry.FrustumInside ? childId | unchecked((int)0x80000000) : childId;
                            }
                            else
                            {
                                // Tier-0 always-on record (#422): latch-safe — never throw under the OLC read latch.
                                SpatialRTreeDiagnostics.RecordDfsStackOverflow("frustum");
                            }
                        }
                    }

                    if (!latch.ValidateVersion(version))
                    {
                        RestartFromRoot();
                        if (TelemetryConfig.SpatialQueryFrustumActive && _span.RestartCount < byte.MaxValue)
                        {
                            _span.RestartCount++;
                        }
                    }
                }

                return false;
            } // fixed (_planes)
        }

        private void RestartFromRoot()
        {
            _stackTop = 0;
            _currentLeafChunkId = 0;
            if (_tree._rootChunkId != 0)
            {
                _stack[0] = _tree._rootChunkId;
                _stackTop = 1;
            }
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                _disposed = true;
                _span.Dispose();
                _accessor.Dispose();
            }
        }
    }

    // ── kNN Query ────────────────────────────────────────────────────────

    /// <summary>
    /// Find the k nearest entity candidates to a point via iterative radius expansion.
    /// Returns entities whose fat AABB falls within the search radius. The <c>distSq</c> field is set to 0 — callers must recompute actual distances from
    /// component data (the tree stores fat AABBs, not tight bounds). Converges in 1–2 iterations for k &lt; 20.
    /// </summary>
    /// <returns>Number of results written (may be less than k if fewer entities exist).</returns>
    internal int QueryKNN(ReadOnlySpan<double> center, int k, Span<(long entityId, double distSq)> results, ChangeSet changeSet = null, uint categoryMask = 0)
    {
        if (k <= 0 || _entityCount == 0)
        {
            return 0;
        }

        // Phase 3: Spatial:Query:Knn span (Tier-2 gated). IterCount/FinalRadius/ResultCount filled at exit.
        var knnScope = TyphonEvent.BeginSpatialQueryKnn((ushort)Math.Min(k, ushort.MaxValue));
        try
        {

            int halfCoord = _desc.CoordCount / 2;

            // Estimate initial radius from entity density
            double worldVolume = 1.0;
            if (_entityCount > 1)
            {
                // Read root node MBR to estimate world extent
                var accessor = _segment.CreateChunkAccessor(changeSet);
                try
                {
                    byte* rootBase = accessor.GetChunkAddress(_rootChunkId);
                    for (int d = 0; d < halfCoord; d++)
                    {
                        double extent = SpatialNodeHelper.ReadNodeMBRCoord(rootBase, d + halfCoord, _desc) -
                                        SpatialNodeHelper.ReadNodeMBRCoord(rootBase, d, _desc);
                        if (extent > 0)
                        {
                            worldVolume *= extent;
                        }
                    }
                }
                finally
                {
                    accessor.Dispose();
                }
            }

            double entityDensity = _entityCount / Math.Max(worldVolume, 1e-10);
            double volumeForK = k / Math.Max(entityDensity, 1e-10);
            double radius = Math.Pow(volumeForK, 1.0 / halfCoord) * 1.5; // 1.5x safety factor
            radius = Math.Max(radius, 1.0); // Minimum radius

            // Iterative expansion — collect candidate entity IDs within expanding radius. distSq is set to 0 at the tree level because the tree stores fat
            // AABBs, not tight bounds. Callers must recompute actual distances from component data for precise ordering.
            int maxCandidates = Math.Min(k * 4, 256);
            Span<(long entityId, double distSq)> candidates = stackalloc (long, double)[maxCandidates];
            int lastCount = 0;

            for (int iteration = 0; iteration < 8; iteration++)
            {
                int count = 0;
                foreach (var result in QueryRadius(center, radius, changeSet, categoryMask))
                {
                    if (count >= candidates.Length)
                    {
                        break;
                    }
                    candidates[count++] = (result.EntityId, 0);
                }

                if (count >= k || count == lastCount || radius > 1e15)
                {
                    int resultCount = Math.Min(count, k);
                    resultCount = Math.Min(resultCount, results.Length);
                    for (int i = 0; i < resultCount; i++)
                    {
                        results[i] = candidates[i];
                    }
                    knnScope.IterCount = (byte)Math.Min(iteration + 1, byte.MaxValue);
                    knnScope.FinalRadius = (float)radius;
                    knnScope.ResultCount = (ushort)Math.Min(resultCount, ushort.MaxValue);
                    return resultCount;
                }

                lastCount = count;
                radius *= 2.0;
            }

            // Iteration limit reached — return whatever candidates we have from the last pass
            int finalCount = Math.Min(lastCount, k);
            finalCount = Math.Min(finalCount, results.Length);
            for (int i = 0; i < finalCount; i++)
            {
                results[i] = candidates[i];
            }
            knnScope.IterCount = 8;
            knnScope.FinalRadius = (float)radius;
            knnScope.ResultCount = (ushort)Math.Min(finalCount, ushort.MaxValue);
            return finalCount;
        }
        finally
        {
            knnScope.Dispose();
        }
    }

    // ── Count Queries ────────────────────────────────────────────────────

    // Containment classification constants for count query subtree shortcut
    private const int ContainmentDisjoint = 0;
    private const int ContainmentOverlapping = 1;
    private const int ContainmentFullyContained = 2;

    /// <summary>
    /// Count entities whose fat AABB overlaps the given query box without materializing results.
    /// Uses a subtree counting shortcut: when a node's MBR is fully contained within the query region, its entries are counted without per-entry overlap
    /// tests (up to ~30x faster for large fully-covered regions).
    /// </summary>
    internal int CountInAABB(ReadOnlySpan<double> queryCoords, ChangeSet changeSet = null, uint categoryMask = 0)
    {
        if (_rootChunkId == 0)
        {
            return 0;
        }

        // Phase 3: Spatial:Query:Count span (variant 0=AABB). ResultCount filled at exit.
        var countScope = TyphonEvent.BeginSpatialQueryCount(0);
        var accessor = _segment.CreateChunkAccessor(changeSet);
        try
        {
            int count = 0;
            QueryStackBuffer stack = default;
            int stackTop = 0;
            int coordCount = _desc.CoordCount;

            // Copy query coords to stackalloc buffer for pointer-based access in hot loops
            double* qc = stackalloc double[6];
            int len = Math.Min(queryCoords.Length, 6);
            for (int i = 0; i < len; i++)
            {
                qc[i] = queryCoords[i];
            }

            // Sign-bit encoding: bit 31 marks a node as "fully contained" — all its descendants
            // are geometrically inside the query region, so overlap tests can be skipped.
            // Safe because chunk IDs are small positive ints (allocated sequentially from 0).
            const int fullyContainedFlag = unchecked((int)0x80000000);

            stack[0] = _rootChunkId;
            stackTop = 1;

            while (stackTop > 0)
            {
                int raw = stack[--stackTop];
                bool fullyContained = (raw & fullyContainedFlag) != 0;
                int chunkId = raw & 0x7FFFFFFF;

                byte* nodeBase = accessor.GetChunkAddress(chunkId);

                var latch = GetLatch(nodeBase);
                int version = latch.ReadVersion();
                if (version == 0)
                {
                    count = 0;
                    stackTop = 0;
                    stack[0] = _rootChunkId;
                    stackTop = 1;
                    continue;
                }

                bool isLeaf = SpatialNodeHelper.IsLeaf(nodeBase);
                int nodeCount = SpatialNodeHelper.GetCount(nodeBase);

                if (!latch.ValidateVersion(version))
                {
                    count = 0;
                    stackTop = 0;
                    stack[0] = _rootChunkId;
                    stackTop = 1;
                    continue;
                }

                // Node-level category pruning: skip entire node if no entries can match
                if (categoryMask != 0 && (SpatialNodeHelper.ReadUnionCategoryMask(nodeBase, _desc) & categoryMask) == 0)
                {
                    continue;
                }

                if (isLeaf)
                {
                    if (fullyContained && categoryMask == 0)
                    {
                        // Maximum shortcut: all entries geometrically match, no category filter
                        count += nodeCount;
                    }
                    else if (fullyContained)
                    {
                        // Fully contained but need category check (skip overlap tests)
                        for (int i = 0; i < nodeCount; i++)
                        {
                            if ((SpatialNodeHelper.ReadLeafCategoryMask(nodeBase, i, _desc) & categoryMask) == categoryMask)
                            {
                                count++;
                            }
                        }
                    }
                    else
                    {
                        // Standard path: overlap test + category test per entry
                        if (coordCount == 4)
                        {
                            // 2D unrolled leaf scan
                            for (int i = 0; i < nodeCount; i++)
                            {
                                if (SpatialNodeHelper.ReadLeafCoord(nodeBase, i, 2, _desc) >= qc[0]
                                    && SpatialNodeHelper.ReadLeafCoord(nodeBase, i, 0, _desc) <= qc[2]
                                    && SpatialNodeHelper.ReadLeafCoord(nodeBase, i, 3, _desc) >= qc[1]
                                    && SpatialNodeHelper.ReadLeafCoord(nodeBase, i, 1, _desc) <= qc[3])
                                {
                                    if (categoryMask == 0
                                        || (SpatialNodeHelper.ReadLeafCategoryMask(nodeBase, i, _desc) & categoryMask) == categoryMask)
                                    {
                                        count++;
                                    }
                                }
                            }
                        }
                        else
                        {
                            // 3D unrolled leaf scan
                            for (int i = 0; i < nodeCount; i++)
                            {
                                if (SpatialNodeHelper.ReadLeafCoord(nodeBase, i, 3, _desc) >= qc[0]
                                    && SpatialNodeHelper.ReadLeafCoord(nodeBase, i, 0, _desc) <= qc[3]
                                    && SpatialNodeHelper.ReadLeafCoord(nodeBase, i, 4, _desc) >= qc[1]
                                    && SpatialNodeHelper.ReadLeafCoord(nodeBase, i, 1, _desc) <= qc[4]
                                    && SpatialNodeHelper.ReadLeafCoord(nodeBase, i, 5, _desc) >= qc[2]
                                    && SpatialNodeHelper.ReadLeafCoord(nodeBase, i, 2, _desc) <= qc[5])
                                {
                                    if (categoryMask == 0
                                        || (SpatialNodeHelper.ReadLeafCategoryMask(nodeBase, i, _desc) & categoryMask) == categoryMask)
                                    {
                                        count++;
                                    }
                                }
                            }
                        }
                    }
                }
                else
                {
                    // Internal node
                    if (fullyContained)
                    {
                        // All children inherit fully-contained status
                        for (int i = nodeCount - 1; i >= 0; i--)
                        {
                            int childId = SpatialNodeHelper.ReadInternalChildId(nodeBase, i, _desc);
                            if (stackTop < 256)
                            {
                                stack[stackTop++] = childId | fullyContainedFlag;
                            }
                            else
                            {
                                // Tier-0 always-on record (#422): latch-safe — never throw under the OLC read latch.
                                SpatialRTreeDiagnostics.RecordDfsStackOverflow("count");
                            }
                        }
                    }
                    else
                    {
                        // Classify each child: disjoint / overlapping / fully contained
                        if (coordCount == 4)
                        {
                            // 2D unrolled containment classification
                            for (int i = nodeCount - 1; i >= 0; i--)
                            {
                                double cMinX = SpatialNodeHelper.ReadInternalCoord(nodeBase, i, 0, _desc);
                                double cMinY = SpatialNodeHelper.ReadInternalCoord(nodeBase, i, 1, _desc);
                                double cMaxX = SpatialNodeHelper.ReadInternalCoord(nodeBase, i, 2, _desc);
                                double cMaxY = SpatialNodeHelper.ReadInternalCoord(nodeBase, i, 3, _desc);

                                // Disjoint?
                                if (cMaxX < qc[0] || cMinX > qc[2] || cMaxY < qc[1] || cMinY > qc[3])
                                {
                                    continue;
                                }

                                int childId = SpatialNodeHelper.ReadInternalChildId(nodeBase, i, _desc);
                                if (stackTop < 256)
                                {
                                    // Fully contained?
                                    if (cMinX >= qc[0] && cMaxX <= qc[2] && cMinY >= qc[1] && cMaxY <= qc[3])
                                    {
                                        stack[stackTop++] = childId | fullyContainedFlag;
                                    }
                                    else
                                    {
                                        stack[stackTop++] = childId;
                                    }
                                }
                                else
                                {
                                    // Tier-0 always-on record (#422): latch-safe — never throw under the OLC read latch.
                                    SpatialRTreeDiagnostics.RecordDfsStackOverflow("count");
                                }
                            }
                        }
                        else
                        {
                            // 3D unrolled containment classification
                            for (int i = nodeCount - 1; i >= 0; i--)
                            {
                                double cMinX = SpatialNodeHelper.ReadInternalCoord(nodeBase, i, 0, _desc);
                                double cMinY = SpatialNodeHelper.ReadInternalCoord(nodeBase, i, 1, _desc);
                                double cMinZ = SpatialNodeHelper.ReadInternalCoord(nodeBase, i, 2, _desc);
                                double cMaxX = SpatialNodeHelper.ReadInternalCoord(nodeBase, i, 3, _desc);
                                double cMaxY = SpatialNodeHelper.ReadInternalCoord(nodeBase, i, 4, _desc);
                                double cMaxZ = SpatialNodeHelper.ReadInternalCoord(nodeBase, i, 5, _desc);

                                // Disjoint?
                                if (cMaxX < qc[0] || cMinX > qc[3] || cMaxY < qc[1] || cMinY > qc[4]
                                    || cMaxZ < qc[2] || cMinZ > qc[5])
                                {
                                    continue;
                                }

                                int childId = SpatialNodeHelper.ReadInternalChildId(nodeBase, i, _desc);
                                if (stackTop < 256)
                                {
                                    // Fully contained?
                                    if (cMinX >= qc[0] && cMaxX <= qc[3] && cMinY >= qc[1] && cMaxY <= qc[4]
                                        && cMinZ >= qc[2] && cMaxZ <= qc[5])
                                    {
                                        stack[stackTop++] = childId | fullyContainedFlag;
                                    }
                                    else
                                    {
                                        stack[stackTop++] = childId;
                                    }
                                }
                                else
                                {
                                    // Tier-0 always-on record (#422): latch-safe — never throw under the OLC read latch.
                                    SpatialRTreeDiagnostics.RecordDfsStackOverflow("count");
                                }
                            }
                        }
                    }
                }

                if (!latch.ValidateVersion(version))
                {
                    count = 0;
                    stackTop = 0;
                    stack[0] = _rootChunkId;
                    stackTop = 1;
                }
            }

            countScope.ResultCount = count;
            return count;
        }
        finally
        {
            countScope.Dispose();
            accessor.Dispose();
        }
    }

    /// <summary>
    /// Count entities whose fat AABB overlaps a sphere defined by center + radius.
    /// Converts to AABB query internally (same coarse filter as <see cref="RadiusEnumerator"/>).
    /// </summary>
    internal int CountInRadius(ReadOnlySpan<double> center, double radius, ChangeSet changeSet = null, uint categoryMask = 0)
    {
        // Phase 3: Spatial:Query:Count span (variant 1=Radius). Inner CountInAABB also emits its own variant=0 span.
        var countScope = TyphonEvent.BeginSpatialQueryCount(1);
        try
        {
            radius = Math.Max(radius, 0);
            int halfCoord = _desc.CoordCount / 2;
            Span<double> aabb = stackalloc double[_desc.CoordCount];
            for (int d = 0; d < halfCoord; d++)
            {
                aabb[d] = center[d] - radius;
                aabb[d + halfCoord] = center[d] + radius;
            }
            var result = CountInAABB(aabb, changeSet, categoryMask);
            countScope.ResultCount = result;
            return result;
        }
        finally
        {
            countScope.Dispose();
        }
    }
}
