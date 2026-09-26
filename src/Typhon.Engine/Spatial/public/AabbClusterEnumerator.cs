using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Threading;
using Typhon.Schema.Definition;

namespace Typhon.Engine;

/// <summary>
/// Zero-allocation AABB query enumerator over the per-cell cluster spatial index of a single archetype (issue #230 Phase 3). Shared between the
/// game-facing generic entry point <see cref="ClusterSpatialQuery{TArch}.AABB{TBox}"/> and the engine-facing non-generic entry point
/// <see cref="ArchetypeClusterState.QueryAabb"/>. Handles all four cluster storage tiers — 2D and 3D, f32 and f64 — through a single state machine: the 3D
/// overlap test is a strict superset of the 2D test, and 2D archetypes are queried with an infinite Z range that trivially passes the Z component of the
/// overlap check.
/// </summary>
/// <remarks>
/// <para>
/// The state machine is three-phase per call to <see cref="MoveNext"/>:
/// (1) drain the current cluster's occupancy bits and test each entity's tight bounds against the query AABB (narrowphase),
/// (2) advance to the next cluster in the current cell's <see cref="CellSpatialIndex"/> and apply broadphase AABB + category mask filtering,
/// (3) advance to the next cell in the query's overlap range and look up the archetype's per-cell slot.
/// </para>
/// <para>
/// <b>Tier handling.</b> The narrowphase runs one loop per <see cref="SpatialFieldInfo.FieldType"/>, through a bounds reader per field type that decodes
/// exactly as <see cref="SpatialMaintainer.ReadAndValidateBoundsFromPtr"/> does and widens to doubles — 2D types leave Z to the query's range, 3D types
/// carry their own. Widening an f32 bound is exact, and the comparison stays in f64 because the query box is f64 (#914, SQ-06). The storage layer
/// (<see cref="ClusterSpatialAabb"/> / <see cref="CellSpatialIndex"/>) stays unified 6-<b>float</b> CELL-RELATIVE storage per <c>C15</c>, so the broadphase
/// overlap test always runs in 3D f32 and implicitly handles 2D via infinite Z sentinels.
/// </para>
/// <para>
/// <b>Epoch scope.</b> The caller must be inside an <see cref="EpochGuard"/> scope. The narrowphase reads entity bounds through this thread's warm
/// <see cref="ChunkAccessor{TStore}"/> over the cluster segment, rented from <see cref="SpatialQueryAccessorCache"/> when the first cluster is opened (or a
/// promoted half's tree yields its first hit) and handed back when the query is exhausted or disposed: a query that opens no cluster rents nothing, and
/// consecutive queries on a thread keep their page window.
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
    // The queried realm's per-cell state (Realms SP-3), resolved once from the grid: each realm owns its grid.
    private readonly RealmArchetypeSpatial _rs;

    // The query's box, and its sphere for a radius query, in WORLD f64 (#914, SQ-06) — see QueryGeometry. For a 2D query the caller passes ±Infinity on Z,
    // so the Z overlap test trivially passes against 2D cluster storage. The caller builds a radius query's box as the sphere's enclosing box, so the cell
    // expansion and the cluster overlap cover every candidate; RadiusSq 0 means a pure box query.
    private readonly QueryGeometry _query;

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

    // Where the spatial field sits in a cluster and how the narrowphase reads it, including whether the AABB2F block kernel applies — see ClusterFieldLayout.
    private readonly ClusterFieldLayout _layout;

    // The current cluster's block decision (DecideBlocks): whether the kernel has run for it, and which slots of _currentOccupancyBits it approved.
    private bool _blocksDecided;
    private ulong _decidedHits;

    // Is the cluster's spatial field 3D? Decides the cell walk's Z range; the narrowphase takes its dimensionality from its tier reader.
    // ReSharper disable once PrivateFieldCanBeConvertedToLocalVariable
    private readonly bool _is3D;

    // Narrowphase accessor: this thread's warm window over the cluster segment, rented from SpatialQueryAccessorCache on the first cluster open (or first
    // tree hit) and handed back on exhaustion or Dispose. _warmEntry is null while none is held; _warmToken is what the rent stamped — a copy's second
    // return is ignored, and a copy's use after the return throws.
    private SpatialQueryAccessorCache.Entry _warmEntry;
    private int _warmToken;
    private ref ChunkAccessor<PersistentStore> _warm;

    /// <summary>
    /// When set, <see cref="NextCluster"/> names the next admitted cluster without opening its page.
    /// </summary>
    /// <remarks>
    /// <b>Finding a cluster and opening it are different costs, and a caller that shares clusters between queries only wants the first.</b> Opening is a
    /// page-cache request per cluster; profiled at d06 with a thousand subscription sessions it is most of this enumerator's time, because every one of
    /// about 165 overlapping interest cells re-opens the same clusters — each roughly fifteen times a tick. A caller that opens each cluster once and shares
    /// what it read sets this and calls <see cref="OpenCluster"/> itself, at most once per cluster.
    /// </remarks>
    private bool _noOpen;

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

    // Tree half of the broadphase (#872 step 9). A cell half is served by EITHER _currentCellIndex or its R-Tree, never both — see PerCellSpatialSlot.
    // The tree's overlapping cluster ids are collected when the half starts (CollectTreeHits) into the rented window's TreeHits buffer, then popped in
    // the tree's order; the 824-byte tree enumerator never enters this struct.
    private bool _treeActive;
    private int _treeHitCount;
    private int _treeHitNext;

    // Named outliers (SQ-01): clusters whose box reaches further past their cell than the walk's widening covers. Captured at construction, so the whole
    // enumeration reads one consistent set, and visited by name once the cell walk is done.
    private readonly EscapedClusterSet _escaped;
    private int _escapedNext;

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

    /// <summary>True when this query's narrowphase runs <see cref="NarrowphaseAabb2F"/>'s block kernel. Tests assert it, so a green run is known to have
    /// exercised the kernel rather than the scalar loop.</summary>
    internal readonly bool UsesAabb2FBlocks => _layout.Aabb2FBlocks != 0;

    // Last-yielded result.
    private ClusterSpatialQueryResult _current;

    // This query's tally (SO-02): the clusters it opened, their occupied slots, and its matches. Added to the archetype's once, when the window goes back.
    // Last, after _current, so every earlier field keeps its offset. Declared beside _decidedHits, MoveNext measured +9 % on a 1 024-hit query — but two
    // later builds whose MoveNext compiled to the same bytes also measured 12 % apart, so the profile cannot tell a layout cost from where the code
    // landed. What the tally adds per hit is one increment.
    private int _tallyClusters;
    private int _tallyCandidates;
    private int _tallyHits;

    internal AabbClusterEnumerator(ArchetypeClusterState state, SpatialGrid grid, double minX, double minY, double minZ, double maxX, double maxY,
        double maxZ, uint categoryMask, double radiusSq = 0d, double radiusCenterX = 0d, double radiusCenterY = 0d, double radiusCenterZ = 0d)
    {
        _state = state;
        _grid = grid;
        _query = new QueryGeometry(minX, minY, minZ, maxX, maxY, maxZ, radiusSq, radiusCenterX, radiusCenterY, radiusCenterZ);
        _categoryMask = categoryMask;

        // Expand the query AABB to the overlapping cell range. Each overlapping cell's per-archetype spatial slot may or may not exist — the iteration
        // handles null slots gracefully. The Z range is narrowed again below for a 2D archetype.
        //
        // Widened by ClusterReach: a cluster is filed by its entities' CENTRES, so its box reaches past its own cell, and a cluster overhanging the query
        // from a neighbouring cell would otherwise never be examined — an entity 0.2 inside the box, missed (SQ-01). Only the cell range widens; the
        // broadphase and the narrowphase still test against the query box itself. ±Infinity on a 2D query's Z stays ±Infinity. The few clusters that
        // reach further than the widening are not widened for: they are named in EscapedClusters and opened after the walk (TryOpenEscapedCluster).
        //
        // The low side is stepped down one double below the widened value. A box is a closed interval, so a box ending exactly on a cell boundary still
        // touches a query starting there — and the floor would otherwise map that boundary to the next cell up and skip the box's own cell.
        _rs = state.SpatialOf(grid);
        var overhang = (double)Volatile.Read(ref _rs.ClusterReach);
        _escaped = Volatile.Read(ref _rs.EscapedClusters);
        grid.WorldToCellRange(Math.BitDecrement(minX - overhang), Math.BitDecrement(minY - overhang), Math.BitDecrement(minZ - overhang),
            maxX + overhang, maxY + overhang, maxZ + overhang,
            out _cellMinX, out _cellMinY, out _cellMinZ, out _cellMaxX, out _cellMaxY, out _cellMaxZ);

        _layout = new ClusterFieldLayout(state);
        _is3D = _layout.FieldType.Is3D();

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

        _warmEntry = null;
        _warmToken = 0;
        _currentCellX = _cellMinX;
        _currentCellY = _cellMinY;
        _currentCellZ = _cellMinZ;
        _currentCellIndex = null;
        _currentBroadphaseSlot = 0;
        _useSimdScan = false;
        _currentOccupancyBits = 0UL;
        _blocksDecided = false;
        _decidedHits = 0UL;
        _currentClusterChunkId = 0;
        _currentClusterBase = null;
        _currentCellStaticPass = false;
        _currentPerCellSlot = null;
        _current = default;
        _escapedNext = 0;
        _tallyClusters = 0;
        _tallyCandidates = 0;
        _tallyHits = 0;
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
        _grid.CellOrigin(cellKey, out double originX, out double originY, out double originZ);
        _cellQueryMinX = ClusterSpatialAabb.ToCellRelativeMin(_query.MinX, originX);
        _cellQueryMinY = ClusterSpatialAabb.ToCellRelativeMin(_query.MinY, originY);
        _cellQueryMinZ = ClusterSpatialAabb.ToCellRelativeMin(_query.MinZ, originZ);
        _cellQueryMaxX = ClusterSpatialAabb.ToCellRelativeMax(_query.MaxX, originX);
        _cellQueryMaxY = ClusterSpatialAabb.ToCellRelativeMax(_query.MaxY, originY);
        _cellQueryMaxZ = ClusterSpatialAabb.ToCellRelativeMax(_query.MaxZ, originZ);
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
            CollectTreeHits(tree, queryCoords);
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

    /// <summary>
    /// Run a promoted half's tree query to completion, collecting the overlapping cluster ids in the tree's own order into this query's rented window.
    /// </summary>
    /// <remarks>
    /// The tree enumerator — 824 bytes, holding its own accessor over the transient segment and a telemetry span — lives and dies in this frame. Carried
    /// in the enumerator instead, it was over half of a struct every query zeroes and copies, for a path most worlds never take. The ids go into the rented
    /// window's buffer because SQ-05 already keeps that distinct per live query and token-guarded against a copy's late return.
    /// </remarks>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private void CollectTreeHits(CellClusterTree tree, scoped ReadOnlySpan<double> queryCoords)
    {
        int[] hits = null;
        int count = 0;
        // categoryMask 0: the filter is applied on the way out instead, because this broadphase and SpatialRTree disagree on what a mask means. See the
        // tree branch in NextCluster.
        var e = tree.Query(queryCoords);
        try
        {
            while (e.MoveNext())
            {
                if (hits == null)
                {
                    // Rented on the first hit, not before: a promoted half the query misses takes no window.
                    EnsureAccessor();
                    hits = _warmEntry.TreeHits;
                }

                if (count == hits.Length)
                {
                    Array.Resize(ref _warmEntry.TreeHits, count * 2);
                    hits = _warmEntry.TreeHits;
                }

                hits[count++] = (int)e.Current.PayloadId;
            }
        }
        finally
        {
            e.Dispose();
        }

        _treeHitCount = count;
        _treeHitNext = 0;
    }

    /// <summary>Advance to the next matching entity. Returns <c>false</c> when the query is exhausted.</summary>
    /// <remarks>
    /// One call per hit. A caller that wants the matches in bulk should use <see cref="Count"/> or <see cref="Fill"/>, which run the same state machine
    /// without leaving it between hits.
    /// </remarks>
    public bool MoveNext()
    {
        ThrowIfRentStale();
        var sink = new FirstHitSink();
        while (true)
        {
            // 1. Test the current cluster's remaining occupied slots, up to the first match (narrowphase). Bits are only ever set together with
            //    _currentClusterBase.
            if (_currentOccupancyBits != 0UL)
            {
                DecideBlocks(3);
                _currentOccupancyBits = DrainTier(_currentOccupancyBits, ref sink);
                _decidedHits &= _currentOccupancyBits;
                if (sink.Found)
                {
                    _current = sink.Result;
                    _tallyHits++;
                    return true;
                }
            }

            // 2-3. Open the next cluster the query overlaps.
            if (!NextCluster())
            {
                ReleaseRentAfterDrain();
                return false;
            }
        }
    }

    /// <summary>
    /// Drain the rest of the query and return how many entities matched.
    /// </summary>
    /// <remarks>
    /// The whole state machine runs in this one frame: no call per hit, and no result written. Continues from wherever the enumeration stands, so after
    /// <c>k</c> successful <see cref="MoveNext"/> calls it returns the total minus <c>k</c>. The enumerator is exhausted afterwards; <see cref="Current"/> is
    /// unspecified.
    /// </remarks>
    public int Count()
    {
        ThrowIfRentStale();
        var sink = new CountSink();
        while (true)
        {
            if (_currentOccupancyBits != 0UL)
            {
                // The slots the kernel approved need no bounds here, so they are counted rather than walked; the loop takes the rest.
                DecideBlocks(2);
                sink.Count += BitOperations.PopCount(_decidedHits);
                var undecided = _currentOccupancyBits & ~_decidedHits;
                _currentOccupancyBits = undecided == 0UL ? 0UL : DrainTier(undecided, ref sink);
                _decidedHits = 0UL;
            }

            if (!NextCluster())
            {
                _tallyHits += sink.Count;
                ReleaseRentAfterDrain();
                return sink.Count;
            }
        }
    }

    /// <summary>
    /// Write up to <paramref name="destination"/>.Length further matches, in <see cref="MoveNext"/> order, and return how many were written.
    /// </summary>
    /// <remarks>
    /// Returns 0 once the query is exhausted, or for an empty <paramref name="destination"/>. Resumable: the next call continues where this one stopped,
    /// and calls may be mixed with <see cref="MoveNext"/>. <see cref="Current"/> is unspecified after a call.
    /// </remarks>
    public int Fill(scoped Span<ClusterSpatialQueryResult> destination)
    {
        if (destination.IsEmpty)
        {
            return 0;
        }

        ThrowIfRentStale();
        var sink = new SpanSink(destination);
        while (true)
        {
            if (_currentOccupancyBits != 0UL)
            {
                DecideBlocks(3);
                _currentOccupancyBits = DrainTier(_currentOccupancyBits, ref sink);
                _decidedHits &= _currentOccupancyBits;
                if (sink.Written == destination.Length)
                {
                    _tallyHits += sink.Written;
                    return sink.Written;
                }
            }

            if (!NextCluster())
            {
                _tallyHits += sink.Written;
                ReleaseRentAfterDrain();
                return sink.Written;
            }
        }
    }

    /// <summary>
    /// Write up to <paramref name="destination"/>.Length further CLUSTERS the broadphase admits, and return how many were written.
    /// </summary>
    /// <param name="destination">Where to write them.</param>
    /// <returns>How many were written; 0 once the query is exhausted, or for an empty destination.</returns>
    /// <remarks>
    /// <para>
    /// <b>No entity is read.</b> The broadphase — the per-cell cluster index, its SIMD half, the cell tree and the escaped-cluster tail — runs exactly as
    /// it does for <see cref="MoveNext"/>, and the narrowphase that would split each admitted cluster back into entities does not. For a caller whose unit
    /// of interest is the cluster that is the whole query: measured on the SWG demo at d06 with 200 sessions, the entity-level form reports about 416 000
    /// hits per tick where this reports about 19 000 clusters, and the 54 ns each of those hits costs is what it removes.
    /// </para>
    /// <para>
    /// <b>It abandons whatever the previous call left open.</b> This enumeration answers per cluster, so a cluster is reported once and its remaining slots
    /// are dropped rather than carried; mixing it with <see cref="MoveNext"/> or <see cref="Fill"/> on one enumerator therefore loses the entities of the
    /// cluster in hand. Nothing forbids it, and nothing needs it.
    /// </para>
    /// <para>
    /// <b>The bounds are the cluster's own, read back into world space.</b> They come from the archetype's <c>ClusterAabbs</c> in the frame of the cell
    /// <c>ClusterCellMap</c> files the cluster under, rather than from whichever of the three broadphase branches admitted it — one conversion that is
    /// right for all of them, against three that would each have to be kept right separately.
    /// </para>
    /// </remarks>
    public int FillClusters(scoped Span<ClusterBroadphaseHit> destination)
    {
        if (destination.IsEmpty)
        {
            return 0;
        }

        ThrowIfRentStale();
        var aabbs = Volatile.Read(ref _state.ClusterAabbs);
        var cellMap = Volatile.Read(ref _state.ClusterCellMap);
        var written = 0;
        while (written < destination.Length)
        {
            // Drop the cluster in hand rather than draining it: this enumeration's unit is the cluster.
            _currentOccupancyBits = 0UL;
            _decidedHits = 0UL;
            if (!NextCluster())
            {
                ReleaseRentAfterDrain();
                break;
            }

            var chunkId = _currentClusterChunkId;
            var slots = _currentOccupancyBits;
            if (slots == 0UL)
            {
                // An empty cluster the broadphase still holds bounds for. It names no entity, so it is not a hit.
                continue;
            }

            // An unbounded box for a cluster whose bounds cannot be read is the SAFE direction: the caller's own test then admits it and looks inside,
            // where the truth is. Narrowing on a missing entry would drop entities, which is SQ-01's silent direction.
            var minX = double.NegativeInfinity;
            var minY = double.NegativeInfinity;
            var maxX = double.PositiveInfinity;
            var maxY = double.PositiveInfinity;
            if (aabbs != null && cellMap != null && (uint)chunkId < (uint)aabbs.Length && (uint)chunkId < (uint)cellMap.Length)
            {
                ref readonly var box = ref aabbs[chunkId];
                _grid.CellOrigin(cellMap[chunkId], out var originX, out var originY, out _);
                minX = ClusterSpatialAabb.ToWorldExact(box.MinX, originX);
                minY = ClusterSpatialAabb.ToWorldExact(box.MinY, originY);
                maxX = ClusterSpatialAabb.ToWorldExact(box.MaxX, originX);
                maxY = ClusterSpatialAabb.ToWorldExact(box.MaxY, originY);
            }

            destination[written++] = new ClusterBroadphaseHit(chunkId, slots, minX, minY, maxX, maxY);
        }

        _tallyHits += written;
        return written;
    }

    /// <summary>
    /// Advance to the next cluster the broadphase admits WITHOUT opening its page, reporting its id and world-space bounds.
    /// </summary>
    /// <param name="chunkId">The cluster.</param>
    /// <param name="minX">Bounds minimum X, from the archetype's cluster AABBs — no page is read for it.</param>
    /// <param name="minY">Bounds minimum Y.</param>
    /// <param name="maxX">Bounds maximum X.</param>
    /// <param name="maxY">Bounds maximum Y.</param>
    /// <returns><see langword="false"/> once the query is exhausted.</returns>
    /// <remarks>
    /// The occupancy and the entities are not known here — a caller that needs them calls <see cref="OpenCluster"/>, once, and shares the result. An
    /// unbounded box for a cluster whose bounds cannot be read is the safe direction: the caller's own tests then admit it and look inside.
    /// </remarks>
    public bool MoveNextClusterUnopened(out int chunkId, out double minX, out double minY, out double maxX, out double maxY)
    {
        ThrowIfRentStale();
        _noOpen = true;
        _currentOccupancyBits = 0UL;
        _decidedHits = 0UL;
        bool advanced;
        try
        {
            advanced = NextCluster();
        }
        finally
        {
            // Reset whatever happened: a throw inside the walk would otherwise leave every later MoveNext in unopened mode, silently yielding nothing.
            _noOpen = false;
        }

        if (!advanced)
        {
            ReleaseRentAfterDrain();
            chunkId = -1;
            minX = minY = maxX = maxY = 0d;
            return false;
        }

        chunkId = _currentClusterChunkId;
        minX = double.NegativeInfinity;
        minY = double.NegativeInfinity;
        maxX = double.PositiveInfinity;
        maxY = double.PositiveInfinity;
        var aabbs = Volatile.Read(ref _state.ClusterAabbs);
        var cellMap = Volatile.Read(ref _state.ClusterCellMap);
        if (aabbs != null && cellMap != null && (uint)chunkId < (uint)aabbs.Length && (uint)chunkId < (uint)cellMap.Length)
        {
            ref readonly var box = ref aabbs[chunkId];
            _grid.CellOrigin(cellMap[chunkId], out var originX, out var originY, out _);
            minX = ClusterSpatialAabb.ToWorldExact(box.MinX, originX);
            minY = ClusterSpatialAabb.ToWorldExact(box.MinY, originY);
            maxX = ClusterSpatialAabb.ToWorldExact(box.MaxX, originX);
            maxY = ClusterSpatialAabb.ToWorldExact(box.MaxY, originY);
        }

        return true;
    }

    /// <summary>
    /// <see cref="MoveNextClusterUnopened(out int, out double, out double, out double, out double)"/> with the Z bounds too, which the cluster AABBs
    /// already store: what a volumetric caller prunes on. A 2D archetype's Z bounds are whatever its AABBs hold, so its caller supplies its own.
    /// </summary>
    public bool MoveNextClusterUnopened(out int chunkId, out double minX, out double minY, out double minZ, out double maxX, out double maxY,
        out double maxZ)
    {
        ThrowIfRentStale();
        _noOpen = true;
        _currentOccupancyBits = 0UL;
        _decidedHits = 0UL;
        bool advanced;
        try
        {
            advanced = NextCluster();
        }
        finally
        {
            // Reset whatever happened: a throw inside the walk would otherwise leave every later MoveNext in unopened mode, silently yielding nothing.
            _noOpen = false;
        }

        if (!advanced)
        {
            ReleaseRentAfterDrain();
            chunkId = -1;
            minX = minY = minZ = maxX = maxY = maxZ = 0d;
            return false;
        }

        chunkId = _currentClusterChunkId;
        minX = double.NegativeInfinity;
        minY = double.NegativeInfinity;
        minZ = double.NegativeInfinity;
        maxX = double.PositiveInfinity;
        maxY = double.PositiveInfinity;
        maxZ = double.PositiveInfinity;
        var aabbs = Volatile.Read(ref _state.ClusterAabbs);
        var cellMap = Volatile.Read(ref _state.ClusterCellMap);
        if (aabbs != null && cellMap != null && (uint)chunkId < (uint)aabbs.Length && (uint)chunkId < (uint)cellMap.Length)
        {
            ref readonly var box = ref aabbs[chunkId];
            _grid.CellOrigin(cellMap[chunkId], out var originX, out var originY, out var originZ);
            minX = ClusterSpatialAabb.ToWorldExact(box.MinX, originX);
            minY = ClusterSpatialAabb.ToWorldExact(box.MinY, originY);
            maxX = ClusterSpatialAabb.ToWorldExact(box.MaxX, originX);
            maxY = ClusterSpatialAabb.ToWorldExact(box.MaxY, originY);
            minZ = ClusterSpatialAabb.ToWorldExact(box.MinZ, originZ);
            maxZ = ClusterSpatialAabb.ToWorldExact(box.MaxZ, originZ);
        }

        return true;
    }

    /// <summary>
    /// Adds a cluster's occupancy, read by the caller from its own copy, to the query's candidate tally — which the unopened walk cannot count itself and
    /// which feeds the maintenance budget (SO-02).
    /// </summary>
    /// <param name="occupancy">The cluster's occupancy.</param>
    public void TallyOccupancy(ulong occupancy) => _tallyCandidates += BitOperations.PopCount(occupancy);

    /// <summary>Opens a cluster's page through this enumerator's warm accessor and returns its base address.</summary>
    /// <param name="chunkId">The cluster.</param>
    /// <returns>The cluster's base; its first eight bytes are the occupancy word.</returns>
    public byte* OpenCluster(int chunkId)
    {
        ThrowIfRentStale();
        EnsureAccessor();
        return _warm.GetChunkAddress(chunkId);
    }

    /// <summary>Byte offset of the spatial field's column from a cluster's base.</summary>
    public int SpatialFieldsOffset => _layout.FieldsOffset;

    /// <summary>The spatial column's stride.</summary>
    public int SpatialStride => _layout.Stride;

    /// <summary>Whether the spatial field is the flat f32 box a shared snapshot can copy.</summary>
    public bool IsAabb2F => _layout.FieldType == SpatialFieldType.AABB2F;

    /// <summary>
    /// Advance to the next CLUSTER the broadphase admits, reading no entity, and leave it open so the caller may decide whether to drain it.
    /// </summary>
    /// <param name="cluster">The cluster: its chunk id, its occupied slots and its tight bounds in world space.</param>
    /// <returns><see langword="false"/> once the query is exhausted.</returns>
    /// <remarks>
    /// <para>
    /// <b>The caller-driven half of <see cref="FillClusters"/>.</b> That method answers "every cluster, no entities" in one frame, which suits a caller
    /// whose unit of interest is only ever the cluster. This one stops on each cluster so the caller can answer a question about the BOX and then either
    /// walk the entities with <see cref="FillCurrentCluster"/> or move on, paying the narrowphase for the clusters where the box was not decisive and for
    /// no others.
    /// </para>
    /// <para>
    /// <b>Why that split is worth an API.</b> A subscription's broad phase is centred on an interest CELL rather than on any one observer, so a cluster
    /// lying wholly inside the cell's inscribed disc is visible to every member of the cell and one lying outside the enlarged disc to none — in both cases
    /// without a single entity being read or tested. On the SWG demo at d06 that is about 838 clusters reached per cell resolution against 13.2 entities
    /// each, so the decision this exposes is taken 838 times to avoid up to eleven thousand reads.
    /// </para>
    /// <para>
    /// <b>It abandons the entities of the cluster in hand</b>, exactly as <see cref="FillClusters"/> does: advancing is what the caller asked for. The
    /// bounds come from the archetype's <c>ClusterAabbs</c> read back into world space, and a cluster whose bounds cannot be read reports an UNBOUNDED box
    /// so that the caller's own test admits it and looks inside — narrowing on a missing entry would drop entities, which is SQ-01's silent direction.
    /// </para>
    /// </remarks>
    public bool MoveNextCluster(out ClusterBroadphaseHit cluster)
    {
        ThrowIfRentStale();
        var aabbs = Volatile.Read(ref _state.ClusterAabbs);
        var cellMap = Volatile.Read(ref _state.ClusterCellMap);

        while (true)
        {
            // Drop the cluster in hand rather than draining it: advancing by cluster is what this method is.
            _currentOccupancyBits = 0UL;
            _decidedHits = 0UL;
            if (!NextCluster())
            {
                ReleaseRentAfterDrain();
                cluster = default;
                return false;
            }

            var chunkId = _currentClusterChunkId;
            var slots = _currentOccupancyBits;
            if (slots == 0UL)
            {
                // An empty cluster the broadphase still holds bounds for. It names no entity, so it is not a hit.
                continue;
            }

            var minX = double.NegativeInfinity;
            var minY = double.NegativeInfinity;
            var maxX = double.PositiveInfinity;
            var maxY = double.PositiveInfinity;
            if (aabbs != null && cellMap != null && (uint)chunkId < (uint)aabbs.Length && (uint)chunkId < (uint)cellMap.Length)
            {
                ref readonly var box = ref aabbs[chunkId];
                _grid.CellOrigin(cellMap[chunkId], out var originX, out var originY, out _);
                minX = ClusterSpatialAabb.ToWorldExact(box.MinX, originX);
                minY = ClusterSpatialAabb.ToWorldExact(box.MinY, originY);
                maxX = ClusterSpatialAabb.ToWorldExact(box.MaxX, originX);
                maxY = ClusterSpatialAabb.ToWorldExact(box.MaxY, originY);
            }

            _tallyHits++;
            cluster = new ClusterBroadphaseHit(chunkId, slots, minX, minY, maxX, maxY);
            return true;
        }
    }

    /// <summary>
    /// Write up to <paramref name="destination"/>.Length further matches from the CURRENT cluster only, and return how many were written.
    /// </summary>
    /// <param name="destination">Where to write them.</param>
    /// <returns>How many were written; 0 once this cluster is drained.</returns>
    /// <remarks>
    /// <b>Never advances.</b> <see cref="Fill"/> opens the next cluster when the one in hand runs out, which is right for a caller walking the whole query
    /// and wrong for one stepping cluster by cluster with <see cref="MoveNextCluster"/> — there, advancing here would silently skip the box test the caller
    /// stepped in order to make. Resumable against one cluster: call until it returns 0, then step.
    /// </remarks>
    public int FillCurrentCluster(scoped Span<ClusterSpatialQueryResult> destination)
    {
        if (destination.IsEmpty)
        {
            return 0;
        }

        ThrowIfRentStale();
        var sink = new SpanSink(destination);
        while (_currentOccupancyBits != 0UL)
        {
            DecideBlocks(3);
            _currentOccupancyBits = DrainTier(_currentOccupancyBits, ref sink);
            _decidedHits &= _currentOccupancyBits;
            if (sink.Written == destination.Length)
            {
                break;
            }
        }

        _tallyHits += sink.Written;
        return sink.Written;
    }

    // ═══════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════
    // Narrowphase: one drain loop per storage tier
    // ═══════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════
    //
    // The tier is dispatched once per drain call — once per cluster for Count and Fill, once per hit for MoveNext — rather than once per entity; the loop
    // holds the query and the cluster in locals, and a 2D tier does no Z work. Entity bounds are widened to f64 as they always were: exact for the f32
    // tiers, and SQ-06 forbids narrowing the world frame instead. MoveNext, Count and Fill differ only in their sink, so the three cannot disagree on what
    // matches (SQ-03).

    /// <summary>
    /// Reads one entity's tight bounds as f64 — <see cref="SpatialMaintainer.ReadAndValidateBoundsFromPtr"/>'s decode, one struct per field type, so a
    /// drain loop is compiled once per tier with the decode inlined. False for degenerate bounds, as there.
    /// </summary>
    /// <remarks>
    /// The field is copied out of page memory exactly once, into the by-value parameter of <c>Widen</c>, and both the degenerate test and the bounds come
    /// from that copy: reading the page twice would let a concurrent writer slip a value between the check and the use. Internal, so a test can hold each
    /// reader to the reference decode on every field type.
    /// </remarks>
    internal interface IBoundsReader
    {
        static abstract bool Is3D { get; }

        static abstract bool Read(byte* p, out double minX, out double minY, out double minZ, out double maxX, out double maxY, out double maxZ);
    }

    internal readonly struct Aabb2FReader : IBoundsReader
    {
        public static bool Is3D => false;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool Read(byte* p, out double minX, out double minY, out double minZ, out double maxX, out double maxY, out double maxZ) =>
            Widen(*(AABB2F*)p, out minX, out minY, out minZ, out maxX, out maxY, out maxZ);
    }

    internal readonly struct Aabb3FReader : IBoundsReader
    {
        public static bool Is3D => true;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool Read(byte* p, out double minX, out double minY, out double minZ, out double maxX, out double maxY, out double maxZ) =>
            Widen(*(AABB3F*)p, out minX, out minY, out minZ, out maxX, out maxY, out maxZ);
    }

    internal readonly struct BSphere2FReader : IBoundsReader
    {
        public static bool Is3D => false;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool Read(byte* p, out double minX, out double minY, out double minZ, out double maxX, out double maxY, out double maxZ) =>
            Widen(SpatialGeometry.Enclosing(*(BSphere2F*)p), out minX, out minY, out minZ, out maxX, out maxY, out maxZ);
    }

    internal readonly struct BSphere3FReader : IBoundsReader
    {
        public static bool Is3D => true;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool Read(byte* p, out double minX, out double minY, out double minZ, out double maxX, out double maxY, out double maxZ) =>
            Widen(SpatialGeometry.Enclosing(*(BSphere3F*)p), out minX, out minY, out minZ, out maxX, out maxY, out maxZ);
    }

    internal readonly struct Aabb2DReader : IBoundsReader
    {
        public static bool Is3D => false;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool Read(byte* p, out double minX, out double minY, out double minZ, out double maxX, out double maxY, out double maxZ) =>
            Widen(*(AABB2D*)p, out minX, out minY, out minZ, out maxX, out maxY, out maxZ);
    }

    internal readonly struct Aabb3DReader : IBoundsReader
    {
        public static bool Is3D => true;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool Read(byte* p, out double minX, out double minY, out double minZ, out double maxX, out double maxY, out double maxZ) =>
            Widen(*(AABB3D*)p, out minX, out minY, out minZ, out maxX, out maxY, out maxZ);
    }

    internal readonly struct BSphere2DReader : IBoundsReader
    {
        public static bool Is3D => false;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool Read(byte* p, out double minX, out double minY, out double minZ, out double maxX, out double maxY, out double maxZ) =>
            Widen(SpatialGeometry.Enclosing(*(BSphere2D*)p), out minX, out minY, out minZ, out maxX, out maxY, out maxZ);
    }

    internal readonly struct BSphere3DReader : IBoundsReader
    {
        public static bool Is3D => true;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool Read(byte* p, out double minX, out double minY, out double minZ, out double maxX, out double maxY, out double maxZ) =>
            Widen(SpatialGeometry.Enclosing(*(BSphere3D*)p), out minX, out minY, out minZ, out maxX, out maxY, out maxZ);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool Widen(AABB2F b, out double minX, out double minY, out double minZ, out double maxX, out double maxY, out double maxZ)
    {
        (minX, minY, minZ, maxX, maxY, maxZ) = (b.MinX, b.MinY, 0d, b.MaxX, b.MaxY, 0d);
        return !SpatialGeometry.IsDegenerate(b);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool Widen(AABB3F b, out double minX, out double minY, out double minZ, out double maxX, out double maxY, out double maxZ)
    {
        (minX, minY, minZ, maxX, maxY, maxZ) = (b.MinX, b.MinY, b.MinZ, b.MaxX, b.MaxY, b.MaxZ);
        return !SpatialGeometry.IsDegenerate(b);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool Widen(AABB2D b, out double minX, out double minY, out double minZ, out double maxX, out double maxY, out double maxZ)
    {
        (minX, minY, minZ, maxX, maxY, maxZ) = (b.MinX, b.MinY, 0d, b.MaxX, b.MaxY, 0d);
        return !SpatialGeometry.IsDegenerate(b);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool Widen(AABB3D b, out double minX, out double minY, out double minZ, out double maxX, out double maxY, out double maxZ)
    {
        (minX, minY, minZ, maxX, maxY, maxZ) = (b.MinX, b.MinY, b.MinZ, b.MaxX, b.MaxY, b.MaxZ);
        return !SpatialGeometry.IsDegenerate(b);
    }

    /// <summary>What a drain does with each match; returning false stops the drain after it.</summary>
    internal interface IHitSink
    {
        bool Hit(byte* clusterBase, int chunkId, int slot, int idsOffset, double minX, double minY, double minZ, double maxX, double maxY, double maxZ,
            double distSq);
    }

    /// <summary><see cref="Count"/>'s sink: tallies, never stops, and reads nothing past the test.</summary>
    internal struct CountSink : IHitSink
    {
        public int Count;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool Hit(byte* clusterBase, int chunkId, int slot, int idsOffset, double minX, double minY, double minZ, double maxX, double maxY,
            double maxZ, double distSq)
        {
            Count++;
            return true;
        }
    }

    /// <summary><see cref="MoveNext"/>'s sink: keeps the first match and stops.</summary>
    private struct FirstHitSink : IHitSink
    {
        public ClusterSpatialQueryResult Result;
        public bool Found;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool Hit(byte* clusterBase, int chunkId, int slot, int idsOffset, double minX, double minY, double minZ, double maxX, double maxY,
            double maxZ, double distSq)
        {
            Result = ResultAt(clusterBase, chunkId, slot, idsOffset, minX, minY, minZ, maxX, maxY, maxZ, distSq);
            Found = true;
            return false;
        }
    }

    /// <summary><see cref="Fill"/>'s sink: writes each match and stops once the destination is full.</summary>
    private ref struct SpanSink : IHitSink
    {
        private readonly Span<ClusterSpatialQueryResult> _destination;
        public int Written;

        public SpanSink(Span<ClusterSpatialQueryResult> destination) => _destination = destination;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool Hit(byte* clusterBase, int chunkId, int slot, int idsOffset, double minX, double minY, double minZ, double maxX, double maxY,
            double maxZ, double distSq)
        {
            _destination[Written++] = ResultAt(clusterBase, chunkId, slot, idsOffset, minX, minY, minZ, maxX, maxY, maxZ, distSq);
            return Written < _destination.Length;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static ClusterSpatialQueryResult ResultAt(byte* clusterBase, int chunkId, int slot, int idsOffset, double minX, double minY, double minZ,
        double maxX, double maxY, double maxZ, double distSq) =>
        // The slab holds the packed EntityId bit pattern, so the typed wrap happens HERE — once per hit, with the bytes already in a register — rather than
        // at every call site through an internal FromRaw the public API could not reach (#909 part 2).
        new(EntityId.FromRaw(*(long*)(clusterBase + idsOffset + (slot * 8))), chunkId, slot, minX, minY, minZ, maxX, maxY, maxZ, distSq);

    /// <summary>Drain the current cluster's slots in <paramref name="bits"/> into <paramref name="sink"/> through this archetype's tier reader.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private readonly ulong DrainTier<TSink>(ulong bits, scoped ref TSink sink) where TSink : struct, IHitSink, allows ref struct =>
        DrainCluster(in _layout, in _query, _currentClusterBase, _currentClusterChunkId, bits, ref sink);

    /// <summary>
    /// Drain one cluster's slots in <paramref name="bits"/> into <paramref name="sink"/>, through the tier reader for <paramref name="layout"/>'s field type.
    /// The narrowphase every cluster query runs — this enumerator's three drains and <c>ClusterRadiusBatch</c>'s members alike (SQ-03).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static ulong DrainCluster<TSink>(in ClusterFieldLayout layout, in QueryGeometry query, byte* clusterBase, int chunkId, ulong bits,
        scoped ref TSink sink) where TSink : struct, IHitSink, allows ref struct =>
        layout.FieldType switch
        {
            SpatialFieldType.AABB2F => Drain<Aabb2FReader, TSink>(in layout, in query, clusterBase, chunkId, bits, ref sink),
            SpatialFieldType.AABB3F => Drain<Aabb3FReader, TSink>(in layout, in query, clusterBase, chunkId, bits, ref sink),
            SpatialFieldType.BSphere2F => Drain<BSphere2FReader, TSink>(in layout, in query, clusterBase, chunkId, bits, ref sink),
            SpatialFieldType.BSphere3F => Drain<BSphere3FReader, TSink>(in layout, in query, clusterBase, chunkId, bits, ref sink),
            SpatialFieldType.AABB2D => Drain<Aabb2DReader, TSink>(in layout, in query, clusterBase, chunkId, bits, ref sink),
            SpatialFieldType.AABB3D => Drain<Aabb3DReader, TSink>(in layout, in query, clusterBase, chunkId, bits, ref sink),
            SpatialFieldType.BSphere2D => Drain<BSphere2DReader, TSink>(in layout, in query, clusterBase, chunkId, bits, ref sink),
            SpatialFieldType.BSphere3D => Drain<BSphere3DReader, TSink>(in layout, in query, clusterBase, chunkId, bits, ref sink),
            // Nothing decodes an unknown field type (ReadAndValidateBoundsFromPtr's default case), so none of its slots can match.
            _ => 0UL,
        };

    /// <summary>
    /// Run the AABB2F block kernel (<see cref="NarrowphaseAabb2F"/>) over the current cluster once: drop the slots it rejects from
    /// <see cref="_currentOccupancyBits"/> and record the ones it approves in <see cref="_decidedHits"/>. A no-op when the kernel does not apply or has
    /// already run for this cluster.
    /// </summary>
    /// <remarks>
    /// <para><b>Once per cluster, not once per drain call.</b> MoveNext drains one hit per call, and re-running the kernel over the remaining slots on every
    /// call cost 2.3–2.9× the scalar loop (P1 micro-bench). Decided once, the approved slots are all the loop walks afterwards.</para>
    /// <para><b>MoveNext and Fill still take each approved slot through the loop</b>, which tests it again: the result's bounds then come from the read that
    /// tested them, the single-read rule <see cref="IBoundsReader"/> states. Count needs no bounds and takes a popcount.</para>
    /// <para><b>A block with fewer than <paramref name="minSlots"/> occupied slots is left to the loop.</b> A kernel pass costs about 11 ns per block and
    /// the loop about 7 ns per entity (P1 micro-bench), so Count, which only counts what the kernel approves, gains from two slots up; MoveNext and Fill,
    /// which then walk each approved slot through the loop again, only from three. Whichever drain opens a cluster decides it for the others.</para>
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void DecideBlocks(int minSlots)
    {
        if (_layout.Aabb2FBlocks != 0 && !_blocksDecided)
        {
            DecideBlocksCore(minSlots);
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void DecideBlocksCore(int minSlots)
    {
        _blocksDecided = true;
        _currentOccupancyBits = ApplyBlockKernel(in _layout, in _query, _currentClusterBase, _currentOccupancyBits, minSlots, out _decidedHits);
    }

    /// <summary>
    /// The AABB2F block kernel's decision over one cluster: <paramref name="bits"/> with the slots it rejects dropped, and the slots it approves in
    /// <paramref name="approved"/>. Only blocks holding at least <paramref name="minSlots"/> of <paramref name="bits"/> are decided; the rest stay in the
    /// result, undecided, for the loop. The caller has checked that the kernel applies (<see cref="ClusterFieldLayout.Aabb2FBlocks"/>).
    /// </summary>
    internal static ulong ApplyBlockKernel(in ClusterFieldLayout layout, in QueryGeometry query, byte* clusterBase, ulong bits, int minSlots,
        out ulong approved)
    {
        approved = 0UL;

        // Drain's early-out on an inverted Z range is the loop's to take; the kernel does not repeat it, so everything stays undecided.
        if (query.MaxZ < query.MinZ)
        {
            return bits;
        }

        var dense = 0UL;
        for (var g = 0; g < layout.Aabb2FBlocks; g++)
        {
            var block = (bits >> (g * NarrowphaseAabb2F.BlockSize)) & 0xFFFFUL;
            if (BitOperations.PopCount(block) >= minSlots)
            {
                dense |= block << (g * NarrowphaseAabb2F.BlockSize);
            }
        }

        if (dense == 0UL)
        {
            return bits;
        }

        approved = NarrowphaseAabb2F.Match((float*)(clusterBase + layout.FieldsOffset), dense, layout.Aabb2FBlocks, in query);
        return (bits & ~dense) | approved;
    }

    /// <summary>
    /// Make <paramref name="occupancy"/> the current cluster's slots to drain, none of them decided yet, and count the cluster and its occupied slots in the
    /// query's tally — all of them, whatever the drain goes on to test, so the count is the same whether or not the block kernel runs (SO-02).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void OpenOccupancy(ulong occupancy)
    {
        // RM-04: an entity whose realm key names another realm (written this tick, moved at the next fence) is not this query's.
        if (_layout.RealmKeyColumn >= 0)
        {
            occupancy = ArchetypeClusterState.SlotsInRealm(_currentClusterBase, occupancy, _layout.RealmKeyColumn, _layout.RealmKeyStride,
                _rs.Realm.Value);
        }

        _currentOccupancyBits = occupancy;
        _blocksDecided = false;
        _decidedHits = 0UL;
        _tallyClusters++;
        _tallyCandidates += BitOperations.PopCount(occupancy);
    }

    /// <summary>
    /// Test the current cluster's occupied slots in <paramref name="bits"/> against the query and hand each match to <paramref name="sink"/>. Returns the
    /// slots left untested when the sink stops early, 0 once all are done.
    /// </summary>
    /// <remarks>
    /// Everything the loop reads is copied to locals first: the sink stores through a reference, and a store the JIT cannot prove disjoint from this
    /// enumerator would otherwise force every field it reads to be reloaded for each entity.
    /// </remarks>
    internal static ulong Drain<TReader, TSink>(in ClusterFieldLayout layout, in QueryGeometry query, byte* clusterBase, int chunkId, ulong bits,
        scoped ref TSink sink)
        where TReader : struct, IBoundsReader
        where TSink : struct, IHitSink, allows ref struct
    {
        byte* fields = clusterBase + layout.FieldsOffset;
        int stride = layout.Stride;
        int idsOffset = layout.IdsOffset;
        double qMinX = query.MinX, qMinY = query.MinY, qMinZ = query.MinZ, qMaxX = query.MaxX, qMaxY = query.MaxY, qMaxZ = query.MaxZ;
        double radiusSq = query.RadiusSq, cx = query.CenterX, cy = query.CenterY, cz = query.CenterZ;
        bool radius = radiusSq > 0d;

        // A 2D entity takes the query's Z range as its own, so its Z test can only fail on an inverted range — which then rejects the whole cluster. Every
        // shipped caller passes ±Infinity; kept so the tier loop answers exactly what the one-test-per-entity loop did.
        if (!TReader.Is3D && qMaxZ < qMinZ)
        {
            return 0UL;
        }

        while (bits != 0UL)
        {
            int slot = BitOperations.TrailingZeroCount(bits);
            bits &= bits - 1;
            if (!TReader.Read(fields + (slot * stride), out var minX, out var minY, out var minZ, out var maxX, out var maxY, out var maxZ))
            {
                continue; // degenerate — skip
            }

            // Standard AABB overlap: miss if separated along any axis.
            if (maxX < qMinX || minX > qMaxX || maxY < qMinY || minY > qMaxY)
            {
                continue;
            }

            if (TReader.Is3D)
            {
                if (maxZ < qMinZ || minZ > qMaxZ)
                {
                    continue;
                }
            }
            else
            {
                // A 2D entity has no Z extent. Its result carries the query's Z range, as it always has, and that range passes the Z test by definition.
                minZ = qMinZ;
                maxZ = qMaxZ;
            }

            // Optional radius filter (issue #230 Phase 3): the squared distance from the centre to the closest point of the entity's box, "any point in the
            // sphere" as in the legacy SpatialRTree.QueryRadius, so a box that just touches the sphere is accepted. Per axis the distance is
            // max(0, min - c, c - max), without a branch; it squares to exactly what c - Clamp(c, min, max) gave, since a - b and b - a differ only in
            // sign. The value rides in the result, so QueryNearest can sort without re-reading the bounds.
            double distSq = 0d;
            if (radius)
            {
                double dx = double.MaxNative(0d, double.MaxNative(minX - cx, cx - maxX));
                double dy = double.MaxNative(0d, double.MaxNative(minY - cy, cy - maxY));
                distSq = (dx * dx) + (dy * dy);
                if (TReader.Is3D)
                {
                    double dz = double.MaxNative(0d, double.MaxNative(minZ - cz, cz - maxZ));
                    distSq += dz * dz;
                }

                if (distSq > radiusSq)
                {
                    continue;
                }
            }

            if (!sink.Hit(clusterBase, chunkId, slot, idsOffset, minX, minY, minZ, maxX, maxY, maxZ, distSq))
            {
                return bits;
            }
        }

        return 0UL;
    }

    /// <summary>
    /// Open the next cluster the query overlaps — the broadphase and the cell walk — and make it current: base, chunk id, occupancy bits. Returns false
    /// when no cell is left.
    /// </summary>
    /// <remarks>
    /// Out of line so its frame stays out of the drain loops: those run once per entity tested, this once per cluster opened. The accessor is created
    /// lazily on the first cluster, so an empty query never builds one.
    /// </remarks>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private bool NextCluster()
    {
        while (true)
        {
            // 2a. Advance to the next cluster via this cell's R-Tree, when it has one.
            if (_treeActive)
            {
                if (_treeHitNext < _treeHitCount)
                {
                    int treeChunkId = _warmEntry.TreeHits[_treeHitNext++];

                    // The category filter is applied HERE rather than handed to the tree, because the two disagree: this broadphase accepts any overlapping
                    // bit and a zero mask means "no filter", while SpatialRTree's own mask test is the legacy stricter one. Passing _categoryMask down would
                    // therefore make a promoted cell answer a different question from an unpromoted one — an SQ-01 false negative that only appears above the
                    // promotion threshold, which is the hardest possible place to notice it. The mask comes from ClusterAabbs, which is the same value the
                    // linear index would have read.
                    if (!CategoryAdmits(_state.ClusterAabbs[treeChunkId].CategoryMask, _categoryMask))
                    {
                        continue;
                    }

                    if (_noOpen)
                    {
                        _currentClusterChunkId = treeChunkId;
                        _currentOccupancyBits = 0UL;
                        _currentClusterBase = null;
                        _tallyClusters++;
                        return true;
                    }

                    EnsureAccessor();
                    _currentClusterBase = _warm.GetChunkAddress(treeChunkId);
                    _currentClusterChunkId = treeChunkId;
                    OpenOccupancy(Volatile.Read(ref *(ulong*)_currentClusterBase));
                    return true;
                }

                _treeActive = false;
                // Fall through to section 3: this half is drained.
            }

            // 2a. Batched broadphase: the AABB test already happened for a whole batch, so this only pops set bits.
            if (_currentCellIndex != null && _useSimdScan && TryNextBatchedSlot(out int batchedIdx))
            {
                if (!CategoryAdmits(_currentCellIndex.CategoryMasks[batchedIdx], _categoryMask))
                {
                    continue;   // category miss — same "any bit overlap" rule as the scalar branch below
                }

                int batchedChunkId = _currentCellIndex.ClusterIds[batchedIdx];
                if (_noOpen)
                {
                    _currentClusterChunkId = batchedChunkId;
                    _currentOccupancyBits = 0UL;
                    _currentClusterBase = null;
                    _tallyClusters++;
                    return true;
                }

                EnsureAccessor();
                _currentClusterBase = _warm.GetChunkAddress(batchedChunkId);
                _currentClusterChunkId = batchedChunkId;
                OpenOccupancy(Volatile.Read(ref *(ulong*)_currentClusterBase));
                return true;
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
                if (!CategoryAdmits(_currentCellIndex.CategoryMasks[idx], _categoryMask))
                {
                    continue; // category miss
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
                if (_noOpen)
                {
                    _currentClusterChunkId = chunkId;
                    _currentOccupancyBits = 0UL;
                    _currentClusterBase = null;
                    _tallyClusters++;
                    return true;
                }

                EnsureAccessor();
                _currentClusterBase = _warm.GetChunkAddress(chunkId);
                _currentClusterChunkId = chunkId;
                OpenOccupancy(Volatile.Read(ref *(ulong*)_currentClusterBase));
                return true;
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
                        if (!exists || _rs.PerCellIndex == null || cellKey >= _rs.PerCellIndex.Length)
                        {
                            continue;
                        }
                        var slot = _rs.PerCellIndex[cellKey];
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

            // 4. The cell walk is done. Visit the named outliers it did not reach. The drain that asked then hands the window back, once it has added its
            //    matches to the tally: nothing reads the window again, and a query drained to the end without a Dispose — Count() called on the query
            //    itself — must not keep it rented.
            if (!HasStartedHalf)
            {
                return TryOpenEscapedCluster();
            }
        }
    }

    /// <summary>
    /// Open the next named outlier (<see cref="RealmArchetypeSpatial.EscapedClusters"/>) this query overlaps and the cell walk did not reach, making it the
    /// current cluster. Returns false once none is left.
    /// </summary>
    /// <remarks>
    /// An outlier whose home cell lies in the walked range was already opened by the walk and is skipped, so no entity is reported twice; so is one whose
    /// chunk id has since been freed and handed to another cell (<see cref="EscapedClusterSet.IsCurrent"/>). The category rule is the broadphase's: a zero
    /// mask accepts everything, otherwise any overlapping bit.
    /// </remarks>
    private bool TryOpenEscapedCluster()
    {
        var escaped = _escaped;
        while (escaped != null && _escapedNext < escaped.Count)
        {
            int i = _escapedNext++;

            // Cheapest rejection first: most named clusters are nowhere near a given query.
            if (!escaped.Reaches(i, in _query, _cellMinX, _cellMinY, _cellMinZ, _cellMaxX, _cellMaxY, _cellMaxZ)
                || !CategoryAdmits(escaped.CategoryMasks[i], _categoryMask) || !escaped.IsCurrent(i, _state.ClusterCellMap, _state.ClusterRealmMap, _rs.Realm))
            {
                continue;
            }

            int chunkId = escaped.ChunkIds[i];
            if (_noOpen)
            {
                _currentClusterChunkId = chunkId;
                _currentOccupancyBits = 0UL;
                _currentClusterBase = null;
                _tallyClusters++;
                return true;
            }

            EnsureAccessor();
            _currentClusterBase = _warm.GetChunkAddress(chunkId);
            _currentClusterChunkId = chunkId;
            OpenOccupancy(Volatile.Read(ref *(ulong*)_currentClusterBase));
            return true;
        }

        return false;
    }

    /// <summary>
    /// Hand the rented page window back, if one was taken. Called automatically by <c>foreach</c> on a <c>ref struct</c> that implements this method.
    /// </summary>
    /// <remarks>
    /// A promoted half's tree enumerator no longer outlives <see cref="CollectTreeHits"/>, so breaking out of a query mid-cell leaves nothing else to
    /// release.
    /// </remarks>
    public void Dispose()
    {
        ReleaseRent();

        // Leave the enumerator exhausted. Its current cluster's address was valid only while the window pinned it, so a MoveNext after Dispose must find
        // nothing left to drain and no cell left to walk.
        _currentOccupancyBits = 0UL;
        _decidedHits = 0UL;
        _currentClusterBase = null;
        _treeActive = false;
        _currentCellIndex = null;
        _currentPerCellSlot = null;
        _currentCellStaticPass = true;
        _currentCellZ = _cellMaxZ + 1;
        _escapedNext = int.MaxValue;
    }

    /// <summary>
    /// The cluster query's category rule, for every structure it scans and for <c>ClusterRadiusBatch</c> alike: a zero query mask accepts everything,
    /// otherwise any overlapping bit does. Per archetype, so exact at the cluster level.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static bool CategoryAdmits(uint clusterMask, uint queryMask) => queryMask == 0 || (clusterMask & queryMask) != 0;

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

        slot = _scanMaskBase + BitOperations.TrailingZeroCount(_scanMask);
        _scanMask &= _scanMask - 1;
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void EnsureAccessor()
    {
        if (_warmEntry == null)
        {
            RentAccessor();
        }
    }

    /// <summary>
    /// Borrow this thread's warm window over the cluster segment. Out of line so nothing of the rent enters the frames of the per-entity drains: an
    /// accessor built inline left a 448-byte temporary holding managed references in the caller's frame, which a fully interruptible method zeroes in its
    /// prologue on every call.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private void RentAccessor()
    {
        _warmEntry = SpatialQueryAccessorCache.Instance.Rent(_state.ClusterSegment, out _warmToken);
        _warm = ref _warmEntry.Accessor;
    }

    /// <summary>
    /// Hand the window back, if this enumerator holds one, and add the query's tally to its archetype's (SO-02). The token makes a second return — a
    /// copy's — a no-op, and its tally with it: the copy that returned the live rent carried everything both had counted before they split.
    /// </summary>
    /// <remarks>
    /// <para>For <see cref="Dispose"/>, which callers inline: the test and the field stores stay inline, the return and the tally go out of line in a static
    /// method handed values. An instance method taking this enumerator by reference there exposed the struct's address wherever the enumerator was used,
    /// and one built and disposed could no longer be folded away: measured 7.2 → 26.4 ns for construct-and-dispose, with a bulk write barrier and a call in
    /// the loop where there had been neither.</para>
    /// <para>The drains use <see cref="ReleaseRentAfterDrain"/> instead: one compare and a call taking only the enumerator they already hold by reference,
    /// the smallest addition to code that runs once per hit.</para>
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void ReleaseRent()
    {
        var entry = _warmEntry;
        if (entry != null)
        {
            HandBack(entry, _warmToken, _state, _tallyClusters, _tallyCandidates, _tallyHits);
            _warmEntry = null;
            _warm = ref Unsafe.NullRef<ChunkAccessor<PersistentStore>>();
        }
    }

    /// <summary>
    /// <see cref="ReleaseRent"/> for the drains, called once the query is exhausted: one compare inline, and an out-of-line call that takes nothing but
    /// this enumerator, which the drains already hold by reference.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void ReleaseRentAfterDrain()
    {
        if (_warmEntry != null)
        {
            ReleaseRentOutOfLine();
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void ReleaseRentOutOfLine() => ReleaseRent();

    /// <summary>
    /// Return the window (not disposed: it stays warm for this thread's next query) and, if the return was honoured, add the tally. Out of line: once per
    /// query.
    /// </summary>
    /// <remarks>
    /// A promoted half rents on its first tree hit, before the category filter, so a query can hold the window having opened nothing: it tallies nothing.
    /// </remarks>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void HandBack(SpatialQueryAccessorCache.Entry entry, int token, ArchetypeClusterState state, int clusters, int candidates, int hits)
    {
        if (SpatialQueryAccessorCache.Return(entry, token) && clusters != 0)
        {
            state.RecordQueryTally(entry.ThreadId, clusters, candidates, hits);
        }
    }

    /// <summary>
    /// Throw when this enumerator's window has been handed back and it is used again — a copy of it (<c>GetEnumerator()</c> returns one) was disposed or
    /// drained, and the original carried on. The window may belong to another query by then, and its current cluster's address to nothing.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private readonly void ThrowIfRentStale()
    {
        var entry = _warmEntry;
        if (entry != null && entry.Token != _warmToken)
        {
            ThrowHelper.ThrowInvalidOp(
                "This AabbClusterEnumerator's page window was handed back — a copy of the enumerator was disposed or drained — so it can no longer be used.");
        }
    }
}
