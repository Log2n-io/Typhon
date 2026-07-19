using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Threading;
using Typhon.Schema.Definition;

namespace Typhon.Engine;

// DatabaseEngine — cluster migration & shadow-entry processing (partial). Extracted from DatabaseEngine.cs for file-size / IDE-analysis reasons;
// behaviour unchanged. Detects and executes intra-archetype cluster migrations discovered during the tick fence, recomputes cluster zone maps,
// and drains shadow / shadow-field index entries. Runs as part of the fence finalize path (see DatabaseEngine.TickFence.cs).
public partial class DatabaseEngine
{
    /// <summary>
    /// Recompute zone maps for all dirty clusters in the dirty bitmap snapshot.
    /// Each dirty cluster gets a full min/max scan for each indexed field.
    /// </summary>
    private unsafe void RecomputeClusterZoneMaps(ArchetypeClusterState clusterState, long[] dirtyBits)
    {
        var clusterAccessor = clusterState.ClusterSegment.CreateChunkAccessor();
        try
        {
            for (var wordIdx = 0; wordIdx < dirtyBits.Length; wordIdx++)
            {
                if (dirtyBits[wordIdx] == 0)
                {
                    continue;
                }

                var clusterChunkId = wordIdx;

                // Guard against freed/unallocated chunks (stale dirty bits from destroyed entities)
                if (clusterChunkId == 0 || !clusterState.ClusterSegment.IsChunkAllocated(clusterChunkId))
                {
                    continue;
                }

                var clusterBase = clusterAccessor.GetChunkAddress(clusterChunkId);
                var ixSlots = clusterState.IndexSlots;

                for (var s = 0; s < ixSlots.Length; s++)
                {
                    ref var ixSlot = ref ixSlots[s];
                    for (var f = 0; f < ixSlot.Fields.Length; f++)
                    {
                        ixSlot.Fields[f].ZoneMap?.Recompute(clusterChunkId, clusterBase, clusterState.Layout, ixSlot.Slot, ixSlot.Fields[f].FieldOffset);
                    }
                }
            }
        }
        finally
        {
            clusterAccessor.Dispose();
        }
    }

    /// <summary>
    /// Iterate dirty cluster entities and (1) detect cell crossings for migration (issue #229 Phase 3) and
    /// (2) update per-archetype spatial R-Tree positions.
    /// Called at tick boundary from <see cref="WriteClusterTickFence"/>.
    /// </summary>
    /// <remarks>
    /// <para><b>Precondition:</b> <paramref name="dirtyBits"/> has already been masked against live
    /// occupancy by <see cref="WriteClusterTickFence"/> (line ~916: <c>dirtyBits[i] &amp;= occupancy</c>). Every
    /// set bit in this array therefore corresponds to a currently-occupied slot. Breaking this invariant would
    /// let destroyed or reclaimed slots pollute migration detection and R-Tree updates. Do not split the pre-mask
    /// from this iteration without refreshing the occupancy guarantee.</para>
    ///
    /// <para><b>Migration detection</b> runs only when the archetype has opted into the spatial grid
    /// (<c>ClusterCellMap != null</c>, implying a configured <see cref="SpatialGrid"/>). The detection is
    /// cluster-coherent: all entities in a cluster share the same cell (Phase 1+2 invariant), so the current
    /// cell's world bounds and the hysteresis margin are hoisted out of the inner per-slot loop. The per-entity
    /// check is an exit-by-margin axis-aligned bounds test (4 comparisons, early-exit), only falling back to
    /// <see cref="SpatialGrid.WorldToCellKey"/> when the margin is actually exceeded. The hysteresis formulation
    /// is semantically equivalent to <c>claude/design/Spatial/SpatialTiers/01-spatial-clusters.md</c> §"Migration
    /// Hysteresis" but reorganized for a fast common-case "entity stayed inside" path.</para>
    ///
    /// <para><b>Non-finite positions throw.</b> If an entity's spatial field contains NaN or Infinity,
    /// this method raises <see cref="InvalidOperationException"/> with diagnostic context (entity id, cluster,
    /// slot, position). Silent-clamping a non-finite position would produce invisible data corruption in the
    /// spatial index. The contract is: upstream systems MUST write finite positions. Consistent with Phase 1+2's
    /// spawn-time <see cref="SpatialGrid.WorldToCellKey"/> guard.</para>
    /// </remarks>
    private unsafe void DetectClusterMigrations(ArchetypeClusterState clusterState, ArchetypeEngineState engineState, ushort archetypeId, long[] dirtyBits,
        ref ChunkAccessor<PersistentStore> clusterAccessor)
    {
        // Hybrid migration detection:
        //   (a) Drain pre-flagged migrations from ClusterMigrationPendingSlots (set by WriteSpatial at write time — sparse, near-zero cost).
        //   (b) Fall back to the legacy scan over dirtyBits for slots the barrier didn't cover (legacy writers: Transaction.OpenMut + Write — the MVCC commit
        //       path doesn't go through WriteSpatial yet). Each cluster's pre-flagged slot mask is used to skip already-handled slots in the scan, so the two
        //       paths don't double-enqueue.
        //
        // For AntHill (all writes through WriteSpatial), step (b)'s per-slot work is fully masked out — the loop body becomes a popcount-and-skip,
        // which is fast even at 100k entities.
        var processBitmap = clusterState.ClusterProcessBitmap;
        var migrationPending = clusterState.ClusterMigrationPendingSlots;
        var migrationDestKeys = clusterState.ClusterMigrationDestCellKeys;

        var scanSlotCount = 0;
        if (TelemetryConfig.SpatialClusterMigrationDetectActive)
        {
            for (var wi = 0; wi < dirtyBits.Length; wi++)
            {
                scanSlotCount += BitOperations.PopCount((ulong)dirtyBits[wi]);
            }
        }
        var detectScanSpan = TyphonEvent.BeginSpatialClusterMigrationDetectScan(archetypeId, scanSlotCount);
        try
        {
            // Pre-size pending-migration queue.
            var expectedCapacity = Math.Max(16, clusterState.LastTickMigrationCount + (clusterState.LastTickMigrationCount >> 2));
            if (clusterState.PendingMigrations == null || clusterState.PendingMigrations.Length < expectedCapacity)
            {
                clusterState.PendingMigrations = new MigrationRequest[expectedCapacity];
            }

            var migrationsQueuedCount = 0;
            var hysteresisAbsorbedCount = 0;
            var clustersTouched = 0;

            // ─── Step (a): drain WriteSpatial-flagged migrations ───
            if (processBitmap != null && migrationPending != null)
            {
                for (var wordIdx = 0; wordIdx < processBitmap.Length; wordIdx++)
                {
                    var word = processBitmap[wordIdx];
                    if (word == 0)
                    {
                        continue;
                    }

                    while (word != 0)
                    {
                        var chunkId = (wordIdx << 6) + BitOperations.TrailingZeroCount((ulong)word);
                        word &= word - 1;
                        if (chunkId >= migrationPending.Length)
                        {
                            continue;
                        }

                        var slotMask = migrationPending[chunkId];
                        if (slotMask == 0)
                        {
                            continue;
                        }

                        var destCellKey = migrationDestKeys[chunkId];
                        if (destCellKey < 0)
                        {
                            continue;
                        }

                        clustersTouched++;
                        var currentCellKey = clusterState.ClusterCellMap[chunkId];
                        while (slotMask != 0)
                        {
                            var slotIndex = BitOperations.TrailingZeroCount(slotMask);
                            slotMask &= slotMask - 1;
                            migrationsQueuedCount++;
                            TyphonEvent.EmitSpatialClusterMigrationDetect(archetypeId, chunkId, currentCellKey, destCellKey);
                            clusterState.EnqueueMigration(chunkId, slotIndex, destCellKey);
                            TyphonEvent.EmitSpatialClusterMigrationQueue(archetypeId, chunkId, (ushort)Math.Min(clusterState.PendingMigrationCount, ushort.MaxValue));
                        }
                    }
                }
            }

            // ─── Step (b): legacy scan over dirtyBits for slots not covered by step (a) ───
            // Skipped entirely when SpatialBarrierOnly — caller has guaranteed every spatial write
            // goes through WriteSpatial, so step (a) is exhaustive.
            if (clusterState.SpatialBarrierOnly)
            {
                clusterState.LastTickHysteresisAbsorbedCount = hysteresisAbsorbedCount;
                detectScanSpan.MigrationsQueued = migrationsQueuedCount;
                detectScanSpan.HysteresisAbsorbed = hysteresisAbsorbedCount;
                detectScanSpan.ClustersTouched = clustersTouched;
                return;
            }

            ref var ss = ref clusterState.SpatialSlot;
            var layout = clusterState.Layout;
            var compSlot = ss.Slot;
            var compSize = layout.ComponentSize(compSlot);
            var compOffset = layout.ComponentOffset(compSlot);
            var grid = _spatialGrid;
            var clusterCellMap = clusterState.ClusterCellMap;
            var fieldType = ss.FieldInfo.FieldType;
            ref readonly var cfg = ref grid.Config;
            var cellSize = cfg.CellSize;
            var worldMinX = cfg.WorldMin.X;
            var worldMinY = cfg.WorldMin.Y;
            var hysteresisMargin = cellSize * cfg.MigrationHysteresisRatio;

            for (var wordIdx = 0; wordIdx < dirtyBits.Length; wordIdx++)
            {
                var word = dirtyBits[wordIdx];
                if (word == 0)
                {
                    continue;
                }

                var clusterChunkId = wordIdx;
                // Mask out slots already handled by step (a).
                var handledMask = (migrationPending != null && clusterChunkId < migrationPending.Length) ? migrationPending[clusterChunkId] : 0UL;
                var effective = (ulong)word & ~handledMask;
                if (effective == 0)
                {
                    continue;
                }

                var clusterBase = clusterAccessor.GetChunkAddress(clusterChunkId);
                var currentCellKey = clusterCellMap[clusterChunkId];
                if (currentCellKey < 0)
                {
                    continue;
                }

                var (cx, cy) = grid.CellKeyToCoords(currentCellKey);
                var curCellMinX = worldMinX + cx * cellSize;
                var curCellMinY = worldMinY + cy * cellSize;
                var curCellMaxX = curCellMinX + cellSize;
                var curCellMaxY = curCellMinY + cellSize;
                clustersTouched++;

                var remaining = effective;
                while (remaining != 0)
                {
                    var slotIndex = BitOperations.TrailingZeroCount(remaining);
                    remaining &= remaining - 1;
                    var entityPK = *(long*)(clusterBase + layout.EntityIdsOffset + slotIndex * 8);
                    var fieldPtr = clusterBase + compOffset + slotIndex * compSize + ss.FieldOffset;
                    SpatialGrid.ReadSpatialCenter2D(fieldPtr, fieldType, out var posX, out var posY);
                    if (!float.IsFinite(posX) || !float.IsFinite(posY))
                    {
                        throw new InvalidOperationException(
                            $"Non-finite position on spatial entity: entityId=0x{entityPK:X16}, clusterChunkId={clusterChunkId}, slotIndex={slotIndex}, position=({posX}, {posY}).");
                    }
                    var exited = posX < curCellMinX - hysteresisMargin
                                 || posX > curCellMaxX + hysteresisMargin
                                 || posY < curCellMinY - hysteresisMargin
                                 || posY > curCellMaxY + hysteresisMargin;
                    if (exited)
                    {
                        var newCellKey = grid.WorldToCellKey(posX, posY);
                        if (newCellKey != currentCellKey)
                        {
                            migrationsQueuedCount++;
                            TyphonEvent.EmitSpatialClusterMigrationDetect(archetypeId, clusterChunkId, currentCellKey, newCellKey);
                            clusterState.EnqueueMigration(clusterChunkId, slotIndex, newCellKey);
                            TyphonEvent.EmitSpatialClusterMigrationQueue(archetypeId, clusterChunkId, (ushort)Math.Min(clusterState.PendingMigrationCount, ushort.MaxValue));
                        }
                    }
                    else if (posX < curCellMinX || posX > curCellMaxX || posY < curCellMinY || posY > curCellMaxY)
                    {
                        hysteresisAbsorbedCount++;
                        if (TelemetryConfig.SpatialClusterMigrationHysteresisActive)
                        {
                            var ex = posX < curCellMinX ? (curCellMinX - posX) : (posX > curCellMaxX ? (posX - curCellMaxX) : 0f);
                            var ey = posY < curCellMinY ? (curCellMinY - posY) : (posY > curCellMaxY ? (posY - curCellMaxY) : 0f);
                            TyphonEvent.EmitSpatialClusterMigrationHysteresis(archetypeId, clusterChunkId, ex * ex + ey * ey);
                        }
                    }
                }
            }

            clusterState.LastTickHysteresisAbsorbedCount = hysteresisAbsorbedCount;
            detectScanSpan.MigrationsQueued = migrationsQueuedCount;
            detectScanSpan.HysteresisAbsorbed = hysteresisAbsorbedCount;
            detectScanSpan.ClustersTouched = clustersTouched;
        }
        finally
        {
            detectScanSpan.Dispose();
        }
    }

    /// <summary>
    /// In-place ClusterEntityRecord field updater consumed by <see cref="RawValuePagedHashMap{TKey,TStore}.TryUpdateInPlace"/>
    /// during migration. Patches the 4-byte ClusterChunkId and 1-byte SlotIndex fields without rewriting the rest of the record.
    /// Struct (not ref struct) so it can sit on the stack as a local in <see cref="ExecuteMigrations"/> and pass through `ref`.
    /// </summary>
    private readonly unsafe struct ClusterLocationUpdater : IRawValueUpdater
    {
        private readonly int _chunkId;
        private readonly byte _slotIndex;

        public ClusterLocationUpdater(int chunkId, byte slotIndex)
        {
            _chunkId = chunkId;
            _slotIndex = slotIndex;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Update(byte* valueBytes)
        {
            ClusterEntityRecordAccessor.SetClusterChunkId(valueBytes, _chunkId);
            ClusterEntityRecordAccessor.SetSlotIndex(valueBytes, _slotIndex);
        }
    }

    /// <summary>
    /// Execute all pending cell-crossing migrations queued by <see cref="DetectClusterMigrations"/>.
    /// Called at the cluster tick fence, AFTER detection, BEFORE the cluster tick fence WAL publish loop.
    /// Issue #229 Phase 3.
    /// </summary>
    /// <remarks>
    /// <para>Per-migration pipeline:</para>
    /// <list type="number">
    ///   <item>Read entity id from source slot</item>
    ///   <item><see cref="ArchetypeClusterState.ClaimSlotInCell"/> on the destination cell (allocates a new cluster if needed)</item>
    ///   <item>Copy every component slot's bytes source → destination (Persistent + Transient; Q8)</item>
    ///   <item>Copy EntityId and EnabledBits</item>
    ///   <item>Remove the old per-archetype B+Tree index entries and insert new ones at the new <c>clusterLocation</c></item>
    ///   <item>Remove the old spatial R-Tree back-pointer and insert a new one at the new <c>clusterLocation</c></item>
    ///   <item>Upsert the EntityMap <see cref="ClusterEntityRecordAccessor"/> with the new (chunkId, slot)</item>
    ///   <item><see cref="ArchetypeClusterState.ReleaseSlot"/> on the source (clears occupancy, decrements cell.EntityCount, detaches empty clusters)</item>
    ///   <item>Record the dirty-bit transition — clear the source bit (so WAL publish won't serialize a cleared source) and set the destination bit (so the
    ///         destination's new content IS serialized by the subsequent ClusterTickFence WAL publish loop). On the parallel path the transition is appended to
    ///         the worker-local <paramref name="dirtyBuffer"/> as a <see cref="DirtyBitDelta"/>; on the serial path (null buffer) it is applied directly to the
    ///         archetype's <see cref="ArchetypeClusterState.FenceDirtyBits"/></item>
    /// </list>
    ///
    /// <para><b>WAL atomicity.</b> All writes flow through a single <see cref="ChangeSet"/> scoped to this method, so either the entire migration batch lands
    /// or none of it does (Q1 decision). The enclosing <c>OnTickEndInternal</c> ordering — <c>WriteTickFence</c> before <c>UoW.Flush</c> — ensures the
    /// migration is durable within the tick that triggered it.</para>
    ///
    /// <para><b>Destination-cluster growth.</b> If <c>ClaimSlotInCell</c> allocates a brand-new cluster whose chunk id exceeds the current
    /// <see cref="ArchetypeClusterState.FenceDirtyBits"/> length, the array is grown on demand: the serial path calls
    /// <see cref="ArchetypeClusterState.GrowFenceDirtyBitsForChunkId"/> before setting the bit, while the parallel path defers the set to
    /// <see cref="ArchetypeClusterState.ApplyDirtyBitDeltas"/>, which grows the array once under its finalize lock when draining the buffer. Either way the
    /// destination slot bit survives the subsequent WAL publish.</para>
    /// </remarks>
    private unsafe void ExecuteMigrations(ArchetypeClusterState clusterState, ArchetypeEngineState engineState, ushort archetypeId, int sliceStart, 
        int sliceCount, ChangeSet changeSet, List<DirtyBitDelta> dirtyBuffer = null)
    {
        var totalPending = clusterState.PendingMigrationCount;
        if (sliceCount <= 0 || sliceStart >= totalPending)
        {
            return;
        }
        var sliceEndExclusive = Math.Min(sliceStart + sliceCount, totalPending);
        var count = sliceEndExclusive - sliceStart;
        if (count <= 0)
        {
            return;
        }
        // dirtyBits[] is the FenceDirtyBits buffer set by Prep. Pre-sized by TickDriver to PrimarySegmentCapacity + PendingMigrationCount, so no Array.Resize
        // is ever needed inside this slice loop — workers Interlocked.Or/And on disjoint or shared words without parallel-resize race.
        var dirtyBits = clusterState.FenceDirtyBits;

        var startTimestamp = Stopwatch.GetTimestamp();

        var layout = clusterState.Layout;
        var componentCount = layout.ComponentCount;
        // Total component instances moved this batch — surfaces in the profiler tooltip alongside the entity count
        // so users see the actual data-shuffling cost (a 3-component archetype migrating 1300 entities moves 3900
        // component slots' worth of data, not just 1300).
        using var migrationScope = TyphonEvent.BeginClusterMigration(archetypeId, count, count * componentCount);

        var grid = _spatialGrid;
        var transientMask = layout.TransientSlotMask;
        ref var ss = ref clusterState.SpatialSlot;
        var spatialCompSlot = ss.Slot;
        var spatialCompOffset = layout.ComponentOffset(spatialCompSlot);
        var spatialCompSize = layout.ComponentSize(spatialCompSlot);

        // Single-assignment accessor construction (TYPHON004 forbids the default→reassign pattern).
        var hasClusterAccessor = clusterState.ClusterSegment != null;
        var clusterAccessor = hasClusterAccessor ? clusterState.ClusterSegment.CreateChunkAccessor(changeSet) : default;

        var hasTransientClusterAccessor = clusterState.TransientSegment != null;
        var transientClusterAccessor = hasTransientClusterAccessor ? clusterState.TransientSegment.CreateChunkAccessor() : default;

        var hasIdxAccessor = clusterState.IndexSegment != null;
        var idxAccessor = hasIdxAccessor ? clusterState.IndexSegment.CreateChunkAccessor(changeSet) : default;

        var emAccessor = engineState.EntityMap.Segment.CreateChunkAccessor(changeSet);

        // Narrowphase scratch for the #230 Phase 1 per-cell index migration hook. Hoisted out of the
        // migration loop to avoid CA2014 stack-pressure accumulation — a batch of thousands of migrations
        // would otherwise allocate 32 bytes per iteration that can't be released until ExecuteMigrations
        // returns.
        // Sized for 3D ([minX, minY, minZ, maxX, maxY, maxZ]); 2D reads only populate the first 4 slots. Issue #230 Phase 3 unified 2D/3D per-cell paths.
        Span<double> migrantCoords = stackalloc double[6];

        try
        {
            var pending = clusterState.PendingMigrations;
            for (var i = sliceStart; i < sliceEndExclusive; i++)
            {
                var req = pending[i];
                var srcChunkId = req.SourceClusterChunkId;
                var srcSlot = req.SourceSlotIndex;
                var destCellKey = req.DestCellKey;

                // 0. Stale-source guard: verify the source slot's occupancy bit is still set.
                // The detection phase reads occupancy through a read-only accessor (no ChangeSet → DC not bumped). If
                // checkpoint decremented DC to 0 between detection and execution, the page may have been evicted and
                // reloaded from disk with stale occupancy data. Skip the migration — the entity was already migrated
                // in a previous tick and the detection saw phantom occupancy.
                var srcPrimaryPre = hasClusterAccessor ? clusterAccessor.GetChunkAddress(srcChunkId, true) : transientClusterAccessor.GetChunkAddress(srcChunkId, true);
                var srcOcc = *(ulong*)srcPrimaryPre;
                if ((srcOcc & (1UL << srcSlot)) == 0)
                {
                    continue;
                }

                // 1. Read entity id from source slot (needed before any reallocation pointer invalidation).
                var entityPK = *(long*)(srcPrimaryPre + layout.EntityIdsOffset + srcSlot * 8);

                // 1b. Destroyed-in-flight check. The occupancy bit read in step 0 and the entityId read here are NOT atomic together — a concurrent destroy on
                // the same source slot (FlushPendingDestroys clears occupancy bit then zeros entityId) can land between the two reads. The torn-read tell is
                // entityPK == 0: occupancy looked set, but by the time we read entityId, the slot was cleared. Skip the migration: the source entity is gone,
                // there's nothing to move.
                if (entityPK == 0)
                {
                    continue;
                }

                // 2. Claim destination slot in the target cell. May allocate a new cluster (new chunk id).
                //    ClaimSlotInCell maintains cell.EntityCount / cell.ClusterCount + ClusterCellMap.
                int dstChunkId;
                int dstSlot;
                if (hasClusterAccessor)
                {
                    (dstChunkId, dstSlot) = clusterState.ClaimSlotInCell(destCellKey, ref clusterAccessor, changeSet, grid);
                }
                else
                {
                    (dstChunkId, dstSlot) = clusterState.ClaimSlotInCell(destCellKey, ref transientClusterAccessor, grid);
                }

                // 3. Re-fetch source / destination bases after potential segment growth inside ClaimSlotInCell.
                byte* srcBase;
                byte* dstBase;
                byte* srcTransBase = null;
                byte* dstTransBase = null;
                if (hasClusterAccessor)
                {
                    srcBase = clusterAccessor.GetChunkAddress(srcChunkId, true);
                    dstBase = clusterAccessor.GetChunkAddress(dstChunkId, true);
                    if (hasTransientClusterAccessor)
                    {
                        srcTransBase = transientClusterAccessor.GetChunkAddress(srcChunkId, true);
                        dstTransBase = transientClusterAccessor.GetChunkAddress(dstChunkId, true);
                    }
                }
                else
                {
                    // Pure-Transient archetype: primary is the transient segment itself.
                    srcBase = transientClusterAccessor.GetChunkAddress(srcChunkId, true);
                    dstBase = transientClusterAccessor.GetChunkAddress(dstChunkId, true);
                }

                // 4. Copy component data src → dst for EVERY slot, routing Transient vs Persistent via TransientSlotMask.
                //    Transient data survives across ticks (Q8) so both must be copied.
                for (var s = 0; s < componentCount; s++)
                {
                    var compSize = layout.ComponentSize(s);
                    var compOff = layout.ComponentOffset(s);
                    byte* sBase;
                    byte* dBase;
                    if ((transientMask & (1 << s)) != 0)
                    {
                        // Mixed archetype: transient slots live in the transient store. Pure-Transient archetype: primary
                        // IS the transient store, so srcBase/dstBase already point at it.
                        sBase = (srcTransBase != null) ? srcTransBase : srcBase;
                        dBase = (dstTransBase != null) ? dstTransBase : dstBase;
                    }
                    else
                    {
                        sBase = srcBase;
                        dBase = dstBase;
                    }
                    var src = sBase + compOff + srcSlot * compSize;
                    var dst = dBase + compOff + dstSlot * compSize;
                    Unsafe.CopyBlockUnaligned(dst, src, (uint)compSize);
                }

                // 5. Copy EntityId into destination slot primary segment.
                *(long*)(dstBase + layout.EntityIdsOffset + dstSlot * 8) = entityPK;

                // 6. Copy per-component EnabledBits. For each slot, transcribe src.bit(srcSlot) → dst.bit(dstSlot).
                //    Source bits are cleared later by ReleaseSlot.
                for (var s = 0; s < componentCount; s++)
                {
                    var ebOff = layout.EnabledBitsOffset(s);
                    var srcEnabled = *(ulong*)(srcBase + ebOff);
                    if ((srcEnabled & (1UL << srcSlot)) != 0)
                    {
                        *(ulong*)(dstBase + ebOff) |= 1UL << dstSlot;
                    }
                }

                var oldClusterLocation = srcChunkId * 64 + srcSlot;
                var newClusterLocation = dstChunkId * 64 + dstSlot;

                // 7. Update per-archetype B+Tree index entries. Key is unchanged (data was just copied); value
                //    (clusterLocation) changes. Follow the destroy+spawn primitive pattern: Remove(key) + Add(key, newLoc).
                if (hasIdxAccessor && clusterState.IndexSlots != null)
                {
                    var ixSlots = clusterState.IndexSlots;
                    for (var ixs = 0; ixs < ixSlots.Length; ixs++)
                    {
                        ref var ixSlot = ref ixSlots[ixs];
                        var ixCompSize = layout.ComponentSize(ixSlot.Slot);
                        var dstCompBase = dstBase + layout.ComponentOffset(ixSlot.Slot) + dstSlot * ixCompSize;
                        for (var fi = 0; fi < ixSlot.Fields.Length; fi++)
                        {
                            ref var field = ref ixSlot.Fields[fi];
                            var fieldPtr = dstCompBase + field.FieldOffset;
                            var key = KeyBytes8.FromPointer(fieldPtr, field.FieldSize);
                            // For non-unique (AllowMultiple) cluster indexes, read the srcBase elementId from the
                            // source cluster's tail and call RemoveValue — Remove(key) would wipe the entire buffer
                            // at the key and corrupt siblings. srcBase is still the source cluster's bytes (the
                            // component COPY done in step 4 is src→dst, so the source tail is intact). Issue #229 Phase 3.
                            // Regression test: ClusterIndex_NonUniqueField_MigrateOneEntity_PreservesSiblingsInIndex.
                            if (field.AllowMultiple)
                            {
                                var elementId = *(int*)(srcBase + layout.IndexElementIdOffset(field.MultiFieldIndex, srcSlot));
                                field.Index.RemoveValue(&key, elementId, oldClusterLocation, ref idxAccessor);
                                var newElementId = field.Index.Add(fieldPtr, newClusterLocation, ref idxAccessor);
                                *(int*)(dstBase + layout.IndexElementIdOffset(field.MultiFieldIndex, dstSlot)) = newElementId;
                            }
                            else
                            {
                                field.Index.Remove(&key, out _, ref idxAccessor);
                                field.Index.Add(fieldPtr, newClusterLocation, ref idxAccessor);
                            }
                            field.ZoneMap?.Widen(dstChunkId, fieldPtr);
                        }
                    }
                }

                // 8. Maintain per-cell cluster AABB index at the destination (issue #230 Phase 3 Option B: the legacy R-Tree step 8 call has been removed;
                // the per-cell index is the single source of truth).
                var dstFieldPtr = dstBase + spatialCompOffset + dstSlot * spatialCompSize + ss.FieldOffset;

                // Union the migrant's bounds into the dst cluster's AABB.
                // If dst is a brand-new cluster (first entity since allocation), reset the AABB to Empty first so any stale state from a prior life of
                // the chunk id is discarded. Gated on Dynamic mode (static mode is handled at spawn/destroy only — static clusters don't migrate).
                // The src cluster's AABB stays conservative (not shrunk) — Phase 1 trade-off.
                // If src becomes empty, ReleaseSlot below → FinaliseEmptyClusterCellState removes it from the per-cell index.
                if (ss.FieldInfo.Mode == SpatialMode.Dynamic && clusterState.ClusterCellMap != null)
                {
                    if (SpatialMaintainer.ReadAndValidateBoundsFromPtr(dstFieldPtr, ss.FieldInfo, migrantCoords, ss.Descriptor))
                    {
                        clusterState.EnsureClusterAabbsCapacity(dstChunkId + 1);
                        clusterState.EnsureClusterSpatialIndexSlotCapacity(dstChunkId + 1);

                        var wasInIndex = clusterState.ClusterSpatialIndexSlot[dstChunkId] >= 0;
                        ref var dstClusterAabb = ref clusterState.ClusterAabbs[dstChunkId];
                        if (!wasInIndex)
                        {
                            dstClusterAabb = ClusterSpatialAabb.Empty;
                        }
                        // Tier-dispatched union: 2D fields wrote [minX, minY, maxX, maxY] into the first 4 slots; 3D fields wrote the full 6-double layout.
                        // Category mask comes from the archetype-level [SpatialIndex(Category=)] attribute (issue #230 Phase 3).
                        var archetypeCategory = ss.FieldInfo.Category;
                        if (ss.FieldInfo.FieldType == SpatialFieldType.AABB3F || ss.FieldInfo.FieldType == SpatialFieldType.BSphere3F)
                        {
                            dstClusterAabb.Union3F(
                                (float)migrantCoords[0], (float)migrantCoords[1], (float)migrantCoords[2],
                                (float)migrantCoords[3], (float)migrantCoords[4], (float)migrantCoords[5],
                                archetypeCategory);
                        }
                        else
                        {
                            dstClusterAabb.Union2F(
                                (float)migrantCoords[0], (float)migrantCoords[1],
                                (float)migrantCoords[2], (float)migrantCoords[3],
                                archetypeCategory);
                        }

                        var dstCellKey = clusterState.ClusterCellMap[dstChunkId];
                        if (dstCellKey >= 0)
                        {
                            if (!wasInIndex)
                            {
                                clusterState.AddClusterToPerCellIndex(dstChunkId, dstCellKey, dstClusterAabb);
                            }
                            else
                            {
                                var indexSlot = clusterState.ClusterSpatialIndexSlot[dstChunkId];
                                clusterState.PerCellIndex[dstCellKey].DynamicIndex.UpdateAt(indexSlot, in dstClusterAabb);
                            }
                        }
                    }
                }

                // 9. Update EntityMap ClusterEntityRecord with the new (clusterChunkId, slotIndex).
                //    CRITICAL: EntityMap is keyed by EntityKey (the 52-bit top half of RawValue), NOT by the full RawValue stored in cluster slots.
                //    Passing RawValue here would silently miss every lookup — the map would never get updated, and the entity would remain resolvable via
                //    its stale (srcChunkId, srcSlot) pointer until a subsequent spawn reclaimed that slot, at which point the stale EntityMap entry would
                //    resolve to the unrelated new entity's bytes. Unpack explicitly (unsigned shift to avoid sign extension on the top bit).
                //    Regression test: Migration_ThenSubsequentSpawn_ReclaimingSourceSlot_DoesNotCorruptMigratedEntity.
                //    In-place primitive (TryUpdateInPlace) — single hash → bucket → chain scan, mutate the 5 bytes that change
                //    (4-byte ChunkId + 1-byte SlotIndex) under the bucket's OLC write lock. Halves the EntityMap stage cost vs the
                //    pre-#TBD TryGet+Upsert pair which did two chain scans + a full-record stack copy + double OLC traversal.
                //    Returns false if the entity is already gone (destroy race precondition from Q9 says the occupancy pre-mask
                //    should have filtered this out, but the no-op return preserves the same forgiving semantics as before).
                var entityKey = EntityId.FromRaw(entityPK).EntityKey;
                var clusterLocationUpdater = new ClusterLocationUpdater(dstChunkId, (byte)dstSlot);
                var updated = engineState.EntityMap.TryUpdateInPlace(entityKey, ref clusterLocationUpdater, ref emAccessor);
                if (!updated)
                {
                    // EntityMap doesn't have this entity — was committed-destroyed before fence ran. We've already copied data to (dstChunkId, dstSlot), so the
                    // destination cluster now contains an orphan entity's bytes that nothing references. Roll back the destination side: clear the slot's
                    // occupancy + entityId so spatial queries don't keep returning this ghost. The source side gets cleared by the ReleaseSlot below as usual.
                    // Log so we can root-cause the underlying WriteSpatial-flagged-but-then-destroyed race.
                    Console.WriteLine($"[Migrate-Orphan] archId={archetypeId} entityKey={entityKey} "
                        + $"srcChunk={srcChunkId} srcSlot={srcSlot} dstChunk={dstChunkId} dstSlot={dstSlot} — "
                        + "TryUpdateInPlace returned false (entity gone). Rolling back dst slot.");
                    if (hasClusterAccessor)
                    {
                        var dstRollbackBase = clusterAccessor.GetChunkAddress(dstChunkId, true);
                        Interlocked.And(ref *(long*)dstRollbackBase, ~(1L << dstSlot));
                        *(long*)(dstRollbackBase + layout.EntityIdsOffset + dstSlot * 8) = 0;
                        for (var s = 0; s < componentCount; s++)
                        {
                            var ebOff = layout.EnabledBitsOffset(s);
                            Interlocked.And(ref *(long*)(dstRollbackBase + ebOff), ~(1L << dstSlot));
                        }
                    }
                    else if (hasTransientClusterAccessor)
                    {
                        var dstRollbackBase = transientClusterAccessor.GetChunkAddress(dstChunkId, true);
                        Interlocked.And(ref *(long*)dstRollbackBase, ~(1L << dstSlot));
                        *(long*)(dstRollbackBase + layout.EntityIdsOffset + dstSlot * 8) = 0;
                        for (var s = 0; s < componentCount; s++)
                        {
                            var ebOff = layout.EnabledBitsOffset(s);
                            Interlocked.And(ref *(long*)(dstRollbackBase + ebOff), ~(1L << dstSlot));
                        }
                    }
                    // Don't proceed to ReleaseSlot src — the original entity is already gone (its slot was cleared at destroy commit). Don't bump dirtyBits —
                    // the migration was a no-op.
                    continue;
                }

                // 10. Release the source slot. Clears occupancy, EnabledBits, EntityId, decrements cell.EntityCount. If the cluster becomes empty, the
                // finalize-and-free is DEFERRED to FinalizeArchetypeFence (review C-1) — freeing here would race with a concurrent ClaimSlotInCell that may
                // have just CAS-claimed a slot.
                if (hasClusterAccessor)
                {
                    clusterState.ReleaseSlot(ref clusterAccessor, srcChunkId, srcSlot, changeSet, grid, deferFinalize: true);
                }
                else
                {
                    clusterState.ReleaseSlot(ref transientClusterAccessor, srcChunkId, srcSlot, grid, deferFinalize: true);
                }

                // 11. Record dirty-bit deltas to a worker-local buffer instead of writing FenceDirtyBits directly. False-sharing on adjacent chunkIds
                //     (8 longs per 64B cache line) made concurrent Interlocked.Or/And ping-pong cache lines across workers — drained at chunk end under
                //     _finalizeLock as a single batched write per archetype (no cross-worker contention). When the chunk's buffer is null (serial
                //     WriteTickFence path), fall back to a direct Interlocked write with on-demand grow.
                if (dirtyBuffer != null)
                {
                    dirtyBuffer.Add(new DirtyBitDelta
                    {
                        ArchetypeId = archetypeId,
                        SrcChunkId = srcChunkId,
                        SrcClearMask = 1L << srcSlot,
                        DstChunkId = dstChunkId,
                        DstSetMask = 1L << dstSlot,
                    });
                }
                else
                {
                    if (srcChunkId < dirtyBits.Length)
                    {
                        Interlocked.And(ref dirtyBits[srcChunkId], ~(1L << srcSlot));
                    }
                    if (dstChunkId >= dirtyBits.Length)
                    {
                        clusterState.GrowFenceDirtyBitsForChunkId(dstChunkId);
                        dirtyBits = clusterState.FenceDirtyBits;
                    }
                    Interlocked.Or(ref dirtyBits[dstChunkId], 1L << dstSlot);
                }
            }
        }
        finally
        {
            emAccessor.Dispose();
            if (hasIdxAccessor)
            {
                idxAccessor.Dispose();
            }
            if (hasTransientClusterAccessor)
            {
                transientClusterAccessor.Dispose();
            }
            if (hasClusterAccessor)
            {
                clusterAccessor.Dispose();
            }

            // saveChanges and ReleaseExcessDirtyMarks are deliberately NOT called here. ExecuteMigrations operates on the UoW's shared ChangeSet (passed
            // by the caller through WriteClusterTickFence → WriteTickFence). The UoW owns the commit lifecycle: in WAL mode SaveChanges is never called
            // (WAL records replace direct page writes); in WAL-less GroupCommit/Deferred modes UoW.Flush invokes SaveChanges + FlushToDisk centrally;
            // ReleaseExcessDirtyMarks happens once at UoW disposal. See claude/overview/02-execution.md §2.1 (UoW lifecycle) and §2.3 (durability modes).
            // Test/admin callers that invoke WriteTickFence without a UoW get a one-shot local ChangeSet created and committed by WriteTickFence itself.

            // NOTE: PendingMigrationCount is reset to 0 by FinalizeArchetypeFence after ALL slices have completed — resetting here would race with sibling
            // slices reading PendingMigrations / PendingMigrationCount.
        }

        var endTimestamp = Stopwatch.GetTimestamp();
        var durationMs = (endTimestamp - startTimestamp) * 1000.0 / Stopwatch.Frequency;
        // Accumulate per-slice counters atomically — multiple workers may slice the same archetype's PendingMigrations.
        Interlocked.Add(ref clusterState.LastTickMigrationCount, count);
        // Time accumulation as double via CAS-loop (no Interlocked.Add(double) in .NET).
        SpinWait sw = default;
        while (true)
        {
            var current = clusterState.LastTickMigrationExecuteMs;
            var candidate = current + durationMs;
            if (Interlocked.CompareExchange(ref Unsafe.As<double, long>(ref clusterState.LastTickMigrationExecuteMs), BitConverter.DoubleToInt64Bits(candidate), 
                    BitConverter.DoubleToInt64Bits(current)) == BitConverter.DoubleToInt64Bits(current))
            {
                break;
            }
            sw.SpinOnce();
        }
        // Test observation hook: each slice writes the (constant for this fence) dirtyBits length — the last writer wins; value is the same.
        clusterState.LastMigrationDirtyBitsWordCount = dirtyBits.Length;

        if (count >= 1000)
        {
            SpatialMaintainer.LogHighMigrationRate(Logger, count, archetypeId, durationMs);
        }
    }

    /// <summary>
    /// Drains the per-archetype shadow buffers for cluster-backed indexed fields, updating per-archetype B+Trees. Reads current field values from cluster SoA,
    /// compares with captured old values, and calls B+Tree.Move for changes. Called at tick boundary from <see cref="WriteClusterTickFence"/>.
    /// </summary>
    private unsafe int ProcessClusterShadowEntries(ArchetypeClusterState clusterState, ArchetypeEngineState engineState, ChangeSet changeSet)
    {
        // Quick check: any shadow buffers non-empty? Skip allocation if all empty.
        var anyShadow = false;
        var ixSlots = clusterState.IndexSlots;
        for (var s = 0; s < ixSlots.Length && !anyShadow; s++)
        {
            for (var f = 0; f < ixSlots[s].Fields.Length; f++)
            {
                if (ixSlots[s].ShadowBuffers[f].Count > 0)
                {
                    anyShadow = true;
                    break;
                }
            }
        }

        if (!anyShadow)
        {
            clusterState.ClusterShadowBitmap.Clear();
            return 0;
        }

        var clusterAccessor = clusterState.ClusterSegment.CreateChunkAccessor();

        var totalShadowEntries = 0;
        try
        {
            for (var s = 0; s < ixSlots.Length; s++)
            {
                ref var ixSlot = ref ixSlots[s];
                for (var f = 0; f < ixSlot.Fields.Length; f++)
                {
                    var buffer = ixSlot.ShadowBuffers[f];
                    var count = buffer.Count;
                    if (count == 0)
                    {
                        continue;
                    }

                    totalShadowEntries += count;

                    ref var field = ref ixSlot.Fields[f];
                    var idxAccessor = field.Index.Segment.CreateChunkAccessor(changeSet);

                    try
                    {
                        for (var e = 0; e < count; e++)
                        {
                            ref var entry = ref buffer[e];
                            var clusterChunkId = entry.ChunkId >> 6;   // entityIndex → chunkId
                            var slotIndex = entry.ChunkId & 0x3F;      // entityIndex → slot

                            // Check occupancy (entity may have been destroyed this tick)
                            var clusterBase = clusterAccessor.GetChunkAddress(clusterChunkId);
                            var occupancy = *(ulong*)clusterBase;
                            if ((occupancy & (1UL << slotIndex)) == 0)
                            {
                                // Entity destroyed — remove old index entry using shadow value
                                var destroyOldKey = entry.OldKey;
                                field.Index.Remove(&destroyOldKey, out _, ref idxAccessor);

                                // Notify views of deletion (same pattern as ProcessShadowFieldEntries)
                                var table = engineState.SlotToComponentTable[ixSlot.Slot];
                                var delViews = table.ViewRegistry.GetViewsForField(f);
                                for (var v = 0; v < delViews.Length; v++)
                                {
                                    var reg = delViews[v];
                                    if (reg.View.IsDisposed)
                                    {
                                        continue;
                                    }

                                    var delFlags = (byte)((f & 0x3F) | 0x80); // isDeletion
                                    reg.DeltaBuffer.TryAppend(entry.EntityPK, entry.OldKey, default, 0, delFlags, reg.ComponentTag);
                                }

                                continue;
                            }

                            // Read current (post-mutation) field value from cluster SoA
                            var compSize = clusterState.Layout.ComponentSize(ixSlot.Slot);
                            var compBase = clusterBase + clusterState.Layout.ComponentOffset(ixSlot.Slot) + slotIndex * compSize;
                            var fieldPtr = compBase + field.FieldOffset;
                            var oldKey = entry.OldKey;
                            var newKey = KeyBytes8.FromPointer(fieldPtr, field.FieldSize);

                            if (oldKey.RawValue == newKey.RawValue)
                            {
                                continue; // Field didn't actually change
                            }

                            // Update per-archetype B+Tree: remove old key, insert new key, same ClusterLocation value
                            var clusterLocation = entry.ChunkId; // entityIndex = clusterLocation
                            field.Index.Move(&oldKey, fieldPtr, clusterLocation, ref idxAccessor);

                            // Notify registered views (same pattern as ProcessShadowFieldEntries)
                            {
                                var table = engineState.SlotToComponentTable[ixSlot.Slot];
                                var views = table.ViewRegistry.GetViewsForField(f);
                                for (var v = 0; v < views.Length; v++)
                                {
                                    var reg = views[v];
                                    if (reg.View.IsDisposed)
                                    {
                                        continue;
                                    }

                                    var flags = (byte)(f & 0x3F);
                                    reg.DeltaBuffer.TryAppend(entry.EntityPK, oldKey, newKey, 0, flags, reg.ComponentTag);
                                }
                            }
                        }
                    }
                    finally
                    {
                        idxAccessor.Dispose();
                    }

                    buffer.Reset();
                }
            }
        }
        finally
        {
            clusterAccessor.Dispose();
            // SaveChanges deliberately omitted: caller (WriteTickFence) owns the ChangeSet lifecycle. See ExecuteMigrations finally for full rationale.
        }

        clusterState.ClusterShadowBitmap.Clear();
        return totalShadowEntries;
    }

    /// <summary>
    /// Drains the per-field shadow buffers for a SingleVersion ComponentTable, updating indexes and notifying views for any field values that changed since
    /// the shadow was captured.
    /// Called at tick boundary from <see cref="WriteTickFence"/>.
    /// </summary>
    private int ProcessShadowEntries(ComponentTable table, ChangeSet changeSet)
    {
        var fields = table.IndexedFieldInfos;
        var buffers = table.FieldShadowBuffers;
        var isTransient = table.StorageMode == StorageMode.Transient;

        var totalShadowEntries = 0;
        for (var fieldIdx = 0; fieldIdx < fields.Length; fieldIdx++)
        {
            var buffer = buffers[fieldIdx];
            var count = buffer.Count;
            if (count == 0)
            {
                continue;
            }

            totalShadowEntries += count;

            ref var ifi = ref fields[fieldIdx];

            if (isTransient)
            {
                var index = ifi.TransientIndex;
                var compAccessor = table.TransientComponentSegment.CreateChunkAccessor();
                var idxAccessor = index.Segment.CreateChunkAccessor();
                try
                {
                    ProcessShadowFieldEntries(table, fieldIdx, ref ifi, buffer, count, index, ref compAccessor, ref idxAccessor);
                }
                finally
                {
                    compAccessor.Dispose();
                    idxAccessor.Dispose();
                }
            }
            else
            {
                var index = ifi.PersistentIndex;

                // ChangeSet required for index write operations (Move/MoveValue may trigger TAIL segment growth for AllowMultiple indexes).
                // Reuse the caller's shared ChangeSet — UoW owns the commit lifecycle (see WriteTickFence).
                var compAccessor = table.ComponentSegment.CreateChunkAccessor(changeSet);
                var idxAccessor = index.Segment.CreateChunkAccessor(changeSet);
                try
                {
                    ProcessShadowFieldEntries(table, fieldIdx, ref ifi, buffer, count, index, ref compAccessor, ref idxAccessor);
                }
                finally
                {
                    compAccessor.Dispose();
                    idxAccessor.Dispose();
                }
            }

            buffer.Reset();
        }

        table.ShadowBitmap.Clear();
        table.ClearDestroyedChunkIds();
        return totalShadowEntries;
    }

    /// <summary>
    /// Processes all shadow entries for a single indexed field, updating the B+Tree index and notifying views.
    /// Generic over TStore to support both PersistentStore (Versioned/SV) and TransientStore paths.
    /// </summary>
    private static unsafe void ProcessShadowFieldEntries<TStore>(ComponentTable table, int fieldIdx, ref IndexedFieldInfo ifi,
        FieldShadowBuffer buffer, int count, BTreeBase<TStore> index, ref ChunkAccessor<TStore> compAccessor, ref ChunkAccessor<TStore> idxAccessor)
        where TStore : struct, IPageStore
    {
        for (var e = 0; e < count; e++)
        {
            ref var entry = ref buffer[e];

            // Check if entity was destroyed this tick.
            // PrepareEcsDestroys handles non-shadowed destroys; here we handle shadowed (mutated-then-destroyed).
            if (table.IsChunkDestroyed(entry.ChunkId))
            {
                // Entity is dead — remove old index entry using shadow value (matches current index key).
                // Copy to local to allow address-of on stack variable.
                var destroyOldKey = entry.OldKey;
                if (index.AllowMultiple)
                {
                    var ptr = compAccessor.GetChunkAddress(entry.ChunkId);
                    var elementId = *(int*)(ptr + ifi.OffsetToIndexElementId);
                    index.RemoveValue(&destroyOldKey, elementId, entry.ChunkId, ref idxAccessor);
                }
                else
                {
                    index.Remove(&destroyOldKey, out _, ref idxAccessor);
                }

                // Notify views of deletion
                var delViews = table.ViewRegistry.GetViewsForField(fieldIdx);
                for (var v = 0; v < delViews.Length; v++)
                {
                    var reg = delViews[v];
                    if (reg.View.IsDisposed)
                    {
                        continue;
                    }

                    var delFlags = (byte)((fieldIdx & 0x3F) | 0x80); // isDeletion
                    reg.DeltaBuffer.TryAppend(entry.EntityPK, entry.OldKey, default, 0, delFlags, reg.ComponentTag);
                }

                continue;
            }

            // Read current (post-mutation) field value
            var chunkPtr = compAccessor.GetChunkAddress(entry.ChunkId);
            var newFieldPtr = chunkPtr + ifi.OffsetToField;
            var oldKey = entry.OldKey;
            var newKey = KeyBytes8.FromPointer(newFieldPtr, ifi.Size);

            // Skip if field value didn't actually change
            if (oldKey.RawValue == newKey.RawValue)
            {
                continue;
            }

            // Update B+Tree index
            if (index.AllowMultiple)
            {
                var elementId = *(int*)(chunkPtr + ifi.OffsetToIndexElementId);
                var newElementId = index.MoveValue(&oldKey, newFieldPtr, elementId, entry.ChunkId, ref idxAccessor, out _, out _);
                // Write back new element ID — page is already dirty from the mutation that triggered shadowing
                *(int*)(chunkPtr + ifi.OffsetToIndexElementId) = newElementId;
            }
            else
            {
                index.Move(&oldKey, newFieldPtr, entry.ChunkId, ref idxAccessor);
            }

            // Notify registered views
            var views = table.ViewRegistry.GetViewsForField(fieldIdx);
            for (var v = 0; v < views.Length; v++)
            {
                var reg = views[v];
                if (reg.View.IsDisposed)
                {
                    continue;
                }

                var flags = (byte)(fieldIdx & 0x3F);
                reg.DeltaBuffer.TryAppend(entry.EntityPK, oldKey, newKey, 0, flags, reg.ComponentTag);
            }
        }
    }
}
