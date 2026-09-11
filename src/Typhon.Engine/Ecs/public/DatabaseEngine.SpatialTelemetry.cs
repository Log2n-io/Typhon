using System;
using System.Diagnostics;
using System.Threading;
using JetBrains.Annotations;

namespace Typhon.Engine;

public partial class DatabaseEngine
{
    /// <summary>
    /// Wall-clock milliseconds the most recent <see cref="InitializeArchetypes"/> spent rebuilding cluster-to-cell mappings.
    /// </summary>
    private double _openCellStateRebuildMs;

    /// <summary>Wall-clock milliseconds the most recent <see cref="InitializeArchetypes"/> spent rebuilding per-cluster AABBs and the per-cell index.</summary>
    private double _openClusterAabbRebuildMs;

    /// <summary>
    /// Milliseconds spent reconstructing the cluster-to-cell mapping at open (<c>RebuildCellState</c>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The cell layer is transient — nothing about the grid is persisted, so every open reconstructs it from entity positions in an <c>O(entities)</c> sweep.
    /// This figure and <see cref="OpenClusterAabbRebuildMs"/> are what decide whether that reconstruction stays affordable at target entity counts or the grid
    /// must be persisted. Zero on a database with no cluster-eligible archetypes, or when every archetype opened with no active clusters.
    /// </para>
    /// <para>
    /// Describes the <b>most recent</b> <see cref="InitializeArchetypes"/> call, not the sum of all of them: a repeat call reallocates the per-archetype state
    /// it measures, so every other counter restarts with it and a lifetime sum here would be the one figure that did not.
    /// </para>
    /// </remarks>
    [PublicAPI]
    public double OpenCellStateRebuildMs => _openCellStateRebuildMs;

    /// <summary>
    /// Milliseconds spent recomputing per-cluster AABBs and the per-cell spatial index at open (<c>RebuildClusterAabbs</c>).
    /// </summary>
    /// <remarks>See <see cref="OpenCellStateRebuildMs"/> — the two are halves of the same startup sweep and are read together.</remarks>
    [PublicAPI]
    public double OpenClusterAabbRebuildMs => _openClusterAabbRebuildMs;

    /// <summary>
    /// How long the previous tick's partitioning fence took, in milliseconds — <b>the span, not summed CPU</b>. The interval runs from the start of Prep's
    /// <c>Prepare</c> to the end of the last phase that dispatched a chunk, so it covers the six phase spans plus the scheduler's gaps between them.
    /// </summary>
    /// <remarks>
    /// <para><b>This is the number that answers "what did the fence cost the frame".</b> The sum of the six phase spans is what the partitioning COSTS;
    /// this is what the host WAITS, and a frame budget is spent in the second. Read <see cref="SpatialMigrationTelemetry.MigrationTotalMs"/> against it:
    /// that one is CPU-milliseconds summed across workers, so their ratio is roughly the parallelism the migration work actually achieved. Presenting the
    /// summed figure as the fence's duration is the error that made an 8 ms budget buy one repair unit.</para>
    /// <para><b>Engine-wide, deliberately not on <see cref="SpatialMigrationTelemetry"/>.</b> One fence serves every archetype, so hanging it off a
    /// per-archetype snapshot would invite <see cref="GetSpatialTelemetryTotal"/> to sum one tick's fence once per archetype.</para>
    /// <para><b>Zero on a host that drives <see cref="WriteTickFence(long, ChangeSet)"/> itself</b> rather than running the parallel fence: the phase-exec systems that
    /// time it never run, so there is no span to report. That zero means "the parallel fence did not drive this tick", not "the fence was free".</para>
    /// </remarks>
    [PublicAPI]
    public double LastFenceSpanMs => _lastFenceSpanTicks * 1000d / Stopwatch.Frequency;

    /// <summary>
    /// How long the previous tick's partitioning fence blocked the host, in milliseconds — <b>the whole fence, measured on the tick thread</b>, from the
    /// moment the fence window opens to the moment it closes. This is the interruption a host feels: no user system runs inside it at any worker count.
    /// </summary>
    /// <remarks>
    /// <para><b>Read this, not <see cref="LastFenceSpanMs"/>, to answer "how long was user code stopped".</b> The span starts at Prep's <c>Prepare</c>, so it
    /// omits the serial prep that runs first on the tick thread — the fence-context reset, the dormancy drain and <c>ProcessTableFence</c> over every
    /// component table. That prep is single-threaded by construction, so no worker count shrinks it, and a sweep across worker counts that reads only the
    /// span reports a speed-up the host never receives. <c>LastFenceStallMs - LastFenceSpanMs</c> is that serial remainder, and it is Amdahl's fraction for
    /// the fence.</para>
    /// <para><b>Engine-wide, for the same reason as the span:</b> one fence serves every archetype.</para>
    /// <para><b>Zero on a host that drives <see cref="WriteTickFence(long, ChangeSet)"/> itself</b>, which is the same zero-means-zero discipline as the
    /// span — such a host is holding its own stopwatch around the call and does not need this one.</para>
    /// </remarks>
    [PublicAPI]
    public double LastFenceStallMs => _lastFenceStallTicks * 1000d / Stopwatch.Frequency;

    /// <summary>
    /// The spatial grid's occupancy and memory, or an all-zero snapshot when no grid is configured (#872 step 8, AC-8.5 and AC-8.7).
    /// </summary>
    /// <remarks>
    /// <para><b>What each figure answers.</b> <c>BlockCount</c> and <c>OccupiedCellCount</c> against <c>BlockCellCapacity</c> give <c>IntraBlockFill</c>,
    /// which is Q3's measurement: a low fill argues for replacing the dense per-block <c>int[]</c> with a bitmask plus compaction (P2), a high one says the
    /// dense array is right. <c>ResidentBytes</c> against <c>DenseEquivalentBytes</c> is C2's argument made observable — the dense predecessor allocated a
    /// 64-byte descriptor for every cell the world bounds implied, occupied or not.</para>
    /// <para><b>It is also the guard on the one discipline the sparse grid depends on.</b> A read path that resolves a cell WITH creation — a query
    /// broadphase, a tier sweep — materialises a cell per coordinate it touches. Every answer stays correct and nothing fails; the only symptom is
    /// <c>ResidentBytes</c> climbing toward <c>DenseEquivalentBytes</c>. See rule <c>VG-02</c>.</para>
    /// <para>Read without a lock, so a snapshot taken during a tick can mix values from either side of a cell creation. These are diagnostic counters.</para>
    /// </remarks>
    [PublicAPI]
    public SpatialGridOccupancy GetSpatialGridOccupancy()
    {
        var grid = _spatialGrid;
        if (grid == null)
        {
            return default;
        }

        var (bx, by, bz) = grid.BlockDimensions;
        return new SpatialGridOccupancy
        {
            BlockCount = grid.BlockCount,
            OccupiedCellCount = grid.CellCount,
            BlockCellCapacity = grid.BlockCellCapacity,
            BlockDimX = bx,
            BlockDimY = by,
            BlockDimZ = bz,
            IntraBlockFill = grid.IntraBlockFill,
            ResidentBytes = grid.ResidentBytes,
            DenseEquivalentBytes = grid.DenseEquivalentBytes,
        };
    }

    /// <summary>
    /// Reads one archetype's spatial-partitioning counters. Allocation-free; never throws.
    /// </summary>
    /// <param name="archetypeId">The archetype's runtime id. Out of range, or an archetype with no cluster state, yields an all-zero snapshot.</param>
    /// <returns>A snapshot of the archetype's counters. See <see cref="SpatialMigrationTelemetry"/> for which members describe the last tick and which are
    /// cumulative.</returns>
    /// <remarks>
    /// The snapshot is taken field by field without a lock, so a read racing the tick fence can mix values from either side of it. That is deliberate: these
    /// are diagnostic counters, and serializing a reader against the fence would cost more than the inconsistency is worth. Call it from the tick loop —
    /// after the fence, before the next tick — for a coherent view.
    /// </remarks>
    [PublicAPI]
    public SpatialMigrationTelemetry GetSpatialTelemetry(int archetypeId)
    {
        var states = _archetypeStates;
        if (states == null || archetypeId < 0 || archetypeId >= states.Length)
        {
            return default;
        }

        var clusterState = states[archetypeId]?.ClusterState;
        if (clusterState == null)
        {
            return default;
        }

        // Read once rather than per-member: re-reading the count for each of the two means could divide two different sums by two different denominators.
        // It does NOT make the trio atomic — the two sums are plain loads taken afterwards, so a concurrent FoldTightnessSample landing between them still
        // yields a sum covering samples this denominator excludes, and the mean reads slightly high. That is the same torn-across-a-fence-boundary looseness
        // the accessor's own remarks already claim for every other member; serialising against the fence would cost more than the inconsistency is worth.
        var samples = Volatile.Read(ref clusterState.LastTickTightnessSamples);

        return new SpatialMigrationTelemetry(
            clusterState.LastTickMigrationCount,
            clusterState.LastTickHysteresisAbsorbedCount,
            clusterState.LastTickMigrationExecuteMs,
            clusterState.LastTickClustersScanned,
            clusterState.LastTickDriftersDetected,
            clusterState.LastTickDriftAbsorbedCount,
            clusterState.LastTickReclusterBudgetUsedMs,
            clusterState.ActiveClusterCount,
            clusterState.TotalMigrationCount,
            clusterState.TotalHysteresisAbsorbedCount,
            clusterState.LastTickRepairedEntityCount,
            clusterState.LastTickRepairUnitCount,
            clusterState.LastTickRepairUnitsRefused)
        {
            // #872 step 11's members ride an object initialiser rather than the constructor: at thirteen positional arguments the call was already at the
            // limit of what a reader can check, and six more would make a mis-ordered pair of ints a silent telemetry bug rather than a compile error.
            SlotsScanned = clusterState.LastTickSlotsScanned,
            MigrationTotalMs = clusterState.LastTickMigrationTotalMs,
            MigrationSliceCount = clusterState.LastTickMigrationSliceCount,
            ZoneMapBatchOpens = clusterState.LastTickZoneMapBatchOpens,
            MigrationPrologueMs = TicksToMs(clusterState.LastTickMigrationPrologueTicks),
            MigrationEpilogueMs = TicksToMs(clusterState.LastTickMigrationEpilogueTicks),
            CrossingsExecuted = clusterState.LastTickCrossingsExecuted,
            RelocationsExecuted = clusterState.LastTickRelocationsExecuted,
            RepairsExecuted = clusterState.LastTickRepairsExecuted,
            FinalizeLockAcquisitions = clusterState.FinalizeLockAcquisitions,
            RelocationsThrottled = clusterState.LastTickRelocationsThrottled,
            RelocationsSuperseded = clusterState.LastTickRelocationsSuperseded,
            PrepSnapshotMs = TicksToMs(clusterState.PrepSnapshotTicks),
            PrepMaskMs = TicksToMs(clusterState.PrepMaskTicks),
            PrepShadowMs = TicksToMs(clusterState.PrepShadowTicks),
            PrepZoneMapMs = TicksToMs(clusterState.PrepZoneMapTicks),
            PrepDetectMs = TicksToMs(clusterState.PrepDetectTicks),
            PrepThrottleMs = TicksToMs(clusterState.PrepThrottleTicks),
            PrepPlanMs = TicksToMs(clusterState.PrepPlanTicks),
            PrepSortMs = TicksToMs(clusterState.PrepSortTicks),
            PrepPreSizeMs = TicksToMs(clusterState.PrepPreSizeTicks),
            PrepDirtyClusters = clusterState.PrepDirtyClusters,
            DriftersUnplaced = clusterState.LastTickDriftersUnplaced,
            RepairValveFires = clusterState.LastTickRepairValveFires,
            RepairQueueDepth = clusterState.RepairQueue?.Count ?? 0,
            RepairQueueEvicted = clusterState.RepairQueue?.TotalEvicted ?? 0L,
            RepairQueueMaintenanceMs = QueueMaintenanceMs(clusterState),
            MeasuredNsPerEntity = clusterState.LastTickMeasuredNsPerEntity,
            DriftGatedClusters = clusterState.LastTickDriftGatedClusters,
            DriftSuppressedByDensity = clusterState.LastTickDriftSuppressedByDensity,
            DriftersUnplacedNoCandidate = clusterState.LastTickDriftersUnplacedNoCandidate,
            DriftersSpilled = clusterState.LastTickDriftersSpilled,
            PinsRejected = clusterState.LastTickPinsRejected,
            RelocationsAdmitted = clusterState.LastTickRelocationsAdmitted,
            CrossingsQueued = clusterState.LastTickCrossingsQueued,
            JumpCrossings = clusterState.LastTickJumpCrossings,
            ClampedDestinations = clusterState.LastTickClampedDestinations,
            LargestArrivalRun = clusterState.LastTickLargestArrivalRun,
            ArrivalCellsTouched = clusterState.LastTickArrivalCellsTouched,
            RelocationSpendNs = clusterState.LastTickRelocationSpendNs,
            RepairBudgetStarvedNs = clusterState.LastTickRepairBudgetStarvedNs,
            MaxClusterOverhang = Volatile.Read(ref clusterState.MaxClusterOverhang),
            CellTreePromotions = clusterState.LastTickCellTreePromotions,
            CellTreeDemotions = clusterState.LastTickCellTreeDemotions,
            TightnessSampleCount = samples,
            MeanClusterExtentRatio = samples > 0 ? clusterState.LastTickTightnessExtentSum / samples : 0d,
            MeanPackingBound = samples > 0 ? clusterState.LastTickTightnessBoundSum / samples : 0d,
        };
    }

    /// <summary>One archetype's queue-maintenance time in milliseconds, or zero when it has no queue yet.</summary>
    /// <summary>Stopwatch ticks to milliseconds. The sub-spans are accumulated as raw timestamps to keep the bracket to one subtraction.</summary>
    private static double TicksToMs(long ticks) => ticks * 1000d / System.Diagnostics.Stopwatch.Frequency;

    private static double QueueMaintenanceMs(ArchetypeClusterState clusterState) =>
        clusterState.RepairQueue == null ? 0d : clusterState.RepairQueue.LastTickMaintenanceTicks * 1000d / Stopwatch.Frequency;

    /// <summary>
    /// Sums <see cref="GetSpatialTelemetry"/> across every archetype in this engine. Allocation-free; never throws.
    /// </summary>
    /// <returns>An engine-wide snapshot. <see cref="SpatialMigrationTelemetry.MigrationExecuteMs"/> is a sum across archetypes, not a wall-clock duration —
    /// parallel fence workers overlap, so it can exceed the tick's elapsed time.</returns>
    [PublicAPI]
    public SpatialMigrationTelemetry GetSpatialTelemetryTotal()
    {
        var states = _archetypeStates;
        if (states == null)
        {
            return default;
        }

        var migrations = 0;
        var absorbed = 0;
        var executeMs = 0d;
        var scanned = 0;
        var slotsScanned = 0;
        var drifters = 0;
        var driftAbsorbed = 0;
        var budgetMs = 0d;
        var activeClusters = 0;
        var totalMigrations = 0L;
        var totalAbsorbed = 0L;
        var repairedEntities = 0;
        var repairUnits = 0;
        var repairRefused = 0;
        var migrationTotalMs = 0d;
        var migrationSlices = 0;
        var zoneMapBatchOpens = 0;
        var prologueTicks = 0L;
        var epilogueTicks = 0L;
        var crossingsExecuted = 0;
        var relocationsExecuted = 0;
        var repairsExecuted = 0;
        var finalizeLockAcquisitions = 0L;
        var relocationsThrottled = 0;
        var relocationsSuperseded = 0;
        var driftersUnplaced = 0;
        var valveFires = 0;
        var queueDepth = 0;
        var queueEvicted = 0L;
        var queueMaintenanceMs = 0d;
        var measuredNsPerEntity = 0d;
        var measuredSamples = 0;
        var driftGated = 0;
        var driftSuppressedByDensity = 0;
        var unplacedNoCandidate = 0;
        var spilled = 0;
        var pinsRejected = 0;
        var relocationsAdmitted = 0;
        var crossingsQueued = 0;
        var jumpCrossings = 0;
        var clampedDestinations = 0;
        var largestArrivalRun = 0;
        var arrivalCellsTouched = 0;
        var relocationSpendNs = 0d;
        var repairStarvedNs = 0d;
        var maxOverhang = 0f;
        var treePromotions = 0;
        var treeDemotions = 0;
        var tightnessSamples = 0;
        var tightnessExtentSum = 0d;
        var tightnessBoundSum = 0d;

        for (var i = 0; i < states.Length; i++)
        {
            var clusterState = states[i]?.ClusterState;
            if (clusterState == null)
            {
                continue;
            }

            migrations += clusterState.LastTickMigrationCount;
            absorbed += clusterState.LastTickHysteresisAbsorbedCount;
            executeMs += clusterState.LastTickMigrationExecuteMs;
            scanned += clusterState.LastTickClustersScanned;
            slotsScanned += clusterState.LastTickSlotsScanned;
            drifters += clusterState.LastTickDriftersDetected;
            driftAbsorbed += clusterState.LastTickDriftAbsorbedCount;
            budgetMs += clusterState.LastTickReclusterBudgetUsedMs;
            activeClusters += clusterState.ActiveClusterCount;
            totalMigrations += clusterState.TotalMigrationCount;
            totalAbsorbed += clusterState.TotalHysteresisAbsorbedCount;
            repairedEntities += clusterState.LastTickRepairedEntityCount;
            repairUnits += clusterState.LastTickRepairUnitCount;
            repairRefused += clusterState.LastTickRepairUnitsRefused;
            migrationTotalMs += clusterState.LastTickMigrationTotalMs;
            // Summed like every other extensive quantity here. The slice count is per archetype, so the engine-wide figure is how many Migrate spans the
            // tick summed in total — which is the right denominator for the engine-wide MigrationExecuteMs beside it.
            migrationSlices += clusterState.LastTickMigrationSliceCount;
            zoneMapBatchOpens += clusterState.LastTickZoneMapBatchOpens;
            prologueTicks += clusterState.LastTickMigrationPrologueTicks;
            epilogueTicks += clusterState.LastTickMigrationEpilogueTicks;
            crossingsExecuted += clusterState.LastTickCrossingsExecuted;
            relocationsExecuted += clusterState.LastTickRelocationsExecuted;
            repairsExecuted += clusterState.LastTickRepairsExecuted;
            finalizeLockAcquisitions += clusterState.FinalizeLockAcquisitions;
            relocationsThrottled += clusterState.LastTickRelocationsThrottled;
            relocationsSuperseded += clusterState.LastTickRelocationsSuperseded;
            driftersUnplaced += clusterState.LastTickDriftersUnplaced;
            valveFires += clusterState.LastTickRepairValveFires;
            queueDepth += clusterState.RepairQueue?.Count ?? 0;
            queueEvicted += clusterState.RepairQueue?.TotalEvicted ?? 0L;
            queueMaintenanceMs += QueueMaintenanceMs(clusterState);
            driftGated += clusterState.LastTickDriftGatedClusters;
            driftSuppressedByDensity += clusterState.LastTickDriftSuppressedByDensity;
            unplacedNoCandidate += clusterState.LastTickDriftersUnplacedNoCandidate;
            spilled += clusterState.LastTickDriftersSpilled;
            pinsRejected += clusterState.LastTickPinsRejected;
            relocationsAdmitted += clusterState.LastTickRelocationsAdmitted;
            crossingsQueued += clusterState.LastTickCrossingsQueued;
            jumpCrossings += clusterState.LastTickJumpCrossings;
            clampedDestinations += clusterState.LastTickClampedDestinations;
            // MAXED, not summed: the largest arrival is a property of one cell, and two archetypes' runs into different cells do not add.
            largestArrivalRun = Math.Max(largestArrivalRun, clusterState.LastTickLargestArrivalRun);
            arrivalCellsTouched += clusterState.LastTickArrivalCellsTouched;
            relocationSpendNs += clusterState.LastTickRelocationSpendNs;
            repairStarvedNs += clusterState.LastTickRepairBudgetStarvedNs;
            treePromotions += clusterState.LastTickCellTreePromotions;
            treeDemotions += clusterState.LastTickCellTreeDemotions;

            // MAXED, not summed — see SpatialMigrationTelemetry.MaxClusterOverhang. It is a bound every kNN ring widens by, and the engine-wide bound is the
            // largest any archetype has proved, not the sum of what each proved separately.
            var overhang = Volatile.Read(ref clusterState.MaxClusterOverhang);
            if (overhang > maxOverhang)
            {
                maxOverhang = overhang;
            }

            // Summed as NUMERATORS, divided once at the end: a mean of the per-archetype means would weight a quiet archetype that scanned one cluster
            // equally with a busy one that scanned ten thousand. Read the sample count once for the same reason the per-archetype accessor does.
            var samples = Volatile.Read(ref clusterState.LastTickTightnessSamples);
            if (samples > 0)
            {
                tightnessSamples += samples;
                tightnessExtentSum += clusterState.LastTickTightnessExtentSum;
                tightnessBoundSum += clusterState.LastTickTightnessBoundSum;
            }

            // AVERAGED, not summed, and it is the one member here that is. Every other value is an extensive quantity — more archetypes, more of it — but
            // a cost per entity is intensive, and summing it would report an engine with four archetypes as four times as expensive per entity as each of
            // them is. Only archetypes that actually produced an estimate contribute, so a quiet one does not drag the mean toward zero.
            if (clusterState.LastTickMeasuredNsPerEntity > 0d)
            {
                measuredNsPerEntity += clusterState.LastTickMeasuredNsPerEntity;
                measuredSamples++;
            }
        }

        return new SpatialMigrationTelemetry(migrations, absorbed, executeMs, scanned, drifters, driftAbsorbed, budgetMs, activeClusters, totalMigrations,
            totalAbsorbed, repairedEntities, repairUnits, repairRefused)
        {
            SlotsScanned = slotsScanned,
            MigrationTotalMs = migrationTotalMs,
            MigrationSliceCount = migrationSlices,
            ZoneMapBatchOpens = zoneMapBatchOpens,
            MigrationPrologueMs = TicksToMs(prologueTicks),
            MigrationEpilogueMs = TicksToMs(epilogueTicks),
            CrossingsExecuted = crossingsExecuted,
            RelocationsExecuted = relocationsExecuted,
            RepairsExecuted = repairsExecuted,
            FinalizeLockAcquisitions = finalizeLockAcquisitions,
            RelocationsThrottled = relocationsThrottled,
            RelocationsSuperseded = relocationsSuperseded,
            DriftersUnplaced = driftersUnplaced,
            RepairValveFires = valveFires,
            RepairQueueDepth = queueDepth,
            RepairQueueEvicted = queueEvicted,
            RepairQueueMaintenanceMs = queueMaintenanceMs,
            MeasuredNsPerEntity = measuredSamples > 0 ? measuredNsPerEntity / measuredSamples : 0d,
            DriftGatedClusters = driftGated,
            DriftSuppressedByDensity = driftSuppressedByDensity,
            DriftersUnplacedNoCandidate = unplacedNoCandidate,
            DriftersSpilled = spilled,
            PinsRejected = pinsRejected,
            RelocationsAdmitted = relocationsAdmitted,
            CrossingsQueued = crossingsQueued,
            JumpCrossings = jumpCrossings,
            ClampedDestinations = clampedDestinations,
            LargestArrivalRun = largestArrivalRun,
            ArrivalCellsTouched = arrivalCellsTouched,
            RelocationSpendNs = relocationSpendNs,
            RepairBudgetStarvedNs = repairStarvedNs,
            MaxClusterOverhang = maxOverhang,
            CellTreePromotions = treePromotions,
            CellTreeDemotions = treeDemotions,
            TightnessSampleCount = tightnessSamples,
            MeanClusterExtentRatio = tightnessSamples > 0 ? tightnessExtentSum / tightnessSamples : 0d,
            MeanPackingBound = tightnessSamples > 0 ? tightnessBoundSum / tightnessSamples : 0d,
        };
    }
}
