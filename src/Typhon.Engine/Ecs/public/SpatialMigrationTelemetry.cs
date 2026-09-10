using JetBrains.Annotations;

namespace Typhon.Engine;

/// <summary>
/// A snapshot of one archetype's spatial-partitioning counters, or of the whole engine's when obtained from
/// <see cref="DatabaseEngine.GetSpatialTelemetryTotal"/>. Obtained from <see cref="DatabaseEngine.GetSpatialTelemetry"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Two clocks, deliberately.</b> The <c>...Count</c> / <c>...Ms</c> members describe the <b>most recently completed tick</b> and are reset at the top of
/// every tick fence; the <c>Total...</c> members only grow. A poll-based consumer (an OTel scrape every few seconds) that reads a per-tick value samples one
/// arbitrary tick out of hundreds — use the cumulative members and differentiate for a rate; use the per-tick members from inside a tick loop, where "this
/// tick" is exactly what you meant.
/// </para>
/// <para>
/// <b>Zero means zero, never "unknown".</b> An archetype with no cluster state, an out-of-range id and a tick in which nothing happened all report zero.
/// <see cref="ClustersScanned"/>, <see cref="DriftersDetected"/> and <see cref="DriftAbsorbedCount"/> gained their producers in step 10 of
/// <c>claude/design/Spatial/vdb-cell-grid-and-migration.md</c>; <see cref="ReclusterBudgetUsedMs"/>, <see cref="RepairedEntityCount"/>,
/// <see cref="RepairUnitCount"/> and <see cref="RepairUnitsRefused"/> in step 12.
/// </para>
/// <para>
/// <b>Cumulative members restart with the archetype's cluster state.</b> <see cref="DatabaseEngine.InitializeArchetypes"/> reallocates the per-archetype state
/// array, so a repeat call — rare, but explicitly tolerated — returns the totals to zero. They measure the life of the cluster state, not of the process.
/// </para>
/// <para>Reading is allocation-free and lock-free: every member is a plain field read of live engine state, torn only across a fence boundary.</para>
/// </remarks>
[PublicAPI]
public readonly struct SpatialMigrationTelemetry
{
    internal SpatialMigrationTelemetry(int migrationCount, int hysteresisAbsorbedCount, double migrationExecuteMs, int clustersScanned, int driftersDetected,
        int driftAbsorbedCount, double reclusterBudgetUsedMs, int activeClusterCount, long totalMigrations, long totalHysteresisAbsorbed,
        int repairedEntityCount, int repairUnitCount, int repairUnitsRefused)
    {
        RepairedEntityCount = repairedEntityCount;
        RepairUnitCount = repairUnitCount;
        RepairUnitsRefused = repairUnitsRefused;
        DriftAbsorbedCount = driftAbsorbedCount;
        MigrationCount = migrationCount;
        HysteresisAbsorbedCount = hysteresisAbsorbedCount;
        MigrationExecuteMs = migrationExecuteMs;
        ClustersScanned = clustersScanned;
        DriftersDetected = driftersDetected;
        ReclusterBudgetUsedMs = reclusterBudgetUsedMs;
        ActiveClusterCount = activeClusterCount;
        TotalMigrations = totalMigrations;
        TotalHysteresisAbsorbed = totalHysteresisAbsorbed;
    }

    /// <summary>Entities moved to a different cluster because they crossed a spatial cell boundary, during the most recently completed tick.</summary>
    public int MigrationCount { get; }

    /// <summary>
    /// Cell-boundary crossings that did <b>not</b> produce a migration during the most recently completed tick, because the entity was still inside the
    /// hysteresis margin. Read against <see cref="MigrationCount"/>: a high ratio means the margin is doing its job, a near-zero one means it is too narrow to
    /// absorb oscillation around the boundary.
    /// </summary>
    /// <remarks>
    /// <b>The unit differs by write path.</b> An archetype using the spatial write barrier (<c>SetSpatialBarrierOnly</c>) counts one per absorbed <i>write</i>,
    /// so an entity parked in the margin and written twice in a tick contributes two; every other archetype counts one per <i>slot</i> per tick, because its
    /// producer is a once-per-tick scan. The two agree on the overwhelmingly common workload of one spatial write per entity per tick, and diverge above that.
    /// Treat this as a rate signal for tuning, not an exact entity count.
    /// </remarks>
    public int HysteresisAbsorbedCount { get; }

    /// <summary>
    /// Wall-clock milliseconds spent executing migrations during the most recently completed tick, summed across every worker that took a slice.
    /// </summary>
    public double MigrationExecuteMs { get; }

    /// <summary>
    /// Clusters examined by the intra-cell drifter scan during the most recently completed tick.
    /// </summary>
    /// <remarks>
    /// Clusters that were WRITTEN this tick, not clusters that exist — a settled world scans nothing, which is the cheap half of the design's promise and the
    /// denominator that makes <see cref="DriftersDetected"/> mean anything.
    /// </remarks>
    public int ClustersScanned { get; }

    /// <summary>
    /// Entity slots the AABB refresh pass actually read during the most recently completed tick.
    /// </summary>
    /// <remarks>
    /// <para>The refresh's own cost, in the only unit that scales with the world: <see cref="ClustersScanned"/> is incremented after the pass has decided a
    /// cluster had something to say, so it cannot distinguish a pass that opened ten clusters from one that opened two thousand.</para>
    /// <para><b>What a healthy value looks like.</b> Roughly the occupied-slot count of the clusters that were WRITTEN this tick. If it tracks the whole
    /// population instead — 63 000 on a 64 000-entity world where 640 entities moved — the pass has lost its dirty gate, which is exactly the regression
    /// this counter was added to make visible.</para>
    /// </remarks>
    public int SlotsScanned { get; init; }

    /// <summary>
    /// Entities found outside their cluster's target region during the most recently completed tick — candidates for intra-cell relocation.
    /// </summary>
    /// <remarks>
    /// Counts DETECTION, not outcome. An entity is counted here the moment the target-region rule rejects it, whether or not placement then found a better
    /// cluster to put it in — a cell whose every other cluster is full produces drifters and no migrations, and that gap is the signal you want, not noise to
    /// be suppressed. Read against <see cref="MigrationCount"/> to see it.
    /// </remarks>
    public int DriftersDetected { get; }

    /// <summary>
    /// Entities outside their cluster's target region by less than the intra-cell drift margin during the most recently completed tick, and therefore left
    /// alone.
    /// </summary>
    /// <remarks>
    /// <para><b>Deliberately not folded into <see cref="HysteresisAbsorbedCount"/>.</b> That counter is about cell-boundary oscillation and tunes
    /// <c>MigrationHysteresisRatio</c>; this one is about intra-cell drift and tunes <c>ClusterDriftMarginRatio</c>. They answer different questions and their
    /// margins move independently, so a single number would tune neither — which is the whole reason step 10 added a second counter rather than reusing the
    /// first.</para>
    /// <para>Read as a fraction of <c>DriftAbsorbedCount + DriftersDetected</c>: near zero means the margin is too narrow to damp anything, near one means it
    /// is wide enough to be suppressing repairs the step exists to make.</para>
    /// </remarks>
    public int DriftAbsorbedCount { get; }

    /// <summary>
    /// Milliseconds of the per-tick re-clustering budget the repair path committed during the most recently completed tick.
    /// </summary>
    /// <remarks>
    /// <b>Projected, not measured, and the difference is the design.</b> A repair unit is admitted only if the remaining budget covers its whole cost, so
    /// the estimate has to exist before the work does; reporting the elapsed time instead would report a number that gated nothing. The projection is
    /// <c>entities x SpatialGridConfig.RepairNsPerEntity</c>. Compare it against a measured tick time to find out whether that constant is honest — which is
    /// exactly what step 11's adaptive budget will do automatically.
    /// </remarks>
    public double ReclusterBudgetUsedMs { get; }

    /// <summary>Entities re-packed by the repair path — the full Morton re-sort — during the most recently completed tick.</summary>
    /// <remarks>
    /// Disjoint from <see cref="MigrationCount"/> in intent though not in mechanism: a repair emits ordinary migration requests, so the entities counted
    /// here are also counted there when the requests execute. This is the count the PLANNER committed to; that one is what the Migrate phase actually moved,
    /// and the two differ by the requests whose source slot had emptied in between.
    /// </remarks>
    public int RepairedEntityCount { get; }

    /// <summary>Repair units admitted during the most recently completed tick. A unit is one cell's N worst clusters, or one whole cell.</summary>
    public int RepairUnitCount { get; }

    /// <summary>
    /// Repair units whose projected cost exceeded the remaining budget, and which were therefore never begun.
    /// </summary>
    /// <remarks>
    /// A Morton sort cannot be halved — a partly re-sorted cell has paid the cost and banked only part of the benefit — so the budget admits whole units and
    /// refuses the rest outright. A persistently non-zero reading against a zero <see cref="RepairUnitCount"/> means the budget is below the cost of the
    /// smallest unit on offer and no repair can ever happen; raise <c>ReclusterBudgetMs</c>, or lower <c>RepairWorstClustersPerUnit</c> so a unit is smaller.
    /// </remarks>
    public int RepairUnitsRefused { get; }

    /// <summary>
    /// The whole cost of the most recently completed tick's migrations in milliseconds — the migrant loop <b>plus</b> the bulk index descent and the bulk
    /// EntityMap patch, summed across every worker.
    /// </summary>
    /// <remarks>
    /// <para><b>Read this, not <see cref="MigrationExecuteMs"/>, for cost per entity.</b> That one brackets the migrant loop alone, and since #872 step 6
    /// the loop merely STAGES the index update — the descent that applies it happens in a later phase. The secondary index was measured at ~48 % of a
    /// migration's cost, so the older field under-reports by roughly half. Step 11 added the two missing timers to produce this one, and it is what the
    /// adaptive budget divides by <see cref="MigrationCount"/>.</para>
    /// <para><b>CPU-milliseconds, not span.</b> W workers each busy for 1 ms report 4, not 1.</para>
    /// </remarks>
    public double MigrationTotalMs { get; init; }

    /// <summary>
    /// <c>ExecuteMigrations</c> slices that ran during the most recently completed tick — the number of spans summed into <see cref="MigrationExecuteMs"/>.
    /// </summary>
    /// <remarks>
    /// <para><b>Read it before dividing <see cref="MigrationExecuteMs"/> by <see cref="MigrationCount"/>.</b> That quotient is a per-entity cost only while
    /// this is 1. The parallel fence sizes the Migrate phase's slices from the worker count, so raising W raises the number of spans summed into the
    /// numerator while the workload fixes the denominator, and every per-slice fixed cost inside the bracket — three chunk-accessor rentals and the span
    /// construction — is charged again to every entity in the tick.</para>
    /// <para><b>This is what #912 was: 425 -&gt; 844 -&gt; 1 440 ns/entity at W = 2/4/8 was recorded as contention in the relocation drain for three steps,
    /// and nothing published the quantity that would have told the two apart.</b> The decomposition is
    /// <c>(Prologue + Epilogue) / MigrationCount</c> for the per-slice term and the remainder for the per-entity one.</para>
    /// </remarks>
    public int MigrationSliceCount { get; init; }

    /// <summary>
    /// Indexed-field slots covered by the tick's Migrate-slice zone-map batches — one per indexed field per slice (#926).
    /// </summary>
    /// <remarks>
    /// <para><b>Read it against <see cref="MigrationSliceCount"/>, never against <see cref="MigrationCount"/>.</b> The identity is
    /// <c>ZoneMapBatchOpens == MigrationSliceCount x indexed fields</c>, and the point of publishing it is that the right-hand side does not mention the
    /// migration count at all. Before #926 the Migrate loop took the zone map's archetype-wide grow latch once per migrant per field — two atomics on one
    /// cache line, from every worker — and the fix was to hold one batch per field for the whole slice. A number that starts tracking the migration count
    /// again is that fix regressing, and no timing is needed to see it.</para>
    /// <para><b>Slots, not latches, and the two differ.</b> A String64 field is indexed but has no zone map (there is no numeric min/max to summarise), so
    /// its slot is covered by the batch pass and holds no latch. Counting slots is what makes the identity above exact — the migrant loop's field id counts
    /// fields, not maps — but it means an archetype indexed only on String64 reports a non-zero figure having acquired nothing.</para>
    /// <para><b>Zero on the SERIAL fence, which deliberately opens no batches</b> — it has one writer and nothing to amortise, and holding a batch there
    /// would strand a destination past the pinned generation with no growing fallback available. Also zero on a tick that migrated nothing, and for an
    /// archetype with no indexed fields. All three mean what they say.</para>
    /// </remarks>
    public int ZoneMapBatchOpens { get; init; }

    /// <summary>
    /// Milliseconds the tick's Migrate slices spent before their first migrant — span construction and the three chunk-accessor rentals — summed across
    /// slices. Part of <see cref="MigrationExecuteMs"/>, not additional to it.
    /// </summary>
    /// <remarks>
    /// Grows with <see cref="MigrationSliceCount"/> rather than with <see cref="MigrationCount"/>, which is exactly what makes it worth publishing
    /// separately: it is the term a per-entity reading mis-attributes.
    /// </remarks>
    public double MigrationPrologueMs { get; init; }

    /// <summary>
    /// Milliseconds the tick's Migrate slices spent after their last migrant — the three accessor disposals and the migration span's publish — summed
    /// across slices. Part of <see cref="MigrationExecuteMs"/>, not additional to it.
    /// </summary>
    /// <inheritdoc cref="MigrationPrologueMs"/>
    public double MigrationEpilogueMs { get; init; }

    /// <summary>Cell-crossing migrations executed during the most recently completed tick.</summary>
    /// <remarks>
    /// <para>This, <see cref="RelocationsExecuted"/> and <see cref="RepairsExecuted"/> sum EXACTLY to <see cref="MigrationCount"/> — the split is checkable
    /// rather than trusted, which is the property #911 gave the trace record and #912 brought to this surface.</para>
    /// <para><b>Executed, not queued.</b> <see cref="CrossingsQueued"/> and <see cref="RelocationsAdmitted"/> are decisions taken a phase earlier and a
    /// tick earlier respectively; these three are what the drain actually moved. The gap between a queued relocation and an executed one is
    /// <see cref="RelocationsSuperseded"/> plus the stale-source guard.</para>
    /// <para><b>Always counted, unlike the trace record's split.</b> A Migrate slice mixes all three kinds by construction, so attributing a per-entity cost
    /// to one of them needs this; and needing the profiler on to get it would perturb the bracket the cost is measured in.</para>
    /// </remarks>
    public int CrossingsExecuted { get; init; }

    /// <summary>Intra-cell relocations executed during the most recently completed tick.</summary>
    /// <inheritdoc cref="CrossingsExecuted"/>
    public int RelocationsExecuted { get; init; }

    /// <summary>Repair moves executed during the most recently completed tick.</summary>
    /// <inheritdoc cref="CrossingsExecuted"/>
    public int RepairsExecuted { get; init; }

    /// <summary>
    /// Exclusive acquisitions of the archetype's finalize latch during the most recently completed tick.
    /// </summary>
    /// <remarks>
    /// <para><b>The archetype-wide serialisation point, and the only one a fence worker can reach.</b> Twenty-one call sites take it: the three bulk
    /// enqueues, the per-archetype array growths, the new-cluster slow paths and the cell-tree segment creation. A phase whose per-worker CPU rises with the
    /// worker count is serialised here or it is not serialised at all — so this staying flat while per-entity cost rises ELIMINATES the latch, which is a
    /// result worth as much as naming it.</para>
    /// <para><b>A count, not a held time.</b> Timing it would cost two timestamps per acquisition against critical sections frequently shorter than that,
    /// and would change what it measures. #912's method note says it: count operations, do not time a phase already known to be off.</para>
    /// </remarks>
    public long FinalizeLockAcquisitions { get; init; }

    /// <summary>
    /// Intra-cell relocations the re-clustering budget refused during the most recently completed tick, and therefore dropped (#872 step 11).
    /// </summary>
    /// <remarks>
    /// <para><b>Dropped, not deferred, and that is deliberate.</b> A relocation's chosen destination was the least-enlargement cluster as of the AABBs of
    /// the tick that detected it; carrying it forward would apply a stale choice against bounds this tick's own migrations have moved. It is re-detected
    /// next tick from current data, so what this counts is deferred WORK, not lost work.</para>
    /// <para>Read against <see cref="DriftersDetected"/>: a persistently high ratio means the budget is below the world's drift rate, and equilibrium
    /// tightness — not correctness — is what degrades. That is §5.6's stated failure mode, by design.</para>
    /// </remarks>
    public int RelocationsThrottled { get; init; }

    /// <summary>
    /// Intra-cell relocations dropped because a cell crossing already claimed the same entity during the most recently completed tick (#877).
    /// </summary>
    /// <remarks>
    /// <para><b>Not a budget signal, and that is why it is not folded into <see cref="RelocationsThrottled"/>.</b> Drift detection runs in AabbRefresh, so
    /// its relocations are decided by the next tick's Prep — by which time the crossing detector may have filed a <c>CellCrossing</c> for the same entity.
    /// An entity that drifted to the edge of its cell is precisely the one most likely to leave it, so the overlap is the common case on a moving world,
    /// not a corner. The relocation is dropped because the crossing supersedes it: the entity is migrating regardless, to a cell the relocation's
    /// destination was never chosen for.</para>
    /// <para><b>Before #877 both requests were executed.</b> <c>ExecuteMigrations</c>' stale-source guard is an occupancy test rather than an identity test,
    /// so it only covered the case where the freed slot was still empty when the second request drained; when an unrelated migrant had claimed it, the
    /// relocation moved THAT entity to a destination chosen for someone else. A high reading here is normal and healthy. A high reading that starts
    /// tracking <see cref="RelocationsThrottled"/> is worth a look, because it means drift and crossings are competing for the same entities.</para>
    /// </remarks>
    public int RelocationsSuperseded { get; init; }

    /// <summary>
    /// Prep's internal split for the most recently completed tick, in milliseconds of wall time, in phase order:
    /// snapshot, occupancy mask, index replay, min/max refresh, crossing detection, budget, repair plan, pre-size.
    /// </summary>
    /// <remarks>
    /// <b>Added because the design that proposes optimising Prep could not say which of its steps cost anything.</b> The phase-level spans say Prep is 52 %
    /// of the fence; they do not say whether that is the occupancy mask, the min/max rescan or the decisions at the tail. Ranking the steps from what each
    /// one touches rather than from what each one costs is the same mistake one level down that the phase spans were added to prevent one level up.
    /// </remarks>
    public double PrepSnapshotMs { get; init; }

    /// <inheritdoc cref="PrepSnapshotMs"/>
    public double PrepMaskMs { get; init; }

    /// <inheritdoc cref="PrepSnapshotMs"/>
    public double PrepShadowMs { get; init; }

    /// <inheritdoc cref="PrepSnapshotMs"/>
    public double PrepZoneMapMs { get; init; }

    /// <inheritdoc cref="PrepSnapshotMs"/>
    public double PrepDetectMs { get; init; }

    /// <inheritdoc cref="PrepSnapshotMs"/>
    public double PrepThrottleMs { get; init; }

    /// <inheritdoc cref="PrepSnapshotMs"/>
    public double PrepPlanMs { get; init; }

    /// <inheritdoc cref="PrepSnapshotMs"/>
    public double PrepPreSizeMs { get; init; }

    /// <summary>Clusters whose change word survived the occupancy mask — the size of the domain a sliced Prep would partition.</summary>
    public int PrepDirtyClusters { get; init; }

    /// <summary>
    /// Drifters that were detected but for which placement found no better cluster, during the most recently completed tick.
    /// </summary>
    /// <remarks>
    /// The gap <see cref="DriftersDetected"/>'s own remarks point at, now counted rather than inferred: a cell whose every cluster is equally bad produces
    /// drifters and no migrations. Together the identity
    /// <c>DriftersDetected = admitted + RelocationsThrottled + RelocationsSuperseded + DriftersUnplaced</c> holds over a tick,
    /// which is what makes "a drifter is never both absorbed and throttled" checkable rather than merely asserted (<c>AC-11.7</c>).
    /// </remarks>
    public int DriftersUnplaced { get; init; }

    /// <summary>
    /// Repair units admitted by the safety valve during the most recently completed tick — begun despite insufficient remaining budget because the cell's
    /// degradation had reached <c>SpatialGridConfig.ClusterRepairCriticalExtentRatio</c>.
    /// </summary>
    /// <remarks>
    /// <b>The only budget overshoot the engine permits</b>, and it is bounded: the valve caps its unit at <c>RepairWorstClustersPerUnit</c> clusters and
    /// fires at most once per tick per archetype. A persistently non-zero reading means degradation is outrunning the budget — the condition §5.6's valve
    /// exists to bound rather than to hide, so raise <c>ReclusterBudgetMs</c> rather than treating the valve as the steady state.
    /// </remarks>
    public int RepairValveFires { get; init; }

    /// <summary>Cells currently waiting in the repair priority queue. Unlike every other per-tick member this is a LEVEL, not a rate: it persists.</summary>
    public int RepairQueueDepth { get; init; }

    /// <summary>
    /// Candidates evicted from the repair queue since this cluster state was created, because the queue was at
    /// <c>SpatialGridConfig.RepairQueueMaxCells</c>. Cumulative.
    /// </summary>
    /// <remarks>
    /// Read against <see cref="RepairQueueDepth"/>: a growing count while the depth sits at the cap says the cap is below what the world actually
    /// degrades, and cells are being forgotten. Zero at a depth below the cap is the healthy reading.
    /// </remarks>
    public long RepairQueueEvicted { get; init; }

    /// <summary>
    /// Milliseconds spent maintaining the repair queue — absorbing nominations and re-ranking — during the most recently completed tick.
    /// </summary>
    /// <remarks>
    /// <c>AC-11.5</c>'s numerator: "a queue that costs more to maintain than the work it schedules is a net loss". Read as a fraction of
    /// <see cref="ReclusterBudgetUsedMs"/>. Re-ranking is lazy — triggered by new nominations or by a <c>SpatialGrid.TierVersion</c> change, not by the
    /// tick — so a settled world should report near zero here.
    /// </remarks>
    public double RepairQueueMaintenanceMs { get; init; }

    /// <summary>
    /// The measured per-entity migration cost, in nanoseconds, that this tick's budget was actually spent against (#872 step 11).
    /// </summary>
    /// <remarks>
    /// <para>An EWMA over <see cref="MigrationTotalMs"/> per migrant plus the repair planner's own measured cost, seeded from
    /// <c>SpatialGridConfig.RepairNsPerEntity</c> and clamped to a band around it. It replaces that constant as the operative number: <c>AC-12.7</c>
    /// measured the real cost at 22x to 117x the design's estimate, and a budget calibrated on the wrong constant admits units costing many times what it
    /// thinks they do.</para>
    /// <para><b>Blended across migration kinds.</b> Cell crossings, intra-cell relocations and repair moves all contribute; splitting them needs per-class
    /// attribution inside the apply phases, where the staged records carry no kind. A blended measurement is nonetheless strictly better than a constant
    /// that cannot track the machine at all.</para>
    /// </remarks>
    public double MeasuredNsPerEntity { get; init; }

    /// <summary>Clusters that passed the intra-cell drift gate last tick — the population detection walked. Wave-2 K1.</summary>
    public int DriftGatedClusters { get; init; }

    /// <summary>
    /// Clusters that exceeded the configured floor (<c>ClusterTargetExtentRatio</c>) but not their cell's density-derived target, so the drift scan never
    /// ran on them — the work step 14's target function removed. A world in the 16–64 entities/cell basin reports every written cluster here and zero in
    /// <see cref="DriftGatedClusters"/>.
    /// </summary>
    public int DriftSuppressedByDensity { get; init; }

    /// <summary>Of <see cref="DriftersUnplaced"/>, those whose cell offered no candidate cluster at all. Wave-2 K2.</summary>
    public int DriftersUnplacedNoCandidate { get; init; }

    /// <summary>
    /// Drifters whose cell had candidates but no free slot left this pass, filed for a fresh cluster at drain time rather than left in place (step 14).
    /// </summary>
    public int DriftersSpilled { get; init; }

    /// <summary>Pinned claims rejected at drain time and executed as first fit instead. Wave-2 K5.</summary>
    public int PinsRejected { get; init; }

    /// <summary>Intra-cell relocations the throttle admitted last tick. Wave-2 K6.</summary>
    public int RelocationsAdmitted { get; init; }

    /// <summary>Cell-crossing requests the throttle found queued and charged last tick. Wave-2 K6.</summary>
    public int CrossingsQueued { get; init; }

    /// <summary>Budget charged to admitted relocations last tick, in nanoseconds. Wave-2 K9.</summary>
    public double RelocationSpendNs { get; init; }

    /// <summary>
    /// <see cref="RelocationSpendNs"/> on a tick where the repair planner refused at least one unit; zero otherwise. Budget spent on relocations while a
    /// repair was turned away. Wave-2 K9.
    /// </summary>
    public double RepairBudgetStarvedNs { get; init; }

    /// <summary>
    /// The largest distance by which any of this archetype's cluster boxes reaches outside its own cell, in world units (#911 O2).
    /// </summary>
    /// <remarks>
    /// <para><b>A third clock, and the only member with one.</b> It is neither per-tick nor a growing total: it is a running MAXIMUM that never falls, by
    /// design — every kNN ring test widens by it, and too large merely widens a search while too small loses results. So it does not reset at the fence and
    /// <see cref="DatabaseEngine.GetSpatialTelemetryTotal"/> takes the max across archetypes rather than the sum. Differentiating it yields nothing.</para>
    /// <para>Non-zero only once a cluster has proved it: a world of point entities reports zero forever, which is correct rather than missing. Rises at the
    /// fence following the write that produced it, not at the write.</para>
    /// </remarks>
    public float MaxClusterOverhang { get; init; }

    /// <summary>Cell halves promoted from the linear scan to a per-cell R-Tree during the most recently completed tick.</summary>
    /// <remarks>
    /// <para><b>Published because "does a cell half ever actually promote in a real workload?" was unanswerable without a debugger.</b> Promotion needs a
    /// half at or above <c>SpatialOptions.CellTreePromoteThreshold</c> clusters AND a mean extent at or below <c>CellTreePromoteTightness</c>, and the engine
    /// measures 0.63-1.03 of the cell under motion — so a persistent zero here is the finding, not a broken counter.</para>
    /// <para>Read against <see cref="CellTreeDemotions"/>: both non-zero and tracking each other means the promote/demote gap is too narrow and cells are
    /// thrashing between two O(clusters) rebuilds.</para>
    /// </remarks>
    public int CellTreePromotions { get; init; }

    /// <summary>Cell halves that fell back from a per-cell R-Tree to the linear scan during the most recently completed tick.</summary>
    /// <remarks>See <see cref="CellTreePromotions"/> — the two are read together.</remarks>
    public int CellTreeDemotions { get; init; }

    /// <summary>
    /// Clusters that contributed a tightness reading during the most recently completed tick — the denominator of <see cref="MeanClusterExtentRatio"/> and
    /// <see cref="MeanPackingBound"/>.
    /// </summary>
    /// <remarks>
    /// <para><b>Clusters WRITTEN this tick, not clusters that exist.</b> The reading is folded into the AABB refresh, where the extent is already in
    /// registers and the cell's population has already been read for the density target. A settled world therefore reports zero samples — and that is why
    /// this member exists: without it a mean of zero over nothing is indistinguishable from clusters that are genuinely points, and "zero means zero, never
    /// unknown" would be violated by the means rather than honoured.</para>
    /// <para>A full-population reading is a different measurement — <c>SpatialPartitionMatrix.MeasurePartition</c> sweeps every active cluster for it — and
    /// costs <c>O(active clusters)</c>, which is precisely what this path exists not to pay.</para>
    /// </remarks>
    public int TightnessSampleCount { get; init; }

    /// <summary>
    /// Mean measured tightness over <see cref="TightnessSampleCount"/> clusters: each cluster's largest axis extent as a fraction of its cell's edge.
    /// </summary>
    /// <remarks>Zero when <see cref="TightnessSampleCount"/> is zero. This is the quantity every table of the design's §5.8 reports.</remarks>
    public double MeanClusterExtentRatio { get; init; }

    /// <summary>
    /// Mean packing bound over the same clusters: <c>(slotsPerCluster / entitiesInCell)^(1/d)</c> as a fraction of the cell edge — the tightest a full
    /// cluster can be in that cell without fragmenting.
    /// </summary>
    /// <remarks>
    /// Geometry, not tuning, and independent of <c>ClusterTargetPackingSlack</c>: it is published even in constant mode, where the gates ignore it. It is
    /// <b>1</b> for any cell holding no more entities than one cluster's slots — the 16-64 per-cell basin the density guidance recommends — which is why
    /// intra-cell maintenance correctly switches itself off there and why a tightness of 0.9 in that basin is not a defect.
    /// </remarks>
    public double MeanPackingBound { get; init; }

    /// <summary>
    /// <see cref="MeanClusterExtentRatio"/> divided by <see cref="MeanPackingBound"/> — measured extent against what geometry allows. <b>The</b> number the
    /// spatial partitioning subsystem is judged on. <c>1</c> is optimal packing; the engine measured ~1.24 on a Morton-ordered bulk load and ~1.56 on a
    /// random-order one.
    /// </summary>
    /// <remarks>
    /// <b>A ratio of means, not a mean of ratios.</b> The two differ whenever the sampled clusters sit in cells of differing population, and the ratio of
    /// means is what the design's tables quote — a mean extent read against a bound. Both operands are published so a consumer that wants the other
    /// statistic can say so explicitly rather than inheriting one silently. Zero when <see cref="TightnessSampleCount"/> is zero.
    /// </remarks>
    public double MeanTightnessToBound => MeanPackingBound > 0d ? MeanClusterExtentRatio / MeanPackingBound : 0d;

    /// <summary>
    /// Clusters currently live. The denominator for every ratio above — a migration count means nothing without the population it came from.
    /// </summary>
    public int ActiveClusterCount { get; }

    /// <summary>Migrations executed since this archetype's cluster state was created. Cumulative; differentiate over time for a rate.</summary>
    public long TotalMigrations { get; }

    /// <summary>
    /// Cell-boundary crossings absorbed by the hysteresis margin since this archetype's cluster state was created. Cumulative twin of
    /// <see cref="HysteresisAbsorbedCount"/>, and subject to the same per-write-path unit caveat.
    /// </summary>
    public long TotalHysteresisAbsorbed { get; }
}
