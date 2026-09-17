using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace Typhon.Engine.Internals;

/// <summary>
/// One cell's R-Tree over cluster AABBs — the structure <c>C4</c> specifies in place of <see cref="CellSpatialIndex"/>'s linear scan (#872 step 9).
/// </summary>
/// <remarks>
/// <para><b>One <see cref="SpatialRTree{TStore}"/> instance per cell, over a segment shared by every cell of one archetype.</b> The design proposed detaching
/// the tree's metadata from chunk 0 and threading a <c>ref</c> root through twelve methods so a single instance could serve every cell. That was measured and
/// found unnecessary: <c>SharedSegmentRTreeHarnessTests.Claim2</c> drives two ordinary trees over one segment through 120 interleaved inserts, splits both,
/// and each returns exactly its own payloads. The four metadata values are already per-INSTANCE fields; chunk 0 is a write-through mirror nothing reads
/// unless the tree is constructed with <c>load: true</c>, which a transient tree never is.</para>
/// <para><b>That measurement is SEQUENTIAL, and the concurrent case rests on something else.</b> <c>Claim2</c> interleaves the two trees' operations on one
/// thread — it spawns nothing — so it licenses two trees sharing a segment, not two trees mutating it at once. Cell-disjoint Migrate slices do produce the
/// latter: two workers can promote or grow two different cells simultaneously. What makes that safe is <see cref="ChunkBasedSegment{TStore}"/>'s own
/// allocator, which is lock-free with growth serialised on its <c>_growLock</c>, plus the double-check in <c>TryEnsureCellTreeSegment</c> for the creation
/// race. <c>CellTreeDensityTransitionTests</c> is the first thing that drives it from several workers at all.</para>
/// <para><b>The shared chunk 0 was the remaining worry, and it does not apply here.</b> The concern was that every tree on the segment writes chunk 0 on
/// every insert under the tree's own <c>_metadataLock</c>, one contended line per archetype once the fence runs cells in parallel. This type constructs its
/// tree with <c>mirrorMetadata: false</c> (see the constructor below), and <c>SpatialRTree.SyncMetadata</c> returns before taking that lock when the flag is
/// clear — so a cell cluster tree neither writes chunk 0 nor contends for it. The mirror exists for trees that may be reloaded with <c>load: true</c>, which
/// a transient per-cell tree never is.</para>
/// <para><b>Why one segment per cell was rejected, with the number.</b> A <see cref="ChunkBasedSegment{TStore}"/> spans at least two pages since the v4
/// directory-only root (the root page carries the page directory and holds zero chunks), so 16 KiB minimum. At the 128³ / 1 % baseline — 20 971 occupied
/// cells — that is ~328 MiB of mostly-empty segment per spatial archetype, against ~10 MiB for the whole VDB layer.</para>
/// <para><b>Payloads are cluster chunk ids and bounds are <c>C15</c> cell-relative.</b> Both follow from the structure being per-cell per-archetype: a cluster
/// chunk id is only meaningful inside one archetype's <c>ClusterSegment</c> (issue #229 Q10), and a cluster lives wholly inside one cell (<c>C13</c>), which
/// is what makes the frame unambiguous.</para>
/// </remarks>
internal sealed class CellClusterTree
{
    /// <summary>
    /// The variant every cell tree uses. 3D f32 because <c>C16</c> makes 2D a degenerate Z axis rather than a separate code path, and <c>C15</c> rules out
    /// f64 — it would more than halve fan-out (~4 entries per node against 11), attacking the <c>O(log C)</c> the tree exists for.
    /// </summary>
    internal const SpatialVariant Variant = SpatialVariant.R3Df32;

    private readonly SpatialRTree<TransientStore> _tree;
    private readonly ChunkBasedSegment<TransientStore> _segment;
    private int _clusterCount;

    /// <summary>
    /// Leaves left with a MBR wider than the union of their entries by an in-place update, owed a refit before the exclusive window closes (<c>ST-07</c>).
    /// </summary>
    /// <remarks>
    /// Duplicates are allowed and not filtered. A refit is idempotent, so a leaf recorded five times costs four redundant recomputations of eleven entries —
    /// against a <see cref="System.Collections.Generic.HashSet{T}"/> probe on every in-place update, which is the path this whole design exists to keep cheap.
    /// The list is cleared, not reallocated, so steady state allocates nothing.
    /// </remarks>
    private int[] _looseLeaves = new int[16];

    private int _looseLeafCount;

    /// <summary>Chunks the tree freed during the current operation, drained immediately to scrub <see cref="_looseLeaves"/>.</summary>
    private readonly List<int> _freedChunks = [];

    /// <summary>Number of clusters currently indexed by this cell's tree.</summary>
    internal int ClusterCount => _clusterCount;

    /// <summary>The tree, for the differential harness and the validator. Not part of the index contract.</summary>
    /// <remarks>
    /// That sentence was false between #872 step 9 and #900: the ray and frustum query paths reached <c>SpatialRTree</c>'s masked overloads through here,
    /// so the accessor was load-bearing for two production queries while documenting itself as a test seam. They now go through
    /// <see cref="QueryRay"/> and <see cref="QueryFrustum"/>, which take no mask — keep it that way, or SQ-02's structural half lapses back into a
    /// convention. Nothing in <c>src/</c> reads this property.
    /// </remarks>
    internal SpatialRTree<TransientStore> Tree => _tree;

    internal CellClusterTree(ChunkBasedSegment<TransientStore> segment, int[] payloadBackPointers)
    {
        _segment = segment;
        _tree = new SpatialRTree<TransientStore>(segment, Variant, mirrorMetadata: false)
        {
            PayloadBackPointers = payloadBackPointers,
            FreedChunkSink = _freedChunks,
        };
    }

    /// <summary>
    /// Re-point the tree at the archetype's back-pointer array. Called before every mutation because the owner grows that array by reallocation, which leaves
    /// the tree holding the abandoned one.
    /// </summary>
    /// <remarks>
    /// One reference store on a path that is already allocating chunks — cheaper than the alternatives, which are to make the array non-growable or to have
    /// the owner walk every cell's tree on each resize. See <see cref="SpatialRTree{TStore}.PayloadBackPointers"/> for why a stale reference here is a silent
    /// stale handle rather than a lost <c>-1</c>.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void RebindBackPointers(int[] payloadBackPointers) => _tree.PayloadBackPointers = payloadBackPointers;

    /// <summary>
    /// Insert a cluster. The handle is recorded in the shared back-pointer array, which is the only place it is kept.
    /// </summary>
    /// <remarks>
    /// <b>The caller must not keep a copy of the handle.</b> Any entry in this tree can be relocated by a mutation belonging to a DIFFERENT cluster — a leaf
    /// split scatters both halves through the overlap-minimising permutation, and a removal swaps the last entry into the freed slot — so a privately-held
    /// handle goes stale without its owner touching anything. The back-pointer array is the one store the tree repairs on every such move; reading it is
    /// one indexed load, which is cheaper than any scheme for keeping a second copy honest.
    /// </remarks>
    internal void Add(int clusterChunkId, in ClusterSpatialAabb aabb)
    {
        // UpdateAt and RemoveAt both refuse a NULL handle; this refuses a non-null one, which is the same guard from the other side. Adding a cluster twice
        // inserts a second leaf entry and then overwrites the handle, so the first entry is orphaned — returned by every query, unreachable by RemoveAt, and
        // uncounted. That is ST-05 with no detector, since nothing else ever looks for two entries with one payload id.
        if (!SpatialRTree<TransientStore>.IsNullHandle(_tree.PayloadBackPointers[clusterChunkId]))
        {
            ThrowHelper.ThrowInvalidOp($"Cluster {clusterChunkId} is already in this cell's tree — adding it twice orphans the first entry (ST-05).");
        }

        Span<double> coords = stackalloc double[6];
        ToCoords(in aabb, coords);

        var accessor = _segment.CreateChunkAccessor();
        try
        {
            _tree.Insert(clusterChunkId, coords, ref accessor, null, aabb.CategoryMask);
            _clusterCount++;
        }
        finally
        {
            accessor.Dispose();
        }
    }

    /// <summary>
    /// Retire this tree, returning every chunk it still holds to the shared segment. The instance must not be used afterwards.
    /// </summary>
    /// <remarks>
    /// Called by the demotion path once the last cluster has been removed. Removing entries cascades leaf frees up the tree, but the final empty ROOT has no
    /// parent to unlink it from and so survives — one stranded chunk per tree, on a segment shared by every cell of the archetype and reclaimed by nothing.
    /// </remarks>
    internal void Release()
    {
        if (_clusterCount != 0)
        {
            ThrowHelper.ThrowInvalidOp($"This cell's tree still holds {_clusterCount} clusters; releasing it now would strand every chunk they occupy.");
        }

        _tree.ReleaseRootChunk();
        _looseLeafCount = 0;
        _freedChunks.Clear();
    }

    /// <summary>
    /// Retire a tree that is still populated, returning every chunk it holds. For the wholesale discard paths, where the owner is about to drop the entire
    /// per-cell index rather than demote one cell.
    /// </summary>
    /// <remarks>
    /// Empties through <see cref="RemoveAt"/> so the leaf frees cascade exactly as they do on the demotion path, then releases the root the cascade cannot
    /// reach. Defensive about a handle that is already null rather than throwing the way <see cref="RemoveAt"/> does: this runs while state is being rebuilt,
    /// sometimes after a crash, and abandoning a rebuild over one inconsistent handle would be a worse outcome than the chunk it would have reclaimed.
    /// </remarks>
    internal void ReleaseAll()
    {
        var ids = new int[_clusterCount];
        var found = 0;
        foreach (var clusterChunkId in EnumerateClusterIds())
        {
            if (found < ids.Length)
            {
                ids[found++] = clusterChunkId;
            }
        }

        for (var i = 0; i < found; i++)
        {
            if (!SpatialRTree<TransientStore>.IsNullHandle(_tree.PayloadBackPointers[ids[i]]))
            {
                RemoveAt(ids[i]);
            }
        }

        _clusterCount = 0;
        Release();
    }

    /// <summary>The cluster's current packed handle, or <see cref="SpatialRTree{TStore}.NullHandle"/> when it is not in this tree.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal int HandleOf(int clusterChunkId) => _tree.PayloadBackPointers[clusterChunkId];

    /// <summary>
    /// Update a cluster's bounds in place when they still fit the leaf that holds them, and reinsert only when they escape — <c>C5</c>.
    /// </summary>
    /// <remarks>
    /// <para>The in-place store is the whole economic argument: a cluster's box moves every tick for a dynamic archetype, and remove-and-reinsert on each is
    /// ~94-235 µs per cell per tick against ~14 µs escape-bound. It is also where a stale handle would do its damage, which is why
    /// <c>SpatialRTree.PayloadBackPointers</c> had to exist before this method could.</para>
    /// <para><b>The handle is read from the back-pointer array, never passed in.</b> Taking it as a parameter was the earlier shape and it was wrong: the
    /// caller cannot keep a handle valid, because another cluster's split or removal relocates this one. A stale handle fails the identity check inside
    /// <see cref="SpatialRTree{TStore}.TryUpdateLeafEntryInPlace"/> — which is the check working — but the escape path underneath would then have removed at
    /// that same stale location, which is a live entry belonging to somebody else. Reading the authoritative store removes the failure mode rather than
    /// detecting half of it.</para>
    /// <para><b>This leaves the leaf's MBR loose, and that is deliberate.</b> Not refitting is what makes the fast path fast. <c>ST-01</c> states leaf MBR
    /// EQUALITY, so the looseness must not outlive the exclusive window — the caller refits the leaves it touched at the end of the pass. Too-loose is
    /// <c>ST-01</c>'s performance-only direction; the fatal direction is too-tight, which this path cannot produce because it only ever widens.</para>
    /// </remarks>
    internal void UpdateAt(int clusterChunkId, in ClusterSpatialAabb aabb, out bool escaped)
    {
        Span<double> coords = stackalloc double[6];
        ToCoords(in aabb, coords);

        int handle = _tree.PayloadBackPointers[clusterChunkId];
        if (SpatialRTree<TransientStore>.IsNullHandle(handle))
        {
            ThrowHelper.ThrowInvalidOp($"Cluster {clusterChunkId} has no live handle in this cell's tree — update it only after Add and before RemoveAt.");
        }

        var accessor = _segment.CreateChunkAccessor();
        try
        {
            var (leafChunkId, slotIndex) = SpatialRTree<TransientStore>.UnpackHandle(handle);
            var outcome = _tree.TryUpdateLeafEntryInPlace(leafChunkId, slotIndex, clusterChunkId, coords, aabb.CategoryMask, ref accessor);
            if (outcome == LeafUpdateResult.Updated)
            {
                escaped = false;
                RecordLooseLeaf(leafChunkId);
                return;
            }

            if (outcome == LeafUpdateResult.PreconditionFailed)
            {
                // NOT an escape, and treating it as one is a silent deletion. This branch existed as a fall-through until #872 step 9: the handle does not name
                // this cluster, so removing at it deletes whoever does and retires THEIR back-pointer — ST-05's own failure, with no exception near the cause.
                // Under a single writer the branch is unreachable, which is exactly why it must throw: reaching it means ST-05 has a gap and the loud failure
                // is the only thing that will surface it.
                ThrowHelper.ThrowInvalidOp(
                    $"Cluster {clusterChunkId}'s handle (leaf {leafChunkId}, slot {slotIndex}) no longer names it. PayloadBackPointers is repaired on every "
                    + "relocation, so this means either a gap in that repair or a concurrent writer on one tree, which SpatialRTree does not support "
                    + "(ADR-044, invariant O2).");
            }

            // Escaped its leaf: remove and reinsert. The removal is identity-checked even though the outcome above already proved the slot is ours — the two
            // reads are separated by nothing here, but the checked form is what keeps that true if this method ever grows a step between them.
            escaped = true;
            _tree.RemoveChecked(leafChunkId, slotIndex, clusterChunkId, ref accessor);

            // Before the reinsert, not after: the reinsert can ALLOCATE the chunk the removal just freed, and a record naming it would then point at a live
            // node again — possibly one belonging to another cell's tree on this shared segment.
            ScrubFreedLooseLeaves();
            _tree.Insert(clusterChunkId, coords, ref accessor, null, aabb.CategoryMask);
        }
        finally
        {
            accessor.Dispose();
        }
    }

    private void RecordLooseLeaf(int leafChunkId)
    {
        // Collapse a run of updates to the same leaf. A refit is a leaf recompute PLUS an ancestor walk to the root, and every level re-reads all ~11 children
        // to rebuild its union mask — so a duplicate is not the cheap repeat the first version of this comment claimed, it is a whole root walk. Comparing
        // against the last entry catches the common case (a cell's clusters are updated in id order, and neighbours share a leaf) without the per-update
        // HashSet probe this path exists to avoid.
        if (_looseLeafCount > 0 && _looseLeaves[_looseLeafCount - 1] == leafChunkId)
        {
            return;
        }

        if (_looseLeafCount == _looseLeaves.Length)
        {
            Array.Resize(ref _looseLeaves, _looseLeaves.Length * 2);
        }
        _looseLeaves[_looseLeafCount++] = leafChunkId;
    }

    /// <summary>
    /// Drop any pending refit naming a chunk the tree has just freed.
    /// </summary>
    /// <remarks>
    /// <b>Called immediately after every structural mutation, and it must be immediate.</b> A freed chunk keeps its bytes — leaf flag, count, MBR, parent
    /// pointer — so a stale record still passes every "is this a leaf" test, and on the shared per-archetype segment the next allocation can hand that chunk
    /// to a DIFFERENT cell's tree. Refitting it then rewrites a live node belonging to another cell and walks its parent chain, which is ST-01 and ST-03
    /// damage with nothing near the cause. Single-threaded repro: a leaf holding one cluster, updated in place (recorded) and then escaping (leaf emptied and
    /// freed) leaves exactly this dangling record.
    /// </remarks>
    private void ScrubFreedLooseLeaves()
    {
        if (_freedChunks.Count == 0)
        {
            return;
        }

        for (int f = 0; f < _freedChunks.Count; f++)
        {
            int freed = _freedChunks[f];
            for (int i = _looseLeafCount - 1; i >= 0; i--)
            {
                if (_looseLeaves[i] == freed)
                {
                    _looseLeaves[i] = _looseLeaves[--_looseLeafCount];
                }
            }
        }

        _freedChunks.Clear();
    }

    /// <summary>
    /// Refit every leaf an in-place update left loose, restoring <c>ST-01</c>'s equality. Call once at the end of the AABB refresh pass, before any query runs.
    /// </summary>
    /// <remarks>
    /// <b>Why this is owed rather than optional.</b> Skipping the refit is what makes the in-place path fast, and a too-loose MBR costs a query nothing but a
    /// redundant visit. What it does break is <c>ST-01</c> read literally — so a <c>TreeValidator</c> run, or any rebuild that assumes equality, sees a
    /// violation that is real even though no query is wrong. Paying it once per pass rather than once per update is the whole trade: the leaves touched in a
    /// tick are a small fraction of the tree, and each is refit once regardless of how many of its entries moved.
    /// </remarks>
    internal void RefitLooseLeaves()
    {
        if (_looseLeafCount == 0)
        {
            return;
        }

        var accessor = _segment.CreateChunkAccessor();
        try
        {
            for (int i = 0; i < _looseLeafCount; i++)
            {
                _tree.RefitLeafAndAncestors(_looseLeaves[i], ref accessor);
            }
        }
        finally
        {
            accessor.Dispose();
        }

        _looseLeafCount = 0;
    }

    /// <summary>Leaves currently owed a refit. Zero outside the exclusive window if the caller is doing its job — which is what the test asserts.</summary>
    internal int LooseLeafCount => _looseLeafCount;

    /// <summary>Remove a cluster. Its handle is retired to <see cref="SpatialRTree{TStore}.NullHandle"/> by the removal.</summary>
    internal void RemoveAt(int clusterChunkId)
    {
        int handle = _tree.PayloadBackPointers[clusterChunkId];
        if (SpatialRTree<TransientStore>.IsNullHandle(handle))
        {
            ThrowHelper.ThrowInvalidOp($"Cluster {clusterChunkId} is not present in this cell's tree, so it cannot be removed from it.");
        }

        var accessor = _segment.CreateChunkAccessor();
        try
        {
            var (leafChunkId, slotIndex) = SpatialRTree<TransientStore>.UnpackHandle(handle);
            _tree.RemoveChecked(leafChunkId, slotIndex, clusterChunkId, ref accessor);
            ScrubFreedLooseLeaves();
            _clusterCount--;
        }
        finally
        {
            accessor.Dispose();
        }
    }

    /// <summary>
    /// Cluster chunk ids whose bounds overlap <paramref name="queryCoords"/>, which must already be in this cell's frame.
    /// </summary>
    /// <remarks>
    /// <b>No category mask, on purpose (SQ-02).</b> The tree's own leaf test is AND-conjunctive while every cluster query filters ANY-BIT, so handing a
    /// caller's mask down here would make a promoted cell answer a different question from an unpromoted one — a false negative appearing only above
    /// <c>CellTreePromoteThreshold</c>. Callers apply <see cref="AabbClusterEnumerator.CategoryAdmits"/> to what comes back. Until #900 this took a
    /// <c>categoryMask</c> that every caller passed <c>0</c>; removing the parameter is what makes the invariant structural rather than a convention.
    /// <para><b>The price, stated so it is not rediscovered as a missed optimisation.</b> Passing <c>0</c> makes <c>SpatialRTree</c>'s node-level union
    /// prune unreachable for cluster queries, so a promoted cell descends to every geometrically overlapping cluster and filters afterwards. That is the
    /// cost of the two layers disagreeing about what a mask means; it is not a regression, since every caller already passed <c>0</c>.</para>
    /// </remarks>
    internal SpatialRTree<TransientStore>.AABBQueryEnumerator Query(scoped ReadOnlySpan<double> queryCoords) =>
        _tree.QueryAABB(queryCoords, null, 0);

    /// <summary>
    /// The same query, over an accessor the caller owns rather than one rented per call.
    /// </summary>
    /// <remarks>
    /// A cell walk touches many cells and asks each a small question, so an accessor created per question starts with an empty
    /// page window and takes the load-and-evict slow path every time. One accessor held across the walk pays that once.
    /// <para><b>No category mask, on purpose (SQ-02)</b> — see <see cref="Query"/> for why the <c>0</c> below is hardcoded.</para>
    /// </remarks>
    internal SpatialRTree<TransientStore>.AABBQueryEnumerator QueryWith(
        scoped ReadOnlySpan<double> queryCoords,
        ref ChunkAccessor<TransientStore> accessor) =>
        _tree.QueryAABBWith(queryCoords, ref accessor, 0);

    /// <summary>
    /// The same query, stated in this cell's f32 frame rather than marshalled through f64.
    /// </summary>
    /// <remarks>
    /// Every caller already holds floats — cluster bounds are f32 and <c>C15</c> cell-relative — so the f64 array was a
    /// round trip to a width nothing on the vector path reads. See <c>AABBQueryEnumerator</c>'s f32 constructor.
    /// <para><b>No category mask, on purpose (SQ-02)</b> — see <see cref="Query"/> for why the <c>0</c> below is hardcoded.</para>
    /// </remarks>
    internal SpatialRTree<TransientStore>.AABBQueryEnumerator QueryF32(
        float minX, float minY, float minZ, float maxX, float maxY, float maxZ) =>
        _tree.QueryAABBF32(minX, minY, minZ, maxX, maxY, maxZ, 0);

    /// <inheritdoc cref="QueryF32"/>
    internal SpatialRTree<TransientStore>.AABBQueryEnumerator QueryF32With(
        float minX, float minY, float minZ, float maxX, float maxY, float maxZ,
        ref ChunkAccessor<TransientStore> accessor) =>
        _tree.QueryAABBF32With(minX, minY, minZ, maxX, maxY, maxZ, 0, ref accessor);

    /// <summary>
    /// Cluster chunk ids whose bounds the ray enters, in the tree's own priority order, stated in this cell's frame.
    /// </summary>
    /// <remarks>
    /// <b>No category mask, on purpose (SQ-02)</b> — see <see cref="Query"/>. This wrapper exists so the ray path does not have to reach
    /// <see cref="Tree"/> to get at a masked overload: before #900 it did, and the invariant held there only because a hand-written <c>0</c> sat at the
    /// call site. Two wrappers are a small price for the half of "structural, not conventional" that was otherwise unsupported.
    /// </remarks>
    internal SpatialRTree<TransientStore>.RayEnumerator QueryRay(ReadOnlySpan<double> origin, ReadOnlySpan<double> direction, double maxDist) =>
        _tree.QueryRay(origin, direction, maxDist, null, 0);

    /// <summary>
    /// Cluster chunk ids not fully outside a set of half-space planes, which must already be in this cell's frame and 3D.
    /// </summary>
    /// <inheritdoc cref="QueryRay"/>
    internal SpatialRTree<TransientStore>.FrustumEnumerator QueryFrustum(ReadOnlySpan<double> planes, int planeCount) =>
        _tree.QueryFrustum(planes, planeCount, null, 0);

    /// <summary>Create an accessor over this cell tree's segment, for a caller that will run several queries against it.</summary>
    internal ChunkAccessor<TransientStore> CreateAccessor() => _segment.CreateChunkAccessor();

    /// <summary>
    /// Every cluster chunk id in this tree, in descent order. Used by demotion, which rebuilds a linear index from the tree's contents.
    /// </summary>
    /// <remarks>
    /// A full-extent query rather than a bespoke walk: it reuses the one traversal that is already differentially tested against a brute-force scan, and it
    /// cannot drift from what a real query sees. The extent is finite-but-enormous rather than infinite because an infinite coordinate entering the overlap
    /// test produces <c>NaN</c> comparisons on any axis whose node bound is also infinite, and <c>NaN</c> compares false — which would silently return
    /// nothing, the failure being indistinguishable from an empty cell.
    /// </remarks>
    internal ClusterIdEnumerable EnumerateClusterIds() => new(this);

    /// <summary>Allocation-free wrapper turning a full-extent query into a <c>foreach</c> over cluster chunk ids.</summary>
    internal readonly ref struct ClusterIdEnumerable
    {
        private readonly CellClusterTree _owner;

        internal ClusterIdEnumerable(CellClusterTree owner) => _owner = owner;

        public Enumerator GetEnumerator() => new(_owner);

        /// <summary>Drains a full-extent query, yielding each payload as an <see cref="int"/> cluster chunk id.</summary>
        internal ref struct Enumerator
        {
            private SpatialRTree<TransientStore>.AABBQueryEnumerator _inner;

            internal Enumerator(CellClusterTree owner)
            {
                Span<double> all = stackalloc double[6];
                all[0] = FullExtentMin;
                all[1] = FullExtentMin;
                all[2] = FullExtentMin;
                all[3] = FullExtentMax;
                all[4] = FullExtentMax;
                all[5] = FullExtentMax;
                // The 0 is written out rather than left to QueryAABB's default (SQ-02): SpatialRTree spells the same concept both ways — 0 here, but
                // uint.MaxValue on Insert — so an invariant resting on which default applies is one rename away from silently inverting.
                _inner = owner._tree.QueryAABB(all, null, 0);
            }

            public int Current => (int)_inner.Current.PayloadId;

            public bool MoveNext() => _inner.MoveNext();

            public void Dispose() => _inner.Dispose();
        }
    }

    /// <summary>Lower bound of the full-extent query box. Finite on purpose — see <see cref="EnumerateClusterIds"/>.</summary>
    private const double FullExtentMin = -1e30;

    /// <inheritdoc cref="FullExtentMin"/>
    private const double FullExtentMax = 1e30;

    /// <summary>
    /// Expand a cluster AABB into the tree's <c>[min0..minN, max0..maxN]</c> coordinate layout.
    /// </summary>
    /// <summary>
    /// The Z extent every 2D cluster is given. <b>Unit thickness, not zero</b> — see <see cref="ToCoords"/>.
    /// </summary>
    private const double FlatSlabMin = 0d;

    /// <inheritdoc cref="FlatSlabMin"/>
    private const double FlatSlabMax = 1d;

    /// <remarks>
    /// <para>The 2D sentinel is translated rather than passed through. A 2D archetype leaves Z at ±Infinity, and an R-Tree node MBR that unions an infinite
    /// extent becomes infinite on that axis for the whole subtree — which prunes nothing and turns every descent into a full scan.</para>
    /// <para><b>The slab has unit thickness, and that is load-bearing rather than arbitrary.</b> Collapsing 2D onto a ZERO-thickness slab is the obvious
    /// translation and it is catastrophic: every cost function in this tree is a product of extents across all three axes (<c>ComputeArea</c>,
    /// and <c>ChooseSubtree</c>'s <c>area</c>/<c>enlargedArea</c>), so a zero Z extent makes every node's area zero, every enlargement <c>0 - 0 = 0</c>, and
    /// every candidate indistinguishable. <c>ChooseSubtree</c> then keeps its first candidate on every comparison, so every insert walks the leftmost path
    /// and the tree degenerates into a list — measured at 5-25x SLOWER than the linear scan it replaces, with query cost growing linearly in cluster count
    /// instead of logarithmically. A thickness of exactly 1 makes the 3D product equal the true 2D area, so all three heuristics behave as their 2D
    /// equivalents rather than merely avoiding the degeneracy.</para>
    /// </remarks>
    private static void ToCoords(in ClusterSpatialAabb aabb, Span<double> coords)
    {
        bool flat = float.IsPositiveInfinity(aabb.MinZ) || float.IsNegativeInfinity(aabb.MaxZ);
        coords[0] = aabb.MinX;
        coords[1] = aabb.MinY;
        coords[2] = flat ? FlatSlabMin : aabb.MinZ;
        coords[3] = aabb.MaxX;
        coords[4] = aabb.MaxY;
        coords[5] = flat ? FlatSlabMax : aabb.MaxZ;
    }

    /// <summary>Expand a query box into the tree's coordinate layout, replacing an infinite bound with the tree's full extent.</summary>
    /// <remarks>
    /// <para><b>An infinite bound means UNBOUNDED, and it must not be collapsed onto the flat slab.</b> This method used to map an infinite Z range onto
    /// <see cref="FlatSlabMin"/>..<see cref="FlatSlabMax"/> — the slab <see cref="ToCoords"/> writes for a 2D cluster — which is right only while every
    /// STORED box is also on that slab. A 3D archetype stores its real Z, so a cell holding entities at, say, z ≈ 125 was asked for clusters overlapping
    /// z ∈ [0, 1] and answered NOTHING: a silent <c>SQ-01</c> false negative on every query that left an axis open, which is the documented way to ask a
    /// 2D question of a 3D-capable index. Measured across five game-shaped worlds, 13 of 15 population points returned zero from the tree where the
    /// linear scan answered correctly (#905).</para>
    /// <para>The full extent is the right image of infinity for both cases: a 2D cluster's slab and a 3D cluster's real Z both lie inside it. It is the
    /// same bound <see cref="EnumerateClusterIds"/> already uses to mean "everything", and it is finite for the reason recorded there — an infinite
    /// coordinate inside the tree's own arithmetic poisons the node MBRs it unions into.</para>
    /// <para>Each bound is mapped independently. A caller may leave one side of an axis open, and X and Y are no different from Z.</para>
    /// </remarks>
    internal static void QueryToCoords(float minX, float minY, float minZ, float maxX, float maxY, float maxZ, Span<double> coords)
    {
        coords[0] = Lower(minX);
        coords[1] = Lower(minY);
        coords[2] = Lower(minZ);
        coords[3] = Upper(maxX);
        coords[4] = Upper(maxY);
        coords[5] = Upper(maxZ);
        return;

        static double Lower(float v) => float.IsNegativeInfinity(v) || float.IsNaN(v) ? FullExtentMin : Math.Max(v, FullExtentMin);

        static double Upper(float v) => float.IsPositiveInfinity(v) || float.IsNaN(v) ? FullExtentMax : Math.Min(v, FullExtentMax);
    }
}
