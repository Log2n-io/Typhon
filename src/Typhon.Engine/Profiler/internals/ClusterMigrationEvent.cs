// CS0282: split-partial-struct field ordering — benign for TraceEvent ref structs (codec encodes per-field, never as a blob). See #294.
#pragma warning disable CS0282

using Typhon.Profiler;

namespace Typhon.Engine.Internals;

/// <summary>
/// Producer-side ref struct for <see cref="TraceEventKind.ClusterMigration"/>. Three required fields plus, since #911, the three-way per-kind split as
/// optionals — which is what put an optional-mask byte on this kind's wire shape for the first time (see the enum's remarks: the block is APPENDED, so an
/// older record stays a strict prefix).
/// </summary>
[TraceEvent(TraceEventKind.ClusterMigration, EmitEncoder = true)]
internal ref partial struct ClusterMigrationEvent
{
    [BeginParam]
    public ushort ArchetypeId;
    [BeginParam]
    public int MigrationCount;
    /// <summary>
    /// Total component instances moved across this batch — set by the producer to <c>MigrationCount × archetype.componentCount</c>.
    /// Lets the viewer report data-movement cost (vs. just entity count). Optional at producer site; left at 0 when unset.
    /// </summary>
    [BeginParam]
    public int ComponentCount;

    // ── #911 O1 (G1): the three kinds, told apart ──────────────────────────────────────────────────────────────────────
    //
    // The span brackets one Migrate SLICE, and a slice mixes all three kinds by construction: the pending queue is sorted by
    // DESTINATION CELL KEY so the drain can carve worker-disjoint slices, which interleaves crossings, relocations and
    // repairs wherever they target the same cells. So the trace said how long migrations took and never which kind — every
    // finding in the design's §5.8 that needed per-kind attribution was reached by adding throwaway counters to a branch.
    //
    // COUNTS, not a span per migration. A span per migrated entity is 10^5/second on a moving world — the rate
    // TyphonEvent.SuppressedKinds exists to keep off the ring — and the kind is already on the request, so counting it in
    // the loop that reads it costs one increment on a path that has just done a byte copy.

    /// <summary>Outcome: requests in this slice that moved an entity to a different CELL — correctness moves the throttle never refuses.</summary>
    [Optional(MaskValue = 0x01)] private int _crossingCount;

    /// <summary>Outcome: intra-cell relocations in this slice — quality moves, the ones the budget may refuse.</summary>
    [Optional(MaskValue = 0x02)] private int _relocationCount;

    /// <summary>Outcome: entities in this slice belonging to a repair unit's Morton re-pack.</summary>
    [Optional(MaskValue = 0x04)] private int _repairCount;
}

/// <summary>
/// Per-tick span around <c>DetectClusterMigrations</c>. Fires once per archetype per tick whenever the spatial path runs, regardless of whether any entity
/// actually crossed a cell — gives the user visibility into the scan cost in workloads where motion stays within hysteresis (AntHill).
/// Outcome counts (migrations queued, hysteresis absorbed) are recorded separately by the existing
/// <see cref="TraceEventKind.SpatialClusterMigrationQueue"/> and <see cref="TraceEventKind.SpatialClusterMigrationHysteresis"/> instants.
/// </summary>
[TraceEvent(TraceEventKind.SpatialClusterMigrationDetectScan, EmitEncoder = true, Gate = "SpatialClusterMigrationDetectActive")]
internal ref partial struct SpatialClusterMigrationDetectScanEvent
{
    [BeginParam] public ushort ArchetypeId;
    /// <summary>Slots iterated in this scan (== bits set in the dirty/synthetic bitmap passed to DetectClusterMigrations).</summary>
    [BeginParam] public int ScanSlotCount;

    /// <summary>Outcome: slots that crossed a cell boundary and were queued for migration this scan.</summary>
    [Optional(MaskValue = 0x01)] private int _migrationsQueued;
    /// <summary>Outcome: slots that exited the raw cell but stayed within the hysteresis margin — counted but not migrated.</summary>
    [Optional(MaskValue = 0x02)] private int _hysteresisAbsorbed;
    /// <summary>Outcome: distinct clusters with at least one dirty slot — gives concentration vs. spread of activity.</summary>
    [Optional(MaskValue = 0x04)] private int _clustersTouched;
}

/// <summary>
/// One admitted repair unit (#911 O1). Emitted from the Prep-phase planner at the moment of admission, so a trace can answer WHICH CELL was repaired and how
/// degraded it was — the question <see cref="TraceEventKind.ClusterMigration"/> cannot express, because it brackets a slice holding all three migration kinds.
/// </summary>
[TraceEvent(TraceEventKind.SpatialRepairUnit, EmitEncoder = true, Gate = "SpatialClusterRepairActive")]
internal ref partial struct SpatialRepairUnitEvent
{
    [BeginParam] public ushort ArchetypeId;
    /// <summary>The cell whose worst clusters this unit re-packs. A repair unit is always one cell's.</summary>
    [BeginParam] public int CellKey;
    /// <summary>Clusters in the unit — <c>RepairWorstClustersPerUnit</c>, or fewer when the cell holds fewer or the valve capped it.</summary>
    [BeginParam] public int ClusterCount;
    /// <summary>Entities the unit re-packs. The number every budget projection is built from.</summary>
    [BeginParam] public int EntityCount;

    /// <summary>The cell's max-axis extent as a fraction of the cell size, which is the term it was RANKED on.</summary>
    [Optional(MaskValue = 0x01)] private float _degradation;
    /// <summary>Non-zero when the safety valve admitted this unit despite insufficient remaining budget.</summary>
    [Optional(MaskValue = 0x02)] private byte _valveFired;
    /// <summary>Entities the plan actually moved. Below <see cref="EntityCount"/> when part of the unit was already spoken for.</summary>
    [Optional(MaskValue = 0x04)] private int _movedCount;
}

/// <summary>
/// Per-archetype, per-tick rollup of the throttle's relocation outcome split (#911 O1). Instant-shaped: it reports a decision already made, and has no
/// duration of its own.
/// </summary>
/// <remarks>
/// <b>Every field is required, because an instant has no optional-mask byte</b> — the generator's <c>EmitInstant</c> path sums the <c>[BeginParam]</c> fields
/// and ignores <c>[Optional]</c> entirely. Which is the right shape here anyway: this is a rollup emitted once per archetype per tick, so every term is
/// always present and a mask would encode nothing.
/// </remarks>
[TraceEvent(TraceEventKind.SpatialRelocationOutcome, Shape = TraceEventShape.Instant, Gate = "SpatialClusterRelocationActive")]
internal ref partial struct SpatialRelocationOutcomeEvent
{
    [BeginParam] public ushort ArchetypeId;
    /// <summary>Intra-cell relocations the throttle admitted this tick.</summary>
    [BeginParam] public int Admitted;
    /// <summary>Relocations the BUDGET refused — the signal that drift outruns the budget.</summary>
    [BeginParam] public int Throttled;
    /// <summary>Relocations dropped because a cell crossing already claimed the same entity. Refused by nothing; not a budget signal.</summary>
    [BeginParam] public int Superseded;
    /// <summary>Drifters for which placement found no better cluster — a saturated CELL, whose remedy is the opposite of a saturated budget's.</summary>
    [BeginParam] public int Unplaced;
    /// <summary>Of the unplaced, those whose cell offered no candidate cluster at all.</summary>
    [BeginParam] public int UnplacedNoCandidate;
    /// <summary>Drifters filed against a fresh cluster because their cell had candidates but no free slot this pass.</summary>
    [BeginParam] public int Spilled;
    /// <summary>Pinned claims rejected at drain time and executed as first fit instead.</summary>
    [BeginParam] public int PinsRejected;
    /// <summary>Cell-crossing requests the throttle found queued and charged. Never refused — present so the budget's denominator is on the timeline too.</summary>
    [BeginParam] public int CrossingsQueued;
}

/// <summary>
/// Per-archetype, per-tick snapshot of the spatial-maintenance counters (#911 O3). Instant-shaped.
/// </summary>
/// <remarks>
/// This is a TRANSPORT, not a measurement: every value is a field the fence has already computed and published on
/// <see cref="SpatialMigrationTelemetry"/>. It exists because the Workbench's attach session is a one-way trace stream with no way to call an accessor on the
/// engine it is watching.
/// </remarks>
[TraceEvent(TraceEventKind.SpatialArchetypeTelemetry, Shape = TraceEventShape.Instant, Gate = "SpatialArchetypeTelemetryActive")]
internal ref partial struct SpatialArchetypeTelemetryEvent
{
    [BeginParam] public ushort ArchetypeId;
    /// <summary>Clusters currently live — the denominator every rate below is read against.</summary>
    [BeginParam] public int ActiveClusters;
    /// <summary>Entities migrated this tick, all three kinds together. The per-kind split rides <see cref="TraceEventKind.ClusterMigration"/>.</summary>
    [BeginParam] public int Migrations;
    /// <summary>
    /// The whole cost of this tick's migrations — the migrant loop plus the bulk index descent and the EntityMap patch.
    /// <b>CPU-milliseconds summed across workers, not a span:</b> W workers busy for 1 ms report W. A consumer that renders it as a duration is making the
    /// error that let an 8 ms budget buy one repair unit.
    /// </summary>
    [BeginParam] public float MigrationCpuMs;
    /// <summary>Cell-boundary crossings absorbed by the hysteresis margin this tick — the crossing group's other half.</summary>
    [BeginParam] public int HysteresisAbsorbed;
    /// <summary>Entities found outside their cluster's target region this tick. The identity's left-hand side; its outcomes ride kind 65.</summary>
    [BeginParam] public int DriftersDetected;
    /// <summary>Repair units admitted this tick.</summary>
    [BeginParam] public int RepairUnits;
    /// <summary>Repair units whose projected cost exceeded the remaining budget.</summary>
    [BeginParam] public int RepairUnitsRefused;
    /// <summary>Cells waiting in the repair priority queue. A LEVEL, not a rate: it persists across ticks.</summary>
    [BeginParam] public int RepairQueueDepth;
    /// <summary>Milliseconds of the re-clustering budget the repair path committed this tick. Projected, not measured — the projection is what gates.</summary>
    [BeginParam] public float BudgetUsedMs;
    /// <summary>
    /// Clusters that contributed a tightness reading. ZERO is meaningful and load-bearing: it says the fence wrote nothing, not that clusters are points.
    /// </summary>
    [BeginParam] public int TightnessSamples;
    /// <summary>Mean measured cluster extent as a fraction of the cell edge, over <see cref="TightnessSamples"/> clusters.</summary>
    [BeginParam] public float ExtentRatio;
    /// <summary>Mean packing bound over the same clusters — the denominator tightness is judged against.</summary>
    [BeginParam] public float PackingBound;
    /// <summary>Cell halves promoted to a per-cell R-Tree this tick.</summary>
    [BeginParam] public int CellTreePromotions;
    /// <summary>Cell halves demoted back to the linear scan this tick. Kept separate from the promotions: a net of zero hides a cell thrashing between both.</summary>
    [BeginParam] public int CellTreeDemotions;
}

/// <summary>
/// Per-tick span around <c>RecomputeDirtyClusterAabbs</c>. Captures the cost of the occupancy scan + bit-exact AABB compare across every active cluster,
/// even when most AABBs end up unchanged. The per-cluster AABB changes are recorded separately by <see cref="TraceEventKind.SpatialCellIndexUpdate"/>.
/// </summary>
[TraceEvent(TraceEventKind.SpatialClusterAabbRefresh, EmitEncoder = true, Gate = "SpatialCellIndexUpdateActive")]
internal ref partial struct SpatialClusterAabbRefreshEvent
{
    [BeginParam] public ushort ArchetypeId;
    /// <summary>Active clusters scanned this tick (each does an occupancy walk + AABB recompute).</summary>
    [BeginParam] public int ClusterScanned;

    /// <summary>Outcome: clusters whose AABB actually changed and got an UpdateAt + Cell:Index:Update emit.</summary>
    [Optional(MaskValue = 0x01)] private int _aabbsChanged;
    /// <summary>Outcome: total occupancy slots iterated across all scanned clusters — the real O(n) cost driver of the pass.</summary>
    [Optional(MaskValue = 0x02)] private int _slotsScanned;
    /// <summary>Outcome: clusters that hit the max-extent guard and enqueued outlier migrations (rare path).</summary>
    [Optional(MaskValue = 0x04)] private int _outlierGuardFires;
}

