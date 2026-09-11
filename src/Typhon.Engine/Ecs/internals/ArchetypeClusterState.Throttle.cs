using System;
using System.Diagnostics;

namespace Typhon.Engine.Internals;

/// <summary>
/// The re-clustering throttle — one budget, spent by policy, with memory (#872 step 11, design §5.6).
/// </summary>
/// <remarks>
/// <para><b>Why a controller at all.</b> Steps 10 and 12 shipped with their work effectively unbudgeted. Drift detection appended every drifter it found
/// and the fence executed all of them; the repair planner had a budget but compared it against a hand-set constant and forgot whatever it refused.
/// §5.6's decision is that "re-clustering runs to a budget, never to completion", because deferring a pass costs one tick of looser bounds while
/// overrunning the frame costs the frame.</para>
/// <para><b>The two mechanisms budget differently, and §5.6 says why.</b> Delta relocation is resumable — each drifter is independent, so the budget can
/// stop anywhere — and is budgeted in ENTITIES. A repair cannot be halved: a partly re-sorted cell has paid the cost and banked part of the benefit, so
/// it is budgeted in WHOLE UNITS and a unit the remaining budget cannot finish is never begun.</para>
/// <para><b>Everything here runs single-threaded, at the tail of Prep.</b> That is what makes <c>AC-11.6</c> true by construction rather than by making a
/// parallel decision reproducible: one thread, one pass, order-preserving.</para>
/// </remarks>
internal sealed partial class ArchetypeClusterState
{
    /// <summary>Weight of the newest sample in the per-entity cost estimators. See <see cref="ObserveMigrationCost(in SpatialGridConfig, double)"/>.</summary>
    /// <remarks>
    /// A constant rather than a knob. It sets how fast the model tracks a change in cost, and 0.25 settles a step change inside ten ticks while ignoring
    /// a single anomalous tick — a range where nothing a user could reasonably choose behaves differently enough to be worth a configuration surface.
    /// </remarks>
    private const double CostEwmaAlpha = 0.25d;

    /// <summary>Bounds on the per-entity estimate, as multiples of the configured <c>RepairNsPerEntity</c> seed.</summary>
    /// <remarks>
    /// A clamp, not a safety net for a broken measurement: one pathological tick — a page fault storm, a stop-the-world GC landing inside the bracket —
    /// would otherwise poison the model for the ten ticks the EWMA takes to forget it, and during those ticks the budget admits nothing at all. The band
    /// is wide enough that a genuinely different machine is tracked and narrow enough that an outlier cannot stop the feature.
    /// </remarks>
    private const double CostEstimateFloorFactor = 0.1d;

    /// <inheritdoc cref="CostEstimateFloorFactor"/>
    private const double CostEstimateCeilingFactor = 20d;

    /// <summary>Scale the repair planner's own per-entity cost is clamped against, in nanoseconds.</summary>
    /// <remarks>
    /// Separate from <c>RepairNsPerEntity</c> because the two measure different work by two orders of magnitude: moving an entity copies components,
    /// claims a slot and descends an index, while planning one is a Morton encode, a comparison and an array write. Sharing the migration band put a floor
    /// of a tenth of the migration seed under the planner term, which no measurement could get below.
    /// </remarks>
    private const double PlannerSeedNsPerEntity = 60d;

    /// <summary>
    /// The persistent, ranked set of cells waiting to be repaired. Created on first use, once the grid's knobs are known.
    /// </summary>
    internal CellRepairQueue RepairQueue;

    /// <summary>EWMA of the whole migration cost per entity moved, in nanoseconds. Zero until the first tick that moved something.</summary>
    private double _migrationNsPerEntityEwma;

    /// <summary>EWMA of the repair PLANNER's own cost per entity planned, in nanoseconds — gather, Morton sort, destination allocation.</summary>
    /// <remarks>
    /// Modelled separately because it is charged to a different phase and, unlike the migration cost, is repair's alone: no cell crossing and no
    /// relocation pays it. Step 12's projection ignored it entirely.
    /// </remarks>
    private double _plannerNsPerEntityEwma;

    /// <summary><see cref="Stopwatch"/> ticks the repair planner spent in the most recently completed tick, and the entities it planned.</summary>
    private long _lastTickPlannerTicks;

    /// <inheritdoc cref="_lastTickPlannerTicks"/>
    private int _lastTickPlannedEntities;

    /// <summary>
    /// The relocations the budget ADMITTED, lifted out of the pending queue before the mandatory requests are compacted over them.
    /// </summary>
    /// <remarks>
    /// Per-archetype and reused across ticks, so its steady state is one allocation. See <see cref="ApplyMigrationThrottle(SpatialGrid, double)"/> for why the copy is not
    /// optional: an in-place partition overwrites the entries a later pass has yet to read. It holds only the SURVIVORS — about 1 300 requests where it
    /// used to hold every candidate, 54 800 of them, at the 25 % reference point of the #872 matrix (#882).
    /// </remarks>
    private MigrationRequest[] _throttleScratch = [];

    /// <summary>
    /// Queue indices of this tick's relocation candidates — the throttle's working set while it decides which survive.
    /// </summary>
    /// <remarks>
    /// Four bytes per candidate against twenty for the request itself, and every decision the throttle makes about a relocation (is its source slot claimed,
    /// does the budget still stretch) reads the queue through one of these rather than a copy. Per-archetype and reused (#882).
    /// </remarks>
    private int[] _relocationIndices = [];

    /// <summary>Intra-cell relocations dropped by the throttle in the most recently completed tick.</summary>
    internal int LastTickRelocationsThrottled;

    /// <summary>
    /// Drifters detected in the most recently completed tick for which placement found no better cluster, and which were therefore left where they were.
    /// </summary>
    /// <remarks>
    /// The third term of <c>AC-11.7</c>'s identity — <c>DriftersDetected = admitted + throttled + unplaced</c>. Without it the identity cannot close and
    /// "no migration is both absorbed and throttled" stays an assertion rather than a check: a drifter that vanished because nowhere was better is
    /// indistinguishable, from the outside, from one the throttle silently swallowed.
    /// </remarks>
    internal int LastTickDriftersUnplaced;

    /// <summary>
    /// Wave-2 K2: the subset of <see cref="LastTickDriftersUnplaced"/> whose cell offered NO candidate at all — a single-cluster cell, or one whose
    /// every other cluster is full. Detection walked the cluster for nothing.
    /// </summary>
    internal int LastTickDriftersUnplacedNoCandidate;

    /// <summary>
    /// Drifters whose cell had candidates but no capacity left this pass, filed with <see cref="MigrationRequest.AnyCluster"/> so the drain's first-fit
    /// claim allocates a fresh cluster for them — the design's "allocate a new cluster if none qualifies", made explicit (step 14). Step 17's split
    /// replaces this with a median cut; until then the count says how often a cell overflows.
    /// </summary>
    internal int LastTickDriftersSpilled;

    /// <summary>
    /// Wave-2 K5: pinned claims (relocation or repair) rejected at drain time — a stale cluster identity or a full cluster — and therefore executed as
    /// first fit, which is <c>CR-02</c>'s fallback and the placement this subsystem exists to repair.
    /// </summary>
    internal int LastTickPinsRejected;

    /// <summary>Wave-2 K6: intra-cell relocations the throttle admitted into the drain prefix in the most recently completed tick.</summary>
    internal int LastTickRelocationsAdmitted;

    /// <summary>
    /// Mandatory cell-crossing requests the throttle found queued and charged. Repair requests are filed BEFORE the throttle runs (step 14) and are
    /// pre-charged by the planner, so they are neither counted here nor charged again — see <see cref="LastTickRepairedEntityCount"/>.
    /// </summary>
    internal int LastTickCrossingsQueued;

    /// <summary>Wave-2 K9: the budget the admitted relocations were charged, in nanoseconds — <c>admitted × estimate</c>.</summary>
    internal double LastTickRelocationSpendNs;

    /// <summary>
    /// Budget the relocation throttle had spent BEFORE the repair planner ran on a tick where the planner refused a unit for budget. Since step 14 the
    /// planner runs first, so this is zero by construction — a tripwire: any non-zero reading means the Prep order has regressed to relocations-first,
    /// which is the priority inversion §5.8.3 measured. Asserted by <c>ClusterDensityTargetTests</c>.
    /// </summary>
    internal double LastTickRepairBudgetStarvedNs;

    /// <summary>
    /// Safety-valve admissions in the most recently completed tick — repair units begun with insufficient budget because the cell was critical.
    /// </summary>
    internal int LastTickRepairValveFires;

    /// <summary>
    /// Source slots named by this tick's mandatory requests, keyed by cluster chunk id — the supersede filter's claim set (#877).
    /// </summary>
    /// <remarks>
    /// Per-archetype and reused, so a steady-state tick allocates nothing; cleared at the top of every throttle pass. Flat rather than hashed since #882:
    /// the filter probes it once per queued relocation — 54 800 times per tick at the reference point — and that was the largest single term in a step
    /// measured at 21 % of Prep. See <see cref="ClusterSlotClaimSet"/>.
    /// </remarks>
    private readonly ClusterSlotClaimSet _mandatorySourceSlots = new();

    /// <summary>
    /// Relocations dropped because a mandatory request already names the same source slot — the fourth term of <c>TH-02</c>'s identity (#877).
    /// </summary>
    /// <remarks>
    /// Distinct from <see cref="LastTickRelocationsThrottled"/> on purpose. A throttled relocation is one the BUDGET refused and is the signal that the
    /// budget is too small; a superseded one was refused by nothing — its entity is migrating anyway, under a request that outranks it. Folding the two
    /// would make <c>RelocationsThrottled</c> report budget pressure that does not exist, which is the number step 11's whole controller is tuned against.
    /// </remarks>
    internal int LastTickRelocationsSuperseded;

    // ══════════════════════════════════════════════════════════════════════════════════════════════════════════
    // Prep sub-spans (#872 — the measurement gate on claude/design/Runtime/11-prep-phase-optimisation.md §7)
    // ══════════════════════════════════════════════════════════════════════════════════════════════════════════
    //
    // Prep is the fence's dominant phase — 52 % of it at W = 8, 64 % at 128 000 entities — and until these existed
    // its INTERNAL split was unknown. The design document ranks its steps from what each one touches rather than
    // from what each one costs, which is precisely the mistake the prior document's R2 warned against one level up:
    // "you may be optimising a 100 µs slice of a 960 µs span".
    //
    // Deliberately plain `long` fields bracketed with Stopwatch.GetTimestamp(), following RecordPlannerCost and
    // AccrueQueueMaintenance directly below. NOT TyphonEvent spans: those are gate-conditional
    // (Gate = "RuntimeWriteTickFenceClusterActive") and measure nothing in a default Release run, and their
    // `using var` shape blocks JIT inlining. One timestamp pair is ~20 ns against a phase measured in milliseconds.

    // Two meanings since #886. On an atomic Prep every sub-span is WALL time on the one worker. On a sliced Prep, ② ③ ④ ⑤ are SUMMED across the slices
    // (CPU, Interlocked folds) while ① ⑥ ⑦ ⑧ stay wall — so on a sliced tick the four middle spans can exceed the phase's own span. Read them as work,
    // not as latency.

    /// <summary>① draining the change list — the locked read-modify-write per word, plus its allocation.</summary>
    internal long PrepSnapshotTicks;

    /// <summary>② masking the change list against live occupancy, and on the clean branch REBUILDING it.</summary>
    internal long PrepMaskTicks;

    /// <summary>③ replaying index key-changes parked during the tick.</summary>
    internal long PrepShadowTicks;

    /// <summary>④ recomputing per-cluster min/max summaries.</summary>
    internal long PrepZoneMapTicks;

    /// <summary>⑤ testing which changed entities left their cell.</summary>
    internal long PrepDetectTicks;

    /// <summary>⑥ spending the re-clustering budget over the pending queue.</summary>
    internal long PrepThrottleTicks;

    /// <summary>⑦ ranking the repair queue and planning units. Includes queue maintenance; see <see cref="RecordPlannerCost"/>.</summary>
    internal long PrepPlanTicks;

    /// <summary>
    /// Putting the drain prefix in destination-cell order (#910), between ⑦ and ⑧ — the sort the Migrate phase's Prepare ran before it moved here.
    /// </summary>
    internal long PrepSortTicks;

    /// <summary>⑧ pre-sizing the fence buffers — an O(all clusters) resize every tick.</summary>
    internal long PrepPreSizeTicks;

    /// <summary>Clusters whose change word survived the occupancy mask — the map's actual domain size.</summary>
    internal int PrepDirtyClusters;

    /// <summary>Reset every Prep sub-span. Called from the counter-reset preamble, beside every other per-tick counter.</summary>
    internal void ResetPrepSubSpans()
    {
        PrepSnapshotTicks = 0;
        PrepMaskTicks = 0;
        PrepShadowTicks = 0;
        PrepZoneMapTicks = 0;
        PrepDetectTicks = 0;
        PrepThrottleTicks = 0;
        PrepPlanTicks = 0;
        PrepSortTicks = 0;
        PrepPreSizeTicks = 0;
        PrepDirtyClusters = 0;
    }

    /// <summary>The per-entity cost the budget is being spent against, in nanoseconds. Published every tick, whether or not anything was admitted.</summary>
    /// <remarks>
    /// Published so a reader can tell a budget that bought little because work was dear from one that bought little because none was queued — which needs
    /// the estimate to exist on the quiet ticks too, so it is written by <see cref="ObserveMigrationCost(in SpatialGridConfig, double)"/> rather than by the admission path.
    /// </remarks>
    internal double LastTickMeasuredNsPerEntity;

    /// <summary>
    /// Fold this tick's nominations into the persistent queue without planning anything — the path an archetype whose Prep found no work takes.
    /// </summary>
    /// <remarks>
    /// Step 12 CLEARED the nominations here. That was correct for a per-tick list (a plan allocates clusters this tick's Migrate and Finalize must
    /// consume, and neither runs for an idle archetype) but it also threw away the evidence, so a cell that degraded on its last busy tick was never
    /// repaired. Absorbing keeps the evidence and plans nothing, which is the distinction step 12 had no structure to express.
    /// </remarks>
    internal void AbsorbRepairNominations(SpatialGrid grid, long tickNumber)
    {
        var nominations = RepairNominations;
        if (nominations.Count == 0)
        {
            return;
        }

        var queue = EnsureRepairQueue(grid);
        if (queue != null && CellClusterPool != null)
        {
            var start = Stopwatch.GetTimestamp();
            queue.Absorb(nominations, grid, this, tickNumber);
            AccrueQueueMaintenance(queue, start);
        }

        nominations.Clear();
    }

    /// <summary>Create the repair queue on first use, once the grid's knobs are known. Returns <c>null</c> when there is no grid to size it from.</summary>
    internal CellRepairQueue EnsureRepairQueue(SpatialGrid grid)
    {
        if (RepairQueue != null || grid == null)
        {
            return RepairQueue;
        }

        ref readonly var cfg = ref grid.Config;
        RepairQueue = new CellRepairQueue(cfg.RepairQueueMaxCells, cfg.RepairAgingRatePerTick);
        return RepairQueue;
    }

    /// <summary>
    /// One source slot, one request: the drain prefix must never name the same <c>(cluster, slot)</c> twice (<c>CR-05</c>, #877).
    /// </summary>
    /// <remarks>
    /// <para><b>DEBUG-only, for the same reason <c>BTree.AssertSortedAscending</c> is.</b> It is <c>O(prefix)</c> against a prefix whose entries each cost
    /// ~1 500 ns to execute, so the ratio is defensible — but it runs on Prep, single-threaded, on every archetype of every tick, and the release contract is
    /// upheld by the two producers rather than by this check. Debug enforces what Release maintains.</para>
    /// <para>It used to allocate a <c>Dictionary</c> sized to the prefix on every tick of every Debug run — the whole test suite's cost, for a check that
    /// fires approximately never. It now reuses a <see cref="ClusterSlotClaimSet"/> and pays the quadratic rescan only on the failing path, where the
    /// message's quality matters and the run is about to end anyway (#882).</para>
    /// <para><b>Why an assertion and not a filter.</b> A duplicate reaching here means one of the two exclusion points failed, and the entries carry no
    /// information about which of the pair is the correct one to keep — the throttle's supersede rule and the planner's exclusion map both encode a
    /// PRIORITY, and a late filter would have to guess it. Failing loudly at the point of detection is what turned #877 from three unrelated-looking
    /// symptoms (a missing entity, an invalid B+Tree child, an access violation) into one defect with one cause.</para>
    /// </remarks>
    [Conditional("DEBUG")]
    internal void AssertNoDuplicateMigrationSources(long tickNumber)
    {
        var prefix = PendingMigrationDrainCount;
        if (prefix < 2)
        {
            return;
        }

        var seen = _duplicateSourceGuard;
        seen.Clear();
        var queue = PendingMigrations;
        for (var i = 0; i < prefix; i++)
        {
            var chunkId = queue[i].SourceClusterChunkId;
            var slotIndex = queue[i].SourceSlotIndex;
            if ((seen.ClaimedSlots(chunkId) & (1UL << slotIndex)) != 0UL)
            {
                // The claim set records membership, not the index that claimed it. Recover the earlier one by rescan: quadratic, but only ever on the path
                // that is about to throw, and the index is what makes the message diagnosable.
                var first = 0;
                for (var j = 0; j < i; j++)
                {
                    if (queue[j].SourceClusterChunkId == chunkId && queue[j].SourceSlotIndex == slotIndex)
                    {
                        first = j;
                        break;
                    }
                }

                throw new InvalidOperationException(
                    $"CR-05 violated on tick {tickNumber}: pending migrations {first} ({queue[first].Kind}) and {i} ({queue[i].Kind}) both name source "
                    + $"cluster {chunkId} slot {slotIndex}. ExecuteMigrations' stale-source guard tests OCCUPANCY, not "
                    + "identity, so whichever drains second migrates whatever occupies the slot by then — see #877.");
            }

            seen.Claim(chunkId, slotIndex);
        }
    }

    /// <summary>Reused membership set for <see cref="AssertNoDuplicateMigrationSources"/>. DEBUG-only in effect; the field costs one reference in
    /// Release.</summary>
    private readonly ClusterSlotClaimSet _duplicateSourceGuard = new();

    /// <summary>Reset the throttle's per-tick state. Called from the fence's counter-reset block, beside every other per-tick counter.</summary>
    internal void ResetThrottleTickState()
    {
        LastTickRelocationsThrottled = 0;
        LastTickRelocationsSuperseded = 0;
        LastTickDriftersUnplaced = 0;
        LastTickRepairValveFires = 0;
        LastTickDriftersUnplacedNoCandidate = 0;
        LastTickDriftersSpilled = 0;
        LastTickPinsRejected = 0;
        LastTickRelocationsAdmitted = 0;
        LastTickCrossingsQueued = 0;
        LastTickRelocationSpendNs = 0d;
        LastTickRepairBudgetStarvedNs = 0d;

        // Not LastTickMeasuredNsPerEntity: ObserveMigrationCost republishes it on the very next line of the caller, and zeroing it here would leave it at
        // zero for any archetype that has no grid to observe against — which reads as "the model has no estimate" rather than "there is no model".

        if (RepairQueue != null)
        {
            RepairQueue.LastTickMaintenanceTicks = 0;
        }
    }

    /// <summary>
    /// Fold the previous tick's measurements into the cost model. Called once per archetype, at the top of Prep, BEFORE the counters are zeroed.
    /// </summary>
    /// <remarks>
    /// <para><b>The numerator is the whole migration, and getting that wrong was the trap.</b> <see cref="LastTickMigrationExecuteMs"/> brackets only the
    /// migrant loop — which since step 6 merely STAGES the index update and since step 7 the EntityMap patch. Both applies happen in later phases, and the
    /// secondary index alone was measured at ~48 % of a migration's cost. An estimator built on the loop alone under-admits by about half in the wrong
    /// direction: it thinks migrations are cheap and admits twice the work the budget can pay for. <see cref="LastTickMigrationTotalMs"/> is the corrected
    /// sum, and step 11 added the two missing timers to produce it.</para>
    /// <para><b>Blended across the three migration kinds</b>, and documented as such. Splitting it needs per-class attribution inside the apply phases,
    /// where the staged records carry no kind at all. A blended MEASURED number is nonetheless strictly better than a hand-set constant that was 22x to
    /// 117x off in AC-12.7's own measurement, which is the failure this replaces.</para>
    /// <para><b>Only updated on a tick that moved something.</b> A quiet tick has no sample; folding its zero in would decay the model toward zero and
    /// admit unbounded work on the tick after a lull.</para>
    /// <para><b>An EWMA cannot ring, and that is the point (<c>AC-11.4</c>).</b> The estimate is PER ENTITY, so admitting fewer entities does not change
    /// it — there is no feedback path from the admission decision back into the measurement, and a first-order low-pass with no feedback converges
    /// monotonically. A debt or credit term would create exactly the loop the AC forbids, on an actuator that at the default budget admits one unit or
    /// zero.</para>
    /// </remarks>
    internal void ObserveMigrationCost(in SpatialGridConfig config) => ObserveMigrationCost(in config, 1d);

    /// <param name="config">The grid configuration carrying the seed.</param>
    /// <param name="parallelism">
    /// How many workers' worth of CPU the fence's migration phases ran per unit of span last tick — CPU ticks ÷ span ticks, ≥ 1, published by the
    /// runtime; <c>1</c> on the serial fence (step 14, D2). <see cref="LastTickMigrationTotalMs"/> is CPU summed across workers, and the budget it is
    /// spent against is a FRAME budget, which is spent in span: charging a migration at its summed CPU over-billed it by the fence's parallel speed-up
    /// (measured 5–15×), which is why an 8 ms budget bought one valve unit.
    /// </param>
    internal void ObserveMigrationCost(in SpatialGridConfig config, double parallelism)
    {
        var seed = config.RepairNsPerEntity > 0f ? config.RepairNsPerEntity : 1500f;

        var migrated = LastTickMigrationCount;
        if (migrated > 0)
        {
            var divisor = parallelism > 1d ? parallelism : 1d;
            var sample = LastTickMigrationTotalMs * 1_000_000d / migrated / divisor;
            _migrationNsPerEntityEwma = Blend(_migrationNsPerEntityEwma, sample, seed);
        }

        if (_lastTickPlannedEntities > 0)
        {
            // Clamped against the PLANNER's own scale, not the migration seed's. Sharing the band floored a planner that costs tens of nanoseconds per
            // entity at a tenth of the migration seed — 150 ns at the default — which is a fixed surcharge on every repair projection that no measurement
            // could ever remove.
            var sample = _lastTickPlannerTicks * 1_000_000_000d / Stopwatch.Frequency / _lastTickPlannedEntities;
            _plannerNsPerEntityEwma = Blend(_plannerNsPerEntityEwma, sample, PlannerSeedNsPerEntity);
        }

        _lastTickPlannerTicks = 0;
        _lastTickPlannedEntities = 0;

        // Published HERE, not as a side effect of the admission decision, and the difference is observable.
        //
        // It used to be written inside RepairCostEstimateNs, which the throttle calls only after establishing that it has requests to weigh — so on any
        // tick with an empty pending queue the telemetry reported ZERO rather than the model's current value. A consumer dividing the budget by it to
        // reproduce the decision then gets +Infinity, and in C# a float-to-int conversion saturates rather than throwing, so the bound silently becomes
        // int.MaxValue. This method runs once per archetype per tick unconditionally, which is what the field's documentation already claimed.
        LastTickMeasuredNsPerEntity = RepairCostEstimateNs(in config);
    }

    /// <summary>One EWMA step, clamped to a band around the configured seed.</summary>
    private static double Blend(double current, double sample, double seed)
    {
        var floor = seed * CostEstimateFloorFactor;
        var ceiling = seed * CostEstimateCeilingFactor;
        var clamped = sample < floor ? floor : (sample > ceiling ? ceiling : sample);
        return current <= 0d ? clamped : (current * (1d - CostEwmaAlpha)) + (clamped * CostEwmaAlpha);
    }

    /// <summary>
    /// The per-entity cost a repair unit is projected against: the measured migration cost plus the measured planner cost, falling back to the configured
    /// seed until the first measurement exists.
    /// </summary>
    private double RepairCostEstimateNs(in SpatialGridConfig config) => MigrationCostEstimateNs(in config) + _plannerNsPerEntityEwma;

    /// <summary>
    /// The measured cost of MOVING one entity, without the repair planner's own overhead — what a relocation is charged.
    /// </summary>
    /// <remarks>
    /// <b>A relocation must not be charged the planner's gather-and-sort, and charging it was under-admitting relocations on every tick.</b> The planner
    /// term exists because a repair pays for a Morton sort and a destination allocation that no cell crossing and no drift relocation ever touches. Adding
    /// it to the throttle's per-entity price inflated the cost of exactly the mechanism that does not incur it — and the inflation had a floor rather than
    /// tapering away, because <see cref="Blend"/> clamps the planner EWMA into the MIGRATION seed's band and so never lets it fall below a tenth of that
    /// seed however cheap the planner actually is. Two errors pushing the same way.
    /// </remarks>
    private double MigrationCostEstimateNs(in SpatialGridConfig config)
    {
        var seed = config.RepairNsPerEntity > 0f ? config.RepairNsPerEntity : 1500f;
        return _migrationNsPerEntityEwma > 0d ? _migrationNsPerEntityEwma : seed;
    }

    /// <summary>
    /// Relocation nominations this tick's drift scan has already produced. Reset by <c>ClearAabbRefreshBookkeeping</c>.
    /// </summary>
    internal int DriftNominationsThisTick;

    /// <summary>
    /// How many relocation nominations the drift scan may produce before it stops looking — <c>0</c> meaning "no limit".
    /// </summary>
    /// <remarks>
    /// <para><b>The measurement this exists for.</b> At the 25 % reference point the scan nominated <b>55 415</b> relocations per tick and
    /// <see cref="ApplyMigrationThrottle(SpatialGrid, double)"/> admitted <b>1 288</b> — a ratio of 43 : 1. Every one of the 54 127 rejects had already paid
    /// <c>BuildRelocationCandidates</c>, three <c>AxisOvershoot</c> calls, a <c>ChooseRelocationTarget</c> descent over the cell's candidates (367 473 calls
    /// per traced run) and a 20-byte append, and then went through four more full passes inside the throttle. The work was not merely wasted; it was the
    /// largest single term in two different phases.</para>
    /// <para><b>Why capping is EXACT here rather than an approximation.</b> The throttle does not rank relocations — <c>TH-01</c> requires it to preserve
    /// queue order within each class, so it admits a PREFIX of them. A cap of <c>C</c> nominations therefore yields the identical admitted set as long as
    /// <c>C</c> is at least what the budget can pay for, and the cap below is deliberately <b>double</b> that plus a floor. Ordering across parallel slices
    /// was already nondeterministic (each worker appends its own buffer and bulk-enqueues), so nothing that was previously guaranteed is lost.</para>
    /// <para><b>Zero budget still means no enforcement</b>, matching <see cref="ApplyMigrationThrottle(SpatialGrid, double)"/>'s own reading of
    /// <c>ReclusterBudgetMs = 0</c>: the fixtures that pin drift behaviour set exactly that, and they must keep seeing every drifter the oracle sees.</para>
    /// </remarks>
    internal int ComputeDriftNominationCap(in SpatialGridConfig cfg)
    {
        var budgetNs = cfg.ReclusterBudgetMs * 1_000_000d;
        if (budgetNs <= 0d)
        {
            return 0;
        }

        var estimateNs = MigrationCostEstimateNs(in cfg);
        if (estimateNs <= 0d)
        {
            return 0;
        }

        // x2 for the crossings that take budget ahead of relocations and for the EWMA drifting under the true cost, +256 so a tiny budget still lets the
        // mechanism run at all. At the reference point this is ~2 800 against 55 415 produced today.
        var affordable = budgetNs / estimateNs;
        var cap = (int)Math.Min(affordable * 2d + 256d, int.MaxValue);
        return cap > 0 ? cap : 0;
    }

    /// <summary>Record one repair-planning pass so the next tick's <see cref="ObserveMigrationCost(in SpatialGridConfig, double)"/> has a sample.</summary>
    /// <remarks>
    /// <b>Queue maintenance is subtracted, not included.</b> The caller brackets the whole of <c>PlanCellRepairs</c>, which contains the absorb and the
    /// re-rank — and those are already reported separately as <c>RepairQueueMaintenanceMs</c> for <c>AC-11.5</c>. Counting them here as well would inflate
    /// the per-entity planner estimate, which reduces admissions, which is the queue's own overhead quietly making the queue less useful.
    /// </remarks>
    internal void RecordPlannerCost(long elapsedTicks, int entitiesPlanned)
    {
        var maintenance = RepairQueue?.LastTickMaintenanceTicks ?? 0L;
        var net = elapsedTicks - maintenance;
        _lastTickPlannerTicks += net > 0L ? net : 0L;
        _lastTickPlannedEntities += entitiesPlanned;
    }

    /// <summary>Charge the queue's own maintenance to <c>AC-11.5</c>'s counter.</summary>
    private static void AccrueQueueMaintenance(CellRepairQueue queue, long startTimestamp)
    {
        if (queue != null)
        {
            queue.LastTickMaintenanceTicks += Stopwatch.GetTimestamp() - startTimestamp;
        }
    }

    /// <summary>
    /// Spend one tick's re-clustering budget over the pending migration queue: keep every mandatory request, keep as many intra-cell relocations as the
    /// budget affords, and DROP the rest. Returns the budget left for the repair planner, in nanoseconds.
    /// </summary>
    /// <remarks>
    /// <para><b>Truncation, not deferral, and the distinction is the difference between this being a four-line change and a rule rewrite.</b> <c>CR-01</c>
    /// states that Migrate executes exactly <c>PendingMigrations[0 .. P)</c> where <c>P</c> is the count at Prep's return, and records what happens when
    /// that prefix is too small: "executed requests stay queued and re-execute against slots their entities have left, and the queue grows without bound"
    /// — measured at 224 854 migrations on the twentieth tick. Deferring the tail would reopen it, and would also need the serial fence (which passes
    /// <c>PendingMigrationCount</c>, not the prefix) taught about the split — as the destination-cell sort needed to be until #910 narrowed it to the
    /// prefix. Lowering the COUNT leaves prefix == count, so
    /// nothing downstream changes and <c>CR-01</c> stays true verbatim.</para>
    /// <para><b>A dropped relocation is not a lost one, and is arguably the better outcome.</b> Its <c>DestClusterChunkId</c> was the least-enlargement
    /// choice against the AABBs of the tick that detected it, which this tick's own migrations have since moved; <c>TryClaimPinnedSlot</c> would reject
    /// the stale pin (<c>CR-02</c>) and fall back to the first fit this whole issue exists to repair. Re-detecting next tick recomputes the choice against
    /// bounds that are current.</para>
    /// <para><b>Cell crossings are charged but never refused.</b> §5.7 makes them correctness — an entity whose position left its cell must move — so a
    /// heavy crossing tick starves repair rather than the other way round. That is deliberate: correctness first, selectivity second.</para>
    /// <para><b>Stable within each class.</b> The partition walks the queue in encounter order and never sorts, so the surviving order is exactly the order
    /// the detectors produced.<br/>Since #882 it is: one classifying pass recording relocations by INDEX, two passes over those indices to decide which
    /// survive, a lift-out of the survivors alone, and only then the compaction — see the step comments in the body, where the ORDER of the five is the
    /// invariant. <c>AC-11.6</c> needs that; a partition that reordered would make the admitted set
    /// depend on the partition's internals.</para>
    /// </remarks>
    internal double ApplyMigrationThrottle(SpatialGrid grid) => ApplyMigrationThrottle(grid, 0d);

    /// <summary>
    /// The cost the queue's mandatory requests will charge — every kind but <see cref="MigrationKind.Relocation"/> and <see cref="MigrationKind.Repair"/> —
    /// at the current per-entity estimate. What the planner is handed as its budget is the configured budget minus this (step 14, D2).
    /// </summary>
    internal double PendingMandatoryCostNs(in SpatialGridConfig cfg)
    {
        var count = PendingMigrationCount;
        if (count == 0)
        {
            return 0d;
        }

        var queue = PendingMigrations;
        var mandatory = 0;
        for (var i = 0; i < count; i++)
        {
            var kind = queue[i].Kind;
            if (kind != MigrationKind.Relocation && kind != MigrationKind.Repair)
            {
                mandatory++;
            }
        }

        return mandatory * MigrationCostEstimateNs(in cfg);
    }

    /// <param name="grid">The grid whose configuration carries the budget.</param>
    /// <param name="preChargedNs">
    /// What the repair planner already committed this tick, in nanoseconds (step 14, D2). Subtracted from the budget before anything is charged, and
    /// the planner's own <see cref="MigrationKind.Repair"/> requests are then admitted without being charged a second time — they remain mandatory.
    /// </param>
    internal double ApplyMigrationThrottle(SpatialGrid grid, double preChargedNs)
    {
        if (grid == null)
        {
            return 0d;
        }

        ref readonly var cfg = ref grid.Config;
        var budgetNs = cfg.ReclusterBudgetMs * 1_000_000d;
        if (budgetNs <= 0d)
        {
            // Zero means NO BUDGET ENFORCEMENT, not "do no re-clustering", and the distinction is not a nicety.
            //
            // `ReclusterBudgetMs = 0` already had a documented meaning before step 11 — it disables REPAIR, because a whole-unit admission needs a positive
            // budget to project against — and step-10 fixtures set it precisely to isolate relocation from repair. Extending it to also drop every
            // relocation would silently change what an existing knob does: a user who turned repair off would lose the steady-state path as well, and the
            // engine would quietly revert to the first-fit placement this whole issue exists to repair. Three step-10 tests caught exactly that.
            //
            // AC-11.8 is still satisfied, and by the same reasoning: relocations stay bounded by DETECTION (one per drifter found in clusters written this
            // tick), the repair queue keeps absorbing and evicting at its cap, nothing is deferred, and nothing deadlocks. Throttling to zero is available
            // to anyone who wants it — set a budget below one entity's cost — it is simply not what zero means.
            return 0d;
        }

        var count = PendingMigrationCount;
        if (count == 0)
        {
            return budgetNs;
        }

        // The MIGRATION cost, not the repair estimate: a relocation moves an entity and pays nothing for the planner. See MigrationCostEstimateNs.
        var estimateNs = MigrationCostEstimateNs(in cfg);
        if (estimateNs <= 0d)
        {
            return budgetNs;
        }

        var queue = PendingMigrations;
        var remainingNs = budgetNs - preChargedNs;

        // -- The classes are SEPARATED before anything is compacted, and the ORDER of the five steps is the invariant --
        //
        // The obvious in-place two-pass partition is WRONG, silently, and loses data with an infinite budget. Pass one// compacts the mandatory entries to
        // the front; pass two then re-scans the SAME array for relocations — but every index below the compaction cursor has already been overwritten by a
        // crossing. Traced on [R0, C1]: pass one writes C1 over index 0, pass two sees two crossings, and R0 is gone with nothing counted as throttled.
        //
        // The queue's own layout makes that the normal case rather than a corner. AabbRefresh appends this tick's relocations first and DetectClusterMigrations
        // appends next tick's crossings behind them, so relocations occupy the low indices and exactly min(relocations, crossings) of them are destroyed —
        // every one, on any tick with at least as many crossings as drifters, with RelocationsThrottled reading zero throughout. Step 10's placement would
        // revert to first fit on precisely the busy ticks it matters most on. TH-01 names this as an on_violation, and
        // RelocationsSurviveATickThatAlsoCarriesCellCrossings is the arm that catches it end to end.
        //
        // THE ORDERING BELOW IS WHAT KEEPS THAT SAFE, and it is not visible from any single step. Every read of a relocation's request happens BEFORE the
        // compaction writes over the queue: steps 1-3 decide entirely from INDICES, step 4 lifts the survivors out, and only step 5 compacts. Moving step 5
        // earlier reintroduces the measured bug exactly.
        //
        // What changed in #882: the lift-out used to copy the whole relocation set — 54 800 twenty-byte requests at the 25 % reference point of the #872 matrix,
        // 1.1 MB written per tick, to admit about 1 300 of them. The decisions need only each candidate's queue INDEX, so the copy is now four bytes per
        // candidate and twenty per survivor.
        if (_relocationIndices.Length < count)
        {
            _relocationIndices = new int[Math.Max(count, Math.Max(16, _relocationIndices.Length * 2))];
        }

        // Declared out here because step 5 needs them; the INDEX list deliberately is not — see the closing brace below.
        var mandatory = 0;
        var crossings = 0;
        var superseded = 0;
        var throttled = 0;
        var admittedRelocations = 0;
        MigrationRequest[] survivors;

        {
            // -- 1. Classify in ONE pass. Mandatory requests are counted and charged; relocations are remembered by index --
            //
            // A crossing charges the budget even when that drives it negative — it cannot be refused, so the only honest
            // accounting is to let it consume what it costs and leave repair with nothing.
            //
            // The source slots are recorded as they go by, for the supersede filter below (#877).
            _mandatorySourceSlots.Clear();
            var relocIndices = _relocationIndices;
            var relocationCount = 0;
            for (var i = 0; i < count; i++)
            {
                ref readonly var request = ref queue[i];
                if (request.Kind == MigrationKind.Relocation)
                {
                    relocIndices[relocationCount++] = i;
                    continue;
                }

                _mandatorySourceSlots.Claim(request.SourceClusterChunkId, request.SourceSlotIndex);
                mandatory++;
                if (request.Kind != MigrationKind.Repair)
                {
                    // A repair request was charged by the planner that filed it (preChargedNs); charging it again here would bill the same move twice.
                    remainingNs -= estimateNs;
                    crossings++;
                }
            }

            // -- 2. A relocation whose entity is already leaving under a mandatory request is DROPPED, not admitted (#877) --
            //
            // Drift detection runs in AabbRefresh, so its relocations are decided by the NEXT tick's Prep — and by then the
            // crossing detector may have filed a CellCrossing for the same entity, because an entity that drifted to the edge
            // of its cell is exactly the one most likely to leave it. Both requests then name the same source slot.
            //
            // ExecuteMigrations does not catch this. Its stale-source guard is an OCCUPANCY test, not an identity test
            // (srcOcc bit clear — ClusterMigration.cs step 0), so it only saves the case where the slot is still empty when
            // the second request drains. The crossing runs first, frees the slot, and any later ClaimSlotInCell in the same
            // pass may hand that slot to an unrelated migrant — whereupon the relocation moves THAT entity to a destination
            // chosen for someone else, and its EntityMap entry and index rows point at a slot it does not occupy. Measured:
            // "Entity(...) not found or not visible" and "B+Tree bulk update reached an invalid child", 6 of 12 seeds at the
            // default budget.
            //
            // Dropping is the right resolution and not merely the cheap one, for the reason T1 already gives for dropping
            // rather than deferring: the relocation's DestClusterChunkId was the least-enlargement choice against LAST tick's
            // AABBs, for an entity that has since left the cell entirely. Re-detecting after the crossing lands is more
            // correct than executing a placement computed for a cell the entity no longer lives in.
            //
            // Only mandatory requests can supersede. Two relocations cannot name one slot — detection visits each slot of
            // each cluster once, and a throttled relocation is dropped rather than carried — so the queue never holds two.
            if (_mandatorySourceSlots.Count > 0)
            {
                var kept = 0;
                for (var r = 0; r < relocationCount; r++)
                {
                    ref readonly var request = ref queue[relocIndices[r]];
                    var claimed = _mandatorySourceSlots.ClaimedSlots(request.SourceClusterChunkId);
                    if ((claimed & (1UL << request.SourceSlotIndex)) != 0UL)
                    {
                        superseded++;
                        continue;
                    }

                    relocIndices[kept++] = relocIndices[r];
                }

                relocationCount = kept;
            }

            // -- 3. Spend what is left, in encounter order. Decides HOW MANY survive; copies nothing ----------------------
            for (var r = 0; r < relocationCount; r++)
            {
                if (remainingNs < estimateNs)
                {
                    throttled = relocationCount - r;
                    break;
                }

                admittedRelocations++;
                remainingNs -= estimateNs;
            }

            // -- 4. Lift the SURVIVORS out — the only entries step 5 could destroy ----------------------------------------
            if (_throttleScratch.Length < admittedRelocations)
            {
                _throttleScratch = new MigrationRequest[Math.Max(admittedRelocations, Math.Max(16, _throttleScratch.Length * 2))];
            }

            survivors = _throttleScratch;
            for (var r = 0; r < admittedRelocations; r++)
            {
                survivors[r] = queue[relocIndices[r]];
            }

            // `relocIndices` and `relocationCount` DIE HERE, and the brace is the point. Past it the queue is rewritten, so an index into it means something
            // else entirely — reading one would reproduce TH-01's measured failure, min(relocations, crossings) entries destroyed with RelocationsThrottled
            // reading zero. A comment asking a future editor not to is weaker than the compiler refusing.
        }

        // -- 5. Only NOW compact the mandatory requests to the front, then append the survivors behind them ------------
        var admitted = 0;
        for (var i = 0; i < count; i++)
        {
            if (queue[i].Kind != MigrationKind.Relocation)
            {
                queue[admitted++] = queue[i];
            }
        }

        // A tripwire, not a check: steps 1-4 write nothing to the queue, so today this cannot fail for any input. It exists so that an edit which DOES
        // write to the queue before step 5 is caught here rather than by CR-01's unbounded-queue symptom twenty ticks later.
        Debug.Assert(admitted == mandatory, "step 5 found a different mandatory set than step 1 counted — something before it now writes to the queue");
        Debug.Assert(admitted + admittedRelocations <= count, "the throttle may only ever shorten the queue");

        for (var r = 0; r < admittedRelocations; r++)
        {
            queue[admitted++] = survivors[r];
        }

        PendingMigrationCount = admitted;
        LastTickRelocationsThrottled = throttled;
        LastTickRelocationsSuperseded = superseded;
        LastTickRelocationsAdmitted = admittedRelocations;
        LastTickCrossingsQueued = crossings;
        LastTickRelocationSpendNs = admittedRelocations * estimateNs;
        return remainingNs > 0d ? remainingNs : 0d;
    }
}
