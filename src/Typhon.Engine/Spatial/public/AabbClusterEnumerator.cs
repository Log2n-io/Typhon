using System;
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

    // The query box expressed in the CURRENT cell's frame. Cluster bounds are C15 cell-relative (#872 step 9), so the broadphase compare needs both sides in
    // the same frame. Converted once per CELL rather than once per cluster: the origin is constant across every cluster in a cell, so this is six
    // subtractions per cell instead of six per cluster, on a path whose whole purpose is to reject clusters in bulk.
    private float _cellQueryMinX;
    private float _cellQueryMinY;
    private float _cellQueryMinZ;
    private float _cellQueryMaxX;
    private float _cellQueryMaxY;
    private float _cellQueryMaxZ;
    private readonly uint _categoryMask;

    // Cell range the query AABB covers, inclusive. Clamped to the grid extent by SpatialGrid. A 2D archetype's query passes ±Infinity on Z, which
    // WorldToCellRange saturates to the full depth range — one cell for a flat world, so the Z loop below runs exactly once and costs nothing.
    private readonly int _cellMinX;
    private readonly int _cellMinY;
    private readonly int _cellMinZ;
    private readonly int _cellMaxX;
    private readonly int _cellMaxY;
    private readonly int _cellMaxZ;

    // Cluster-SoA field offset for the spatial field within each cluster, precomputed.
    private readonly int _spatialCompOffset;
    private readonly int _spatialCompSize;
    private readonly int _spatialFieldOffset;
    private readonly SpatialFieldInfo _fieldInfo;

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
    private int _currentCellZ;
    private CellSpatialIndex _currentCellIndex;    // null when we need to advance to the next cell
    private int _currentBroadphaseSlot;            // next index into _currentCellIndex.ClusterIds to scan

    // ── Batched broadphase (SIMD) ──────────────────────────────────────────────────────────────────────────────────
    //
    // The linear scan is what a spatial query actually runs: a cell only gets an R-Tree above CellTreePromoteThreshold
    // clusters, which measured game worlds essentially never reach. It was a scalar loop over six float SoA arrays while
    // the tree's leaf scan had already been vectorised — the optimisation effort aimed at the path that does not run.
    //
    // CellSpatialIndex.MatchBatch tests 64 slots at a time and returns a bit per overlap; this carries the current batch's
    // mask and its base slot, refilling when the mask empties. Sixty-four rather than the whole index because this is an
    // enumerator: the narrowphase runs between yields, so an unbounded match set would need somewhere to live.
    private ulong _scanMask;
    private int _scanMaskBase;
    private int _scanNextBatch;

    /// <summary>Is the current cell half big enough for the batched scan to beat the scalar loop? See <see cref="CellSpatialIndex.SimdScanMinClusters"/>.</summary>
    private bool _useSimdScan;
    private ulong _currentOccupancyBits;           // remaining occupied slots in the current cluster (bits cleared as we iterate)
    private int _currentClusterChunkId;            // chunk id of the cluster currently in narrowphase
    private byte* _currentClusterBase;             // base pointer of that cluster

    // Two-pass per-cell iteration: each cell has a StaticIndex and a DynamicIndex, both optional. Issue #230 Phase 3 activated the Static path. The
    // enumerator visits DynamicIndex first, then StaticIndex, then advances to the next cell. _currentCellStaticPass is true when we've already drained
    // DynamicIndex and are now iterating StaticIndex for the same cell.
    private bool _currentCellStaticPass;
    private PerCellSpatialSlot _currentPerCellSlot;

    // Tree half of the broadphase (#872 step 9). A cell half is served by EITHER _currentCellIndex or this enumerator, never both — see PerCellSpatialSlot.
    private SpatialRTree<TransientStore>.AABBQueryEnumerator _treeEnum;
    private bool _treeActive;

    /// <summary>
    /// True while a cell half is being scanned, through EITHER structure.
    /// </summary>
    /// <remarks>
    /// The cell walk uses this to decide it has found a cell and can stop advancing. Spelling that as <c>_currentCellIndex != null</c> — which it was until
    /// the tree path existed — reads a promoted cell as empty, walks straight past the cell it just started, and returns nothing at all. The query is then
    /// silently empty, and only above the promotion threshold: <c>SQ-01</c>'s worst shape. Caught by
    /// <c>CellTreePromotionTests.PromotedCell_AnswersIdenticallyToTheLinearScan</c>, which is why that test compares against the linear path rather than
    /// against an expected count.
    /// </remarks>
    private readonly bool HasStartedHalf => _currentCellIndex != null || _treeActive;

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

        // Expand the query AABB to the overlapping cell range. Each overlapping cell's per-archetype spatial slot may or may not exist — the iteration
        // handles null slots gracefully. The Z range is narrowed again below for a 2D archetype.
        grid.WorldToCellRange(minX, minY, minZ, maxX, maxY, maxZ, out _cellMinX, out _cellMinY, out _cellMinZ, out _cellMaxX, out _cellMaxY, out _cellMaxZ);

        // ref readonly, not a copy: ClusterSpatialSlot is ~104 bytes, 72 of them the descriptor, and copying it here to read five fields was 104 bytes of
        // memcpy on every query construction (#916 O3). The rest of the codebase already reads it this way.
        ref readonly var ss = ref state.SpatialSlot;
        _spatialCompOffset = state.Layout.ComponentOffset(ss.Slot);
        _spatialCompSize = state.Layout.ComponentSize(ss.Slot);
        _spatialFieldOffset = ss.FieldOffset;
        _fieldInfo = ss.FieldInfo;
        _is3D = ss.FieldInfo.FieldType == SpatialFieldType.AABB3F || ss.FieldInfo.FieldType == SpatialFieldType.BSphere3F;

        // A 2D archetype's query carries ±Infinity on Z, meaning "every Z", which WorldToCellRange saturates to the full depth. Left alone that makes every
        // such query sweep every Z plane of a deep grid, of which exactly one can ever hold a cell: ReadSpatialCenter3D reports posZ = 0 for both 2D field
        // types, so a 2D archetype's entities all live in the plane containing world Z = 0. Collapsing to that plane is not an optimisation of the answer —
        // the other planes are empty by construction. A flat world is already one plane deep, so this only bites a 2D archetype sharing a volumetric grid.
        //
        // The plane itself is a function of the grid config alone, so it is computed once at construction (SpatialGrid.FlatPlaneZ) rather than per query —
        // #916's O2.
        if (!_is3D)
        {
            _cellMinZ = grid.FlatPlaneZ;
            _cellMaxZ = grid.FlatPlaneZ;
        }

        _accessor = default;
        _accessorCreated = false;
        _currentCellX = _cellMinX;
        _currentCellY = _cellMinY;
        _currentCellZ = _cellMinZ;
        _currentCellIndex = null;
        _currentBroadphaseSlot = 0;
        _useSimdScan = false;
        _currentOccupancyBits = 0UL;
        _currentClusterChunkId = 0;
        _currentClusterBase = null;
        _currentCellStaticPass = false;
        _currentPerCellSlot = null;
        _current = default;
    }

    /// <summary>The most recently yielded result. Valid only after <see cref="MoveNext"/> returns <c>true</c>.</summary>
    public ClusterSpatialQueryResult Current => _current;

    /// <summary>
    /// Re-express the query box in <paramref name="cellKey"/>'s frame, so the broadphase can compare it against that cell's <c>C15</c> cell-relative cluster
    /// bounds.
    /// </summary>
    /// <remarks>
    /// <para>The rounding goes OUTWARD on both sides — min down, max up — the opposite of what a stored bound does, and for the same reason. A query box
    /// that narrows by an ULP under the conversion can drop a cluster grazing its edge, and <c>SQ-01</c> counts that as a false negative however small the
    /// margin was. Widening can only ever produce an extra narrowphase visit, which the entity-level test then rejects.</para>
    /// <para>Infinities pass through unchanged: a 2D query uses ±Infinity for Z, and <c>±Infinity - origin</c> is still ±Infinity, so the Z test stays the
    /// trivially-true comparison it is meant to be. It is <c>Infinity - Infinity</c> that would produce a NaN, and neither side of this subtraction is ever
    /// the cluster's sentinel — the origin is a finite world coordinate.</para>
    /// </remarks>
    private void SetCellQueryFrame(int cellKey)
    {
        _grid.CellOrigin(cellKey, out float originX, out float originY, out float originZ);
        _cellQueryMinX = ClusterSpatialAabb.ToCellRelativeMin(_queryMinX, originX);
        _cellQueryMinY = ClusterSpatialAabb.ToCellRelativeMin(_queryMinY, originY);
        _cellQueryMinZ = ClusterSpatialAabb.ToCellRelativeMin(_queryMinZ, originZ);
        _cellQueryMaxX = ClusterSpatialAabb.ToCellRelativeMax(_queryMaxX, originX);
        _cellQueryMaxY = ClusterSpatialAabb.ToCellRelativeMax(_queryMaxY, originY);
        _cellQueryMaxZ = ClusterSpatialAabb.ToCellRelativeMax(_queryMaxZ, originZ);
    }

    /// <summary>
    /// Begin scanning one half of a cell, through whichever structure is serving it. Returns false when that half is empty.
    /// </summary>
    /// <remarks>
    /// <b><see cref="SetCellQueryFrame"/> must already have run for this cell.</b> The tree query box is built from that cell-relative frame, so starting a
    /// half before the frame is established would query the tree with the PREVIOUS cell's coordinates — every bound off by the offset between the two cells,
    /// and the cell answering nothing rather than answering wrongly. That is <c>SQ-01</c>'s silent direction, and it is the same defect the two
    /// <c>SetCellQueryFrame</c> call sites in the cell walk already exist to prevent.
    /// </remarks>
    private bool TryStartCellHalf(PerCellSpatialSlot slot, bool isStatic)
    {
        // Acquire, not a plain field read: PerCellSpatialSlot publishes the tree with a release store precisely so this load pairs with it, and it must
        // come BEFORE the index load below — that order is what makes the promotion window unobservable.
        var tree = slot.ReadTree(isStatic);
        if (tree != null && tree.ClusterCount > 0)
        {
            Span<double> queryCoords = stackalloc double[6];
            CellClusterTree.QueryToCoords(_cellQueryMinX, _cellQueryMinY, _cellQueryMinZ, _cellQueryMaxX, _cellQueryMaxY, _cellQueryMaxZ, queryCoords);

            // categoryMask 0: the filter is applied on the way out instead, because this broadphase and SpatialRTree disagree on what a mask means. See the
            // tree branch in MoveNext.
            _treeEnum = tree.Query(queryCoords, 0);
            _treeActive = true;
            _currentCellIndex = null;
            return true;
        }

        var linear = slot.ReadIndex(isStatic);
        if (linear != null && linear.ClusterCount > 0)
        {
            _currentCellIndex = linear;
            _currentBroadphaseSlot = 0;
            _scanMask = 0;
            _scanMaskBase = 0;
            _scanNextBatch = 0;
            _useSimdScan = SpatialQueryTuning.SimdLinearScan && linear.ClusterCount >= CellSpatialIndex.SimdScanMinClusters;
            return true;
        }

        return false;
    }

    /// <summary>Advance to the next matching entity. Returns <c>false</c> when the query is exhausted.</summary>
    public bool MoveNext()
    {
        // Hoisted stackalloc scratch for narrowphase entity bound reads. Sized for 3D (6 doubles) — 2D reads only populate the first 4 slots, and the
        // unused tail costs nothing. Allocating ONCE per MoveNext call (not per loop iteration) avoids accumulating stack pressure across iterations of the
        // state machine's while(true) — a query that scans thousands of clusters before finding the first match would otherwise allocate 48 bytes per
        // iteration that can't be released until MoveNext returns. See CA2014 for the general guidance.
        Span<double> entityCoords = stackalloc double[6];

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
                // No descriptor argument: this enumerator carried a 72-byte SpatialNodeDescriptor field solely to pass it here, and the callee never read it.
                // Both are gone (#916 O3) — see SpatialMaintainer.ReadAndValidateBoundsFromPtr.
                if (!SpatialMaintainer.ReadAndValidateBoundsFromPtr(fieldPtr, _fieldInfo, entityCoords))
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
                    float dx = _radiusCenterX - Math.Clamp(_radiusCenterX, eMinX, eMaxX);
                    float dy = _radiusCenterY - Math.Clamp(_radiusCenterY, eMinY, eMaxY);
                    float dz = _radiusCenterZ - Math.Clamp(_radiusCenterZ, eMinZ, eMaxZ);
                    distSq = dx * dx + dy * dy + dz * dz;
                    if (distSq > _radiusSq)
                    {
                        continue;
                    }
                }

                long entityId = *(long*)(_currentClusterBase + _state.Layout.EntityIdsOffset + slot * 8);
                _current = new ClusterSpatialQueryResult(entityId, _currentClusterChunkId, slot, eMinX, eMinY, eMinZ, eMaxX, eMaxY, eMaxZ, distSq);
                return true;
            }

            // 2a. Advance to the next cluster via this cell's R-Tree, when it has one.
            if (_treeActive)
            {
                if (_treeEnum.MoveNext())
                {
                    int treeChunkId = (int)_treeEnum.Current.PayloadId;

                    // The category filter is applied HERE rather than handed to the tree, because the two disagree: this broadphase accepts any overlapping
                    // bit and a zero mask means "no filter", while SpatialRTree's own mask test is the legacy stricter one. Passing _categoryMask down would
                    // therefore make a promoted cell answer a different question from an unpromoted one — an SQ-01 false negative that only appears above the
                    // promotion threshold, which is the hardest possible place to notice it. The mask comes from ClusterAabbs, which is the same value the
                    // linear index would have read.
                    if (_categoryMask != 0 && (_state.ClusterAabbs[treeChunkId].CategoryMask & _categoryMask) == 0)
                    {
                        continue;
                    }

                    EnsureAccessor();
                    _currentClusterBase = _accessor.GetChunkAddress(treeChunkId);
                    _currentClusterChunkId = treeChunkId;
                    _currentOccupancyBits = *(ulong*)_currentClusterBase;
                    continue;
                }

                _treeEnum.Dispose();
                _treeActive = false;
                // Fall through to section 3: this half is drained.
            }

            // 2a. Batched broadphase: the AABB test already happened for a whole batch, so this only pops set bits.
            if (_currentCellIndex != null && _useSimdScan && TryNextBatchedSlot(out int batchedIdx))
            {
                if (_categoryMask != 0 && (_currentCellIndex.CategoryMasks[batchedIdx] & _categoryMask) == 0)
                {
                    continue;   // category miss — same "any bit overlap" rule as the scalar branch below
                }

                int batchedChunkId = _currentCellIndex.ClusterIds[batchedIdx];
                EnsureAccessor();
                _currentClusterBase = _accessor.GetChunkAddress(batchedChunkId);
                _currentClusterChunkId = batchedChunkId;
                _currentOccupancyBits = *(ulong*)_currentClusterBase;
                continue;
            }

            // 2. Advance to the next cluster in the current cell's broadphase (linear scan).
            if (_currentCellIndex != null && !_useSimdScan && _currentBroadphaseSlot < _currentCellIndex.ClusterCount)
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
                if (cMaxX < _cellQueryMinX || cMinX > _cellQueryMaxX)
                {
                    continue;
                }

                if (cMaxY < _cellQueryMinY || cMinY > _cellQueryMaxY)
                {
                    continue;
                }

                if (cMaxZ < _cellQueryMinZ || cMinZ > _cellQueryMaxZ)
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
            _useSimdScan = false;

            // 3a. If we just finished DynamicIndex for the current cell and haven't yet tried StaticIndex, try it now.
            if (!_currentCellStaticPass && _currentPerCellSlot != null)
            {
                _currentCellStaticPass = true;
                if (TryStartCellHalf(_currentPerCellSlot, isStatic: true))
                {
                    continue;
                }
            }

            // 3b. Advance to the next cell and start fresh with its DynamicIndex.
            _currentCellStaticPass = false;
            _currentPerCellSlot = null;
            while (_currentCellZ <= _cellMaxZ)
            {
                while (_currentCellY <= _cellMaxY)
                {
                    while (_currentCellX <= _cellMaxX)
                    {
                        // TryGetCellKey, never ComputeCellKey: a query box covers mostly empty space, and resolving-with-create would materialise a cell for
                        // every coordinate it sweeps — turning the broadphase into the thing that fills a sparse grid in.
                        bool exists = _grid.TryGetCellKey(_currentCellX, _currentCellY, _currentCellZ, out int cellKey);
                        _currentCellX++;
                        if (!exists || _state.PerCellIndex == null || cellKey >= _state.PerCellIndex.Length)
                        {
                            continue;
                        }
                        var slot = _state.PerCellIndex[cellKey];
                        if (slot == null)
                        {
                            continue;
                        }
                        // Prefer the dynamic half if it holds anything; otherwise fall through to the static half in the same iteration.
                        if (slot.DynamicClusterCount > 0)
                        {
                            // Established only once a cell is known to HOLD something. A query box sweeps mostly-empty space — that is why the walk two
                            // lines above uses TryGetCellKey rather than ComputeCellKey — so converting the frame before that is proven would pay an
                            // origin chase and six conversions for every empty cell the box crosses. It must also precede TryStartCellHalf, which builds
                            // the tree query box out of the frame.
                            SetCellQueryFrame(cellKey);
                            _currentPerCellSlot = slot;
                            _currentCellStaticPass = false;
                            TryStartCellHalf(slot, isStatic: false);
                            break;
                        }
                        if (slot.StaticClusterCount > 0)
                        {
                            // BOTH break paths must establish the frame. Setting it only on the Dynamic one leaves a cell whose Dynamic index is empty
                            // reading its clusters against the PREVIOUS cell's origin — every bound off by the offset between the two, and every Static-only
                            // cell silently answering nothing. Three Release-suite tests caught exactly that.
                            SetCellQueryFrame(cellKey);
                            _currentPerCellSlot = slot;
                            _currentCellStaticPass = true; // already at Static, no second pass needed for this cell
                            TryStartCellHalf(slot, isStatic: true);
                            break;
                        }
                    }
                    // A started half is EITHER a linear index or a tree, so "did we find a cell" cannot be spelled as "_currentCellIndex != null" — that
                    // reads a promoted cell as empty, walks straight past it, and the query returns nothing at all. Silent, and only above the threshold.
                    if (HasStartedHalf)
                    {
                        break;
                    }
                    _currentCellY++;
                    _currentCellX = _cellMinX;
                }
                if (HasStartedHalf)
                {
                    break;
                }
                _currentCellZ++;
                _currentCellY = _cellMinY;
                _currentCellX = _cellMinX;
            }

            // 4. Done — no more cells with clusters.
            if (!HasStartedHalf)
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
        // The tree enumerator holds its OWN ChunkAccessor over the transient segment and an open telemetry span, and it is only drained naturally when a query
        // runs to completion. A caller that breaks out of the foreach mid-cell — a "first hit wins" search, an exception — would otherwise leak both. The
        // pre-tree enumerator had no second resource, so this line has no ancestor to have been copied from.
        if (_treeActive)
        {
            _treeEnum.Dispose();
            _treeActive = false;
        }

        if (_accessorCreated)
        {
            _accessor.Dispose();
            _accessorCreated = false;
        }
    }

    /// <summary>Enumerator pattern: a ref struct enumerator is its own source.</summary>
    public AabbClusterEnumerator GetEnumerator() => this;

    /// <summary>
    /// Next slot whose cluster AABB overlaps the query box, from the batched match mask, refilling it as needed.
    /// </summary>
    /// <remarks>
    /// The Z axis is tested like any other. A 2D cluster leaves Z at the <c>+∞ / −∞</c> sentinel and a 2D query passes
    /// <c>±∞</c> bounds, and both comparisons come out true on those values — so the axis costs two vector compares and
    /// needs no special case, exactly as in the scalar loop it replaces.
    /// </remarks>
    private bool TryNextBatchedSlot(out int slot)
    {
        while (_scanMask == 0)
        {
            if (_scanNextBatch >= _currentCellIndex.ClusterCount)
            {
                slot = -1;
                return false;
            }

            _scanMaskBase = _scanNextBatch;
            _scanMask = _currentCellIndex.MatchBatch(
                _scanMaskBase,
                _cellQueryMinX, _cellQueryMinY, _cellQueryMinZ,
                _cellQueryMaxX, _cellQueryMaxY, _cellQueryMaxZ,
                testZ: true);
            _scanNextBatch += 64;
        }

        slot = _scanMaskBase + System.Numerics.BitOperations.TrailingZeroCount(_scanMask);
        _scanMask &= _scanMask - 1;
        return true;
    }

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
