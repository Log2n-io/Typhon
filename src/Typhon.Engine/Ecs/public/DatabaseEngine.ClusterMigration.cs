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
    /// Recompute zone maps for all dirty clusters in the dirty bitmap snapshot. Each dirty cluster gets a full min/max scan for each indexed field whose
    /// component was actually written this tick — a field nobody wrote cannot have changed its min/max, so rescanning it reproduces the same two values.
    /// </summary>
    /// <remarks>
    /// The narrowing reuses <see cref="ArchetypeClusterState.FenceWrittenSlots"/> (#559 §4.5), snapshotted by the caller immediately before this call. Without
    /// it an archetype whose indexed field is stable — the common shape: index the classification, scan the value that churns — rescans every dirty cluster
    /// every tick to recompute an identical answer. On the guide sample (20 001 entities, one indexed field the tick systems never touch) that was ~80 % of
    /// the fence's Prep phase.
    /// <para>
    /// Fail-safe by construction: the union is <see cref="ArchetypeClusterState.AllSlotsWritten"/> when any writer did not identify its component, so an
    /// unidentified write rescans everything. The failure direction is redundant work, never a stale zone map.
    /// </para>
    /// <para>
    /// Migration does not rely on this pass — <c>ExecuteMigrations</c> widens the destination zone map directly as it moves each entity (see the
    /// <c>ZoneMap?.Widen(dstChunkId, ...)</c> call below), so a cluster that only gained entities by migration is correct without a rescan here.
    /// </para>
    /// </remarks>
    /// <param name="clusterState">The archetype whose zone maps are being refreshed.</param>
    /// <param name="dirtyBits">One word per cluster chunk id; a non-zero word marks a cluster whose summaries may be stale.</param>
    /// <param name="clusterAccessor">
    /// The caller's OPEN accessor on <see cref="ArchetypeClusterState.ClusterSegment"/>, or <c>default</c> when the archetype is pure-Transient and has no
    /// cluster segment to accessorise. See the remarks on <see cref="ProcessClusterShadowEntries"/> for why this is threaded in rather than rented here.
    /// </param>
    private void RecomputeClusterZoneMaps(ArchetypeClusterState clusterState, long[] dirtyBits, ref ChunkAccessor<PersistentStore> clusterAccessor)
    {
        BeginZoneMapTick(clusterState);
        RecomputeClusterZoneMapsRange(clusterState, dirtyBits, ref clusterAccessor, 0, dirtyBits.Length);
    }

    /// <summary>Advances the zone-map exact-pass rotation once for this tick and publishes the phase every slice reads (#886 lead D).</summary>
    private static void BeginZoneMapTick(ArchetypeClusterState clusterState)
        => clusterState.ZoneMapRetightenPhase = (int)(Interlocked.Increment(ref clusterState.ZoneMapRetightenTick) & (ZoneMapRetightenPeriod - 1));

    /// <inheritdoc cref="RecomputeClusterZoneMaps"/>
    /// <remarks>The <c>[firstWord, firstWord + wordCount)</c> range is the slice's; the rotation phase must already have been published by
    /// <see cref="BeginZoneMapTick"/> — this method never advances it, or W slices would rotate the exact pass W times too fast.</remarks>
    private void RecomputeClusterZoneMapsRange(ArchetypeClusterState clusterState, long[] dirtyBits, ref ChunkAccessor<PersistentStore> clusterAccessor,
        int firstWord, int wordCount)
    {
        // Nothing durable-or-indexed was written this tick ⇒ every zone map still describes its cluster exactly. Bail before touching anything.
        var writtenSlots = clusterState.FenceWrittenSlots;
        if (writtenSlots == 0)
        {
            return;
        }

        var pureTransient = clusterState.ClusterSegment == null;

        if (HasZoneMaps(clusterState.IndexSlots))
        {
            Debug.Assert(!pureTransient, "a pure-Transient archetype cannot own PersistentStore-backed index slots");
            RecomputeZoneMapsForHome(clusterState, dirtyBits, writtenSlots, clusterState.IndexSlots, clusterState.ClusterSegment, ref clusterAccessor,
                ref clusterAccessor, firstWord, wordCount);
        }

        if (HasZoneMaps(clusterState.TransientIndexSlots))
        {
            var transientAccessor = clusterState.TransientSegment.CreateChunkAccessor();
            try
            {
                if (pureTransient)
                {
                    RecomputeZoneMapsForHome(clusterState, dirtyBits, writtenSlots, clusterState.TransientIndexSlots, clusterState.TransientSegment,
                        ref transientAccessor, ref transientAccessor, firstWord, wordCount);
                }
                else
                {
                    RecomputeZoneMapsForHome(clusterState, dirtyBits, writtenSlots, clusterState.TransientIndexSlots, clusterState.ClusterSegment,
                        ref clusterAccessor, ref transientAccessor, firstWord, wordCount);
                }
            }
            finally
            {
                transientAccessor.Dispose();
            }
        }
    }

    /// <summary>True when any field in <paramref name="ixSlots"/> carries a zone map. Null-safe; also false for a home whose only indexed fields are
    /// <c>String64</c>, which get none.</summary>
    private static bool HasZoneMaps<TStore>(ClusterIndexSlot<TStore>[] ixSlots) where TStore : struct, IPageStore
    {
        if (ixSlots == null)
        {
            return false;
        }

        for (var s = 0; s < ixSlots.Length; s++)
        {
            for (var f = 0; f < ixSlots[s].Fields.Length; f++)
            {
                if (ixSlots[s].Fields[f].ZoneMap != null)
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// How often a cluster's zone map is re-derived exactly rather than widened. Power of two so the rotation is a mask.
    /// </summary>
    /// <remarks>
    /// 16 ticks is ~0.27 s at 60 Hz. The cost of a larger value is selectivity: a bound stays as wide as the widest key any entity held since the last exact
    /// pass, so a cluster whose extreme entity left keeps that entity's key in its range until its turn comes round. The cost of a smaller one is the scan
    /// this whole path exists to avoid. Nothing depends on the exact figure — it is a knob, and the shape of the trade is monotone in both directions.
    /// </remarks>
    private const int ZoneMapRetightenPeriod = 16;

    /// <summary>
    /// Recomputes one index home's zone maps over the dirty clusters. Same three-store split as <see cref="ProcessClusterShadowEntries"/>: the occupancy word
    /// comes from <paramref name="primaryAccessor"/>, the component column from <paramref name="dataAccessor"/>, and they are the same accessor for every home
    /// except a Transient slot on a mixed archetype.
    /// </summary>
    /// <remarks>
    /// Allocation guard reads <paramref name="primarySegment"/> — the segment the dirty bits are indexed against. Chunk ids are lockstep across the two
    /// segments, so one guard covers both.
    /// </remarks>
    private static unsafe void RecomputeZoneMapsForHome<TIdx, TPrimary, TData>(ArchetypeClusterState clusterState, long[] dirtyBits, int writtenSlots,
        ClusterIndexSlot<TIdx>[] ixSlots, ChunkBasedSegment<TPrimary> primarySegment, ref ChunkAccessor<TPrimary> primaryAccessor,
        ref ChunkAccessor<TData> dataAccessor, int firstWord, int wordCount)
        where TIdx : struct, IPageStore
        where TPrimary : struct, IPageStore
        where TData : struct, IPageStore
    {
        // Field-outer, cluster-inner (#886 lead D): one shared acquire of the zone map's grow latch per field per call, not one per (cluster × field).
        // Under a sliced Prep the per-write acquire was a CAS on one padded word per field from eight cores at once — ~4 000 bounces a tick — and the
        // step's CPU tripled. The addresses are re-resolved per field, which is an accessor window hit; the latch was the cost.
        var retightenPhase = clusterState.ZoneMapRetightenPhase;
        var end = Math.Min(dirtyBits.Length, firstWord + wordCount);
        for (var s = 0; s < ixSlots.Length; s++)
        {
            ref var ixSlot = ref ixSlots[s];
            if ((writtenSlots & (1 << ixSlot.Slot)) == 0)
            {
                continue;
            }

            for (var f = 0; f < ixSlot.Fields.Length; f++)
            {
                var zoneMap = ixSlot.Fields[f].ZoneMap;
                if (zoneMap == null)
                {
                    continue;
                }

                var fieldOffset = ixSlot.Fields[f].FieldOffset;
                var store = zoneMap.BeginBatch(end);
                try
                {
                    for (var wordIdx = firstWord; wordIdx < end; wordIdx++)
                    {
                        if (dirtyBits[wordIdx] == 0)
                        {
                            continue;
                        }

                        var clusterChunkId = wordIdx;
                        if (clusterChunkId == 0 || !primarySegment.IsChunkAllocated(clusterChunkId))
                        {
                            continue;
                        }

                        var primaryBase = primaryAccessor.GetChunkAddress(clusterChunkId);
                        var dataBase = dataAccessor.GetChunkAddress(clusterChunkId);
                        var exactPass = ((clusterChunkId + retightenPhase) & (ZoneMapRetightenPeriod - 1)) == 0;
                        if (exactPass)
                        {
                            zoneMap.RecomputeInto(store, clusterChunkId, primaryBase, dataBase, clusterState.Layout, ixSlot.Slot, fieldOffset);
                        }
                        else
                        {
                            zoneMap.WidenMaskedInto(store, clusterChunkId, (ulong)dirtyBits[wordIdx], dataBase, clusterState.Layout, ixSlot.Slot, fieldOffset);
                        }
                    }
                }
                finally
                {
                    zoneMap.EndBatch();
                }
            }
        }
    }

    private unsafe void DetectClusterMigrations(ArchetypeClusterState clusterState, ArchetypeEngineState engineState, ushort archetypeId, long[] dirtyBits,
        ref ChunkAccessor<PersistentStore> clusterAccessor)
    {
        clusterState.EnsurePendingMigrationCapacityForTick();
        DetectClusterMigrationsRange(clusterState, engineState, archetypeId, dirtyBits, ref clusterAccessor, 0, dirtyBits.Length, null);
    }

    /// <summary>
    /// Step (a) of detection: the crossings <c>ClusterRef.WriteSpatial</c> already flagged at write time
    /// (<see cref="ArchetypeClusterState.ClusterMigrationPendingSlots"/>), appended to the shared queue in ascending cluster order. Serial — the
    /// unsliced detector calls it first, the sliced Prep's head calls it once before the slices; a slice never does.
    /// </summary>
    /// <remarks>
    /// <para><b>The flag says WHICH slot moved; the position says WHERE it is.</b> The barrier records a per-cluster destination computed from the value
    /// it was handed, and nothing un-flags a slot: an entity written out of its cell and back within one tick, or a slot two writes carried to two
    /// different cells, still carries the first destination at the fence. Executing it moves an entity into a cell it is not in — silently, every
    /// counter balancing (CC-02). The same goes for a write that reaches a spawn's slot inside the claim-to-data window the spawn commit still has
    /// (see <c>NoteClusterBorn</c>'s "known residual"): the writer flags a crossing from ITS value, the spawn's own position then lands, and the flag
    /// describes an entity that never existed. <c>ClusterPlacementTests.ConcurrentSpawnsAndBoundGrowthKeepClustersInTheirCell</c> reached that one in
    /// about one cold run in ten; <c>ClusterMigrationTests.WriteSpatial_CrossAndReturnInOneTick_StaysInItsCell</c> reaches the first with two writes.</para>
    /// <para>So the drain re-reads the slot's field and decides exactly as the legacy scan does — exited with the hysteresis margin, then the cell the
    /// centre resolves to — and the recorded key is at most a hint. One field read per flagged slot, against the migration it would otherwise
    /// mis-execute. A flag whose entity is home is dropped and counted in <see cref="ArchetypeClusterState.LastTickStaleFlagsDropped"/>.</para>
    /// </remarks>
    private static unsafe void DrainPreFlaggedMigrations(ArchetypeClusterState clusterState, ushort archetypeId, ref ChunkAccessor<PersistentStore> clusterAccessor,
        ref int migrationsQueuedCount, ref int clustersTouched)
    {
        var processBitmap = clusterState.ClusterProcessBitmap;
        var migrationPending = clusterState.ClusterMigrationPendingSlots;
        if (processBitmap == null || migrationPending == null)
        {
            return;
        }

        ref var ss = ref clusterState.SpatialSlot;
        var layout = clusterState.Layout;
        var compSize = layout.ComponentSize(ss.Slot);
        var compOffset = layout.ComponentOffset(ss.Slot);
        var fieldType = ss.FieldInfo.FieldType;
        var grid = clusterState.Grid;
        ref readonly var cfg = ref grid.Config;
        var cellSize = cfg.CellSize;
        var hysteresisMargin = cellSize * cfg.MigrationHysteresisRatio;
        var staleDropped = 0;

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

                var currentCellKey = clusterState.ClusterCellMap[chunkId];
                if (currentCellKey < 0)
                {
                    continue;
                }

                clustersTouched++;
                var clusterBase = clusterAccessor.GetChunkAddress(chunkId);
                var (cx, cy, cz) = grid.CellKeyToCoords(currentCellKey);
                var curCellMinX = cfg.WorldMin.X + cx * cellSize;
                var curCellMinY = cfg.WorldMin.Y + cy * cellSize;
                var curCellMinZ = cfg.WorldMin.Z + cz * cellSize;
                while (slotMask != 0)
                {
                    var slotIndex = BitOperations.TrailingZeroCount(slotMask);
                    slotMask &= slotMask - 1;
                    var fieldPtr = clusterBase + compOffset + slotIndex * compSize + ss.FieldOffset;
                    SpatialGrid.ReadSpatialCenter3D(fieldPtr, fieldType, out var posX, out var posY, out var posZ);
                    var exited = posX < curCellMinX - hysteresisMargin || posX > curCellMinX + cellSize + hysteresisMargin
                                 || posY < curCellMinY - hysteresisMargin || posY > curCellMinY + cellSize + hysteresisMargin
                                 || posZ < curCellMinZ - hysteresisMargin || posZ > curCellMinZ + cellSize + hysteresisMargin;
                    var destCellKey = exited ? grid.WorldToCellKey(posX, posY, posZ) : currentCellKey;
                    if (destCellKey == currentCellKey)
                    {
                        staleDropped++;
                        continue;
                    }

                    migrationsQueuedCount++;
                    TyphonEvent.EmitSpatialClusterMigrationDetect(archetypeId, chunkId, currentCellKey, destCellKey);
                    clusterState.EnqueueMigration(chunkId, slotIndex, destCellKey);
                    TyphonEvent.EmitSpatialClusterMigrationQueue(archetypeId, chunkId,
                        (ushort)Math.Min(clusterState.PendingMigrationCount, ushort.MaxValue));
                }
            }
        }

        clusterState.LastTickStaleFlagsDropped = staleDropped;
    }

    /// <inheritdoc cref="DetectClusterMigrations"/>
    // firstWord: First dirty-bitmap word this call owns (#886 lead D: a Prep slice detects only its own clusters).
    // wordCount: Width of the owned range, in words.
    // sink: Where a slice files its crossings — slice-private, concatenated by the tail in slice order. <c>null</c> appends to the shared
    // queue directly, which is the unsliced path and the only one allowed to grow it.
    private unsafe void DetectClusterMigrationsRange(ArchetypeClusterState clusterState, ArchetypeEngineState engineState, ushort archetypeId, long[] dirtyBits,
        ref ChunkAccessor<PersistentStore> clusterAccessor, int firstWord, int wordCount, List<MigrationRequest> sink)
    {
        // Hybrid migration detection:
        //   (a) Drain pre-flagged migrations from ClusterMigrationPendingSlots (set by WriteSpatial at write time — sparse, near-zero cost).
        //   (b) Fall back to the legacy scan over dirtyBits for slots the barrier didn't cover (legacy writers: Transaction.OpenMut + Write — the MVCC commit
        //       path doesn't go through WriteSpatial yet). Each cluster's pre-flagged slot mask is used to skip already-handled slots in the scan, so the two
        //       paths don't double-enqueue.
        //
        // For AntHill (all writes through WriteSpatial), step (b)'s per-slot work is fully masked out — the loop body becomes a popcount-and-skip,
        // which is fast even at 100k entities.
        var migrationPending = clusterState.ClusterMigrationPendingSlots;

        var scanSlotCount = 0;
        if (TelemetryConfig.SpatialClusterMigrationDetectActive)
        {
            var scanEnd = Math.Min(dirtyBits.Length, firstWord + wordCount);
            for (var wi = firstWord; wi < scanEnd; wi++)
            {
                scanSlotCount += BitOperations.PopCount((ulong)dirtyBits[wi]);
            }
        }
        var detectScanSpan = TyphonEvent.BeginSpatialClusterMigrationDetectScan(archetypeId, scanSlotCount);
        try
        {
            var migrationsQueuedCount = 0;
            var hysteresisAbsorbedCount = 0;
            var clustersTouched = 0;

            // ─── Step (a): drain WriteSpatial-flagged migrations ───
            //
            // Whole-bitmap and serial by shape: it walks ClusterProcessBitmap end to end and appends to the shared queue. A Prep slice therefore never runs
            // it — the head does, once, before the slices (#886): the queue then reads step (a) first and the slices' step (b) after, which is the order
            // the unsliced detector produces. Running it per slice would have appended W copies of every pre-flagged request through the unsynchronised
            // EnqueueMigration, and CR-05's guard is Debug-only.
            if (sink == null)
            {
                DrainPreFlaggedMigrations(clusterState, archetypeId, ref clusterAccessor, ref migrationsQueuedCount, ref clustersTouched);
            }

            // ─── Step (b): legacy scan over dirtyBits for slots not covered by step (a) ───
            // Skipped entirely when SpatialBarrierOnly — caller has guaranteed every spatial write
            // goes through WriteSpatial, so step (a) is exhaustive.
            if (clusterState.SpatialBarrierOnly)
            {
                // This branch contributes NOTHING to the absorbed count, and must not pretend otherwise. The local's only ++ is in step (b) below, which this
                // branch returns before reaching, so it is provably zero here — writing it to the field or the span would be arithmetic that reads as if it
                // did something. On the barrier-only path the count is produced at write time by ClusterRef.MaybeFlagMigration and already drained into
                // LastTickHysteresisAbsorbedCount by PrepareArchetypeFence, so THAT is the truthful value for the span to report (#872).
                Debug.Assert(hysteresisAbsorbedCount == 0,
                    "step (a) has started counting absorbed crossings — fold it into the drain in PrepareArchetypeFence instead of dropping it here");
                detectScanSpan.MigrationsQueued = migrationsQueuedCount;
                detectScanSpan.HysteresisAbsorbed = clusterState.LastTickHysteresisAbsorbedCount;
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
            var worldMinZ = cfg.WorldMin.Z;
            var hysteresisMargin = cellSize * cfg.MigrationHysteresisRatio;

            var end = Math.Min(dirtyBits.Length, firstWord + wordCount);
            for (var wordIdx = firstWord; wordIdx < end; wordIdx++)
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

                var (cx, cy, cz) = grid.CellKeyToCoords(currentCellKey);
                var curCellMinX = worldMinX + cx * cellSize;
                var curCellMinY = worldMinY + cy * cellSize;
                var curCellMinZ = worldMinZ + cz * cellSize;
                var curCellMaxX = curCellMinX + cellSize;
                var curCellMaxY = curCellMinY + cellSize;
                var curCellMaxZ = curCellMinZ + cellSize;
                clustersTouched++;

                var remaining = effective;
                while (remaining != 0)
                {
                    var slotIndex = BitOperations.TrailingZeroCount(remaining);
                    remaining &= remaining - 1;
                    var entityPK = *(long*)(clusterBase + layout.EntityIdsOffset + slotIndex * 8);
                    var fieldPtr = clusterBase + compOffset + slotIndex * compSize + ss.FieldOffset;
                    SpatialGrid.ReadSpatialCenter3D(fieldPtr, fieldType, out var posX, out var posY, out var posZ);
                    if (!float.IsFinite(posX) || !float.IsFinite(posY) || !float.IsFinite(posZ))
                    {
                        throw new InvalidOperationException(
                            $"Non-finite position on spatial entity: entityId=0x{entityPK:X16}, clusterChunkId={clusterChunkId}, slotIndex={slotIndex}, "
                            + $"position=({posX}, {posY}, {posZ}).");
                    }
                    var exited = posX < curCellMinX - hysteresisMargin
                                 || posX > curCellMaxX + hysteresisMargin
                                 || posY < curCellMinY - hysteresisMargin
                                 || posY > curCellMaxY + hysteresisMargin
                                 || posZ < curCellMinZ - hysteresisMargin
                                 || posZ > curCellMaxZ + hysteresisMargin;
                    if (exited)
                    {
                        var newCellKey = grid.WorldToCellKey(posX, posY, posZ);
                        if (newCellKey != currentCellKey)
                        {
                            migrationsQueuedCount++;
                            TyphonEvent.EmitSpatialClusterMigrationDetect(archetypeId, clusterChunkId, currentCellKey, newCellKey);
                            if (sink != null)
                            {
                                sink.Add(new MigrationRequest(clusterChunkId, slotIndex, newCellKey));
                            }
                            else
                            {
                                clusterState.EnqueueMigration(clusterChunkId, slotIndex, newCellKey);
                            }

                            TyphonEvent.EmitSpatialClusterMigrationQueue(archetypeId, clusterChunkId,
                                (ushort)Math.Min(sink?.Count ?? clusterState.PendingMigrationCount, ushort.MaxValue));
                        }
                    }
                    else if (   posX < curCellMinX || posX > curCellMaxX
                             || posY < curCellMinY || posY > curCellMaxY
                             || posZ < curCellMinZ || posZ > curCellMaxZ)
                    {
                        hysteresisAbsorbedCount++;
                        if (TelemetryConfig.SpatialClusterMigrationHysteresisActive)
                        {
                            var ex = posX < curCellMinX ? (curCellMinX - posX) : (posX > curCellMaxX ? (posX - curCellMaxX) : 0f);
                            var ey = posY < curCellMinY ? (curCellMinY - posY) : (posY > curCellMaxY ? (posY - curCellMaxY) : 0f);
                            var ez = posZ < curCellMinZ ? (curCellMinZ - posZ) : (posZ > curCellMaxZ ? (posZ - curCellMaxZ) : 0f);
                            TyphonEvent.EmitSpatialClusterMigrationHysteresis(archetypeId, clusterChunkId, (ex * ex) + (ey * ey) + (ez * ez));
                        }
                    }
                }
            }

            // This is the ONLY producer of the absorbed count on the non-barrier path, and the mirror image of the branch above: here the scan counts and the
            // live write-time accumulator is the one that stays zero. `+=` rather than `=` so the two compose instead of one clobbering the other — the fence
            // has already drained the live value into this field by the time we get here, and a `=` would silently discard it if an archetype ever produced
            // both. Exactly one of the two is non-zero for a given archetype today; the Debug.Assert above is what says so out loud.
            if (sink != null)
            {
                Interlocked.Add(ref clusterState.PrepSliceHysteresisAbsorbed, hysteresisAbsorbedCount);
            }
            else
            {
                clusterState.LastTickHysteresisAbsorbedCount += hysteresisAbsorbedCount;
                clusterState.TotalHysteresisAbsorbedCount += hysteresisAbsorbedCount;
            }

            detectScanSpan.MigrationsQueued = migrationsQueuedCount;
            detectScanSpan.HysteresisAbsorbed = sink != null ? hysteresisAbsorbedCount : clusterState.LastTickHysteresisAbsorbedCount;
            detectScanSpan.ClustersTouched = clustersTouched;
        }
        finally
        {
            detectScanSpan.Dispose();
        }
    }

    /// <summary>
    /// The inline per-entity location updater, used when the batch is too small for bucket runs to exist.
    /// </summary>
    /// <remarks>
    /// Reads the record's own BornTSN/DiedTSN back out under the same bucket write lock that performs the patch, exactly as
    /// <see cref="ClusterLocationBulkUpdater"/> does for the staged path — the H1 fold needs them at that instant, not from a later lookup.
    /// </remarks>
    private unsafe struct ClusterLocationInlineUpdater : IRawValueUpdater
    {
        private readonly int _chunkId;
        private readonly byte _slotIndex;

        public ClusterLocationInlineUpdater(int chunkId, byte slotIndex)
        {
            _chunkId = chunkId;
            _slotIndex = slotIndex;
        }

        public long ObservedBornTsn;

        public long ObservedDiedTsn;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Update(byte* valueBytes)
        {
            ref var header = ref ClusterEntityRecordAccessor.GetHeader(valueBytes);
            ObservedBornTsn = header.BornTSN;
            ObservedDiedTsn = header.DiedTSN;
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
    ///   <item><see cref="ArchetypeClusterState.ClaimSlotInCell(int, ref ChunkAccessor{PersistentStore}, ChangeSet, SpatialGrid, long)"/> on the
    ///         destination cell (allocates a new cluster if needed)</item>
    ///   <item>Copy every component slot's bytes source → destination (Persistent + Transient; Q8)</item>
    ///   <item>Copy EntityId and EnabledBits</item>
    ///   <item>Remove the old per-archetype B+Tree index entries and insert new ones at the new <c>clusterLocation</c></item>
    ///   <item>Union the migrant's bounds into the destination cluster's AABB, rebased into the destination cell's frame, and add or update that
    ///         cluster in the destination cell's index. There is no per-entity spatial back-pointer to move: since #872 step 13 the per-cell cluster
    ///         index is the only index home (rule SH-01), and the entity-level R-Tree it used to point into no longer exists. The SOURCE cluster's
    ///         bound is deliberately left conservative here rather than shrunk in place; the fence's refresh pass retightens it</item>
    ///   <item>Upsert the EntityMap <see cref="ClusterEntityRecordAccessor"/> with the new (chunkId, slot)</item>
    ///   <item><see cref="ArchetypeClusterState.ReleaseSlot(ref ChunkAccessor{PersistentStore}, int, int, ChangeSet, SpatialGrid, bool)"/> on the
    ///         source (clears occupancy, decrements cell.EntityCount, detaches empty clusters)</item>
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
        int sliceCount, ChangeSet changeSet, List<DirtyBitDelta> dirtyBuffer = null, int chunkIndex = 0)
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
        //
        // Position is load-bearing: this brackets the three CreateChunkAccessor rentals below, any of which can block on page acquisition, exactly as it did
        // before #911. Moving it past them shrinks kind 60's duration with no work getting faster, which silently breaks every timeline comparison against a
        // pre-#911 trace. Not a `using var` — the per-kind fields are written after the loop and a using-variable is readonly — so it is disposed in the
        // `finally` below, after those writes, because Dispose is what publishes the record.
        //
        // Residual, stated rather than papered over: an exception between here and that `try` leaks the ring slot. The statements in between are field reads
        // and struct constructions, so this is the "the process is already failing" case, and the alternative (an outer try wrapping the whole method) buys
        // that at the price of re-indenting 400 lines.
        var migrationScope = TyphonEvent.BeginClusterMigration(archetypeId, count, count * componentCount);

        // #911 O1. `ProfilerActive` is `static readonly`, so when the profiler is off the JIT folds this to `false` and deletes every counting block below —
        // the same zero-cost-when-off guarantee the Begin* factories give. Counted over the whole slice rather than over the requests that survive the
        // stale-source guard, because the span's own MigrationCount is the slice length: the three add up to it exactly, which makes the split checkable
        // in a trace instead of merely plausible.
        var kindCountsActive = TelemetryConfig.ProfilerActive;
        var crossingCount = 0;
        var relocationCount = 0;
        var repairCount = 0;

        var grid = _spatialGrid;
        var transientMask = layout.TransientSlotMask;
        ref var ss = ref clusterState.SpatialSlot;
        var spatialCompSlot = ss.Slot;
        var spatialCompOffset = layout.ComponentOffset(spatialCompSlot);
        var spatialCompSize = layout.ComponentSize(spatialCompSlot);

        // Single-assignment accessor construction (TYPHON004 forbids the default→reassign pattern).
        var hasClusterAccessor = clusterState.ClusterSegment != null;

        // Step 14: the cluster this slice allocated for FreshCluster relocations, per destination cell. Slices are carved on DestCellKey and a
        // relocation's destination is its source cell, so the pair never has to survive the slice. See ClaimSlotInFreshCluster.
        var freshCell = -1;
        var freshCluster = -1;
        var clusterAccessor = hasClusterAccessor ? clusterState.ClusterSegment.CreateChunkAccessor(changeSet) : default;

        var hasTransientClusterAccessor = clusterState.TransientSegment != null;
        var transientClusterAccessor = hasTransientClusterAccessor ? clusterState.TransientSegment.CreateChunkAccessor() : default;

        // No index accessor is rented here anymore. Step 6 replaced this loop's Remove(key) + Add(key, newLoc) with an append to IndexUpdateStaging, and
        // staging touches no B+Tree page — the IndexMassUpdate phase rents its own accessors when it applies the batch. The pair that used to be rented (the
        // archetype's index segment and, for String64 fields, its second segment — #658) were created and disposed with nothing in between.

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

                if (kindCountsActive)
                {
                    switch (req.Kind)
                    {
                        case MigrationKind.Relocation:
                            relocationCount++;
                            break;
                        case MigrationKind.Repair:
                            repairCount++;
                            break;
                        default:
                            crossingCount++;
                            break;
                    }
                }

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
                //    H1 bound: the claim publishes the destination occupancy bit, so the destination's visibility summary has to bound this entity BEFORE that
                //    store — but the entity's own BornTSN is not readable until step 9, under the EntityMap bucket lock. NextFreeId is the TSN high-water mark,
                //    hence an upper bound on the BornTSN of anything that already exists, which is what a migrated entity is. Folding it is conservative: the
                //    destination is gated until snapshots pass the current tick, and step 9's fold of the real (lower) value then only confirms it. Passing
                //    the real value here instead would need a second EntityMap probe per migrated entity, and reading it before the claim reopens exactly the
                //    torn-read window step 1b exists to close.
                var migrationBornBound = TransactionChain.NextFreeId;
                int dstChunkId;
                int dstSlot;
                // A step-10 intra-cell relocation names its destination CLUSTER, not just the cell: the least-enlargement choice detection made is the
                // entire point of the move, and first-fit would discard it (see MigrationRequest.DestClusterChunkId). AnyCluster keeps the cell-crossing
                // behaviour byte for byte — the pinned overload's first test is the sign check.
                // A step-12 repair additionally names the SLOT, because the whole destination layout is an output of the Morton sort rather than of the
                // claim. AnySlot keeps step 10's behaviour byte for byte — the pinned overload tests the sign before touching the exact-slot path.
                var preferredCluster = req.DestClusterChunkId;
                var preferredSlot = req.DestSlotIndex;
                if (preferredCluster == MigrationRequest.FreshCluster)
                {
                    (dstChunkId, dstSlot) = hasClusterAccessor
                        ? clusterState.ClaimSlotInFreshCluster(destCellKey, ref clusterAccessor, changeSet, grid, migrationBornBound, ref freshCell,
                            ref freshCluster)
                        : clusterState.ClaimSlotInFreshCluster(destCellKey, ref transientClusterAccessor, null, grid, migrationBornBound, ref freshCell,
                            ref freshCluster);
                }
                else
                {
                    // Step 15 (D4). A cell-crossing request names no cluster, and the drain is where the choice is best made: the destination cell is
                    // this slice's alone (slices are carved on DestCellKey), so the occupancy it reads is live and the bounds it reads already include
                    // the arrivals this slice has unioned in. The position comes from the source slot — the line the copy below is about to read anyway
                    // — rather than travelling in the request, rebased into the DESTINATION cell's frame, which is the frame ClusterAabbs stores (C15).
                    // A pinned request (relocation, repair) passes straight through: the pinned overload tests the pin before it looks at the point.
                    var destPx = 0f;
                    var destPy = 0f;
                    var destPz = 0f;
                    if (grid.Config.LeastEnlargementPlacement || grid.Config.GrowthCapPlacement)
                    {
                        // Both switches off: the pinned overload never looks at the point, so the field is not read for it (step 15 review).
                        var srcFieldPtr = srcPrimaryPre + spatialCompOffset + srcSlot * spatialCompSize + ss.FieldOffset;
                        SpatialGrid.ReadSpatialCenter3D(srcFieldPtr, ss.FieldInfo.FieldType, out var migrantX, out var migrantY, out var migrantZ);
                        grid.CellOrigin(destCellKey, out var destOriginX, out var destOriginY, out var destOriginZ);
                        destPx = migrantX - destOriginX;
                        destPy = migrantY - destOriginY;
                        destPz = migrantZ - destOriginZ;
                    }

                    if (hasClusterAccessor)
                    {
                        (dstChunkId, dstSlot) = clusterState.ClaimSlotInCell(destCellKey, preferredCluster, preferredSlot, destPx, destPy, destPz,
                            ref clusterAccessor, changeSet, grid, migrationBornBound);
                    }
                    else
                    {
                        (dstChunkId, dstSlot) = clusterState.ClaimSlotInCell(destCellKey, preferredCluster, preferredSlot, destPx, destPy, destPz,
                            ref transientClusterAccessor, grid, migrationBornBound);
                    }
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

                // 7. Stage this migrant's index value updates instead of applying them (#872 step 6). The KEY is unchanged — the component copy in step 4
                //    already moved the entity's bytes, so the destination holds the same indexed value it had — and only the VALUE (clusterLocation) moves.
                //    That is precisely the shape the bulk partitioning descent exists for, so what used to be Remove(key) + Add(key, newLoc) per migrant per
                //    field, two root-to-leaf descents to change four bytes, becomes one appended record. The IndexMassUpdate phase then applies every
                //    archetype's whole batch in one descent per leaf-snapped key range.
                // Gated on IndexSlots alone. The obvious-looking `IndexSegment != null` would be wrong: a field's nodes live in whichever segment its
                // stride requires, so an archetype indexed ONLY on String64 fields keeps them all in IndexSegmentString64 (#658) and would skip staging
                // entirely — its index would simply never be updated after a migration. IndexSlots is the list of fields that need updating, which is the
                // actual precondition.
                if (clusterState.IndexSlots != null)
                {
                    var ixSlots = clusterState.IndexSlots;
                    var staging = clusterState.IndexUpdates;
                    var fieldId = 0;
                    for (var ixs = 0; ixs < ixSlots.Length; ixs++)
                    {
                        ref var ixSlot = ref ixSlots[ixs];
                        var ixCompSize = layout.ComponentSize(ixSlot.Slot);
                        var dstCompBase = dstBase + layout.ComponentOffset(ixSlot.Slot) + dstSlot * ixCompSize;
                        for (var fi = 0; fi < ixSlot.Fields.Length; fi++, fieldId++)
                        {
                            ref var field = ref ixSlot.Fields[fi];
                            // fieldPtr already holds the key: the component copy in step 4 is src -> dst, so the destination bytes are the entity's current
                            // value. Passed straight through as a raw address rather than copied into a local first — building a KeyBytes8 here memcpy'd
                            // FieldSize bytes into an 8-byte struct (64 for a String64 field), smashing the stack and crashing the host. Same defect and
                            // same fix as the destroy twin (Transaction.ECS.cs:1393).
                            var fieldPtr = dstCompBase + field.FieldOffset;
                            var stride = field.Index.BulkEntryStride(field.AllowMultiple);
                            var dest = staging.Reserve(chunkIndex, fieldId, stride);

                            if (field.AllowMultiple)
                            {
                                // The elementId does NOT change, and that is the quiet simplification the conversion buys. Remove+Add relocated the entity's
                                // element into a new buffer and had to write the new id into the destination tail; a value-only update leaves the element
                                // exactly where it is, so the destination inherits the source's id and one VSBS allocation per migrant per field disappears.
                                // srcBase still holds the source cluster's bytes — step 4's copy is src -> dst and does not touch the elementId tail.
                                var elementId = *(int*)(srcBase + layout.IndexElementIdOffset(field.MultiFieldIndex, srcSlot));
                                field.Index.WriteBulkMultiEntry(dest, fieldPtr, elementId, oldClusterLocation, newClusterLocation);
                                *(int*)(dstBase + layout.IndexElementIdOffset(field.MultiFieldIndex, dstSlot)) = elementId;
                            }
                            else
                            {
                                field.Index.WriteBulkEntry(dest, fieldPtr, newClusterLocation);
                            }

                            // The zone map is not an index and has no ordering dependency on the tree, so it stays inline.
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
                    if (SpatialMaintainer.ReadAndValidateBoundsFromPtr(dstFieldPtr, ss.FieldInfo, migrantCoords))
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

                        // C15 (#872 step 9): the destination cluster's bounds belong to the DESTINATION cell's frame. This is the rebase point — a migrant's
                        // coordinates arrive in world space and are converted here, so a cluster that changes cell never carries a bound measured from the
                        // cell it left. Getting this wrong is silent: every bound would be off by exactly the offset between the two cells.
                        var dstCellKey = clusterState.ClusterCellMap[dstChunkId];
                        if (dstCellKey >= 0)
                        {
                            _spatialGrid.CellOrigin(dstCellKey, out float dstOriginX, out float dstOriginY, out float dstOriginZ);
                            if (ss.FieldInfo.FieldType == SpatialFieldType.AABB3F || ss.FieldInfo.FieldType == SpatialFieldType.BSphere3F)
                            {
                                dstClusterAabb.Union3F(
                                    ClusterSpatialAabb.ToCellRelativeMin(migrantCoords[0], dstOriginX),
                                    ClusterSpatialAabb.ToCellRelativeMin(migrantCoords[1], dstOriginY),
                                    ClusterSpatialAabb.ToCellRelativeMin(migrantCoords[2], dstOriginZ),
                                    ClusterSpatialAabb.ToCellRelativeMax(migrantCoords[3], dstOriginX),
                                    ClusterSpatialAabb.ToCellRelativeMax(migrantCoords[4], dstOriginY),
                                    ClusterSpatialAabb.ToCellRelativeMax(migrantCoords[5], dstOriginZ),
                                    archetypeCategory);
                            }
                            else
                            {
                                dstClusterAabb.Union2F(
                                    ClusterSpatialAabb.ToCellRelativeMin(migrantCoords[0], dstOriginX),
                                    ClusterSpatialAabb.ToCellRelativeMin(migrantCoords[1], dstOriginY),
                                    ClusterSpatialAabb.ToCellRelativeMax(migrantCoords[2], dstOriginX),
                                    ClusterSpatialAabb.ToCellRelativeMax(migrantCoords[3], dstOriginY),
                                    archetypeCategory);
                            }
                            if (!wasInIndex)
                            {
                                clusterState.AddClusterToPerCellIndex(dstChunkId, dstCellKey, dstClusterAabb);
                            }
                            else
                            {
                                clusterState.UpdateClusterInPerCellIndex(dstChunkId, dstCellKey, in dstClusterAabb);
                            }
                        }
                    }
                }

                // 9. Stage this migrant's EntityMap location patch instead of applying it (#872 step 7, §5.4).
                //    CRITICAL: EntityMap is keyed by EntityKey (the 52-bit top half of RawValue), NOT by the full RawValue stored in cluster slots. Passing
                //    RawValue here would silently miss every lookup — the map would never get updated, and the entity would remain resolvable via its stale
                //    (srcChunkId, srcSlot) pointer until a subsequent spawn reclaimed that slot, at which point the stale EntityMap entry would resolve to the
                //    unrelated new entity's bytes. Unpack explicitly.
                //    Regression test: Migration_ThenSubsequentSpawn_ReclaimingSourceSlot_DoesNotCorruptMigratedEntity.
                //
                //    What staging buys: TryUpdateInPlace already wrote only the 5 bytes that change, so the cost it could not shed was the hash, the
                //    PackedMeta read, the ResolveBucket, the directory lookup, the dirty-mark and the OLC lock/unlock pair — all per entity. Sorting the batch
                //    by bucket amortises every one of those across the entries sharing a bucket. The bucket index is computed HERE, on the worker, because the
                //    batch has to be sorted by it and recomputing it in the apply would put one of the two hashes back.
                var entityKey = EntityId.FromRaw(entityPK).EntityKey;
                if (clusterState.UseBulkEntityMapUpdate)
                {
                    clusterState.EntityMapUpdates.Add(chunkIndex, new EntityLocationUpdate
                    {
                        EntityKey = entityKey,
                        Bucket = engineState.EntityMap.BucketIndexOf(entityKey),
                        DstChunkId = dstChunkId,
                        DstSlot = dstSlot,
                    });
                }
                else
                {
                    // The inline path, kept for batches too small to produce bucket runs. It differs from the staged one ONLY in where the map write happens:
                    // the same fold, the same rollback, and the same conservative treatment of steps 10 and 11 below, so the two arms are comparable and the
                    // engine has one set of semantics rather than two.
                    var entry = new EntityLocationUpdate { EntityKey = entityKey, DstChunkId = dstChunkId, DstSlot = dstSlot };
                    var updater = new ClusterLocationInlineUpdater(dstChunkId, (byte)dstSlot);
                    if (engineState.EntityMap.TryUpdateInPlace(entityKey, ref updater, ref emAccessor))
                    {
                        clusterState.NoteClusterBorn(dstChunkId, updater.ObservedBornTsn);
                        if (updater.ObservedDiedTsn != 0)
                        {
                            clusterState.NoteClusterDied(dstChunkId, ArchetypeClusterState.VisibilityUnknown);
                        }
                    }
                    else
                    {
                        clusterState.RollbackOrphanedDestinationSlot(entry.DstChunkId, entry.DstSlot, entry.EntityKey, changeSet);
                    }
                }

                // The H1 visibility fold and the destroyed-in-flight rollback that used to live here now run in the EntityMapUpdate phase, which is where the
                // "was the entity still in the map" verdict becomes known. Steps 10 and 11 below are therefore no longer gated on that verdict, and both
                // resulting differences are CONSERVATIVE:
                //
                //   * An orphan's source slot is now released. ReleaseSlot derives `wasOccupied` from ClearSlotMetadata's return and gates the cell-count
                //     decrement and the drain check on it, so releasing a slot a destroy already cleared is a no-op — and a destroy clearing that slot is the
                //     only way the entity left the map. Had it somehow not, releasing is the correct action regardless.
                //   * An orphan now records a dirty-bit delta for a migration that gets rolled back. That marks slots dirty which need not be, never the
                //     reverse.
                //
                // Neither is reachable unless the orphan case fires at all, which step 0's occupancy pre-check and step 1b's entityPK guard are supposed to
                // have filtered out, and which is additionally an EW-01 violation that ExclusiveWindow now throws on rather than absorbing silently.

                // 10. Release the source slot. Clears occupancy, EnabledBits, EntityId, decrements cell.EntityCount. If the cluster becomes empty, the
                // finalize-and-free is DEFERRED to FinalizeArchetypeFence (review C-1) — freeing here would race with a concurrent ClaimSlotInCell that may
                // have just CAS-claimed a slot.
                if (hasClusterAccessor)
                {
                    clusterState.ReleaseSlot(ref clusterAccessor, srcChunkId, srcSlot, changeSet, grid, true);
                }
                else
                {
                    clusterState.ReleaseSlot(ref transientClusterAccessor, srcChunkId, srcSlot, grid, true);
                }

                // 10b. The source cluster just lost an entity, so its AABB may be too large — flag a full recompute for this tick's refresh pass (#872
                // step 10, AC-10.9). Step 8 above notes the src AABB "stays conservative (not shrunk) — Phase 1 trade-off"; that trade is what an
                // intra-cell relocation cannot accept, since tightening the source IS the point of the move. Cheap and unconditional: a cell-crossing
                // migration wants it just as much, and the flag costs one CAS against a cluster the refresh is likely to visit anyway.
                clusterState.FlagClusterForShrinkRefresh(srcChunkId);

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
            migrationScope.CrossingCount = crossingCount;
            migrationScope.RelocationCount = relocationCount;
            migrationScope.RepairCount = repairCount;

            emAccessor.Dispose();
            if (hasTransientClusterAccessor)
            {
                transientClusterAccessor.Dispose();
            }
            if (hasClusterAccessor)
            {
                clusterAccessor.Dispose();
            }

            // AFTER the accessor disposals, which is where `using var migrationScope` used to put it before the readonly-field rules forced an explicit
            // Dispose. The argument the comment at the span's START makes — that moving it shrinks kind 60 with no work getting faster, and silently breaks
            // every timeline comparison against a pre-#911 trace — applies to the END in exactly the same way. Releasing three accessors is real work this
            // method does, and the span has always covered it.
            migrationScope.Dispose();

            // saveChanges and ReleaseDirtyMarks are deliberately NOT called here. ExecuteMigrations operates on the UoW's shared ChangeSet (passed
            // by the caller through WriteClusterTickFence → WriteTickFence). The UoW owns the commit lifecycle: in WAL mode SaveChanges is never called
            // (WAL records replace direct page writes); in WAL-less GroupCommit/Deferred modes UoW.Flush invokes SaveChanges + FlushToDisk centrally;
            // ReleaseDirtyMarks happens once at UoW disposal. See claude/overview/02-execution.md §2.1 (UoW lifecycle) and §2.3 (durability modes).
            // Test/admin callers that invoke WriteTickFence without a UoW get a one-shot local ChangeSet created and committed by WriteTickFence itself.

            // NOTE: PendingMigrationCount is reset to 0 by FinalizeArchetypeFence after ALL slices have completed — resetting here would race with sibling
            // slices reading PendingMigrations / PendingMigrationCount.
        }

        var endTimestamp = Stopwatch.GetTimestamp();
        var durationMs = (endTimestamp - startTimestamp) * 1000.0 / Stopwatch.Frequency;
        // Accumulate per-slice counters atomically — multiple workers may slice the same archetype's PendingMigrations.
        Interlocked.Add(ref clusterState.LastTickMigrationCount, count);
        // Cumulative twin of the above (#872 step 1). One Interlocked per SLICE, not per migration — the per-tick counter is reset every fence, so an
        // asynchronous scrape of it samples one arbitrary tick and cannot yield a rate.
        //
        // This may well touch a SECOND contended cache line rather than sharing the one above: ArchetypeClusterState is a plain sealed class, so the CLR uses
        // LayoutKind.Auto and groups fields by alignment — an 8-byte long and a 4-byte int declared adjacently are not laid out adjacently, and declaration
        // order buys nothing. If the Migrate phase ever shows up as contended here, MD-03's remedy is an explicit padded struct, not a field reorder.
        Interlocked.Add(ref clusterState.TotalMigrationCount, count);
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
    /// <remarks>
    /// Dispatches over the archetype's two index homes (#655). Three segments are in play and they are not the same axis:
    /// <list type="bullet">
    ///   <item><description><b>index</b> — where the tree's nodes live: the persisted segments for SingleVersion / Versioned slots, the heap-backed ones for
    ///   Transient slots.</description></item>
    ///   <item><description><b>primary</b> — where the occupancy word and the AllowMultiple elementId tail live: the cluster segment, or the Transient segment
    ///   when the archetype is pure-Transient and has no cluster segment at all (as <c>ScanActiveChunksTransient</c> already treats it).</description></item>
    ///   <item><description><b>data</b> — where this slot's component bytes live: whichever segment matches the slot's storage mode.</description></item>
    /// </list>
    /// For a SingleVersion / Versioned slot all three collapse onto the cluster segment, which is why this needed only one accessor before.
    /// </remarks>
    /// <param name="clusterState">The archetype whose parked index key-changes are being replayed.</param>
    /// <param name="engineState">Resolves a component slot to its table, for the view-registry notifications the drain emits.</param>
    /// <param name="changeSet">Threaded into the index accessor for a persisted store; <c>null</c> for a Transient one, which logs nothing.</param>
    /// <param name="clusterAccessor">
    /// The caller's OPEN accessor on <see cref="ArchetypeClusterState.ClusterSegment"/>, or <c>default</c> when the archetype is pure-Transient (the
    /// <c>ClusterSegment == null</c> branch of <c>PrepareArchetypeFenceCore</c>), where no cluster-segment address is ever taken.
    /// </param>
    /// <remarks>
    /// <para><b>#882 — threaded in rather than rented here, and the reason is the page window.</b> Prep already holds an accessor on this very segment for
    /// its occupancy mask and its crossing test. An accessor is a self-contained ~430-byte struct with its OWN 32-slot page window and its own clock hand —
    /// two of them share nothing — so renting a second one here made both re-resolve the same pages and each pay
    /// <c>IncrementSlotRefCount</c>/<c>DecrementSlotRefCount</c> on the same shared <c>PageInfo</c> cache lines. With
    /// <see cref="RecomputeClusterZoneMaps"/> renting a third, one Prep opened three windows onto one segment and let them evict each other's entries.</para>
    /// <para>Measured before the change: the shadow drain was <b>43 % of Prep</b> at the 25 % reference point of the #872 matrix, and 47 % under stress —
    /// almost none of it B+Tree work.</para>
    /// </remarks>
    private int ProcessClusterShadowEntries(ArchetypeClusterState clusterState, ArchetypeEngineState engineState, ChangeSet changeSet,
        ref ChunkAccessor<PersistentStore> clusterAccessor)
        => ProcessClusterShadowEntriesRange(clusterState, engineState, changeSet, ref clusterAccessor, 0, int.MaxValue, true);

    /// <inheritdoc cref="ProcessClusterShadowEntries"/>
    // firstWord: First cluster chunk id this call owns (#886 lead D: a Prep slice drains only the entries of its own clusters).
    // wordCount: Width of the owned range, in clusters.
    // resetBuffers: Whether to reset each drained buffer and the shadow bitmap afterwards. A slice must not — other slices are still reading
    // the same buffers — so the tail does it once for all of them.
    private int ProcessClusterShadowEntriesRange(ArchetypeClusterState clusterState, ArchetypeEngineState engineState, ChangeSet changeSet,
        ref ChunkAccessor<PersistentStore> clusterAccessor, int firstWord, int wordCount, bool resetBuffers)
    {
        var totalShadowEntries = 0;
        var pureTransient = clusterState.ClusterSegment == null;

        if (HasPendingShadow(clusterState.IndexSlots))
        {
            Debug.Assert(!pureTransient, "a pure-Transient archetype cannot own PersistentStore-backed index slots");
            totalShadowEntries += DrainClusterShadowSlots(clusterState, engineState, clusterState.IndexSlots, ref clusterAccessor, ref clusterAccessor,
                changeSet, firstWord, wordCount, resetBuffers);
        }

        if (HasPendingShadow(clusterState.TransientIndexSlots))
        {
            var transientAccessor = clusterState.TransientSegment.CreateChunkAccessor();
            try
            {
                if (pureTransient)
                {
                    totalShadowEntries += DrainClusterShadowSlots(clusterState, engineState, clusterState.TransientIndexSlots, ref transientAccessor,
                        ref transientAccessor, changeSet, firstWord, wordCount, resetBuffers);
                }
                else
                {
                    totalShadowEntries += DrainClusterShadowSlots(clusterState, engineState, clusterState.TransientIndexSlots, ref clusterAccessor,
                        ref transientAccessor, changeSet, firstWord, wordCount, resetBuffers);
                }
            }
            finally
            {
                transientAccessor.Dispose();
            }
        }

        if (resetBuffers)
        {
            clusterState.ClusterShadowBitmap.Clear();
        }

        return totalShadowEntries;
    }

    /// <summary>True when any field in <paramref name="ixSlots"/> has captured shadow entries awaiting a drain. Null-safe: an archetype has a slot array per
    /// index home and may have only one of them.</summary>
    private static bool HasPendingShadow<TStore>(ClusterIndexSlot<TStore>[] ixSlots) where TStore : struct, IPageStore
    {
        if (ixSlots == null)
        {
            return false;
        }

        for (var s = 0; s < ixSlots.Length; s++)
        {
            for (var f = 0; f < ixSlots[s].Fields.Length; f++)
            {
                if (ixSlots[s].ShadowBuffers[f].Count > 0)
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>Opens an accessor over an index segment, threading the caller's <see cref="ChangeSet"/> only for a persisted store.</summary>
    /// <remarks>
    /// A Transient segment has nothing to log or checkpoint, and the tick-fence shadow pass makes the same distinction by hand in its two branches.
    /// Resolved by type test rather than by an overload because the caller is generic over the store; the test is a JIT-time constant per instantiation.
    /// </remarks>
    private static ChunkAccessor<TStore> CreateIndexAccessor<TStore>(ChunkBasedSegment<TStore> segment, ChangeSet changeSet) where TStore : struct, IPageStore
        => typeof(TStore) == typeof(PersistentStore) ? segment.CreateChunkAccessor(changeSet) : segment.CreateChunkAccessor();

    /// <summary>Drains one index home's shadow buffers. See <see cref="ProcessClusterShadowEntries"/> for what each of the three stores addresses.</summary>
    private unsafe int DrainClusterShadowSlots<TIdx, TPrimary, TData>(ArchetypeClusterState clusterState, ArchetypeEngineState engineState,
        ClusterIndexSlot<TIdx>[] ixSlots, ref ChunkAccessor<TPrimary> primaryAccessor, ref ChunkAccessor<TData> dataAccessor, ChangeSet changeSet,
        int firstWord, int wordCount, bool resetBuffers)
        where TIdx : struct, IPageStore
        where TPrimary : struct, IPageStore
        where TData : struct, IPageStore
    {
        var totalShadowEntries = 0;

        // One shared write per drain instead of one per shadow entry per field (review M4). The fence drains archetypes in parallel, so this counter is a
        // store other cores may be watching; N of them bought nothing over one.
        var mutations = 0;
            // ── Nothing wrote this component, so no key in it moved ────────────────────────────────────────────────
            //
            // Capture is per ENTITY, not per field: ShadowClusterIndexedFields runs on the first write to an entity and shadows EVERY indexed field of EVERY
            // fence-maintained slot, because at that moment it cannot know which components the transaction is about to touch. The drain can know. A
            // component absent from FenceWrittenSlots was not written by anyone this tick, so every one of its shadow entries is guaranteed to compare equal
            // — and reaching that verdict costs a page-window probe and a key read each.
            //
            // Measured: the partitioning benchmark's archetype pairs a moving `Bounds` with an indexed `Tag` that nothing ever writes, which is the ordinary
            // shape of a component, not a contrivance. At 100 % moving the drain walked 64 000 entries per tick to prove Tag had not changed — 22 % of the
            // entire fence.
            //
            // Gated on SlotReleasesThisTick as well, and that second term is what makes this exact rather than merely plausible. The loop below is not only
            // a compare: its `occupancy == 0` branch is the destroy-side index REMOVAL for fence-maintained slots. An entity written for component T and then
            // destroyed needs its indexed component S taken out of the tree, and S is precisely what the written-slot test would skip. With no releases at all
            // this tick that case cannot exist. With releases, the drain runs in full, exactly as before.
            //
            // `writtenSlots != 0` is the third term and it is a FAIL-SAFE, not an optimisation. Zero is ambiguous: it means "nothing was written" on the
            // paths that maintain WrittenSlotUnion, and "this path does not maintain it" everywhere else — and a pure-Transient archetype is the second kind.
            // Its writes never reach the SetDirty overload that records a component slot, so the union stays zero while the shadow buffers fill, and a gate
            // that trusted the zero skipped every drain and left the tree on the pre-mutation key
            // (ClusterPureTransientIndexTests.Mutate_MovesTheKeyInTheTreeAtTheFence, caught exactly this). A non-empty buffer with an empty union is a
            // contradiction, and the honest response to a contradiction is to do the work.
            var writtenSlots = clusterState.FenceWrittenSlots;
            var skipUnwritten = writtenSlots != 0 && Volatile.Read(ref clusterState.SlotReleasesThisTick) == 0;

            for (var s = 0; s < ixSlots.Length; s++)
            {
                ref var ixSlot = ref ixSlots[s];
                var slotUnwritten = skipUnwritten && (writtenSlots & (1 << ixSlot.Slot)) == 0;

                for (var f = 0; f < ixSlot.Fields.Length; f++)
                {
                    var buffer = ixSlot.ShadowBuffers[f];
                    var count = buffer.Count;
                    if (count == 0)
                    {
                        continue;
                    }

                    if (slotUnwritten)
                    {
                        // Reset, not skip: the entries describe THIS tick and must not be seen by the next one, whose FenceWrittenSlots may well include
                        // this component. Leaving them queued would replay stale old-keys against a tree that has since moved.
                        if (resetBuffers)
                        {
                            buffer.Reset();
                        }

                        continue;
                    }

                    ref var field = ref ixSlot.Fields[f];

                    // Walk the buffer in ascending cluster order rather than in the order user code happened to write the entities (#882). The two
                    // GetChunkAddress calls below are the drain's whole cost on the common path — the one where the indexed field did not actually change —
                    // and in append order they miss the accessor's 32-page window on nearly every entry. See BuildShadowDrainOrder for the measurement.
                    //
                    // One behaviour this reorders, deliberately: with two entities colliding on a UNIQUE key inside one drain,
                    // Transaction.RejectUniqueIndexCollision below rejects whichever it reaches SECOND, so which of the two is rejected now follows cluster
                    // order instead of write order. Both are legal and neither is promised by any rule — but NOTHING PINS IT: no fixture in this repo
                    // declares a unique cluster index and drives two colliding writes through one drain, so this paragraph is the only record that the
                    // choice moved.
                    // The atomic path orders the buffer here, into the archetype's scratch. A slice reads the plan the head built once for the whole
                    // buffer and takes its own clusters' contiguous range of it (#886 lead D) — the field's ChunkId column is read once per tick, not
                    // three times per slice.
                    var order = resetBuffers 
                        ? clusterState.BuildShadowDrainOrder(buffer, count) : buffer.DrainOrderForClusters(firstWord, firstWord + wordCount);
                    var included = order.Length;
                    totalShadowEntries += included;
                    var idxAccessor = CreateIndexAccessor(field.Index.Segment, changeSet);

                    try
                    {
                        for (var k = 0; k < included; k++)
                        {
                            ref var entry = ref buffer[order[k]];
                            var clusterChunkId = entry.ChunkId >> 6;   // entityIndex → chunkId
                            var slotIndex = entry.ChunkId & 0x3F;      // entityIndex → slot

                            // Check occupancy (entity may have been destroyed this tick). The occupancy word and the elementId tail come from the PRIMARY
                            // segment; this slot's component bytes come from the DATA one. They are the same chunk of the same segment for a SingleVersion /
                            // Versioned slot, and different segments for a Transient slot in a mixed archetype (#655).
                            var primaryBase = primaryAccessor.GetChunkAddress(clusterChunkId);
                            var dataBase = dataAccessor.GetChunkAddress(clusterChunkId);
                            var occupancy = *(ulong*)primaryBase;
                            if ((occupancy & (1UL << slotIndex)) == 0)
                            {
                                // Entity destroyed — remove old index entry using shadow value
                                mutations++;   // (#665)
                                var destroyOldKey = entry.OldKey;
                                if (field.AllowMultiple)
                                {
                                    // Remove only THIS entity's (key, clusterLocation) element — Remove(key) would drop the whole buffer and
                                    // take every sibling sharing the value with it (issue #659; same rule as the destroy and commit paths).
                                    // ClearSlotMetadata zeroes occupancy, EnabledBits and the EntityIds slot but leaves the elementId tail
                                    // intact, so it is still readable here even though the slot is already released.
                                    var destroyElementId = *(int*)(primaryBase + clusterState.Layout.IndexElementIdOffset(field.MultiFieldIndex, slotIndex));
                                    field.Index.RemoveValue(&destroyOldKey, destroyElementId, entry.ChunkId, ref idxAccessor);
                                }
                                else
                                {
                                    field.Index.Remove(&destroyOldKey, out _, ref idxAccessor);
                                }

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
                                    QueryPathProbe.PrePublishAppendHook?.Invoke();
                                    reg.DeltaBuffer.TryAppend(entry.EntityPK, entry.OldKey, default, 0, delFlags, reg.ComponentTag);
                                }

                                continue;
                            }

                            // Read current (post-mutation) field value from cluster SoA
                            var compSize = clusterState.Layout.ComponentSize(ixSlot.Slot);
                            var compBase = dataBase + clusterState.Layout.ComponentOffset(ixSlot.Slot) + slotIndex * compSize;
                            var fieldPtr = compBase + field.FieldOffset;
                            var oldKey = entry.OldKey;
                            var newKey = KeyBytes8.FromPointer(fieldPtr, field.FieldSize);

                            if (oldKey.RawValue == newKey.RawValue)
                            {
                                continue; // Field didn't actually change
                            }

                            mutations++;   // past the guard, so this is real tree work (#665)

                            // Update per-archetype B+Tree: remove old key, insert new key, same ClusterLocation value
                            var clusterLocation = entry.ChunkId; // entityIndex = clusterLocation
                            if (field.AllowMultiple)
                            {
                                // A multi-value leaf holds a VSBS buffer id, not an entity location: a plain Move would overwrite it with the
                                // raw clusterLocation and every entity at that key would vanish from the index (issue #659). MoveValue moves
                                // just this entity's element and returns its new id, which goes back into the cluster's elementId tail.
                                // Fetched forWrite only on this branch; the mutation that triggered shadowing already dirtied the page.
                                var writableBase = primaryAccessor.GetChunkAddress(clusterChunkId, true);
                                var elementIdPtr = (int*)(writableBase + clusterState.Layout.IndexElementIdOffset(field.MultiFieldIndex, slotIndex));
                                *elementIdPtr = field.Index.MoveValue(&oldKey, fieldPtr, *elementIdPtr, clusterLocation, ref idxAccessor, out _, out _);
                            }
                            else
                            {
                                // The SV mutation path. A SingleVersion component has no revision chain, so its index maintenance happens HERE, at the
                                // tick-fence shadow drain, rather than on the commit path — which is why guarding only the commit sites left the collision
                                // reachable (#675).
                                //
                                // #886 lead C: no pre-read. Every arm of BTree.Move re-finds newKey under the leaf's write latch and returns false, with the
                                // tree untouched, when another entry holds it — a verdict that stays true when two workers drain two clusters at once,
                                // which a TryGet-then-Move pair does not: both can pass the read and both then insert. false also means "oldKey is not in
                                // the tree", which was silent before and stays silent; the cold-path check below tells the two apart and raises #675's
                                // message for the first. The pessimistic arm is the one whose existence check is not latched, and it reports the same
                                // collision by throwing after having removed the old key — the state the pre-read's throw also left, since the data already
                                // holds the colliding value either way.
                                bool moved;
                                try
                                {
                                    moved = field.Index.Move(&oldKey, fieldPtr, clusterLocation, ref idxAccessor);
                                }
                                catch (UniqueConstraintViolationException)
                                {
                                    moved = false;
                                }

                                if (!moved)
                                {
                                    Transaction.RejectUniqueIndexCollision(ref field, fieldPtr, clusterLocation, ref idxAccessor);
                                }
                            }

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
                                    QueryPathProbe.PrePublishAppendHook?.Invoke();
                                    reg.DeltaBuffer.TryAppend(entry.EntityPK, oldKey, newKey, 0, flags, reg.ComponentTag);
                                }
                            }
                        }
                    }
                    finally
                    {
                        idxAccessor.Dispose();
                    }

                    if (resetBuffers)
                    {
                        buffer.Reset();
                    }
                }
            }

        // Through the padded field, atomically: W slices of one archetype fold into it at once (#886 lead D). The statistics worker only compares it
        // to a threshold.
        Interlocked.Add(ref clusterState._mutationsSinceRebuild.Value, mutations);
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
                    QueryPathProbe.PrePublishAppendHook?.Invoke();
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
                QueryPathProbe.PrePublishAppendHook?.Invoke();
                reg.DeltaBuffer.TryAppend(entry.EntityPK, oldKey, newKey, 0, flags, reg.ComponentTag);
            }
        }
    }
}
