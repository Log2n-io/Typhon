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
    /// <summary>
    /// Cell-crossing requests the throttle found queued and charged. Never refused — present so the budget's denominator is on the timeline too.
    /// </summary>
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
    /// <summary>
    /// Cell halves demoted back to the linear scan this tick. Kept separate from the promotions: a net of zero hides a cell thrashing between both.
    /// </summary>
    [BeginParam] public int CellTreeDemotions;

    // ── Appended for the maintenance controller (#906; rules SO-02, TH-04, RP-07). A record written before these is a strict prefix of one written after,
    //    and the generated decoder zero-fills the fields a shorter record lacks. ─────────────────────────────────────────────────────────────────────

    /// <summary>Clusters this archetype's range queries opened since the previous fence (SO-02). Sum across records for a window, never average.</summary>
    [BeginParam] public long QueryClustersOpened;
    /// <summary>Entities in those clusters, every occupied slot. Summed candidates over summed hits is what a match cost over the window.</summary>
    [BeginParam] public long QueryCandidates;
    /// <summary>Matches those queries returned.</summary>
    [BeginParam] public long QueryHits;
    /// <summary>
    /// The configured <c>ReclusterBudgetMs</c>, the ceiling the grant is a share of. Repeated per record: the attach stream carries no configuration.
    /// </summary>
    [BeginParam] public float BudgetConfiguredMs;
    /// <summary>The budget the controller granted this tick (TH-04): the configured budget times the share the queries' efficiency earned.</summary>
    [BeginParam] public float BudgetGrantedMs;
    /// <summary>The configured <c>QueryEfficiencyTolerance</c>: how far above the best the grant becomes whole. Zero means the controller is off.</summary>
    [BeginParam] public float EfficiencyTolerance;
    /// <summary>The controller's input: smoothed candidates over smoothed hits. Zero without a signal.</summary>
    [BeginParam] public float CandidatesPerHitSmoothed;
    /// <summary>The controller's set point: the lowest smoothed value since the last re-base. Zero without a signal.</summary>
    [BeginParam] public float CandidatesPerHitBest;
    /// <summary>
    /// Consecutive ticks at the whole budget, set there by the distance. The re-base comes on the tick after <c>EfficiencyRebaseTicks</c> of them.
    /// </summary>
    [BeginParam] public int TicksAtWholeBudget;
    /// <summary>Bit 0: the queries hit enough to steer by. Bit 1: this tick re-based the best, accepting what the whole budget could not recover.</summary>
    [BeginParam] public byte ControllerFlags;
    /// <summary>Re-bases since the archetype's cluster state was created. Cumulative, so a dropped record or a late attach loses none of them.</summary>
    [BeginParam] public int EfficiencyRebases;
    /// <summary>Cells waiting out <c>RepairCooldownTicks</c> after a repair (RP-07). A level.</summary>
    [BeginParam] public int RepairCellsCooling;
    /// <summary>Repair units the safety valve admitted past the budget this tick.</summary>
    [BeginParam] public int RepairValveFires;
    /// <summary>Entities the repair plan committed to move this tick.</summary>
    [BeginParam] public int RepairedEntities;
    /// <summary>
    /// Repair-queue candidates evicted at the cap since the archetype's cluster state was created. Cumulative: differentiate across records.
    /// </summary>
    [BeginParam] public long RepairQueueEvicted;
    /// <summary>The measured per-entity migration cost the budget was spent against, in nanoseconds.</summary>
    [BeginParam] public float MeasuredNsPerEntity;
    /// <summary>The throttle's multiplier on the drift target (step 14, D2): 1 is none; at its cap relocation detection is off.</summary>
    [BeginParam] public float DriftTargetBoost;

    // ── Appended for the realm census (#WB-05). Not a realm dimension: everything above stays summed across realms, because the counters are owned per
    //    archetype. These two exist so a consumer of the per-realm rows can say how many rows it is NOT showing. ──────────────────────────────────────

    /// <summary>Realms this archetype has cluster state in, runnable or not. The denominator of the per-realm rows.</summary>
    [BeginParam] public int PresentRealms;
    /// <summary>How many of those were runnable this tick — the number of <c>SpatialRealmTelemetry</c> rows emitted beside this record.</summary>
    [BeginParam] public int RunnableRealms;

    // ── Appended for the per-realm RATE rows (kind 70). Two counts rather than one, and the pair is load-bearing: kind 69 reports its row count against a
    //    population it already carries, but this record's population is PresentRealms, and present is not touched. One number could not separate "the cap
    //    truncated the rows" from "those realms did nothing", which are the two things a consumer must never confuse. ─────────────────────────────────

    /// <summary>
    /// Realms the fence touched this tick for this archetype — the realms that HAVE a kind-70 row to emit, counted BEFORE the cap.
    /// </summary>
    /// <remarks>
    /// Before the cap on purpose. Counted after, it would equal <see cref="RatesRealmsEmitted"/> by construction and say nothing at all — the same
    /// correction the frame assembler's window mark needed, for the same reason.
    /// </remarks>
    [BeginParam] public int RatesRealmsTouched;

    /// <summary>
    /// How many kind-70 rows were actually emitted beside this record. Equal to <see cref="RatesRealmsTouched"/> means nothing was truncated, and an absent
    /// realm did no work; less than it means a consumer must state how many working realms it is NOT showing.
    /// </summary>
    [BeginParam] public int RatesRealmsEmitted;
}

/// <summary>
/// Per-realm, per-archetype snapshot of one realm's partition SHAPE (#WB-05). Instant-shaped.
/// </summary>
/// <remarks>
/// <para>
/// A TRANSPORT, like <see cref="SpatialArchetypeTelemetryEvent"/>, and for the same reason: the Workbench's attach session is a one-way trace stream with no
/// way to call an accessor on the engine it is watching. Every value is a field the realm already owns.
/// </para>
/// <para>
/// <b>Why shape and not rates.</b> After #1050 a realm owns its grid, its cell size, its dimensionality and its own maintenance budget, so "is spatial
/// healthy" became a per-realm question — a 16 km planet and a 50 m dungeon have different pathologies and a thrashing dungeon is invisible behind a calm
/// planet. What is per-realm TODAY is the shape: the grid, the reach, the outliers, the promoted and blocked cells, the configured budget. The per-tick rate
/// counters are not — they live one-per-archetype on <c>ArchetypeClusterState</c> — so they are absent here rather than copied under a realm's name.
/// </para>
/// <para>
/// <b>Runnable realms only</b>, per <c>Realms/02-runtime-lifecycle.md</c> §6. The realms left out are accounted for by
/// <see cref="SpatialArchetypeTelemetryEvent.PresentRealms"/> / <see cref="SpatialArchetypeTelemetryEvent.RunnableRealms"/> on the same tick.
/// </para>
/// </remarks>
[TraceEvent(TraceEventKind.SpatialRealmTelemetry, Shape = TraceEventShape.Instant, Gate = "SpatialRealmTelemetryActive")]
internal ref partial struct SpatialRealmTelemetryEvent
{
    [BeginParam] public ushort RealmId;
    [BeginParam] public ushort ArchetypeId;
    /// <summary>The realm's run state this tick: <see cref="RealmRunState"/> — Dormant 0, Simulated 1, Active 2, Closing 3.</summary>
    [BeginParam] public byte RunState;
    /// <summary>The realm's tick divisor: 1 every tick, N once every N ticks. Saturates at 255, which no policy reaches.</summary>
    [BeginParam] public byte Divisor;
    /// <summary>This realm's own cell edge, in world units. The whole point of the record: it differs per realm.</summary>
    [BeginParam] public float CellSize;
    /// <summary>Cells in this realm's grid — what separates a planet from an interior more plainly than its bounds do.</summary>
    [BeginParam] public int CellCount;
    /// <summary>Cells along the third axis. 1 for a flat realm, so the record says the dimensionality without a separate flag.</summary>
    [BeginParam] public int GridDepth;
    /// <summary>This archetype's clusters in this realm. Zero for a realm whose entities have all left, which is a real state, not a missing row.</summary>
    [BeginParam] public int Clusters;
    /// <summary>
    /// How far past its own cell a query must reach for this archetype's clusters IN THIS REALM, in world units. Read against
    /// <see cref="CellSize"/>: a reach of 180 in a 64 m realm means the cell-level broadphase is pruning nothing.
    /// </summary>
    [BeginParam] public float ClusterReach;
    /// <summary>Clusters excluded from the reach and visited by name instead. A handful is the design; a growing count is not.</summary>
    [BeginParam] public int EscapedClusters;
    /// <summary>Cell halves currently carrying a per-cell R-Tree in this realm.</summary>
    [BeginParam] public int PromotedCells;
    /// <summary>Cells whose tightness blocked a promotion in this realm.</summary>
    [BeginParam] public int BlockedCells;
    /// <summary>
    /// This realm's <b>declared</b> <c>ReclusterBudgetMs</c> — what its <c>RealmConfig</c> asks for, NOT what the engine enforces.
    /// </summary>
    /// <remarks>
    /// The distinction is load-bearing and a consumer must carry it. Maintenance is budgeted per ARCHETYPE (Realms D-6: "budget and repair queue stay per
    /// archetype"), and the one budget actually spent is taken from realm 0's grid — <c>ApplyMigrationThrottle(PrimaryGrid, …)</c>. So two realms declaring
    /// different values both get realm 0's, and a panel that presents this as the ceiling a grant was measured against would be comparing the grant to a
    /// number nothing used. It is reported per row because the declaration is a real per-realm fact worth seeing — not least when it differs from what runs.
    /// </remarks>
    [BeginParam] public float BudgetConfiguredMs;
    /// <summary>
    /// This realm's <b>declared</b> <c>QueryEfficiencyTolerance</c>. Declared, for the same reason as <see cref="BudgetConfiguredMs"/>: one controller per
    /// archetype steers the one budget, from every realm's queries mixed, using realm 0's tolerance. Zero declares the controller off for this realm.
    /// </summary>
    [BeginParam] public float EfficiencyTolerance;
}

/// <summary>
/// What one realm's partition DID this tick, for one archetype — the rate twin of <see cref="SpatialRealmTelemetryEvent"/>'s shape.
/// </summary>
/// <remarks>
/// <para>
/// <b>Emitted for realms the fence TOUCHED</b>, which is a different set from kind 67's runnable realms and contains neither. A row here with no kind-67
/// row is a non-runnable realm that did maintenance work — an anomaly worth seeing rather than a row to drop. Keeping the two records apart is what lets a
/// missing row mean exactly one thing in each.
/// </para>
/// <para>
/// <b>The field order is the engine counter block's own declaration order</b> (<c>RealmTickCounters.Fields</c>), so the emitter is a straight copy with no
/// reordering. A swapped pair would compile and would be invisible until someone read a number that made no sense.
/// </para>
/// <para>
/// <b>The tightness pair are SUMS with their sample count beside them, never means</b>, because a consumer folding realms must re-derive the mean from
/// summed numerators over the summed count — averaging per-realm means weights a realm that scanned one cluster like one that scanned ten thousand.
/// <b><see cref="LargestArrivalRun"/> folds with MAX, never with +.</b>
/// </para>
/// <para>
/// <b>Capped per archetype per tick.</b> <see cref="SpatialArchetypeTelemetryEvent.RatesRealmsTouched"/> and
/// <see cref="SpatialArchetypeTelemetryEvent.RatesRealmsEmitted"/> on the same tick say whether the cap fired: equal means an absent realm did no work,
/// and a shortfall means a consumer must state how many working realms it is not showing.
/// </para>
/// </remarks>
[TraceEvent(TraceEventKind.SpatialRealmRates, Shape = TraceEventShape.Instant, Gate = "SpatialRealmRatesActive")]
internal ref partial struct SpatialRealmRatesEvent
{
    [BeginParam] public ushort RealmId;
    [BeginParam] public ushort ArchetypeId;
    /// <summary>Sum of the measured extent ratios behind <c>TightnessSamples</c>, for this realm.</summary>
    [BeginParam] public double TightnessExtentSum;
    /// <summary>Sum of the packing bounds behind <c>TightnessSamples</c>, for this realm.</summary>
    [BeginParam] public double TightnessBoundSum;
    /// <summary>Budget the admitted relocations of this realm were charged, in nanoseconds.</summary>
    [BeginParam] public double RelocationSpendNs;
    /// <summary>Clusters of this realm examined by the intra-cell drifter scan this tick.</summary>
    [BeginParam] public int ClustersScanned;
    /// <summary>Entity slots of this realm the AABB refresh actually walked this tick.</summary>
    [BeginParam] public int SlotsScanned;
    /// <summary>Entities of this realm the intra-cell scan found outside their cluster's target region this tick. Detection, not outcome.</summary>
    [BeginParam] public int DriftersDetected;
    /// <summary>Drifters of this realm left in place because they were inside the drift dead zone.</summary>
    [BeginParam] public int DriftAbsorbed;
    /// <summary>Drifters of this realm for which placement found no better cluster.</summary>
    [BeginParam] public int DriftersUnplaced;
    /// <summary>Clusters of this realm that passed the intra-cell drift gate.</summary>
    [BeginParam] public int DriftGatedClusters;
    /// <summary>Clusters of this realm above the configured floor but below their cell's density-derived target, so the drift scan never ran.</summary>
    [BeginParam] public int DriftSuppressedByDensity;
    /// <summary>The subset of <c>DriftersUnplaced</c> whose cell offered no candidate at all.</summary>
    [BeginParam] public int DriftersUnplacedNoCandidate;
    /// <summary>Drifters of this realm whose cell had candidates but no capacity left this pass.</summary>
    [BeginParam] public int DriftersSpilled;
    /// <summary>Clusters of this realm that contributed a tightness reading this tick — the denominator of the two sums above.</summary>
    [BeginParam] public int TightnessSamples;
    /// <summary>Migrations executed into this realm this tick. Its three kinds below sum to it exactly.</summary>
    [BeginParam] public int MigrationCount;
    /// <summary>Cell-crossing migrations executed into this realm.</summary>
    [BeginParam] public int CrossingsExecuted;
    /// <summary>Intra-cell relocations executed in this realm.</summary>
    [BeginParam] public int RelocationsExecuted;
    /// <summary>Repair moves executed in this realm.</summary>
    [BeginParam] public int RepairsExecuted;
    /// <summary>Crossings filed in this realm whose destination cell is not adjacent to the source cell.</summary>
    [BeginParam] public int JumpCrossings;
    /// <summary>Crossings filed in this realm whose position lay outside the grid and were clamped into an edge cell.</summary>
    [BeginParam] public int ClampedDestinations;
    /// <summary>Write-time crossing flags of this realm that the drain found describing an entity that is home, and dropped rather than executed.</summary>
    [BeginParam] public int StaleFlagsDropped;
    /// <summary>Intra-cell relocations of this realm the budget refused.</summary>
    [BeginParam] public int RelocationsThrottled;
    /// <summary>Relocations of this realm dropped because a mandatory request already names the same source slot.</summary>
    [BeginParam] public int RelocationsSuperseded;
    /// <summary>Intra-cell relocations of this realm the throttle admitted into the drain prefix.</summary>
    [BeginParam] public int RelocationsAdmitted;
    /// <summary>Mandatory cell-crossing requests of this realm the throttle found queued and charged.</summary>
    [BeginParam] public int CrossingsQueued;
    /// <summary>Pinned claims in this realm rejected at drain time and therefore executed as first fit.</summary>
    [BeginParam] public int PinsRejected;
    /// <summary>Entities of this realm re-packed by the repair path this tick.</summary>
    [BeginParam] public int RepairedEntityCount;
    /// <summary>Repair units admitted in this realm this tick.</summary>
    [BeginParam] public int RepairUnitCount;
    /// <summary>Repair units of this realm the remaining budget could not finish, and which were therefore never begun.</summary>
    [BeginParam] public int RepairUnitsRefused;
    /// <summary>Safety-valve admissions in this realm — repair units begun with insufficient budget because the cell was critical.</summary>
    [BeginParam] public int RepairValveFires;
    /// <summary>Distinct destination cells of this realm's drained cell crossings.</summary>
    [BeginParam] public int ArrivalCellsTouched;
    /// <summary>The most cell crossings into one destination cell of this realm this tick.</summary>
    /// <remarks>
    /// <b>A MAXIMUM, and the one member of this record that does not sum.</b> SO-01 requires the telemetry roll-up to fold by KIND rather than uniformly —
    /// it is why <c>ClusterReach</c> maxes where the extensive counters sum — and the same applies along BOTH axes this record is folded on: adding two
    /// realms' peaks, or one realm's peaks across a window, reports a burst no cell ever received. Nothing in the bytes says so, which is why it is said
    /// here.
    /// </remarks>
    [BeginParam] public int LargestArrivalRun;
    /// <summary>Cell halves of this realm promoted to a tree this tick.</summary>
    [BeginParam] public int CellTreePromotions;
    /// <summary>Cell halves of this realm that fell back from a tree this tick.</summary>
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

