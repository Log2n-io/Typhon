using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Internals;

/// <summary>
/// Per-archetype runtime state for cluster storage. Manages the cluster segment, active cluster tracking, and slot claiming for entity spawn/destroy.
/// </summary>
/// <remarks>
/// <para>Each cluster-eligible archetype gets one <see cref="ArchetypeClusterState"/> instance, created during <c>DatabaseEngine.InitializeArchetypes</c>.</para>
/// <para>Active clusters are tracked in a compact array for O(N_clusters) iteration.
/// Free slot discovery uses bitmask TZCNT on OccupancyBits.</para>
/// </remarks>
internal sealed unsafe class ArchetypeClusterState
{
    /// <summary>ChunkBasedSegment backing cluster data (SV + V components). Null for pure-Transient archetypes.</summary>
    public ChunkBasedSegment<PersistentStore> ClusterSegment;

    /// <summary>ChunkBasedSegment backing Transient component data. Null if archetype has no Transient components.
    /// Uses identical layout as <see cref="ClusterSegment"/> (same stride, same offsets). Chunk IDs are synchronized
    /// via lockstep allocation/free.</summary>
    public ChunkBasedSegment<TransientStore> TransientSegment;

    /// <summary>TransientStore instance kept alive for heap-backed TransientSegment. Null if no Transient components.</summary>
    internal TransientStore? TransientClusterStore;

    /// <summary>Precomputed layout info (offsets, sizes, cluster size N).</summary>
    public ArchetypeClusterInfo Layout;

    /// <summary>Compact array of chunk IDs for clusters with occupancy > 0.</summary>
    public int[] ActiveClusterIds;

    /// <summary>Number of active clusters (valid entries in <see cref="ActiveClusterIds"/>).</summary>
    public int ActiveClusterCount;

    /// <summary>Chunk ID of first cluster with at least one free slot. -1 = none (allocate new).</summary>
    public int FreeClusterHead;

    // ═════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════
    // Parallel-fence migration finalize latch. Single per-archetype exclusive latch acquired only on the rare path where an occupancy clear flips the LAST
    // bit of a cluster — the worker that drained the cluster enters the finalize section (FinaliseEmptyClusterCellState + RemoveFromActiveList + segment
    // FreeChunk) while other workers continue their migration work undisturbed. Padded to 64 bytes so the latch field owns a full cache line and uncontended
    // acquisitions don't ping-pong with adjacent hot fields like ActiveClusterCount / MigrationHint / LastTickMigrationCount.
    // See rule MD-03 in claude/rules/spatial.md.
    // ═════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════

    [StructLayout(LayoutKind.Explicit, Size = 64)]
    private struct PaddedFinalizeLock
    {
        [FieldOffset(0)] public AccessControlSmall Lock;
    }

    private PaddedFinalizeLock _finalizeLock;

    // ═════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════
    // Deferred-drain list. Migrate-phase workers atomically clear slot bits — when a clear flips the LAST bit of a cluster, the worker DOES NOT immediately
    // finalize-and-free the chunk (would race with concurrent ClaimSlotInCell on the same cluster: the claimant could CAS-set a fresh bit between the AND
    // and the finalize lock, then we'd free a chunk that has live data). Instead, the worker records the chunkId here. FinalizeArchetypeFence walks the
    // list serially (per-archetype atomic), re-checks occupancy under _finalizeLock, and frees only clusters that are still empty. See review C-1.
    //
    // Slot reservation is lock-free: Interlocked.Increment on _drainedCount, then write into _drainedClusterIds[slot].
    // Capacity is pre-sized by PreSizeMigrationBuffers to PendingMigrationCount (one migration releases at most one source slot, so cluster-drain
    // count ≤ migration count).
    // ═════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════
    internal int[] _drainedClusterIds;
    internal int _drainedCount;

    // ═════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════
    // Fence-tick intermediate state. Populated by the Prep phase of the parallel fence (DatabaseEngine.PrepareArchetypeFence), consumed by the Migrate phase
    // (ExecuteMigrationsSlice) and the Finalize phase (FinalizeArchetypeFence). Single-archetype-scoped; reset at the top of Prep each tick.
    // ═════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>Branch path selected by Prep. 0 = no work (pure-transient with no dirty / no-spatial clean / non-cluster-eligible),
    /// 1 = clean-bitmap spatial refresh path (local occupancy bits, no WAL), 2 = dirty-bitmap path (full snapshot + WAL).</summary>
    internal byte FenceBranchPath;

    /// <summary>Dirty-bits snapshot for this fence tick, set by Prep, mutated atomically by Migrate (slot bit flips), read by Finalize for AABB + WAL.
    /// On branch path 1 this is the local occupancy-only spatialBits buffer; on path 2 it's the real <c>ClusterDirtyBitmap.Snapshot()</c> result.</summary>
    internal long[] FenceDirtyBits;

    /// <summary>Popcount of dirty entries after occupancy-masking. Drives WAL chunk sizing in Finalize. Path 1 leaves this at 0.</summary>
    internal int FenceEntryCount;

    /// <summary>Dirty cluster count (per-word non-zero count) at the end of Prep. Used for telemetry only.</summary>
    internal int FenceDirtyClusterCount;

    /// <summary>
    /// Popcount of <see cref="ClusterProcessBitmap"/> captured at end of Prep. Read by the AabbRefresh planner to size per-archetype cost without redoing the
    /// popcount on TickDriver (review D-4). -1 indicates "not computed this tick" (non-BarrierOnly archetypes use <see cref="ActiveClusterCount"/> directly).
    /// </summary>
    internal int FenceProcessBitmapClusterCount;

    /// <summary>
    /// Apply a contiguous run of <see cref="DirtyBitDelta"/> entries to <see cref="FenceDirtyBits"/>. Called from <see cref="DatabaseEngine.FlushDirtyBitDeltas"/>
    /// after each chunk's Migrate phase completes — the chunk's worker-local buffer is sorted by archetypeId, then this method applies all deltas for one
    /// archetype under a single <see cref="_finalizeLock"/> acquisition. Plain bit ops (no Interlocked) are correct under the lock:
    /// only one worker writes to this archetype's FenceDirtyBits at a time, eliminating cross-worker cache-line false-sharing on adjacent chunkIds.
    /// </summary>
    /// <param name="buffer">Worker-local list of <see cref="DirtyBitDelta"/> entries, sorted by archetypeId; this archetype's run is contiguous.</param>
    /// <param name="offset">Index of the first entry in <paramref name="buffer"/> belonging to this archetype's run.</param>
    /// <param name="count">Number of contiguous entries starting at <paramref name="offset"/> to apply; a value &lt;= 0 is a no-op.</param>
    internal void ApplyDirtyBitDeltas(List<DirtyBitDelta> buffer, int offset, int count)
    {
        if (count <= 0)
        {
            return;
        }

        ref WaitContext nullCtx = ref Unsafe.NullRef<WaitContext>();
        _finalizeLock.Lock.EnterExclusiveAccess(ref nullCtx);
        try
        {
            // First pass: find the max chunkId referenced so we grow FenceDirtyBits once if needed.
            int maxChunkId = -1;
            for (int i = 0; i < count; i++)
            {
                var d = buffer[offset + i];
                if (d.SrcChunkId > maxChunkId)
                {
                    maxChunkId = d.SrcChunkId;
                }

                if (d.DstChunkId > maxChunkId)
                {
                    maxChunkId = d.DstChunkId;
                }
            }
            if (FenceDirtyBits == null || maxChunkId >= FenceDirtyBits.Length)
            {
                int required = maxChunkId + 1;
                if (FenceDirtyBits == null)
                {
                    FenceDirtyBits = new long[Math.Max(required, 16)];
                }
                else
                {
                    int newLen = FenceDirtyBits.Length;
                    while (newLen < required)
                    {
                        newLen = Math.Max(newLen * 2, required);
                    }

                    Array.Resize(ref FenceDirtyBits, newLen);
                }
            }

            // Second pass: apply clears and sets. Plain bit ops — we hold the lock, no other worker is writing.
            var bits = FenceDirtyBits;
            for (int i = 0; i < count; i++)
            {
                var d = buffer[offset + i];
                if (d.SrcClearMask != 0 && d.SrcChunkId >= 0 && d.SrcChunkId < bits.Length)
                {
                    bits[d.SrcChunkId] &= ~d.SrcClearMask;
                }
                if (d.DstSetMask != 0 && d.DstChunkId >= 0 && d.DstChunkId < bits.Length)
                {
                    bits[d.DstChunkId] |= d.DstSetMask;
                }
            }
        }
        finally
        {
            _finalizeLock.Lock.ExitExclusiveAccess();
        }
    }

    /// <summary>
    /// Grow <see cref="FenceDirtyBits"/> on-demand under <see cref="_finalizeLock"/> so a Migrate-phase worker can safely write to <c>FenceDirtyBits[chunkId]</c>
    /// when its dstChunkId exceeds the pre-sized length. The lock excludes concurrent grows; callers must re-read <see cref="FenceDirtyBits"/> after the call
    /// to pick up the (possibly grown) array reference. Idempotent — if another worker already grew the array beyond <paramref name="chunkId"/>, returns
    /// without further work.
    /// </summary>
    internal void GrowFenceDirtyBitsForChunkId(int chunkId)
    {
        if (FenceDirtyBits != null && chunkId < FenceDirtyBits.Length)
        {
            return;
        }

        ref WaitContext nullCtx = ref Unsafe.NullRef<WaitContext>();
        _finalizeLock.Lock.EnterExclusiveAccess(ref nullCtx);
        try
        {
            // Re-check under lock — another worker may have already grown past us.
            int required = chunkId + 1;
            if (FenceDirtyBits == null)
            {
                FenceDirtyBits = new long[Math.Max(required, 16)];
            }
            else if (FenceDirtyBits.Length < required)
            {
                int newLen = FenceDirtyBits.Length;
                while (newLen < required)
                {
                    newLen = Math.Max(newLen * 2, required);
                }

                Array.Resize(ref FenceDirtyBits, newLen);
            }
        }
        finally
        {
            _finalizeLock.Lock.ExitExclusiveAccess();
        }
    }

    /// <summary>
    /// Grow <see cref="FenceDirtyBits"/> and supporting per-cluster arrays to an upper-bound size that the parallel Migrate phase will never exceed. Called by
    /// TickDriver between the Prep and Migrate phase dispatches — guarantees worker threads never need to <c>Array.Resize</c> a buffer during their parallel
    /// apply.
    /// </summary>
    /// <param name="upperBound">Worst-case maximum cluster chunk ID + 1 that this fence tick could touch. Typically, <c>PrimarySegmentCapacity +
    /// PendingMigrationCount</c> — one new cluster per migration in the worst case.</param>
    internal void PreSizeMigrationBuffers(int upperBound)
    {
        if (upperBound <= 0)
        {
            return;
        }

        // FenceDirtyBits is per-cluster (one long word per cluster chunk id). Grow to at least the upper bound.
        if (FenceDirtyBits == null)
        {
            FenceDirtyBits = new long[upperBound];
        }
        else if (FenceDirtyBits.Length < upperBound)
        {
            // Preserve existing dirty bits set during Prep — Array.Resize copies; we just need more tail space for migrations that may target chunk ids beyond
            // the snapshot length.
            int oldLen = FenceDirtyBits.Length;
            int newLen = oldLen;
            while (newLen < upperBound)
            {
                newLen = Math.Max(newLen * 2, upperBound);
            }

            Array.Resize(ref FenceDirtyBits, newLen);
        }

        // Per-cluster AABB + cell-mapping + spatial-index-slot arrays need to cover any newly allocated dst cluster.
        EnsureClusterAabbsCapacity(upperBound);
        EnsureClusterSpatialIndexSlotCapacity(upperBound);
        EnsureClusterCellMapCapacity(upperBound);
        EnsureClusterWriteBookkeepingCapacity(upperBound);

        // Deferred-drain list sized to PendingMigrationCount (each migration drains at most one source slot, so the cluster-drain count cannot exceed migration
        // count). _drainedCount is zeroed by Prep.
        int drainCap = Math.Max(16, PendingMigrationCount);
        if (_drainedClusterIds == null || _drainedClusterIds.Length < drainCap)
        {
            _drainedClusterIds = new int[Math.Max(drainCap, (_drainedClusterIds?.Length ?? 0) * 2)];
        }
    }

    /// <summary>
    /// Sort <see cref="PendingMigrations"/> in place by destination cell key so the parallel Migrate phase can give each worker a contiguous slice and have all
    /// of that worker's destination cells be disjoint from every other worker's destination cells. Called by TickDriver between Prep and Migrate.
    /// </summary>
    internal void SortPendingMigrationsByDestCellKey()
    {
        if (PendingMigrations == null || PendingMigrationCount < 2)
        {
            return;
        }

        Array.Sort(PendingMigrations, 0, PendingMigrationCount, MigrationByDestCellKeyComparer.Instance);
    }

    private sealed class MigrationByDestCellKeyComparer : IComparer<MigrationRequest>
    {
        public static readonly MigrationByDestCellKeyComparer Instance = new();
        public int Compare(MigrationRequest x, MigrationRequest y) => x.DestCellKey.CompareTo(y.DestCellKey);
    }

    /// <summary>
    /// Record a cluster that's been drained to empty by ReleaseSlot. Lock-free append via <see cref="Interlocked.Increment(ref int)"/>. Capacity is guaranteed
    /// by <see cref="PreSizeMigrationBuffers"/>. Same cluster may legitimately be recorded twice if a previous tick's drain wasn't followed by a finalize and
    /// this tick re-empties it after a refill+drain cycle — <see cref="DrainPendingClusterFinalizations"/> re-checks occupancy under the finalize lock and
    /// skips non-empty entries.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void RecordClusterDrain(int clusterChunkId)
    {
        int idx = Interlocked.Increment(ref _drainedCount) - 1;
        if (_drainedClusterIds == null || idx >= _drainedClusterIds.Length)
        {
            // PreSizeMigrationBuffers should have covered this — fall back to a synchronized grow.
            ref WaitContext nullCtx = ref Unsafe.NullRef<WaitContext>();
            _finalizeLock.Lock.EnterExclusiveAccess(ref nullCtx);
            try
            {
                if (_drainedClusterIds == null)
                {
                    _drainedClusterIds = new int[Math.Max(16, idx + 1)];
                }
                else if (_drainedClusterIds.Length <= idx)
                {
                    int newLen = _drainedClusterIds.Length * 2;
                    while (newLen <= idx)
                    {
                        newLen *= 2;
                    }

                    Array.Resize(ref _drainedClusterIds, newLen);
                }
            }
            finally
            {
                _finalizeLock.Lock.ExitExclusiveAccess();
            }
        }
        _drainedClusterIds[idx] = clusterChunkId;
    }

    /// <summary>
    /// Walk the deferred-drain list serially (called once per archetype from <see cref="DatabaseEngine.FinalizeArchetypeFence"/> after all Migrate-phase slices
    /// have completed). For each drained cluster, re-check occupancy: if still empty, run finalize + free; if a concurrent Claim re-filled it during Migrate,
    /// leave it alone. Resets <see cref="_drainedCount"/> to zero.
    /// <para>
    /// <b>Concurrency invariant:</b> Finalize-for-one-archetype runs on exactly one worker (one work item per archetype, dispatched atomically). By the time
    /// this method runs, the Migrate and AabbRefresh phase barriers have both passed — no concurrent ClaimSlotInCell or ReleaseSlot can mutate this archetype's
    /// clusters. The occupancy re-read is therefore single-threaded and the per-archetype lock is unnecessary here.
    /// </para>
    /// <para>
    /// Note: a cluster can appear in the drain list AND have non-zero occupancy if a Migrate-phase Claim refilled it after the drain was recorded. That's a
    /// Migrate-phase Claim arriving AFTER a Migrate-phase Release: legal because the cluster was still in the cell's claim list, and the Claim correctly
    /// re-occupied it. Skip the finalize.
    /// </para>
    /// </summary>
    internal void DrainPendingClusterFinalizations(SpatialGrid grid)
    {
        int count = _drainedCount;
        if (count == 0)
        {
            return;
        }

        var ids = _drainedClusterIds;
        bool hasCluster = ClusterSegment != null;
        var clusterAccessor = hasCluster ? ClusterSegment.CreateChunkAccessor() : default;
        var transientAccessor = TransientSegment != null ? TransientSegment.CreateChunkAccessor() : default;

        for (int i = 0; i < count; i++)
        {
            int chunkId = ids[i];
            byte* clusterBase = hasCluster ? clusterAccessor.GetChunkAddress(chunkId, true) : transientAccessor.GetChunkAddress(chunkId, true);
            if (*(ulong*)clusterBase != 0)
            {
                continue; // Claim re-filled this cluster after the drain — keep alive
            }

            FinaliseEmptyClusterCellState(grid, chunkId);
            RemoveFromActiveList(chunkId);
            ClusterSegment?.FreeChunk(chunkId);
            TransientSegment?.FreeChunk(chunkId);
        }
        _drainedCount = 0;
    }

    /// <summary>
    /// Per-cluster cell membership for spatial archetypes (issue #229 Phase 1+2). Flat array indexed by <c>clusterChunkId</c>, value is the spatial
    /// grid <c>cellKey</c> the cluster is attached to, or <c>-1</c> if unmapped (cluster not yet allocated, or archetype is not opted into the grid).
    /// </summary>
    /// <remarks>
    /// Lazily allocated by <see cref="ClaimSlotInCell"/> or <see cref="RebuildCellState"/>. Non-spatial archetypes and spatial archetypes running without a
    /// configured <see cref="SpatialGrid"/> leave this field <c>null</c> — the existing <see cref="ClaimSlot"/> path is unchanged for them.
    /// </remarks>
    public int[] ClusterCellMap;

    // ═══════════════════════════════════════════════════════════════════════
    // Migration queue (issue #229 Phase 3). Lazily allocated; only used when
    // SpatialSlot.HasSpatialIndex AND a SpatialGrid is configured AND cell crossings
    // actually occur. Population is sequential (detection loop runs single-threaded
    // inside WriteClusterTickFence), drained by ExecuteMigrations in the same loop.
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>Per-archetype pending migration queue. Populated by cell-crossing detection in
    /// <c>DetectClusterMigrations</c>, drained by <see cref="DatabaseEngine.ExecuteMigrations"/> at the tick fence.
    /// Null until the first cell-crossing is detected.</summary>
    internal MigrationRequest[] PendingMigrations;

    /// <summary>Number of valid entries in <see cref="PendingMigrations"/>. Reset to zero at the start
    /// of every <see cref="DatabaseEngine.ExecuteMigrations"/> call.</summary>
    internal int PendingMigrationCount;

    /// <summary>Telemetry counter: number of migrations executed in the most recently completed tick.</summary>
    public int LastTickMigrationCount;

    /// <summary>Telemetry counter: number of position changes that crossed the raw cell boundary but were
    /// absorbed by the hysteresis margin (no migration queued). Useful for tuning
    /// <see cref="SpatialGridConfig.MigrationHysteresisRatio"/>.</summary>
    public int LastTickHysteresisAbsorbedCount;

    /// <summary>Telemetry counter: wall-clock duration of <see cref="DatabaseEngine.ExecuteMigrations"/> in milliseconds,
    /// for the most recently completed tick.</summary>
    public double LastTickMigrationExecuteMs;

    /// <summary>
    /// Coarse work-estimate counter bumped on every cell-crossing flagged by <c>WriteSpatial</c>. Read and reset (snapshot-then-zero) by the fence work-planner
    /// to size the per-archetype migration cost. Non-atomic on purpose: order-of-magnitude is enough for chunk bucketing; lost increments under contention are
    /// tolerable.
    /// </summary>
    internal int MigrationHint;

    /// <summary>
    /// Test observation hook: length (in long words) of the <c>dirtyBits</c> snapshot at the end of <c>ExecuteMigrations</c>. Used by regression tests to
    /// verify the snapshot was grown when migration allocated a brand-new destination cluster whose chunk id exceeded the pre-migration length.
    /// Zero when no migrations ran.
    /// </summary>
    public int LastMigrationDirtyBitsWordCount;

    /// <summary>Per-entity dirty tracking for tick fence WAL serialization. Index = clusterChunkId * 64 + slotIndex.</summary>
    public DirtyBitmap ClusterDirtyBitmap;

    /// <summary>
    /// Per-cluster tight 2D AABB plus category mask for spatially-active clusters (issue #230).
    /// Indexed by clusterChunkId. Populated by spawn/destroy/migration hooks and the tick-fence recompute pass. Null for non-spatial archetypes or before the
    /// first spatial write. In-memory only — rebuilt at startup via <see cref="RebuildClusterAabbs"/> from entity positions (Q2/Q6 transient-state decision).
    /// Phase 1 is 2D f32 only.
    /// </summary>
    internal ClusterSpatialAabb[] ClusterAabbs;

    /// <summary>
    /// Per-cluster back-pointer into its cell's <see cref="CellSpatialIndex.ClusterIds"/> SoA array.
    /// <c>-1</c> for clusters not currently in the per-cell index (non-spatial archetypes, Static-mode archetypes in Phase 1, or before the first insertion).
    /// Indexed by clusterChunkId.
    /// </summary>
    internal int[] ClusterSpatialIndexSlot;

    // ═════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════
    // Write-time spatial bookkeeping. Populated by ClusterRef.WriteSpatial(...) at the write site. Consumed by the fence-time sparse-iteration pass — only
    // clusters with bits set here do any work at fence time. See claude/design/spatial/write-time-spatial.md.
    // ═════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// One bit per cluster — set whenever WriteSpatial detects pending work (migration or shrink-rescan). Drives the fence-time loop, replacing the
    /// unconditional scan of every active cluster. Indexed by clusterChunkId; word at <c>i / 64</c>, bit <c>i % 64</c>. Lazy-allocated alongside ClusterAabbs.
    /// </summary>
    internal long[] ClusterProcessBitmap;

    /// <summary>
    /// Per-cluster bitmap of slots needing migration this tick — one u64 per cluster, bit <c>i</c> set means slot <c>i</c>'s entity has crossed the
    /// cell+hysteresis boundary. Drained at fence by <see cref="DatabaseEngine.DetectClusterMigrations"/>. Indexed by clusterChunkId.
    /// </summary>
    internal ulong[] ClusterMigrationPendingSlots;

    /// <summary>
    /// Per-cluster destination cell key for the migration batch in <see cref="ClusterMigrationPendingSlots"/>. <c>-1</c> when no migration is pending.
    /// By cluster-coherence invariant, all flagged slots in a single cluster migrate to the same destination cell key (the first writer wins; conflicting
    /// writes are resolved at fence time by re-reading the slot's position). Indexed by clusterChunkId.
    /// </summary>
    internal int[] ClusterMigrationDestCellKeys;

    /// <summary>
    /// Per-cluster shrink-pending axes mask. Bit layout: 0x01=MinX, 0x02=MaxX, 0x04=MinY, 0x08=MaxY, 0x10=MinZ, 0x20=MaxZ. Set when an entity at an axis
    /// extreme moves inward — fence must rescan this cluster on the flagged axes only. Indexed by clusterChunkId.
    /// </summary>
    internal byte[] ClusterShrinkPendingAxes;

    /// <summary>
    /// Per-archetype per-cell spatial slot, indexed by cellKey. Null entries for cells where this archetype has no clusters. Lazy-allocated:
    /// the <see cref="PerCellSpatialSlot"/> is created on first cluster insertion into that cell. The DynamicIndex inside is also lazy (created on first
    /// <see cref="CellSpatialIndex.Add"/>). Null entirely for non-spatial archetypes or before grid opt-in.
    /// </summary>
    internal PerCellSpatialSlot[] PerCellIndex;

    /// <summary>
    /// Snapshot of the previous tick's dirty bitmap (occupancy-masked). Set during <c>WriteClusterTickFence</c>, consumed
    /// by <c>TyphonRuntime.BuildFilteredClusterEntities</c> for change-filtered parallel dispatch.
    /// Word index = clusterChunkId, bit position = slotIndex. Null when no entities were dirty.
    /// </summary>
    public long[] PreviousTickDirtySnapshot;

    // ═══════════════════════════════════════════════════════════════════════
    // Per-archetype B+Tree indexes. Null if archetype has no indexed fields.
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>Per-archetype B+Tree index slots, one per component slot with indexed fields. Null if no indexed fields.</summary>
    public ClusterIndexSlot[] IndexSlots;

    /// <summary>
    /// Per-slot SingleVersion <see cref="ComponentCollection{T}"/> descriptors — the buffers to release when a slot is freed on
    /// destroy. SV CC has no revision chain, so the cluster slot is the buffer's sole owner and must release it directly (unlike
    /// Versioned CC, whose buffers are owned by content chunks and released by the revision cleanup). Null when the archetype has
    /// no SingleVersion component carrying a ComponentCollection field — zero overhead on the destroy hot path.
    /// </summary>
    internal ClusterCollectionSlot[] CollectionSlots;

    /// <summary>Shadow guard bitmap. Guards first-write-per-tick shadow capture. Same index semantics as <see cref="ClusterDirtyBitmap"/>.</summary>
    public DirtyBitmap ClusterShadowBitmap;

    /// <summary>Shared <see cref="ChunkBasedSegment{TStore}"/> backing all per-archetype B+Trees for this archetype.</summary>
    public ChunkBasedSegment<PersistentStore> IndexSegment;

    // ═══════════════════════════════════════════════════════════════════════
    // Per-archetype Spatial R-Tree. Null if archetype has no spatial fields.
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>Per-archetype spatial R-Tree state. Check <c>SpatialSlot.HasSpatialIndex</c> for presence.</summary>
    public ClusterSpatialSlot SpatialSlot;

    /// <summary>
    /// Per-archetype <see cref="DirtyBitmapRing"/> consumed by <c>SpatialInterestSystem</c> for delta queries and the 64-tick staleness fallback
    /// (issue #230 Phase 3). Populated at <see cref="InitializeSpatial"/>; archived at the tick fence. Relocated from <c>ClusterSpatialSlot.DirtyRing</c>
    /// to decouple the ring from the legacy per-entity tree that's being removed in Phase 3 — the ring's lifecycle belongs to the archetype's cluster state,
    /// not to any particular spatial index implementation.
    /// </summary>
    public DirtyBitmapRing ClusterDirtyRing;

    /// <summary>
    /// Per-archetype per-cell cluster claim list (issue #229 Q10 resolution). Holds the cluster chunk IDs of THIS archetype's clusters attached to each
    /// grid cell. Before Q10 this pool was owned by <see cref="SpatialGrid"/> and shared across archetypes, which meant two spatial archetypes couldn't
    /// coexist on the same grid (their cluster chunk IDs would collide at the cell level). Under Q10 each archetype owns its own pool — queries and
    /// spawn-time "find a free slot in this cell" scans only see clusters of the current archetype. <c>null</c> when the archetype has no spatial field
    /// or when no grid is configured. Allocated during <see cref="InitializeSpatial"/> when the grid is known.
    /// </summary>
    internal CellClusterPool CellClusterPool;

    // ═══════════════════════════════════════════════════════════════════════
    // Issue #231: Tier dispatch state. The version counter is bumped whenever
    // a cluster is added to or removed from the active list — the per-archetype
    // TierClusterIndex reads it to skip rebuilds when the cluster set is stable.
    // The index itself is allocated lazily on the first tier-filtered dispatch.
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>Monotonic counter, incremented by <see cref="AddToActiveList"/> and <see cref="RemoveFromActiveList"/>.
    /// Consumed by <see cref="TierClusterIndex.RebuildIfStale"/> to short-circuit when no cluster has been added or removed since the last rebuild. Issue #231.
    /// </summary>
    public int ClusterSetVersion { get; private set; }

    /// <summary>Lazily-allocated per-archetype tier index (issue #231). Built on demand by <c>TyphonRuntime.OnParallelQueryPrepare</c> the first time a
    /// tier-filtered system runs against this archetype. Subsequent rebuilds are version-guarded and usually no-ops.</summary>
    internal TierClusterIndex TierIndex;

    // ═══════════════════════════════════════════════════════════════════════
    // Issue #233: Cluster dormancy state. Per-cluster sleep tracking for
    // skipping idle clusters during dispatch. Null arrays = dormancy not
    // enabled (non-spatial archetypes or threshold not set).
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>Per-cluster sleep state, indexed by cluster chunk ID. Null for non-spatial archetypes (zero overhead).
    /// Allocated eagerly for spatial archetypes in <see cref="InitializeSpatial"/>. Issue #233.</summary>
    internal ClusterSleepState[] SleepStates;

    /// <summary>Per-cluster ticks-since-last-dirty counter. ushort gives ~18 minutes at 60Hz before wrap, which far exceeds
    /// any reasonable <see cref="SleepThresholdTicks"/>. Same sizing/lifecycle as <see cref="SleepStates"/>. Issue #233.</summary>
    internal ushort[] SleepCounters;

    /// <summary>Number of consecutive clean ticks before a cluster transitions to <see cref="ClusterSleepState.Sleeping"/>.
    /// 0 = dormancy disabled (counters still increment but no transition). Default 0. Set by game code. Issue #233.
    /// Clamped to [0, 65535] because <see cref="SleepCounters"/> uses <c>ushort</c> storage (~18 minutes at 60Hz).</summary>
    private int _sleepThresholdTicks;
    public int SleepThresholdTicks
    {
        get => _sleepThresholdTicks;
        set => _sleepThresholdTicks = Math.Clamp(value, 0, ushort.MaxValue);
    }

    /// <summary>When &gt; 0, sleeping clusters periodically wake on a staggered schedule: cluster wakes when
    /// <c>(tickNumber % HeartbeatIntervalTicks) == (chunkId % HeartbeatIntervalTicks)</c>. 0 = no heartbeat. Issue #233.</summary>
    public int HeartbeatIntervalTicks;

    /// <summary>Count of clusters currently in <see cref="ClusterSleepState.Sleeping"/> state. When 0, all dormancy filtering
    /// in <c>OnParallelQueryPrepare</c> is skipped (zero overhead). Issue #233.</summary>
    public int SleepingClusterCount;

    /// <summary>Archetype ID for this cluster state. Set during <see cref="InitializeSpatial"/>. Used by
    /// <see cref="SetDirty"/> to tag wake requests via <see cref="DormancyReporter"/>. Issue #233.</summary>
    internal int ArchetypeId;

    /// <summary>Back-reference to the engine's <see cref="SpatialGrid"/>. Set during <see cref="InitializeSpatial"/>. Used by <c>ClusterRef.WriteSpatial</c> to
    /// evaluate cell-boundary crossings at the write site without plumbing the grid through every call layer. <c>null</c> for non-spatial archetypes.</summary>
    internal SpatialGrid Grid;

    /// <summary>
    /// When <c>true</c>, the engine treats <c>ClusterRef.WriteSpatial</c> as the canonical (and only) writer of this archetype's spatial component. Enables two
    /// fence-time optimizations:
    /// <list type="bullet">
    ///   <item><c>DatabaseEngine.DetectClusterMigrations</c> skips its legacy dirtyBits scan
    ///         (step (b)) — all migrations are expected to come from <see cref="ClusterMigrationPendingSlots"/>.</item>
    ///   <item><see cref="RecomputeDirtyClusterAabbs"/> iterates <see cref="ClusterProcessBitmap"/>
    ///         (sparse) instead of <see cref="ActiveClusterIds"/> (full).</item>
    /// </list>
    /// Setting this on an archetype whose spatial field is mutated via raw <c>GetSpan</c> / <c>OpenMut + Write</c> will cause those mutations to be invisible
    /// to the engine's spatial maintenance — only set when you've migrated ALL spatial writers to <c>WriteSpatial</c>.
    /// Default <c>false</c>: legacy behaviour (full scan), safe for any caller.
    /// </summary>
    internal bool SpatialBarrierOnly;

    /// <summary>Tick number of the last <see cref="TransitionWakePendingToActive"/> call. Guards against redundant scans
    /// when multiple systems reference the same archetype. Issue #233.</summary>
    private long _lastWakeTransitionTick = -1;

    private ArchetypeClusterState() { }

    /// <summary>Chunk capacity of the primary (non-null) segment.</summary>
    internal int PrimarySegmentCapacity => ClusterSegment?.ChunkCapacity ?? TransientSegment.ChunkCapacity;

    /// <summary>Mark an entity slot as dirty for tick fence processing.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void SetDirty(int clusterChunkId, int slotIndex)
    {
        int entityIndex = clusterChunkId * 64 + slotIndex;
        ClusterDirtyBitmap.Set(entityIndex);

        // Issue #233: if this cluster is sleeping, request a deferred wake. The null check on SleepStates is the zero-cost bypass for non-spatial archetypes.
        // The byte read + compare is branch-predicted not-taken for Active clusters (common case). Race: parallel workers may see stale state — false negative
        // means one extra tick of sleep (dirty bit still records the writes); false positive is a harmless duplicate request.
        if (SleepStates != null && clusterChunkId < SleepStates.Length && SleepStates[clusterChunkId] == ClusterSleepState.Sleeping)
        {
            DormancyReporter.RequestWake(ArchetypeId, clusterChunkId);
        }
    }

    /// <summary>
    /// Engine-internal non-generic entry point for f32 AABB queries against the per-cell cluster spatial index (issue #230 Phase 3). Mirrors the game-facing
    /// generic entry point <see cref="ClusterSpatialQuery{TArch}.AABB{TBox}"/> but without the <c>TArch</c> compile-time type — consumers that iterate cluster
    /// archetypes at runtime (<c>SpatialTriggerSystem</c>, <c>SpatialInterestSystem</c>, <c>EcsQuery</c>) use this overload directly. Both entry points return
    /// the same <see cref="AabbClusterEnumerator"/> and therefore share a single state machine. Handles both 2D and 3D cluster archetype storage tiers — 2D
    /// callers pass <see cref="float.NegativeInfinity"/> / <see cref="float.PositiveInfinity"/> for the Z bounds to trivially satisfy the Z overlap test
    /// against 2D cluster storage.
    /// </summary>
    /// <param name="grid">The engine's spatial grid. Passed explicitly rather than stored on the state because the grid is a <see cref="DatabaseEngine"/>-owned
    /// singleton and the state has no other reason to hold a reference to it.</param>
    /// <param name="minX">Query bounds min-X.</param>
    /// <param name="minY">Query bounds min-Y.</param>
    /// <param name="minZ">Query bounds min-Z. For 2D queries against a 2D cluster archetype, pass <see cref="float.NegativeInfinity"/>.</param>
    /// <param name="maxX">Query bounds max-X.</param>
    /// <param name="maxY">Query bounds max-Y.</param>
    /// <param name="maxZ">Query bounds max-Z. For 2D queries against a 2D cluster archetype, pass <see cref="float.PositiveInfinity"/>.</param>
    /// <param name="categoryMask">Category bitmask; a cluster is skipped if its union mask does not intersect. Pass <see cref="uint.MaxValue"/> to accept all.</param>
    /// <remarks>
    /// This method does not validate <see cref="ClusterSpatialSlot.HasSpatialIndex"/> — the enumerator returns an empty result set naturally when the per-cell
    /// index is null or empty. Callers that want to skip the work entirely (to avoid constructing a dead enumerator) should check <c>HasSpatialIndex</c>
    /// themselves first. This matches the ergonomics the existing cluster-archetype iteration loops in <c>SpatialTriggerSystem</c> and <c>SpatialInterestSystem</c>
    /// expect.
    /// </remarks>
    public AabbClusterEnumerator QueryAabb(SpatialGrid grid, float minX, float minY, float minZ, float maxX, float maxY, float maxZ,
        uint categoryMask = uint.MaxValue) => new(this, grid, minX, minY, minZ, maxX, maxY, maxZ, categoryMask);

    /// <summary>
    /// Radius (sphere) query against the per-cell cluster spatial index (issue #230 Phase 3). Returns an enumerator over every entity whose tight AABB is
    /// within <paramref name="radius"/> of the query center, using the closest-point-on-AABB semantic that matches the legacy
    /// <see cref="SpatialRTree{T}.QueryRadius"/>. The enumerator drives the broadphase with the sphere's enclosing AABB and applies the sphere distance
    /// check at narrowphase. <see cref="ClusterSpatialQueryResult.DistanceSq"/> is populated on each hit.
    /// </summary>
    /// <param name="grid">The engine's spatial grid.</param>
    /// <param name="centerX">Sphere center X.</param>
    /// <param name="centerY">Sphere center Y.</param>
    /// <param name="centerZ">Sphere center Z. For 2D archetypes, this parameter is ignored — the Z bounds of the query AABB are set to infinity so the
    /// Z overlap test trivially passes against 2D entities.</param>
    /// <param name="radius">Sphere radius in world units.</param>
    /// <param name="categoryMask">Category bitmask; <c>0</c> means "no filter".</param>
    public AabbClusterEnumerator QueryRadius(SpatialGrid grid, float centerX, float centerY, float centerZ, float radius, uint categoryMask = uint.MaxValue)
    {
        float minX = centerX - radius;
        float minY = centerY - radius;
        float maxX = centerX + radius;
        float maxY = centerY + radius;
        bool is3D = SpatialSlot.FieldInfo.FieldType == SpatialFieldType.AABB3F || SpatialSlot.FieldInfo.FieldType == SpatialFieldType.BSphere3F;
        float minZ = is3D ? centerZ - radius : float.NegativeInfinity;
        float maxZ = is3D ? centerZ + radius : float.PositiveInfinity;
        float effectiveCenterZ = is3D ? centerZ : 0f;
        return new AabbClusterEnumerator(this, grid, minX, minY, minZ, maxX, maxY, maxZ, categoryMask, radius * radius, centerX, centerY, effectiveCenterZ);
    }

    /// <summary>
    /// k-nearest-neighbor query against the per-cell cluster spatial index (issue #230 Phase 3). Collects up to <paramref name="k"/> entities whose tight
    /// AABBs are closest to the query center, sorted by distance ascending. Returns the number of valid entries written to <paramref name="results"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Simple implementation.</b> Runs a <see cref="QueryRadius"/> query with a grid-spanning radius, collects every candidate (entityId, distSq) into
    /// a managed list, runs a partial selection sort to find the top k by distance, and writes them to the caller buffer. Correct for any k; performance
    /// degrades when the grid is large and the query center is not near a cluster. Iterative radius expansion is an optimization follow-up tracked under
    /// sub-issues of #228.
    /// </para>
    /// <para>
    /// Allocates one <see cref="System.Collections.Generic.List{T}"/> per call for the candidate buffer. Not intended for per-tick hot loops.
    /// </para>
    /// </remarks>
    public int QueryNearest(SpatialGrid grid, float centerX, float centerY, float centerZ, int k, Span<(long entityId, float distSq)> results, 
        uint categoryMask = uint.MaxValue)
    {
        if (k <= 0 || results.Length == 0)
        {
            return 0;
        }

        int targetCount = Math.Min(k, results.Length);

        // Generous radius: use float.MaxValue so no entity is excluded by the broadphase. QueryRadius clamps the AABB to the
        // grid's world bounds, so this won't allocate more cells than exist. Using float.MaxValue instead of the grid diagonal
        // ensures 3D archetypes with entities far along the Z axis are also captured.
        float maxRadius = float.MaxValue;

        var scratch = new List<(long entityId, float distSq)>(64);
        foreach (var hit in QueryRadius(grid, centerX, centerY, centerZ, maxRadius, categoryMask))
        {
            scratch.Add((hit.EntityId, hit.DistanceSq));
        }

        // Partial selection sort for top k by distance ascending. O(k × n) — fine for small k.
        int n = scratch.Count;
        int resultCount = Math.Min(targetCount, n);
        for (int i = 0; i < resultCount; i++)
        {
            int minIdx = i;
            float minDist = scratch[i].distSq;
            for (int j = i + 1; j < n; j++)
            {
                if (scratch[j].distSq < minDist)
                {
                    minIdx = j;
                    minDist = scratch[j].distSq;
                }
            }
            if (minIdx != i)
            {
                (scratch[i], scratch[minIdx]) = (scratch[minIdx], scratch[i]);
            }
            results[i] = scratch[i];
        }
        return resultCount;
    }

    /// <summary>
    /// Create a new ArchetypeClusterState for a cluster-eligible archetype (fresh database).
    /// </summary>
    /// <param name="layout">Precomputed cluster layout (shared by both segments).</param>
    /// <param name="segment">PersistentStore backing segment for SV+V components. Null for pure-Transient archetypes.</param>
    /// <param name="transientSegment">TransientStore backing segment for Transient components. Default (null) if no Transient.</param>
    /// <param name="transientStore">TransientStore instance to keep alive. Null if no Transient.</param>
    public static ArchetypeClusterState Create(ArchetypeClusterInfo layout, ChunkBasedSegment<PersistentStore> segment,
        ChunkBasedSegment<TransientStore> transientSegment = null, TransientStore? transientStore = null)
    {
        Debug.Assert(segment != null || transientSegment != null, "At least one cluster segment must be provided");
        int capacity = segment?.ChunkCapacity ?? transientSegment.ChunkCapacity;
        return new ArchetypeClusterState
        {
            ClusterSegment = segment,
            TransientSegment = transientSegment,
            TransientClusterStore = transientStore,
            Layout = layout,
            ActiveClusterIds = new int[16],
            ActiveClusterCount = 0,
            FreeClusterHead = -1,
            // Index = clusterChunkId * 64 + slotIndex. The 64 multiplier is fixed (not cluster size N)
            // because it aligns each cluster to exactly one bitmap word for O(1) per-cluster dirty scan.
            ClusterDirtyBitmap = new DirtyBitmap(Math.Max(64, capacity * 64)),
        };
    }

    /// <summary>
    /// Create an ArchetypeClusterState from an existing persisted segment (database reopen).
    /// Scans cluster occupancy bitmaps to rebuild <see cref="ActiveClusterIds"/> and <see cref="FreeClusterHead"/>.
    /// </summary>
    public static ArchetypeClusterState CreateFromExisting(ArchetypeClusterInfo layout, ChunkBasedSegment<PersistentStore> segment,
        ChunkBasedSegment<TransientStore> transientSegment = null, TransientStore? transientStore = null)
    {
        Debug.Assert(segment != null || transientSegment != null, "At least one cluster segment must be provided");
        int capacity = segment?.ChunkCapacity ?? transientSegment.ChunkCapacity;
        var state = new ArchetypeClusterState
        {
            ClusterSegment = segment,
            TransientSegment = transientSegment,
            TransientClusterStore = transientStore,
            Layout = layout,
            ActiveClusterIds = new int[16],
            ActiveClusterCount = 0,
            FreeClusterHead = -1,
            // Index = clusterChunkId * 64 + slotIndex. The 64 multiplier is fixed (not cluster size N)
            // because it aligns each cluster to exactly one bitmap word for O(1) per-cluster dirty scan.
            ClusterDirtyBitmap = new DirtyBitmap(Math.Max(64, capacity * 64)),
        };

        state.RebuildActiveList();
        return state;
    }

    /// <summary>
    /// Scan all allocated chunks in the segment, read OccupancyBits, and rebuild <see cref="ActiveClusterIds"/>,
    /// <see cref="ActiveClusterCount"/>, and <see cref="FreeClusterHead"/> from persisted data.
    /// </summary>
    private void RebuildActiveList()
    {
        ActiveClusterCount = 0;
        FreeClusterHead = -1;

        // Scan primary segment (PersistentStore for mixed/SV, TransientStore for pure-Transient)
        if (ClusterSegment != null)
        {
            var accessor = ClusterSegment.CreateChunkAccessor();
            try
            {
                ScanActiveChunks(ref accessor, ClusterSegment.ChunkCapacity);
            }
            finally
            {
                accessor.Dispose();
            }
        }
        else if (TransientSegment != null)
        {
            var accessor = TransientSegment.CreateChunkAccessor();
            try
            {
                ScanActiveChunksTransient(ref accessor, TransientSegment.ChunkCapacity);
            }
            finally
            {
                accessor.Dispose();
            }
        }
    }

    private void ScanActiveChunks(ref ChunkAccessor<PersistentStore> accessor, int capacity)
    {
        for (int chunkId = 1; chunkId < capacity; chunkId++)
        {
            if (!ClusterSegment.IsChunkAllocated(chunkId))
            {
                continue;
            }

            byte* clusterBase = accessor.GetChunkAddress(chunkId);
            ulong occupancy = *(ulong*)clusterBase;

            if (occupancy == 0)
            {
                continue;
            }

            AddToActiveList(chunkId);

            if (FreeClusterHead < 0 && (~occupancy & Layout.FullMask) != 0)
            {
                FreeClusterHead = chunkId;
            }
        }
    }

    private void ScanActiveChunksTransient(ref ChunkAccessor<TransientStore> accessor, int capacity)
    {
        for (int chunkId = 1; chunkId < capacity; chunkId++)
        {
            if (!TransientSegment.IsChunkAllocated(chunkId))
            {
                continue;
            }

            byte* clusterBase = accessor.GetChunkAddress(chunkId);
            ulong occupancy = *(ulong*)clusterBase;

            if (occupancy == 0)
            {
                continue;
            }

            AddToActiveList(chunkId);

            if (FreeClusterHead < 0 && (~occupancy & Layout.FullMask) != 0)
            {
                FreeClusterHead = chunkId;
            }
        }
    }

    /// <summary>
    /// Claim a free slot in an existing cluster, or allocate a new cluster.
    /// Returns the cluster chunk ID and the slot index within the cluster.
    /// </summary>
    /// <remarks>
    /// <para>Uses CAS on OccupancyBits for correctness under future concurrent commit scenarios.
    /// FinalizeSpawns is single-writer (no concurrent commit), so CAS always succeeds on first try.</para>
    /// <para>The OccupancyBit is set immediately by this method. The caller MUST write component data and EntityKey before the next iteration boundary to
    /// maintain the invariant that occupied slots contain valid data.</para>
    /// </remarks>
    public (int clusterChunkId, int slotIndex) ClaimSlot(ref ChunkAccessor<PersistentStore> accessor, ChangeSet changeSet)
    {
        // Try existing cluster with free slots (O(1) when FreeClusterHead is valid)
        if (FreeClusterHead >= 0)
        {
            int clusterId = FreeClusterHead;
            byte* clusterBase = accessor.GetChunkAddress(clusterId, true);
            ref ulong occupancy = ref *(ulong*)clusterBase;

            ulong current = occupancy;
            ulong available = ~current & Layout.FullMask;
            if (available != 0)
            {
                int slot = BitOperations.TrailingZeroCount(available);
                ulong desired = current | (1UL << slot);

                // CAS for future-proof concurrent commit. Single-writer (no concurrent commit).
                if (Interlocked.CompareExchange(ref occupancy, desired, current) == current)
                {
                    // If cluster is now full, reset head — next call allocates new (O(1))
                    if (desired == Layout.FullMask)
                    {
                        FreeClusterHead = -1;
                    }

                    return (clusterId, slot);
                }

                // CAS failed (concurrent writer took a different slot) — reread once
                current = occupancy;
                available = ~current & Layout.FullMask;
                if (available != 0)
                {
                    slot = BitOperations.TrailingZeroCount(available);
                    desired = current | (1UL << slot);
                    occupancy = desired; // Direct write — single-writer (no concurrent commit)
                    if (desired == Layout.FullMask)
                    {
                        FreeClusterHead = -1;
                    }

                    return (clusterId, slot);
                }
            }

            // Current free cluster is actually full — reset and fall through to allocate
            FreeClusterHead = -1;
        }

        // No free clusters — allocate new one (O(1))
        int newClusterId = AllocateNewCluster(changeSet);
        byte* newBase = accessor.GetChunkAddress(newClusterId, true);

        // Claim slot 0 in the fresh cluster
        *(ulong*)newBase = 1UL; // OccupancyBit 0 set
        FreeClusterHead = Layout.ClusterSize > 1 ? newClusterId : -1;

        return (newClusterId, 0);
    }

    /// <summary>
    /// Claim a free slot for pure-Transient archetypes (no PersistentStore segment).
    /// Same logic as the PersistentStore overload but using TransientStore accessor.
    /// </summary>
    public (int clusterChunkId, int slotIndex) ClaimSlot(ref ChunkAccessor<TransientStore> accessor)
    {
        if (FreeClusterHead >= 0)
        {
            int clusterId = FreeClusterHead;
            byte* clusterBase = accessor.GetChunkAddress(clusterId, true);
            ref ulong occupancy = ref *(ulong*)clusterBase;

            ulong current = occupancy;
            ulong available = ~current & Layout.FullMask;
            if (available != 0)
            {
                int slot = BitOperations.TrailingZeroCount(available);
                ulong desired = current | (1UL << slot);

                if (Interlocked.CompareExchange(ref occupancy, desired, current) == current)
                {
                    if (desired == Layout.FullMask)
                    {
                        FreeClusterHead = -1;
                    }
                    return (clusterId, slot);
                }

                current = occupancy;
                available = ~current & Layout.FullMask;
                if (available != 0)
                {
                    slot = BitOperations.TrailingZeroCount(available);
                    desired = current | (1UL << slot);
                    occupancy = desired;
                    if (desired == Layout.FullMask)
                    {
                        FreeClusterHead = -1;
                    }
                    return (clusterId, slot);
                }
            }

            FreeClusterHead = -1;
        }

        int newClusterId = AllocateNewCluster(null);
        byte* newBase = accessor.GetChunkAddress(newClusterId, true);
        *(ulong*)newBase = 1UL;
        FreeClusterHead = Layout.ClusterSize > 1 ? newClusterId : -1;

        return (newClusterId, 0);
    }

    // ══════════════════════════════════════════════════════════════════════════════
    // Phase 1+2 of issue #229 — spatially coherent slot claiming. Only used when the
    // engine has a configured SpatialGrid AND this archetype has a spatial field.
    // All entities in a given cluster will share the same grid cell.
    // ══════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Attempt to claim one free slot in <paramref name="clusterChunkId"/>. Returns the slot index, or <c>-1</c> if the cluster is full. Shared by both
    /// scan phases of both <see cref="ClaimSlotInCell"/> overloads — collapses what would otherwise be four copies of the dirty-aware claim block.
    /// </summary>
    /// <remarks>
    /// The cluster is first read with <c>dirty:false</c> — the scan must not dirty full clusters it skips (that would inflate ActiveChunkWriters /
    /// ChangeSet / writeback pressure for nothing). Only once a free slot is confirmed is the cluster re-fetched with <c>dirty:true</c>: this raises
    /// ActiveChunkWriters BEFORE the occupancy mutation (ACW-before-write invariant) and is an MRU cache hit. The CAS commit plus its retry loop keeps
    /// concurrent claimants on the same cluster safe (single-writer today; parallel-fence migration paths can hit the same dst cluster from cell-partitioned
    /// workers). The <c>dirty:true</c> re-fetch does not move the chunk, so the <c>occupancy</c> ref taken from the first fetch stays valid.
    /// </remarks>
    private int TryClaimSlotInCluster<TStore>(ref ChunkAccessor<TStore> accessor, int clusterChunkId)
        where TStore : struct, IPageStore
    {
        byte* clusterBase = accessor.GetChunkAddress(clusterChunkId);
        ref ulong occupancy = ref *(ulong*)clusterBase;

        ulong current = occupancy;
        ulong available = ~current & Layout.FullMask;
        if (available == 0)
        {
            return -1;
        }

        // Free slot found — dirty the page before mutating occupancy (ACW-before-write). MRU hit: clusterChunkId was just read above.
        accessor.GetChunkAddress(clusterChunkId, true);

        while (true)
        {
            int slot = BitOperations.TrailingZeroCount(available);
            ulong desired = current | (1UL << slot);
            if (Interlocked.CompareExchange(ref occupancy, desired, current) == current)
            {
                return slot;
            }

            // CAS lost to a concurrent writer — re-read occupancy and retry. Cluster already dirtied above; the dirty mark is idempotent.
            current = occupancy;
            available = ~current & Layout.FullMask;
            if (available == 0)
            {
                return -1;
            }
        }
    }

    /// <summary>
    /// Claim a free slot in a cluster belonging to the given spatial <paramref name="cellKey"/>, allocating a new cluster attached to the cell if none of
    /// its existing clusters has a free slot.
    /// </summary>
    /// <remarks>
    /// <para>This is the spatial-aware counterpart of <see cref="ClaimSlot"/>. Unlike <c>ClaimSlot</c> it ignores <see cref="FreeClusterHead"/> — that hint
    /// is a global free-slot cache that cannot distinguish cells, so it's useless once spatial coherence is required. Instead, we scan this archetype's
    /// own cluster list for the target cell (typically ≤80 entries for AntHill-scale density, ≤15-30 ns scan cost).</para>
    /// <para>Under the Q10 resolution the scanned list is strictly this archetype's — other spatial archetypes sharing the grid have their own
    /// <see cref="CellClusterPool"/> instances, so no cross-archetype cluster chunk IDs ever appear in this scan.</para>
    /// <para>Every successful claim bumps the global <see cref="CellState.EntityCount"/>. Allocation of a new cluster additionally bumps the global
    /// <see cref="CellState.ClusterCount"/>, appends the cluster to this archetype's per-cell claim list, and records the mapping in
    /// <see cref="ClusterCellMap"/>.</para>
    /// </remarks>
    public (int clusterChunkId, int slotIndex) ClaimSlotInCell(int cellKey, ref ChunkAccessor<PersistentStore> accessor, ChangeSet changeSet, SpatialGrid grid)
    {
        ref var cell = ref grid.GetCell(cellKey);
        var clusters = CellClusterPool.GetClusters(cellKey);

        // Scan this archetype's existing clusters attached to this cell for a free slot. The scan is split into two phases around the per-cell cursor — the
        // logical index of the first cluster that might still have a free slot. Phase 1 walks [scanStart, len): clusters fill front-to-back, so for an
        // append-only spawn this is O(1) amortized (the cursor sits on the sole non-full tail cluster). Phase 2 walks [0, scanStart) and runs ONLY when
        // phase 1 found nothing — it self-heals a stale-high cursor (cross-tick swap-with-last drift in RemoveCluster, or a slot freed behind the cursor by
        // a parallel-migration release which — unlike serial destroy — deliberately does NOT reset the cursor). Phase 1 ∪ phase 2 cover the whole list, so a
        // new cluster is allocated only when every existing cluster is genuinely full. This makes the cursor a pure hint: stale values cost a redundant
        // scan, never a missed free slot.
        int scanStart = CellClusterPool.GetScanCursor(cellKey);
        if (scanStart > clusters.Length)
        {
            scanStart = clusters.Length;
        }

        // Phase 1 — forward scan from the cursor. firstNonFull tracks the contiguous full-cluster prefix so the cursor only advances past clusters that are
        // genuinely full (a non-full cluster earlier in the scan pins it — the cursor must never skip a cluster that still has a free slot).
        int firstNonFull = scanStart;
        for (int i = scanStart; i < clusters.Length; i++)
        {
            int clusterId = clusters[i];
            int slot = TryClaimSlotInCluster(ref accessor, clusterId);
            if (slot < 0)
            {
                if (i == firstNonFull)
                {
                    firstNonFull = i + 1;
                }
                continue;
            }
            CellClusterPool.AdvanceScanCursor(cellKey, firstNonFull);
            Interlocked.Increment(ref cell.EntityCount);
            return (clusterId, slot);
        }

        // Phase 2 — scan of the [0, scanStart) prefix the cursor skipped. Reached only when phase 1 found no free slot. On success the cursor is moved
        // BACKWARD (SetScanCursor, not the monotonic AdvanceScanCursor) to this phase's own contiguous-full prefix, so subsequent claims start in the
        // reclaimed region instead of re-walking the now-full tail. Safe as a plain write — the cell is worker-exclusive on the migration path and
        // single-threaded on spawn/destroy.
        int prefixFirstNonFull = 0;
        for (int i = 0; i < scanStart; i++)
        {
            int clusterId = clusters[i];
            int slot = TryClaimSlotInCluster(ref accessor, clusterId);
            if (slot < 0)
            {
                if (i == prefixFirstNonFull)
                {
                    prefixFirstNonFull = i + 1;
                }
                continue;
            }
            CellClusterPool.SetScanCursor(cellKey, prefixFirstNonFull);
            Interlocked.Increment(ref cell.EntityCount);
            return (clusterId, slot);
        }

        // No free slot in any cluster of this cell — allocate a new cluster and attach it to this archetype's per-cell claim list.
        // Slow path: protected by the per-archetype finalize latch. Three operations must be atomic w.r.t. other workers:
        //   (1) Dual-segment AllocateChunk — ClusterSegment + TransientSegment must return matching chunk IDs (lockstep).
        //       Worker interleave would mismatch them and crash the Debug.Assert.
        //   (2) AddToActiveList — appends to ActiveClusterIds[], increments ActiveClusterCount, bumps ClusterSetVersion.
        //   (3) CellClusterPool.AddCluster + ClusterCellMap[newChunkId] = cellKey — per-cell pool mutation + back-pointer.
        // These all happen here. The hot path (existing-cluster CAS above) does NOT take this lock.
        int newChunkId;
        ref WaitContext nullCtx0 = ref Unsafe.NullRef<WaitContext>();
        _finalizeLock.Lock.EnterExclusiveAccess(ref nullCtx0);
        try
        {
            newChunkId = AllocateNewCluster(changeSet);
            EnsureClusterCellMapCapacity(newChunkId + 1);
            ClusterCellMap[newChunkId] = cellKey;
            CellClusterPool.AddCluster(cellKey, newChunkId);
            // The fresh cluster is appended at the end of the cell list and is the only one with free slots — point the cursor at it so the next claim
            // skips straight to it instead of re-scanning the now-full prefix.
            CellClusterPool.AdvanceScanCursor(cellKey, CellClusterPool.GetClusterCount(cellKey) - 1);
        }
        finally
        {
            _finalizeLock.Lock.ExitExclusiveAccess();
        }

        // Cell counters use Interlocked unconditionally (other archetypes sharing this grid may bump them too).
        Interlocked.Increment(ref cell.ClusterCount);
        Interlocked.Increment(ref cell.EntityCount);

        byte* newBase = accessor.GetChunkAddress(newChunkId, true);
        *(ulong*)newBase = 1UL; // occupancy bit 0

        // Phase 3: Spatial:Grid:ClusterCellAssign instant — fired when a new cluster is bound to a cell.
        TyphonEvent.EmitSpatialGridClusterCellAssign(newChunkId, cellKey, (ushort)Math.Min(ArchetypeId, ushort.MaxValue));
        return (newChunkId, 0);
    }

    /// <summary>
    /// Pure-Transient overload of <see cref="ClaimSlotInCell"/>. Identical logic, different accessor type.
    /// </summary>
    public (int clusterChunkId, int slotIndex) ClaimSlotInCell(int cellKey, ref ChunkAccessor<TransientStore> accessor, SpatialGrid grid)
    {
        ref var cell = ref grid.GetCell(cellKey);
        var clusters = CellClusterPool.GetClusters(cellKey);

        // Two-phase cursor scan — see the PersistentStore overload above for the full rationale (O(M²) re-scan collapse, phase-2 self-heal, hint semantics).
        int scanStart = CellClusterPool.GetScanCursor(cellKey);
        if (scanStart > clusters.Length)
        {
            scanStart = clusters.Length;
        }

        // Phase 1 — forward scan from the cursor.
        int firstNonFull = scanStart;
        for (int i = scanStart; i < clusters.Length; i++)
        {
            int clusterId = clusters[i];
            int slot = TryClaimSlotInCluster(ref accessor, clusterId);
            if (slot < 0)
            {
                if (i == firstNonFull)
                {
                    firstNonFull = i + 1;
                }
                continue;
            }
            CellClusterPool.AdvanceScanCursor(cellKey, firstNonFull);
            Interlocked.Increment(ref cell.EntityCount);
            return (clusterId, slot);
        }

        // Phase 2 — prefix scan, reached only when phase 1 found nothing. On success the cursor is moved backward to the reclaimed region.
        int prefixFirstNonFull = 0;
        for (int i = 0; i < scanStart; i++)
        {
            int clusterId = clusters[i];
            int slot = TryClaimSlotInCluster(ref accessor, clusterId);
            if (slot < 0)
            {
                if (i == prefixFirstNonFull)
                {
                    prefixFirstNonFull = i + 1;
                }
                continue;
            }
            CellClusterPool.SetScanCursor(cellKey, prefixFirstNonFull);
            Interlocked.Increment(ref cell.EntityCount);
            return (clusterId, slot);
        }

        // No free slot — allocate a new cluster and attach it to this archetype's per-cell claim list.
        // See PersistentStore overload above for the rationale on locking this slow path.
        int newChunkId;
        ref WaitContext nullCtx1 = ref Unsafe.NullRef<WaitContext>();
        _finalizeLock.Lock.EnterExclusiveAccess(ref nullCtx1);
        try
        {
            newChunkId = AllocateNewCluster(null);
            EnsureClusterCellMapCapacity(newChunkId + 1);
            ClusterCellMap[newChunkId] = cellKey;
            CellClusterPool.AddCluster(cellKey, newChunkId);
            // Point the cursor at the fresh cluster — see the PersistentStore overload.
            CellClusterPool.AdvanceScanCursor(cellKey, CellClusterPool.GetClusterCount(cellKey) - 1);
        }
        finally
        {
            _finalizeLock.Lock.ExitExclusiveAccess();
        }

        Interlocked.Increment(ref cell.ClusterCount);
        Interlocked.Increment(ref cell.EntityCount);

        byte* newBase = accessor.GetChunkAddress(newChunkId, true);
        *(ulong*)newBase = 1UL;

        // Phase 3: Spatial:Grid:ClusterCellAssign instant — fired when a new cluster is bound to a cell.
        TyphonEvent.EmitSpatialGridClusterCellAssign(newChunkId, cellKey, (ushort)Math.Min(ArchetypeId, ushort.MaxValue));
        return (newChunkId, 0);
    }

    /// <summary>
    /// Reconstruct <see cref="ClusterCellMap"/> and the grid's per-cell state from the current active clusters' entity positions. Called at startup for
    /// spatial archetypes after the <see cref="SpatialGrid"/> is configured — on a fresh database this is a no-op (no active clusters); on a reopened
    /// database it re-derives the cluster→cell mapping from persisted data.
    /// </summary>
    /// <remarks>
    /// <para>Reads the first occupied entity's spatial field from each active cluster and uses
    /// <see cref="SpatialGrid.WorldToCellKeyFromSpatialField"/> to compute its cell. This relies on the spatial coherence invariant (all entities in a
    /// cluster belong to the same cell) — reading only the first entity is sufficient.</para>
    /// <para>Non-spatial archetypes and archetypes without a configured grid are no-ops. Pure-Transient archetypes are also skipped since their data doesn't
    /// survive restart.</para>
    /// <para><b>Precondition — NOT idempotent on a dirty grid.</b> This method ADDS to <see cref="CellState.EntityCount"/> /
    /// <see cref="CellState.ClusterCount"/> and appends cluster IDs to this archetype's <see cref="CellClusterPool"/>. Callers MUST pass either a
    /// fresh <see cref="SpatialGrid"/> or one that has been reset via <see cref="SpatialGrid.ResetCellState"/> (and the per-archetype pools must also be
    /// reset) — calling twice without a reset double-counts entities and duplicates cluster IDs in the pool. The single caller today
    /// (<c>DatabaseEngine.InitializeArchetypes</c>) constructs a fresh grid + allocates a fresh per-archetype pool inside <see cref="InitializeSpatial"/>
    /// immediately before this loop, satisfying the precondition.</para>
    /// </remarks>
    public void RebuildCellState(SpatialGrid grid)
    {
        if (grid == null || !SpatialSlot.HasSpatialIndex || ClusterSegment == null)
        {
            return;
        }
        if (ActiveClusterCount == 0)
        {
            return;
        }

        EnsureClusterCellMapCapacity(PrimarySegmentCapacity);
        Array.Fill(ClusterCellMap, -1);

        var ss = SpatialSlot;
        int componentOffset = Layout.ComponentOffset(ss.Slot);
        int compStride = Layout.ComponentSize(ss.Slot);
        var fieldType = ss.FieldInfo.FieldType;

        var clusterAccessor = ClusterSegment.CreateChunkAccessor();
        try
        {
            for (int i = 0; i < ActiveClusterCount; i++)
            {
                int chunkId = ActiveClusterIds[i];
                byte* clusterBase = clusterAccessor.GetChunkAddress(chunkId);
                ulong occupancy = *(ulong*)clusterBase;
                if (occupancy == 0)
                {
                    continue;
                }

                int firstSlot = BitOperations.TrailingZeroCount(occupancy);
                byte* fieldPtr = clusterBase + componentOffset + firstSlot * compStride + ss.FieldOffset;
                int cellKey = grid.WorldToCellKeyFromSpatialField(fieldPtr, fieldType);

                ClusterCellMap[chunkId] = cellKey;
                CellClusterPool.AddCluster(cellKey, chunkId);
                ref var cell = ref grid.GetCell(cellKey);
                cell.ClusterCount++;
                cell.EntityCount += BitOperations.PopCount(occupancy);
            }
        }
        finally
        {
            clusterAccessor.Dispose();
        }
    }

    /// <summary>
    /// Grow <see cref="ClusterCellMap"/> to hold at least <paramref name="requiredLength"/> entries, initializing new slots to <c>-1</c> (unmapped).
    /// Called lazily by <see cref="ClaimSlotInCell"/> when a new cluster chunk ID lands beyond the current bounds.
    /// </summary>
    internal void EnsureClusterCellMapCapacity(int requiredLength)
    {
        if (ClusterCellMap == null)
        {
            int initial = Math.Max(16, requiredLength);
            ClusterCellMap = new int[initial];
            Array.Fill(ClusterCellMap, -1);
            return;
        }
        if (ClusterCellMap.Length >= requiredLength)
        {
            return;
        }
        // Defensive: if ClusterCellMap.Length is ever 0 (shouldn't happen through normal
        // construction — we always allocate >= 16 — but a future constructor path could regress)
        // start the doubling from 1 instead of 0 to avoid an infinite loop.
        int newLen = Math.Max(ClusterCellMap.Length, 1);
        while (newLen < requiredLength)
        {
            newLen *= 2;
        }
        int oldLen = ClusterCellMap.Length;
        Array.Resize(ref ClusterCellMap, newLen);
        Array.Fill(ClusterCellMap, -1, oldLen, newLen - oldLen);
    }

    /// <summary>
    /// Grow <see cref="ClusterAabbs"/> to hold at least <paramref name="requiredLength"/> entries. Issue #230.
    /// New slots are left at <see cref="ClusterSpatialAabb.Empty"/> (neutral seed for subsequent unions).
    /// </summary>
    internal void EnsureClusterAabbsCapacity(int requiredLength)
    {
        if (ClusterAabbs == null)
        {
            int initial = Math.Max(16, requiredLength);
            ClusterAabbs = new ClusterSpatialAabb[initial];
            for (int i = 0; i < initial; i++)
            {
                ClusterAabbs[i] = ClusterSpatialAabb.Empty;
            }
            return;
        }
        if (ClusterAabbs.Length >= requiredLength)
        {
            return;
        }
        int newLen = Math.Max(ClusterAabbs.Length, 1);
        while (newLen < requiredLength)
        {
            newLen *= 2;
        }
        int oldLen = ClusterAabbs.Length;
        Array.Resize(ref ClusterAabbs, newLen);
        for (int i = oldLen; i < newLen; i++)
        {
            ClusterAabbs[i] = ClusterSpatialAabb.Empty;
        }
    }

    /// <summary>
    /// Grow <see cref="ClusterSpatialIndexSlot"/> to hold at least <paramref name="requiredLength"/> entries, initializing new slots to <c>-1</c> (not in
    /// the per-cell index). Issue #230.
    /// </summary>
    internal void EnsureClusterSpatialIndexSlotCapacity(int requiredLength)
    {
        if (ClusterSpatialIndexSlot == null)
        {
            int initial = Math.Max(16, requiredLength);
            ClusterSpatialIndexSlot = new int[initial];
            Array.Fill(ClusterSpatialIndexSlot, -1);
            return;
        }
        if (ClusterSpatialIndexSlot.Length >= requiredLength)
        {
            return;
        }
        int newLen = Math.Max(ClusterSpatialIndexSlot.Length, 1);
        while (newLen < requiredLength)
        {
            newLen *= 2;
        }
        int oldLen = ClusterSpatialIndexSlot.Length;
        Array.Resize(ref ClusterSpatialIndexSlot, newLen);
        Array.Fill(ClusterSpatialIndexSlot, -1, oldLen, newLen - oldLen);
    }

    /// <summary>
    /// Grow the four write-time bookkeeping arrays — <see cref="ClusterProcessBitmap"/>,
    /// <see cref="ClusterMigrationPendingSlots"/>, <see cref="ClusterMigrationDestCellKeys"/>,
    /// <see cref="ClusterShrinkPendingAxes"/> — in lockstep. Called alongside
    /// <see cref="EnsureClusterAabbsCapacity"/> so the four arrays are always sized to match the cluster segment's chunk-id range.
    /// </summary>
    internal void EnsureClusterWriteBookkeepingCapacity(int requiredLength)
    {
        // ClusterProcessBitmap: 1 bit per cluster → (requiredLength + 63) / 64 long words.
        int requiredWords = (requiredLength + 63) >> 6;
        if (ClusterProcessBitmap == null)
        {
            int initialWords = Math.Max(1, requiredWords);
            ClusterProcessBitmap = new long[initialWords];
        }
        else if (ClusterProcessBitmap.Length < requiredWords)
        {
            int newLen = Math.Max(ClusterProcessBitmap.Length, 1);
            while (newLen < requiredWords)
            {
                newLen *= 2;
            }

            Array.Resize(ref ClusterProcessBitmap, newLen);
            // No init — Array.Resize zero-fills.
        }

        // Per-cluster arrays sized 1:1 with clusterChunkId range.
        if (ClusterMigrationPendingSlots == null)
        {
            int initial = Math.Max(16, requiredLength);
            ClusterMigrationPendingSlots = new ulong[initial];
            ClusterMigrationDestCellKeys = new int[initial];
            Array.Fill(ClusterMigrationDestCellKeys, -1);
            ClusterShrinkPendingAxes = new byte[initial];
            return;
        }
        if (ClusterMigrationPendingSlots.Length >= requiredLength)
        {
            return;
        }
        int newClusterLen = Math.Max(ClusterMigrationPendingSlots.Length, 1);
        while (newClusterLen < requiredLength)
        {
            newClusterLen *= 2;
        }

        int oldLen = ClusterMigrationPendingSlots.Length;
        Array.Resize(ref ClusterMigrationPendingSlots, newClusterLen);
        Array.Resize(ref ClusterMigrationDestCellKeys, newClusterLen);
        Array.Fill(ClusterMigrationDestCellKeys, -1, oldLen, newClusterLen - oldLen);
        Array.Resize(ref ClusterShrinkPendingAxes, newClusterLen);
    }

    /// <summary>
    /// Grow <see cref="PerCellIndex"/> to hold at least <paramref name="requiredLength"/> entries. New slots are left <c>null</c> —
    /// each <see cref="PerCellSpatialSlot"/> is lazily allocated on first cluster insertion into that cell via <see cref="AddClusterToPerCellIndex"/>.
    /// Issue #230.
    /// </summary>
    internal void EnsurePerCellIndexCapacity(int requiredLength)
    {
        if (PerCellIndex == null)
        {
            int initial = Math.Max(16, requiredLength);
            PerCellIndex = new PerCellSpatialSlot[initial];
            return;
        }
        if (PerCellIndex.Length >= requiredLength)
        {
            return;
        }
        int newLen = Math.Max(PerCellIndex.Length, 1);
        while (newLen < requiredLength)
        {
            newLen *= 2;
        }
        Array.Resize(ref PerCellIndex, newLen);
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Issue #233: Dormancy capacity + core logic
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Grow <see cref="SleepStates"/> and <see cref="SleepCounters"/> to hold at least <paramref name="requiredLength"/> entries.
    /// New entries initialize to <see cref="ClusterSleepState.Active"/> / 0. Issue #233.
    /// </summary>
    internal void EnsureSleepStateCapacity(int requiredLength)
    {
        if (SleepStates == null || SleepCounters == null)
        {
            return; // Dormancy not enabled for this archetype
        }
        if (SleepStates.Length >= requiredLength)
        {
            return;
        }
        int newLen = Math.Max(SleepStates.Length, 1);
        while (newLen < requiredLength)
        {
            newLen *= 2;
        }
        // SleepStates: new entries default to 0 = Active (Array.Resize zero-fills)
        Array.Resize(ref SleepStates, newLen);
        // SleepCounters: new entries default to 0 (Array.Resize zero-fills)
        Array.Resize(ref SleepCounters, newLen);
    }

    /// <summary>
    /// Advance sleep counters for all active clusters and transition idle clusters to <see cref="ClusterSleepState.Sleeping"/>.
    /// Also handles heartbeat wake for already-sleeping clusters. Called single-threaded from <c>WriteClusterTickFence</c>
    /// after migrations and AABB recomputation. Issue #233.
    /// </summary>
    /// <param name="dirtyBits">Occupancy-masked dirty bitmap snapshot from the tick fence. Word index = chunkId.
    /// A nonzero word means at least one entity in that cluster was written this tick.</param>
    /// <param name="tickNumber">Current tick number, used for heartbeat staggering.</param>
    internal void DormancySweep(long[] dirtyBits, long tickNumber)
    {
        if (SleepStates == null || SleepThresholdTicks <= 0)
        {
            return;
        }

        for (int i = 0; i < ActiveClusterCount; i++)
        {
            int chunkId = ActiveClusterIds[i];
            if (chunkId >= SleepStates.Length)
            {
                continue;
            }

            var state = SleepStates[chunkId];

            if (state == ClusterSleepState.Active)
            {
                // Check dirty bitmap: nonzero word means at least one entity written this tick
                bool dirty = chunkId < dirtyBits.Length && dirtyBits[chunkId] != 0;
                if (dirty)
                {
                    SleepCounters[chunkId] = 0;
                }
                else
                {
                    int counter = SleepCounters[chunkId] + 1;
                    if (counter >= SleepThresholdTicks)
                    {
                        SleepStates[chunkId] = ClusterSleepState.Sleeping;
                        SleepingClusterCount++;
                    }
                    else
                    {
                        SleepCounters[chunkId] = (ushort)counter;
                    }
                }
            }
            else if (state == ClusterSleepState.Sleeping && HeartbeatIntervalTicks > 0)
            {
                // Heartbeat: staggered wake so only ~1/N sleeping clusters wake per tick
                if ((int)(tickNumber % HeartbeatIntervalTicks) == chunkId % HeartbeatIntervalTicks)
                {
                    SleepStates[chunkId] = ClusterSleepState.WakePending;
                    // SleepingClusterCount is decremented when WakePending→Active in TransitionWakePendingToActive
                }
            }
            // WakePending clusters are left alone — they'll transition to Active at tick start.
        }
    }

    /// <summary>
    /// Process a single wake request: if the cluster is <see cref="ClusterSleepState.Sleeping"/>, transition to <see cref="ClusterSleepState.WakePending"/>.
    /// Deduplication is implicit: calling on an already-WakePending cluster is a no-op. Called single-threaded from <c>WriteClusterTickFence</c> after
    /// draining <see cref="DormancyReporter"/>. Issue #233.
    /// </summary>
    internal void ProcessWakeRequest(int chunkId)
    {
        if (SleepStates == null || chunkId >= SleepStates.Length)
        {
            return;
        }
        if (SleepStates[chunkId] == ClusterSleepState.Sleeping)
        {
            SleepStates[chunkId] = ClusterSleepState.WakePending;
            // SleepingClusterCount is decremented in TransitionWakePendingToActive (next tick start)
        }
    }

    /// <summary>
    /// Transition all <see cref="ClusterSleepState.WakePending"/> clusters to <see cref="ClusterSleepState.Active"/>.
    /// Called single-threaded from <c>BuildTierIndexesAtTickStart</c> before tier index rebuild so woken clusters appear in this tick's per-tier lists.
    /// Guarded by <see cref="_lastWakeTransitionTick"/> to avoid redundant scans when multiple systems reference the same archetype. Issue #233.
    /// </summary>
    internal void TransitionWakePendingToActive(long currentTick)
    {
        if (SleepStates == null || _lastWakeTransitionTick == currentTick)
        {
            return;
        }
        _lastWakeTransitionTick = currentTick;

        for (int i = 0; i < ActiveClusterCount; i++)
        {
            int chunkId = ActiveClusterIds[i];
            if (chunkId < SleepStates.Length && SleepStates[chunkId] == ClusterSleepState.WakePending)
            {
                SleepStates[chunkId] = ClusterSleepState.Active;
                SleepCounters[chunkId] = 0;
                SleepingClusterCount--;
            }
        }
    }

    /// <summary>
    /// Recompute the tight 2D AABB and category-mask union of a cluster by scanning its occupied slots. The spatial field is read
    /// via <see cref="SpatialMaintainer.ReadAndValidateBoundsFromPtr"/> which dispatches on the archetype's <see cref="SpatialFieldInfo.FieldType"/>.
    /// Degenerate entities (NaN/Inf bounds) are skipped. Issue #230.
    /// </summary>
    /// <remarks>
    /// Cost: one pass over <see cref="ArchetypeClusterInfo.ClusterSize"/> occupancy bits, ~50-100 ns per occupied entity on the L1-hot common path.
    /// Category mask is the OR of per-entity masks; in Phase 1 all entities use the default <c>uint.MaxValue</c> mask, so this collapses to <c>uint.MaxValue</c>.
    /// </remarks>
    internal ClusterSpatialAabb RecomputeClusterAabb(int clusterChunkId, ref ChunkAccessor<PersistentStore> accessor)
        => RecomputeClusterAabb(clusterChunkId, ref accessor, out _);

    internal ClusterSpatialAabb RecomputeClusterAabb(int clusterChunkId, ref ChunkAccessor<PersistentStore> accessor, out int slotsScanned)
    {
        var ss = SpatialSlot;
        byte* clusterBase = accessor.GetChunkAddress(clusterChunkId);
        ulong occupancy = *(ulong*)clusterBase;
        slotsScanned = BitOperations.PopCount(occupancy);
        int componentOffset = Layout.ComponentOffset(ss.Slot);
        int componentStride = Layout.ComponentSize(ss.Slot);

        var aabb = ClusterSpatialAabb.Empty;
        // 6 doubles covers both 2D ([minX, minY, maxX, maxY]) and 3D ([minX, minY, minZ, maxX, maxY, maxZ]) layouts produced by
        // SpatialMaintainer.ReadAndValidateBoundsFromPtr. The tail slots cost nothing for 2D reads.
        Span<double> coords = stackalloc double[6];
        bool is3D = ss.FieldInfo.FieldType == SpatialFieldType.AABB3F || ss.FieldInfo.FieldType == SpatialFieldType.BSphere3F;

        ulong bits = occupancy;
        while (bits != 0)
        {
            int slot = BitOperations.TrailingZeroCount(bits);
            bits &= bits - 1;

            byte* fieldPtr = clusterBase + componentOffset + slot * componentStride + ss.FieldOffset;
            if (!SpatialMaintainer.ReadAndValidateBoundsFromPtr(fieldPtr, ss.FieldInfo, coords, ss.Descriptor))
            {
                continue; // skip degenerate slot
            }

            if (is3D)
            {
                aabb.Union3F((float)coords[0], (float)coords[1], (float)coords[2], (float)coords[3], (float)coords[4], (float)coords[5], ss.FieldInfo.Category);
            }
            else
            {
                aabb.Union2F((float)coords[0], (float)coords[1], (float)coords[2], (float)coords[3], ss.FieldInfo.Category);
            }
        }

        return aabb;
    }

    /// <summary>
    /// Startup rebuild of per-cluster AABBs from entity positions. Mirrors <see cref="RebuildCellState"/>:
    /// both derive transient state from persistent cluster data on database reopen. Iterates all active clusters, recomputes each AABB, stores it
    /// in <see cref="ClusterAabbs"/>, and adds the cluster to its cell's <see cref="PerCellSpatialSlot.DynamicIndex"/> (lazy-allocated).
    /// Back-pointer recorded in <see cref="ClusterSpatialIndexSlot"/> so subsequent updates are O(1). Issue #230.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Phase 1 supports Dynamic mode only. Static-mode archetypes are skipped — they keep using the existing per-archetype R-Tree path.
    /// </para>
    /// <para>
    /// Precondition: <see cref="RebuildCellState"/> has already run, so <see cref="ClusterCellMap"/> is populated and every active cluster's cell is known.
    /// </para>
    /// </remarks>
    public void RebuildClusterAabbs()
    {
        if (!SpatialSlot.HasSpatialIndex || ClusterSegment == null)
        {
            return;
        }
        // Issue #230 Phase 3 Option B: both Dynamic and Static cluster archetypes rebuild from data on reopen. AddClusterToPerCellIndex (called below) routes
        // to PerCellSpatialSlot.DynamicIndex / StaticIndex based on the archetype's SpatialMode, so the rebuild is mode-agnostic at this level.
        if (ActiveClusterCount == 0)
        {
            return;
        }

        EnsureClusterAabbsCapacity(PrimarySegmentCapacity);
        EnsureClusterSpatialIndexSlotCapacity(PrimarySegmentCapacity);
        EnsureClusterWriteBookkeepingCapacity(PrimarySegmentCapacity);

        // Reset the per-cell index before rebuilding so repeated calls to RebuildClusterAabbs (e.g. a startup reopen of a database that was reopened in the
        // same process) do not double-count clusters that already have entries in the index from a prior spawn/migration path.
        if (PerCellIndex != null)
        {
            Array.Clear(PerCellIndex);
        }
        Array.Fill(ClusterSpatialIndexSlot, -1);

        var clusterAccessor = ClusterSegment.CreateChunkAccessor();
        try
        {
            for (int i = 0; i < ActiveClusterCount; i++)
            {
                int chunkId = ActiveClusterIds[i];
                ClusterSpatialAabb aabb = RecomputeClusterAabb(chunkId, ref clusterAccessor);
                ClusterAabbs[chunkId] = aabb;

                // Add to the per-cell index. The cell key was already written into ClusterCellMap by RebuildCellState. Skip clusters whose cell is unknown
                // (ClusterCellMap[chunkId] == -1) or whose AABB is degenerate (all entities were skipped).
                if (ClusterCellMap == null || chunkId >= ClusterCellMap.Length)
                {
                    continue;
                }
                int cellKey = ClusterCellMap[chunkId];
                if (cellKey < 0)
                {
                    continue;
                }
                if (float.IsPositiveInfinity(aabb.MinX))
                {
                    continue; // empty — no valid entities
                }

                AddClusterToPerCellIndex(chunkId, cellKey, aabb);
            }
        }
        finally
        {
            clusterAccessor.Dispose();
        }
    }

    /// <summary>
    /// Tick-fence pass: re-tighten cluster AABBs and propagate to the per-cell index. Now driven by the write-time bookkeeping arrays
    /// (<see cref="ClusterProcessBitmap"/>, <see cref="ClusterShrinkPendingAxes"/>) populated by <c>ClusterRef.WriteSpatial</c>.
    /// <para>
    /// For each cluster with its process bit set:
    /// <list type="bullet">
    ///   <item>If <c>ShrinkPendingAxes != 0</c>: an entity at an axis extreme moved inward, so the stored AABB no longer fits — rescan this cluster's occupied
    ///         slots to recompute the tight AABB.</item>
    ///   <item>Otherwise the process bit was set by an inline AABB grow (already applied) or a migration flag — just propagate the (already-current)
    ///         <see cref="ClusterAabbs"/> entry to <c>PerCellIndex.UpdateAt</c>.</item>
    /// </list>
    /// </para>
    /// <para>
    /// All three bookkeeping arrays (<see cref="ClusterProcessBitmap"/>, <see cref="ClusterMigrationPendingSlots"/>, <see cref="ClusterShrinkPendingAxes"/>)
    /// are cleared at the end of the pass. The migration drain is expected to have already happened in <c>DatabaseEngine.DetectClusterMigrations</c> (which
    /// runs immediately before this method).
    /// </para>
    /// <para>
    /// The <paramref name="dirtyBits"/> parameter is retained for API stability; this method no longer reads it.
    /// </para>
    /// </summary>
    internal void RecomputeDirtyClusterAabbs(long[] dirtyBits, ref ChunkAccessor<PersistentStore> accessor, SpatialGrid grid = null)
    {
        _ = dirtyBits;

        if (!SpatialSlot.HasSpatialIndex)
        {
            return;
        }

        if (SpatialSlot.FieldInfo.Mode != SpatialMode.Dynamic)
        {
            return;
        }

        if (ClusterSpatialIndexSlot == null || ClusterAabbs == null)
        {
            return;
        }

        if (PerCellIndex == null || ClusterCellMap == null)
        {
            return;
        }

        // Whole-archetype convenience wrapper: serial WriteTickFence path. The parallel path dispatches RecomputeDirtyClusterAabbsSlice across workers directly
        // and then runs ClearAabbRefreshBookkeeping in Finalize.
        var refreshSpan = TyphonEvent.BeginSpatialClusterAabbRefresh((ushort)ArchetypeId, ActiveClusterCount);
        try
        {
            int totalWork = (SpatialBarrierOnly && ClusterProcessBitmap != null) ? ClusterProcessBitmap.Length : ActiveClusterCount;
            if (totalWork > 0)
            {
                var outlierBuffer = new List<MigrationRequest>(0);
                RecomputeDirtyClusterAabbsSlice(0, totalWork, ref accessor, grid, outlierBuffer, out int aabbsChanged, out int slotsScanned, 
                    out int outlierGuardFires);
                EnqueueMigrationsBulk(outlierBuffer);
                refreshSpan.AabbsChanged = aabbsChanged;
                refreshSpan.SlotsScanned = slotsScanned;
                refreshSpan.OutlierGuardFires = outlierGuardFires;
            }
            ClearAabbRefreshBookkeeping();
        }
        finally
        {
            refreshSpan.Dispose();
        }
    }

    /// <summary>
    /// Apply the AABB recompute pass to a contiguous slice of this archetype's clusters. Safe to call concurrently across DISJOINT slices of the SAME archetype
    /// (used by the parallel-fence AabbRefresh phase).
    /// <para>
    /// Slicing axis depends on iteration mode (captured from <see cref="SpatialBarrierOnly"/>):
    /// <list type="bullet">
    ///   <item><b>BarrierOnly</b>: slice <see cref="ClusterProcessBitmap"/> by word range. <paramref name="sliceStart"/>=startWord,
    ///         <paramref name="sliceCount"/>=wordCount. Each word's bits are disjoint cluster chunk-IDs so no two slices touch the same cluster.</item>
    ///   <item><b>Legacy</b>: slice <see cref="ActiveClusterIds"/> by index range. <paramref name="sliceStart"/>=activeIdx,
    ///         <paramref name="sliceCount"/>=count. Each slice owns a disjoint range of active-list indices.</item>
    /// </list>
    /// </para>
    /// <para>
    /// <b>Thread-safety</b>: writes only to per-cluster slots (<see cref="ClusterAabbs"/>[chunkId]) and per-cell index slots
    /// (<c>PerCellIndex[cellKey].DynamicIndex.UpdateAt(indexSlot, ...)</c>). Different clusters always have different <c>indexSlot</c>s even within the same
    /// cell, so SoA writes don't collide. The rare <see cref="FlagOutliersForMigration"/> path (extent-guard fire) serializes <see cref="EnqueueMigration"/>
    /// internally via <c>_finalizeLock</c>.
    /// </para>
    /// </summary>
    internal void RecomputeDirtyClusterAabbsSlice(int sliceStart, int sliceCount, ref ChunkAccessor<PersistentStore> accessor, SpatialGrid grid, 
        List<MigrationRequest> outlierBuffer, out int aabbsChanged, out int slotsScanned, out int outlierGuardFires)
    {
        aabbsChanged = 0;
        slotsScanned = 0;
        outlierGuardFires = 0;

        if (!SpatialSlot.HasSpatialIndex)
        {
            return;
        }

        if (SpatialSlot.FieldInfo.Mode != SpatialMode.Dynamic)
        {
            return;
        }

        if (ClusterSpatialIndexSlot == null || ClusterAabbs == null)
        {
            return;
        }

        if (PerCellIndex == null || ClusterCellMap == null)
        {
            return;
        }

        if (sliceCount <= 0)
        {
            return;
        }

        float maxExtent = 0f;
        float cellSize = 0f;
        bool outlierGuardActive = grid != null && (cellSize = grid.Config.CellSize) > 0f;
        if (outlierGuardActive)
        {
            maxExtent = cellSize * 1.2f;
        }

        if (SpatialBarrierOnly && ClusterProcessBitmap != null)
        {
            int wordEnd = Math.Min(sliceStart + sliceCount, ClusterProcessBitmap.Length);
            for (int wordIdx = sliceStart; wordIdx < wordEnd; wordIdx++)
            {
                long word = ClusterProcessBitmap[wordIdx];
                if (word == 0)
                {
                    continue;
                }

                while (word != 0)
                {
                    int chunkId = (wordIdx << 6) + BitOperations.TrailingZeroCount((ulong)word);
                    word &= word - 1;

                    if (chunkId >= ClusterSpatialIndexSlot.Length)
                    {
                        continue;
                    }

                    int indexSlot = ClusterSpatialIndexSlot[chunkId];
                    if (indexSlot < 0)
                    {
                        continue;
                    }

                    if (chunkId >= ClusterCellMap.Length)
                    {
                        continue;
                    }

                    int cellKey = ClusterCellMap[chunkId];
                    if (cellKey < 0)
                    {
                        continue;
                    }

                    var slot = PerCellIndex[cellKey];
                    if (slot == null || slot.DynamicIndex == null)
                    {
                        continue;
                    }

                    byte shrinkMask = ClusterShrinkPendingAxes != null && chunkId < ClusterShrinkPendingAxes.Length ? ClusterShrinkPendingAxes[chunkId] : (byte)0;

                    ref var stored = ref ClusterAabbs[chunkId];
                    ClusterSpatialAabb fresh;
                    if (shrinkMask != 0)
                    {
                        fresh = RecomputeClusterAabb(chunkId, ref accessor, out int clusterSlots);
                        slotsScanned += clusterSlots;
                        if (float.IsPositiveInfinity(fresh.MinX))
                        {
                            continue;
                        }
                    }
                    else
                    {
                        fresh = stored;
                        if (float.IsPositiveInfinity(fresh.MinX))
                        {
                            continue;
                        }
                    }

                    if (stored.MinX == fresh.MinX && stored.MinY == fresh.MinY && stored.MinZ == fresh.MinZ &&
                        stored.MaxX == fresh.MaxX && stored.MaxY == fresh.MaxY && stored.MaxZ == fresh.MaxZ && shrinkMask == 0)
                    {
                        continue;
                    }

                    fresh.CategoryMask = slot.DynamicIndex.CategoryMasks[indexSlot];
                    stored = fresh;
                    slot.DynamicIndex.UpdateAt(indexSlot, in fresh);
                    aabbsChanged++;
                    TyphonEvent.EmitSpatialCellIndexUpdate(cellKey, indexSlot);

                    if (outlierGuardActive && ((fresh.MaxX - fresh.MinX) > maxExtent || (fresh.MaxY - fresh.MinY) > maxExtent))
                    {
                        outlierGuardFires++;
                        FlagOutliersForMigration(chunkId, cellKey, grid, ref accessor, outlierBuffer);
                    }
                }
            }
        }
        else
        {
            int activeEnd = Math.Min(sliceStart + sliceCount, ActiveClusterCount);
            for (int activeIdx = sliceStart; activeIdx < activeEnd; activeIdx++)
            {
                int chunkId = ActiveClusterIds[activeIdx];

                if (chunkId >= ClusterSpatialIndexSlot.Length)
                {
                    continue;
                }

                int indexSlot = ClusterSpatialIndexSlot[chunkId];
                if (indexSlot < 0)
                {
                    continue;
                }

                if (chunkId >= ClusterCellMap.Length)
                {
                    continue;
                }

                int cellKey = ClusterCellMap[chunkId];
                if (cellKey < 0)
                {
                    continue;
                }

                var slot = PerCellIndex[cellKey];
                if (slot == null || slot.DynamicIndex == null)
                {
                    continue;
                }

                ClusterSpatialAabb fresh = RecomputeClusterAabb(chunkId, ref accessor, out int clusterSlots);
                slotsScanned += clusterSlots;
                if (float.IsPositiveInfinity(fresh.MinX))
                {
                    continue;
                }

                ref var stored = ref ClusterAabbs[chunkId];
                if (stored.MinX == fresh.MinX && stored.MinY == fresh.MinY && stored.MinZ == fresh.MinZ &&
                    stored.MaxX == fresh.MaxX && stored.MaxY == fresh.MaxY && stored.MaxZ == fresh.MaxZ)
                {
                    continue;
                }

                fresh.CategoryMask = slot.DynamicIndex.CategoryMasks[indexSlot];
                stored = fresh;
                slot.DynamicIndex.UpdateAt(indexSlot, in fresh);
                aabbsChanged++;
                TyphonEvent.EmitSpatialCellIndexUpdate(cellKey, indexSlot);

                if (outlierGuardActive && ((fresh.MaxX - fresh.MinX) > maxExtent || (fresh.MaxY - fresh.MinY) > maxExtent))
                {
                    outlierGuardFires++;
                    FlagOutliersForMigration(chunkId, cellKey, grid, ref accessor, outlierBuffer);
                }
            }
        }
    }

    /// <summary>
    /// Count the clusters actually represented by an AABB-refresh slice. Used for the per-slice telemetry span (<c>ClusterScanned</c> field). Legacy mode:
    /// <paramref name="sliceCount"/> directly. Barrier mode: popcount of the slice's bitmap words.
    /// </summary>
    internal int CountClustersInAabbSlice(int sliceStart, int sliceCount)
    {
        if (sliceCount <= 0)
        {
            return 0;
        }

        if (SpatialBarrierOnly && ClusterProcessBitmap != null)
        {
            int end = Math.Min(sliceStart + sliceCount, ClusterProcessBitmap.Length);
            int total = 0;
            for (int w = sliceStart; w < end; w++)
            {
                total += BitOperations.PopCount((ulong)ClusterProcessBitmap[w]);
            }
            return total;
        }
        return Math.Min(sliceCount, Math.Max(0, ActiveClusterCount - sliceStart));
    }

    /// <summary>
    /// Clear the write-time bookkeeping arrays (<see cref="ClusterProcessBitmap"/>, <see cref="ClusterMigrationPendingSlots"/>,
    /// <see cref="ClusterShrinkPendingAxes"/>) for the next tick. Single-threaded — called once per archetype from
    /// <see cref="DatabaseEngine.FinalizeArchetypeFence"/> after all AABB slices finished.
    /// </summary>
    internal void ClearAabbRefreshBookkeeping()
    {
        if (ClusterProcessBitmap == null)
        {
            return;
        }

        for (int wordIdx = 0; wordIdx < ClusterProcessBitmap.Length; wordIdx++)
        {
            long word = ClusterProcessBitmap[wordIdx];
            if (word == 0)
            {
                continue;
            }

            while (word != 0)
            {
                int chunkId = (wordIdx << 6) + BitOperations.TrailingZeroCount((ulong)word);
                word &= word - 1;
                if (ClusterMigrationPendingSlots != null && chunkId < ClusterMigrationPendingSlots.Length)
                {
                    ClusterMigrationPendingSlots[chunkId] = 0;
                    ClusterMigrationDestCellKeys[chunkId] = -1;
                }
                if (ClusterShrinkPendingAxes != null && chunkId < ClusterShrinkPendingAxes.Length)
                {
                    ClusterShrinkPendingAxes[chunkId] = 0;
                }
            }
            ClusterProcessBitmap[wordIdx] = 0;
        }
    }

    /// <summary>
    /// Safety valve for the "Max Cluster AABB Extent" invariant from design doc 01-spatial-clusters.md (issue #230 Phase 3 closure of Phase 1 gap). Scans a
    /// cluster whose recomputed AABB has grown beyond <c>cellSize × 1.2</c> and enqueues migration for any entity that has drifted outside the current cell's
    /// raw bounds. This bypasses the hysteresis dead zone that <c>DatabaseEngine.DetectClusterMigrations</c> normally honors — the point is
    /// exactly to force-migrate entities that the hysteresis had absorbed individually but whose accumulated drift is degrading the cluster's spatial
    /// coherence.
    /// </summary>
    /// <remarks>
    /// Rare path. Runs inside <see cref="RecomputeDirtyClusterAabbs"/> only when the extent check fires — well-behaved workloads never hit it. The enqueued
    /// migrations are drained on the next tick (not this one), because this runs AFTER <see cref="DatabaseEngine.ExecuteMigrations"/> in the tick fence
    /// order. That one-tick lag is the "safety valve, not a common case" note from the design doc.
    /// </remarks>
    private void FlagOutliersForMigration(int clusterChunkId, int cellKey, SpatialGrid grid, ref ChunkAccessor<PersistentStore> accessor,
        List<MigrationRequest> outlierBuffer)
    {
        var ss = SpatialSlot;
        byte* clusterBase = accessor.GetChunkAddress(clusterChunkId);
        ulong occupancy = *(ulong*)clusterBase;
        int compOffset = Layout.ComponentOffset(ss.Slot);
        int compStride = Layout.ComponentSize(ss.Slot);

        var (cellX, cellY) = grid.CellKeyToCoords(cellKey);
        ref readonly var cfg = ref grid.Config;
        float cellMinX = cfg.WorldMin.X + cellX * cfg.CellSize;
        float cellMinY = cfg.WorldMin.Y + cellY * cfg.CellSize;
        float cellMaxX = cellMinX + cfg.CellSize;
        float cellMaxY = cellMinY + cfg.CellSize;

        ulong bits = occupancy;
        while (bits != 0)
        {
            int slotIndex = BitOperations.TrailingZeroCount(bits);
            bits &= bits - 1;

            byte* fieldPtr = clusterBase + compOffset + slotIndex * compStride + ss.FieldOffset;
            SpatialGrid.ReadSpatialCenter2D(fieldPtr, ss.FieldInfo.FieldType, out float posX, out float posY);

            if (!float.IsFinite(posX) || !float.IsFinite(posY))
            {
                continue; // defensive — non-finite positions should have been rejected upstream
            }

            // Raw cell boundary (no hysteresis) — force migrate anything outside.
            if (posX < cellMinX || posX > cellMaxX || posY < cellMinY || posY > cellMaxY)
            {
                int newCellKey = grid.WorldToCellKey(posX, posY);
                if (newCellKey != cellKey)
                {
                    // Worker-local buffer: caller bulk-appends under _finalizeLock once at slice end. Avoids per-entity lock acquisition (review D-2).
                    // For serial callers (RecomputeDirtyClusterAabbs whole-archetype wrapper), the buffer is appended without contention.
                    outlierBuffer.Add(new MigrationRequest(clusterChunkId, slotIndex, newCellKey));
                }
            }
        }
    }

    /// <summary>
    /// Add a cluster to its cell's <see cref="PerCellSpatialSlot"/> — routed to <see cref="PerCellSpatialSlot.DynamicIndex"/> for Dynamic archetypes and
    /// <see cref="PerCellSpatialSlot.StaticIndex"/> for Static archetypes. Lazily allocates the slot and index as needed. Records the back-pointer in
    /// <see cref="ClusterSpatialIndexSlot"/> for O(1) subsequent updates. Issue #230.
    /// </summary>
    internal void AddClusterToPerCellIndex(int clusterChunkId, int cellKey, in ClusterSpatialAabb aabb)
    {
        EnsurePerCellIndexCapacity(cellKey + 1);
        EnsureClusterSpatialIndexSlotCapacity(clusterChunkId + 1);
        EnsureClusterWriteBookkeepingCapacity(clusterChunkId + 1);

        var slot = PerCellIndex[cellKey];
        if (slot == null)
        {
            slot = new PerCellSpatialSlot();
            PerCellIndex[cellKey] = slot;
        }

        bool isStatic = SpatialSlot.FieldInfo.Mode == SpatialMode.Static;
        if (isStatic)
        {
            if (slot.StaticIndex == null)
            {
                slot.StaticIndex = new CellSpatialIndex();
            }
            int indexSlot = slot.StaticIndex.Add(clusterChunkId, aabb);
            ClusterSpatialIndexSlot[clusterChunkId] = indexSlot;
            TyphonEvent.EmitSpatialCellIndexAdd(cellKey, indexSlot, clusterChunkId, slot.StaticIndex.Capacity);
        }
        else
        {
            if (slot.DynamicIndex == null)
            {
                slot.DynamicIndex = new CellSpatialIndex();
            }
            int indexSlot = slot.DynamicIndex.Add(clusterChunkId, aabb);
            ClusterSpatialIndexSlot[clusterChunkId] = indexSlot;
            TyphonEvent.EmitSpatialCellIndexAdd(cellKey, indexSlot, clusterChunkId, slot.DynamicIndex.Capacity);
        }
    }

    /// <summary>
    /// Remove a cluster from its cell's <see cref="PerCellSpatialSlot"/>. Routes to Static or Dynamic based on the archetype's
    /// <see cref="SpatialFieldInfo.Mode"/>. Fixes up the back-pointer of any cluster that was swapped into the removed slot by the SoA swap-with-last.
    /// Clears <see cref="ClusterSpatialIndexSlot"/> for the removed cluster. Issue #230.
    /// </summary>
    internal void RemoveClusterFromPerCellIndex(int clusterChunkId, int cellKey)
    {
        if (PerCellIndex == null || cellKey < 0 || cellKey >= PerCellIndex.Length)
        {
            return;
        }
        var slot = PerCellIndex[cellKey];
        if (slot == null)
        {
            return;
        }
        if (ClusterSpatialIndexSlot == null || clusterChunkId >= ClusterSpatialIndexSlot.Length)
        {
            return;
        }
        int indexSlot = ClusterSpatialIndexSlot[clusterChunkId];
        if (indexSlot < 0)
        {
            return; // not in the index
        }

        bool isStatic = SpatialSlot.FieldInfo.Mode == SpatialMode.Static;
        CellSpatialIndex targetIndex = isStatic ? slot.StaticIndex : slot.DynamicIndex;
        if (targetIndex == null)
        {
            return;
        }

        int swappedClusterId = targetIndex.RemoveAt(indexSlot);
        TyphonEvent.EmitSpatialCellIndexRemove(cellKey, indexSlot, swappedClusterId);
        if (swappedClusterId >= 0 && swappedClusterId < ClusterSpatialIndexSlot.Length)
        {
            // The swapped cluster now lives at indexSlot; fix its back-pointer.
            ClusterSpatialIndexSlot[swappedClusterId] = indexSlot;
        }
        ClusterSpatialIndexSlot[clusterChunkId] = -1;
    }

    /// <summary>
    /// Append a migration request to the per-archetype queue. Lazily allocates the backing array on first use
    /// and doubles its capacity on overflow. Issue #229 Phase 3.
    /// </summary>
    /// <remarks>
    /// Called only from the cell-crossing detection loop in <c>DetectClusterMigrations</c> — single-threaded,
    /// no synchronization needed. The typical hot path writes a handful of entries per tick; even on a busy tick
    /// with thousands of migrations the array doubles ~10-12 times total (initial 16 -> 32K).
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void EnqueueMigration(int sourceClusterChunkId, int sourceSlotIndex, int destCellKey)
    {
        if (PendingMigrations == null)
        {
            PendingMigrations = new MigrationRequest[16];
        }
        else if (PendingMigrationCount == PendingMigrations.Length)
        {
            Array.Resize(ref PendingMigrations, PendingMigrations.Length * 2);
        }
        PendingMigrations[PendingMigrationCount++] = new MigrationRequest(sourceClusterChunkId, sourceSlotIndex, destCellKey);
    }

    /// <summary>
    /// Bulk-append a worker-local outlier-buffer to <see cref="PendingMigrations"/>. Takes <see cref="_finalizeLock"/> once per slice (review D-2).
    /// Empty buffer = no-op, no lock acquisition.
    /// </summary>
    internal void EnqueueMigrationsBulk(List<MigrationRequest> outlierBuffer)
    {
        if (outlierBuffer == null || outlierBuffer.Count == 0)
        {
            return;
        }

        ref WaitContext nullCtx = ref Unsafe.NullRef<WaitContext>();
        _finalizeLock.Lock.EnterExclusiveAccess(ref nullCtx);
        try
        {
            int n = outlierBuffer.Count;
            if (PendingMigrations == null)
            {
                int initCap = Math.Max(16, n);
                int p = 1;
                while (p < initCap)
                {
                    p <<= 1;
                }

                PendingMigrations = new MigrationRequest[p];
            }
            else if (PendingMigrationCount + n > PendingMigrations.Length)
            {
                int newLen = PendingMigrations.Length * 2;
                while (newLen < PendingMigrationCount + n)
                {
                    newLen *= 2;
                }

                Array.Resize(ref PendingMigrations, newLen);
            }
            for (int i = 0; i < n; i++)
            {
                PendingMigrations[PendingMigrationCount++] = outlierBuffer[i];
            }
        }
        finally
        {
            _finalizeLock.Lock.ExitExclusiveAccess();
        }
        outlierBuffer.Clear();
    }

    /// <summary>
    /// Allocate a new cluster from both segments (lockstep). Initializes to zero and adds to active list.
    /// </summary>
    public int AllocateNewCluster(ChangeSet changeSet)
    {
        int chunkId;
        if (ClusterSegment != null)
        {
            chunkId = ClusterSegment.AllocateChunk(true, changeSet);
        }
        else
        {
            // Pure-Transient: allocate from TransientStore only
            chunkId = TransientSegment.AllocateChunk(true);
        }

        // Dual-segment: allocate matching chunk in TransientSegment (lockstep ensures same chunk IDs)
        if (TransientSegment != null && ClusterSegment != null)
        {
            int transientChunkId = TransientSegment.AllocateChunk(true);
            Debug.Assert(transientChunkId == chunkId, $"Dual-segment chunk ID mismatch: PS={chunkId}, TS={transientChunkId}");
        }

        AddToActiveList(chunkId);
        return chunkId;
    }

    /// <summary>Add a cluster chunk ID to the active list.</summary>
    public void AddToActiveList(int chunkId)
    {
        if (ActiveClusterCount >= ActiveClusterIds.Length)
        {
            Array.Resize(ref ActiveClusterIds, ActiveClusterIds.Length * 2);
        }
        ActiveClusterIds[ActiveClusterCount++] = chunkId;
        // Issue #231: any change to the active cluster set invalidates the tier index.
        ClusterSetVersion++;
        // Issue #233: ensure dormancy arrays cover the new chunkId, initialize to Active/0.
        if (SleepStates != null)
        {
            EnsureSleepStateCapacity(chunkId + 1);
            SleepStates[chunkId] = ClusterSleepState.Active;
            SleepCounters[chunkId] = 0;
        }
    }

    /// <summary>Remove a cluster chunk ID from the active list (swap-with-last, O(1)).</summary>
    public void RemoveFromActiveList(int chunkId)
    {
        for (int i = 0; i < ActiveClusterCount; i++)
        {
            if (ActiveClusterIds[i] == chunkId)
            {
                // Issue #233: if the removed cluster was sleeping or wake-pending, adjust the count.
                // WakePending clusters are still counted in SleepingClusterCount (they were incremented at the
                // Active→Sleeping transition and decremented only when WakePending→Active completes).
                if (SleepStates != null && chunkId < SleepStates.Length)
                {
                    var sleepState = SleepStates[chunkId];
                    if (sleepState == ClusterSleepState.Sleeping || sleepState == ClusterSleepState.WakePending)
                    {
                        SleepingClusterCount--;
                    }
                }

                ActiveClusterIds[i] = ActiveClusterIds[ActiveClusterCount - 1];
                ActiveClusterCount--;

                // If the removed cluster was the free head, reset
                if (FreeClusterHead == chunkId)
                {
                    FreeClusterHead = -1;
                }

                // Issue #231: any change to the active cluster set invalidates the tier index.
                ClusterSetVersion++;
                return;
            }
        }
    }

    /// <summary>
    /// Release one occupied slot of a Versioned/Transient mixed cluster on the persistent segment. Atomically clears the slot's OccupancyBit + EnabledBits + EntityKey
    /// via <see cref="ClearSlotMetadata"/>, maintains the per-cell entity counter when the slot was actually occupied, and — if the slot was the cluster's last occupant —
    /// either finalises the cluster immediately or defers finalisation to the per-tick fence depending on <paramref name="deferFinalize"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Slot-level work.</b> <see cref="ClearSlotMetadata"/> returns the occupancy bitmap as it was BEFORE the clear, so the method can tell (a) whether the
    /// slot was actually occupied (no-op release on a free slot is silently absorbed — no cell-count decrement, no drain handling) and (b) whether clearing
    /// this single bit transitioned the cluster from non-empty to empty in one observation. Cell-entity bookkeeping
    /// (<see cref="DecrementCellEntityCountOnRelease"/>) fires only on the genuinely-was-occupied path to keep <see cref="CellState.EntityCount"/>
    /// consistent with the occupancy bitmaps under repeated-release idempotence.
    /// </para>
    /// <para>
    /// <b>Drain branches.</b> When this release drains the cluster (last bit cleared), the cluster must exit the active set, get removed from its cell's pool
    /// segment, and have its chunks returned to both <see cref="ClusterSegment"/> and <see cref="TransientSegment"/>. Two paths:
    /// <list type="bullet">
    ///   <item><b><paramref name="deferFinalize"/> = false</b> (default — single-threaded callers like <c>Transaction.Destroy</c>): finalise immediately.
    ///     Safe because no concurrent claimer can CAS a slot back in between our last-bit-clear and the segment free.</item>
    ///   <item><b><paramref name="deferFinalize"/> = true</b> (parallel-fence migration path, review C-1): record the drained cluster via
    ///     <see cref="RecordClusterDrain"/> and let <c>FinalizeArchetypeFence</c> do the finalize+free pass after all workers have quiesced. Skipping immediate
    ///     free closes the race where another worker mid-<c>ClaimSlotInCell</c> has already CAS-claimed a slot in this cluster between our last-bit-clear and
    ///     any subsequent lock — finalising now would free a chunk the claimer is about to write into.</item>
    /// </list>
    /// </para>
    /// <para>
    /// <b>Free-list hint.</b> On a release that does NOT drain the cluster, the cluster still has free capacity and is a good candidate for the next claim. If
    /// <see cref="FreeClusterHead"/> is currently unset (-1), it's biased to this cluster so the next <c>ClaimSlotInCell</c> hits an O(1) lookup. This is a
    /// hint only — the claim path validates the head still has a free bit before using it.
    /// </para>
    /// <para>
    /// <b>Threading.</b> Caller owns the writer mutex for this <see cref="ArchetypeClusterState"/> when <paramref name="deferFinalize"/> = false. When deferred,
    /// the caller is the parallel fence path where workers operate on disjoint clusters; <see cref="RecordClusterDrain"/> uses
    /// <see cref="System.Threading.Interlocked.Increment"/> to reserve a slot in the per-archetype drain list, so concurrent drain records are safe.
    /// </para>
    /// </remarks>
    /// <param name="accessor">Chunk accessor bound to the <see cref="PersistentStore"/> segment — provides the in-memory address of the cluster chunk for
    /// direct metadata mutation.</param>
    /// <param name="clusterChunkId">Chunk id of the cluster containing the slot to release.</param>
    /// <param name="slotIndex">Zero-based index of the slot within the cluster (0..<see cref="ArchetypeClusterInfo.ClusterSize"/>-1). The slot's occupancy bit,
    /// enabled bits, and entity key are all cleared.</param>
    /// <param name="changeSet">Change set threaded through for WAL / dirty-page bookkeeping on the persistent segment writes performed
    /// by <see cref="ClearSlotMetadata"/>.</param>
    /// <param name="grid">
    /// Optional spatial grid. When non-null <em>and</em> <see cref="ClusterCellMap"/> is populated for the released cluster, this method maintains the cell
    /// descriptor: <see cref="CellState.EntityCount"/> always decrements (only on genuinely-was-occupied releases), and a going-empty cluster is removed
    /// from its cell's pool segment at finalise time. Pass <c>null</c> when the archetype has no spatial slot — the cell bookkeeping is then a no-op.
    /// </param>
    /// <param name="deferFinalize">
    /// When <c>true</c>, postpones the drain finalisation (cell removal + active-list eviction + segment free) so the per-tick fence can run it after the
    /// parallel migration pass completes. Set by the cluster-migration / parallel-fence call sites; default <c>false</c> for single-threaded callers
    /// (<c>Transaction.Destroy</c>, etc.) that can safely finalise inline.
    /// </param>
    public void ReleaseSlot(ref ChunkAccessor<PersistentStore> accessor, int clusterChunkId, int slotIndex, ChangeSet changeSet, SpatialGrid grid = null,
        bool deferFinalize = false)
    {
        byte* clusterBase = accessor.GetChunkAddress(clusterChunkId, true);

        // Release SV ComponentCollection buffers held in this slot BEFORE clearing it — but only on a true destroy.
        // Migration passes deferFinalize:true and is a MOVE: the handle was byte-copied to the destination slot, so the
        // buffer must NOT be freed here. SV CC has no revision chain; the cluster slot is the buffer's sole owner.
        if (!deferFinalize && CollectionSlots != null)
        {
            ReleaseSlotCollections(clusterBase, slotIndex, changeSet);
        }

        ulong slotMask = 1UL << slotIndex;
        ulong prevOccupancy = ClearSlotMetadata(clusterBase, slotIndex);
        bool wasOccupied = (prevOccupancy & slotMask) != 0;
        bool clusterDrained = wasOccupied && (prevOccupancy & ~slotMask) == 0;

        if (wasOccupied)
        {
            // resetCursor: !deferFinalize — serial releases reset the cursor for immediate reuse; parallel-migration releases skip it (see the method doc).
            DecrementCellEntityCountOnRelease(grid, clusterChunkId, resetCursor: !deferFinalize);
        }

        if (clusterDrained)
        {
            if (deferFinalize)
            {
                // Parallel-fence migration path (review C-1). Defer finalize-and-free to FinalizeArchetypeFence — freeing here would race with a concurrent
                // ClaimSlotInCell that may have just CAS-claimed a slot in this cluster between our last-bit-clear and any lock acquire. The deferred list is
                // per-archetype, slot reservation lock-free via Interlocked.Increment.
                RecordClusterDrain(clusterChunkId);
            }
            else
            {
                // Single-threaded caller (Transaction.Destroy, etc.) — safe to finalize immediately.
                FinaliseEmptyClusterCellState(grid, clusterChunkId);
                RemoveFromActiveList(clusterChunkId);
                ClusterSegment.FreeChunk(clusterChunkId);
                TransientSegment?.FreeChunk(clusterChunkId);
            }
        }
        else if (FreeClusterHead < 0)
        {
            FreeClusterHead = clusterChunkId;
        }
    }

    /// <summary>
    /// Release a slot for pure-Transient archetypes (no PersistentStore segment).
    /// </summary>
    public void ReleaseSlot(ref ChunkAccessor<TransientStore> accessor, int clusterChunkId, int slotIndex, SpatialGrid grid = null, bool deferFinalize = false)
    {
        byte* clusterBase = accessor.GetChunkAddress(clusterChunkId, true);

        ulong slotMask = 1UL << slotIndex;
        ulong prevOccupancy = ClearSlotMetadata(clusterBase, slotIndex);
        bool wasOccupied = (prevOccupancy & slotMask) != 0;
        bool clusterDrained = wasOccupied && (prevOccupancy & ~slotMask) == 0;

        if (wasOccupied)
        {
            // resetCursor: !deferFinalize — serial releases reset the cursor for immediate reuse; parallel-migration releases skip it (see the method doc).
            DecrementCellEntityCountOnRelease(grid, clusterChunkId, resetCursor: !deferFinalize);
        }

        if (clusterDrained)
        {
            if (deferFinalize)
            {
                RecordClusterDrain(clusterChunkId);
            }
            else
            {
                FinaliseEmptyClusterCellState(grid, clusterChunkId);
                RemoveFromActiveList(clusterChunkId);
                TransientSegment.FreeChunk(clusterChunkId);
            }
        }
        else if (FreeClusterHead < 0)
        {
            FreeClusterHead = clusterChunkId;
        }
    }

    /// <summary>
    /// Build the SingleVersion <c>ComponentCollection</c> descriptor (<see cref="CollectionSlots"/>) used by <c>ReleaseSlot</c> to free CC buffers on destroy.
    /// Only SingleVersion CC fields are tracked: the cluster slot is their sole owner. Versioned CC is owned by content chunks (released via the revision
    /// cleanup); Transient CC is rejected at registration. No-op for archetypes without an SV CC field (leaves <see cref="CollectionSlots"/> null).
    /// </summary>
    public void InitializeCollections(ComponentTable[] slotToTable)
    {
        List<ClusterCollectionSlot> slots = null;
        for (int slot = 0; slot < slotToTable.Length; slot++)
        {
            var table = slotToTable[slot];
            if (table == null || table.StorageMode != StorageMode.SingleVersion || !table.HasCollections)
            {
                continue;
            }

            var byOffset = table.ComponentCollectionVSBSByOffset;
            var fields = new ClusterCollectionField[byOffset.Count];
            int fi = 0;
            foreach (var kvp in byOffset)
            {
                // kvp.Key is the offset within the component's pure data; cluster slots have no overhead, so it IS the slot-relative field offset.
                fields[fi++] = new ClusterCollectionField { FieldOffset = kvp.Key, Vsbs = kvp.Value };
            }

            (slots ??= []).Add(new ClusterCollectionSlot { Slot = slot, Fields = fields });
        }

        CollectionSlots = slots?.ToArray();
    }

    /// <summary>
    /// Release the SingleVersion ComponentCollection buffers held in one cluster slot. Called from <c>ReleaseSlot</c> on a true destroy (not migration),
    /// before the slot data is cleared.
    /// </summary>
    private void ReleaseSlotCollections(byte* clusterBase, int slotIndex, ChangeSet changeSet)
    {
        var layout = Layout;
        foreach (var cs in CollectionSlots)
        {
            byte* compBase = clusterBase + layout.ComponentOffset(cs.Slot) + slotIndex * layout.ComponentSize(cs.Slot);
            foreach (var f in cs.Fields)
            {
                int bufferId = *(int*)(compBase + f.FieldOffset);
                if (bufferId != 0)
                {
                    var ca = f.Vsbs.Segment.CreateChunkAccessor(changeSet);
                    f.Vsbs.BufferRelease(bufferId, ref ca);
                    ca.Dispose();
                }
            }
        }
    }

    /// <summary>
    /// Decrement the cell's entity count when a slot is released. No-op if cluster is unmapped.
    /// <para><paramref name="resetCursor"/> controls the scan-cursor reset: on the serial release path (<c>Transaction.Destroy</c>, <c>deferFinalize</c>
    /// false) we reset to 0 so the freed slot is immediately reusable by the next claim. On the parallel-fence migration path (<c>deferFinalize</c> true)
    /// the reset is SKIPPED — releases there touch arbitrary, non-worker-exclusive source cells, so resetting would zero the cursors of destination cells
    /// other workers are actively claiming into (cursor thrash) and pound a shared array (false sharing). Phase-2 of <see cref="ClaimSlotInCell"/> recovers
    /// any slot freed behind the cursor, so skipping the reset costs at most a redundant scan, never a missed free slot.</para>
    /// </summary>
    private void DecrementCellEntityCountOnRelease(SpatialGrid grid, int clusterChunkId, bool resetCursor)
    {
        if (grid == null || ClusterCellMap == null || clusterChunkId >= ClusterCellMap.Length)
        {
            return;
        }
        int cellKey = ClusterCellMap[clusterChunkId];
        if (cellKey < 0)
        {
            return;
        }
        Interlocked.Decrement(ref grid.GetCell(cellKey).EntityCount);

        if (resetCursor)
        {
            // Serial release — a slot just freed up in this cell; reset the scan cursor so the next ClaimSlotInCell re-scans from 0 and immediately reuses
            // the freed slot (or a free slot in a cluster the swap-with-last RemoveCluster shuffled ahead of the old cursor).
            CellClusterPool?.ResetScanCursor(cellKey);
        }
    }

    /// <summary>Detach an empty cluster from this archetype's per-cell claim list and clear its cell mapping.</summary>
    private void FinaliseEmptyClusterCellState(SpatialGrid grid, int clusterChunkId)
    {
        if (grid == null || ClusterCellMap == null || clusterChunkId >= ClusterCellMap.Length)
        {
            return;
        }
        int cellKey = ClusterCellMap[clusterChunkId];
        if (cellKey < 0)
        {
            return;
        }
        // Issue #229 Q10: per-archetype pool removal. Only decrements the global CellState.ClusterCount if the pool actually owned this cluster id.
        // Called inside the per-archetype _finalizeLock (parallel-fence migration path), so the CellClusterPool mutation is serialized — but the cell
        // descriptor counter is shared across archetypes, so it still needs Interlocked.
        if (CellClusterPool.RemoveCluster(cellKey, clusterChunkId))
        {
            Interlocked.Decrement(ref grid.GetCell(cellKey).ClusterCount);
        }

        // Issue #230 Phase 1: also remove from the per-cell cluster AABB index and reset the cluster's stored AABB. Runs before we clear ClusterCellMap so
        // RemoveClusterFromPerCellIndex can look up the cell key internally.
        RemoveClusterFromPerCellIndex(clusterChunkId, cellKey);
        if (ClusterAabbs != null && clusterChunkId < ClusterAabbs.Length)
        {
            ClusterAabbs[clusterChunkId] = ClusterSpatialAabb.Empty;
        }

        ClusterCellMap[clusterChunkId] = -1;
    }

    /// <summary>
    /// Atomically clear EnabledBits, OccupancyBit, and EntityId for a slot (store-agnostic pointer math). Returns the PRE-AND occupancy word so the caller
    /// can detect "this clear flipped the last bit" via <c>(prev &amp; slotMask) != 0 &amp;&amp; (prev &amp; ~slotMask) == 0</c>. The parallel-fence migration
    /// path uses this last-bit-wins signal to decide which worker enters the finalize section under <see cref="_finalizeLock"/>.
    /// </summary>
    /// <remarks>
    /// All bit mutations use <see cref="Interlocked.And(ref long, long)"/> so concurrent releases of different slots in the same cluster (parallel workers
    /// handling cell-partitioned migrations whose sources share a cluster) compose without lost updates. The EntityId scalar write is independent (different
    /// 8-byte slot per release) so it stays a plain store.
    /// </remarks>
    private ulong ClearSlotMetadata(byte* clusterBase, int slotIndex)
    {
        long slotMask = 1L << slotIndex;
        long inverseMask = ~slotMask;

        for (int slot = 0; slot < Layout.ComponentCount; slot++)
        {
            Interlocked.And(ref *(long*)(clusterBase + Layout.EnabledBitsOffset(slot)), inverseMask);
        }

        ulong prevOccupancy = (ulong)Interlocked.And(ref *(long*)clusterBase, inverseMask);

        *(long*)(clusterBase + Layout.EntityIdsOffset + slotIndex * 8) = 0;

        return prevOccupancy;
    }

    /// <summary>
    /// Initialize per-archetype B+Tree index infrastructure from the component tables.
    /// Called after cluster state creation for archetypes with <see cref="ArchetypeMetadata.HasClusterIndexes"/>.
    /// </summary>
    public void InitializeIndexes(ComponentTable[] slotToTable, ChunkBasedSegment<PersistentStore> indexSegment, bool load, ChangeSet changeSet)
    {
        IndexSegment = indexSegment;

        int slotCount = 0;
        for (int slot = 0; slot < slotToTable.Length; slot++)
        {
            // Skip Transient slots — their indexes use per-ComponentTable TransientIndex (BTree<TransientStore>)
            if (slotToTable[slot].StorageMode == StorageMode.Transient)
            {
                continue;
            }
            var infos = slotToTable[slot].IndexedFieldInfos;
            if (infos != null && infos.Length > 0)
            {
                slotCount++;
            }
        }

        IndexSlots = new ClusterIndexSlot[slotCount];
        int idx = 0;
        // Sequential counter for AllowMultiple indexed fields across ALL component slots in this archetype.
        // Drives each field's MultiFieldIndex, which selects the corresponding section in the cluster layout's elementId tail
        // (see ArchetypeClusterInfo.IndexElementIdOffset). Must match the flat count passed to ArchetypeClusterInfo.Compute at archetype registration time.
        int multiFieldCounter = 0;
        for (int slot = 0; slot < slotToTable.Length; slot++)
        {
            var table = slotToTable[slot];
            // Skip Transient slots — indexes maintained per-ComponentTable, not per-archetype
            if (table.StorageMode == StorageMode.Transient)
            {
                continue;
            }
            var infos = table.IndexedFieldInfos;
            if (infos == null || infos.Length == 0)
            {
                continue;
            }

            var fields = new ClusterIndexField[infos.Length];
            var shadowBuffers = new FieldShadowBuffer[infos.Length];

            // Iterate component definition fields to find indexed ones (in stable order matching IndexedFieldInfos)
            int fi = 0;
            for (int i = 0; i < table.Definition.MaxFieldId && fi < infos.Length; i++)
            {
                var fieldDef = table.Definition[i];
                if (fieldDef == null || !fieldDef.HasIndex)
                {
                    continue;
                }

                ref var ifi = ref infos[fi];
                // FieldOffset in cluster = field offset within pure component data (no ComponentOverhead in clusters)
                int clusterFieldOffset = ifi.OffsetToField - table.ComponentOverhead;
                var btree = ComponentTable.CreateIndexForFieldCore(fieldDef, (short)fieldDef.FieldId, load, indexSegment, changeSet);
                // AllowMultiple fields claim the next sequential slot in the cluster's elementId tail.
                // Single-value fields don't allocate tail space and use MultiFieldIndex = -1.
                int multiFieldIndex = ifi.AllowMultiple ? multiFieldCounter++ : -1;
                fields[fi] = new ClusterIndexField
                {
                    FieldOffset = clusterFieldOffset,
                    FieldSize = ifi.Size,
                    Index = btree,
                    AllowMultiple = ifi.AllowMultiple,
                    ZoneMap = new ZoneMapArray(PrimarySegmentCapacity, ifi.Size,
                        fieldDef.Type == FieldType.Float, fieldDef.Type == FieldType.Double,
                        (fieldDef.Type & FieldType.Unsigned) != 0),
                    MultiFieldIndex = multiFieldIndex,
                };
                shadowBuffers[fi] = new FieldShadowBuffer();
                fi++;
            }

            IndexSlots[idx++] = new ClusterIndexSlot
            {
                Slot = slot,
                Fields = fields,
                ShadowBuffers = shadowBuffers,
            };
        }

        // Sanity: the MultiFieldIndex counter must match the count supplied to ArchetypeClusterInfo.Compute.
        // A mismatch means the cluster layout tail is mis-sized or fields will read the wrong slots.
        Debug.Assert(multiFieldCounter == Layout.MultipleIndexedFieldCount,
            $"Cluster elementId tail: InitializeIndexes counted {multiFieldCounter} AllowMultiple fields but Layout reserves {Layout.MultipleIndexedFieldCount}");

        ClusterShadowBitmap = new DirtyBitmap(Math.Max(64, PrimarySegmentCapacity * 64));
    }

    /// <summary>
    /// Rebuild per-archetype B+Tree indexes from cluster data (scan all occupied entities).
    /// Used on reopen when index segment is not persisted or is corrupted.
    /// </summary>
    public void RebuildIndexesFromData(ChangeSet changeSet)
    {
        if (IndexSlots == null || IndexSlots.Length == 0)
        {
            return;
        }

        // Index rebuild reads from primary segment (SV/V data — Transient excluded from IndexSlots)
        var clusterAccessor = ClusterSegment.CreateChunkAccessor();
        var idxAccessor = IndexSegment.CreateChunkAccessor(changeSet);
        try
        {
            for (int c = 0; c < ActiveClusterCount; c++)
            {
                int chunkId = ActiveClusterIds[c];
                byte* clusterBase = clusterAccessor.GetChunkAddress(chunkId);
                ulong occupancy = *(ulong*)clusterBase;

                while (occupancy != 0)
                {
                    int slotIndex = BitOperations.TrailingZeroCount(occupancy);
                    occupancy &= occupancy - 1;
                    int clusterLocation = chunkId * 64 + slotIndex;

                    for (int s = 0; s < IndexSlots.Length; s++)
                    {
                        ref var ixSlot = ref IndexSlots[s];
                        byte* compBase = clusterBase + Layout.ComponentOffset(ixSlot.Slot);
                        int compSize = Layout.ComponentSize(ixSlot.Slot);
                        for (int f = 0; f < ixSlot.Fields.Length; f++)
                        {
                            ref var field = ref ixSlot.Fields[f];
                            byte* fieldPtr = compBase + slotIndex * compSize + field.FieldOffset;
                            int elementId = field.Index.Add(fieldPtr, clusterLocation, ref idxAccessor);
                            // Rebuild writes a fresh elementId into the cluster tail, overwriting any stale
                            // value from the previous (torn-down) BTree state. Issue #229 Phase 3.
                            if (field.AllowMultiple)
                            {
                                *(int*)(clusterBase + Layout.IndexElementIdOffset(field.MultiFieldIndex, slotIndex)) = elementId;
                            }
                        }
                    }
                }
            }
        }
        finally
        {
            idxAccessor.Dispose();
            clusterAccessor.Dispose();
        }
    }

    /// <summary>
    /// Initialize per-archetype spatial state (issue #230 Phase 3 Option B, Q10 multi-archetype resolution). Sets up the <see cref="SpatialSlot"/>
    /// metadata, the <see cref="ClusterDirtyRing"/>, and the per-archetype <see cref="CellClusterPool"/>. The per-cell index itself is lazily populated
    /// by spawn/migration hooks (or rebuilt from cluster data by <see cref="RebuildCellState"/> + <see cref="RebuildClusterAabbs"/> on reopen).
    /// </summary>
    /// <param name="slotToTable">Component tables indexed by slot (used to find the spatial field).</param>
    /// <param name="grid">The engine's configured spatial grid. Used to size the per-archetype <see cref="CellClusterPool"/> so its per-cell arrays cover
    /// every valid cell key. Under Q10 the pool is per-archetype — each cluster-spatial archetype sharing the grid gets its own instance sized to the
    /// grid's cell count.</param>
    /// <param name="archetypeId">Numeric id of this archetype, stored into <see cref="ArchetypeId"/>; keys this archetype's per-cell cluster claims within the
    /// shared grid so scans only walk its own clusters. Defaults to 0.</param>
    public void InitializeSpatial(ComponentTable[] slotToTable, SpatialGrid grid, int archetypeId = 0)
    {
        ArchetypeId = archetypeId;
        Grid = grid;

        for (int slot = 0; slot < slotToTable.Length; slot++)
        {
            var table = slotToTable[slot];
            if (table.SpatialIndex == null)
            {
                continue;
            }

            var tableFi = table.SpatialIndex.FieldInfo;
            // FieldOffset in cluster = field offset within pure component data (no ComponentOverhead in clusters)
            int clusterFieldOffset = tableFi.FieldOffset - table.ComponentOverhead;
            var variant = tableFi.ToVariant();
            var descriptor = SpatialNodeDescriptor.ForVariant(variant);

            // Create a modified SpatialFieldInfo with cluster-relative offset
            var fi = new SpatialFieldInfo(clusterFieldOffset, tableFi.FieldSize, tableFi.FieldType, tableFi.Margin, tableFi.CellSize, tableFi.Mode,
                tableFi.Category);

            // Dirty ring lives exclusively on ArchetypeClusterState after issue #230 Phase 3 legacy purge. Consumers (SpatialInterestSystem,
            // DatabaseEngine.WriteClusterTickFence) read ClusterDirtyRing directly.
            ClusterDirtyRing = new DirtyBitmapRing(Math.Max(4, ClusterSegment.ChunkCapacity));

            // Issue #229 Q10: allocate this archetype's own CellClusterPool. Other cluster-spatial archetypes sharing the same grid each get their own
            // instance, so claim-list scans at spawn time only walk clusters of the current archetype.
            CellClusterPool = new CellClusterPool(grid.CellCount);

            // Issue #233: allocate dormancy arrays for spatial archetypes. Non-spatial archetypes leave SleepStates null (zero overhead).
            int capacity = Math.Max(16, PrimarySegmentCapacity);
            SleepStates = new ClusterSleepState[capacity];
            SleepCounters = new ushort[capacity];

            SpatialSlot = new ClusterSpatialSlot
            {
                HasSpatialIndex = true,
                Slot = slot,
                FieldOffset = clusterFieldOffset,
                FieldInfo = fi,
                Descriptor = descriptor,
            };
            break; // Only one spatial field per archetype
        }
    }

    /// <summary>
    /// Rebuild Versioned component HEAD values in cluster slots from revision chains.
    /// Called on database reopen when the cluster slot WAL might be stale (crash between commit and tick fence).
    /// For each occupied entity, walks the revision chain to find the HEAD and copies its value to the cluster slot.
    /// </summary>
    public void RebuildVersionedHeadFromChain(ArchetypeMetadata meta, ArchetypeEngineState engineState, ChangeSet changeSet)
    {
        if (meta.VersionedSlotMask == 0)
        {
            return;
        }

        // Invariant: VersionedSlotMask != 0 implies ArchetypeClusterInfo.Compute allocated a non-null
        // SlotToVersionedIndex array (see ArchetypeClusterInfo.cs — the array is only allocated when
        // versionedSlotMask != 0). Cache the reference in a local so the null check is expressed once
        // at the top of the method instead of at every indexing site, and the compiler's nullability
        // analysis sees a non-null local for the rest of the body.
        var slotToVi = Layout.SlotToVersionedIndex;
        if (slotToVi == null)
        {
            return;
        }

        var clusterAccessor = ClusterSegment.CreateChunkAccessor();
        var mapAccessor = engineState.EntityMap.Segment.CreateChunkAccessor();
        int recordSize = meta._entityRecordSize;
        byte* recordBuf = stackalloc byte[recordSize];

        // Pre-create accessors for each Versioned slot's tables (hoisted out of entity/slot loops)
        var compRevAccessors = new ChunkAccessor<PersistentStore>[meta.ComponentCount];
        var contentAccessors = new ChunkAccessor<PersistentStore>[meta.ComponentCount];
        for (int slot = 0; slot < meta.ComponentCount; slot++)
        {
            if (slotToVi[slot] >= 0)
            {
                var table = engineState.SlotToComponentTable[slot];
                compRevAccessors[slot] = table.CompRevTableSegment.CreateChunkAccessor();
                contentAccessors[slot] = table.ComponentSegment.CreateChunkAccessor();
            }
        }

        try
        {
            for (int c = 0; c < ActiveClusterCount; c++)
            {
                int chunkId = ActiveClusterIds[c];
                byte* clusterBase = clusterAccessor.GetChunkAddress(chunkId, true);
                ulong occupancy = *(ulong*)clusterBase;

                while (occupancy != 0)
                {
                    int slotIndex = BitOperations.TrailingZeroCount(occupancy);
                    occupancy &= occupancy - 1;

                    // Read entity key from cluster
                    long entityPK = *(long*)(clusterBase + Layout.EntityIdsOffset + slotIndex * 8);
                    long entityKey = EntityId.FromRaw(entityPK).EntityKey;

                    // Read ClusterEntityRecord from EntityMap to get compRevFirstChunkId
                    if (!engineState.EntityMap.TryGet(entityKey, recordBuf, ref mapAccessor))
                    {
                        continue;
                    }

                    // For each Versioned slot: walk chain → find HEAD → copy to cluster slot
                    for (int slot = 0; slot < meta.ComponentCount; slot++)
                    {
                        int vi = slotToVi[slot];
                        if (vi < 0)
                        {
                            continue;
                        }

                        int compRevFirstChunkId = ClusterEntityRecordAccessor.GetCompRevFirstChunkId(recordBuf, vi);
                        if (compRevFirstChunkId == 0)
                        {
                            continue;
                        }

                        // Walk chain to find HEAD (latest committed entry)
                        ref var compRevAccessor = ref compRevAccessors[slot];
                        var chainResult = RevisionChainReader.WalkChain(ref compRevAccessor, compRevFirstChunkId, long.MaxValue);
                        if (chainResult.IsFailure)
                        {
                            continue;
                        }

                        // Read HEAD value from content chunk and copy to cluster slot
                        int headChunkId = chainResult.Value.CurCompContentChunkId;
                        ref var contentAccessor = ref contentAccessors[slot];
                        byte* srcAddr = contentAccessor.GetChunkAddress(headChunkId);
                        int compSize = Layout.ComponentSize(slot);
                        byte* dstSlot = clusterBase + Layout.ComponentOffset(slot) + slotIndex * compSize;
                        Unsafe.CopyBlockUnaligned(dstSlot, srcAddr + engineState.SlotToComponentTable[slot].ComponentOverhead, (uint)compSize);
                    }
                }
            }
        }
        finally
        {
            // Dispose all hoisted accessors
            for (int slot = 0; slot < meta.ComponentCount; slot++)
            {
                if (slotToVi[slot] >= 0)
                {
                    compRevAccessors[slot].Dispose();
                    contentAccessors[slot].Dispose();
                }
            }

            mapAccessor.Dispose();
            clusterAccessor.Dispose();
        }
    }
}

/// <summary>
/// Per-component-slot index state for a cluster-eligible archetype. One per component slot that has indexed fields.
/// </summary>
internal struct ClusterIndexSlot
{
    /// <summary>Component slot index within the archetype.</summary>
    public int Slot;

    /// <summary>Per-indexed-field B+Tree instances (per-archetype ownership).</summary>
    public ClusterIndexField[] Fields;

    /// <summary>Per-indexed-field shadow buffers for old value capture before mutation.</summary>
    public FieldShadowBuffer[] ShadowBuffers;
}

/// <summary>
/// Per-archetype spatial index metadata for a cluster-eligible archetype with a <c>[SpatialIndex]</c> field. Holds the narrowphase-facing metadata
/// (<see cref="Slot"/>, <see cref="FieldOffset"/>, <see cref="FieldInfo"/>, <see cref="Descriptor"/>) that both the legacy per-entity tree (being removed
/// in issue #230 Phase 3) and the new per-cell cluster index path (<see cref="ArchetypeClusterState.PerCellIndex"/>) read during spatial bound dispatch.
/// </summary>
internal struct ClusterSpatialSlot
{
    /// <summary>
    /// <c>true</c> when <see cref="ArchetypeClusterState.InitializeSpatial"/> has populated this slot with a configured spatial field. This is the single
    /// check for "does this archetype have a cluster spatial index?" — the per-cell index (<see cref="ArchetypeClusterState.PerCellIndex"/>) itself is
    /// lazily allocated and provides no always-on existence sentinel of its own.
    /// </summary>
    public bool HasSpatialIndex;

    /// <summary>Component slot index that has the spatial field.</summary>
    public int Slot;

    /// <summary>Byte offset of spatial field within cluster component SoA (no ComponentOverhead).</summary>
    public int FieldOffset;

    /// <summary>Spatial field metadata (margin, mode, field type).</summary>
    public SpatialFieldInfo FieldInfo;

    /// <summary>Node layout descriptor.</summary>
    public SpatialNodeDescriptor Descriptor;
}

/// <summary>
/// Per-indexed-field B+Tree state within a cluster-eligible archetype.
/// </summary>
internal struct ClusterIndexField
{
    /// <summary>Byte offset of this field within the pure component data (no ComponentOverhead — clusters have no overhead).</summary>
    public int FieldOffset;

    /// <summary>Field size in bytes.</summary>
    public int FieldSize;

    /// <summary>Per-archetype B+Tree instance. Value = ClusterLocation (clusterChunkId * 64 + slotIndex).</summary>
    public BTreeBase<PersistentStore> Index;

    /// <summary>Whether index allows multiple values per key.</summary>
    public bool AllowMultiple;

    /// <summary>Zone map for cluster-level query pruning. Non-null for numeric field types.</summary>
    public ZoneMapArray ZoneMap;

    /// <summary>
    /// Sequential index into the cluster's elementId tail section (0..<see cref="ArchetypeClusterInfo.MultipleIndexedFieldCount"/>-1),
    /// or <c>-1</c> when <see cref="AllowMultiple"/> is false (no tail section allocated for this field).
    /// Used by the cluster destroy/migrate path to locate the per-entity elementId via
    /// <see cref="ArchetypeClusterInfo.IndexElementIdOffset"/> and pass it to
    /// <see cref="BTreeBase{TStore}.RemoveValue"/>, so that only this entity's specific
    /// <c>(key, clusterLocation)</c> entry is removed — not the entire buffer at the key.
    /// </summary>
    public int MultiFieldIndex;
}

/// <summary>
/// Per-component-slot SingleVersion ComponentCollection state for a cluster-eligible archetype. One entry per SV component slot that carries a
/// ComponentCollection field. The cluster slot is the sole owner of these buffers (SV has no revision chain), so they are released directly in
/// <c>ReleaseSlot</c> on destroy.
/// </summary>
internal struct ClusterCollectionSlot
{
    /// <summary>Component slot index within the archetype.</summary>
    public int Slot;

    /// <summary>The ComponentCollection fields of this SingleVersion component.</summary>
    public ClusterCollectionField[] Fields;
}

/// <summary>
/// A single ComponentCollection field within a SingleVersion cluster slot.
/// </summary>
internal struct ClusterCollectionField
{
    /// <summary>Byte offset of the ComponentCollection <c>_bufferId</c> within the pure component data (no overhead in clusters).</summary>
    public int FieldOffset;

    /// <summary>The variable-sized buffer segment backing this collection's element type.</summary>
    public VariableSizedBufferSegmentBase<PersistentStore> Vsbs;
}
