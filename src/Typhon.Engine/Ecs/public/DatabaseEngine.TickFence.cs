using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Threading;
using Typhon.Schema.Definition;

namespace Typhon.Engine;

// DatabaseEngine — tick-fence commit pipeline (partial). Extracted from DatabaseEngine.cs for file-size / IDE-analysis reasons; behaviour unchanged.
// Serializes dirty component data to the WAL at each tick boundary: WriteTickFence → per-table/per-archetype fence Prepare/Finalize, AABB recompute,
// and dirty-bit delta flushing. See claude/overview/04-data.md and the durability rules (LOG-*) for the fence contract.
public partial class DatabaseEngine
{
    /// <summary>
    /// Serializes dirty SingleVersion component data to WAL at tick boundary. One TickFence chunk per SV ComponentTable.
    /// Called by the game loop at each tick boundary.
    /// </summary>
    /// <param name="tickNumber">Monotonic tick identifier.</param>
    /// <param name="changeSet">Caller-supplied ChangeSet for shared dirty-page tracking across the whole tick fence (typically the per-tick UoW's
    /// shared ChangeSet — see <see cref="UnitOfWork.ChangeSet"/>). When null, a one-shot local ChangeSet is created and committed by this method itself
    /// (test/admin path: tests that invoke <c>WriteTickFence</c> directly without a UoW retain their original behaviour).</param>
    /// <returns>Highest LSN written, or 0 if nothing was serialized.</returns>
    public long WriteTickFence(long tickNumber, ChangeSet changeSet = null)
    {
        // When the caller doesn't supply a ChangeSet (e.g., tests that invoke WriteTickFence outside a UoW), we own the lifecycle: create a fresh
        // ChangeSet, thread it through the per-table tick-fence callees, and commit it ourselves at the end. Production callers (TyphonRuntime)
        // pass _currentUow.ChangeSet so dirty-page tracking is consolidated with everything else this tick — UoW.Flush handles the actual writeback.
        var ownChangeSet = changeSet == null;
        if (ownChangeSet)
        {
            changeSet = MMF.CreateChangeSet();
        }

        long highestLSN;
        try
        {
            highestLSN = WriteTickFenceCore(tickNumber, changeSet);
        }
        finally
        {
            if (ownChangeSet)
            {
                changeSet.SaveChanges();
                changeSet.ReleaseExcessDirtyMarks();
            }
        }

        return highestLSN;
    }

    private long WriteTickFenceCore(long tickNumber, ChangeSet changeSet)
    {
        long highestLSN = 0;
        using var epochGuard = EpochGuard.Enter(EpochManager);

        foreach (var table in _componentTableByType.Values)
        {
            var contributed = ProcessTableFence(table, tickNumber, changeSet);
            if (contributed > highestLSN)
            {
                highestLSN = contributed;
            }
        }

        // Cluster tick fence: serialize dirty cluster-backed entity data to WAL
        WriteClusterTickFence(tickNumber, ref highestLSN, changeSet);

        if (highestLSN > 0)
        {
            Interlocked.Exchange(ref _lastTickFenceLSN, highestLSN);
        }

        return highestLSN;
    }

    /// <summary>Per-thread scratch arena for fence batches — ProcessTableFence is documented safe to call concurrently across distinct tables.</summary>
    [ThreadStatic]
    private static CommitBatchArena _fenceArena;

    /// <summary>Soft cap on a single fence <c>Append</c> frame; larger fences split into multiple Appends (each fence record is individually committed).</summary>
    private const int MaxFenceBatchBytes = 256 * 1024;

    private long AppendFenceBatch(ref CommitBatchBuilder batch)
    {
        var wc = WaitContext.FromDeadline(Deadline.FromTimeout(TimeoutOptions.Current.DefaultCommitTimeout));
        return DurabilityLog.Append(ref batch, ref wc);
    }

    /// <summary>
    /// Tick-fence body for a single <see cref="ComponentTable"/>. Encapsulates the per-table work historically inlined in <see cref="WriteTickFenceCore"/>'s
    /// loop: dirty-bitmap snapshot, WAL chunk serialization, shadow + spatial maintenance, dirty-ring archive. Returns the highest LSN published by this table
    /// (0 if none / skipped). Safe to call concurrently across distinct tables — touches only the table's own state plus the MPSC <see cref="WalCommitBuffer"/>.
    /// </summary>
    internal long ProcessTableFence(ComponentTable table, long tickNumber, ChangeSet changeSet)
    {
        if (table.StorageMode == StorageMode.Versioned || table.DirtyBitmap == null)
        {
            return 0;
        }

        if (!table.DirtyBitmap.HasDirty)
        {
            table.PreviousTickDirtyBitmap = null;
            table.PreviousTickHadDirtyEntities = false;
            return 0;
        }

        // Snapshot DirtyBitmap — atomic swap, clears bitmap for next tick
        var dirtyBits = table.DirtyBitmap.Snapshot();

        // The runtime iterates set bits at dispatch time (same pattern as ProcessSpatialEntries).
        table.PreviousTickDirtyBitmap = dirtyBits;
        table.PreviousTickHadDirtyEntities = true;

        // Popcount once — used both by the per-table fence span payload and by the WAL chunk sizing path below.
        var entryCount = 0;
        for (var i = 0; i < dirtyBits.Length; i++)
        {
            entryCount += BitOperations.PopCount((ulong)dirtyBits[i]);
        }

        long highestLSN = 0;
        var tableScope = TyphonEvent.BeginWriteTickFenceTable(table.WalTypeId, entryCount);
        try
        {
            var walPublished = false;
            var hasShadow = table.HasShadowableIndexes;
            var hasSpatial = table.SpatialIndex != null && table.SpatialIndex.FieldInfo.Mode == SpatialMode.Dynamic;

            // WAL serialization: SV only — Transient has no WAL persistence, skip straight to shadow processing. Each dirty entity
            // becomes one fence-flagged Slot record through the v2 codec (M3): the entity PK is read from the chunk overhead (offset 0,
            // the same read PipelineExecutor does at :724), so fence records are logical (EntityId, ComponentTypeId), never physical chunk ids.
            if (table.StorageMode == StorageMode.SingleVersion && entryCount > 0)
            {
                var stride = table.ComponentStorageSize;
                var overhead = table.ComponentOverhead;
                var componentTypeId = (ushort)ArchetypeRegistry.GetComponentTypeId(table.Definition.POCOType);
                var recOverhead = RecordHeader.SizeInBytes + SlotRecordBody.FixedSize;

                // One arena per thread — ProcessTableFence is documented safe to call concurrently across distinct tables.
                var fenceArena = _fenceArena ??= new CommitBatchArena();
                fenceArena.Reset();
                var batch = new CommitBatchBuilder(fenceArena, tickNumber, 0, fenceMode: true);
                var batchBytes = 0;

                var accessor = table.ComponentSegment.CreateChunkAccessor();
                try
                {
                    for (var wi = 0; wi < dirtyBits.Length; wi++)
                    {
                        var word = dirtyBits[wi];
                        while (word != 0)
                        {
                            var bit = BitOperations.TrailingZeroCount((ulong)word);
                            word &= word - 1; // clear lowest set bit
                            var chunkId = wi * 64 + bit;

                            var src = accessor.GetChunkAsReadOnlySpan(chunkId);
                            var entityPk = MemoryMarshal.Read<long>(src);

                            // Flush before the frame would exceed the per-Append cap. Fence records are individually committed, so
                            // splitting across Appends is safe; the codec splits each batch into RecordBatch chunks internally.
                            if (batchBytes > 0 && batchBytes + recOverhead + stride > MaxFenceBatchBytes)
                            {
                                highestLSN = Math.Max(highestLSN, AppendFenceBatch(ref batch));
                                walPublished = true;
                                fenceArena.Reset();
                                batch = new CommitBatchBuilder(fenceArena, tickNumber, 0, fenceMode: true);
                                batchBytes = 0;
                            }

                            // Wire identity is the per-archetype slot (LOG-06); resolve from this entity's archetype (routing id in the PK).
                            var slot = (ushort)GetMetaByRouting(EntityId.FromRaw(entityPk).ArchetypeId).GetSlot(componentTypeId);
                            batch.AddSlot(entityPk, slot, src.Slice(overhead, stride));
                            batchBytes += recOverhead + stride;
                        }
                    }

                    if (!batch.IsEmpty)
                    {
                        highestLSN = Math.Max(highestLSN, AppendFenceBatch(ref batch));
                        walPublished = true;
                    }
                }
                finally
                {
                    accessor.Dispose();
                }
            }

            // Deferred index maintenance: process shadowed old field values for non-Versioned indexed fields.
            // Must run even without WAL (indexes are in-memory structures independent of WAL).
            if (hasShadow)
            {
                var shadowScope = TyphonEvent.BeginWriteTickFenceShadow(table.WalTypeId, table.IndexedFieldInfos?.Length ?? 0);
                try
                {
                    shadowScope.TotalShadowEntries = ProcessShadowEntries(table, changeSet);
                }
                finally
                {
                    shadowScope.Dispose();
                }
            }

            // Spatial index maintenance: iterate dirty entities, update R-Tree positions.
            // Uses dirtyBits snapshot (still in scope from DirtyBitmap.Snapshot above).
            // Spatial doesn't need shadows — back-pointers provide O(1) leaf lookup, and the containment check
            // uses the fat AABB stored in the tree node. Only the final position matters.
            if (hasSpatial)
            {
                var spatialScope = TyphonEvent.BeginWriteTickFenceSpatial(table.WalTypeId, entryCount);
                try
                {
                    spatialScope.EscapedCount = ProcessSpatialEntries(table, dirtyBits, changeSet);
                }
                finally
                {
                    spatialScope.Dispose();
                }
            }

            // Archive dirty bitmap into ring buffer for interest management delta queries
            table.SpatialIndex?.InterestSystem?.DirtyRing.Archive(tickNumber, dirtyBits, dirtyBits.Length);

            tableScope.WalPublished = walPublished ? (byte)1 : (byte)0;
            tableScope.HasShadow = hasShadow ? (byte)1 : (byte)0;
            tableScope.HasSpatial = hasSpatial ? (byte)1 : (byte)0;
        }
        finally
        {
            tableScope.Dispose();
        }

        return highestLSN;
    }

    /// <summary>
    /// Serializes dirty cluster entity data to WAL for all cluster-eligible archetypes.
    /// Called from <see cref="WriteTickFence"/> after per-ComponentTable processing.
    /// </summary>
    /// <summary>Create a fresh CBS&lt;TransientStore&gt; for cluster Transient component storage.</summary>
    private void CreateTransientClusterSegment(int stride, out TransientStore? store, out ChunkBasedSegment<TransientStore> segment)
    {
        store = new TransientStore(TransientOptions, MemoryAllocator, EpochManager, this);
        var tsValue = store.Value;
        // Allocate the initial pages on `tsValue` BEFORE constructing the segment. TransientStore is a struct, so the segment's base LogicalSegment copies it
        // by value in its ctor — if we allocated after construction, the segment's copy would keep _pageCount=0 and the first Grow would re-allocate duplicate
        // page indices (0,1,2,3 again), corrupting the forward chain. Allocating first means base(tsValue) captures _pageCount=4. (See ComponentTable.CreateTransientSegments.)
        Span<int> tsPages = stackalloc int[4];
        tsValue.AllocatePages(ref tsPages, 0, null);
        segment = new ChunkBasedSegment<TransientStore>(EpochManager, tsValue, stride);
        segment.Create(PageBlockType.None, StorageSegmentKind.Cluster, tsPages, false);
    }

    /// <summary>
    /// After reopening a mixed archetype with Transient components, allocate matching chunks in the fresh
    /// TransientSegment so chunk IDs stay synchronized with the persisted PersistentStore segment.
    /// </summary>
    /// <remarks>
    /// <para>Relies on the TransientSegment being freshly created (no prior allocations/frees), which guarantees
    /// sequential chunk ID assignment (1, 2, 3, ...). This is always true because TransientStore data doesn't
    /// survive restart — the segment is created fresh in every reopen path.</para>
    /// </remarks>
    private static void SyncTransientSegmentToActive(ArchetypeClusterState clusterState)
    {
        if (clusterState.TransientSegment == null)
        {
            return;
        }

        // Find max chunk ID among active clusters
        var maxChunkId = 0;
        for (var i = 0; i < clusterState.ActiveClusterCount; i++)
        {
            if (clusterState.ActiveClusterIds[i] > maxChunkId)
            {
                maxChunkId = clusterState.ActiveClusterIds[i];
            }
        }

        // Allocate chunks in TransientStore sequentially up to maxChunkId so IDs match.
        // TransientStore is always fresh — sequential allocation produces IDs 1..maxChunkId.
        for (var id = 1; id <= maxChunkId; id++)
        {
            var allocatedId = clusterState.TransientSegment.AllocateChunk(true);
            Debug.Assert(allocatedId == id, $"TransientSegment sync: expected chunk ID {id}, got {allocatedId}");
        }
    }

    private void WriteClusterTickFence(long tickNumber, ref long highestLSN, ChangeSet changeSet)
    {
        // Issue #233: drain all deferred wake requests collected during parallel system execution. Must run once BEFORE the per-archetype loop so each
        // archetype's DormancySweep (below) sees up-to-date WakePending states and skips those clusters instead of re-sleeping them. The fence parallel
        // path runs this drain in FencePrep (TickDriver) so per-archetype work can be split across workers without coordinating on this global state.
        DormancyReporter.DrainAll(_archetypeStates);

        foreach (var meta in ArchetypeRegistry.GetAllArchetypes())
        {
            var contributed = ProcessArchetypeFence(meta, tickNumber, changeSet);
            if (contributed > highestLSN)
            {
                highestLSN = contributed;
            }
        }
    }

    /// <summary>
    /// Serial entry point for one archetype's tick-fence work. Runs Prepare → ExecuteMigrations (no slicing) → Finalize in sequence on the calling thread.
    /// Used by the legacy/opt-out path (<c>EnableParallelFence = false</c>) where the whole fence runs single-threaded. The parallel path calls
    /// <see cref="PrepareArchetypeFence"/>, <see cref="ExecuteMigrationsSlice"/>, and <see cref="FinalizeArchetypeFence"/> directly through their phase-scoped
    /// internal systems.
    /// </summary>
    internal long ProcessArchetypeFence(ArchetypeMetadata meta, long tickNumber, ChangeSet changeSet)
    {
        if (!PrepareArchetypeFence(meta, tickNumber, changeSet))
        {
            return 0;
        }
        var clusterState = _archetypeStates[meta.ArchetypeId].ClusterState;
        if (clusterState.PendingMigrationCount > 0)
        {
            ExecuteMigrationsSlice(meta, 0, clusterState.PendingMigrationCount, changeSet);
        }
        // AABB recompute: mirrors the parallel AabbRefresh phase. The wrapper handles bookkeeping clear at its tail —
        // FinalizeArchetypeFence's redundant ClearAabbRefreshBookkeeping then iterates an already-empty bitmap (cheap).
        if (clusterState.SpatialSlot.HasSpatialIndex && clusterState.SpatialSlot.FieldInfo.Mode == SpatialMode.Dynamic && clusterState.FenceBranchPath != 0)
        {
            RecomputeArchetypeAabbs(meta);
        }
        return FinalizeArchetypeFence(meta, tickNumber, changeSet);
    }

    /// <summary>
    /// Serial-path AABB recompute entry: opens a chunk accessor and runs the whole-archetype <see cref="ArchetypeClusterState.RecomputeDirtyClusterAabbs"/>
    /// (which delegates to a single full-range slice and clears bookkeeping at the tail). Used by <see cref="ProcessArchetypeFence"/>.
    /// </summary>
    internal void RecomputeArchetypeAabbs(ArchetypeMetadata meta)
    {
        if (meta == null || !meta.IsClusterEligible || meta.ArchetypeId >= _archetypeStates.Length)
        {
            return;
        }

        var clusterState = _archetypeStates[meta.ArchetypeId]?.ClusterState;
        if (clusterState == null || clusterState.ClusterSegment == null)
        {
            return;
        }

        var spatialScope = TyphonEvent.BeginWriteTickFenceClusterSpatial(meta.ArchetypeId, clusterState.FenceDirtyClusterCount);
        var accessor = clusterState.ClusterSegment.CreateChunkAccessor();
        try
        {
            clusterState.RecomputeDirtyClusterAabbs(clusterState.FenceDirtyBits, ref accessor, _spatialGrid);
            spatialScope.MigrationsExecuted = clusterState.LastTickMigrationCount;
        }
        finally
        {
            accessor.Dispose();
            spatialScope.Dispose();
        }
    }

    /// <summary>
    /// Parallel-path AABB recompute entry: applies a contiguous slice of the archetype's AABB recompute. Safe to call concurrently across DISJOINT slices of
    /// the same archetype. Bookkeeping clear happens once per archetype in <see cref="FinalizeArchetypeFence"/> after the phase barrier.
    /// </summary>
    internal void RecomputeArchetypeAabbsSlice(ArchetypeMetadata meta, int sliceStart, int sliceCount)
    {
        if (meta == null || !meta.IsClusterEligible || meta.ArchetypeId >= _archetypeStates.Length)
        {
            return;
        }

        var clusterState = _archetypeStates[meta.ArchetypeId]?.ClusterState;
        if (clusterState == null || clusterState.ClusterSegment == null)
        {
            return;
        }

        if (clusterState.FenceBranchPath == 0)
        {
            return;
        }

        // ClusterScanned = clusters actually considered by this slice. In legacy mode it equals sliceCount (index range count). In barrier mode it's the
        // popcount across the slice's bitmap words — computed inside the slice helper.
        var clustersInSlice = clusterState.CountClustersInAabbSlice(sliceStart, sliceCount);
        var refreshSpan = TyphonEvent.BeginSpatialClusterAabbRefresh(meta.ArchetypeId, clustersInSlice);
        // CreateChunkAccessor is a struct ctor (4 field assigns) and EpochGuard is already entered at chunk level in FencePhaseExecSystemBase.Execute —
        // per-slice accessor cost is sub-microsecond. Not worth caching.
        var accessor = clusterState.ClusterSegment.CreateChunkAccessor();
        // Worker-local outlier buffer (review D-2): RecomputeDirtyClusterAabbsSlice appends here per-entity without locking; we bulk-enqueue under
        // _finalizeLock once after the slice finishes. List is short-lived per slice (no pooling — outlier fires are rare; allocations are bounded by the
        // AABB-Refresh chunk count per tick).
        var outlierBuffer = new List<MigrationRequest>(0);
        try
        {
            clusterState.RecomputeDirtyClusterAabbsSlice(sliceStart, sliceCount, ref accessor, _spatialGrid, outlierBuffer, out var aabbsChanged, 
                out var slotsScanned, out var outlierGuardFires);
            clusterState.EnqueueMigrationsBulk(outlierBuffer);
            refreshSpan.AabbsChanged = aabbsChanged;
            refreshSpan.SlotsScanned = slotsScanned;
            refreshSpan.OutlierGuardFires = outlierGuardFires;
        }
        finally
        {
            accessor.Dispose();
            refreshSpan.Dispose();
        }
    }

    /// <summary>
    /// Phase 1 of the parallel cluster tick fence: per-archetype prep work that must complete BEFORE any migration apply.
    /// Returns <c>true</c> if subsequent phases (Migrate/Finalize) have work to do for this archetype.
    /// </summary>
    /// <remarks>
    /// <para>Order-tight pipeline:</para>
    /// <list type="number">
    ///   <item>Pure-transient short-circuit: snapshot dirty bitmap (if any), propagate per-table flags, dormancy sweep. Returns false.</item>
    ///   <item>Clean-bitmap path: dormancy sweep with empty bitmap, then on spatial-Dynamic archetypes build local occupancy-only spatialBits and run
    ///         DetectClusterMigrations. Stores branch path = 1 on the cluster state if any migrations queued or spatial refresh needed.</item>
    ///   <item>Dirty-bitmap path: snapshot bitmap, occupancy-mask, ProcessClusterShadowEntries, RecomputeClusterZoneMaps, DetectClusterMigrations.
    ///         Stores branch path = 2 + the snapshot in <see cref="ArchetypeClusterState.FenceDirtyBits"/>.</item>
    /// </list>
    /// <para>Safe to call concurrently across DISTINCT archetypes — touches only this archetype's own cluster state plus the per-archetype B+Tree (OLC-safe)
    /// plus per-cluster shadow buffers (per-cluster). Cell-descriptor mutations are deferred to ExecuteMigrationsSlice (Phase 2) and Finalize (Phase 3);
    /// Prep itself does not bump cell counters.</para>
    /// </remarks>
    internal unsafe bool PrepareArchetypeFence(ArchetypeMetadata meta, long tickNumber, ChangeSet changeSet)
    {
        if (meta == null || !meta.IsClusterEligible || meta.ArchetypeId >= _archetypeStates.Length)
        {
            return false;
        }

        var engineState = _archetypeStates[meta.ArchetypeId];
        var clusterState = engineState?.ClusterState;
        if (clusterState == null)
        {
            return false;
        }

        // Reset fence-tick intermediate state at the top of every Prep so a stale snapshot from a previous tick never leaks into the current tick's
        // Migrate / Finalize phases. The Migrate slices (Phase 2) Interlocked.Add into LastTickMigrationCount / LastTickMigrationExecuteMs — start at zero here.
        clusterState.FenceBranchPath = 0;
        clusterState.FenceDirtyBits = null;
        clusterState.FenceEntryCount = 0;
        clusterState.FenceDirtyClusterCount = 0;
        clusterState.FenceProcessBitmapClusterCount = -1; // recomputed in Prep when in BarrierOnly mode
        clusterState.LastTickMigrationCount = 0;
        clusterState.LastTickMigrationExecuteMs = 0d;
        clusterState._drainedCount = 0; // deferred-drain list reset (review C-1 fix)

        // Pure-Transient archetypes have no PersistentStore segment — nothing to persist to WAL, no migrations.
        // Entire flow runs inside Prep; Migrate and Finalize will see FenceBranchPath = 0 and skip.
        if (clusterState.ClusterSegment == null)
        {
            var clusterScopeT = TyphonEvent.BeginWriteTickFenceCluster(meta.ArchetypeId);
            try
            {
                if (clusterState.ClusterDirtyBitmap.HasDirty)
                {
                    var transientDirtyBits = clusterState.ClusterDirtyBitmap.Snapshot();
                    clusterState.PreviousTickDirtySnapshot = transientDirtyBits;
                    var transientDirtyClusterCount = 0;
                    for (var i = 0; i < transientDirtyBits.Length; i++)
                    {
                        transientDirtyClusterCount += BitOperations.PopCount((ulong)transientDirtyBits[i]);
                    }
                    clusterScopeT.DirtyClusterCount = transientDirtyClusterCount;
                    for (var slot = 0; slot < clusterState.Layout.ComponentCount; slot++)
                    {
                        engineState.SlotToComponentTable[slot].PreviousTickHadDirtyEntities = true;
                        engineState.SlotToComponentTable[slot].PreviousTickDirtyBitmap ??= Array.Empty<long>();
                    }
                    clusterState.DormancySweep(transientDirtyBits, tickNumber);
                }
                else
                {
                    clusterState.PreviousTickDirtySnapshot = null;
                    clusterState.DormancySweep(Array.Empty<long>(), tickNumber);
                }
            }
            finally
            {
                clusterScopeT.Dispose();
            }
            return false;
        }

        // Clean-bitmap branch: spatial-Dynamic archetypes still need a sparse refresh because WriteSpatial-only callers may have moved positions without
        // setting the dirty bitmap. We populate FenceDirtyBits with the local occupancy bits (so DetectClusterMigrations can scan only live slots) and route to
        // branch path 1. Finalize will run the AABB recompute + dormancy sweep; no WAL emit on this branch.
        if (!clusterState.ClusterDirtyBitmap.HasDirty)
        {
            clusterState.PreviousTickDirtySnapshot = null;

            if (clusterState.SpatialSlot.HasSpatialIndex && clusterState.SpatialSlot.FieldInfo.Mode == SpatialMode.Dynamic && clusterState.ActiveClusterCount > 0)
            {
                var clusterScopeC = TyphonEvent.BeginWriteTickFenceCluster(meta.ArchetypeId);
                try
                {
                    clusterScopeC.HasSpatial = 1;
                    var accessorLocal = clusterState.ClusterSegment.CreateChunkAccessor();
                    try
                    {
                        var wordCount = clusterState.PrimarySegmentCapacity;
                        var spatialBits = new long[Math.Max(wordCount, 1)];
                        for (var ai = 0; ai < clusterState.ActiveClusterCount; ai++)
                        {
                            var chId = clusterState.ActiveClusterIds[ai];
                            if (chId < 0 || chId >= spatialBits.Length)
                            {
                                continue;
                            }

                            var occB = accessorLocal.GetChunkAddress(chId);
                            var occ = *(ulong*)occB;
                            spatialBits[chId] = (long)occ;
                        }

                        DetectClusterMigrations(clusterState, engineState, meta.ArchetypeId, spatialBits, ref accessorLocal);
                        clusterState.FenceDirtyBits = spatialBits;
                        clusterState.FenceBranchPath = 1; // clean-spatial-refresh: AABB recompute in Finalize, no WAL
                    }
                    finally
                    {
                        accessorLocal.Dispose();
                    }
                }
                finally
                {
                    clusterScopeC.Dispose();
                }
                return true; // Migrate (if pending) + Finalize have work to do
            }

            // No spatial refresh needed — dormancy sweep on empty bitmap here, no migrations, no Finalize work.
            clusterState.DormancySweep(Array.Empty<long>(), tickNumber);
            return false;
        }

        // Dirty-bitmap branch: full snapshot + occupancy mask + shadow + zone-maps + detect. Migrate phase will execute pending migrations (if any) under
        // cell-partitioned worker slices; Finalize will run AABB recompute, dormancy, and WAL emit on the post-migration FenceDirtyBits.
        var clusterScope = TyphonEvent.BeginWriteTickFenceCluster(meta.ArchetypeId);
        try
        {
            var dirtyBits = clusterState.ClusterDirtyBitmap.Snapshot();

            // Mask dirty bits with live occupancy to skip destroyed entities whose dirty bit remained set.
            var accessor = clusterState.ClusterSegment.CreateChunkAccessor();
            try
            {
                var entryCount = 0;
                var dirtyClusterCount = 0;
                for (var i = 0; i < dirtyBits.Length; i++)
                {
                    if (dirtyBits[i] == 0)
                    {
                        continue;
                    }
                    var occBase = accessor.GetChunkAddress(i);
                    var occupancy = *(ulong*)occBase;
                    dirtyBits[i] &= (long)occupancy;
                    if (dirtyBits[i] != 0)
                    {
                        dirtyClusterCount++;
                    }
                    entryCount += BitOperations.PopCount((ulong)dirtyBits[i]);
                }

                clusterScope.DirtyClusterCount = dirtyClusterCount;
                clusterScope.EntryCount = entryCount;

                // Shadow + zone-maps: runs in Prep so the per-archetype B+Tree Move calls happen before any Migrate-phase Remove+Add calls reorder the index.
                // B+Tree itself is OLC-safe across concurrent archetypes (each runs in its own Prep chunk).
                if (clusterState.IndexSlots != null)
                {
                    clusterScope.HasShadow = 1;
                    var shadowScope = TyphonEvent.BeginWriteTickFenceClusterShadow(meta.ArchetypeId, dirtyClusterCount);
                    try
                    {
                        shadowScope.TotalShadowEntries = ProcessClusterShadowEntries(clusterState, engineState, changeSet);
                    }
                    finally
                    {
                        shadowScope.Dispose();
                    }
                    RecomputeClusterZoneMaps(clusterState, dirtyBits);
                }

                // Detect migrations: populates clusterState.PendingMigrations. Spatial-only — Dynamic mode.
                if (clusterState.SpatialSlot.HasSpatialIndex && clusterState.SpatialSlot.FieldInfo.Mode == SpatialMode.Dynamic)
                {
                    clusterScope.HasSpatial = 1;
                    DetectClusterMigrations(clusterState, engineState, meta.ArchetypeId, dirtyBits, ref accessor);
                }
            }
            finally
            {
                accessor.Dispose();
            }

            clusterState.FenceDirtyBits = dirtyBits;
            clusterState.FenceBranchPath = 2;
            clusterState.FenceEntryCount = clusterScope.EntryCount;
            clusterState.FenceDirtyClusterCount = clusterScope.DirtyClusterCount;
        }
        finally
        {
            clusterScope.Dispose();
        }

        // Pre-size FenceDirtyBits + per-cluster arrays to a generous upper bound so the Migrate phase (parallel or serial) doesn't hit ExecuteMigrations'
        // on-demand grow path under normal conditions. The strict bound (PrimarySegmentCapacity + PendingMigrationCount) under-estimates in practice when
        // multiple Migrate workers each allocate new clusters and inter-archetype shadow/index allocations also grow segments — observed dstChunkId values
        // exceeded this bound under AntHill loads. The doubled-plus-buffer bound covers worst-case interleavings; the cost is ~32KB extra per archetype,
        // trivial. On-demand grow under _finalizeLock (ArchetypeClusterState.GrowFenceDirtyBitsForChunkId) remains as a safety net for pathological cases.
        var existingLen = clusterState.FenceDirtyBits?.Length ?? 0;
        var upperBound = Math.Max(clusterState.PrimarySegmentCapacity, existingLen) + 2 * clusterState.PendingMigrationCount + 64;
        clusterState.PreSizeMigrationBuffers(upperBound);

        // Memoize popcount of ClusterProcessBitmap so the AabbRefresh planner doesn't redo it on TickDriver (D-4).
        // Only meaningful in BarrierOnly mode; Legacy mode reads ActiveClusterCount directly.
        if (clusterState.SpatialBarrierOnly && clusterState.ClusterProcessBitmap != null)
        {
            var total = 0;
            var bm = clusterState.ClusterProcessBitmap;
            for (var w = 0; w < bm.Length; w++)
            {
                total += BitOperations.PopCount((ulong)bm[w]);
            }

            clusterState.FenceProcessBitmapClusterCount = total;
        }

        return true;
    }

    /// <summary>
    /// Phase 2 of the parallel cluster tick fence: apply a contiguous slice of one archetype's <see cref="ArchetypeClusterState.PendingMigrations"/>.
    /// Safe to call concurrently from multiple workers — each worker owns a disjoint slice (sorted by destination cell key) so dst-side mutations
    /// (slot claim, AABB union, per-cell index update) hit worker-exclusive cells. Source-side mutations (occupancy clear, dirtyBits flip,
    /// cell.EntityCount decrement) use <see cref="System.Threading.Interlocked"/> primitives; rare empty-cluster finalization is serialized via
    /// the per-archetype <see cref="ArchetypeClusterState._finalizeLock"/> through <see cref="ArchetypeClusterState.ReleaseSlot"/>.
    /// </summary>
    /// <remarks>
    /// Callers must ensure (a) <see cref="ArchetypeClusterState.FenceDirtyBits"/> has been pre-sized to at least
    /// <c>PrimarySegmentCapacity + PendingMigrationCount</c> entries by TickDriver before any Migrate-phase worker runs (eliminates parallel
    /// <c>Array.Resize</c>), and (b) the slice <c>[sliceStart, sliceStart+sliceCount)</c> is disjoint from every other worker's slice.
    /// </remarks>
    internal void ExecuteMigrationsSlice(ArchetypeMetadata meta, int sliceStart, int sliceCount, ChangeSet changeSet, List<DirtyBitDelta> dirtyBuffer = null)
    {
        if (sliceCount <= 0)
        {
            return;
        }

        var engineState = _archetypeStates[meta.ArchetypeId];
        var clusterState = engineState?.ClusterState;
        if (clusterState == null || clusterState.PendingMigrationCount == 0)
        {
            return;
        }

        ExecuteMigrations(clusterState, engineState, meta.ArchetypeId, sliceStart, sliceCount, changeSet, dirtyBuffer);
    }

    /// <summary>
    /// Apply a contiguous run of <see cref="DirtyBitDelta"/> entries to one archetype's <c>FenceDirtyBits</c>. Called from
    /// <c>FenceMigrateExecSystem.OnAfterChunk</c> after sorting the chunk's buffer by archetypeId so a single <c>_finalizeLock</c> acquisition covers the whole
    /// archetype run. Plain non-atomic bit writes are correct under the lock — clears and sets within a chunk operate on distinct (chunkId, slot) pairs by
    /// construction. Grows <c>FenceDirtyBits</c> on-demand under the same lock.
    /// </summary>
    internal void FlushDirtyBitDeltas(ushort archetypeId, List<DirtyBitDelta> buffer, int offset, int count)
    {
        if (count <= 0 || archetypeId >= _archetypeStates.Length)
        {
            return;
        }

        var clusterState = _archetypeStates[archetypeId]?.ClusterState;

        clusterState?.ApplyDirtyBitDeltas(buffer, offset, count);
    }

    /// <summary>
    /// Phase 3 of the parallel cluster tick fence: post-migration AABB recompute, dormancy sweep, dirty-ring archive, ComponentTable flag
    /// propagation, and WAL chunk serialization for the archetype's post-migration <see cref="ArchetypeClusterState.FenceDirtyBits"/>.
    /// Safe to call concurrently across DISTINCT archetypes. Returns the highest LSN published by this archetype's WAL chunks (0 if none).
    /// </summary>
    internal unsafe long FinalizeArchetypeFence(ArchetypeMetadata meta, long tickNumber, ChangeSet changeSet)
    {
        if (meta == null || !meta.IsClusterEligible || meta.ArchetypeId >= _archetypeStates.Length)
        {
            return 0;
        }
        var engineState = _archetypeStates[meta.ArchetypeId];
        var clusterState = engineState?.ClusterState;
        if (clusterState == null || clusterState.FenceBranchPath == 0)
        {
            return 0;
        }

        long highestLSN = 0;
        var dirtyBits = clusterState.FenceDirtyBits;
        
        // Reset the per-archetype pending-migration queue exactly once, AFTER all Migrate-phase slices finished and BEFORE we begin Finalize work.
        // Resetting inside ExecuteMigrationsSlice would race with sibling slices.
        clusterState.PendingMigrationCount = 0;

        // Drain pending cluster finalizations (review C-1 fix): ReleaseSlot during Migrate only records the chunkId; actual finalize + FreeChunk happens here,
        // after the Migrate/AabbRefresh phase barriers. By this point no concurrent ClaimSlotInCell can race with us — safe to free clean clusters.
        clusterState.DrainPendingClusterFinalizations(_spatialGrid);

        var accessor = clusterState.ClusterSegment.CreateChunkAccessor();
        try
        {

            // AABB recompute moved out of Finalize into the parallel AabbRefresh phase (FenceAabbRefreshExecSystem). Finalize is now responsible only for
            // the post-AABB bookkeeping clear + dormancy sweep + WAL emit. The serial WriteTickFence wrapper (no-WAL path) calls RecomputeDirtyClusterAabbs
            // directly before reaching FinalizeArchetypeFence, so it works equivalently.
            //
            // The bookkeeping clear lives here (single-threaded, per-archetype) — it ran inside the legacy RecomputeDirtyClusterAabbs tail before and must run
            // AFTER all AABB slices finished, which the phase barrier guarantees.
            if (clusterState.SpatialSlot.HasSpatialIndex && clusterState.SpatialSlot.FieldInfo.Mode == SpatialMode.Dynamic)
            {
                clusterState.ClearAabbRefreshBookkeeping();
            }

            // Clean-spatial-refresh branch (path 1) stops here — no dormancy sweep change (already swept clean), no WAL emit.
            if (clusterState.FenceBranchPath == 1)
            {
                return 0;
            }

            // Dormancy sweep with the final post-migration dirty bits.
            clusterState.DormancySweep(dirtyBits, tickNumber);

            // Archive dirty bitmap into per-archetype DirtyBitmapRing for spatial interest management.
            clusterState.ClusterDirtyRing?.Archive(tickNumber, dirtyBits, dirtyBits.Length);

            var entryCount = clusterState.FenceEntryCount;
            // Account for any net dirty-bit change from migrations: clears src bits, sets dst bits — net change is zero per migration in the common case, but a
            // destination chunk that was previously not in the snapshot grows it. For simplicity we recompute entryCount by popcount; the migration count is
            // small and this is one quick pass.
            if (clusterState.LastTickMigrationCount > 0)
            {
                var recomputed = 0;
                for (var i = 0; i < dirtyBits.Length; i++)
                {
                    if (dirtyBits[i] != 0)
                    {
                        recomputed += BitOperations.PopCount((ulong)dirtyBits[i]);
                    }
                }
                entryCount = recomputed;
            }

            if (entryCount == 0)
            {
                return highestLSN;
            }

            // Store dirty snapshot for change-filtered runtime dispatch.
            clusterState.PreviousTickDirtySnapshot = dirtyBits;

            // Propagate dirty status to ComponentTables for change-filtered runtime dispatch.
            for (var slot = 0; slot < clusterState.Layout.ComponentCount; slot++)
            {
                var table = engineState.SlotToComponentTable[slot];
                table.PreviousTickHadDirtyEntities = true;
                table.PreviousTickDirtyBitmap ??= Array.Empty<long>();
            }

            var layout = clusterState.Layout;
            var transientMask = meta.TransientSlotMask;
            // Precompute the durable (non-transient) component slots' WAL identity once per archetype. Each becomes one Slot record
            // per dirty entity (M4); the entity PK is read from the cluster's id array, so fence records are logical, never physical.
            Span<int> durableSlots = stackalloc int[layout.ComponentCount];
            Span<ushort> slotTypeIds = stackalloc ushort[layout.ComponentCount];
            var durableCount = 0;
            for (var slot = 0; slot < layout.ComponentCount; slot++)
            {
                if ((transientMask & (1 << slot)) != 0)
                {
                    continue;
                }

                durableSlots[durableCount] = slot;
                slotTypeIds[durableCount] = (ushort)ArchetypeRegistry.GetComponentTypeId(engineState.SlotToComponentTable[slot].Definition.POCOType);
                durableCount++;
            }

            // One arena per thread — fence emission is concurrency-safe across distinct archetypes.
            var fenceArena = _fenceArena ??= new CommitBatchArena();
            fenceArena.Reset();
            var batch = new CommitBatchBuilder(fenceArena, tickNumber, 0, fenceMode: true);
            var batchBytes = 0;
            var recOverhead = RecordHeader.SizeInBytes + SlotRecordBody.FixedSize;

            for (var wi = 0; wi < dirtyBits.Length; wi++)
            {
                var word = dirtyBits[wi];
                while (word != 0)
                {
                    var bit = BitOperations.TrailingZeroCount((ulong)word);
                    word &= word - 1;
                    var slotIndex = bit;

                    var clusterBase = accessor.GetChunkAddress(wi);
                    var entityPk = *(long*)(clusterBase + layout.EntityIdsOffset + slotIndex * 8);

                    for (var d = 0; d < durableCount; d++)
                    {
                        var slot = durableSlots[d];
                        var compSize = layout.ComponentSize(slot);

                        // Flush before the frame would exceed the per-Append cap (fence records are individually committed).
                        if (batchBytes > 0 && batchBytes + recOverhead + compSize > MaxFenceBatchBytes)
                        {
                            highestLSN = Math.Max(highestLSN, AppendFenceBatch(ref batch));
                            fenceArena.Reset();
                            batch = new CommitBatchBuilder(fenceArena, tickNumber, 0, fenceMode: true);
                            batchBytes = 0;
                        }

                        var src = clusterBase + layout.ComponentOffset(slot) + slotIndex * compSize;
                        // Wire identity is the per-archetype slot (LOG-06); `slot` is that index (durableSlots[d]).
                        batch.AddSlot(entityPk, (ushort)slot, new ReadOnlySpan<byte>(src, compSize));
                        batchBytes += recOverhead + compSize;
                    }
                }
            }

            if (!batch.IsEmpty)
            {
                highestLSN = Math.Max(highestLSN, AppendFenceBatch(ref batch));
            }
        }
        finally
        {
            accessor.Dispose();
        }
        return highestLSN;
    }
}
