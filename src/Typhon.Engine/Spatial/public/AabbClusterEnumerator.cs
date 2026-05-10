using System.Numerics;
using System.Runtime.CompilerServices;

namespace Typhon.Engine;

/// <summary>
/// Zero-allocation f32 AABB query enumerator over the per-cell cluster spatial index of a single archetype (issue #230 Phase 3). Shared between the
/// game-facing generic entry point <see cref="ClusterSpatialQuery{TArch}.AABB{TBox}"/> and the engine-facing non-generic entry point
/// <see cref="ArchetypeClusterState.QueryAabb"/>. Handles both 2D (AABB2F / BSphere2F) and 3D (AABB3F / BSphere3F) cluster storage tiers through a single
/// state machine — the 3D overlap test is a strict superset of the 2D test, and 2D archetypes are queried with an infinite Z range that trivially passes
/// the Z component of the overlap check.
/// </summary>
/// <remarks>
/// <para>
/// The state machine is three-phase per call to <see cref="MoveNext"/>:
/// (1) drain the current cluster's occupancy bits and test each entity's tight bounds against the query AABB (narrowphase),
/// (2) advance to the next cluster in the current cell's <see cref="CellSpatialIndex"/> and apply broadphase AABB + category mask filtering,
/// (3) advance to the next cell in the query's overlap range and look up the archetype's per-cell slot.
/// </para>
/// <para>
/// <b>Tier handling.</b> The narrowphase branches on <see cref="SpatialFieldInfo.FieldType"/> to unpack entity coordinates correctly: 2D fields write 4
/// doubles <c>[minX, minY, maxX, maxY]</c> via <see cref="SpatialMaintainer.ReadAndValidateBoundsFromPtr"/> while 3D fields write 6 doubles
/// <c>[minX, minY, minZ, maxX, maxY, maxZ]</c>. The storage layer (<see cref="ClusterSpatialAabb"/> / <see cref="CellSpatialIndex"/>) uses unified 6-float
/// storage, so the broadphase overlap test always runs in 3D and implicitly handles 2D via infinite Z sentinels.
/// </para>
/// <para>
/// <b>Epoch scope.</b> The caller must be inside an <see cref="EpochGuard"/> scope; the enumerator creates a <see cref="ChunkAccessor{TStore}"/> on the
/// cluster segment to read entity bounds during the narrowphase pass. The accessor is lazily opened on the first broadphase hit to keep empty-result queries
/// allocation-free.
/// </para>
/// <para>
/// <b>Phase 3 history.</b> Originally introduced as a nested <c>ClusterSpatialQuery&lt;TArch&gt;.AABBEnumerator</c> ref struct, hoisted out of the generic
/// outer type as <c>Aabb2fEnumerator</c> during Phase 3 scaffolding, then unified to handle both 2D and 3D when the 3D-blocker scope discovery showed
/// existing cluster archetypes depend on 3D bounds via the legacy per-entity tree.
/// </para>
/// </remarks>
public unsafe ref struct AabbClusterEnumerator
{
    private readonly ArchetypeClusterState _state;
    private readonly SpatialGrid _grid;

    // Query bounds in world units (f32). For 2D queries, the Z components are set to +/- infinity by the caller so the Z overlap test trivially passes
    // against 2D cluster storage (which leaves Z bounds at the ClusterSpatialAabb.Empty sentinel values).
    private readonly float _queryMinX;
    private readonly float _queryMinY;
    private readonly float _queryMinZ;
    private readonly float _queryMaxX;
    private readonly float _queryMaxY;
    private readonly float _queryMaxZ;
    private readonly uint _categoryMask;

    // Cell range the query AABB covers, inclusive. Clamped to the grid extent by SpatialGrid. The grid itself is 2D (XY) — Z is never used for cell
    // bucketing; 3D archetypes still bucket into the same XY cells and distinguish by Z at the narrowphase only.
    private readonly int _cellMinX;
    private readonly int _cellMinY;
    private readonly int _cellMaxX;
    private readonly int _cellMaxY;

    // Cluster-SoA field offset for the spatial field within each cluster, precomputed.
    private readonly int _spatialCompOffset;
    private readonly int _spatialCompSize;
    private readonly int _spatialFieldOffset;
    private readonly SpatialFieldInfo _fieldInfo;
    private readonly SpatialNodeDescriptor _descriptor;

    // Is the cluster's spatial field 3D? Precomputed at construction so the narrowphase inner loop doesn't re-dispatch on FieldType every iteration.
    private readonly bool _is3D;

    // Optional radius filter (issue #230 Phase 3 — Radius query support). When <see cref="_radiusSq"/> is positive, the narrowphase applies a distance
    // check after the AABB overlap check: the entity passes only if the closest point on its tight AABB to (_radiusCenterX, _radiusCenterY, _radiusCenterZ)
    // is within sqrt(_radiusSq). The broadphase still uses the enclosing AABB — the caller is responsible for constructing an enumerator whose
    // _queryMin*/_queryMax* bounds match the sphere's enclosing AABB, so the cell expansion and cluster AABB overlap cover every candidate. When
    // <see cref="_radiusSq"/> is zero, the narrowphase runs the pure AABB check only (legacy AabbClusterEnumerator behavior).
    private readonly float _radiusSq;
    private readonly float _radiusCenterX;
    private readonly float _radiusCenterY;
    private readonly float _radiusCenterZ;

    // Cluster segment accessor for narrowphase entity reads. Disposed via Dispose().
    private ChunkAccessor<PersistentStore> _accessor;
    private bool _accessorCreated;

    // Iteration state.
    private int _currentCellX;
    private int _currentCellY;
    private CellSpatialIndex _currentCellIndex;    // null when we need to advance to the next cell
    private int _currentBroadphaseSlot;            // next index into _currentCellIndex.ClusterIds to scan
    private ulong _currentOccupancyBits;           // remaining occupied slots in the current cluster (bits cleared as we iterate)
    private int _currentClusterChunkId;            // chunk id of the cluster currently in narrowphase
    private byte* _currentClusterBase;             // base pointer of that cluster

    // Two-pass per-cell iteration: each cell has a StaticIndex and a DynamicIndex, both optional. Issue #230 Phase 3 activated the Static path. The
    // enumerator visits DynamicIndex first, then StaticIndex, then advances to the next cell. _currentCellStaticPass is true when we've already drained
    // DynamicIndex and are now iterating StaticIndex for the same cell.
    private bool _currentCellStaticPass;
    private PerCellSpatialSlot _currentPerCellSlot;

    // Last-yielded result.
    private ClusterSpatialQueryResult _current;

    internal AabbClusterEnumerator(ArchetypeClusterState state, SpatialGrid grid, float minX, float minY, float minZ, float maxX, float maxY, float maxZ,
        uint categoryMask, float radiusSq = 0f, float radiusCenterX = 0f, float radiusCenterY = 0f, float radiusCenterZ = 0f)
    {
        _state = state;
        _grid = grid;
        _queryMinX = minX;
        _queryMinY = minY;
        _queryMinZ = minZ;
        _queryMaxX = maxX;
        _queryMaxY = maxY;
        _queryMaxZ = maxZ;
        _categoryMask = categoryMask;
        _radiusSq = radiusSq;
        _radiusCenterX = radiusCenterX;
        _radiusCenterY = radiusCenterY;
        _radiusCenterZ = radiusCenterZ;

        // Expand query AABB to the overlapping cell range. The grid is 2D (XY) — Z coordinates never participate in cell bucketing. Each overlapping cell's
        // per-archetype spatial slot may or may not exist — the iteration handles null slots gracefully.
        grid.WorldToCellRange(minX, minY, maxX, maxY, out _cellMinX, out _cellMinY, out _cellMaxX, out _cellMaxY);

        var ss = state.SpatialSlot;
        _spatialCompOffset = state.Layout.ComponentOffset(ss.Slot);
        _spatialCompSize = state.Layout.ComponentSize(ss.Slot);
        _spatialFieldOffset = ss.FieldOffset;
        _fieldInfo = ss.FieldInfo;
        _descriptor = ss.Descriptor;
        _is3D = ss.FieldInfo.FieldType == SpatialFieldType.AABB3F || ss.FieldInfo.FieldType == SpatialFieldType.BSphere3F;

        _accessor = default;
        _accessorCreated = false;
        _currentCellX = _cellMinX;
        _currentCellY = _cellMinY;
        _currentCellIndex = null;
        _currentBroadphaseSlot = 0;
        _currentOccupancyBits = 0UL;
        _currentClusterChunkId = 0;
        _currentClusterBase = null;
        _currentCellStaticPass = false;
        _currentPerCellSlot = null;
        _current = default;
    }

    /// <summary>The most recently yielded result. Valid only after <see cref="MoveNext"/> returns <c>true</c>.</summary>
    public ClusterSpatialQueryResult Current => _current;

    /// <summary>Advance to the next matching entity. Returns <c>false</c> when the query is exhausted.</summary>
    public bool MoveNext()
    {
        // Hoisted stackalloc scratch for narrowphase entity bound reads. Sized for 3D (6 doubles) — 2D reads only populate the first 4 slots, and the
        // unused tail costs nothing. Allocating ONCE per MoveNext call (not per loop iteration) avoids accumulating stack pressure across iterations of the
        // state machine's while(true) — a query that scans thousands of clusters before finding the first match would otherwise allocate 48 bytes per
        // iteration that can't be released until MoveNext returns. See CA2014 for the general guidance.
        System.Span<double> entityCoords = stackalloc double[6];

        // Lazy accessor creation: only opened when the first cluster is about to be scanned. Avoids accessor construction cost for empty queries (no
        // overlapping cells with clusters).
        while (true)
        {
            // 1. Drain the current cluster's occupancy bits (narrowphase).
            if (_currentOccupancyBits != 0UL && _currentClusterBase != null)
            {
                int slot = BitOperations.TrailingZeroCount(_currentOccupancyBits);
                _currentOccupancyBits &= _currentOccupancyBits - 1;

                // Read entity's tight bounds and test against query AABB.
                byte* fieldPtr = _currentClusterBase + _spatialCompOffset + slot * _spatialCompSize + _spatialFieldOffset;
                if (!SpatialMaintainer.ReadAndValidateBoundsFromPtr(fieldPtr, _fieldInfo, entityCoords, _descriptor))
                {
                    continue; // degenerate — skip
                }

                // Unpack entity coordinates based on the archetype's spatial field tier. 2D fields produce [minX, minY, maxX, maxY]; 3D fields produce
                // [minX, minY, minZ, maxX, maxY, maxZ]. The _is3D branch is precomputed at construction so the JIT can hoist it. For 2D entities we leave Z
                // bounds at the query Z range (which is itself infinite for 2D queries) so the Z overlap test trivially passes.
                float eMinX, eMinY, eMinZ, eMaxX, eMaxY, eMaxZ;
                if (_is3D)
                {
                    eMinX = (float)entityCoords[0];
                    eMinY = (float)entityCoords[1];
                    eMinZ = (float)entityCoords[2];
                    eMaxX = (float)entityCoords[3];
                    eMaxY = (float)entityCoords[4];
                    eMaxZ = (float)entityCoords[5];
                }
                else
                {
                    eMinX = (float)entityCoords[0];
                    eMinY = (float)entityCoords[1];
                    eMaxX = (float)entityCoords[2];
                    eMaxY = (float)entityCoords[3];
                    // 2D entity has no Z extent. Set to the query Z range so the Z overlap test always passes for 2D entities (regardless of what the
                    // 2D query's _queryMinZ/_queryMaxZ are — they're typically +/- infinity, but even if they're not, making the entity Z match the query
                    // Z guarantees the test passes).
                    eMinZ = _queryMinZ;
                    eMaxZ = _queryMaxZ;
                }

                // Standard AABB overlap: miss if separated along any axis.
                if (eMaxX < _queryMinX || eMinX > _queryMaxX)
                {
                    continue;
                }

                if (eMaxY < _queryMinY || eMinY > _queryMaxY)
                {
                    continue;
                }

                if (eMaxZ < _queryMinZ || eMinZ > _queryMaxZ)
                {
                    continue;
                }

                // Optional radius filter (issue #230 Phase 3). When active, compute the closest point on the entity's AABB to the sphere center and reject
                // the entity if the squared distance exceeds the sphere's squared radius. "Any-point-in-sphere" semantic matches the legacy
                // SpatialRTree.QueryRadius behavior — a tight entity AABB that just kisses the sphere boundary is accepted. The computed distSq is also
                // carried into the result struct so QueryNearest can sort without re-reading the entity's AABB.
                float distSq = 0f;
                if (_radiusSq > 0f)
                {
                    float dx = _radiusCenterX - System.Math.Clamp(_radiusCenterX, eMinX, eMaxX);
                    float dy = _radiusCenterY - System.Math.Clamp(_radiusCenterY, eMinY, eMaxY);
                    float dz = _radiusCenterZ - System.Math.Clamp(_radiusCenterZ, eMinZ, eMaxZ);
                    distSq = dx * dx + dy * dy + dz * dz;
                    if (distSq > _radiusSq)
                    {
                        continue;
                    }
                }

                long entityId = *(long*)(_currentClusterBase + _state.Layout.EntityIdsOffset + slot * 8);
                _current = new ClusterSpatialQueryResult(entityId, _currentClusterChunkId, slot, distSq);
                return true;
            }

            // 2. Advance to the next cluster in the current cell's broadphase (linear scan).
            if (_currentCellIndex != null && _currentBroadphaseSlot < _currentCellIndex.ClusterCount)
            {
                int idx = _currentBroadphaseSlot++;
                // Category filter. Convention matches the legacy SpatialRTree: a zero categoryMask means "no filter" (accept all). A non-zero categoryMask
                // requires the cluster's union mask to intersect (any overlapping bit is enough). This is intentionally "any bit overlap" rather than the
                // legacy tree's stricter "all bits present". Because category is per-archetype (all entities in a cluster share the same mask), the broadphase
                // filter is exact — no per-entity narrowphase re-filter is needed. Phase 1/2 pre-migration code had this same "any overlap" rule but failed to
                // special-case categoryMask=0 as "no filter"; Phase 3 restores the legacy-compatible zero semantic so callers that pass 0 (e.g. the default
                // SpatialTriggerSystem CategoryMask) accept all clusters.
                if (_categoryMask != 0)
                {
                    uint clusterMask = _currentCellIndex.CategoryMasks[idx];
                    if ((clusterMask & _categoryMask) == 0)
                    {
                        continue; // category miss
                    }
                }

                // AABB overlap against the cluster's stored bounds. The broadphase always runs in 3D — 2D clusters have Z bounds left at the Empty sentinel
                // (+inf/-inf), which trivially passes the Z overlap test against any 2D query's infinite Z range.
                float cMinX = _currentCellIndex.MinX[idx];
                float cMinY = _currentCellIndex.MinY[idx];
                float cMinZ = _currentCellIndex.MinZ[idx];
                float cMaxX = _currentCellIndex.MaxX[idx];
                float cMaxY = _currentCellIndex.MaxY[idx];
                float cMaxZ = _currentCellIndex.MaxZ[idx];
                if (cMaxX < _queryMinX || cMinX > _queryMaxX)
                {
                    continue;
                }

                if (cMaxY < _queryMinY || cMinY > _queryMaxY)
                {
                    continue;
                }

                if (cMaxZ < _queryMinZ || cMinZ > _queryMaxZ)
                {
                    continue;
                }

                // Broadphase hit — open the cluster for narrowphase scanning.
                int chunkId = _currentCellIndex.ClusterIds[idx];
                EnsureAccessor();
                _currentClusterBase = _accessor.GetChunkAddress(chunkId);
                _currentClusterChunkId = chunkId;
                _currentOccupancyBits = *(ulong*)_currentClusterBase;
                continue; // next iteration will drain occupancy bits
            }

            // 3. Advance to the next sub-index. Each cell has two sub-indexes: DynamicIndex (visited first) and StaticIndex (visited second). When the
            //    current sub-index is exhausted, try the StaticIndex of the same cell; if that's also exhausted or null, advance to the next cell and
            //    restart with its DynamicIndex. This two-pass walk is how Phase 3 satisfies acceptance criterion 7 ("Static/dynamic split: static clusters
            //    skip fence updates, queries check both").
            _currentCellIndex = null;
            _currentBroadphaseSlot = 0;

            // 3a. If we just finished DynamicIndex for the current cell and haven't yet tried StaticIndex, try it now.
            if (!_currentCellStaticPass && _currentPerCellSlot != null)
            {
                _currentCellStaticPass = true;
                if (_currentPerCellSlot.StaticIndex != null && _currentPerCellSlot.StaticIndex.ClusterCount > 0)
                {
                    _currentCellIndex = _currentPerCellSlot.StaticIndex;
                    continue;
                }
            }

            // 3b. Advance to the next cell and start fresh with its DynamicIndex.
            _currentCellStaticPass = false;
            _currentPerCellSlot = null;
            while (_currentCellY <= _cellMaxY)
            {
                while (_currentCellX <= _cellMaxX)
                {
                    int cellKey = _grid.ComputeCellKey(_currentCellX, _currentCellY);
                    _currentCellX++;
                    if (cellKey < 0 || _state.PerCellIndex == null || cellKey >= _state.PerCellIndex.Length)
                    {
                        continue;
                    }
                    var slot = _state.PerCellIndex[cellKey];
                    if (slot == null)
                    {
                        continue;
                    }
                    // Prefer DynamicIndex if it has entries; otherwise fall through to StaticIndex in the same iteration.
                    if (slot.DynamicIndex != null && slot.DynamicIndex.ClusterCount > 0)
                    {
                        _currentPerCellSlot = slot;
                        _currentCellIndex = slot.DynamicIndex;
                        _currentCellStaticPass = false;
                        break;
                    }
                    if (slot.StaticIndex != null && slot.StaticIndex.ClusterCount > 0)
                    {
                        _currentPerCellSlot = slot;
                        _currentCellIndex = slot.StaticIndex;
                        _currentCellStaticPass = true; // already at Static, no second pass needed for this cell
                        break;
                    }
                }
                if (_currentCellIndex != null)
                {
                    break;
                }
                _currentCellY++;
                _currentCellX = _cellMinX;
            }

            // 4. Done — no more cells with clusters.
            if (_currentCellIndex == null)
            {
                return false;
            }
        }
    }

    /// <summary>
    /// Dispose the narrowphase accessor if one was opened. Called automatically by <c>foreach</c> on a <c>ref struct</c> that implements this method.
    /// </summary>
    public void Dispose()
    {
        if (_accessorCreated)
        {
            _accessor.Dispose();
            _accessorCreated = false;
        }
    }

    /// <summary>Enumerator pattern: a ref struct enumerator is its own source.</summary>
    public AabbClusterEnumerator GetEnumerator() => this;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void EnsureAccessor()
    {
        if (!_accessorCreated)
        {
            _accessor = _state.ClusterSegment.CreateChunkAccessor();
            _accessorCreated = true;
        }
    }
}
