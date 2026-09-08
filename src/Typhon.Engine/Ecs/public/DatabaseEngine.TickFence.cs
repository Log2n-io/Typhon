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
    /// Closes a tick: publishes the tick's pending work and establishes the crash-recovery boundary. <b>Must be called once per tick.</b>
    /// </summary>
    /// <remarks>
    /// <para><b>Four responsibilities</b>, not one:</para>
    /// <list type="bullet">
    ///   <item>serializes dirty <c>SingleVersion</c> component data to the WAL — one TickFence chunk per SV ComponentTable;</item>
    ///   <item>executes the tick's pending <b>cluster migrations</b> (entities that crossed a spatial cell boundary);</item>
    ///   <item>recomputes <b>cluster spatial AABBs</b> and the per-cell spatial index;</item>
    ///   <item>drains <b>zone maps</b> and dirty-bit deltas.</item>
    /// </list>
    /// <para><b>Under <see cref="TyphonRuntime"/> this is automatic</b> — the runtime calls it from its tick-end callback and
    /// application code does not. <b>A host that does NOT use the runtime must call it itself, once per tick</b>, after that tick's writes have been
    /// committed and before the next tick begins. See the embedding guide (<c>doc/guide/embedding-without-the-runtime.md</c>).</para>
    /// <para><b>What happens if it is never called.</b> Nothing throws and nothing is corrupted — the database silently stops maintaining itself:
    /// <c>SingleVersion</c> components degrade to <c>Transient</c>-like durability (no crash recovery), queued cluster migrations never execute, and
    /// spatial AABBs and zone maps go stale, so spatial queries return results computed against old positions.</para>
    /// <para><b>Threading — a caller obligation, not an enforced one</b> (rule <c>EW-01</c>). No other thread may mutate the database while this runs: the
    /// fence rewrites cluster B+Trees, the EntityMap, the per-cell spatial index and the cluster-to-cell map, and a concurrent writer corrupts them silently.
    /// Under <see cref="TyphonRuntime"/> it holds by construction — the fence runs from the scheduler's tick-end callback, after every system has completed,
    /// and each system's Transaction is committed and disposed in its own epilogue. A host driving the fence itself owns the same guarantee; trivially
    /// satisfied single-threaded, unchecked otherwise.</para>
    /// <para><b>The one trap inside the runtime is a side transaction.</b> <c>TickContext.CreateSideTransaction</c> hands back an ORDINARY transaction, so it
    /// can write an indexed field and mutate a B+Tree, and the caller owns its <c>Commit</c> and <c>Dispose</c> — nothing joins it to the tick. A side
    /// transaction held past the end of the system that created it, and committed while the fence runs, is exactly the concurrent mutation this contract
    /// forbids. Commit and dispose it before the system returns.</para>
    /// <para><b>Checkpointing is separate and automatic</b> (timer, dirty-page threshold, back-pressure, graceful shutdown).
    /// <see cref="ForceCheckpoint"/> is not part of the tick loop.</para>
    /// </remarks>
    /// <param name="tickNumber">Monotonic tick identifier. Must increase between calls.</param>
    /// <param name="changeSet">ChangeSet for dirty-page tracking across the whole fence. Under the runtime this is the per-tick UoW's shared ChangeSet
    /// (see <see cref="UnitOfWork.ChangeSet"/>), which consolidates the fence's dirty pages with the rest of the tick. <b>Pass <c>null</c> when driving
    /// the fence from a host without a per-tick UoW</b> — this method then creates and commits a one-shot ChangeSet itself, which is correct but yields
    /// per-fence rather than per-tick consolidation.</param>
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
            // EW-01's window, opened where the rule's scope says it opens. It enrols this thread, so the fence's own serial writes pass; a mutation of a
            // fence-owned structure from any other thread while this runs throws at the mutation site instead of corrupting it silently.
            using var window = EpochManager.FenceWindow.Open();
            highestLSN = WriteTickFenceCore(tickNumber, changeSet);
        }
        finally
        {
            if (ownChangeSet)
            {
                changeSet.SaveChanges();
                changeSet.ReleaseDirtyMarks();
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

    /// <summary>Descriptor capacity of one fence-block batch, sized so that <see cref="MaxFenceBatchBytes"/>, not this, closes a batch (#886).</summary>
    internal const int MaxFenceBatchBlocks = 256;

    private long AppendFenceBatch(ref CommitBatchBuilder batch)
    {
        var wc = WaitContext.FromDeadline(Deadline.FromTimeout(TimeoutOptions.Current.DefaultCommitTimeout));
        return DurabilityLog.Append(ref batch, ref wc);
    }

    /// <summary>Per-thread descriptor scratch for columnar fence emission — one entry per dirty cluster, payload-free (#559).</summary>
    [ThreadStatic]
    private static RecordCodec.FenceBlockDescriptor[] FenceBlocks;

    /// <summary>
    /// Per-thread arena for the collection-content batch that accompanies a columnar fence (#389). Separate from <see cref="_fenceArena"/>: the two are
    /// never live at once, but a cluster fence and a flat fence are different call paths and sharing one arena between them buys nothing.
    /// </summary>
    [ThreadStatic]
    private static CommitBatchArena FenceCollectionArena;

    // Histogram scratch for the serial fence's radix sorts — the staged EntityMap and index runs of ApplyStaged*. Serial by construction, so one per engine.
    private int[] _serialRadixCounts;

    private Span<int> SerialRadixCounts => _serialRadixCounts ??= new int[RadixSort.Buckets];

    /// <summary>
    /// Emits the full <c>ComponentCollection</c> content of every dirty entity in a cluster fence, as its own fence batch of CollectionDelta records.
    /// </summary>
    /// <remarks>
    /// A second walk of the same dirty bits the block loop used. It could have been folded into that loop, but the block loop's job is to describe contiguous
    /// slot RANGES for bulk copying, while this one is inherently per-entity and per-field — merging them would put a scalar-rate loop inside a bulk-rate one
    /// for the benefit of the rare archetype that carries a collection at all.
    /// </remarks>
    private unsafe long AppendClusterCollectionContent(
        ArchetypeEngineState engineState,
        long[] dirtyBits,
        ref ChunkAccessor<PersistentStore> accessor,
        int entityIdsOffset,
        ReadOnlySpan<int> slotIndices,
        ReadOnlySpan<int> componentSizes,
        ReadOnlySpan<int> componentOffsets,
        long tickNumber,
        int firstWord,
        int wordCount)
    {
        var arena = FenceCollectionArena ??= new CommitBatchArena();
        arena.Reset();
        var batch = new CommitBatchBuilder(arena, tickNumber, 0, true);
        var batchBytes = 0;
        long highestLSN = 0;

        var endWord = Math.Min(dirtyBits.Length, firstWord + wordCount);
        for (var wi = firstWord; wi < endWord; wi++)
        {
            var word = dirtyBits[wi];
            while (word != 0)
            {
                var slotIdx = BitOperations.TrailingZeroCount((ulong)word);
                word &= word - 1;

                var clusterBase = accessor.GetChunkAddress(wi);
                var entityId = *(long*)(clusterBase + entityIdsOffset + (slotIdx * sizeof(long)));
                if (entityId == 0)
                {
                    continue;   // unoccupied cluster slot
                }

                for (var c = 0; c < slotIndices.Length; c++)
                {
                    var table = engineState.SlotToComponentTable[slotIndices[c]];
                    if (!table.HasCollections)
                    {
                        continue;
                    }

                    var compSize = componentSizes[c];
                    var value = new ReadOnlySpan<byte>(clusterBase + componentOffsets[c] + (slotIdx * compSize), compSize);
                    batchBytes += CollectionContentEmitter.Emit(ref batch, table, entityId, (ushort)slotIndices[c], value);
                }

                if (batchBytes > MaxFenceBatchBytes)
                {
                    highestLSN = Math.Max(highestLSN, AppendFenceBatch(ref batch));
                    arena.Reset();
                    batch = new CommitBatchBuilder(arena, tickNumber, 0, true);
                    batchBytes = 0;
                }
            }
        }

        if (!batch.IsEmpty)
        {
            highestLSN = Math.Max(highestLSN, AppendFenceBatch(ref batch));
        }

        return highestLSN;
    }

    private long AppendFenceBlockBatch(
        RecordCodec.FenceBlockDescriptor[] blocks,
        int count,
        ushort archetypeId,
        long tickNumber,
        int entityKeysOffset,
        ReadOnlySpan<int> slotIndices,
        ReadOnlySpan<int> componentSizes,
        ReadOnlySpan<int> componentOffsets,
        int totalComponentSize,
        ReadOnlySpan<ulong> columnHandleRanges)
    {
        var wc = WaitContext.FromDeadline(Deadline.FromTimeout(TimeoutOptions.Current.DefaultCommitTimeout));
        return DurabilityLog.AppendFenceBlocks(
            blocks.AsSpan(0, count), archetypeId, tickNumber, entityKeysOffset,
            slotIndices, componentSizes, componentOffsets, totalComponentSize, columnHandleRanges, ref wc);
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
                var batch = new CommitBatchBuilder(fenceArena, tickNumber, 0, true);
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

                            // A chunk with no PK in its overhead is not a published entity. Routing ids start at 1, so
                            // EntityId.FromRaw(0).ArchetypeId indexes a null slot and the GetSlot below NREs — which, escaping the fence, freezes the
                            // runtime's tick counter and leaks that tick's UnitOfWork, silently in Release (#837). DIRTY-01 removed the one producer that
                            // could put such a chunk here (a spawn-staging id), but the deref itself was never guarded, so a future producer would
                            // reproduce #837 verbatim. Skip it, exactly as the cluster walker skips an unoccupied slot in AppendClusterCollectionContent.
                            if (entityPk == 0)
                            {
                                continue;
                            }

                            // Flush before the frame would exceed the per-Append cap. Fence records are individually committed, so
                            // splitting across Appends is safe; the codec splits each batch into RecordBatch chunks internally.
                            if (batchBytes > 0 && batchBytes + recOverhead + stride > MaxFenceBatchBytes)
                            {
                                highestLSN = Math.Max(highestLSN, AppendFenceBatch(ref batch));
                                walPublished = true;
                                fenceArena.Reset();
                                batch = new CommitBatchBuilder(fenceArena, tickNumber, 0, true);
                                batchBytes = 0;
                            }

                            // Wire identity is the per-archetype slot (LOG-06); resolve from this entity's archetype (routing id in the PK).
                            var slot = (ushort)GetMetaByRouting(EntityId.FromRaw(entityPk).ArchetypeId).GetSlot(componentTypeId);
                            var value = src.Slice(overhead, stride);
                            batch.AddSlot(entityPk, slot, value, table.CollectionHandleRanges);
                            batchBytes += recOverhead + stride;
                            if (table.HasCollections)
                            {
                                // Mandatory, not opportunistic: the Slot payload's handle is zeroed (LOG-06), so applying it without a fold to follow would
                                // empty a collection whose content was previously safe on the checkpoint timeline. See CollectionContentEmitter.
                                batchBytes += CollectionContentEmitter.Emit(ref batch, table, entityPk, slot, value);
                            }
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

            // The deferred shadow pass that used to run here maintained the per-ComponentTable indexes for non-Versioned components. Those indexes are gone
            // (#629) — a cluster-backed archetype maintains its own trees through the cluster shadow bitmap — and instrumenting this branch across the full
            // suite showed it was never entered. `hasShadow` is still reported on the tick-fence span below.

            // The per-entity spatial pass that used to run here rebuilt the entity-level R-Tree from this table's dirty bitmap. That tree is gone (#872 step
            // 13) — cluster AABBs are refreshed by the fence's own AabbRefresh phase, from cluster storage, in parallel — so there is nothing table-scoped
            // left to do. `hasSpatial` now gates NOTHING: the ring archive below is unconditional, and the flag's only remaining reader is the trace byte on
            // the tick-fence span, where it still answers "is this table spatial at all".

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
    /// <summary>
    /// Give a cluster state the means to build its shared per-cell R-Tree segment on first promotion (#872 step 9), and apply the configured thresholds.
    /// </summary>
    /// <remarks>
    /// <para>A factory rather than an eager segment: promotion is opt-in and, at the measured crossover, unreachable for most workloads — so the common case
    /// must cost nothing. A <see cref="ChunkBasedSegment{TStore}"/> is at least two pages, and paying that for every spatial archetype in every database to
    /// serve a case none of them reach is the kind of default that only shows up as a memory graph nobody can explain.</para>
    /// <para>Attached here rather than inside <see cref="ArchetypeClusterState"/> because that type is built by static factory methods holding no services,
    /// and giving it a <see cref="DatabaseEngine"/> reference to reach the allocator would couple the storage state to the engine for one lazy allocation.</para>
    /// </remarks>
    private ArchetypeClusterState AttachCellTreeFactory(ArchetypeClusterState state)
    {
        state.CellTreeSegmentFactory = stride =>
        {
            CreateTransientClusterSegment(stride, out var treeStore, out var treeSegment);
            return (treeSegment, treeStore!.Value);
        };
        state.CellTreePromoteThreshold = ClusterCellTreePromoteThreshold;
        state.CellTreeDemoteThreshold = ClusterCellTreePromoteThreshold == int.MaxValue ? int.MaxValue : ClusterCellTreePromoteThreshold / 2;
        state.CellTreePromoteTightness = ClusterCellTreePromoteTightness;
        // Twice the promote gate, and never past the cell itself: a mean of 1.0 is a cell whose clusters each span it, which is the loosest a bound
        // inside its own cell can read.
        state.CellTreeDemoteTightness = MathF.Min(1f, ClusterCellTreePromoteTightness * 2f);
        return state;
    }

    /// <summary>
    /// Clusters in one cell at which that cell's linear broadphase is replaced by an R-Tree. Seeded from
    /// <see cref="SpatialOptions.CellTreePromoteThreshold"/>; settable directly so a test can move the boundary without rebuilding the options graph.
    /// </summary>
    /// <remarks>
    /// Read once per archetype, by <see cref="AttachCellTreeFactory"/> during <c>InitializeArchetypes</c> — a later change does not reach archetypes that
    /// already exist. See <c>ArchetypeClusterState.CellTreePromoteThreshold</c> for the crossover argument and <c>BroadphaseCrossoverSweepTests</c> for the
    /// numbers behind the default.
    /// </remarks>
    internal int ClusterCellTreePromoteThreshold { get; set; }

    /// <summary>
    /// Mean cluster extent, as a fraction of the cell edge, at or below which a cell half may promote (<see cref="SpatialOptions.CellTreePromoteTightness"/>).
    /// Read once per archetype by <see cref="AttachCellTreeFactory"/>, like the count threshold beside it.
    /// </summary>
    internal float ClusterCellTreePromoteTightness { get; set; } = SpatialOptions.DefaultCellTreePromoteTightness;

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
            clusterState.IndexUpdates?.BeginTick(1);
            clusterState.EntityMapUpdates?.BeginTick(1);

            // The serial path has no FenceContext to carry RuntimeOptions, so it applies the same rule against the SAME CONSTANT the option defaults to —
            // not a copy of the value. Without this decision the flag keeps its `false` initial value, every serial migration takes the inline path, and
            // ApplyStagedEntityMapUpdates below becomes dead code no test would reach. The gain is real even at one worker: 402 vs 691 ns/migrant at ~1.8
            // entries per bucket. A runtime-less host cannot override the threshold; that is a documented limitation, not a drifted duplicate.
            var emBuckets = _archetypeStates[meta.ArchetypeId].EntityMap?.LiveBucketCount ?? 0;
            clusterState.UseBulkEntityMapUpdate = emBuckets <= 0
                || clusterState.PendingMigrationCount >= (long)(EntityMapUpdateStaging.DefaultMinEntriesPerBucket * emBuckets);
            ExecuteMigrationsSlice(meta, 0, clusterState.PendingMigrationCount, changeSet);

            // The serial path has no IndexMassUpdate phase to drain the staging, so it drains it here, on one part. Leaving this out is not a slow path but a
            // WRONG one: migration now STAGES its index value updates instead of applying them, so without a drain every migrated entity's index entry keeps
            // pointing at the cluster location it just left, and a query answers with a stale one.
            ApplyStagedIndexUpdates(clusterState, changeSet);
            ApplyStagedEntityMapUpdates(meta, clusterState, changeSet);
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
    /// Applies one archetype's staged EntityMap location patches, single-threaded, then folds visibility and rolls back any orphan (#872 step 7).
    /// </summary>
    /// <remarks>
    /// The serial fence's counterpart to <c>FenceEntityMapUpdateExecSystem</c>, and it is not an optimisation but a correctness requirement: migration now
    /// STAGES its location patches instead of applying them, so without this drain every migrated entity's EntityMap record keeps pointing at the cluster slot
    /// it just left — and once a later spawn reclaims that slot, the stale record resolves to an unrelated entity's bytes.
    /// <para>
    /// One part, no bucket partition: with a single worker there is nothing to keep apart, and the partition exists only so concurrent workers never share a
    /// bucket chunk. The batch still has to be SORTED, because that is what the run amortisation reads.
    /// </para>
    /// </remarks>
    internal void ApplyStagedEntityMapUpdates(ArchetypeMetadata meta, ArchetypeClusterState clusterState, ChangeSet changeSet)
    {
        var staging = clusterState?.EntityMapUpdates;
        if (staging == null)
        {
            return;
        }

        // #872 step 11: this phase's cost belongs to the migration that staged the work, and until now nothing measured it. The sort and the merge are
        // inside the bracket deliberately — at small batches they are the majority of the phase, and a cost model that skipped them would under-admit
        // exactly where the bulk path stops paying for itself.
        var applyStart = Stopwatch.GetTimestamp();
        try
        {
            staging.ClearPrepared();
            staging.SortChunk(0, SerialRadixCounts);
            var count = staging.MergeAndPartition(1);
            if (count == 0)
            {
                return;
            }

            var state = _archetypeStates[meta.ArchetypeId];
            var slice = staging.Prepared.AsSpan(0, count);
            var accessor = state.EntityMap.Segment.CreateChunkAccessor(changeSet);
            try
            {
                state.EntityMap.UpdateValuesBulk<EntityLocationUpdate, ClusterLocationBulkUpdater>(slice, ref accessor);
            }
            finally
            {
                accessor.Dispose();
            }

            for (var i = 0; i < slice.Length; i++)
            {
                ref var entry = ref slice[i];
                if (!entry.Found)
                {
                    clusterState.RollbackOrphanedDestinationSlot(entry.DstChunkId, entry.DstSlot, entry.EntityKey, changeSet);
                    continue;
                }

                clusterState.NoteClusterBorn(entry.DstChunkId, entry.ObservedBornTsn);
                if (entry.ObservedDiedTsn != 0)
                {
                    clusterState.NoteClusterDied(entry.DstChunkId, ArchetypeClusterState.VisibilityUnknown);
                }
            }
        }
        finally
        {
            Interlocked.Add(ref clusterState.LastTickMigrationApplyTicks, Stopwatch.GetTimestamp() - applyStart);
        }
    }

    /// <summary>
    /// Applies every indexed field's staged value updates for one archetype, single-threaded, in one partitioning descent per field (#872 step 6).
    /// </summary>
    /// <remarks>
    /// The serial fence's counterpart to <c>FenceIndexMassUpdateExecSystem</c>. It takes the same sort but skips the leaf-snapped partition entirely: with
    /// one worker there is nothing to keep apart, and the snap exists only so that concurrent workers never share a leaf.
    /// </remarks>
    internal void ApplyStagedIndexUpdates(ArchetypeClusterState clusterState, ChangeSet changeSet)
    {
        var staging = clusterState?.IndexUpdates;
        if (staging == null || staging.FieldCount == 0 || clusterState.IndexSlots == null)
        {
            return;
        }

        // #872 step 11: the largest single component of a migration's cost — measured at ~48 % — and until now entirely outside every timer.
        var applyStart = Stopwatch.GetTimestamp();
        try
        {
            for (var fieldId = 0; fieldId < staging.FieldCount; fieldId++)
            {
                if (staging.StagedBytes(fieldId) == 0)
                {
                    continue;
                }

                var fieldRef = staging.Field(fieldId);
                ref var field = ref clusterState.IndexSlots[fieldRef.SlotIndex].Fields[fieldRef.FieldIndex];
                var multi = field.AllowMultiple;

                // The parallel path's Migrate workers sort their own chunk before leaving it; this path has no workers, so it sorts its single run here. The
                // merge below then sees one sorted run and copies it, which is the degenerate case it already handles.
                var run = staging.ChunkSpan(0, fieldId);
                field.Index.SortBulkEntries(run, staging.SortScratch(0, run.Length), SerialRadixCounts, multi);

                var merged = staging.MergeSortedRuns(fieldId, field.Index.BulkEntryStride(multi), field.Index, multi, out var byteCount);
                if (byteCount == 0)
                {
                    continue;
                }

                var accessor = field.Index.Segment.CreateChunkAccessor(changeSet);
                try
                {
                    field.Index.ApplyBulkEntries(merged.AsSpan(0, byteCount), multi, ref accessor, out _);
                }
                finally
                {
                    accessor.Dispose();
                }
            }

            staging.BeginTick(1);   // consume: the entries have been applied and must not be applied again next tick
        }
        finally
        {
            Interlocked.Add(ref clusterState.LastTickMigrationApplyTicks, Stopwatch.GetTimestamp() - applyStart);
        }
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
        // Worker-local deferral buffer for promoted cells, same shape and lifetime as the outlier buffer above and merged the same way. Allocated only when
        // this archetype actually has a promoted cell — the overwhelmingly common case is none, and an empty List per slice per tick is not free.
        var promotedBuffer = clusterState.PromotedCellCount > 0 ? new List<ArchetypeClusterState.PromotedAabbApply>(0) : null;
        // Worker-local repair nominations (#872 step 12), merged the same way as the outlier buffer above — but held PER WORKER rather than allocated per
        // slice. With ReclusterBudgetMs at its default of 1.0 this path is live out of the box, so a fresh List per slice per tick is a real per-tick
        // allocation on the fence; step 11 also doubled the element width, so each growth doubling costs twice what it did. EnqueueRepairNominationsBulk
        // clears it after the merge, so a reused list starts every slice empty. Still gated on the budget: with repair switched off the planner discards
        // nominations unread, and producing them would be pure cost on the detection path.
        var repairBuffer = _spatialGrid != null && _spatialGrid.Config.ReclusterBudgetMs > 0f
            ? ArchetypeClusterState.NominationScratch ??= [] : null;
        try
        {
            clusterState.RecomputeDirtyClusterAabbsSlice(sliceStart, sliceCount, ref accessor, _spatialGrid, promotedBuffer, outlierBuffer, repairBuffer,
                out var aabbsChanged, out var slotsScanned, out var outlierGuardFires, out var clustersScanned, out var driftersDetected,
                out var driftAbsorbed, out var driftersUnplaced, out var driftGatedClusters, out var driftSuppressedByDensity,
                out var driftersUnplacedNoCandidate, out var driftersSpilled, out var tightness);
            // Interlocked, not plain adds: every AabbRefresh slice of this archetype reaches here, and the three counters are archetype-wide. They are reset
            // once per tick in PrepareArchetypeFence, so a lost add would under-report for that tick only — which is exactly the kind of quiet inaccuracy that
            // makes a measurement useless for tuning P4.
            Interlocked.Add(ref clusterState.LastTickClustersScanned, clustersScanned);
            Interlocked.Add(ref clusterState.LastTickSlotsScanned, slotsScanned);
            Interlocked.Add(ref clusterState.LastTickDriftersDetected, driftersDetected);
            Interlocked.Add(ref clusterState.LastTickDriftAbsorbedCount, driftAbsorbed);
            Interlocked.Add(ref clusterState.LastTickDriftersUnplaced, driftersUnplaced);
            Interlocked.Add(ref clusterState.LastTickDriftGatedClusters, driftGatedClusters);
            Interlocked.Add(ref clusterState.LastTickDriftSuppressedByDensity, driftSuppressedByDensity);
            Interlocked.Add(ref clusterState.LastTickDriftersUnplacedNoCandidate, driftersUnplacedNoCandidate);
            Interlocked.Add(ref clusterState.LastTickDriftersSpilled, driftersSpilled);
            clusterState.FoldTightnessSample(in tightness);
            clusterState.EnqueueMigrationsBulk(outlierBuffer);
            clusterState.EnqueuePromotedAppliesBulk(promotedBuffer);
            clusterState.EnqueueRepairNominationsBulk(repairBuffer);
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
    internal bool PrepareArchetypeFence(ArchetypeMetadata meta, long tickNumber, ChangeSet changeSet)
    {
        var hasWork = PrepareArchetypeFenceCore(meta, tickNumber, changeSet);

        // ── The drain prefix, recorded on the way out and NOWHERE ELSE ────────────────────────────────────────────────
        //
        // Whatever is queued when Prep finishes is exactly what this tick's Migrate phase will execute: the parallel work
        // plan is built from PendingMigrationCount after Prep returns, and the serial path passes that same count straight
        // to ExecuteMigrationsSlice. Requests filed LATER — by FlagOutliersForMigration and by step 10's drift detection,
        // both of which run inside AabbRefresh, after Migrate — land beyond the prefix and must survive to the next tick.
        //
        // This lives in a wrapper rather than at the bottom of the body because the body has THREE exits, and taking the
        // one that skips this is not an edge case. An archetype written through the spatial barrier sets ClusterProcessBitmap
        // and leaves ClusterDirtyBitmap clean, so it leaves through the clean-bitmap `return true` on every ordinary tick.
        // With the snapshot inside the body, that archetype recorded a drain prefix of zero while Migrate executed the whole
        // queue, Finalize then compacted nothing, and the queue grew by its drifter count every tick — with the entire
        // backlog re-executing each time. Measured before the fix: 16 000 entities produced 17 234 migrations on the first
        // tick and 224 854 on the twentieth, against ~10 900 genuine drifters per tick.
        //
        // Worth being blunt about, because it is strictly worse than the bug it replaced. The old code reset the count in
        // Finalize, which discarded the AabbRefresh producers' work silently but at least kept the queue bounded; a prefix
        // that is wrong in the other direction re-executes stale requests against slots their entities have already left.
        // Guarded exactly as the core's own preamble guards, and for the same reasons: WriteClusterTickFence walks every
        // archetype from GetAllArchetypes() with no filter, so `meta` may be null or carry an id past the state array — the
        // two conditions the core returns false for. Reading the array before re-testing them made the wrapper throw on the
        // inputs its own body was written to reject.
        if (meta == null || meta.ArchetypeId >= _archetypeStates.Length)
        {
            return hasWork;
        }

        var pending = _archetypeStates[meta.ArchetypeId]?.ClusterState;
        if (pending != null)
        {
            FinishArchetypeFencePrep(pending, hasWork, tickNumber, changeSet);        }

        return hasWork;
    }


    /// <summary>Per-tick state every Prep path starts from — the atomic item and the sliced head alike (#886 lead D).</summary>
    private void ResetArchetypeFenceTickState(ArchetypeClusterState clusterState)
    {
        clusterState.ResetPrepSliceState();
        clusterState.FinalizeHeadRan = false;
        clusterState.FinalizeSliceable = false;
        clusterState.FenceBranchPath = 0;
        clusterState.FenceDirtyBits = null;

        // The pending-migration queue is deliberately NOT cleared here, nor in Finalize where it used to be. See
        // ArchetypeClusterState.PendingMigrationDrainCount: the queue has producers on both sides of its consumer, so a per-tick reset destroyed
        // everything filed by the AabbRefresh-side detectors. Finalize now compacts away exactly the prefix that was executed.
        clusterState.FenceEntryCount = 0;
        clusterState.FenceDirtyClusterCount = 0;
        clusterState.FenceProcessBitmapClusterCount = -1; // recomputed in Prep when in BarrierOnly mode
        // Snapshot before zeroing: DetectClusterMigrations pre-sizes PendingMigrations from last tick's migration count, and reading the field itself would read
        // the zero written on the line below — same fence, a few hundred lines earlier — so the estimate was always Max(16, 0) and the queue regrew from 16 on
        // every migration-heavy tick (#872).
        // #872 step 11: BEFORE the counters below are zeroed, because it reads them. One sample per tick, folded into the per-entity cost model the repair
        // budget is spent against — which is what makes RepairNsPerEntity a seed rather than the operative constant.
        if (_spatialGrid != null)
        {
            clusterState.ObserveMigrationCost(in _spatialGrid.Config, _lastFenceMigrationParallelism);
        }

        // Step 14 (D2): last tick's throttle verdict raises or decays the intra-cell target before the counters it reads are zeroed.
        if (_spatialGrid != null)
        {
            clusterState.UpdateDriftTargetBoost(in _spatialGrid.Config);
        }
        clusterState.ResetThrottleTickState();
        clusterState.ResetPrepSubSpans();
        clusterState.PreviousTickMigrationCount = clusterState.LastTickMigrationCount;
        clusterState.LastTickMigrationCount = 0;
        clusterState.LastTickMigrationExecuteMs = 0d;
        // #872 step 11: the apply phases accumulate into this from their own workers, exactly as the Migrate slices do into the line above — and it is
        // zeroed the same PLAIN way, deliberately. Both counters are published by workers (an Interlocked.Add here, a compare-exchange loop there) and both
        // are reset from Prep, and what orders the reset against those publications is the fence phase barrier, not a release on the store. Giving one of
        // the pair a Volatile.Write and not the other would imply a distinction between them that does not exist.
        clusterState.LastTickMigrationApplyTicks = 0L;
        clusterState.LastTickClustersScanned = 0;
        clusterState.LastTickSlotsScanned = 0;
        clusterState.LastTickDriftersDetected = 0;
        clusterState.LastTickDriftAbsorbedCount = 0;
        clusterState.LastTickDriftGatedClusters = 0;
        clusterState.LastTickDriftSuppressedByDensity = 0;
        clusterState.LastTickCellTreePromotions = 0;
        clusterState.LastTickCellTreeDemotions = 0;
        // #911 O2. Zeroed with the rest of the per-tick block for the same reason: the AabbRefresh slices that produce them run AFTER this, and a tick whose
        // refresh scans nothing must report no samples rather than the previous tick's mean — which is the distinction the sample count exists to carry.
        clusterState.LastTickTightnessSamples = 0;
        clusterState.LastTickTightnessExtentSum = 0d;
        clusterState.LastTickTightnessBoundSum = 0d;

        // LastTickHysteresisAbsorbedCount was NOT reset here until #872, and DetectClusterMigrations only ever ASSIGNED it (=, not +=). A tick in which
        // detection did not run therefore reported the PREVIOUS tick's absorbed count as if it were this tick's — a stale reading indistinguishable from a live
        // one, in the one counter that tunes MigrationHysteresisRatio. Detection is reached on a quiet tick only through the clean-bitmap branch below, which is
        // gated on ActiveClusterCount > 0, so an archetype that empties out stopped updating it entirely.
        //
        // Drain rather than plain-zero: on the SpatialBarrierOnly path the count is accumulated live by ClusterRef.MaybeFlagMigration as writes happen, because
        // that path never reaches the scan that would otherwise count it. Both producers now compose — this drains the live one, and DetectClusterMigrations
        // adds its scan's tally with += rather than clobbering. Exactly one of the two is ever non-zero for a given archetype.
        var absorbedLive = clusterState.HysteresisAbsorbedLive;
        clusterState.HysteresisAbsorbedLive = 0;
        clusterState.LastTickHysteresisAbsorbedCount = absorbedLive;
        clusterState.TotalHysteresisAbsorbedCount += absorbedLive;

        // ReclusterBudgetUsedMs is produced by the step-12 repair planner, which runs in the Prep WRAPPER — after this body returns — so this reset always
        // precedes the write and never clobbers it. It reports the PROJECTED spend of the units admitted this tick, not a measured elapsed time: the budget
        // decides admission before any work happens (AC-12.5), so the number that gates has to be the number that is reported. Step 11 replaces the constant
        // behind the projection with a controller driven by the previous tick's measurement.
        // ClustersScanned / DriftersDetected / DriftAbsorbed used to be reset HERE too, on the same "no producer yet" grounds. Step 10 gave them one, and their
        // reset moved up beside LastTickMigrationCount where the rest of the per-tick counters live — a second zeroing of two of the three was worse than
        // redundant, because it silently excluded the third and would have hidden a producer that ran between the two blocks.
        clusterState.LastTickReclusterBudgetUsedMs = 0d;
        clusterState.LastTickRepairedEntityCount = 0;
        clusterState.LastTickRepairUnitCount = 0;
        clusterState.LastTickRepairUnitsRefused = 0;
        clusterState._drainedCount = 0; // deferred-drain list reset (review C-1 fix)
    }

    /// <summary>Step ⑧: the pre-size and the process-bitmap memo, run once per archetype from the tail of <see cref="FinishArchetypeFencePrep"/>.</summary>
    private void PreSizeArchetypeFence(ArchetypeClusterState clusterState)
    {
        // Pre-size FenceDirtyBits + per-cluster arrays to a generous upper bound so the Migrate phase (parallel or serial) doesn't hit ExecuteMigrations'
        // on-demand grow path under normal conditions. The strict bound (PrimarySegmentCapacity + PendingMigrationCount) under-estimates in practice when
        // multiple Migrate workers each allocate new clusters and inter-archetype shadow/index allocations also grow segments — observed dstChunkId values
        // exceeded this bound under AntHill loads. The doubled-plus-buffer bound covers worst-case interleavings; the cost is ~32KB extra per archetype,
        // trivial. On-demand grow under _finalizeLock (ArchetypeClusterState.GrowFenceDirtyBitsForChunkId) remains as a safety net for pathological cases.
        var existingLen = clusterState.FenceDirtyBits?.Length ?? 0;
        var upperBound = Math.Max(clusterState.PrimarySegmentCapacity, existingLen) + 2 * clusterState.PendingMigrationCount + 64;
        // PerCellIndex is indexed by CELL key, so its bound comes from the grid rather than the segment. A migration's destination cell was created by
        // crossing detection back in Prep, so the current cell count already covers every key the Migrate phase can name; the doubling is the same kind of
        // slack the cluster bound carries, and it is what keeps AddClusterToPerCellIndex off the growth path when it runs from a worker.
        var cellUpperBound = _spatialGrid != null ? 2 * _spatialGrid.CellCount + 64 : 0;
        var preSizeStart = Stopwatch.GetTimestamp();
        clusterState.PreSizeMigrationBuffers(upperBound, cellUpperBound);
        clusterState.PrepPreSizeTicks += Stopwatch.GetTimestamp() - preSizeStart;

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
    }

    /// <summary>Steps ⑥ and ⑦ and the drain prefix: serial by TH-01 and RP-02, run once per archetype after the whole map — atomic item or tail.</summary>
    private void FinishArchetypeFencePrep(ArchetypeClusterState pending, bool hasWork, long tickNumber, ChangeSet changeSet)
    {
        // ── #872 step 12: plan the repair BEFORE the prefix is taken ────────────────────────────────────────────────
        //
        // The requests a repair emits are meant for THIS tick's Migrate phase, not the next one, and the prefix below is
        // what decides that. The whole reason planning lives in Prep rather than beside the nomination that feeds it is
        // that the fresh destination clusters it allocates must be created and filled inside one exclusive window — with
        // a tick in between, an ordinary spawn's first-fit scan would claim into them, and the sorted packing the planner
        // just computed would be handed to the placement policy this issue exists to repair.
        //
        // Gated on hasWork, and the nominations are DISCARDED rather than carried when it is false. An archetype whose
        // Prep found nothing has FenceBranchPath == 0, so neither Migrate nor Finalize runs for it this tick: requests
        // filed here would sit unexecuted and the clusters allocated for them would have no sweep to free them. Dropping
        // the nomination costs one deferred repair — the cluster re-nominates the next time it is written.
        //
        // KNOWN GAP, still open after step 11, and narrowed rather than closed. In barrier-only mode the AABB pass visits
        // only clusters WRITTEN this tick, so a cell that degrades and then goes completely still is never re-NOMINATED.
        // Step 11's queue fixes the half that was in reach — a nomination the budget refuses, or one that arrives on a tick
        // that cannot plan, is now REMEMBERED rather than discarded, so a cell nominated once while it was moving is still
        // repaired after it stops. What remains needs `hasWork |= queue.Count > 0`, which re-arms Migrate and Finalize for
        // an otherwise idle archetype and collides head-on with AC-10.8 ("a tick with no movement does no relocation work
        // and allocates nothing"). That trade is bigger than this step and is deliberately not taken here.
        //
        // ── The planner runs BEFORE the throttle, and the ordering is the policy (§5.6, revised by §5.8.3) ──────────
        //
        // One budget, spent in priority order: cell crossings are correctness and take what they need; REPAIR takes the
        // next claim; intra-cell relocations get the remainder. The reverse order shipped first, on the argument that a
        // rare repair must not outbid the steady-state path — and measured the opposite: relocations consumed the whole
        // budget on every tick at every budget (relocationSpendMs == budget up to 16 ms), the planner entered with a
        // median 630 ns of 8 ms, and the one unit per tick that ran was the safety valve. Greedy relocation has no
        // gradient in a cell whose boxes all span it, so what it bought with that budget was net widening. Repair is the
        // mechanism that converges unconditionally, so it is charged first; what it commits is pre-charged to the
        // throttle, which still charges crossings and refuses relocations against what is left (step 14, D2).
        if (hasWork)
        {
            var tailStart = Stopwatch.GetTimestamp();
            var budgetNs = _spatialGrid != null ? _spatialGrid.Config.ReclusterBudgetMs * 1_000_000d : 0d;
            var crossingsNs = _spatialGrid != null ? pending.PendingMandatoryCostNs(in _spatialGrid.Config) : 0d;
            var repairCommittedNs = 0d;
            // Zeroed here, not inside the planner: PlanArchetypeRepairs returns early on an empty queue without touching it, and a stale value from the
            // last tick that DID plan would be pre-charged to a throttle that owes nothing.
            pending.LastTickReclusterBudgetUsedMs = 0d;
            if (budgetNs > 0d)
            {
                PlanArchetypeRepairs(pending, changeSet, tickNumber, Math.Max(0d, budgetNs - crossingsNs));
                repairCommittedNs = pending.LastTickReclusterBudgetUsedMs * 1_000_000d;
            }
            else
            {
                // Zero budget disables repair (AC-11.8) but not the absorb: the queue must keep receiving nominations so a budget raised at runtime
                // finds candidates rather than an empty queue.
                PlanArchetypeRepairs(pending, changeSet, tickNumber, 0d);
            }

            var planEnd = Stopwatch.GetTimestamp();
            pending.PrepPlanTicks += planEnd - tailStart;

            pending.ApplyMigrationThrottle(_spatialGrid, repairCommittedNs);
            pending.PrepThrottleTicks += Stopwatch.GetTimestamp() - planEnd;
        }
        else
        {
            // The LIST is per-tick even though the QUEUE is not: it describes the tick that produced it. Absorbed into the
            // persistent queue first — that is exactly the "refused or unplannable nomination is no longer lost" half above
            // — and only then cleared.
            pending.AbsorbRepairNominations(_spatialGrid, tickNumber);
        }

        pending.PendingMigrationDrainCount = hasWork ? pending.PendingMigrationCount : 0;

        // Every producer has now filed: crossings and the outlier guard in the core above, relocations carried from last tick's AabbRefresh, and repair
        // units from the planner. CR-05 is checkable exactly here and nowhere earlier (#877).
        pending.AssertNoDuplicateMigrationSources(tickNumber);

        // ⑧ LAST, and here rather than at the core's branch-2 exit where it used to sit. Two things were wrong with that placement and both are the same
        // mistake — sizing against numbers that were not final yet:
        //   * the core has THREE exits and only one of them reached the pre-size, so an archetype leaving through the clean-bitmap `return true` (which is
        //     every ordinary tick for one written through the spatial barrier) had its arrays sized by whichever earlier tick happened to take branch 2;
        //   * it ran BEFORE PlanArchetypeRepairs, which allocates destination clusters and files further migration requests — so both terms of the bound,
        //     PrimarySegmentCapacity and PendingMigrationCount, were read before the producer that moves them furthest had run.
        // Measured: a 3 000-entity archetype migrating 464 clusters reached the Migrate phase with ClusterSpatialIndexSlot 64 long and a chunk id of 64 to
        // record. That used to be an unsynchronised Array.Resize on a worker thread; it is now a refusal, which is how the placement bug became visible.
        PreSizeArchetypeFence(pending);

        // LAST, so the probe observes the archetype exactly as the Migrate phase will find it — including the pre-size, which is the thing the phase depends
        // on most and the thing a test most needs to be able to perturb.
        ArchetypeClusterState.PrepQueueProbe?.Invoke(pending, tickNumber);
    }

    /// <summary>How many active clusters an archetype needs before its Prep is worth slicing: two slices' worth, so that one worker does not open two accessors
    /// for the work one would have done.</summary>
    /// <remarks>A static rather than a const so the partition harness can switch the sliced path off in the same binary (<c>--no-prep-slice</c>).</remarks>
    internal static int PrepSliceMinClusters = 2 * FenceWorkPlan.PrepSliceWords;

    /// <summary>
    /// Phase 1, serial head (#886 lead D): decides per archetype whether this tick's Prep runs as slices, and does the part of Prep that must precede every
    /// slice — the snapshot, the written-slot exchange, the queue and zone-map pre-grows, and the one-per-tick zone-map rotation. Runs on the driver in
    /// <c>FencePrepExecSystem.Prepare</c>, before the plan is built, so the planner can slice <c>FenceDirtyBits</c> by word range.
    /// </summary>
    /// <remarks>
    /// An archetype that does not qualify is left entirely alone: its single <c>ArchetypePrep</c> item runs <see cref="PrepareArchetypeFence"/> exactly
    /// as before, snapshot included. Branch 1 (clean spatial refresh), the pure-Transient path and <c>SpatialBarrierOnly</c> archetypes never qualify —
    /// none of them has the drain that makes slicing worth a barrier's worth of accessors.
    /// </remarks>
    internal void PrepareArchetypeFenceHeads(int workerCount)
    {
        var states = _archetypeStates;
        if (states == null || workerCount < 2)
        {
            // One worker gains nothing from slices and pays for every one of them: Matrix W measured the sliced Prep at W = 1 as 3.63 ms against the
            // atomic item's 3.03. Slicing starts at two workers, where it already halves the phase.
            return;
        }

        foreach (var meta in ArchetypeRegistry.GetAllArchetypes())
        {
            if (!meta.IsClusterEligible || meta.ArchetypeId >= states.Length)
            {
                continue;
            }

            var clusterState = states[meta.ArchetypeId]?.ClusterState;
            if (clusterState == null)
            {
                continue;
            }

            clusterState.PrepSliceable = false;
            if (clusterState.ClusterSegment == null || clusterState.SpatialBarrierOnly || clusterState.ActiveClusterCount < PrepSliceMinClusters
                || !clusterState.ClusterDirtyBitmap.HasDirty)
            {
                continue;
            }

            ResetArchetypeFenceTickState(clusterState);

            var subSpan = Stopwatch.GetTimestamp();
            var dirtyBits = clusterState.ClusterDirtyBitmap.Snapshot();
            clusterState.PrepSnapshotTicks += Stopwatch.GetTimestamp() - subSpan;

            // Snapshot-and-clear the written-slot union in the same step as the dirty bitmap (#559 §4.5), so Finalize reads a stable value while writers
            // for the NEXT tick start from zero.
            clusterState.FenceWrittenSlots = Interlocked.Exchange(ref clusterState.WrittenSlotUnion, 0);
            clusterState.FenceDirtyBits = dirtyBits;
            clusterState.FenceBranchPath = 2;

            // Everything a slice must never grow.
            clusterState.EnsurePendingMigrationCapacityForTick();

            // Step (a) of detection — the crossings WriteSpatial flagged at write time — is whole-bitmap and appends to the shared queue, so it runs
            // here, once, and the slices run step (b) only. The queue order is then the unsliced one: (a) first, (b) after.
            if (clusterState.SpatialSlot.HasSpatialIndex && clusterState.SpatialSlot.FieldInfo.Mode == SpatialMode.Dynamic)
            {
                var preFlagged = 0;
                var preFlaggedClusters = 0;
                // The head runs before the slices' Execute enters its epoch, and the accessor's Debug gate requires one.
                using var headEpoch = EpochGuard.Enter(EpochManager);
                var headAccessor = clusterState.ClusterSegment.CreateChunkAccessor();
                try
                {
                    DrainPreFlaggedMigrations(clusterState, meta.ArchetypeId, ref headAccessor, ref preFlagged, ref preFlaggedClusters);
                }
                finally
                {
                    headAccessor.Dispose();
                }
            }

            BeginZoneMapTick(clusterState);
            clusterState.EnsureZoneMapCapacity(Math.Max(clusterState.PrimarySegmentCapacity, dirtyBits.Length));
            clusterState.BuildShadowDrainPlans();
            clusterState.PrepSliceable = true;
        }
    }

    /// <summary>
    /// Phase 1, one slice (#886 lead D): steps ② ③ ④ ⑤ over the dirty-bitmap words <c>[firstWord, firstWord + wordCount)</c> of one archetype, with a
    /// private accessor, the head's drain plan and a pooled, item-private crossing list. Every write is to a chunk-id-indexed entry inside the range, to a tree
    /// node under its own latch, to a multi-producer ring, or to an <c>Interlocked</c> fold.
    /// </summary>
    internal unsafe void RunPrepSlice(ArchetypeMetadata meta, int firstWord, int wordCount, ChangeSet changeSet, List<MigrationRequest> crossings)
    {
        var engineState = _archetypeStates[meta.ArchetypeId];
        var clusterState = engineState?.ClusterState;
        if (clusterState == null || !clusterState.PrepSliceable)
        {
            return;
        }

        var dirtyBits = clusterState.FenceDirtyBits;
        var end = Math.Min(dirtyBits.Length, firstWord + wordCount);
        var clusterScope = TyphonEvent.BeginWriteTickFenceCluster(meta.ArchetypeId);
        var accessor = clusterState.ClusterSegment.CreateChunkAccessor();
        ArchetypeClusterState.EnterPrepSlice();
        ArchetypeClusterState.NotePrepSliceRun();
        try
        {
            // ② mask this range's dirty bits with live occupancy, so destroyed entities whose dirty bit remained set drop out of every later step.
            var entryCount = 0;
            var dirtyClusterCount = 0;
            var subSpan = Stopwatch.GetTimestamp();
            for (var i = firstWord; i < end; i++)
            {
                if (dirtyBits[i] == 0)
                {
                    continue;
                }

                var occupancy = *(ulong*)accessor.GetChunkAddress(i);
                dirtyBits[i] &= (long)occupancy;
                if (dirtyBits[i] != 0)
                {
                    dirtyClusterCount++;
                }

                entryCount += BitOperations.PopCount((ulong)dirtyBits[i]);
            }

            Interlocked.Add(ref clusterState.PrepMaskTicks, Stopwatch.GetTimestamp() - subSpan);
            clusterScope.DirtyClusterCount = dirtyClusterCount;
            clusterScope.EntryCount = entryCount;

            // ③ ④ — same gate as the atomic path (#655): either home may carry the index.
            //
            // ③ runs concurrently across slices, and for one day (16fa2891) it did not: BTree.MoveValue's pessimistic fallback read buffer ids without the
            // leaf latch and lost elements under exactly this concurrency (#887, ~45 % of PrepSliceEquivalenceTests runs at W = 8). The tree is fixed — every
            // buffer is now touched only under its leaf's latch, and a key emptied by one thread is dropped only if still empty under the latch (IXW-06) —
            // so the drain is back where the width is. If PrepSliceEquivalenceTests reddens again, IXW-06's verifier (BTreeMoveValueConcurrencyTests) is
            // the first thing to run, before anything here is suspected.
            if (clusterState.IndexSlots != null || clusterState.TransientIndexSlots != null)
            {
                clusterScope.HasShadow = 1;
                var shadowScope = TyphonEvent.BeginWriteTickFenceClusterShadow(meta.ArchetypeId, dirtyClusterCount);
                subSpan = Stopwatch.GetTimestamp();
                try
                {
                    shadowScope.TotalShadowEntries = ProcessClusterShadowEntriesRange(
                        clusterState, engineState, changeSet, ref accessor, firstWord, wordCount, false);
                }
                finally
                {
                    shadowScope.Dispose();
                }

                Interlocked.Add(ref clusterState.PrepShadowTicks, Stopwatch.GetTimestamp() - subSpan);

                subSpan = Stopwatch.GetTimestamp();
                RecomputeClusterZoneMapsRange(clusterState, dirtyBits, ref accessor, firstWord, wordCount);
                Interlocked.Add(ref clusterState.PrepZoneMapTicks, Stopwatch.GetTimestamp() - subSpan);
            }

            // ⑤ crossings, into a slice-private list the tail concatenates in slice order.
            if (clusterState.SpatialSlot.HasSpatialIndex && clusterState.SpatialSlot.FieldInfo.Mode == SpatialMode.Dynamic)
            {
                clusterScope.HasSpatial = 1;
                subSpan = Stopwatch.GetTimestamp();
                // The list is the exec system's, pooled per work item and reused tick over tick; cleared here rather than trusted, because a tail that
                // never ran (a failed Prep) leaves last tick's requests in it.
                crossings.Clear();
                DetectClusterMigrationsRange(clusterState, engineState, meta.ArchetypeId, dirtyBits, ref accessor, firstWord, wordCount, crossings);
                if (crossings.Count > 0)
                {
                    clusterState.RegisterPrepSliceCrossings(firstWord, crossings);
                }

                Interlocked.Add(ref clusterState.PrepDetectTicks, Stopwatch.GetTimestamp() - subSpan);
            }

            Interlocked.Add(ref clusterState.FenceEntryCount, entryCount);
            Interlocked.Add(ref clusterState.FenceDirtyClusterCount, dirtyClusterCount);
            Interlocked.Add(ref clusterState.PrepDirtyClusters, dirtyClusterCount);
        }
        finally
        {
            ArchetypeClusterState.ExitPrepSlice();
            accessor.Dispose();
            clusterScope.Dispose();
        }
    }

    /// <summary>
    /// Phase 1, serial tail (#886 lead D): for every archetype whose Prep ran as slices, the crossings in slice order, the buffer resets, ⑧, then ⑥ ⑦ and
    /// the drain prefix exactly as the atomic path runs them. Called from <c>FenceMigrateExecSystem.Prepare</c>, which is single-threaded by construction
    /// and precedes the destination-cell sort that needs the queue complete. It is timed inside the Migrate span: ⑥ ⑦ ⑧ are relocated there, not removed,
    /// and any phase table read after this change has to say so.
    /// </summary>
    internal void PrepareArchetypeFenceTails(long tickNumber, ChangeSet changeSet)
    {
        var states = _archetypeStates;
        if (states == null)
        {
            return;
        }

        for (var aid = 0; aid < states.Length; aid++)
        {
            var clusterState = states[aid]?.ClusterState;
            if (clusterState == null || !clusterState.PrepSliceable)
            {
                continue;
            }

            clusterState.PrepSliceable = false;
            clusterState.DrainPrepSliceCrossings();
            clusterState.LastTickHysteresisAbsorbedCount += clusterState.PrepSliceHysteresisAbsorbed;
            clusterState.TotalHysteresisAbsorbedCount += clusterState.PrepSliceHysteresisAbsorbed;
            clusterState.ResetShadowBuffersAfterSlices();
            // ⑧ is inside FinishArchetypeFencePrep now — see the comment at its tail.
            FinishArchetypeFencePrep(clusterState, true, tickNumber, changeSet);
        }
    }

    /// <summary>
    /// Run the repair planner for one archetype, renting the accessor it needs (#872 step 12). Separated from the caller only so the accessor's
    /// <c>try/finally</c> does not sit in the middle of the drain-prefix reasoning above.
    /// </summary>
    /// <remarks>
    /// <para>The accessor carries <paramref name="changeSet"/> because the planner allocates clusters and publishes their occupancy word, which is a write
    /// and owes the WAL the same atomicity as every other fence write.</para>
    /// <para><b>Skipped when there is nothing to absorb AND nothing waiting</b> — not merely when nothing was nominated. The queue is persistent since step
    /// 11, so a world that has stopped moving can still hold a backlog worth planning, and the early-out has to ask about both. The cost of that is stated
    /// rather than hidden: an archetype whose queue the budget can never drain rents an accessor and ranks every tick for as long as the backlog lasts.
    /// <c>PlanCellRepairs</c> bounds the per-tick work by stopping its scan once the budget cannot afford another unit; what it cannot avoid is the rent
    /// and the rank themselves, and <c>RepairQueueMaintenanceMs</c> is what makes that visible.</para>
    /// </remarks>
    private void PlanArchetypeRepairs(ArchetypeClusterState clusterState, ChangeSet changeSet, long tickNumber, double remainingBudgetNs)
    {
        // A pure-Transient archetype absorbs and DISCARDS rather than returning with the list intact. Returning here left RepairNominations untouched on
        // the hasWork path while the !hasWork path both absorbed and cleared, so the list would grow across ticks. It is unreachable today only because a
        // null cluster segment implies FenceBranchPath == 0 and therefore no AabbRefresh producer — an accidental guarantee, not a designed one.
        if (clusterState.ClusterSegment == null)
        {
            clusterState.AbsorbRepairNominations(_spatialGrid, tickNumber);
            return;
        }

        // The queue can hold candidates when nothing was nominated this tick — that is the whole point of it being persistent — so the early-out is on
        // "nothing to absorb AND nothing waiting", not on the nomination list alone.
        if (clusterState.RepairNominations.Count == 0 && (clusterState.RepairQueue == null || clusterState.RepairQueue.Count == 0))
        {
            return;
        }

        var accessor = clusterState.ClusterSegment.CreateChunkAccessor(changeSet);
        var plannerStart = Stopwatch.GetTimestamp();
        try
        {
            var planned = clusterState.PlanCellRepairs(_spatialGrid, ref accessor, tickNumber, remainingBudgetNs, out var budgetUsedMs);
            clusterState.LastTickReclusterBudgetUsedMs = budgetUsedMs;

            // Timed here rather than inside the planner so the bracket covers the accessor rent too, and fed back on the NEXT tick — the planner's own
            // cost per entity is a term step 12's projection ignored entirely, and it is repair's alone: no crossing and no relocation pays it.
            clusterState.RecordPlannerCost(Stopwatch.GetTimestamp() - plannerStart, planned);
        }
        finally
        {
            accessor.Dispose();
        }
    }

    /// <inheritdoc cref="PrepareArchetypeFence"/>
    private unsafe bool PrepareArchetypeFenceCore(ArchetypeMetadata meta, long tickNumber, ChangeSet changeSet)
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
        ResetArchetypeFenceTickState(clusterState);

        // Pure-Transient archetypes have no PersistentStore segment — nothing to persist to WAL, no migrations.
        // Entire flow runs inside Prep; Migrate and Finalize will see FenceBranchPath = 0 and skip.
        if (clusterState.ClusterSegment == null)
        {
            var clusterScopeT = TyphonEvent.BeginWriteTickFenceCluster(meta.ArchetypeId);

            // This branch is `ClusterSegment == null`, so there is no cluster segment to open an accessor on and neither callee will take a cluster-segment
            // address — both dispatch on the same `pureTransient` test. Passing the default rather than overloading keeps one shape for the two call sites
            // (#882).
            var noClusterAccessor = default(ChunkAccessor<PersistentStore>);
            try
            {
                // Drain the archetype's own index shadow buffers (#655). This branch had none: a pure-Transient archetype could not carry per-archetype
                // indexes at all, so an in-place write to an indexed field captured a shadow entry that nothing ever consumed — the index would keep the
                // pre-mutation key forever. Runs before the dormancy sweep, matching the ordering of the cluster branch below.
                if (clusterState.TransientIndexSlots != null)
                {
                    var transientShadowScope = TyphonEvent.BeginWriteTickFenceShadow(meta.ArchetypeId, clusterState.TransientIndexSlots.Length);
                    try
                    {
                        transientShadowScope.TotalShadowEntries = ProcessClusterShadowEntries(clusterState, engineState, null, ref noClusterAccessor);
                    }
                    finally
                    {
                        transientShadowScope.Dispose();
                    }
                }

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

                    // Zone maps, same as the cluster branch below (#655). Without this a pure-Transient archetype's maps keep the bounds spawn widened them
                    // to, so Path B prunes away any cluster whose indexed field was later mutated OUT of that range — a silently empty query, not a stale
                    // count. Spawn widens eagerly, which is why the miss only shows after an in-place write.
                    clusterState.FenceWrittenSlots = Interlocked.Exchange(ref clusterState.WrittenSlotUnion, 0);
                    RecomputeClusterZoneMaps(clusterState, transientDirtyBits, ref noClusterAccessor);

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

                        // Only the clusters that CARRY A SIGNAL, not every active cluster.
                        //
                        // This loop used to read the occupancy word of every active cluster and copy it in wholesale, which manufactured a dirty set the size
                        // of the population on a tick where, by construction, the dirty bitmap was EMPTY. Everything downstream then treated a settled world
                        // as a fully-moving one: DetectClusterMigrations scanned every live slot, and the AABB refresh re-derived every bound. Measured on a
                        // 128-entity, 8-cluster fixture, a tick with no writes at all walked 128 of 128 entity slots.
                        //
                        // The branch's own justification is narrower than what it did. It exists because "WriteSpatial-only callers may have moved positions
                        // without setting the dirty bitmap" — and WriteSpatial is not silent: it sets the process bit when the bound grew or a crossing was
                        // flagged (ClusterRef.cs:405), and MaybeGrowAndFlagShrink sets ClusterShrinkPendingAxes when an extreme moved inward
                        // (ClusterRef.cs:455). The remaining case — a WriteSpatial that moves a non-extreme entity within the existing bound — changes
                        // neither the bound nor the cell, so there is nothing for this pass to re-derive.
                        //
                        // ClusterNeedsAabbRecompute is the SAME predicate the refresh itself uses, deliberately shared rather than restated: FenceDirtyBits is
                        // null on this branch (cleared at the top of Prep, published at its end), so the helper falls through to exactly the shrink-flag and
                        // process-bit tests named above. Two copies of this rule would be two things to keep in step.
                        for (var ai = 0; ai < clusterState.ActiveClusterCount; ai++)
                        {
                            var chId = clusterState.ActiveClusterIds[ai];
                            if (chId < 0 || chId >= spatialBits.Length)
                            {
                                continue;
                            }

                            if (!clusterState.ClusterNeedsAabbRecompute(chId))
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
            var subSpan = Stopwatch.GetTimestamp();
            var dirtyBits = clusterState.ClusterDirtyBitmap.Snapshot();
            clusterState.PrepSnapshotTicks += Stopwatch.GetTimestamp() - subSpan;

            // Snapshot-and-clear the written-slot union in the same step as the dirty bitmap (#559 §4.5), so Finalize reads a
            // stable value while writers for the NEXT tick start from zero.
            clusterState.FenceWrittenSlots = Interlocked.Exchange(ref clusterState.WrittenSlotUnion, 0);

            // Mask dirty bits with live occupancy to skip destroyed entities whose dirty bit remained set.
            var accessor = clusterState.ClusterSegment.CreateChunkAccessor();
            try
            {
                var entryCount = 0;
                var dirtyClusterCount = 0;
                subSpan = Stopwatch.GetTimestamp();
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

                clusterState.PrepMaskTicks += Stopwatch.GetTimestamp() - subSpan;
                clusterState.PrepDirtyClusters = dirtyClusterCount;
                clusterScope.DirtyClusterCount = dirtyClusterCount;
                clusterScope.EntryCount = entryCount;

                // Shadow + zone-maps: runs in Prep so the per-archetype B+Tree Move calls happen before any Migrate-phase Remove+Add calls reorder the index.
                // B+Tree itself is OLC-safe across concurrent archetypes (each runs in its own Prep chunk).
                // Gated on EITHER home (#655): an archetype whose only indexed component is Transient has an empty IndexSlots and would otherwise skip both
                // the drain and the zone-map recompute entirely. Both callees dispatch over the two homes themselves.
                if (clusterState.IndexSlots != null || clusterState.TransientIndexSlots != null)
                {
                    clusterScope.HasShadow = 1;
                    var shadowScope = TyphonEvent.BeginWriteTickFenceClusterShadow(meta.ArchetypeId, dirtyClusterCount);
                    subSpan = Stopwatch.GetTimestamp();
                    try
                    {
                        shadowScope.TotalShadowEntries = ProcessClusterShadowEntries(clusterState, engineState, changeSet, ref accessor);
                    }
                    finally
                    {
                        shadowScope.Dispose();
                    }

                    clusterState.PrepShadowTicks += Stopwatch.GetTimestamp() - subSpan;

                    subSpan = Stopwatch.GetTimestamp();
                    RecomputeClusterZoneMaps(clusterState, dirtyBits, ref accessor);
                    clusterState.PrepZoneMapTicks += Stopwatch.GetTimestamp() - subSpan;
                }

                // Detect migrations: populates clusterState.PendingMigrations. Spatial-only — Dynamic mode.
                if (clusterState.SpatialSlot.HasSpatialIndex && clusterState.SpatialSlot.FieldInfo.Mode == SpatialMode.Dynamic)
                {
                    clusterScope.HasSpatial = 1;
                    subSpan = Stopwatch.GetTimestamp();
                    DetectClusterMigrations(clusterState, engineState, meta.ArchetypeId, dirtyBits, ref accessor);
                    clusterState.PrepDetectTicks += Stopwatch.GetTimestamp() - subSpan;
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

        return true;
    }

    /// <summary>
    /// Phase 2 of the parallel cluster tick fence: apply a contiguous slice of one archetype's <see cref="ArchetypeClusterState.PendingMigrations"/>.
    /// Safe to call concurrently from multiple workers — each worker owns a disjoint slice (sorted by destination cell key) so dst-side mutations
    /// (slot claim, AABB union, per-cell index update) hit worker-exclusive cells. Source-side mutations (occupancy clear, dirtyBits flip,
    /// cell.EntityCount decrement) use <see cref="System.Threading.Interlocked"/> primitives; rare empty-cluster finalization is serialized via
    /// the per-archetype <see cref="ArchetypeClusterState._finalizeLock"/> through
    /// <see cref="ArchetypeClusterState.ReleaseSlot(ref ChunkAccessor{PersistentStore}, int, int, ChangeSet, SpatialGrid, bool)"/>.
    /// </summary>
    /// <remarks>
    /// Callers must ensure (a) <see cref="ArchetypeClusterState.FenceDirtyBits"/> has been pre-sized to at least
    /// <c>PrimarySegmentCapacity + PendingMigrationCount</c> entries by TickDriver before any Migrate-phase worker runs (eliminates parallel
    /// <c>Array.Resize</c>), and (b) the slice <c>[sliceStart, sliceStart+sliceCount)</c> is disjoint from every other worker's slice.
    /// </remarks>
    internal void ExecuteMigrationsSlice(ArchetypeMetadata meta, int sliceStart, int sliceCount, ChangeSet changeSet, List<DirtyBitDelta> dirtyBuffer = null,
        int chunkIndex = 0)
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

        ExecuteMigrations(clusterState, engineState, meta.ArchetypeId, sliceStart, sliceCount, changeSet, dirtyBuffer, chunkIndex);
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
    /// Phase 4 of the parallel cluster tick fence for one archetype, end to end: the head (<see cref="FinalizeArchetypeFenceHead"/> — bookkeeping clear,
    /// dormancy sweep, dirty-ring archive, ComponentTable flag propagation, column narrowing) and then the WAL emit over every dirty word
    /// (<see cref="EmitArchetypeFenceRange"/>). Safe to call concurrently across DISTINCT archetypes. Returns the highest LSN published by this archetype's
    /// WAL chunks (0 if none). The <see cref="FenceWorkKind.ArchetypeFinalize"/> item and the serial <see cref="WriteTickFence"/> path run this; when the
    /// archetype's Finalize was sliced (#889) the head ran on the driver and the emit runs as <see cref="FenceWorkKind.FinalizeEmitSlice"/> items instead.
    /// </summary>
    internal long FinalizeArchetypeFence(ArchetypeMetadata meta, long tickNumber, ChangeSet changeSet)
    {
        if (!FinalizeArchetypeFenceHead(meta, tickNumber))
        {
            return 0;
        }

        var engineState = _archetypeStates[meta.ArchetypeId];
        var clusterState = engineState.ClusterState;
        var dirtyBits = clusterState.FenceDirtyBits;
        var accessor = clusterState.ClusterSegment.CreateChunkAccessor();
        try
        {
            var emitStart = Stopwatch.GetTimestamp();
            var highestLSN = EmitArchetypeFenceRange(engineState, clusterState, meta.ArchetypeId, tickNumber, ref accessor, 0, dirtyBits.Length);
            clusterState.LastTickFinalizeEmitTicks = Stopwatch.GetTimestamp() - emitStart;
            return highestLSN;
        }
        finally
        {
            accessor.Dispose();
        }
    }

    /// <summary>How many POPULATED <see cref="FenceWorkPlan.FinalizeSliceWords"/>-word ranges an archetype's dirty bitmap needs before its Finalize emit is
    /// worth slicing (#889): two, so that one worker does not open two accessors for the work one would have done and the head is not moved onto the
    /// driver for a single slice. Counted on the words that are dirty, not on the bitmap's capacity — a 512-word archetype with three dirty words is one
    /// slice's worth of emit.</summary>
    /// <remarks>A static rather than a const so the partition harness can switch the sliced path off in the same binary (<c>--no-finalize-slice</c>,
    /// which sets it to <see cref="int.MaxValue"/>).</remarks>
    internal static int FinalizeSliceMinRanges = 2;

    /// <summary>
    /// Phase 4, serial head (#889): for every archetype whose emit is worth slicing, runs <see cref="FinalizeArchetypeFenceHead"/> here on the driver, in
    /// <c>FenceFinalizeExecSystem.Prepare</c>, so the planner can carve the emit into <see cref="FenceWorkKind.FinalizeEmitSlice"/> items over
    /// <see cref="ArchetypeClusterState.FenceDirtyBits"/> word ranges. An archetype that does not qualify is left alone: its
    /// <see cref="FenceWorkKind.ArchetypeFinalize"/> item runs <see cref="FinalizeArchetypeFence"/> exactly as before, head included.
    /// </summary>
    /// <remarks>
    /// <para><b>Why the head is serial and the emit is not.</b> The head frees drained clusters, sweeps dormancy (<c>SleepingClusterCount++</c> is a plain
    /// increment, DM-02), publishes <c>PreviousTickDirtySnapshot</c> as one store and narrows the emitted columns to one set per archetype — all of it
    /// per-archetype, none of it divisible, and all of it cheap: a few array walks over the active clusters. The emit is the opposite: one
    /// <c>FenceBlock</c> record per dirty cluster, each a bulk copy of the cluster's SoA bytes into its own WAL claim, and at 100 % moving it was 85 % of
    /// Finalize and 0.6 ms of fence span on one worker while seven waited. Two slices never share a word, <c>_fenceBlocks</c> and the collection arena
    /// are thread-static, <c>WalCommitBuffer.TryClaim</c> is MPSC, and fence records are individually committed (recovery applies them by LSN without a
    /// commit marker), so which worker publishes which cluster's record changes the LSN order between clusters and nothing else.</para>
    /// <para><b>The head ran ⇒ the atomic item must not.</b> A head that finds nothing to emit — every column Versioned or Transient, or a Checkpoint
    /// archetype — has still swept dormancy and archived the ring; running the atomic item after it would sweep twice.
    /// <see cref="ArchetypeClusterState.FinalizeHeadRan"/> is what the planner reads, and it emits nothing at all for such an archetype.</para>
    /// </remarks>
    internal void PrepareArchetypeFinalizeHeads(long tickNumber, int workerCount)
    {
        var states = _archetypeStates;
        if (states == null)
        {
            return;
        }

        // Indexed, like the Migrate sort loop, rather than through GetAllArchetypes(): that is an iterator, and this runs on the fence path every tick.
        for (var aid = 0; aid < states.Length; aid++)
        {
            var cs = states[aid]?.ClusterState;
            if (cs == null)
            {
                continue;
            }

            cs.FinalizeHeadRan = false;
            cs.FinalizeSliceable = false;
            if (workerCount < 2 || cs.FenceBranchPath != 2 || cs.FenceDirtyBits == null
                || FenceWorkPlan.CountPopulatedRanges(cs.FenceDirtyBits, FenceWorkPlan.FinalizeSliceWords) < FinalizeSliceMinRanges)
            {
                continue;
            }

            var meta = ArchetypeRegistry.GetMetadata((ushort)aid);
            if (meta == null || !meta.IsClusterEligible)
            {
                continue;
            }

            // Set BEFORE the head runs, deliberately: a head that throws after its dormancy sweep must not be followed by the atomic item's second
            // sweep. The phase is skipped on a throw anyway (#890) and the next Prep resets the flag; this is the belt to that brace.
            cs.FinalizeHeadRan = true;
            cs.FinalizeSliceable = FinalizeArchetypeFenceHead(meta, tickNumber);
        }
    }

    /// <summary>One <see cref="FenceWorkKind.FinalizeEmitSlice"/>: the WAL emit for the dirty words in <c>[firstWord, firstWord + wordCount)</c> of an
    /// archetype whose head ran on the driver. Returns the highest LSN it published.</summary>
    internal long EmitArchetypeFenceSlice(ArchetypeMetadata meta, long tickNumber, int firstWord, int wordCount)
    {
        if (meta == null || meta.ArchetypeId >= _archetypeStates.Length)
        {
            return 0;
        }

        var engineState = _archetypeStates[meta.ArchetypeId];
        var clusterState = engineState?.ClusterState;
        if (clusterState == null || !clusterState.FinalizeSliceable)
        {
            return 0;
        }

        var accessor = clusterState.ClusterSegment.CreateChunkAccessor();
        try
        {
            var emitStart = Stopwatch.GetTimestamp();
            var highestLSN = EmitArchetypeFenceRange(engineState, clusterState, meta.ArchetypeId, tickNumber, ref accessor, firstWord, wordCount);
            // Summed across the slices: CPU, not span, on a sliced tick — the same convention Prep's sub-spans follow (#886).
            Interlocked.Add(ref clusterState.LastTickFinalizeEmitTicks, Stopwatch.GetTimestamp() - emitStart);
            return highestLSN;
        }
        finally
        {
            accessor.Dispose();
        }
    }

    /// <summary>
    /// Publish this archetype's spatial-maintenance counters onto the trace stream, once per tick (#911 O1/O3): the throttle's relocation outcome split
    /// (kind 65) and the counter snapshot the Workbench Spatial panel reads (kind 66).
    /// </summary>
    /// <remarks>
    /// <para><b>Here rather than at each producer, and that is the point.</b> The outcome terms are written by three different phases — the throttle resolves
    /// admitted / throttled / superseded in Prep, drift detection resolves unplaced / spilled in AabbRefresh, the drain resolves pinsRejected in Migrate —
    /// so any earlier site would emit a record whose terms came from different moments and whose identity therefore would not close. This runs after every
    /// phase barrier and after the promote/demote pass, so every field is this tick's final value.</para>
    /// <para><b>Costs nothing when the profiler is off.</b> Both factories open with the <c>static readonly</c> gate check, so the JIT folds the bodies away
    /// and the reads below are the only thing left — which is why they are field loads and not an allocation of
    /// <see cref="SpatialMigrationTelemetry"/>. The explicit gate keeps even those out.</para>
    /// <para><b>The identity spans two ticks, not one.</b> <c>DriftersDetected</c> here is what THIS tick's AabbRefresh found; the outcomes beside it are what
    /// this tick's Prep did with what the PREVIOUS tick found (rule <c>TH-02</c>). A consumer checking
    /// <c>detected == admitted + throttled + superseded + unplaced</c> must pair tick N's detection with tick N+1's outcomes.</para>
    /// </remarks>
    private static void EmitSpatialArchetypeSnapshot(ArchetypeClusterState clusterState, ushort archetypeId)
    {
        if (!TelemetryConfig.ProfilerActive)
        {
            return;
        }

        TyphonEvent.EmitSpatialRelocationOutcome(
            archetypeId,
            clusterState.LastTickRelocationsAdmitted,
            clusterState.LastTickRelocationsThrottled,
            clusterState.LastTickRelocationsSuperseded,
            clusterState.LastTickDriftersUnplaced,
            clusterState.LastTickDriftersUnplacedNoCandidate,
            clusterState.LastTickDriftersSpilled,
            clusterState.LastTickPinsRejected,
            clusterState.LastTickCrossingsQueued);

        var samples = clusterState.LastTickTightnessSamples;
        TyphonEvent.EmitSpatialArchetypeTelemetry(
            archetypeId,
            clusterState.ActiveClusterCount,
            clusterState.LastTickMigrationCount,
            (float)clusterState.LastTickMigrationTotalMs,
            clusterState.LastTickHysteresisAbsorbedCount,
            clusterState.LastTickDriftersDetected,
            clusterState.LastTickRepairUnitCount,
            clusterState.LastTickRepairUnitsRefused,
            clusterState.RepairQueue?.Count ?? 0,
            (float)clusterState.LastTickReclusterBudgetUsedMs,
            samples,
            samples > 0 ? (float)(clusterState.LastTickTightnessExtentSum / samples) : 0f,
            samples > 0 ? (float)(clusterState.LastTickTightnessBoundSum / samples) : 0f,
            clusterState.LastTickCellTreePromotions,
            clusterState.LastTickCellTreeDemotions);
    }

    /// <summary>
    /// Everything Finalize does before the WAL emit, for one archetype: drop the executed migration prefix, free drained clusters, clear the AABB-refresh
    /// bookkeeping, apply and refit promoted cells, sweep dormancy, archive the dirty ring, publish the dirty snapshot and the ComponentTable flags, and
    /// narrow the emitted columns into <see cref="ArchetypeClusterState.FenceEmit"/>. Returns true when there is something to emit.
    /// </summary>
    internal bool FinalizeArchetypeFenceHead(ArchetypeMetadata meta, long tickNumber)
    {
        if (meta == null || !meta.IsClusterEligible || meta.ArchetypeId >= _archetypeStates.Length)
        {
            return false;
        }
        var engineState = _archetypeStates[meta.ArchetypeId];
        var clusterState = engineState?.ClusterState;
        if (clusterState == null || clusterState.FenceBranchPath == 0)
        {
            return false;
        }

        var dirtyBits = clusterState.FenceDirtyBits;
        clusterState.LastTickFinalizeEmitTicks = 0;
        clusterState.LastTickFinalizeAppendTicks = 0;

        // Drop the prefix this tick executed and keep the rest. This replaced an outright `PendingMigrationCount = 0`, which discarded every request
        // enqueued AFTER the Migrate phase — all of them, for the two detectors that run inside AabbRefresh (FlagOutliersForMigration since #230, and step
        // 10's drift detection). Both are documented as executing "next tick"; neither ever did.
        clusterState.CompactPendingMigrations();

        // Drain pending cluster finalizations (review C-1 fix): ReleaseSlot during Migrate only records the chunkId; actual finalize + FreeChunk happens here,
        // after the Migrate/AabbRefresh phase barriers. By this point no concurrent ClaimSlotInCell can race with us — safe to free clean clusters.
        clusterState.DrainPendingClusterFinalizations(_spatialGrid);

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

        // Deferred promoted-cell applies first, then the refit — in that order, because the refit has to see the tree those applies produce. Both are
        // no-ops when nothing was promoted.
        clusterState.DrainPromotedAabbApplies();

        // ST-07's make-good, in the one place that satisfies both of its constraints: after every AABB slice (the phase barrier above guarantees it) and
        // before any query can run. The in-place update path deliberately leaves leaf MBRs wider than the union of their entries, which is safe for
        // queries and false for ST-01 read literally — so the looseness must not survive the fence.
        clusterState.RefitPromotedCellTrees();

        // After the refit, because a promotion built here would otherwise hand the refit a tree it has already walked past, and because the demote half
        // reads bounds the refit has just made honest (#872 step 16, D3).
        clusterState.EvaluateCellTreeTightnessTransitions();

        EmitSpatialArchetypeSnapshot(clusterState, meta.ArchetypeId);

        // Clean-spatial-refresh branch (path 1) stops here — no dormancy sweep change (already swept clean), no WAL emit.
        if (clusterState.FenceBranchPath == 1)
        {
            return false;
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
            return false;
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

        // #568 — the declared durability window. Checkpoint archetypes emit NO fence WAL records; their SingleVersion values reach disk through the
        // checkpoint, the same path cluster STRUCTURE has always used, so a crash costs up to one checkpoint interval of freshness (not existence).
        //
        // The gate sits HERE, after the dormancy sweep, the dirty-ring archive, PreviousTickDirtySnapshot and the ComponentTable flag propagation above,
        // and NOT at the top of the method. The dirty bitmap has eleven consumers and WAL emit is one of them: zone-map recompute, migration detect and
        // execute, AABB refresh, dormancy, the dirty ring, and both change-filtered dispatch surfaces all read it. Only the emission is optional.
        //
        // ClusterDurabilityTests pins both halves — the emission stops, and every other consumer keeps being fed.
        //
        // Versioned components are unaffected in either setting: their revision chain is logged at commit and is authoritative (see skipMask below), so
        // no ClusterDurability value can lose a Versioned write.
        if (meta.ClusterDurability == ClusterDurability.Checkpoint)
        {
            return false;
        }

        var layout = clusterState.Layout;
        var plan = clusterState.FenceEmit ??= new ArchetypeClusterState.FenceEmitPlan(layout.ComponentCount);

        // Slots excluded from fence emission:
        //   Transient — never persisted at all.
        //   Versioned — the cluster slot holds only a HEAD *cache*; the revision chain is the truth and is logged at commit.
        //     On reopen the HEAD is rebuilt from the chain (ArchetypeClusterState.RebuildVersionedHeadFromChain), so a fence
        //     record for a Versioned slot is written, fsynced, retained and then ignored. Pure waste — do not emit it.
        var skipMask = meta.TransientSlotMask | meta.VersionedSlotMask;
        // Precompute the durable component slots' WAL identity once per archetype. Each becomes one Slot record per dirty
        // entity (M4); the entity PK is read from the cluster's id array, so fence records are logical, never physical.
        // Sizes and offsets are hoisted here too — they are per-archetype constants, and reading them inside the per-entity loop
        // costs one bounds-checked array load per record (100k+ per tick on a large archetype) for a value that never changes.
        Span<int> durableSlots = stackalloc int[layout.ComponentCount];
        Span<int> durableSizes = stackalloc int[layout.ComponentCount];
        Span<int> durableOffsets = stackalloc int[layout.ComponentCount];
        var durableCount = 0;
        for (var slot = 0; slot < layout.ComponentCount; slot++)
        {
            if ((skipMask & (1 << slot)) != 0)
            {
                continue;
            }

            durableSlots[durableCount] = slot;
            durableSizes[durableCount] = layout.ComponentSize(slot);
            durableOffsets[durableCount] = layout.ComponentOffset(slot);
            durableCount++;
        }

        // Nothing durable to emit (every slot Transient and/or Versioned) — skip the whole walk rather than building empty batches.
        if (durableCount == 0)
        {
            return false;
        }

        // #559 §4.5 — narrow the emitted columns to the component slots actually written this tick. The mask is a single union per ARCHETYPE, not one per
        // cluster: per-cluster was implemented and measured first and is slower (+2.1 ms/tick median, ~10 ms spread) because the array is written by every
        // worker on every dirty-marking write and false-shares. The emitter only ever consumes the union, so the finer granularity bought nothing it could
        // use — hence one column set for all clusters of the archetype. The union is fail-safe: a writer that did not identify its component recorded
        // AllSlotsWritten, so the archetype falls back to emitting everything.
        var fenceWritten = clusterState.FenceWrittenSlots;
        var durableMask = 0;
        for (var d = 0; d < durableCount; d++)
        {
            durableMask |= 1 << durableSlots[d];
        }

        var activeMask = fenceWritten & durableMask;
        if (activeMask == 0)
        {
            return false;   // dirty entities, but nothing durable was written to them
        }

        var columnCount = 0;
        var totalCompSize = 0;
        for (var d = 0; d < durableCount; d++)
        {
            if ((activeMask & (1 << durableSlots[d])) == 0)
            {
                continue;
            }

            plan.SlotIndices[columnCount] = durableSlots[d];
            plan.CompSizes[columnCount] = durableSizes[d];
            plan.CompOffsets[columnCount] = durableOffsets[d];
            totalCompSize += durableSizes[d];
            columnCount++;
        }

        plan.ColumnCount = columnCount;
        plan.TotalCompSize = totalCompSize;
        plan.EntityIdsOffset = layout.EntityIdsOffset;

        // LOG-06 for the columnar path: collect the collection-handle byte ranges of every emitted column so the codec can zero them out of the copied
        // SoA bytes. A cluster slot carries no component overhead, so a field's value-relative offset IS its slot-relative one — the same identity that
        // lets ClusterCollectionSlot share the table's descriptor. Almost always empty; the two loops cost nothing when it is.
        var handleRangeCount = 0;
        for (var c = 0; c < columnCount; c++)
        {
            handleRangeCount += engineState.SlotToComponentTable[plan.SlotIndices[c]].CollectionFields.Length;
        }

        plan.EnsureHandleRanges(handleRangeCount);
        plan.HandleRangeCount = handleRangeCount;
        if (handleRangeCount > 0)
        {
            var hr = 0;
            for (var c = 0; c < columnCount; c++)
            {
                foreach (var f in engineState.SlotToComponentTable[plan.SlotIndices[c]].CollectionFields)
                {
                    plan.ColumnHandleRanges[hr++] = RecordCodec.PackColumnHandleRange(c, f.OffsetInComponentStorage, f.HandleSize);
                }
            }
        }

        return true;
    }

    /// <summary>
    /// The WAL emit for the dirty words in <c>[firstWord, firstWord + wordCount)</c> of one archetype's <see cref="ArchetypeClusterState.FenceDirtyBits"/>,
    /// with the columns <see cref="FinalizeArchetypeFenceHead"/> narrowed into <see cref="ArchetypeClusterState.FenceEmit"/>. Two calls over disjoint
    /// ranges may run concurrently (#889): each reads the cluster pages through its own accessor, batches descriptors in the thread-static
    /// <c>_fenceBlocks</c> and claims its own WAL space. Returns the highest LSN it published, 0 when the range held no dirty word.
    /// </summary>
    internal unsafe long EmitArchetypeFenceRange(
        ArchetypeEngineState engineState,
        ArchetypeClusterState clusterState,
        ushort archetypeId,
        long tickNumber,
        ref ChunkAccessor<PersistentStore> accessor,
        int firstWord,
        int wordCount)
    {
        var dirtyBits = clusterState.FenceDirtyBits;
        var plan = clusterState.FenceEmit;
        var endWord = Math.Min(dirtyBits.Length, firstWord + wordCount);
        var slotIndices = plan.SlotIndices.AsSpan(0, plan.ColumnCount);
        var compSizes = plan.CompSizes.AsSpan(0, plan.ColumnCount);
        var compOffsets = plan.CompOffsets.AsSpan(0, plan.ColumnCount);
        var columnHandleRanges = plan.ColumnHandleRanges.AsSpan(0, plan.HandleRangeCount);
        var totalCompSize = plan.TotalCompSize;
        var entityIdsOffset = plan.EntityIdsOffset;
        long highestLSN = 0;

        // Columnar emission (#559): one FenceBlock record per dirty cluster instead of one Slot record per (entity, component).
        // A cluster's entity keys and each component's values are already contiguous in the SoA, so every part of the payload
        // is a single bulk copy — the codec copies straight out of the page into the WAL claim, with no staging arena.
        // 256, not 64 (#886). MaxFenceBatchBytes is the cap this batch was designed around, and at the ~1 KB a one-entity block costs it binds at
        // roughly 256 descriptors. The array used to hold 64, so it was the array that bound, every time, and Finalize paid a Measure -> TryClaim ->
        // Write -> Publish round trip per 64 dirty clusters -- ~31 a tick at 2 000 dirty clusters instead of ~8. Durability-neutral: fence records are
        // individually committed (LOG-04) and the byte cap still bounds the claim; the only thing that changes is how many claims a tick makes.
        var blocks = FenceBlocks ??= new RecordCodec.FenceBlockDescriptor[MaxFenceBatchBlocks];
        var blockCount = 0;
        var batchBytes = 0;
        long appendTicks = 0;

        for (var wi = firstWord; wi < endWord; wi++)
        {
            var word = dirtyBits[wi];
            if (word == 0)
            {
                continue;
            }

            // Emit the contiguous slot RANGE that spans the dirty bits, not the whole cluster and not a gather: an all-dirty
            // cluster degenerates to one copy per column, a single dirty entity to one entity. Clean entities inside the
            // range ride along — redundant, never wrong — and DirtyMask records which ones actually changed.
            var firstSlot = BitOperations.TrailingZeroCount((ulong)word);
            var lastSlot = 63 - BitOperations.LeadingZeroCount((ulong)word);
            var slotSpan = lastSlot - firstSlot + 1;
            var recWire = RecordCodec.FenceBlockWireSize(plan.ColumnCount, slotSpan, totalCompSize);

            if (blockCount > 0 && (batchBytes + recWire > MaxFenceBatchBytes || blockCount == blocks.Length))
            {
                var appendStart = Stopwatch.GetTimestamp();
                highestLSN = Math.Max(highestLSN, AppendFenceBlockBatch(blocks, blockCount, archetypeId, tickNumber,
                    entityIdsOffset, slotIndices, compSizes, compOffsets, totalCompSize, columnHandleRanges));
                appendTicks += Stopwatch.GetTimestamp() - appendStart;
                blockCount = 0;
                batchBytes = 0;
            }

            blocks[blockCount++] = new RecordCodec.FenceBlockDescriptor(
                (nint)accessor.GetChunkAddress(wi), wi, (byte)firstSlot, (byte)slotSpan, (ulong)word >> firstSlot);
            batchBytes += recWire;
        }

        if (blockCount > 0)
        {
            var appendStart = Stopwatch.GetTimestamp();
            highestLSN = Math.Max(highestLSN, AppendFenceBlockBatch(blocks, blockCount, archetypeId, tickNumber,
                entityIdsOffset, slotIndices, compSizes, compOffsets, totalCompSize, columnHandleRanges));
            appendTicks += Stopwatch.GetTimestamp() - appendStart;
        }

        Interlocked.Add(ref clusterState.LastTickFinalizeAppendTicks, appendTicks);

        // #389: the columnar record carries the SoA bytes with every collection handle zeroed, so on its own it would RESTORE a collection as empty —
        // including one whose content was already safe on the checkpoint timeline. The content therefore rides alongside, in its own fence batch of
        // CollectionDelta records. A separate Append rather than a new record kind inside the block: fence records are individually committed (LOG-04),
        // so ordering between the two batches is by LSN, and recovery folds per (entity, slot, field) and flushes after the Slot apply regardless.
        if (plan.HandleRangeCount > 0)
        {
            highestLSN = Math.Max(highestLSN, AppendClusterCollectionContent(
                engineState, dirtyBits, ref accessor, entityIdsOffset, slotIndices, compSizes, compOffsets, tickNumber, firstWord, wordCount));
        }

        return highestLSN;
    }
}
