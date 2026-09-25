using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Threading;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Internals;

/// <summary>
/// A radius query per member of a small set of spheres, walking the cells they share once: <see cref="ClusterSpatialQuery{TArch}.CountRadius"/> and
/// <see cref="ClusterSpatialQuery{TArch}.ForEachInRadius{TSink}"/>. Each member is answered exactly as its own
/// <see cref="ClusterSpatialQuery{TArch}.Radius(in BSphere2F, uint)"/> query answers it (SQ-03).
/// </summary>
/// <remarks>
/// <para><b>Why.</b> The members of one source cluster sit within a cell or two of each other, so their queries walk nearly the same cells, scan the same
/// cell halves and open the same target clusters. Counted on SWG Tatooine at x64 (engine-perf report 2026-09-13, E-J): a batch of one Player cluster's
/// ~20 players opens 6–10 % of the clusters its members open one by one, and visits 6–8 % of their cell halves, while running exactly as many entity
/// tests.</para>
/// <para><b>What is shared, and what is not.</b> The narrowphase is the single query's own (<see cref="AabbClusterEnumerator.DrainCluster{TSink}"/>, with
/// the AABB2F block kernel through <see cref="AabbClusterEnumerator.ApplyBlockKernel"/>), run once per member for each cluster the member needs: the
/// batch shares the walk, the broadphase scan, the page lookup and the window, never the entity test. The walk is this type's own, and three things hold
/// it to each member's single walk:</para>
/// <list type="bullet">
/// <item>A cell is visited for the members whose OWN cell range — the single query's, widened by <c>ClusterReach</c> — holds it, and only those.</item>
/// <item>A cluster is opened for the members whose own cell-frame box overlaps its index box, by the single query's broadphase predicate. One that no
/// member needs is not opened at all.</item>
/// <item>The named outliers come after the walk, per member, with the single query's tests.</item>
/// </list>
/// <para>Cells are visited row by row as the single walk visits them, and each member's cells are a sub-rectangle of that order, so a member's hits come
/// in its own query's order. A promoted half is queried once per member: its tree answers one box at a time, and trees are opt-in.</para>
/// <para><b>Gaps are skipped.</b> A member far from the others (an entity teleported and not yet migrated) makes the union of the ranges a wide
/// rectangle mostly outside every member's own range. The walk jumps over rows and runs of cells no member holds instead of stepping through them.</para>
/// <para><b>One window for the batch</b> (SQ-05), rented on the first cluster opened and handed back in a <c>finally</c>, so a sink that throws does not
/// keep it. A sink may run its own queries: a nested query rents another window.</para>
/// </remarks>
internal static unsafe class ClusterRadiusBatch
{
    /// <summary>Members per call: every member set is one <see cref="ulong"/>. A source cluster holds at most 64 entities.</summary>
    internal const int MaxMembers = 64;

    /// <summary>
    /// What a batch does with one opened cluster for one member. Returns false to retire the member: it takes no further clusters.
    /// </summary>
    internal interface IMemberDrain
    {
        bool DrainMember(int member, in ClusterFieldLayout layout, in QueryGeometry query, byte* clusterBase, int chunkId, ulong occupancy);

        /// <summary>The matches taken over the whole batch, the one a sink threw on included: the batch's hits in its tally (SO-02).</summary>
        int Hits { get; }
    }

    /// <summary><see cref="ClusterSpatialQuery{TArch}.CountRadius"/>: each member's count, in <see cref="AabbClusterEnumerator.Count"/>'s shape.</summary>
    internal ref struct CountDrain : IMemberDrain
    {
        private readonly Span<int> _counts;
        private int _hits;

        public CountDrain(Span<int> counts)
        {
            _counts = counts;
            _hits = 0;
        }

        public readonly int Hits => _hits;

        public bool DrainMember(int member, in ClusterFieldLayout layout, in QueryGeometry query, byte* clusterBase, int chunkId, ulong occupancy)
        {
            // The slots the kernel approves are counted, not walked; the loop takes the rest — Count()'s own split, at its threshold.
            var bits = occupancy;
            var approved = 0UL;
            if (layout.Aabb2FBlocks != 0)
            {
                bits = AabbClusterEnumerator.ApplyBlockKernel(in layout, in query, clusterBase, bits, 2, out approved);
            }

            var sink = new AabbClusterEnumerator.CountSink();
            var undecided = bits & ~approved;
            if (undecided != 0UL)
            {
                AabbClusterEnumerator.DrainCluster(in layout, in query, clusterBase, chunkId, undecided, ref sink);
            }

            var hits = BitOperations.PopCount(approved) + sink.Count;
            _counts[member] += hits;
            _hits += hits;
            return true;
        }
    }

    /// <summary>
    /// <see cref="ClusterSpatialQuery{TArch}.ForEachInRadius{TSink}"/>: each hit to the caller's sink, in <see cref="AabbClusterEnumerator.MoveNext"/>'s
    /// shape — the kernel's approved slots walked through the loop, so a result's bounds come from the read that tested them.
    /// </summary>
    /// <remarks>Holds the caller's sink by value, and is itself the drain's sink: a ref field cannot refer to a sink that is a ref struct.</remarks>
    internal ref struct SinkDrain<TSink> : IMemberDrain, AabbClusterEnumerator.IHitSink where TSink : struct, IRadiusBatchSink, allows ref struct
    {
        public TSink Sink;
        private int _member;
        private bool _retired;
        private int _hits;

        public SinkDrain(TSink sink)
        {
            Sink = sink;
            _member = 0;
            _retired = false;
            _hits = 0;
        }

        public readonly int Hits => _hits;

        public bool DrainMember(int member, in ClusterFieldLayout layout, in QueryGeometry query, byte* clusterBase, int chunkId, ulong occupancy)
        {
            var bits = occupancy;
            if (layout.Aabb2FBlocks != 0)
            {
                bits = AabbClusterEnumerator.ApplyBlockKernel(in layout, in query, clusterBase, bits, 3, out _);
            }

            _member = member;
            _retired = false;
            AabbClusterEnumerator.DrainCluster(in layout, in query, clusterBase, chunkId, bits, ref this);
            return !_retired;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool Hit(byte* clusterBase, int chunkId, int slot, int idsOffset, double minX, double minY, double minZ, double maxX, double maxY,
            double maxZ, double distSq)
        {
            // Before the sink: a hit it throws on was delivered, as the single query's MoveNext counts the hit whose caller then throws.
            _hits++;
            var result = AabbClusterEnumerator.ResultAt(clusterBase, chunkId, slot, idsOffset, minX, minY, minZ, maxX, maxY, maxZ, distSq);
            if (Sink.Hit(_member, in result))
            {
                return true;
            }

            _retired = true;
            return false;
        }
    }

    /// <summary>Run the batch for a 2D archetype (<c>Tier2F</c>): the caller has checked the tier and the member count.</summary>
    internal static void Run<TDrain>(ArchetypeClusterState state, SpatialGrid grid, ReadOnlySpan<BSphere2F> members, uint categoryMask, ref TDrain drain)
        where TDrain : struct, IMemberDrain, allows ref struct
    {
        if (members.IsEmpty)
        {
            return;
        }

        var walk = new Walk(state, grid, members, categoryMask);
        try
        {
            walk.Cells(ref drain);
            walk.Escaped(ref drain);
        }
        finally
        {
            walk.ReleaseRent(drain.Hits);
        }
    }

    [InlineArray(MaxMembers)]
    private struct QueryBuffer
    {
        private QueryGeometry _element;
    }

    [InlineArray(MaxMembers * 4)]
    private struct IntBuffer
    {
        private int _element;
    }

    [InlineArray(MaxMembers * 4)]
    private struct FloatBuffer
    {
        private float _element;
    }

    /// <summary>The batch's walk and its state: one local of <see cref="Run{TDrain}"/>, never copied.</summary>
    /// <remarks>
    /// Not a ref struct holding stack spans, on purpose: a walk method takes the drain by reference, and a drain may be a ref struct (it holds the caller's
    /// span or sink), so the compiler would have to assume the walk's spans could be stored into it. The member arrays are inline buffers of this struct
    /// instead, sized for <see cref="MaxMembers"/> — about 7 KB of stack, zeroed once per batch.
    /// </remarks>
    private struct Walk
    {
        private readonly ArchetypeClusterState _state;
        private readonly SpatialGrid _grid;
        // The queried realm's per-cell state (Realms SP-3), resolved once from the grid.
        private readonly RealmArchetypeSpatial _rs;
        private readonly ClusterFieldLayout _layout;
        private readonly uint _categoryMask;
        private readonly EscapedClusterSet _escaped;
        private readonly int _z;

        // Per member j: its query (world f64); its cell range at [4j..4j+3] (x0, y0, x1, y1, the single query's); and its box in the current cell's frame at
        // [4j..4j+3] (minX, minY, maxX, maxY) — what SetCellQueryFrame computes for its own query.
        private readonly QueryBuffer _queries;
        private readonly IntBuffer _ranges;
        private FloatBuffer _frames;

        // The members still taking hits. A sink retires a member by returning false.
        private ulong _active;

        // The rows the members' cell ranges span; the columns are found per row.
        private readonly int _y0;
        private readonly int _y1;

        // This batch's window over the cluster segment, rented on the first cluster opened (SQ-05).
        private SpatialQueryAccessorCache.Entry _entry;
        private int _token;

        // The batch's tally, member by member as each member's own query would count it (SO-02); added once, when the window goes back, with the drain's
        // hits.
        private int _tallyClusters;
        private int _tallyCandidates;

        public Walk(ArchetypeClusterState state, SpatialGrid grid, ReadOnlySpan<BSphere2F> members, uint categoryMask)
        {
            // Left unzeroed: every read of a member's slot is gated by a mask of members that have been written, so zeroing ~7 KB per batch buys nothing.
            Unsafe.SkipInit(out _queries);
            Unsafe.SkipInit(out _ranges);
            Unsafe.SkipInit(out _frames);
            _state = state;
            _grid = grid;
            _layout = new ClusterFieldLayout(state);
            _categoryMask = categoryMask;
            _entry = null;
            _token = 0;
            _tallyClusters = 0;
            _tallyCandidates = 0;
            _z = grid.FlatPlaneZ;

            // Read once, as a single query reads them at construction: every member walks with one reach and one set of names.
            _rs = state.SpatialOf(grid);
            var reach = (double)Volatile.Read(ref _rs.ClusterReach);
            _escaped = Volatile.Read(ref _rs.EscapedClusters);

            int uy0 = int.MaxValue, uy1 = int.MinValue;
            for (int j = 0; j < members.Length; j++)
            {
                // ArchetypeClusterState.QueryRadius's arithmetic, operand for operand: the member's box, radius and range must be its single query's.
                double cx = members[j].CenterX, cy = members[j].CenterY, r = members[j].Radius;
                double minX = cx - r, minY = cy - r, maxX = cx + r, maxY = cy + r;
                _queries[j] = new QueryGeometry(minX, minY, double.NegativeInfinity, maxX, maxY, double.PositiveInfinity, r * r, cx, cy, 0d);

                // AabbClusterEnumerator's constructor: the range widened by the reach, the low side stepped one double down. It also rejects a NaN or
                // infinite member exactly as the single query does.
                grid.WorldToCellRange(Math.BitDecrement(minX - reach), Math.BitDecrement(minY - reach), Math.BitDecrement(double.NegativeInfinity - reach),
                    maxX + reach, maxY + reach, double.PositiveInfinity + reach,
                    out var x0, out var y0, out _, out var x1, out var y1, out _);
                int b = j * 4;
                _ranges[b] = x0;
                _ranges[b + 1] = y0;
                _ranges[b + 2] = x1;
                _ranges[b + 3] = y1;
                uy0 = Math.Min(uy0, y0);
                uy1 = Math.Max(uy1, y1);
            }

            (_y0, _y1) = (uy0, uy1);
            _active = members.Length == 64 ? ulong.MaxValue : (1UL << members.Length) - 1;
        }

        /// <summary>
        /// Every cell of the members' ranges once, in the single walk's order — rows of increasing Y, cells of increasing X — for the members whose own range
        /// holds it. Rows and runs of cells no active member holds are jumped over.
        /// </summary>
        public void Cells<TDrain>(ref TDrain drain) where TDrain : struct, IMemberDrain, allows ref struct
        {
            int y = _y0;
            while (y <= _y1 && _active != 0UL)
            {
                // The members whose Y range holds this row, and where the next one starts if none does.
                ulong row = 0UL;
                int rowX0 = int.MaxValue, rowX1 = int.MinValue, nextY = int.MaxValue;
                for (var a = _active; a != 0UL; a &= a - 1)
                {
                    int b = BitOperations.TrailingZeroCount(a) * 4;
                    int y0 = _ranges[b + 1], y1 = _ranges[b + 3];
                    if (y0 <= y && y <= y1)
                    {
                        row |= 1UL << (b / 4);
                        rowX0 = Math.Min(rowX0, _ranges[b]);
                        rowX1 = Math.Max(rowX1, _ranges[b + 2]);
                    }
                    else if (y0 > y && y0 < nextY)
                    {
                        nextY = y0;
                    }
                }

                if (row == 0UL)
                {
                    if (nextY == int.MaxValue)
                    {
                        return;
                    }

                    y = nextY;
                    continue;
                }

                int x = rowX0;
                while (x <= rowX1)
                {
                    ulong cell = 0UL;
                    int nextX = int.MaxValue;
                    for (var a = row & _active; a != 0UL; a &= a - 1)
                    {
                        int j = BitOperations.TrailingZeroCount(a);
                        int x0 = _ranges[j * 4], x1 = _ranges[(j * 4) + 2];
                        if (x0 <= x && x <= x1)
                        {
                            cell |= 1UL << j;
                        }
                        else if (x0 > x && x0 < nextX)
                        {
                            nextX = x0;
                        }
                    }

                    if (cell == 0UL)
                    {
                        if (nextX == int.MaxValue)
                        {
                            break;
                        }

                        x = nextX;
                        continue;
                    }

                    VisitCell(x, y, cell, ref drain);
                    x++;
                }

                y++;
            }
        }

        /// <summary>
        /// One cell, for the members in <paramref name="cell"/>: its dynamic half, then its static half, as the single walk takes them.
        /// </summary>
        private void VisitCell<TDrain>(int x, int y, ulong cell, ref TDrain drain) where TDrain : struct, IMemberDrain, allows ref struct
        {
            // TryGetCellKey, never ComputeCellKey: the walk must not create the cells it sweeps.
            if (!_grid.TryGetCellKey(x, y, _z, out int cellKey))
            {
                return;
            }

            var perCell = _rs.PerCellIndex;
            if (perCell == null || cellKey >= perCell.Length)
            {
                return;
            }

            var slot = perCell[cellKey];
            if (slot == null)
            {
                return;
            }

            bool dynamicHalf = slot.DynamicClusterCount > 0;
            if (!dynamicHalf && slot.StaticClusterCount <= 0)
            {
                return;
            }

            // Each member's box in this cell's frame, rounded outward as SetCellQueryFrame rounds it, and their union for the scan. The union of the
            // rounded boxes is the rounded union — the rounding is monotone — so a cluster any member's test keeps, the union's keeps too. Z is ±Infinity
            // for every member of a 2D query, and stays that in any frame.
            _grid.CellOrigin(cellKey, out double originX, out double originY, out double originZ);
            float uMinX = float.PositiveInfinity, uMinY = float.PositiveInfinity, uMaxX = float.NegativeInfinity, uMaxY = float.NegativeInfinity;
            for (var a = cell; a != 0UL; a &= a - 1)
            {
                int j = BitOperations.TrailingZeroCount(a);
                int b = j * 4;
                ref readonly var q = ref _queries[j];
                _frames[b] = ClusterSpatialAabb.ToCellRelativeMin(q.MinX, originX);
                _frames[b + 1] = ClusterSpatialAabb.ToCellRelativeMin(q.MinY, originY);
                _frames[b + 2] = ClusterSpatialAabb.ToCellRelativeMax(q.MaxX, originX);
                _frames[b + 3] = ClusterSpatialAabb.ToCellRelativeMax(q.MaxY, originY);
                uMinX = MathF.Min(uMinX, _frames[b]);
                uMinY = MathF.Min(uMinY, _frames[b + 1]);
                uMaxX = MathF.Max(uMaxX, _frames[b + 2]);
                uMaxY = MathF.Max(uMaxY, _frames[b + 3]);
            }

            float zMin = ClusterSpatialAabb.ToCellRelativeMin(double.NegativeInfinity, originZ);
            float zMax = ClusterSpatialAabb.ToCellRelativeMax(double.PositiveInfinity, originZ);
            if (dynamicHalf)
            {
                ScanHalf(slot, false, cell, uMinX, uMinY, zMin, uMaxX, uMaxY, zMax, ref drain);
            }

            if ((cell & _active) != 0UL)
            {
                ScanHalf(slot, true, cell, uMinX, uMinY, zMin, uMaxX, uMaxY, zMax, ref drain);
            }
        }

        /// <summary>
        /// One half of a cell, through whichever structure serves it; the linear scan tests the union box, as the single query's scan tests its own.
        /// </summary>
        private void ScanHalf<TDrain>(PerCellSpatialSlot slot, bool isStatic, ulong cell, float uMinX, float uMinY, float zMin, float uMaxX, float uMaxY,
            float zMax, ref TDrain drain) where TDrain : struct, IMemberDrain, allows ref struct
        {
            // The tree first, with an acquire, as TryStartCellHalf reads it: that order is what hides a promotion in progress.
            var tree = slot.ReadTree(isStatic);
            if (tree != null && tree.ClusterCount > 0)
            {
                TreeHalf(tree, cell, zMin, zMax, ref drain);
                return;
            }

            var index = slot.ReadIndex(isStatic);
            if (index == null || index.ClusterCount <= 0)
            {
                return;
            }

            if (SpatialQueryTuning.SimdLinearScan && index.ClusterCount >= CellSpatialIndex.SimdScanMinClusters)
            {
                for (int b = 0; b < index.ClusterCount && (cell & _active) != 0UL; b += 64)
                {
                    var mask = index.MatchBatch(b, uMinX, uMinY, zMin, uMaxX, uMaxY, zMax, testZ: true);
                    while (mask != 0UL)
                    {
                        int idx = b + BitOperations.TrailingZeroCount(mask);
                        mask &= mask - 1;
                        Candidate(index, idx, cell, ref drain);
                    }
                }

                return;
            }

            for (int idx = 0; idx < index.ClusterCount && (cell & _active) != 0UL; idx++)
            {
                if (index.MaxX[idx] < uMinX || index.MinX[idx] > uMaxX || index.MaxY[idx] < uMinY || index.MinY[idx] > uMaxY
                    || index.MaxZ[idx] < zMin || index.MinZ[idx] > zMax)
                {
                    continue;
                }

                Candidate(index, idx, cell, ref drain);
            }
        }

        /// <summary>
        /// A cluster the union box overlaps: the members whose own box overlaps it, by the single query's test, and the cluster opened once for them. None,
        /// and it is not opened.
        /// </summary>
        /// <remarks>
        /// The members' Z test is not repeated: every member's Z range is the union's, so the union scan already answered it for each. The X / Y test is
        /// the single query's scalar form; its batched scan's vector form agrees with it on every bound that is not NaN, and a NaN cluster bound already
        /// answers differently on the single query's two paths.
        /// </remarks>
        private void Candidate<TDrain>(CellSpatialIndex index, int idx, ulong cell, ref TDrain drain) where TDrain : struct, IMemberDrain, allows ref struct
        {
            if (!AabbClusterEnumerator.CategoryAdmits(index.CategoryMasks[idx], _categoryMask))
            {
                return;
            }

            float cMinX = index.MinX[idx], cMinY = index.MinY[idx], cMaxX = index.MaxX[idx], cMaxY = index.MaxY[idx];
            ulong need = 0UL;
            for (var a = cell & _active; a != 0UL; a &= a - 1)
            {
                int j = BitOperations.TrailingZeroCount(a);
                int b = j * 4;
                if (!(cMaxX < _frames[b] || cMinX > _frames[b + 2] || cMaxY < _frames[b + 1] || cMinY > _frames[b + 3]))
                {
                    need |= 1UL << j;
                }
            }

            if (need != 0UL)
            {
                Open(index.ClusterIds[idx], need, ref drain);
            }
        }

        /// <summary>
        /// A promoted half, member by member: each member's own tree query, its hits in the tree's order, each cluster opened for that member alone.
        /// </summary>
        private void TreeHalf<TDrain>(CellClusterTree tree, ulong cell, float zMin, float zMax, ref TDrain drain)
            where TDrain : struct, IMemberDrain, allows ref struct
        {
            Span<double> coords = stackalloc double[6];
            for (var a = cell & _active; a != 0UL; a &= a - 1)
            {
                int j = BitOperations.TrailingZeroCount(a);
                int b = j * 4;
                CellClusterTree.QueryToCoords(_frames[b], _frames[b + 1], zMin, _frames[b + 2], _frames[b + 3], zMax, coords);
                int count = CollectTreeHits(tree, coords);
                for (int p = 0; p < count; p++)
                {
                    int chunkId = _entry.TreeHits[p];

                    // The single query's category rule for a tree hit: applied on the way out, from ClusterAabbs (see NextCluster's tree branch).
                    if (!AabbClusterEnumerator.CategoryAdmits(_state.ClusterAabbs[chunkId].CategoryMask, _categoryMask))
                    {
                        continue;
                    }

                    Open(chunkId, 1UL << j, ref drain);
                    if ((_active & (1UL << j)) == 0UL)
                    {
                        break;
                    }
                }
            }
        }

        /// <summary>
        /// A tree query's cluster ids, in the tree's order, into the window's buffer. Rents the window on the first hit, as the single query does.
        /// </summary>
        private int CollectTreeHits(CellClusterTree tree, scoped ReadOnlySpan<double> coords)
        {
            int count = 0;
            var e = tree.Query(coords);
            try
            {
                while (e.MoveNext())
                {
                    EnsureRent();
                    if (count == _entry.TreeHits.Length)
                    {
                        Array.Resize(ref _entry.TreeHits, count * 2);
                    }

                    _entry.TreeHits[count++] = (int)e.Current.PayloadId;
                }
            }
            finally
            {
                e.Dispose();
            }

            return count;
        }

        /// <summary>
        /// The named outliers, after the walk, as each member's single query takes them: those that overlap its box and whose home cell its own range does
        /// not hold (the walk would have opened those), and that still name the cluster they named.
        /// </summary>
        public void Escaped<TDrain>(ref TDrain drain) where TDrain : struct, IMemberDrain, allows ref struct
        {
            var escaped = _escaped;
            if (escaped == null)
            {
                return;
            }

            for (int i = 0; i < escaped.Count && _active != 0UL; i++)
            {
                if (!AabbClusterEnumerator.CategoryAdmits(escaped.CategoryMasks[i], _categoryMask))
                {
                    continue;
                }

                ulong need = 0UL;
                for (var a = _active; a != 0UL; a &= a - 1)
                {
                    int j = BitOperations.TrailingZeroCount(a);
                    int b = j * 4;
                    if (escaped.Reaches(i, in _queries[j], _ranges[b], _ranges[b + 1], _z, _ranges[b + 2], _ranges[b + 3], _z))
                    {
                        need |= 1UL << j;
                    }
                }

                if (need != 0UL && escaped.IsCurrent(i, _state.ClusterCellMap, _state.ClusterRealmMap, _rs.Realm))
                {
                    Open(escaped.ChunkIds[i], need, ref drain);
                }
            }
        }

        /// <summary>Open a cluster once and drain it for each member in <paramref name="need"/>, retiring the members the drain retires.</summary>
        private void Open<TDrain>(int chunkId, ulong need, ref TDrain drain) where TDrain : struct, IMemberDrain, allows ref struct
        {
            EnsureRent();
            byte* clusterBase = _entry.Accessor.GetChunkAddress(chunkId);
            ulong occupancy = *(ulong*)clusterBase;
            if (_layout.RealmKeyColumn >= 0)
            {
                // RM-04, as the single query's cluster open applies it.
                occupancy = ArchetypeClusterState.SlotsInRealm(clusterBase, occupancy, _layout.RealmKeyColumn, _layout.Stride, _rs.Realm.Value);
            }

            // Opened once, counted once per member, with all its occupied slots: each member's own query would have opened it (SO-02).
            if (occupancy == 0UL)
            {
                _tallyClusters += BitOperations.PopCount(need);
                return;
            }

            var slots = BitOperations.PopCount(occupancy);
            for (var a = need; a != 0UL; a &= a - 1)
            {
                int j = BitOperations.TrailingZeroCount(a);

                // Before the drain, as the member's own query counts a cluster when it opens it: a sink that throws leaves the tally at the members reached.
                _tallyClusters++;
                _tallyCandidates += slots;
                if (!drain.DrainMember(j, in _layout, in _queries[j], clusterBase, chunkId, occupancy))
                {
                    _active &= ~(1UL << j);
                }
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void EnsureRent()
        {
            if (_entry == null)
            {
                Rent();
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void Rent() => _entry = SpatialQueryAccessorCache.Instance.Rent(_state.ClusterSegment, out _token);

        /// <summary>
        /// Hand the window back and add the batch's tally, with <paramref name="hits"/> the drain took, once (SO-02). Runs in <see cref="Run{TDrain}"/>'s
        /// finally, so a batch whose sink throws adds what its members' own queries would have up to that hit.
        /// </summary>
        public void ReleaseRent(int hits)
        {
            var entry = _entry;
            if (entry != null)
            {
                // A promoted half rents on its first tree hit, before the category filter: a batch can hold the window having opened nothing.
                if (SpatialQueryAccessorCache.Return(entry, _token) && _tallyClusters != 0)
                {
                    _state.RecordQueryTally(entry.ThreadId, _tallyClusters, _tallyCandidates, hits);
                }

                _entry = null;
            }
        }
    }
}
