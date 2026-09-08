using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Threading;
using JetBrains.Annotations;

namespace Typhon.Engine.Internals;

/// <summary>
/// Engine-wide coarse spatial grid — a VDB-shaped sparse structure: a root hash map from packed block coordinates to a block id, a dense per-block array of
/// cell-slot indices, and a chunked pool holding one 64-byte <see cref="CellState"/> per <b>occupied</b> cell (#872 step 8, decision C2).
/// </summary>
/// <remarks>
/// <para><b>Why sparse.</b> The dense predecessor allocated a <see cref="CellState"/> for every cell the world bounds implied, occupied or not — 128 MiB at a
/// 128³ grid, plus 24 B per cell <em>per spatial archetype</em> in the per-cell pools. A universe specified as 80 % empty paid all of it. Here an empty
/// region costs one absent hash entry.</para>
/// <para><b>Cost accepted</b> (§3.2): resolving a cell goes from one array index to a hash probe plus two dependent loads. A spatially coherent sweep pays
/// the probe once per BLOCK rather than once per cell — see the per-thread last-block fields.</para>
/// <para><b>A cell key is a pool slot, not a coordinate.</b> It names an occupied cell and nothing else, it is not stable across a rebuild, and it carries no
/// position — which is why <see cref="CellState"/> stores its own coordinates and <see cref="CellKeyToCoords"/> reads them back.</para>
/// <para><b>Creation is lazy and concurrent; destruction does not exist yet.</b> §3.5 places both inside the exclusive tick-fence window on the grounds that
/// both mutate shared structure. That is true of destruction — freeing a block under a live reader is a use-after-free — and false of creation, which is
/// monotonic: a concurrent reader either sees the cell or sees it absent, and "absent" was the correct answer a moment earlier. Windowing creation would
/// break read-your-writes, because <c>Transaction.CommitSpawns</c> resolves a cell inside the spawning transaction. Cell destruction is deferred, together
/// with the windowed sweep §3.5 describes; nothing in step 8 removes a cell.</para>
/// <para>The grid stores only transient state. Nothing in <see cref="CellState"/> is persisted; <c>RebuildCellState</c> reconstructs everything from entity
/// positions after a reopen.</para>
/// <para><b>Cells are three-dimensional.</b> A flat world is a grid one cell deep — see <see cref="SpatialGridConfig.Flat"/> — and its blocks are one cell
/// deep too, so a 2D game pays 256 index slots per block rather than 4 096.</para>
/// </remarks>
[PublicAPI]
internal sealed unsafe class SpatialGrid
{
    /// <summary>Cells per chunk of the <see cref="CellState"/> pool. 256 x 64 B = one 16 KiB chunk, appended and never moved (<c>MD-02</c>).</summary>
    private const int CellChunkShift = 8;

    private const int CellChunkSize = 1 << CellChunkShift;
    private const int CellChunkMask = CellChunkSize - 1;

    /// <summary>Largest per-axis block extent — P1's starting value, and the cap in <c>clamp(nextPow2(extent), 1, 16)</c>.</summary>
    private const int MaxBlockDim = 16;

    private readonly SpatialGridConfig _config;

    // ── Block geometry, derived once from the world extent ──────────────────
    private readonly int _blockDimX;
    private readonly int _blockDimY;
    private readonly int _blockDimZ;
    private readonly int _logBlockX;
    private readonly int _logBlockY;
    private readonly int _logBlockZ;
    private readonly int _blockCellCount;

    // ── Root: packed block coords -> block id ──────────────────────────────
    // Lock-free reads (per-stripe OLC); every write happens under _creationLock, so the map's own write path is never contended. Deliberately not disposed:
    // its Dispose only nulls managed references, so letting the GC reclaim the POH arrays along with the grid is correct and saves making SpatialGrid
    // IDisposable — a change that would reach every construction site including a dozen tests.
    private readonly ConcurrentHashMap<long, int> _blockMap = new(256);

    // ── Blocks: blockId -> int[_blockCellCount] of cell slots, -1 = absent ──
    private int[][] _blocks = new int[16][];
    private int _blockCount;

    // ── Cell pool: chunked, so a `ref CellState` handed out earlier stays valid forever (MD-02) ──
    private CellState[][] _cellChunks = new CellState[16][];
    private int _cellCount;

    /// <summary>Guards every structural change: appending a block, appending a cell chunk, and claiming a cell slot.</summary>
    /// <remarks>
    /// A lock rather than a CAS ladder because the path is genuinely rare — once per cell over the life of the process, against one resolve per entity per
    /// spawn — and because the CAS alternative leaks: a loser has already reserved a block or a cell slot and, with no destruction path to reclaim it, would
    /// leak it permanently. Readers never touch this lock.
    /// </remarks>
    private readonly Lock _creationLock = new();

    // Issue #231: bumped every time SetCellTier actually changes a cell's tier byte. The per-archetype
    // TierClusterIndex uses this to skip its rebuild when no cell tier has changed since the last dispatch.
    private int _tierVersion;

    /// <summary>Tier a newly created cell adopts, set by <see cref="ResetAllTiers"/>.</summary>
    /// <remarks>
    /// Without it, a cell created after a tick's <c>ResetAllTiers(Tier3)</c> would start at <see cref="SimTier.None"/>, <c>TierClusterIndex.Rebuild</c> would
    /// skip it, and every cluster in that cell would go undispatched for a tick — a silent missed wake, not an error. The dense grid had no such gap because
    /// every cell already existed when the bulk reset ran.
    /// </remarks>
    /// <remarks>
    /// <see cref="Volatile"/> on both ends: <see cref="ResetAllTiers"/> writes it from the tick thread and <see cref="CreateCell"/> reads it from whichever
    /// thread happens to spawn, with no lock between them. A plain pair leaves a new cell at <see cref="SimTier.None"/> on arm64 — the exact one-tick missed
    /// wake this field exists to prevent.
    /// </remarks>
    private byte _defaultTier;

    // ── Per-thread last-resolved block ─────────────────────────────────────
    // §3.2 expects a spatially coherent sweep to resolve the same block repeatedly; this turns that into one hash probe per BLOCK instead of one per cell,
    // which is what keeps a full-grid tier pass affordable. Keyed by grid INSTANCE because the fields are static: a process runs many engines, and a stale
    // hit from another grid would return a block id that means something else entirely.
    [ThreadStatic] private static SpatialGrid _lastBlockGrid;
    [ThreadStatic] private static int _lastBlockEpoch;
    [ThreadStatic] private static long _lastBlockKey;
    [ThreadStatic] private static int _lastBlockId;

    /// <summary>
    /// Bumped by <see cref="ResetCellState"/>. Every cached block id carries the epoch it was read in, and a stamp that no longer matches is discarded.
    /// </summary>
    /// <remarks>
    /// Clearing only the resetting thread's copy is not enough, and believing it was is the defect this replaces. A reset renumbers block ids from zero; any
    /// OTHER thread that had resolved a block still holds <c>_lastBlockGrid == this</c> with an id from the discarded numbering, and the fast path returns it
    /// <b>without probing the map</b>. That is either a null block array, or — the bad case — a live block belonging to a different region, into which the
    /// thread then files entities. "Reset happens on a quiescent grid" is the wrong invariant: quiescence during the reset says nothing about a per-thread
    /// cache consumed after it, and pool threads outlive both.
    /// </remarks>
    private int _structureEpoch;

    public SpatialGrid(SpatialGridConfig config)
    {
        _config = config;

        _blockDimX = BlockDimFor(config.GridWidth);
        _blockDimY = BlockDimFor(config.GridHeight);
        _blockDimZ = BlockDimFor(config.GridDepth);
        _logBlockX = BitOperations.Log2((uint)_blockDimX);
        _logBlockY = BitOperations.Log2((uint)_blockDimY);
        _logBlockZ = BitOperations.Log2((uint)_blockDimZ);
        _blockCellCount = _blockDimX * _blockDimY * _blockDimZ;

        WorldToCellCoords(0f, 0f, 0f, out _, out _, out FlatPlaneZ);
    }

    /// <summary>
    /// The Z plane containing world Z = 0 — the only plane a 2D archetype's entities can occupy, and therefore the Z range every 2D query collapses to.
    /// </summary>
    /// <remarks>
    /// <para><b>Why it is a field and not a call.</b> A 2D query carries ±Infinity on Z, meaning "every Z", which <see cref="WorldToCellRange"/> saturates to
    /// the grid's full depth; left alone that sweeps every Z plane of a volumetric grid, of which exactly one can ever hold a cell, because
    /// <c>ReadSpatialCenter3D</c> reports <c>posZ = 0</c> for both 2D field types. Collapsing to this plane is not an approximation of the answer — the other
    /// planes are empty by construction.</para>
    /// <para>The value cannot change for the grid's lifetime: it is a function of <see cref="Config"/> alone, which is assigned once here. Recomputing it per
    /// query cost three <c>IsFinite</c> tests and three clamped conversions on <b>every 2D query, ray and frustum walk</b> — #916's O2, measured as part of a
    /// setup term that was ~10 % of a 3x3-cell query and ~40 % of a single-cell one.</para>
    /// </remarks>
    internal readonly int FlatPlaneZ;

    /// <summary>
    /// Per-axis block extent: <c>clamp(nextPow2(extentInCells), 1, 16)</c>. A flat world's Z extent is 1, so its blocks are <c>16 x 16 x 1</c> and the Z term
    /// of the block-local index folds away — decision C16 ("2D is 3D with a degenerate axis") held exactly rather than approximated. A fixed 16³ would leave
    /// 3 840 of every 4 096 index slots permanently unused in such a world: 64 MB of index array for a 1000 x 1000-cell world against 4 MB.
    /// </summary>
    private static int BlockDimFor(int extentInCells) =>
        extentInCells >= MaxBlockDim ? MaxBlockDim : (int)BitOperations.RoundUpToPowerOf2((uint)Math.Max(1, extentInCells));

    public ref readonly SpatialGridConfig Config => ref _config;

    /// <summary>Number of cells that actually exist. Grows as cells are first touched; never shrinks, because step 8 has no destruction path.</summary>
    public int CellCount => Volatile.Read(ref _cellCount);

    /// <summary>Number of allocated blocks — one per occupied block-sized region. AC-8.7.</summary>
    public int BlockCount => Volatile.Read(ref _blockCount);

    /// <summary>Cells one block can hold. AC-8.7's denominator.</summary>
    public int BlockCellCapacity => _blockCellCount;

    /// <summary>Per-axis block extents, for diagnostics and for the fill report.</summary>
    public (int x, int y, int z) BlockDimensions => (_blockDimX, _blockDimY, _blockDimZ);

    /// <summary>
    /// Mean fraction of a block's index slots that name a live cell — Q3's measurement (AC-8.7). Low fill argues for P2's bitmask + compaction payload; high
    /// fill says the dense <c>int[]</c> is right.
    /// </summary>
    public double IntraBlockFill
    {
        get
        {
            int blocks = BlockCount;
            return blocks == 0 ? 0d : CellCount / (blocks * ReachableSlotsPerBlock());
        }
    }

    /// <summary>Index slots in a block that a world coordinate can actually reach, averaged over the grid.</summary>
    /// <remarks>
    /// Not <see cref="BlockCellCapacity"/>. When an axis is not a whole number of blocks, the last block along it is truncated by the world bounds and its
    /// tail slots are unreachable by construction. Dividing by the raw capacity folds that truncation into the fill number and reports it as spatial
    /// sparsity — a 40-cell axis at a 16-cell block extent caps the reported fill at 58 % however densely the world is populated. P1 and P2 are decided ON
    /// this number, so the denominator has to be the slots occupancy could ever fill.
    /// </remarks>
    private double ReachableSlotsPerBlock()
    {
        double reachable = ReachablePerAxis(_config.GridWidth, _blockDimX)
                           * ReachablePerAxis(_config.GridHeight, _blockDimY)
                           * ReachablePerAxis(_config.GridDepth, _blockDimZ);
        return Math.Max(1d, reachable);
    }

    /// <summary>Mean reachable extent of a block along one axis: full blocks contribute their whole extent, the last one only its remainder.</summary>
    private static double ReachablePerAxis(int extentInCells, int blockDim)
    {
        int blocks = ((extentInCells + blockDim) - 1) / blockDim;
        return blocks == 0 ? blockDim : (double)extentInCells / blocks;
    }

    /// <summary>
    /// Bytes the grid's own structures hold: the per-block index arrays plus the allocated cell chunks. Excludes the root map (a fixed-size POH allocation)
    /// and the per-archetype pools. AC-8.5's numerator.
    /// </summary>
    public long ResidentBytes
    {
        get
        {
            long total = (long)BlockCount * _blockCellCount * sizeof(int);
            var chunks = Volatile.Read(ref _cellChunks);
            for (int i = 0; i < chunks.Length; i++)
            {
                if (chunks[i] != null)
                {
                    total += (long)CellChunkSize * 64;
                }
            }
            return total;
        }
    }

    /// <summary>Bytes a dense grid over the same world would have held — 64 per cell the bounds imply, occupied or not. AC-8.5's denominator.</summary>
    public long DenseEquivalentBytes => (long)_config.CellCount * 64;

    /// <summary>
    /// Monotonic version counter, incremented each time a <see cref="SetCellTier"/> call actually flips a cell's tier byte.
    /// Consumed by per-archetype <see cref="TierClusterIndex"/> to short-circuit rebuilds when nothing changed (issue #231).
    /// </summary>
    internal int TierVersion => _tierVersion;

    // ═══════════════════════════════════════════════════════════════════════
    // Cell resolution
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Resolve a cell by grid coordinates, <b>creating it if it does not exist</b>. This is what a spawn needs: an entity's cell must exist before its
    /// cluster can be attached to it.
    /// </summary>
    /// <remarks>
    /// Read-only callers must use <see cref="TryGetCellKey"/> instead. Walking a coordinate rectangle with this method would materialise a cell for every
    /// coordinate in it — which is how a tier pass over an observer box, or a query broadphase over empty space, would silently undo the sparsity the whole
    /// structure exists for. <see cref="ResidentBytes"/> is the observable that catches such a mistake.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int ComputeCellKey(int cellX, int cellY, int cellZ)
    {
        // The dense grid rejected an out-of-range key with an IndexOutOfRangeException from its array. A sparse one would instead CREATE a block outside the
        // world and hand back a perfectly usable key for a cell that cannot be reached by any world position — a phantom the rebuild would never reproduce.
        // Three compares against a hash probe is not a cost worth trading for that.
        if (!InRange(cellX, cellY, cellZ))
        {
            ThrowCellOutOfRange(cellX, cellY, cellZ);
        }

        int blockId = ResolveBlock(cellX, cellY, cellZ, true);
        int local = BlockLocalIndex(cellX, cellY, cellZ);
        var block = Volatile.Read(ref _blocks)[blockId];
        int slot = Volatile.Read(ref block[local]);
        return slot >= 0 ? slot : CreateCell(blockId, local, cellX, cellY, cellZ);
    }

    /// <summary>Resolve a cell by grid coordinates without creating anything. <see langword="false"/> when the cell — or its whole block — is absent.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryGetCellKey(int cellX, int cellY, int cellZ, out int cellKey)
    {
        if (!InRange(cellX, cellY, cellZ))
        {
            cellKey = -1;
            return false;
        }

        int blockId = ResolveBlock(cellX, cellY, cellZ, false);
        if (blockId < 0)
        {
            cellKey = -1;
            return false;
        }

        var block = Volatile.Read(ref _blocks)[blockId];
        cellKey = Volatile.Read(ref block[BlockLocalIndex(cellX, cellY, cellZ)]);
        return cellKey >= 0;
    }

    /// <summary>
    /// The neighbour one step away, without creating it. Inside a block this is index arithmetic; only a step that leaves the block costs a root lookup (C3).
    /// A step into an <b>absent</b> block returns <see langword="false"/>, and returns the real cell once that block is created — the silent-false-negative
    /// case <c>SQ-01</c> cares about (AC-8.2).
    /// </summary>
    public bool TryGetNeighbourCellKey(int cellKey, int dx, int dy, int dz, out int neighbourCellKey)
    {
        var (x, y, z) = CellKeyToCoords(cellKey);

        // TryGetCellKey range-checks for us, and the check matters here rather than merely being tidy: clamping a step off the world edge would report cell
        // (0, y, z) as its own -X neighbour, and a caller walking outward would never terminate.
        return TryGetCellKey(x + dx, y + dy, z + dz, out neighbourCellKey);
    }

    /// <summary>
    /// Access a cell descriptor by cell key for read + write (callers bump <see cref="CellState.EntityCount"/>
    /// and <see cref="CellState.ClusterCount"/> directly).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ref CellState GetCell(int cellKey)
    {
        var chunks = Volatile.Read(ref _cellChunks);
        return ref chunks[cellKey >> CellChunkShift][cellKey & CellChunkMask];
    }

    /// <summary>
    /// Recover a cell's grid coordinates — one load from the cell's own cache line, because a pool slot has no positional meaning of its own.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public (int x, int y, int z) CellKeyToCoords(int cellKey)
    {
        ref var cell = ref GetCell(cellKey);
        return (cell.CellX, cell.CellY, cell.CellZ);
    }

    /// <summary>Whether a cell coordinate names a cell the configured world actually contains.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool InRange(int cellX, int cellY, int cellZ) =>
        (uint)cellX < (uint)_config.GridWidth && (uint)cellY < (uint)_config.GridHeight && (uint)cellZ < (uint)_config.GridDepth;

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void ThrowCellOutOfRange(int cellX, int cellY, int cellZ) =>
        throw new ArgumentOutOfRangeException(nameof(cellX),
            $"Cell ({cellX}, {cellY}, {cellZ}) is outside the configured grid of {_config.GridWidth} x {_config.GridHeight} x {_config.GridDepth}. "
            + $"World positions are clamped into range by WorldToCellKey; a raw coordinate is the caller's responsibility.");

    /// <summary>
    /// Block-local index: <c>(z &lt;&lt; logZ) | (y &lt;&lt; logY) | x</c>, which folds to <c>(y &lt;&lt; logY) | x</c> when the block is one cell deep.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int BlockLocalIndex(int cellX, int cellY, int cellZ) =>
        ((cellZ & (_blockDimZ - 1)) << (_logBlockY + _logBlockX)) | ((cellY & (_blockDimY - 1)) << _logBlockX) | (cellX & (_blockDimX - 1));

    /// <summary>Find (or create) the block owning a cell coordinate. <c>-1</c> when the block is absent and <paramref name="create"/> is false.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int ResolveBlock(int cellX, int cellY, int cellZ, bool create)
    {
        long key = VdbBlockKey.Pack(cellX >> _logBlockX, cellY >> _logBlockY, cellZ >> _logBlockZ);

        int epoch = Volatile.Read(ref _structureEpoch);
        if (ReferenceEquals(_lastBlockGrid, this) && _lastBlockEpoch == epoch && _lastBlockKey == key)
        {
            return _lastBlockId;
        }

        if (_blockMap.TryGetValue(key, out int blockId))
        {
            CacheBlock(epoch, key, blockId);
            return blockId;
        }

        if (!create)
        {
            // A MISS is deliberately not cached. Caching one would be the "assumed negative that later becomes a real cell" of §3.3 — a silent SQ-01 false
            // negative on that thread for every query until something evicted it.
            return -1;
        }

        int created = CreateBlock(key);
        CacheBlock(epoch, key, created);
        return created;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void CacheBlock(int epoch, long key, int blockId)
    {
        _lastBlockGrid = this;
        _lastBlockEpoch = epoch;
        _lastBlockKey = key;
        _lastBlockId = blockId;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private int CreateBlock(long key)
    {
        lock (_creationLock)
        {
            // Re-check: another thread may have created it between the lock-free probe and here.
            if (_blockMap.TryGetValue(key, out int existing))
            {
                return existing;
            }

            int blockId = _blockCount;
            var block = new int[_blockCellCount];
            Array.Fill(block, -1);

            var blocks = _blocks;
            if (blockId == blocks.Length)
            {
                var grown = new int[blocks.Length * 2][];
                Array.Copy(blocks, grown, blocks.Length);
                blocks = grown;
            }

            blocks[blockId] = block;

            // Publish the block array BEFORE the map entry: a reader that finds the key must be able to index _blocks[blockId]. Volatile.Write here is the
            // release; the Volatile.Read of _blocks in the resolve path is the matching acquire.
            Volatile.Write(ref _blocks, blocks);
            Volatile.Write(ref _blockCount, blockId + 1);
            _blockMap.TryAdd(key, blockId);
            return blockId;
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private int CreateCell(int blockId, int localIndex, int cellX, int cellY, int cellZ)
    {
        lock (_creationLock)
        {
            var block = Volatile.Read(ref _blocks)[blockId];
            int existing = block[localIndex];
            if (existing >= 0)
            {
                return existing;
            }

            int slot = _cellCount;
            int chunkIndex = slot >> CellChunkShift;

            var chunks = _cellChunks;
            if (chunkIndex == chunks.Length)
            {
                var grown = new CellState[chunks.Length * 2][];
                Array.Copy(chunks, grown, chunks.Length);
                chunks = grown;
            }

            chunks[chunkIndex] ??= new CellState[CellChunkSize];
            Volatile.Write(ref _cellChunks, chunks);

            ref var cell = ref chunks[chunkIndex][slot & CellChunkMask];
            cell.CellX = cellX;
            cell.CellY = cellY;
            cell.CellZ = cellZ;
            cell.Tier = Volatile.Read(ref _defaultTier);

            Volatile.Write(ref _cellCount, slot + 1);

            // Release: everything above must be visible to a reader that observes this slot. Its acquire is the Volatile.Read of the same element in the
            // resolve path — without the pair, a reader could see the slot index while the cell's coordinates are still zero.
            Volatile.Write(ref block[localIndex], slot);
            return slot;
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // World-space entry points
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Convert a world-space point to a grid cell key, <b>creating the cell if needed</b>. Points outside the configured bounds are clamped to the nearest
    /// valid cell — callers that care about "out of bounds" should test bounds themselves before calling.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int WorldToCellKey(float worldX, float worldY, float worldZ)
    {
        WorldToCellCoords(worldX, worldY, worldZ, out int cellX, out int cellY, out int cellZ);
        return ComputeCellKey(cellX, cellY, cellZ);
    }

    /// <summary>Resolve a world-space point to an existing cell without creating one.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryGetCellKeyAt(float worldX, float worldY, float worldZ, out int cellKey)
    {
        WorldToCellCoords(worldX, worldY, worldZ, out int cellX, out int cellY, out int cellZ);
        return TryGetCellKey(cellX, cellY, cellZ, out cellKey);
    }

    /// <summary>
    /// World-space minimum corner of the cell a key names — the origin every <c>C15</c> cell-relative bound is measured from (#872 step 9).
    /// </summary>
    /// <remarks>
    /// <para>One helper rather than the expression, because the expression was already written out longhand in three places before step 9 —
    /// <c>ClusterRef.MaybeFlagMigration</c>, <c>ArchetypeClusterState.FlagOutliersForMigration</c> and <c>DatabaseEngine.ClusterMigration</c> — and step 8's
    /// review found the one of the three that had never been taught its Z term. Cell-relative bounds add a fourth and fifth caller, at which point a
    /// divergence between them stops being a latent defect and becomes a certainty.</para>
    /// <para><b>Deliberately not clamped and not validated.</b> A caller passing a key for a cell that no longer exists gets whatever coordinates that pool
    /// slot now holds, which is <c>VG-01</c>'s problem, not this method's — adding a bounds test here would hide a stale key rather than surface it.</para>
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void CellOrigin(int cellKey, out float originX, out float originY, out float originZ)
    {
        var (cellX, cellY, cellZ) = CellKeyToCoords(cellKey);
        CellOriginFromCoords(cellX, cellY, cellZ, out originX, out originY, out originZ);
    }

    /// <summary>World-space minimum corner of a cell given its integer coordinates. See <see cref="CellOrigin"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void CellOriginFromCoords(int cellX, int cellY, int cellZ, out float originX, out float originY, out float originZ)
    {
        float cellSize = _config.CellSize;
        originX = _config.WorldMin.X + (cellX * cellSize);
        originY = _config.WorldMin.Y + (cellY * cellSize);
        originZ = _config.WorldMin.Z + (cellZ * cellSize);
    }

    /// <summary>
    /// Floor a world-space point to clamped cell coordinates. Pure arithmetic over the config — it touches no grid structure, which is what makes it safe in
    /// the rebuild's parallel map phase.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WorldToCellCoords(float worldX, float worldY, float worldZ, out int cellX, out int cellY, out int cellZ)
    {
        // Guard against NaN / ±Infinity: relational comparisons with NaN return false on both sides,
        // so the clamp below wouldn't catch a NaN — it would slip through as cellX=0 (or whatever the
        // implementation-defined (int)NaN returns on the current runtime). Rather than produce a
        // silently wrong cell key, throw so the caller fixes the upstream bug.
        if (!float.IsFinite(worldX) || !float.IsFinite(worldY) || !float.IsFinite(worldZ))
        {
            ThrowNonFinitePoint(worldX, worldY, worldZ);
        }

        cellX = ClampAxis(worldX, _config.WorldMin.X, _config.GridWidth);
        cellY = ClampAxis(worldY, _config.WorldMin.Y, _config.GridHeight);
        cellZ = ClampAxis(worldZ, _config.WorldMin.Z, _config.GridDepth);
    }

    /// <summary>
    /// Convert a world-space AABB to the inclusive cell-coordinate range it overlaps. Used by query
    /// paths that iterate all cells touched by a query box (issue #230). Out-of-bounds inputs are
    /// clamped to the grid extent; <see cref="float.NaN"/> inputs throw because they would produce meaningless cell indices.
    /// </summary>
    /// <param name="minX">Query AABB minimum X in world units.</param>
    /// <param name="minY">Query AABB minimum Y in world units.</param>
    /// <param name="minZ">Query AABB minimum Z in world units.</param>
    /// <param name="maxX">Query AABB maximum X in world units.</param>
    /// <param name="maxY">Query AABB maximum Y in world units.</param>
    /// <param name="maxZ">Query AABB maximum Z in world units.</param>
    /// <param name="cellMinX">Inclusive minimum cell X coordinate.</param>
    /// <param name="cellMinY">Inclusive minimum cell Y coordinate.</param>
    /// <param name="cellMinZ">Inclusive minimum cell Z coordinate.</param>
    /// <param name="cellMaxX">Inclusive maximum cell X coordinate.</param>
    /// <param name="cellMaxY">Inclusive maximum cell Y coordinate.</param>
    /// <param name="cellMaxZ">Inclusive maximum cell Z coordinate.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WorldToCellRange(float minX, float minY, float minZ, float maxX, float maxY, float maxZ,
        out int cellMinX, out int cellMinY, out int cellMinZ, out int cellMaxX, out int cellMaxY, out int cellMaxZ)
    {
        // ±Infinity is deliberately tolerated on Z and only there: ArchetypeClusterState.QueryAabb passes ±Infinity for a 2D archetype's Z bounds, meaning
        // "every Z", and the saturating cast turns that into the full depth range — exactly the intended answer. NaN is still rejected on every axis,
        // because it has no such reading.
        if (!float.IsFinite(minX) || !float.IsFinite(minY) || !float.IsFinite(maxX) || !float.IsFinite(maxY)
            || float.IsNaN(minZ) || float.IsNaN(maxZ))
        {
            throw new ArgumentException(
                $"WorldToCellRange received non-finite coordinates: ({minX}, {minY}, {minZ}, {maxX}, {maxY}, {maxZ}). " +
                $"Query data is corrupted upstream — spatial grid cannot compute a cell range for a NaN/Infinity AABB.");
        }

        cellMinX = ClampAxis(minX, _config.WorldMin.X, _config.GridWidth);
        cellMinY = ClampAxis(minY, _config.WorldMin.Y, _config.GridHeight);
        cellMinZ = ClampAxis(minZ, _config.WorldMin.Z, _config.GridDepth);
        cellMaxX = ClampAxis(maxX, _config.WorldMin.X, _config.GridWidth);
        cellMaxY = ClampAxis(maxY, _config.WorldMin.Y, _config.GridHeight);
        cellMaxZ = ClampAxis(maxZ, _config.WorldMin.Z, _config.GridDepth);
    }

    /// <summary>
    /// Floor one world coordinate to a cell coordinate and clamp it into <c>[0, dim)</c>. Infinities saturate to the ends of the range, which is what makes
    /// the "every Z" query of a 2D archetype resolve to the full depth.
    /// </summary>
    /// <remarks>
    /// The cast is safe on an infinity because .NET Core 3.0+ specifies float-to-integer conversion as <b>saturating</b>: <c>(int)float.PositiveInfinity</c>
    /// is <see cref="int.MaxValue"/> and the negative case is <see cref="int.MinValue"/>, so <see cref="Math.Clamp(int,int,int)"/> lands on the right end.
    /// Under the older unspecified conversion this would have been undefined and the clamp would have had to be done in float — which is what an earlier
    /// draft did, until an ablation showed no test could tell the two apart. Same guarantee #872 step 7 leans on for its <c>float.MaxValue</c> sentinel.
    /// </remarks>
    /// <summary>Out of line so the interpolated message is not built into every inlined copy of the per-spawn resolve path.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowNonFinitePoint(float worldX, float worldY, float worldZ) =>
        throw new ArgumentException(
            $"WorldToCellKey received a non-finite coordinate: ({worldX}, {worldY}, {worldZ}). "
            + $"Position data is corrupted upstream — spatial grid cannot place a NaN/Infinity entity.");

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int ClampAxis(float world, float origin, int dim) =>
        Math.Clamp((int)MathF.Floor((world - origin) * _config.InverseCellSize), 0, dim - 1);

    /// <summary>
    /// Extract a centre point from a spatial field pointer. Supports the four f32 tiers; the 2D variants report <c>posZ = 0</c>, which places them in the
    /// grid's first Z plane — the plane a flat world consists entirely of.
    /// </summary>
    /// <remarks>
    /// Shared by <see cref="WorldToCellKeyFromSpatialField"/> and the cell-crossing detection loop in
    /// <c>DatabaseEngine.DetectClusterMigrations</c> (issue #229 Phase 3). The detection path reuses the
    /// extracted center for both the hysteresis bounds check and the fallback <see cref="WorldToCellKey"/> call,
    /// avoiding a double read of the field memory.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void ReadSpatialCenter3D(byte* fieldPtr, SpatialFieldType fieldType, out float posX, out float posY, out float posZ)
    {
        switch (fieldType)
        {
            case SpatialFieldType.AABB2F:
            {
                float minX = *(float*)fieldPtr;
                float minY = *(float*)(fieldPtr + sizeof(float));
                float maxX = *(float*)(fieldPtr + 2 * sizeof(float));
                float maxY = *(float*)(fieldPtr + 3 * sizeof(float));
                posX = (minX + maxX) * 0.5f;
                posY = (minY + maxY) * 0.5f;
                posZ = 0f;
                return;
            }
            case SpatialFieldType.AABB3F:
            {
                // 3D AABB layout is [minX, minY, minZ, maxX, maxY, maxZ].
                float minX = *(float*)fieldPtr;
                float minY = *(float*)(fieldPtr + sizeof(float));
                float minZ = *(float*)(fieldPtr + 2 * sizeof(float));
                float maxX = *(float*)(fieldPtr + 3 * sizeof(float));
                float maxY = *(float*)(fieldPtr + 4 * sizeof(float));
                float maxZ = *(float*)(fieldPtr + 5 * sizeof(float));
                posX = (minX + maxX) * 0.5f;
                posY = (minY + maxY) * 0.5f;
                posZ = (minZ + maxZ) * 0.5f;
                return;
            }
            case SpatialFieldType.BSphere2F:
            {
                // BSphere2F — CenterX, CenterY, Radius
                posX = *(float*)fieldPtr;
                posY = *(float*)(fieldPtr + sizeof(float));
                posZ = 0f;
                return;
            }
            case SpatialFieldType.BSphere3F:
            {
                // BSphere3F — CenterX, CenterY, CenterZ, Radius.
                posX = *(float*)fieldPtr;
                posY = *(float*)(fieldPtr + sizeof(float));
                posZ = *(float*)(fieldPtr + 2 * sizeof(float));
                return;
            }
            default:
                // ValidateSupportedFieldType rejects f64 tiers at ConfigureSpatialGrid time, so this path should not be reachable. Defensive fallback
                // to help diagnose any future field-type addition that forgot to update this dispatch.
                throw new NotSupportedException(
                    $"ReadSpatialCenter3D: field type '{fieldType}' is not supported. f32 tiers (2D and 3D) only.");
        }
    }

    /// <summary>
    /// Extract a centre point from a spatial field pointer and convert it to a cell key, creating the cell if needed. See <see cref="ReadSpatialCenter3D"/>
    /// for the supported field types and for how a 2D field is placed on the Z axis.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int WorldToCellKeyFromSpatialField(byte* fieldPtr, SpatialFieldType fieldType)
    {
        ReadSpatialCenter3D(fieldPtr, fieldType, out float posX, out float posY, out float posZ);
        return WorldToCellKey(posX, posY, posZ);
    }

    /// <summary>Read a spatial field straight to clamped cell <b>coordinates</b>, touching no grid structure.</summary>
    /// <remarks>
    /// This is what the startup rebuild's parallel map phase uses. <c>RebuildSpatialStateFromData</c> maps in parallel and reduces serially in
    /// <c>ActiveClusterIds</c> order precisely so its output does not depend on thread interleaving; resolving to a cell KEY in the map phase would create
    /// cells concurrently and make every pool-slot index a function of the worker count, which <c>ClusterRebuildMergeTests</c> asserts it is not. The reduce
    /// turns these coordinates into keys, so cells are created in cluster order exactly as index slots already are.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void ReadCellCoordsFromSpatialField(byte* fieldPtr, SpatialFieldType fieldType, out int cellX, out int cellY, out int cellZ)
    {
        ReadSpatialCenter3D(fieldPtr, fieldType, out float posX, out float posY, out float posZ);
        WorldToCellCoords(posX, posY, posZ, out cellX, out cellY, out cellZ);
    }

    /// <summary>
    /// Throws if <paramref name="fieldType"/> is not supported by the spatial grid. f32 tiers only, 2D or 3D — a 2D field buckets into the grid's first Z
    /// plane (<see cref="ReadSpatialCenter3D"/>). f64 tiers remain deferred to a follow-up sub-issue of #228.
    /// </summary>
    public static void ValidateSupportedFieldType(SpatialFieldType fieldType, string archetypeName)
    {
        if (fieldType is SpatialFieldType.AABB2F or SpatialFieldType.BSphere2F or SpatialFieldType.AABB3F or SpatialFieldType.BSphere3F)
        {
            return;
        }
        throw new NotSupportedException(
            $"Spatial archetype '{archetypeName}' uses field type '{fieldType}'. " +
            $"The spatial grid currently supports f32 spatial fields only (AABB2F, BSphere2F, AABB3F, BSphere3F). " +
            $"f64 variants are a planned follow-up.");
    }

    /// <summary>
    /// Drop all cell state — blocks, cells and the root map. Called by <c>RebuildCellState</c> before reconstructing the mapping from entity positions. Each
    /// archetype's own <c>CellClusterPool</c> is reset separately by the archetype itself (Q10).
    /// </summary>
    public void ResetCellState()
    {
        lock (_creationLock)
        {
            _blockMap.Clear();
            Volatile.Write(ref _blocks, new int[16][]);
            Volatile.Write(ref _blockCount, 0);
            Volatile.Write(ref _cellChunks, new CellState[16][]);
            Volatile.Write(ref _cellCount, 0);

            // Invalidates EVERY thread's cached block id, not only this one's — see _structureEpoch. Released after the new structure is in place, so a
            // thread that observes the new epoch cannot then read the old arrays.
            Volatile.Write(ref _structureEpoch, _structureEpoch + 1);
            _lastBlockGrid = null;
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Tier assignment (issues #231 / #234)
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Assign a <see cref="SimTier"/> to a single cell. No-op when the cell already has the requested tier (avoids spurious version bumps).
    /// Passing <see cref="SimTier.None"/> clears the cell's tier — the tier index will then skip the cell entirely during rebuild.
    /// </summary>
    /// <remarks>
    /// The tier byte stored on <see cref="CellState.Tier"/> is a single-bit flag value from <see cref="SimTier"/>. Callers must pass a single-bit tier;
    /// multi-bit combinations (e.g. <see cref="SimTier.Near"/>) are rejected because the rebuild path uses
    /// <see cref="System.Numerics.BitOperations.TrailingZeroCount(uint)"/> to map the byte to an array index.
    /// </remarks>
    internal void SetCellTier(int cellKey, SimTier tier)
    {
        if (tier != SimTier.None && !tier.IsSingleTier())
        {
            throw new ArgumentException(
                $"SetCellTier requires a single-bit SimTier flag, got '{tier}'. Multi-tier combinations (e.g. SimTier.Near) are not valid at the cell level.",
                nameof(tier));
        }

        // Descriptive bounds error rather than the bare IndexOutOfRangeException from the pool access.
        if ((uint)cellKey >= (uint)CellCount)
        {
            throw new ArgumentOutOfRangeException(nameof(cellKey), cellKey,
                $"SetCellTier: cellKey {cellKey} does not name a live cell — the grid holds {CellCount}. " +
                $"Cell keys are pool slots handed out by WorldToCellKey / ComputeCellKey; a coordinate with no cell has no key.");
        }

        ref var cell = ref GetCell(cellKey);
        byte newTier = (byte)tier;
        if (cell.Tier != newTier)
        {
            byte oldTier = cell.Tier;
            cell.Tier = newTier;
            _tierVersion++;
            TyphonEvent.EmitSpatialGridCellTierChange(cellKey, oldTier, newTier);
        }
    }

    /// <summary>
    /// Set a cell's tier using min (promote-only) semantics (issue #234 Q7). If the cell's current tier is already higher priority
    /// (lower flag value, e.g. <see cref="SimTier.Tier0"/> = 1 vs <see cref="SimTier.Tier1"/> = 2), the call is a no-op. If the cell
    /// is unset (<see cref="SimTier.None"/> / 0), any tier overrides it. Bumps <see cref="TierVersion"/> only when the cell actually changes.
    /// </summary>
    internal void SetCellTierMin(int cellKey, SimTier tier)
    {
        if (tier == SimTier.None || !tier.IsSingleTier())
        {
            return;
        }

        if ((uint)cellKey >= (uint)CellCount)
        {
            return; // Silently ignore a key that names no live cell (AABB iteration may reach empty regions)
        }

        ref var cell = ref GetCell(cellKey);
        byte newTier = (byte)tier;
        // Min semantics: 0 (None/unset) is overridden by any tier. Among set tiers, keep the lower value (higher priority).
        if (cell.Tier == 0 || newTier < cell.Tier)
        {
            cell.Tier = newTier;
            _tierVersion++;
        }
    }

    /// <summary>
    /// Bulk-set every existing cell to the specified tier (issue #234 Q7), and make it the tier a cell created later adopts. Typically called at the start of
    /// <c>TierAssignment</c> to reset everything to <see cref="SimTier.Tier3"/> before applying per-observer promotions. Bumps <see cref="TierVersion"/> once.
    /// </summary>
    internal void ResetAllTiers(SimTier tier)
    {
        byte val = (byte)tier;
        Volatile.Write(ref _defaultTier, val);

        bool changed = false;
        int count = CellCount;
        for (int i = 0; i < count; i++)
        {
            ref var cell = ref GetCell(i);
            if (cell.Tier != val)
            {
                cell.Tier = val;
                changed = true;
            }
        }
        if (changed)
        {
            _tierVersion++;
        }
    }

    /// <summary>Set tiers for all <b>existing</b> cells overlapping a world-space AABB, using min (promote-only) semantics (issue #234 Q7).</summary>
    /// <remarks>
    /// Deliberately does not create cells: an observer box covers a great deal of empty space, and materialising a cell per coordinate would replace the
    /// sparsity C2 exists for with a dense grid built one tier call at a time. A tier on a clusterless cell is inert anyway — <c>TierClusterIndex.Rebuild</c>
    /// reads only the cells named by <c>ClusterCellMap</c> — so skipping absent cells changes no dispatch decision.
    /// </remarks>
    internal void SetTierInAABB(float minX, float minY, float minZ, float maxX, float maxY, float maxZ, SimTier tier)
    {
        WorldToCellRange(minX, minY, minZ, maxX, maxY, maxZ,
            out int cellMinX, out int cellMinY, out int cellMinZ, out int cellMaxX, out int cellMaxY, out int cellMaxZ);

        // BLOCK-major, not cell-major. A per-cell TryGetCellKey pays a hash probe for every coordinate swept, and the per-thread block cache cannot absorb
        // that: it memoises hits only, because a cached MISS is the silent false negative §3.3 forbids. An observer box over an 80 %-empty world — the case
        // C2 exists for — is mostly misses, so cell-major would defeat §3.2's "one probe per block" exactly where it matters. Here an absent block costs one
        // probe and its whole extent is skipped; a present one costs one probe and then index arithmetic.
        var blocks = Volatile.Read(ref _blocks);
        for (int bz = cellMinZ >> _logBlockZ; bz <= cellMaxZ >> _logBlockZ; bz++)
        {
            for (int by = cellMinY >> _logBlockY; by <= cellMaxY >> _logBlockY; by++)
            {
                for (int bx = cellMinX >> _logBlockX; bx <= cellMaxX >> _logBlockX; bx++)
                {
                    if (!_blockMap.TryGetValue(VdbBlockKey.Pack(bx, by, bz), out int blockId))
                    {
                        continue;
                    }

                    var block = blocks[blockId];
                    int zLo = Math.Max(cellMinZ, bz << _logBlockZ);
                    int zHi = Math.Min(cellMaxZ, ((bz + 1) << _logBlockZ) - 1);
                    int yLo = Math.Max(cellMinY, by << _logBlockY);
                    int yHi = Math.Min(cellMaxY, ((by + 1) << _logBlockY) - 1);
                    int xLo = Math.Max(cellMinX, bx << _logBlockX);
                    int xHi = Math.Min(cellMaxX, ((bx + 1) << _logBlockX) - 1);

                    for (int cz = zLo; cz <= zHi; cz++)
                    {
                        for (int cy = yLo; cy <= yHi; cy++)
                        {
                            for (int cx = xLo; cx <= xHi; cx++)
                            {
                                int cellKey = Volatile.Read(ref block[BlockLocalIndex(cx, cy, cz)]);
                                if (cellKey >= 0)
                                {
                                    SetCellTierMin(cellKey, tier);
                                }
                            }
                        }
                    }
                }
            }
        }
    }
}
