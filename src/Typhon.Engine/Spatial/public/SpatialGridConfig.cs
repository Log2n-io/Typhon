using System;
using System.Numerics;
using JetBrains.Annotations;

namespace Typhon.Engine;

/// <summary>
/// Immutable configuration for the engine-wide spatial grid. Set once via <see cref="DatabaseEngine.ConfigureSpatialGrid"/> before archetypes are initialized.
/// </summary>
/// <remarks>
/// <para>All spatial archetypes share a single coarse grid with one cell size. Per-archetype differences are expressed at the system level, through tier
/// filters, rather than at the grid level.</para>
/// <para><b>The grid is three-dimensional, and a flat world is simply a grid one cell deep.</b> There is deliberately no 2D overload set: a 2D/3D pair
/// would give you a call site where the Z coordinate is silently dropped, collapsing every entity onto the z = 0 plane. That does not raise — it returns
/// spatial query results that are quietly wrong. Use <see cref="Flat"/> to build a one-cell-deep world in a single call, and keep one code path.</para>
/// <para>Grid dimensions are derived per axis from (WorldMax - WorldMin) / CellSize, rounded up, and cell keys are plain row-major
/// <c>(z * GridHeight + y) * GridWidth + x</c>. There is no Morton encoding and no power-of-two padding: a 32-bit 3D Morton key would cap the world at 1 024
/// cells per axis, and the square key space its 2D predecessor needed would have made the descriptor count <c>KeySpaceDim³</c> — over a billion cells for a
/// 1024 x 1024 x 1 world.</para>
/// </remarks>
[PublicAPI]
public readonly struct SpatialGridConfig
{
    /// <summary>World-space minimum corner (inclusive).</summary>
    /// <remarks>
    /// <b>f64 since #914, and that is what makes the world big.</b> Stored bounds stay f32 and cell-relative (<c>C15</c>); it is the FRAME they are measured
    /// from that has to carry the magnitude, because a cell origin at 10⁹ has more mantissa than f32 holds. Widening from <see cref="Vector3"/> is implicit
    /// and exact, so an f32 world configures exactly as it did.
    /// </remarks>
    public readonly Vector3D WorldMin;

    /// <summary>World-space maximum corner (exclusive — the grid excludes the max edge).</summary>
    /// <inheritdoc cref="WorldMin"/>
    public readonly Vector3D WorldMax;

    /// <summary>Size of a single grid cell, in world units. Cells are cubic. Must be &gt; 0.</summary>
    /// <remarks>
    /// f64 so that <c>origin = WorldMin + cellCoord × CellSize</c> is exact at world extent — the product is what overflows f32's mantissa, not the cell size
    /// itself. A cell size that happens to be f32-exact is unaffected.
    /// </remarks>
    public readonly double CellSize;

    /// <summary>
    /// Fractional dead zone applied per axis during entity migration, as a fraction of cell size.
    /// Default 0.05 (5 % of cell size).
    /// </summary>
    public readonly float MigrationHysteresisRatio;

    /// <summary>
    /// The extent a cluster's AABB is expected to stay within, as a fraction of cell size — the <b>target region</b> of §5.2, and parameter <b>P4</b> of
    /// the design's open-parameter table. Default 0.25.
    /// </summary>
    /// <remarks>
    /// <para><b>What it gates.</b> Intra-cell drift detection (#872 step 10) is two-level. A cluster whose largest axis extent is within this bound is
    /// <i>tight enough</i> and its entities are never examined; only inside a cluster that exceeds it does the per-entity test run. That is what makes
    /// "detect broadly" affordable — a healthy world pays three float compares per written cluster and nothing per entity.</para>
    /// <para><b>Why it is not the cluster's own AABB.</b> The write-time CAS in <c>ClusterRef.MaybeGrowAndFlagShrink</c> grows that bound to contain every
    /// entity it holds, so "outside my cluster's AABB" is never true of anything. The target region has to be an independent, tighter box or it detects
    /// nothing.</para>
    /// <para><b>This is the floor of the target, not the target (step 14).</b> The extent a full cluster can reach in a cell of <c>E</c> entities is bounded
    /// by geometry: <c>(slotsPerCluster / E)^(1/d)</c> of the cell edge, 1.0 in a cell that fits one cluster and 0.5 at 512 entities in 3D. A constant
    /// 0.25 asked for the impossible everywhere the density guidance sends users, so the gate fired on every written cluster on every tick and every
    /// entity beyond the target was a drifter with nowhere to go. The operative target is <c>clamp(ClusterTargetPackingSlack × bound, this, 1)</c>,
    /// evaluated per cell from its live population — see <see cref="ClusterTargetPackingSlack"/>. This value only matters in cells dense enough for the
    /// bound to fall below it, which at the default slack is about 3 500 entities per cell in 3D and 1 500 in 2D.</para>
    /// </remarks>
    public readonly float ClusterTargetExtentRatio;

    /// <summary>
    /// Multiplier on the per-cell packing bound that sets the intra-cell target extent: <c>target = clamp(slack × (slotsPerCluster / E)^(1/d),
    /// <see cref="ClusterTargetExtentRatio"/>, 1)</c> of the cell edge, where <c>E</c> is the cell's live entity count. Default 1.5. <c>0</c> disables the
    /// derivation, the throttle's boost and the repair-first exclusion, and makes <see cref="ClusterTargetExtentRatio"/> the constant it used to be.
    /// </summary>
    /// <remarks>
    /// <para><b>Why a slack at all.</b> The bound is what a perfect Morton tiling reaches; an online packing under motion sits above it, and a gate at
    /// exactly the bound would fire on clusters no relocation can improve. 1.5 is the factor the R-Tree crossover sweep itself tiles at, so a cell that
    /// holds the target is one whose tree can prune.</para>
    /// <para><b>A target of 1 means off.</b> In a cell whose population fits one cluster the bound is the cell, so the gate never fires, the drift scan
    /// never runs and no relocation is ever filed — which is the correct amount of intra-cell maintenance for that cell, and was measured to be where every
    /// uniform configuration in the recommended 16–64 entities/cell basin lives. Repair nominates at <c>max(target, ClusterRepairExtentRatio)</c> for the
    /// same reason: a re-sort cannot beat the bound either.</para>
    /// </remarks>
    public readonly float ClusterTargetPackingSlack;

    /// <summary>
    /// Place an arriving entity — a spawn, or a cell crossing at drain time — in the cluster of its cell whose bound grows least to admit it (ties to the
    /// smallest resulting box), rather than in the first cluster with a free slot. Default <c>false</c>: opt-in.
    /// </summary>
    /// <remarks>
    /// <para><b>Off by default because it measured as a wash where repair runs and a loss where it does not.</b> Against first fit, with the batch
    /// ordering on in both arms (step 15, 16 000 entities at 250 per cell, W = 8, medians): Drift at 8 ms 77.4 vs 77.6 %, Drift at 1 ms 85.7 vs 84.5 %,
    /// Cruise at 8 ms 89.3 vs 80.2 % — worse. First fit concentrates arrivals into one cursor cluster per cell that the planner then re-sorts; least
    /// enlargement spreads them over every cluster, each a little wider, and under heavy crossings that is the mean going up. The one thing it does
    /// unambiguously is place a lone arrival beside its neighbours (asserted), which a workload with rare arrivals into a dense, slow pocket may want.</para>
    /// <para>What is NOT optional is the ordering of batch spawns (<see cref="BatchSpawnSortThreshold"/>) — that is where the measured gain lives.</para>
    /// </remarks>
    public readonly bool LeastEnlargementPlacement;

    /// <summary>
    /// Open a fresh cluster for an arrival whose best candidate would stretch past the cell's density-derived target × <see cref="GrowthCapSlack"/>,
    /// up to <see cref="MaxOpenClustersPerCell"/> open clusters per cell. Implies least-enlargement ranking of the candidates. Default <c>false</c>.
    /// </summary>
    /// <remarks>
    /// The one write-path mechanism that CREATES tightness under motion — 7 to 16 points tighter than first fit at 40 ticks, the best sustained arm
    /// of the step-15 measurement — and it is paid for in occupancy and per-cluster fence work: +4–29 % fence, −4 to −17 points of slot occupancy at
    /// 250 entities per cell. At birth on an unsorted fill it buys only ~4 points (ranking finds candidates that fit under the cap, so few clusters
    /// open); birth tightness is <see cref="BatchSpawnSortThreshold"/>'s job. A workload trade, not a default — the case for it is a dense, slow pocket
    /// queried with boxes no larger than a tenth of a cell. Never fires in a cell whose population fits one cluster.
    /// </remarks>
    public readonly bool GrowthCapPlacement;

    /// <summary>Multiplier on the density-derived target that a growth-capped arrival may stretch its cluster to. Default 1.25.</summary>
    public readonly float GrowthCapSlack;

    /// <summary>Open (non-full) clusters the growth cap may hold per cell before it falls back to least enlargement. Default 4.</summary>
    public readonly int MaxOpenClustersPerCell;

    /// <summary>
    /// A transaction that spawns at least this many entities places them in per-cell Morton order, so a bulk load is born at the packing bound rather
    /// than at the full extent of every cell it touches. Default 128; <c>0</c> disables the ordering.
    /// </summary>
    /// <remarks>
    /// Placement is first fit within a cell in visiting order, and a random-order batch fills each cluster with whatever arrived next: measured 99 % of
    /// the cell at 250 entities per cell, against a bound of 63 %. Ordering the batch along the intra-cell Morton curve costs one O(n log n) sort over
    /// the batch and reads only the staged spatial field — no accessor — and leaves the batch born at 1.24× the bound at 250 per cell (1.52× at 512:
    /// Z-order runs that straddle a quadrant boundary are what keeps it off the ideal). Below the threshold a batch
    /// cannot fill more than one cluster of a cell, so the sort would buy nothing.
    /// </remarks>
    public readonly int BatchSpawnSortThreshold;

    /// <summary>
    /// Dead zone around the target region, as a fraction of cell size, below which a drifting entity is left alone. Default 0.05.
    /// </summary>
    /// <remarks>
    /// The intra-cell counterpart of <see cref="MigrationHysteresisRatio"/>, and deliberately a separate number: that one governs <i>cell crossing</i>, is
    /// measured by <c>LastTickHysteresisAbsorbedCount</c>, and both existing detectors emit only when the cell key actually changes — so neither can
    /// absorb anything for a move that stays inside one cell. Without its own margin, an entity sitting on the target-region boundary would be relocated
    /// every tick it jitters across, paying a full migration to move a few units.
    /// </remarks>
    public readonly float ClusterDriftMarginRatio;

    /// <summary>
    /// The extent past which a cluster is considered beyond the delta path's reach and its cell is nominated for a full re-sort, as a fraction of cell size
    /// — design parameter <b>P7</b>. Default 0.75.
    /// </summary>
    /// <remarks>
    /// <para><b>P7 as the design states it cannot fire, and this is the correction.</b> The design says to start at "the existing <c>cellSize x 1.2</c>
    /// extent check". That check belongs to the OUTLIER GUARD, and it is looking for a different fault: a cluster whose bound has grown past its own cell,
    /// which happens only when it holds entities that should have migrated out and did not. A cluster whose entities all genuinely belong to its cell cannot
    /// exceed the cell by more than the hysteresis margin — <see cref="MigrationHysteresisRatio"/>, 5 % by default — so its extent tops out near
    /// <c>1.05 x cellSize</c> and the 1.2 threshold is unreachable. The scenario <c>AC-12.1</c> names, AABBs at some 90 % of the cell, sits comfortably
    /// below it. Wiring repair to that trigger would have produced a repair path that never runs, and a green test suite saying nothing.</para>
    /// <para><b>Why 0.75 and not the drift target.</b> <see cref="ClusterTargetExtentRatio"/> (0.25) is where step 10 starts RELOCATING, and nominating
    /// there would ask for a re-sort of every cluster the delta path is already working on — the opposite of rare. Repair is for degradation relocation
    /// cannot undo, so its threshold belongs well above the drift target and below the cell: three quarters of a cell means the cluster is opened by three
    /// quarters of the queries that touch the cell, and no greedy per-entity move is going to change that. It sits between the two existing gates by
    /// construction, so a cluster that nominates has always been drift-gated too.</para>
    /// <para>Provisional, like P4. The value that resolves it comes from step 11's budget/tightness curve.</para>
    /// </remarks>
    public readonly float ClusterRepairExtentRatio;

    /// <summary>
    /// Per-tick, per-archetype wall-clock budget for the <b>repair</b> path — the full Morton re-sort of §5.2 — in milliseconds. <c>0</c> disables repair
    /// entirely. Default 1.0.
    /// </summary>
    /// <remarks>
    /// <para><b>Whole units, never a fraction of one (§5.6).</b> A Morton sort cannot be halved: a partly re-sorted cell is <i>worse</i> than an untouched
    /// one, because the cost is paid and the benefit is not. So this budget gates whether a unit is <b>started</b>, and a unit the remaining budget cannot
    /// finish is not begun (<c>AC-12.5</c>). That makes the budget an admission threshold rather than a stopping condition, which is the opposite of how
    /// the delta path in step 10 spends — that one is resumable per entity.</para>
    /// <para><b>The estimate, not the measurement, decides.</b> Cost is projected as
    /// <c>entities x <see cref="RepairNsPerEntity"/></c> before anything moves, since the decision has to precede the work. The measured spend lands in
    /// <c>SpatialMigrationTelemetry.ReclusterBudgetUsedMs</c>, which is what tells you whether the projection is honest.</para>
    /// <para><b>Step 11 replaces the constant with a controller.</b> The design's budget is "adjusted at runtime from the previous tick's measured cost";
    /// this is the static knob that controller will drive, and the seam a test uses to pin the budget to just-below-cost.</para>
    /// <para><b>1.0 ms is sized against the measured cost, not chosen.</b> At <see cref="RepairNsPerEntity"/> it admits ~670 entities, which covers one
    /// default unit of eight 49-slot clusters (392 entities) with room to spare. The first value tried was 0.25 ms, which — once the cost was measured
    /// rather than assumed — could not afford a single unit, so the feature would have shipped switched on and never run. Repair is self-limiting: a cell
    /// that has been re-packed is refused on every later tick until its geometry actually changes, so this budget is a ceiling on a rare event and not a
    /// per-tick tax.</para>
    /// </remarks>
    public readonly float ReclusterBudgetMs;

    /// <summary>
    /// Projected cost of moving one entity on the repair path, in nanoseconds — the exchange rate <see cref="ReclusterBudgetMs"/> is spent at. Default 1500.
    /// </summary>
    /// <remarks>
    /// <para><b>Not §5.2's 60 ns. That figure does not survive measurement.</b> §5.2 budgets a batched relocation at ~60 ns/entity and derives the ~6 ms
    /// per 100 K-entity cell that makes repair the rare path rather than the per-tick one. <c>AC-12.7</c>'s measurement
    /// (<c>ClusterRepairTests.MeasureRepairCostPerEntity</c>, Release, 2 000 entities in 41 clusters, six consecutive repairs on a warm engine) reports
    /// <b>1 331 to 6 992 ns/entity</b> — 22x to 117x the estimate — which projects to <b>~133 ms</b> for a 100 K-entity cell rather than ~6 ms. The first
    /// repair a process performs measured 20 681 ns/entity and is warm-up, not signal.</para>
    /// <para><b>What that changes.</b> Repair being the rare path is, if anything, more true than the design argued: at ~133 ms a 100 K-entity cell cannot
    /// be re-sorted whole inside any tick, which is exactly why §5.6's <i>preferred</i> unit is one cell's N worst clusters and why
    /// <see cref="RepairWorstClustersPerUnit"/> defaults to 8 rather than to the whole cell. What it does invalidate is a budget calibrated on 60: it would
    /// admit units costing twenty times what it thinks, and the per-tick spend AC-11.1 bounds would be exceeded by that factor.</para>
    /// <para>1 500 is the warm BEST, deliberately, not the worst. The number is an admission threshold, and the value that matters for step 11 is the one a
    /// controller will converge on from real measurements; seeding it with the worst observed sample would refuse units the machine can comfortably afford.
    /// Exposed rather than hard-coded because it is a property of the machine and of the archetype's component width, not of the design.</para>
    /// </remarks>
    public readonly float RepairNsPerEntity;

    /// <summary>
    /// How many of a cell's worst clusters one repair unit re-packs. Default 8; <c>0</c> or more than the cell holds means the whole cell.
    /// </summary>
    /// <remarks>
    /// §5.6's <b>preferred unit</b> — "one cell's <i>N worst clusters</i>" — with the whole cell as the documented fallback. Finer than a whole cell, still
    /// internally coherent (the entities re-sorted are exactly the ones re-packed), and it targets the clusters actually costing selectivity instead of
    /// spending a 100 K-entity budget to fix eight bad bounds. "Worst" is the largest maximum axis extent, which is the same quantity the
    /// <c>cellSize x 1.2</c> trigger reads.
    /// </remarks>
    public readonly int RepairWorstClustersPerUnit;

    /// <summary>
    /// Degradation at which a cell may jump the repair queue and be serviced even when the budget cannot cover it. Default 1.0. Zero disables the valve.
    /// </summary>
    /// <remarks>
    /// <para><b>§5.6's safety valve: "degradation must be bounded".</b> A budget that never keeps up would otherwise let a cell degrade without limit,
    /// because ranking only decides who goes FIRST, not who goes at all. At 1.0 the trigger is a cluster whose bound covers its entire cell — the worst
    /// state reachable without the outlier guard firing, since a cluster holding only its own cell's entities tops out near
    /// <c>1 + MigrationHysteresisRatio</c>. Strictly above <see cref="ClusterRepairExtentRatio"/>'s 0.75, so a critical cell has always been an ordinary
    /// candidate first.</para>
    /// <para><b>The overshoot this permits is CAPPED, and the cap is not optional.</b> <c>AC-11.1</c> allows exceeding the budget "by more than one
    /// indivisible unit", which reads as licence until one notices that a whole cell is one indivisible unit and a 100 K-entity cell was measured at
    /// ~133 ms — a 133x overrun that would still claim compliance. So a valve admission forces the unit down to
    /// <see cref="RepairWorstClustersPerUnit"/> clusters and fires at most once per tick per archetype. Nothing else in the planner may exceed the
    /// budget at all.</para>
    /// </remarks>
    public readonly float ClusterRepairCriticalExtentRatio;

    /// <summary>
    /// How much a queued cell's rank grows per tick spent waiting. Default 0.05 — a candidate doubles its score after 20 ticks. Zero disables ageing.
    /// </summary>
    /// <remarks>
    /// <para><b>Ranking alone starves, and <c>AC-11.3</c> forbids that.</b> §5.6 asks for candidates ranked by expected selectivity gain and is explicit
    /// that "round-robin is the wrong policy" — but a pure ranking never services a cell that is permanently outranked. The age factor is unbounded in
    /// the tick count, so whatever a candidate's base score, enough waiting carries it to the head. That makes no-starvation a property of the arithmetic
    /// rather than a hope about the workload.</para>
    /// <para>0.05 is slow relative to the rate at which repairs actually happen: a cell that genuinely deserves servicing gets it long before ageing
    /// matters, and ageing only decides the order among candidates the budget has been unable to reach.</para>
    /// </remarks>
    public readonly float RepairAgingRatePerTick;

    /// <summary>
    /// Hard cap on cells waiting in the repair queue. Default 4096. Beyond it the worst-ranked candidate is evicted to admit a better one.
    /// </summary>
    /// <remarks>
    /// <b><c>AC-11.8</c>: the queue must not grow without bound.</b> Step 11's queue is persistent — that is what stops a refused nomination being
    /// forgotten — so it needs an explicit ceiling that a per-tick list did not. Eviction is by score, so a full queue sheds the candidates whose repair
    /// would buy least, and the eviction count is published: a non-zero rate against a full queue is the reading that says the cap is below what the world
    /// actually degrades.
    /// </remarks>
    public readonly int RepairQueueMaxCells;

    // ── Derived values, computed in the constructor ────────────────────────

    /// <summary>
    /// Number of cells along the X axis — derived from (WorldMax.X - WorldMin.X) / CellSize, rounded up.
    /// </summary>
    public readonly int GridWidth;

    /// <summary>Number of cells along the Y axis.</summary>
    public readonly int GridHeight;

    /// <summary>
    /// Number of cells along the Z axis. <c>1</c> for a flat world built with
    /// <see cref="Flat(Vector2,Vector2,double,float,float,float,float,float,float,int,float,float,int,float,bool,bool,float,int,int)"/>.
    /// </summary>
    public readonly int GridDepth;

    /// <summary>Precomputed 1 / <see cref="CellSize"/>.</summary>
    public readonly double InverseCellSize;

    /// <summary>Total number of cell descriptor slots: <see cref="GridWidth"/> × <see cref="GridHeight"/> × <see cref="GridDepth"/>.</summary>
    public readonly int CellCount;

    /// <summary>
    /// Build a grid configuration and precompute the derived cell dimensions. World bounds are half-open: <paramref name="worldMin"/> is inclusive,
    /// <paramref name="worldMax"/> is exclusive.
    /// </summary>
    /// <param name="worldMin">World-space minimum corner (inclusive).</param>
    /// <param name="worldMax">World-space maximum corner (exclusive); must be strictly greater than <paramref name="worldMin"/> on all three axes.</param>
    /// <param name="cellSize">Cell size in world units; must be &gt; 0.</param>
    /// <param name="migrationHysteresisRatio">Per-axis dead zone as a fraction of cell size (default 0.05).</param>
    /// <param name="clusterTargetExtentRatio">Target cluster extent as a fraction of cell size — P4 (default 0.25).</param>
    /// <param name="clusterDriftMarginRatio">Intra-cell drift dead zone as a fraction of cell size (default 0.05).</param>
    /// <param name="clusterRepairExtentRatio">Extent past which a cell is nominated for a full re-sort — P7 (default 0.75).</param>
    /// <param name="reclusterBudgetMs">Per-tick repair budget in milliseconds; 0 disables repair (default 1.0).</param>
    /// <param name="repairNsPerEntity">Projected repair cost per entity in nanoseconds (default 1500, measured).</param>
    /// <param name="repairWorstClustersPerUnit">Clusters per repair unit; 0 means the whole cell (default 8).</param>
    /// <param name="clusterRepairCriticalExtentRatio">Degradation at which a cell jumps the queue regardless of budget; 0 disables it (default 1.0).</param>
    /// <param name="repairAgingRatePerTick">Rank growth per tick a candidate waits; 0 disables ageing (default 0.05).</param>
    /// <param name="repairQueueMaxCells">Hard cap on queued repair candidates (default 4096).</param>
    /// <param name="clusterTargetPackingSlack">
    /// Multiplier on the per-cell packing bound that sets the intra-cell target; 0 keeps the constant (default 1.5).
    /// </param>
    /// <param name="leastEnlargementPlacement">Place arrivals in the least-enlargement cluster of their cell (default false — opt-in).</param>
    /// <param name="growthCapPlacement">Open a fresh cluster when an arrival would stretch past the cap (default false).</param>
    /// <param name="growthCapSlack">Multiplier on the density target the cap allows (default 1.25).</param>
    /// <param name="maxOpenClustersPerCell">Open clusters the cap may hold per cell (default 4).</param>
    /// <param name="batchSpawnSortThreshold">Spawns per transaction above which the batch is placed in Morton order; 0 disables (default 128).</param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="cellSize"/> is not positive, or the derived cell count does not fit a 32-bit cell key.
    /// </exception>
    /// <exception cref="ArgumentException"><paramref name="worldMax"/> is not strictly greater than <paramref name="worldMin"/> on all three axes.</exception>
    public SpatialGridConfig(Vector3D worldMin, Vector3D worldMax, double cellSize, float migrationHysteresisRatio = 0.05f,
        float clusterTargetExtentRatio = 0.25f, float clusterDriftMarginRatio = 0.05f, float clusterRepairExtentRatio = 0.75f,
        float reclusterBudgetMs = 1.0f, float repairNsPerEntity = 1500f, int repairWorstClustersPerUnit = 8,
        float clusterRepairCriticalExtentRatio = 1.0f, float repairAgingRatePerTick = 0.05f, int repairQueueMaxCells = 4096,
        float clusterTargetPackingSlack = 1.5f, bool leastEnlargementPlacement = false, bool growthCapPlacement = false, float growthCapSlack = 1.25f,
        int maxOpenClustersPerCell = 4, int batchSpawnSortThreshold = 128)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(clusterTargetPackingSlack);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(growthCapSlack);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxOpenClustersPerCell, 1);
        ArgumentOutOfRangeException.ThrowIfNegative(batchSpawnSortThreshold);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(cellSize);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(clusterTargetExtentRatio);
        ArgumentOutOfRangeException.ThrowIfNegative(clusterDriftMarginRatio);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(clusterRepairExtentRatio);
        ArgumentOutOfRangeException.ThrowIfNegative(clusterRepairCriticalExtentRatio);

        // Both bounds, and for the two reasons the ratio above is already bounded: a value that silently disables a feature is a configuration error, and
        // so is one that fires it constantly. At or above the outlier guard's 1.2 the valve can never trigger, because a cluster confined to its own cell
        // tops out near 1 + MigrationHysteresisRatio. At or below clusterRepairExtentRatio EVERY nominated cell is critical, so the valve overshoots the
        // budget once per archetype on every tick for ever — which is a sustained overrun wearing a threshold as a disguise. Zero remains legal and means
        // "no valve".
        if (clusterRepairCriticalExtentRatio > 0f
            && (clusterRepairCriticalExtentRatio <= clusterRepairExtentRatio || clusterRepairCriticalExtentRatio >= 1.2f))
        {
            throw new ArgumentOutOfRangeException(nameof(clusterRepairCriticalExtentRatio), clusterRepairCriticalExtentRatio,
                $"ClusterRepairCriticalExtentRatio ({clusterRepairCriticalExtentRatio}) must sit strictly between ClusterRepairExtentRatio "
                + $"({clusterRepairExtentRatio}) and the outlier guard's 1.2, or be 0 to disable the safety valve. At or below the repair ratio every "
                + "nominated cell is critical and the valve overshoots the budget every tick; at or above 1.2 it can never fire at all.");
        }
        ArgumentOutOfRangeException.ThrowIfNegative(repairAgingRatePerTick);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(repairQueueMaxCells);
        // The UPPER bound only, and the asymmetry is deliberate. At or above the outlier guard's 1.2 the threshold can never be reached — a cluster confined
        // to its own cell tops out near 1 + MigrationHysteresisRatio — so the value silently disables the feature, which is a configuration error rather
        // than a tuning choice and deserves a throw.
        //
        // The other half of RP-04's ordering, "above ClusterTargetExtentRatio", is NOT enforced. It is a tuning guideline about the two mechanisms competing,
        // and it stops applying the moment the drift gate is switched off — which the fixtures do by setting the target ratio to 100, a value no cluster can
        // exceed. Throwing on that would reject a legal configuration in which repair is the only mechanism running, so it stays documented on
        // ClusterRepairExtentRatio and in RP-04 rather than being a hard failure.
        if (clusterRepairExtentRatio >= 1.2f)
        {
            throw new ArgumentOutOfRangeException(nameof(clusterRepairExtentRatio),
                $"ClusterRepairExtentRatio ({clusterRepairExtentRatio}) must be below the outlier guard's 1.2, or it can never fire: a cluster whose "
                + "entities all belong to its own cell cannot exceed the cell by more than MigrationHysteresisRatio.");
        }
        ArgumentOutOfRangeException.ThrowIfNegative(reclusterBudgetMs);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(repairNsPerEntity);
        ArgumentOutOfRangeException.ThrowIfNegative(repairWorstClustersPerUnit);
        if (worldMax.X <= worldMin.X || worldMax.Y <= worldMin.Y || worldMax.Z <= worldMin.Z)
        {
            throw new ArgumentException("WorldMax must be strictly greater than WorldMin on all three axes.", nameof(worldMax));
        }

        WorldMin = worldMin;
        WorldMax = worldMax;
        CellSize = cellSize;
        MigrationHysteresisRatio = migrationHysteresisRatio;
        ClusterTargetExtentRatio = clusterTargetExtentRatio;
        ClusterTargetPackingSlack = clusterTargetPackingSlack;
        LeastEnlargementPlacement = leastEnlargementPlacement;
        GrowthCapPlacement = growthCapPlacement;
        GrowthCapSlack = growthCapSlack;
        MaxOpenClustersPerCell = maxOpenClustersPerCell;
        BatchSpawnSortThreshold = batchSpawnSortThreshold;
        ClusterDriftMarginRatio = clusterDriftMarginRatio;
        ClusterRepairExtentRatio = clusterRepairExtentRatio;
        ReclusterBudgetMs = reclusterBudgetMs;
        RepairNsPerEntity = repairNsPerEntity;
        RepairWorstClustersPerUnit = repairWorstClustersPerUnit;
        ClusterRepairCriticalExtentRatio = clusterRepairCriticalExtentRatio;
        RepairAgingRatePerTick = repairAgingRatePerTick;
        RepairQueueMaxCells = repairQueueMaxCells;
        InverseCellSize = 1.0d / cellSize;

        // Ceiling in DOUBLE, not MathF. At an f64 world extent the f32 product loses whole cells: (worldMax.X - worldMin.X) at 2 x 10^9 rounds to the
        // nearest representable f32 ~128 units away, so the derived width could be short by a cell and every entity in the last column would clamp into
        // its neighbour.
        GridWidth  = (int)Math.Ceiling((worldMax.X - worldMin.X) * InverseCellSize);
        GridHeight = (int)Math.Ceiling((worldMax.Y - worldMin.Y) * InverseCellSize);
        GridDepth  = (int)Math.Ceiling((worldMax.Z - worldMin.Z) * InverseCellSize);

        // Computed in long deliberately: three axes multiply, and a silent int overflow here would produce a negative CellCount, a negative-length descriptor
        // array and an exception a long way from the configuration that caused it. The bound is the cell-key type, not memory — a 32-bit key is what every
        // consumer stores (ClusterCellMap, the profiler payloads, CellState lookups).
        long cellCount = (long)GridWidth * GridHeight * GridDepth;
        if (cellCount > int.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(cellSize),
                $"Grid dimensions {GridWidth} x {GridHeight} x {GridDepth} produce {cellCount} cells, which does not fit a 32-bit cell key. " +
                $"Use a larger cell size or a smaller world.");
        }

        CellCount = (int)cellCount;
    }

    /// <summary>
    /// Build a configuration for a <b>flat</b> world — one cell deep on Z, which is how a 2D game expresses itself to a 3D grid (C16). Z coordinates outside
    /// the single cell clamp into it, which is exactly what the grid did for every entity before it gained a third axis.
    /// </summary>
    /// <param name="worldMin">World-space minimum corner on X and Y (inclusive). Z is taken as 0.</param>
    /// <param name="worldMax">World-space maximum corner on X and Y (exclusive).</param>
    /// <param name="cellSize">Cell size in world units; must be &gt; 0.</param>
    /// <param name="migrationHysteresisRatio">Per-axis dead zone as a fraction of cell size (default 0.05).</param>
    /// <param name="clusterTargetExtentRatio">Target cluster extent as a fraction of cell size — P4 (default 0.25).</param>
    /// <param name="clusterDriftMarginRatio">Intra-cell drift dead zone as a fraction of cell size (default 0.05).</param>
    /// <param name="clusterRepairExtentRatio">Extent past which a cell is nominated for a full re-sort — P7 (default 0.75).</param>
    /// <param name="reclusterBudgetMs">Per-tick repair budget in milliseconds; 0 disables repair (default 1.0).</param>
    /// <param name="repairNsPerEntity">Projected repair cost per entity in nanoseconds (default 1500, measured).</param>
    /// <param name="repairWorstClustersPerUnit">Clusters per repair unit; 0 means the whole cell (default 8).</param>
    /// <param name="clusterRepairCriticalExtentRatio">Degradation at which a cell jumps the queue regardless of budget; 0 disables it (default 1.0).</param>
    /// <param name="repairAgingRatePerTick">Rank growth per tick a candidate waits; 0 disables ageing (default 0.05).</param>
    /// <param name="repairQueueMaxCells">Hard cap on queued repair candidates (default 4096).</param>
    /// <param name="clusterTargetPackingSlack">
    /// Multiplier on the per-cell packing bound that sets the intra-cell target; 0 keeps the constant (default 1.5).
    /// </param>
    /// <param name="leastEnlargementPlacement">Place arrivals in the least-enlargement cluster of their cell (default false — opt-in).</param>
    /// <param name="growthCapPlacement">Open a fresh cluster when an arrival would stretch past the cap (default false).</param>
    /// <param name="growthCapSlack">Multiplier on the density target the cap allows (default 1.25).</param>
    /// <param name="maxOpenClustersPerCell">Open clusters the cap may hold per cell (default 4).</param>
    /// <param name="batchSpawnSortThreshold">Spawns per transaction above which the batch is placed in Morton order; 0 disables (default 128).</param>
    public static SpatialGridConfig Flat(Vector2 worldMin, Vector2 worldMax, double cellSize, float migrationHysteresisRatio = 0.05f,
        float clusterTargetExtentRatio = 0.25f, float clusterDriftMarginRatio = 0.05f, float clusterRepairExtentRatio = 0.75f,
        float reclusterBudgetMs = 1.0f, float repairNsPerEntity = 1500f, int repairWorstClustersPerUnit = 8,
        float clusterRepairCriticalExtentRatio = 1.0f, float repairAgingRatePerTick = 0.05f, int repairQueueMaxCells = 4096,
        float clusterTargetPackingSlack = 1.5f, bool leastEnlargementPlacement = false, bool growthCapPlacement = false, float growthCapSlack = 1.25f,
        int maxOpenClustersPerCell = 4, int batchSpawnSortThreshold = 128) =>
        new(new Vector3D(worldMin, 0d), new Vector3D(worldMax, cellSize), cellSize, migrationHysteresisRatio, clusterTargetExtentRatio,
            clusterDriftMarginRatio, clusterRepairExtentRatio, reclusterBudgetMs, repairNsPerEntity, repairWorstClustersPerUnit,
            clusterRepairCriticalExtentRatio, repairAgingRatePerTick, repairQueueMaxCells, clusterTargetPackingSlack, leastEnlargementPlacement,
            growthCapPlacement, growthCapSlack, maxOpenClustersPerCell, batchSpawnSortThreshold);
}
