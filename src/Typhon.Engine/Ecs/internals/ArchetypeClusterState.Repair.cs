using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Internals;

/// <summary>
/// The repair path — a full Morton re-sort and re-pack of a degraded cell (#872 step 12, design §5.2 and §5.6).
/// </summary>
/// <remarks>
/// <para><b>Two mechanisms, not one.</b> Step 10's detect-and-relocate is a per-tick <i>delta</i>: it moves the entities that drifted and leaves the rest
/// alone, which is affordable every tick and converges only while the layout is nearly right. This is the <i>repair</i>: it discards the existing layout of
/// a unit entirely, sorts its entities by position and re-packs them in sort order. Optimal tightness for a given cluster size, ~6 ms for a 100 K-entity
/// cell, and therefore rare by construction.</para>
/// <para><b>Why the delta path cannot do this.</b> Relocation is greedy and local — each drifter goes to whichever existing cluster grows least. A cell whose
/// clusters are ALL wrong has no good destination to offer, so the greedy step shuffles entities between bad boxes. Measured on this branch at step 10: a
/// randomly-laid-out cell produced ~26 relocations per entity over 30 ticks and the mean extent still rose. The re-sort is what recovers such a cell, and it
/// is the only thing in the engine that <b>shrinks a zone map</b> — <c>ZoneMapArray.Widen</c> is the sole other writer and it never narrows.</para>
/// <para><b>Where it runs, and why there.</b> Nomination happens in AabbRefresh, off the same <c>cellSize x 1.2</c> extent check the outlier guard already
/// computes (design parameter P7) — the extents are in registers, so nominating costs one list append on a cluster that has already failed a threshold.
/// Planning happens in <b>Prep</b> of the following tick, which is single-threaded per archetype, and the requests it emits are executed by that same tick's
/// Migrate phase. Everything the plan allocates is therefore created and consumed inside one exclusive fence window, so nothing OUTSIDE the fence — no
/// user-system spawn — can claim into a destination the sort reserved, and the plan is produced by exactly one thread, which is what makes <c>AC-12.4</c>
/// true by construction rather than by scheduling luck. Inside the window a same-tick migration that overflows its own pinned cluster can still first-fit
/// into one of these; that costs the repair a slot and is handled by the fallback chain, not by exclusivity.</para>
/// <para><b>Deviation from §5.2, stated deliberately.</b> The design's cost table gives the repair path "claim / release slot: <b>none</b>", on the grounds
/// that a re-pack knows its destinations and need not claim them. This implementation instead emits ordinary <see cref="MigrationRequest"/>s carrying a
/// pinned cluster AND a pinned slot, and lets <c>ExecuteMigrations</c> move them. The claim it pays is one uncontended CAS on a cache line the copy is about
/// to touch anyway. What it buys is that the ~400 lines maintaining <c>EntityMap</c> keying, the C15 cell-relative rebase, the H1 visibility fold, index
/// element ids, zone maps, dirty-bit deltas and orphan rollback are not duplicated for a path that runs rarely — so <c>AC-12.3</c> holds because the moves go
/// through the pipeline whose invariants are already tested, rather than because a second copy was got right. <c>AC-12.7</c>'s measurement is what
/// adjudicates whether the CAS ever mattered.</para>
/// </remarks>
internal sealed unsafe partial class ArchetypeClusterState
{
    /// <summary>Bits per axis in an intra-cell Morton key. Three axes at 21 bits fill 63 of a <see cref="ulong"/>'s 64.</summary>
    /// <remarks>
    /// <para><b>This is not the Morton step 8 deleted.</b> That one keyed the GRID — cell keys — and was removed because a 32-bit 3D Morton code caps the
    /// world at 1 024 cells per axis while the root is a hash map that gains nothing from key locality (see <c>SpatialGridConfig</c> and
    /// <c>VdbBlockKey</c>). This one orders entities WITHIN one cell, where the coordinate range is one cell wide by construction and the world-size
    /// objection does not arise. The two share a name and nothing else.</para>
    /// <para>21 bits resolves one cell into 2 097 152 steps per axis. At any cell size a float position is worth encoding at, that is far below the
    /// quantisation the sort could distinguish, so the key never loses an ordering the geometry actually expresses.</para>
    /// </remarks>
    internal const int MortonBitsPerAxis = 21;

    /// <summary>
    /// Clusters in a safety-valve unit when the configuration asks for the whole cell — the bound on the one budget overshoot the engine permits.
    /// </summary>
    /// <remarks>
    /// Matches <c>SpatialGridConfig.RepairWorstClustersPerUnit</c>'s default, so a valve admission costs what an ordinary unit costs. It cannot simply
    /// READ that setting, because this branch exists precisely for the configuration where it is <c>0</c> ("the whole cell") and a whole cell is exactly
    /// the unbounded overshoot the cap exists to prevent: at 64 slots a cluster, eight clusters is 512 entities, against the 100 K a large cell could hold.
    /// <para>The agreement with that default is asserted by <c>ClusterRepairQueueTests.TheValveCapMatchesTheDefaultUnitSize</c>, because a duplicated
    /// constant whose only tie to its twin is a sentence in a comment drifts the first time somebody tunes one of them.</para>
    /// </remarks>
    internal const int ValveClustersPerUnit = 8;

    /// <summary>One entity's place in the sort: its intra-cell Morton key, and where it currently lives.</summary>
    /// <remarks>
    /// <b>Ordered by key then by source location, and the tie-break is load-bearing.</b> Two entities at the same quantised position produce the same key,
    /// and an introsort left to resolve that tie picks by pivot placement — deterministic for a given input, but only as a property of the current BCL
    /// implementation. <c>AC-12.4</c> asserts identical output regardless of worker count and scheduling, which deserves a total order defined here rather
    /// than inherited from a sort's internals. <see cref="SourceLocation"/> is unique across the unit by construction — it is <c>chunkId * 64 + slot</c>,
    /// the engine's own <c>ClusterLocation</c> encoding — so (key, location) is total.
    /// </remarks>
    internal readonly struct RepairEntry : IComparable<RepairEntry>
    {
        /// <summary>Intra-cell Morton code of the entity's centre — see <see cref="EncodeIntraCellMorton"/>.</summary>
        internal readonly ulong MortonKey;

        /// <summary>The entity's current <c>clusterChunkId * 64 + slotIndex</c>.</summary>
        /// <remarks>
        /// <b>A <see cref="long"/>, and not for the range it needs today.</b> The engine's own <c>ClusterLocation</c> encoding is a 32-bit
        /// <c>chunkId * 64 + slot</c>, which overflows at chunk id 2^25 — 33.5 M clusters, 2.1 G entities in one archetype. That is a real ceiling rather
        /// than an absurd one at the scale this issue targets, and here it costs nothing to lift: <c>RepairEntry</c> is a <c>ulong</c> plus an <c>int</c>,
        /// which pads to 16 bytes anyway, so the wider field is free. Narrowing back to the 32-bit form happens at the point the request is built, where
        /// the value is a real cluster location again.
        /// </remarks>
        internal readonly long SourceLocation;

        internal RepairEntry(ulong mortonKey, long sourceLocation)
        {
            MortonKey = mortonKey;
            SourceLocation = sourceLocation;
        }

        /// <inheritdoc />
        public int CompareTo(RepairEntry other)
        {
            var byKey = MortonKey.CompareTo(other.MortonKey);
            return byKey != 0 ? byKey : SourceLocation.CompareTo(other.SourceLocation);
        }
    }

    /// <summary>One candidate cluster for a repair unit: how bad it is, and which cluster it is.</summary>
    /// <remarks>
    /// Ranked worst-first on <see cref="MaxExtent"/>, ties broken on <see cref="ChunkId"/> for the same total-order reason as <see cref="RepairEntry"/>.
    /// </remarks>
    private readonly struct RepairCandidate : IComparable<RepairCandidate>
    {
        internal readonly float MaxExtent;

        internal readonly int ChunkId;

        internal RepairCandidate(float maxExtent, int chunkId)
        {
            MaxExtent = maxExtent;
            ChunkId = chunkId;
        }

        /// <inheritdoc />
        public int CompareTo(RepairCandidate other)
        {
            var byExtent = other.MaxExtent.CompareTo(MaxExtent);
            return byExtent != 0 ? byExtent : ChunkId.CompareTo(other.ChunkId);
        }
    }

    /// <summary>
    /// One cluster's vote that its cell needs repairing, and how badly (#872 step 11).
    /// </summary>
    /// <remarks>
    /// <para><b>The degradation rides along because it is free here and expensive later.</b> The AabbRefresh pass has the cluster's fresh bound in
    /// registers at the moment it decides to nominate; the planner, a phase later, would have to re-read every cluster of every candidate cell to recover
    /// the same number. Step 12 discarded it and paid exactly that — its cell order was <c>Array.Sort</c> on the cell KEY, which is arbitrary with respect
    /// to how much repairing any of them would buy.</para>
    /// <para>A ratio, not an extent, so it is comparable across grids and directly comparable against
    /// <c>SpatialGridConfig.ClusterRepairExtentRatio</c> and <c>ClusterRepairCriticalExtentRatio</c> without carrying the cell size alongside.</para>
    /// </remarks>
    internal readonly struct RepairNomination
    {
        internal RepairNomination(int cellKey, float degradation)
        {
            CellKey = cellKey;
            Degradation = degradation;
        }

        /// <summary>The cell whose clusters need re-packing. The CELL, not the cluster: a repair unit is a cell's worst clusters, and which those are is a
        /// ranking the planner performs over the whole cell rather than over whichever clusters one slice happened to hold.</summary>
        internal int CellKey { get; }

        /// <summary>The nominating cluster's largest axis extent as a fraction of the cell size. Above 1.0 the bound covers more than its own cell.</summary>
        internal float Degradation { get; }
    }

    /// <summary>
    /// Cells nominated for repair by the AabbRefresh pass, consumed by the next tick's Prep. Appends go through <see cref="EnqueueRepairNominationsBulk"/>.
    /// </summary>
    /// <remarks>
    /// A list rather than a set: nomination fires per CLUSTER and a cell has many, so duplicates are the norm, and de-duplicating on the producer side would
    /// put a hash lookup on the detection path to save a merge on the rare planning one. The queue folds repeats by keeping the WORST degradation seen for
    /// a cell, which is the number the ranking wants — a cell with one catastrophic cluster deserves servicing ahead of one with several mediocre ones.
    /// </remarks>
    internal readonly List<RepairNomination> RepairNominations = [];

    /// <summary>Per-worker nomination buffer, filled by an AabbRefresh slice and merged into <see cref="RepairNominations"/> when the slice finishes.</summary>
    /// <remarks>
    /// <b><see cref="ThreadStaticAttribute"/> for the reason <c>CandidateScratch</c> is.</b> A fresh <c>List</c> per slice per tick is an allocation on the
    /// default configuration — <c>ReclusterBudgetMs</c> is 1.0, so this path is live out of the box — and step 11 doubled the element from four bytes to
    /// eight, so every growth doubling costs twice what it did. One list per worker, whose capacity converges to the worst slice that worker has scanned
    /// and then stops growing. Never trimmed: reclaiming the capacity would reintroduce the growth sequence it exists to remove.
    /// </remarks>
    [ThreadStatic]
    internal static List<RepairNomination> NominationScratch;

    /// <summary>Entities re-packed by the repair path this tick — the numerator of <c>AC-12.7</c>'s cost per entity.</summary>
    internal int LastTickRepairedEntityCount;

    /// <summary>Repair units admitted this tick. A unit is one cell's N worst clusters, or one whole cell.</summary>
    internal int LastTickRepairUnitCount;

    /// <summary>Repair units the remaining budget could not finish, and which were therefore never begun (<c>AC-12.5</c>).</summary>
    internal int LastTickRepairUnitsRefused;

    /// <summary>Per-archetype planner scratch, reused across ticks. Prep is single-threaded per archetype, so no locking and no thread statics.</summary>
    private RepairEntry[] _repairEntryScratch = [];

    /// <inheritdoc cref="_repairEntryScratch"/>
    private RepairCandidate[] _repairCandidateScratch = [];

    /// <inheritdoc cref="_repairEntryScratch"/>
    private int[] _repairCellScratch = [];

    /// <inheritdoc cref="_repairEntryScratch"/>
    private int[] _repairDestinationScratch = [];

    /// <summary>
    /// Cells whose last repair plan turned out to be a no-op, against the bounds hash that produced that verdict.
    /// </summary>
    /// <remarks>
    /// Bounded by the number of cells that have both nominated and converged, which is the population that would
    /// otherwise re-sort itself every tick. An entry is dropped the moment its cell is genuinely repaired.
    /// </remarks>
    private readonly Dictionary<int, ulong> _repairNoOpGeometry = [];

    /// <summary>
    /// Source slots already named by a request sitting in this tick's drain prefix, keyed by cluster chunk id (#877).
    /// </summary>
    /// <remarks>
    /// <para><b>Why the repair planner needs one at all.</b> <c>DetectDriftersInCluster</c> has always excluded the slots the outlier guard claimed, and its
    /// comment states the hazard exactly: <i>"queueing one entity for both would have two requests naming the same source slot, and whichever drained second
    /// would find it empty."</i> The repair planner gathers from the raw occupancy mask and had no equivalent, so a cell-crossing filed by
    /// <c>DetectClusterMigrations</c> earlier in the same Prep and a repair re-pack of the cluster that entity is leaving would BOTH name it. Measured on the
    /// first fence of every seed of <c>--profile-openmut</c>: 4 of 4.</para>
    /// <para><b>The collision is usually survivable, which is what made it hard to see.</b> The crossing drains first, clears the slot, and
    /// <c>ExecuteMigrations</c>' stale-source guard skips the repair request — no entity is lost and no counter disagrees. It stops being survivable when the
    /// slot is REFILLED before the repair request drains, which the repair path itself makes likely: it allocates fresh destination clusters and frees the
    /// drained sources in the same fence, so chunk ids recycle inside one tick. The repair then moves whichever entity now occupies the slot into a
    /// destination slot reserved for a different one, and the two entities' EntityMap entries and index rows disagree with where they actually live. That is
    /// the <c>InvalidOperationException: Entity ... not found or not visible</c> and the <c>B+Tree bulk update reached an invalid child</c> of #877.</para>
    /// <para><b>Cleared and rebuilt once per plan</b>, not maintained incrementally: repairs filed by an earlier cell in the same tick cannot collide with a
    /// later one, because a cluster belongs to exactly one cell. Only the producers that ran BEFORE the planner — the crossing detector, the outlier guard,
    /// and last tick's surviving relocations — can, and they are all in the prefix when the plan starts.</para>
    /// </remarks>
    /// <remarks>Flat rather than hashed since #882 — see <see cref="ClusterSlotClaimSet"/>. The semantics <c>CR-05</c> constrains are unchanged; only the
    /// container is.</remarks>
    private readonly ClusterSlotClaimSet _repairSourceExclusions = new();

    /// <summary>
    /// The clusters this tick's repair plans allocated for their own re-pack output, closed to every other claimant for the width of the tick.
    /// </summary>
    /// <remarks>
    /// <para><b>Measured, and it is the whole of the pin-rejection population.</b> A repair pins each moved entity to an exact <c>(cluster, slot)</c> of a
    /// FRESHLY ALLOCATED empty cluster, which is what makes the re-pack a permutation that cannot collide with itself. But an empty cluster attached to a
    /// cell is also the most attractive thing the cursor first-fit scan can find, so the cell-crossing drain — thousands of requests a tick against a
    /// repair's hundreds — filled those clusters before the repair's own pinned claims arrived. Every one of the 26 434 rejected repair pins in a 40-tick
    /// <c>Cruise</c> run (14.6 % of 178 145) was "the pinned cluster is full", never a stale cell, and the entity then landed in the right cell but the
    /// wrong cluster: the Morton grouping the plan had just computed, undone for one entity in seven.</para>
    /// <para>Excluding them costs one flat-array probe per scanned cluster on the claim path, the same probe
    /// <see cref="_repairSourceExclusions"/> already makes for relocation candidates. A claimant that finds every cluster of its cell reserved opens a fresh
    /// one, which is correct: those slots are spoken for.</para>
    /// </remarks>
    private readonly ClusterSlotClaimSet _repairDestinationReservations = new();

    /// <summary>Is this cluster reserved for a repair plan's output this tick? Read from the claim path, including user threads at spawn.</summary>
    internal bool IsRepairDestination(int clusterChunkId) =>
        _repairDestinationReservations.Count > 0 && _repairDestinationReservations.ContainsCluster(clusterChunkId);

    // ══════════════════════════════════════════════════════════════════════════════
    // Intra-cell Morton encoding
    // ══════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// The largest of a bound's three axis extents — the single number both the repair trigger and the step-11 ranking are expressed in.
    /// </summary>
    /// <remarks>
    /// <para><b>Flat archetypes fall out rather than being special-cased.</b> A 2D cluster leaves Z at the <c>ClusterSpatialAabb.Empty</c> sentinel
    /// (min <c>+Infinity</c>, max <c>-Infinity</c>), so its Z extent is <c>-Infinity</c> and <see cref="MathF.Max(float, float)"/> discards it.</para>
    /// <para><b>NaN does NOT behave identically to the three-way <c>||</c> this replaced, and the difference is stated rather than glossed.</b>
    /// <see cref="MathF.Max(float, float)"/> propagates NaN, so one non-finite axis makes the whole maximum NaN and every comparison against it false —
    /// where testing the axes separately would still have fired on a finite axis that was genuinely over threshold. The direction is safe (a gate that
    /// does not fire costs a deferred repair, not a wrong answer) and the input is not reachable through the bounds validation, but a comment claiming the
    /// two are equivalent would be false.</para>
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static float MaxAxisExtent(in ClusterSpatialAabb bounds) =>
        MathF.Max(bounds.MaxX - bounds.MinX, MathF.Max(bounds.MaxY - bounds.MinY, bounds.MaxZ - bounds.MinZ));

    /// <summary>Encode a cell-relative position as a 63-bit Morton key, X occupying the low bit of each triple.</summary>
    /// <param name="relX">Position minus cell origin on X. Values outside <c>[0, cellSize)</c> clamp into the cell.</param>
    /// <param name="relY">Position minus cell origin on Y.</param>
    /// <param name="relZ">Position minus cell origin on Z; pass <c>0</c> for a flat archetype.</param>
    /// <param name="inverseCellSize"><c>SpatialGridConfig.InverseCellSize</c>, precomputed.</param>
    /// <remarks>
    /// <para><b>Clamping is not defensive padding.</b> Migration hysteresis (<c>MigrationHysteresisRatio</c>) deliberately leaves an entity in its old cell
    /// for a dead zone past the boundary, so a cell legitimately holds entities slightly outside its own extent. Without the clamp those quantise past the
    /// axis maximum and wrap into the low bits, placing an entity that is just outside one face at the opposite corner of the sort order — which is worse
    /// than not sorting it at all.</para>
    /// <para>A flat archetype passes <paramref name="relZ"/> as <c>0</c>, so every Z bit is constant and the key degenerates to a 2D Morton code
    /// interleaved with zeros. The ordering that produces is exactly the 2D one, so no separate encoder is needed and the sort cannot tell the
    /// difference.</para>
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static ulong EncodeIntraCellMorton(float relX, float relY, float relZ, float inverseCellSize)
    {
        var qx = QuantizeAxis(relX * inverseCellSize);
        var qy = QuantizeAxis(relY * inverseCellSize);
        var qz = QuantizeAxis(relZ * inverseCellSize);
        return SpreadBy2(qx) | (SpreadBy2(qy) << 1) | (SpreadBy2(qz) << 2);
    }

    /// <summary>Map a normalised axis coordinate to <c>[0, 2^<see cref="MortonBitsPerAxis"/> - 1]</c>, clamping out-of-cell values into range.</summary>
    /// <remarks>
    /// The NaN case falls out of the comparison order rather than being tested: <c>!(t &gt; 0f)</c> is true for NaN, so a non-finite coordinate maps to 0
    /// instead of to an undefined float-to-integer conversion. Upstream already rejects non-finite positions — <see cref="GatherClusterCentres"/> excludes
    /// them from its valid mask — so this only has to fail safely, not meaningfully.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static ulong QuantizeAxis(float t)
    {
        const uint scale = (1u << MortonBitsPerAxis) - 1u;
        if (!(t > 0f))
        {
            return 0UL;
        }

        return t >= 1f ? scale : (uint)(t * scale + 0.5f);
    }

    /// <summary>Insert two zero bits after each of the low 21 bits, so three spread values interleave into one 63-bit key.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static ulong SpreadBy2(ulong x)
    {
        x &= 0x1FFFFFUL;
        x = (x | (x << 32)) & 0x001F00000000FFFFUL;
        x = (x | (x << 16)) & 0x001F0000FF0000FFUL;
        x = (x | (x << 8)) & 0x100F00F00F00F00FUL;
        x = (x | (x << 4)) & 0x10C30C30C30C30C3UL;
        x = (x | (x << 2)) & 0x1249249249249249UL;
        return x;
    }

    // ══════════════════════════════════════════════════════════════════════════════
    // Nomination — the AabbRefresh side
    // ══════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Append one slice's repair nominations to the archetype-wide list, under <c>_finalizeLock</c>. Called once per slice, never per cluster.
    /// </summary>
    internal void EnqueueRepairNominationsBulk(List<RepairNomination> nominations)
    {
        if (nominations == null || nominations.Count == 0)
        {
            return;
        }

        ref var nullCtx = ref Unsafe.NullRef<WaitContext>();
        _finalizeLock.Lock.EnterExclusiveAccess(ref nullCtx);
        try
        {
            RepairNominations.AddRange(nominations);
        }
        finally
        {
            _finalizeLock.Lock.ExitExclusiveAccess();
        }

        // Cleared, matching EnqueueMigrationsBulk. Redundant today — every slice allocates its own list — and exactly the
        // assumption that stops being true the day these buffers are pooled.
        nominations.Clear();
    }

    // ══════════════════════════════════════════════════════════════════════════════
    // Planning — the Prep side
    // ══════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Consume the previous tick's nominations and emit pinned migration requests that re-pack each admitted cell in Morton order. Returns the number of
    /// entities the plan will move; <paramref name="budgetUsedMs"/> receives the projected spend.
    /// </summary>
    /// <remarks>
    /// <para><b>Single-threaded by contract.</b> Called from <c>PrepareArchetypeFenceCore</c>, which runs one work item per archetype. Everything here
    /// mutates archetype-local state and allocates clusters, and none of it is written to tolerate a sibling.</para>
    /// <para><b>The budget admits units; it never stops one (§5.6).</b> A Morton sort cannot be halved — a partly re-sorted cell has paid the cost and
    /// banked only part of the benefit — so a unit whose projected cost exceeds the remaining budget is not begun at all (<c>AC-12.5</c>). A refused unit is
    /// counted AND left in the queue: since step 11 the candidate persists, ages, and is reached on a later tick, which is what makes no-starvation a
    /// property of the arithmetic rather than of the workload.</para>
    /// <para><b>Refusal now stops the scan, and that is a reversal.</b> Step 12 continued past a refusal, on the reasoning that its cell order was by cell
    /// key — arbitrary with respect to cost — so breaking would let one large cell at a low key block every other. That reasoning expired with the ranking:
    /// the order is now best-first and the cheapest admissible unit is two clusters, so once the budget cannot cover that, nothing later can be admitted
    /// either. Continuing would mean ranking, sorting and hashing every remaining candidate to reach a conclusion already known — up to
    /// <c>RepairQueueMaxCells</c> of them, on Prep, charged to no budget.</para>
    /// </remarks>
    internal int PlanCellRepairs(SpatialGrid grid, ref ChunkAccessor<PersistentStore> accessor, long tickNumber, double remainingBudgetNs,
        out double budgetUsedMs)
    {
        budgetUsedMs = 0d;
        LastTickRepairedEntityCount = 0;
        LastTickRepairUnitCount = 0;
        LastTickRepairUnitsRefused = 0;

        // Drained unconditionally, ahead of every early-out below, because the LIST is a per-tick handoff even though the QUEUE it feeds is not: a
        // nomination describes the tick that produced it, and an archetype that stops meeting the preconditions would otherwise accumulate them for ever.
        // What survives the tick is the queue's candidate, which carries the worst degradation seen and the tick it started waiting.
        var nominations = RepairNominations;
        var queue = EnsureRepairQueue(grid);
        var maintenanceStart = Stopwatch.GetTimestamp();

        // No CellClusterPool test: absorbing is what makes a nomination survive its tick (TH-03), and gating it on a structure the SCORE happens to read
        // meant that on the paths where the pool was not yet built the nomination was cleared below and lost outright — the exact failure step 11 exists
        // to remove. Score tolerates a missing pool by returning zero, which ranks the candidate last until the pool exists and it is re-ranked.
        if (queue != null && nominations.Count > 0 && grid != null)
        {
            queue.Absorb(nominations, grid, this, tickNumber);
        }

        nominations.Clear();

        // ABOVE every early return below, and that placement is the whole of its correctness. The reservation is per-TICK, and the ticks on which the
        // planner returns early are the normal steady state: a converged cell stops nominating (RP-03's no-op memo), the budget can be zero, the archetype
        // can be Static. Cleared further down, last tick's reservations would outlive their plan for as long as the planner kept returning early — and a
        // destination that received nothing is freed by the same Finalize, so its chunk id is reallocated, possibly into another cell, and that live
        // cluster would then be excluded from every claim path and every relocation candidate for good.
        _repairDestinationReservations.Clear();

        if (queue == null || queue.Count == 0)
        {
            AccrueQueueMaintenance(queue, maintenanceStart);
            return 0;
        }

        if (grid == null || ClusterSegment == null || CellClusterPool == null || ClusterCellMap == null || ClusterAabbs == null)
        {
            AccrueQueueMaintenance(queue, maintenanceStart);
            return 0;
        }

        if (!SpatialSlot.HasSpatialIndex || SpatialSlot.FieldInfo.Mode != SpatialMode.Dynamic)
        {
            AccrueQueueMaintenance(queue, maintenanceStart);
            return 0;
        }

        ref readonly var cfg = ref grid.Config;
        if (cfg.ReclusterBudgetMs <= 0f)
        {
            // AC-11.8. The queue keeps absorbing and keeps evicting at its cap, so it stays bounded, and nothing is planned. Deliberately AFTER the absorb
            // so a budget raised at runtime finds a populated queue rather than an empty one.
            AccrueQueueMaintenance(queue, maintenanceStart);
            return 0;
        }

        // Every source slot the queue has already spoken for, before a single unit is planned (#877). O(prefix) once, against a planner that walks entities.
        BuildRepairSourceExclusions();

        // Ranked, not sorted by cell key. §5.6: "round-robin is the wrong policy" — a region nobody queries never needs tight clusters. Lazy, so a tick
        // whose nominations changed nothing pays a comparison rather than a sort (AC-11.5).
        queue.Rerank(grid, this, tickNumber);
        AccrueQueueMaintenance(queue, maintenanceStart);

        var estimateNsPerEntity = RepairCostEstimateNs(in cfg);
        var remainingNs = remainingBudgetNs;
        var totalMoved = 0;
        var criticalRatio = cfg.ClusterRepairCriticalExtentRatio;

        // A snapshot, because RepairOneCell removes serviced cells from the queue and the ranked array is the queue's own buffer. Copying the keys out
        // first is cheaper than the alternative of deferring every removal to a second pass, and the count is the candidate count, not the entity count.
        var ranked = queue.Ranked;
        if (_repairCellScratch.Length < ranked.Length)
        {
            _repairCellScratch = new int[Math.Max(ranked.Length, Math.Max(16, _repairCellScratch.Length * 2))];
        }
        ranked.CopyTo(_repairCellScratch);
        var rankedCount = ranked.Length;

        // ── The critical candidate goes FIRST, and the scan stops when the budget is gone ───────────────────────────
        //
        // AC-11.2 says a cell past the hard threshold is serviced "regardless of queue depth", and serving it in rank order
        // does not deliver that: criticality is a threshold on degradation while rank is a product of four terms, so a
        // critical cell can sit anywhere in the order and be reached only after the budget has been spent on cells that
        // merely scored well. Hoisting it is what makes the valve a queue-jump rather than a late consolation.
        //
        // The scan then BREAKS rather than continuing, and over a persistent queue that is a different cost class from
        // the per-tick list step 12 had. `continue` meant every remaining candidate — up to RepairQueueMaxCells, 4 096 by
        // default — still paid GetClusters, the ranking loop, an Array.Sort and a geometry hash before failing the budget
        // test, single-threaded on Prep and charged to no budget at all. One unit exhausting a 1 ms budget left 4 095
        // cells fully planned for nothing.
        //
        // The break is safe because the ranking is monotone in what it can afford: candidates are ordered best-first and
        // the cheapest possible unit is two clusters, so once the remaining budget cannot cover that, nothing later in the
        // order can be admitted either — and the one admission that IS allowed to exceed the budget has already been tried.
        //
        // "At most one overshoot per archetype per tick" is enforced STRUCTURALLY here, by the fact that exactly one call
        // site below passes `valveAvailable: true`. It used to be a `_valveFiredThisTick` flag, which this hoist orphaned —
        // the flag stayed assigned and reset for a while with no reader left, which is worse than no flag at all: it reads
        // as the thing enforcing the bound while enforcing nothing. One call site is provable by inspection.
        //
        // What that narrows, stated because it is a real behaviour change: if a SECOND cell is also critical, it is offered
        // no valve this tick and is refused like any other candidate. It ages, keeps its place in the queue and is the
        // hoisted one on a later tick, so AC-11.2's "serviced within N ticks" still holds — with a larger N when several
        // cells are critical at once, which is the case the budget is losing to anyway.
        var criticalIndex = -1;
        if (criticalRatio > 0f)
        {
            for (var i = 0; i < rankedCount; i++)
            {
                if (queue.DegradationOf(_repairCellScratch[i]) >= criticalRatio)
                {
                    criticalIndex = i;
                    break;
                }
            }
        }

        if (criticalIndex >= 0)
        {
            totalMoved += RepairOneCell(_repairCellScratch[criticalIndex], grid, ref accessor, estimateNsPerEntity, true, ref remainingNs);
        }

        var minimumUnitNs = 2 * estimateNsPerEntity;
        for (var i = 0; i < rankedCount; i++)
        {
            if (i == criticalIndex)
            {
                continue;   // already serviced above, at the head
            }

            if (remainingNs < minimumUnitNs)
            {
                break;
            }

            totalMoved += RepairOneCell(_repairCellScratch[i], grid, ref accessor, estimateNsPerEntity, false, ref remainingNs);
        }

        // ── Top up the deferred-drain list for everything the plan added ────────────────────────────────────────────
        //
        // PreSizeMigrationBuffers sizes _drainedClusterIds from PendingMigrationCount, on the premise that one migration
        // releases at most one source slot — and it runs in Prep's CORE, before this. A repair breaks the premise from
        // both ends: it files more migrations, and it has already consumed one drain entry per fresh destination. The
        // Migrate phase would then overflow into RecordClusterDrain's fallback grow, which parallel workers reach and
        // which writes its entry AFTER releasing the lock, re-reading the field — so an entry can be discarded by a
        // concurrent resize, and a discarded drain record is RP-03's cluster that never received an entity and leaks its
        // chunk id.
        //
        // Once, here, over the WHOLE queue rather than per unit. Per unit it would have been `_drainedCount + count`,
        // which under-counts twice over: it ignores the requests other units filed, and it ignores whatever the
        // cell-crossing detectors queued before the planner ran. _drainedCount is every destination this plan allocated
        // (it was zeroed at the top of Prep) and PendingMigrationCount is every request Migrate will drain, so their sum
        // is the true ceiling on drain records for the tick.
        if (totalMoved > 0)
        {
            PreSizeDrainedClusterIds(_drainedCount + PendingMigrationCount);
        }

        LastTickRepairedEntityCount = totalMoved;

        // What the plan COMMITTED, which is what the budget gated on — not elapsed time, which would report a number that gated nothing (RP-01). A
        // safety-valve admission drives `remainingNs` negative on purpose, which makes this LARGER than the budget rather than negative: the overshoot is
        // reported rather than hidden, and LastTickRepairValveFires says which admission caused it.
        budgetUsedMs = (remainingBudgetNs - remainingNs) / 1_000_000d;
        return totalMoved;
    }

    /// <summary>
    /// Plan one cell's repair unit: rank its clusters, admit the unit if the budget covers it, then hand off to <see cref="ExecuteRepairPlan"/>. Returns the
    /// number of entities the unit will move, or <c>0</c> when nothing was begun.
    /// </summary>
    private int RepairOneCell(int cellKey, SpatialGrid grid, ref ChunkAccessor<PersistentStore> accessor, double estimateNsPerEntity, bool valveAvailable,
        ref double remainingNs)
    {
        var clusters = CellClusterPool.GetClusters(cellKey);
        if (clusters.Length < 2)
        {
            // A single cluster is already its own optimal packing — a sort cannot improve a partition of one. Dropped from the queue rather than left to
            // age, because no amount of waiting will make it repairable and an unrepairable candidate at the head is a slot the ranking cannot use.
            RepairQueue?.Remove(cellKey);
            return 0;
        }

        ref readonly var cfg = ref grid.Config;
        if (_repairCandidateScratch.Length < clusters.Length)
        {
            _repairCandidateScratch = new RepairCandidate[Math.Max(clusters.Length, Math.Max(16, _repairCandidateScratch.Length * 2))];
        }

        // Ranked on the same quantity the cellSize x 1.2 trigger reads — the largest axis extent of the stored, cell-relative bound. A cluster with no bound
        // (freed, or never populated) scores negative infinity and sorts last, so it is picked only when the unit takes the whole cell, and then it
        // contributes no entities.
        var candidates = _repairCandidateScratch;
        for (var i = 0; i < clusters.Length; i++)
        {
            var chunkId = clusters[i];
            var extent = float.NegativeInfinity;
            if ((uint)chunkId < (uint)ClusterAabbs.Length)
            {
                ref var box = ref ClusterAabbs[chunkId];
                if (!float.IsPositiveInfinity(box.MinX))
                {
                    extent = MathF.Max(box.MaxX - box.MinX, box.MaxY - box.MinY);
                    var extentZ = box.MaxZ - box.MinZ;
                    if (float.IsFinite(extentZ))
                    {
                        extent = MathF.Max(extent, extentZ);
                    }
                }
            }
            candidates[i] = new RepairCandidate(extent, chunkId);
        }

        Array.Sort(candidates, 0, clusters.Length);

        // ── Nothing has moved since this cell was found already-packed? Then the sort has the same answer ────────────
        //
        // IsAlreadyPackedInSortOrder stops the MOVES, not the cost of discovering there are none: nomination fires on
        // extent alone and a Morton packing does not in general bring every cluster under the threshold, so a converged
        // cell is nominated again on every subsequent tick and pays a full GatherClusterCentres walk plus an Array.Sort
        // to reach the same verdict — on Prep, single-threaded, and charged to no budget because a unit that emits
        // nothing is never debited. Measured with the whole cell as the unit, that is 2 000 entities gathered and sorted
        // per tick, forever.
        //
        // The memo is the hash of the unit's stored bounds, which the ranking loop above has just read into registers.
        // Same bounds as the last no-op ⇒ the same entity positions produce the same key order ⇒ the same partition.
        //
        // A heuristic, in one direction. Entities that shuffle STRICTLY INSIDE their clusters' existing bounds change
        // the Morton order without changing any bound, and this will skip a re-sort that would have helped. That is the
        // same exposure CR-03 already records as a scoped exception, and it is the delta path's population, not repair's.
        var geometry = HashUnitGeometry(candidates, clusters.Length);
        if (_repairNoOpGeometry.TryGetValue(cellKey, out var lastNoOp) && lastNoOp == geometry)
        {
            // Dropped from the queue, not merely skipped. The memo says this cell's geometry has not changed since it was found already packed, so it is
            // not waiting for budget and ageing it to the head spends the head slot on a cell that cannot use it — and, in a CAPPED queue, evicts a cell
            // that can. Nomination re-queues it the moment its geometry actually moves.
            RepairQueue?.Remove(cellKey);
            return 0;
        }

        // The unit is sized from the CONFIGURED value, unconditionally. The valve's cap is applied further down, and only if the valve is actually needed.
        //
        // Capping here on `valveAvailable` — which means "this cell is critical and the valve is unspent", NOT "the valve is being used" — inverts the
        // whole policy. Under the whole-cell configuration every critical cell reached while the valve was unspent had its unit forced down to eight
        // clusters even when the budget covered the entire cell, and because the smaller unit then FIT, the valve was never marked as fired and the shrink
        // repeated for every critical candidate in the tick. The most degraded cells got the smallest repairs, silently and without limit.
        var perUnit = cfg.RepairWorstClustersPerUnit;
        var unitClusters = perUnit <= 0 ? clusters.Length : Math.Min(perUnit, clusters.Length);
        if (unitClusters < 2)
        {
            RepairQueue?.Remove(cellKey);
            return 0;
        }

        // Population first, because the budget decision has to precede every byte of work (AC-12.5). An occupancy popcount is one load per cluster; the
        // entity walk that follows is what costs, and it does not run unless the unit is admitted.
        var population = UnitPopulation(candidates, unitClusters, ref accessor);
        if (population < 2)
        {
            // Same reasoning as the no-op memo above: a unit of fewer than two entities has no permutation to find, so waiting cannot help it.
            RepairQueue?.Remove(cellKey);
            return 0;
        }

        // Measured, not the hand-set constant step 12 shipped — see RepairCostEstimateNs. The projection still PRECEDES the work, which is what RP-01
        // requires; what changed is the number it is built from.
        var projectedNs = population * estimateNsPerEntity;
        var valveFired = false;
        if (projectedNs > remainingNs)
        {
            if (!valveAvailable)
            {
                // Refused, and LEFT IN THE QUEUE. Step 12 discarded the nomination here, so a cell the budget could not afford was forgotten rather than
                // deferred and only came back if it happened to be written again. Ageing now carries it to the head instead (AC-11.3).
                LastTickRepairUnitsRefused++;
                return 0;
            }

            // ── The valve, and the ONE place its cap belongs ────────────────────────────────────────────────────────
            //
            // The full unit does not fit and the cell is critical, so something has to give. Shrinking to the cap is
            // tried FIRST, because a capped unit that fits is an ordinary admission — it spends only what the budget
            // has, and leaves the tick's one overshoot for a cell that genuinely needs it. Only if even the capped
            // unit overruns does the valve actually fire.
            var cappedClusters = Math.Min(ValveClustersPerUnit, clusters.Length);
            if (cappedClusters >= 2 && cappedClusters < unitClusters)
            {
                var cappedPopulation = UnitPopulation(candidates, cappedClusters, ref accessor);
                if (cappedPopulation >= 2)
                {
                    unitClusters = cappedClusters;
                    population = cappedPopulation;
                    projectedNs = population * estimateNsPerEntity;
                }
            }

            // AC-11.2. The accounting happens only if the unit actually moves something, below: spending the tick's single admission on a re-pack that
            // turns out to be a no-op would refuse a genuinely critical cell later in the ranking for work that never happened.
            valveFired = projectedNs > remainingNs;
        }

        // Captured before the plan runs, because the memo below must not record a verdict that the exclusions caused (#877). A unit whose entities are
        // partly spoken for produces a DIFFERENT partition from the one the same geometry will produce next tick, once those requests have drained — so
        // "this geometry re-packs to nothing" is not a conclusion this tick is entitled to draw.
        var unitHadExclusions = UnitHasExcludedSources(candidates, unitClusters);

        // ── #911 O1: the unit gets a span of its own ───────────────────────────────────────────────────────────────────
        //
        // Opened HERE, before the plan runs and before the queue entry is removed: the degradation is the term the cell was
        // RANKED on and it lives on the queue candidate, so reading it after the Remove below would read zero. A unit that
        // turns out to move nothing still gets its span — discovering a cell is already packed costs a gather and a sort,
        // and that cost is invisible on the timeline without it. `MovedCount` is what separates the two cases.
        var unitSpan = TyphonEvent.BeginSpatialRepairUnit((ushort)ArchetypeId, cellKey, unitClusters, population);

        var moved = 0;
        try
        {
            if (TelemetryConfig.SpatialClusterRepairActive)
            {
                // INSIDE the try, so a throw from the probe still reaches the finally that disposes the span rather than leaking its ring slot. Guarded
                // rather than assigned unconditionally because the factory folds to `return default` when the gate is off while the ARGUMENT would still be
                // evaluated, and DegradationOf is a dictionary probe. The gate is `static readonly`, so with the kind off this block disappears entirely.
                unitSpan.Degradation = RepairQueue?.DegradationOf(cellKey) ?? 0f;
            }

            moved = ExecuteRepairPlan(cellKey, grid, ref accessor, candidates, unitClusters, population);
        }
        finally
        {
            // Written inside the finally because Dispose is what PUBLISHES the record — an outcome assigned after it is simply dropped.
            unitSpan.MovedCount = moved;
            unitSpan.ValveFired = valveFired ? (byte)1 : (byte)0;
            unitSpan.Dispose();
        }

        if (moved == 0)
        {
            if (unitHadExclusions)
            {
                // Left in the queue, unmemoised, to age. The cell is not converged; it is temporarily un-repackable because some of its entities are already
                // on their way somewhere else. Memoising here would drop it from the queue against a geometry hash that will still match next tick — and
                // nomination only re-queues a cell whose BOUNDS move, which for a cell whose contents are merely being reshuffled may be never.
                return 0;
            }

            // Remembered against the geometry that produced it, so the next tick's nomination costs a dictionary probe
            // rather than a gather and a sort. Recorded here rather than inside ExecuteRepairPlan because this is where
            // the hash is in scope and where the budget decision lives.
            //
            // Dropped from the queue too: a unit whose sort would change nothing is not waiting for budget, so ageing it to the head would spend the
            // head slot on a cell that cannot use it. The memo above is what stops it costing a gather next tick; nomination re-queues it the moment its
            // geometry actually changes.
            _repairNoOpGeometry[cellKey] = geometry;
            RepairQueue?.Remove(cellKey);
            return 0;
        }

        _repairNoOpGeometry.Remove(cellKey);

        if (valveFired)
        {
            LastTickRepairValveFires++;
        }

        // Debited even when the valve overshot, so ReclusterBudgetUsedMs reports what was actually committed rather than what fitted. `remainingNs` goes
        // negative in that case and every later candidate is refused, which is precisely the "at most one unit over" bound AC-11.1 asks for.
        remainingNs -= projectedNs;
        LastTickRepairUnitCount++;
        RepairQueue?.Remove(cellKey);
        return moved;
    }

    /// <summary>
    /// Entities held by the first <paramref name="unitClusters"/> of the ranked candidates — the number every budget projection is built from.
    /// </summary>
    /// <remarks>
    /// One occupancy load per cluster and a popcount, so it is cheap enough to run twice: once for the configured unit, and again for the valve's smaller
    /// one when the first does not fit. The entity walk that would actually cost something does not happen until the unit is admitted.
    /// </remarks>
    private int UnitPopulation(RepairCandidate[] candidates, int unitClusters, ref ChunkAccessor<PersistentStore> accessor)
    {
        var population = 0;
        for (var i = 0; i < unitClusters; i++)
        {
            var chunkId = candidates[i].ChunkId;
            var clusterBase = accessor.GetChunkAddress(chunkId);
            var occupied = *(ulong*)clusterBase & Layout.FullMask;

            // Excluded here and not only at the gather, so the budget projection matches the work that will actually happen. Charging for entities the
            // gather is about to skip would refuse a later candidate to pay for moves nobody makes — the opposite of what RP-01's projection is for.
            population += BitOperations.PopCount(occupied & ~ExcludedSourceSlots(chunkId));
        }

        return population;
    }

    /// <summary>Slots of <paramref name="clusterChunkId"/> that a queued request already names as its source; <c>0</c> when none do.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private ulong ExcludedSourceSlots(int clusterChunkId) => _repairSourceExclusions.ClaimedSlots(clusterChunkId);

    /// <summary>Does any cluster in the unit hold a slot another request already claims?</summary>
    private bool UnitHasExcludedSources(RepairCandidate[] candidates, int unitClusters)
    {
        if (_repairSourceExclusions.Count == 0)
        {
            return false;
        }

        for (var i = 0; i < unitClusters; i++)
        {
            if (_repairSourceExclusions.ContainsCluster(candidates[i].ChunkId))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Rebuild <see cref="_repairSourceExclusions"/> from the requests already queued for this tick.</summary>
    /// <remarks>
    /// The whole prefix, not just the crossings: an admitted relocation names a source slot for exactly the same reason and collides in exactly the same
    /// way. A repair request cannot appear here, because the map is built before the first unit is planned.
    /// </remarks>
    private void BuildRepairSourceExclusions()
    {
        _repairSourceExclusions.Clear();

        var queued = PendingMigrationCount;
        var requests = PendingMigrations;
        for (var i = 0; i < queued; i++)
        {
            ref readonly var request = ref requests[i];
            _repairSourceExclusions.Claim(request.SourceClusterChunkId, request.SourceSlotIndex);
        }
    }

    /// <summary>
    /// Gather, sort and emit. Kept separate from the admission decision so that the "never started" half of <c>AC-12.5</c> is one early return in front of a
    /// method that performs the whole unit, rather than a condition threaded through the work.
    /// </summary>
    private int ExecuteRepairPlan(int cellKey, SpatialGrid grid, ref ChunkAccessor<PersistentStore> accessor, RepairCandidate[] candidates,
        int unitClusters, int population)
    {
        ref readonly var cfg = ref grid.Config;
        if (_repairEntryScratch.Length < population)
        {
            _repairEntryScratch = new RepairEntry[Math.Max(population, Math.Max(64, _repairEntryScratch.Length * 2))];
        }
        var entries = _repairEntryScratch;

        grid.CellOrigin(cellKey, out var originX, out var originY, out var originZ);
        var flat = SpatialSlot.FieldInfo.FieldType is SpatialFieldType.AABB2F or SpatialFieldType.BSphere2F;

        Span<float> centreScratch = stackalloc float[3 * MaxSlotsPerCluster];
        var count = 0;
        for (var i = 0; i < unitClusters && count < population; i++)
        {
            var chunkId = candidates[i].ChunkId;
            var centres = GatherClusterCentres(chunkId, ref accessor, centreScratch);

            // #877. A slot another request already claims is NOT re-packed: the entity is leaving this cluster under that request, and a second request
            // naming the same source would drain against whatever occupies the slot by then. Excluding it costs the unit one entity and keeps the cell
            // repairable next tick, which is the trade DetectDriftersInCluster already makes for the outlier guard.
            var bits = centres.ValidMask & ~ExcludedSourceSlots(chunkId);
            while (bits != 0 && count < population)
            {
                var slot = BitOperations.TrailingZeroCount(bits);
                bits &= bits - 1;

                var key = EncodeIntraCellMorton(centres.X(slot) - originX, centres.Y(slot) - originY,
                    flat ? 0f : centres.Z(slot) - originZ, cfg.InverseCellSize);
                entries[count++] = new RepairEntry(key, (long)chunkId * MaxSlotsPerCluster + slot);
            }
        }

        if (count < 2)
        {
            return 0;
        }

        // A radix sort was tried here and REFUTED — do not re-add it without a new measurement. RadixSortBenchmarks, the Repair_* rows, quiet box, with
        // the adaptive 8-bit digits these sizes take (the fixed-11-bit first cut was worse still, 7.5x slower at 128): at the sizes a repair unit has
        // (65–2 000 entities) the two-key stable RadixSort (source location, then Morton) runs at 0.31x of this Array.Sort at 128
        // entries, 0.39x at 512 and 0.67x at 2 048 — a 63-bit key is six to eight passes and the tie-break adds up to four more, each with its fixed cost,
        // against an introsort whose CompareTo is devirtualised and inlined on a 16-byte struct. It wins only at 32 768 (2.5x), a unit the planner never
        // admits.
        Array.Sort(entries, 0, count);

        // ── Already sorted? Then moving 2 000 entities buys exactly nothing ──────────────────────────────────────────
        //
        // A repair leaves the unit packed in Morton order. Nothing about that decays on its own, so the NEXT tick's
        // nomination — which fires on extent alone — sees the same geometry and asks for the same re-pack, which produces
        // the identical partition. Without this check the cell is re-packed on every single tick, forever, at full cost
        // and zero gain. Measured before the check: a 2 000-entity cell re-packed all 2 000 on ticks 2 through 6 with the
        // mean extent pinned at 23.0 the whole time.
        //
        // Nomination cannot be the place to stop it. The trigger is a threshold on extent, and a Morton packing does not
        // in general get every cluster under it — at 41 clusters one stayed above 0.75 x cellSize, which is a property of
        // the curve rather than a failure to repair. Whether a re-sort would HELP is a different question from whether the
        // cell looks bad, and this is the cheapest exact answer to it: if every packed group already draws from one source
        // cluster, the partition and the sorted partition coincide, so every resulting bound is the bound the cell already
        // has. One pass over an array that has just been sorted, against a move of the whole unit.
        if (IsAlreadyPackedInSortOrder(entries, count))
        {
            return 0;
        }

        // Fresh destinations, not the unit's own clusters. A re-pack is a PERMUTATION of the unit's slots, and ExecuteMigrations moves one entity at a time
        // into a slot it claims — so a destination still occupied by an entity that has yet to move would fail its claim and fall back to first fit, which
        // is the placement this whole issue exists to repair. Packing into fresh clusters makes every destination free by construction. The cost is one
        // extra cluster set for the width of the fence window: the sources drain empty and are freed by the same Finalize pass that frees any other emptied
        // cluster.
        var capacity = BitOperations.PopCount(Layout.FullMask);

        // ── Every destination first, then every request ──────────────────────────────────────────────────────────────
        //
        // Interleaving them would let an allocation fail partway and leave the unit HALF re-packed, which RP-01 calls
        // strictly worse than untouched: the budget has been charged, some entities have moved into the sorted order and
        // the rest have not, and the resulting bounds are neither the old ones nor the new ones. Allocating up front makes
        // the failure atomic — nothing is emitted, the clusters already taken are on the drain list and Finalize frees
        // them because they are still empty.
        // ── PER-GROUP SKIPPING WAS TRIED HERE AND REFUTED (#872 step 17, D6) ────────────────────────────────────────
        //
        // D6 proposed moving "only the minority of each sorted group" — 40-60 % of a random unit's moves, 85-95 % of a
        // drifted one's. The version that is SAFE at this grain is to skip a group whose entities already draw from one
        // source cluster, which is the per-group form of the whole-unit memo above. Measured over three motion points,
        // three rounds, one process: 4 % fewer migrations at Drift 8 ms, a WASH on fence span everywhere, and at Drift
        // 1 ms it did five times MORE migrations than the baseline — because skipping groups returns budget to the
        // planner, which then admits units it would otherwise have refused. It changes which cells get repaired, not
        // only what each repair costs, and neither direction paid.
        //
        // The 40-60 % figure needs PER-ENTITY granularity: keep the entities already in the plurality cluster and move
        // only the rest into it. That needs the drain to free a slot before it fills it, and the dependency graph
        // between clusters has cycles in general, so it needs a rotation buffer or a cycle-following executor — which
        // is why the destinations here are fresh clusters in the first place (see the comment above). Not built.
        var destinationCount = (count + capacity - 1) / capacity;
        if (_repairDestinationScratch.Length < destinationCount)
        {
            _repairDestinationScratch = new int[Math.Max(destinationCount, Math.Max(8, _repairDestinationScratch.Length * 2))];
        }

        var destinations = _repairDestinationScratch;
        for (var d = 0; d < destinationCount; d++)
        {
            destinations[d] = AllocateEmptyClusterForCell(cellKey, grid, ref accessor);
            if (destinations[d] < 0)
            {
                return 0;
            }

            // Closed to the cursor scan for the rest of the tick — see _repairDestinationReservations. Slot 0 is enough to mark the cluster present, and
            // presence is the whole question the scan asks; the plan owns every slot of a cluster it allocated empty.
            _repairDestinationReservations.Claim(destinations[d], 0);
        }

        // Reserved AFTER the destinations exist, so a failed allocation does not leave the queue grown for requests that
        // were never filed. One growth, not log2(count) of them: EnqueueMigration doubles PendingMigrations on overflow,
        // so emitting 2 000 requests one at a time cost about seven Array.Resize copies inside Prep.
        ReservePendingMigrationCapacity(count);

        for (var i = 0; i < count; i++)
        {
            var source = entries[i].SourceLocation;
            EnqueueMigration(new MigrationRequest((int)(source / MaxSlotsPerCluster), (int)(source % MaxSlotsPerCluster), cellKey,
                destinations[i / capacity], i % capacity, MigrationKind.Repair));
        }

        var emitted = count;

        // No trace event of its own. Adding one means an event id, a DTO, an OpenAPI schema entry and a decoder arm, and the three counters this path
        // maintains already answer the questions step 12 asks — how many entities moved, in how many units, and how many units the budget refused. The
        // per-cluster ClusterCellAssign fired by each destination allocation is what a trace needs to see the re-pack happen.
        return emitted;
    }

    /// <summary>Order-sensitive hash of a cell's ranked cluster bounds — the memo key for a no-op verdict.</summary>
    /// <remarks>
    /// Over the RANKED candidates rather than the raw pool, so a change in which clusters the unit would take is itself a
    /// change. The bit patterns go in raw: two bounds that differ by an ULP must hash differently, and float equality
    /// would be the wrong comparison for a cache key even where it is the right one for geometry.
    /// </remarks>
    private ulong HashUnitGeometry(RepairCandidate[] candidates, int clusterCount)
    {
        var hash = 1469598103934665603UL; // FNV-1a offset basis
        for (var i = 0; i < clusterCount; i++)
        {
            ref var box = ref ClusterAabbs[candidates[i].ChunkId];
            hash = Mix(hash, (uint)candidates[i].ChunkId);
            hash = Mix(hash, (uint)BitConverter.SingleToInt32Bits(box.MinX));
            hash = Mix(hash, (uint)BitConverter.SingleToInt32Bits(box.MinY));
            hash = Mix(hash, (uint)BitConverter.SingleToInt32Bits(box.MinZ));
            hash = Mix(hash, (uint)BitConverter.SingleToInt32Bits(box.MaxX));
            hash = Mix(hash, (uint)BitConverter.SingleToInt32Bits(box.MaxY));
            hash = Mix(hash, (uint)BitConverter.SingleToInt32Bits(box.MaxZ));
        }

        return hash;
    }

    /// <summary>One FNV-1a round over four bytes.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong Mix(ulong hash, uint value) => (hash ^ value) * 1099511628211UL;

    /// <summary>
    /// Would the sorted packing reproduce the partition the unit already has? One pass over the sorted entries.
    /// </summary>
    /// <remarks>
    /// <para>Group <c>g</c> of the packing is <c>entries[g * capacity .. (g + 1) * capacity)</c>, and it becomes one
    /// destination cluster. If every entry in every group already lives in the same source cluster, then each destination
    /// would hold exactly the entities one existing cluster holds — same sets, so the same bounds, so no selectivity
    /// changes hands and the entire move is waste.</para>
    /// <para><b>Conservative in the safe direction.</b> The test asks about SETS, not about slot order, so a unit whose
    /// entities sit in the right clusters but the wrong slots within them is reported as already packed — correctly, since
    /// intra-cluster order is invisible to every query: a cluster is opened or pruned as a whole, on its bound.</para>
    /// <para>It is not an ordering test either. A group drawing from one source cluster is enough; which cluster, and in
    /// what sequence the groups appear, does not matter, because the destination assignment is by group index and the
    /// bounds are what the caller is trying to improve.</para>
    /// </remarks>
    internal bool IsAlreadyPackedInSortOrder(RepairEntry[] entries, int count)
    {
        var capacity = BitOperations.PopCount(Layout.FullMask);
        for (var start = 0; start < count; start += capacity)
        {
            var end = Math.Min(start + capacity, count);
            var groupCluster = entries[start].SourceLocation / MaxSlotsPerCluster;
            for (var i = start + 1; i < end; i++)
            {
                if (entries[i].SourceLocation / MaxSlotsPerCluster != groupCluster)
                {
                    return false;
                }
            }
        }

        return true;
    }

    /// <summary>
    /// Allocate a cluster attached to <paramref name="cellKey"/> with <b>no</b> slot claimed, and register it for the Finalize sweep.
    /// </summary>
    /// <remarks>
    /// <para><b>Empty, unlike every other allocation site.</b> <c>ClaimSlotInCell</c>'s slow path allocates a cluster and publishes occupancy <c>1</c> in the
    /// same breath, because it allocates on behalf of a caller who is claiming slot 0. Repair allocates on behalf of a PLAN, and the entities arrive later
    /// in the same fence window through the ordinary migration path — so occupancy starts at zero and each arrival claims its own pinned slot. Pre-setting
    /// the bits would be cheaper, and is what "no claim on the repair path" would literally mean, but an occupancy bit is authoritative: a set bit with no
    /// entity behind it is counted by the unfiltered <c>Count()</c> fast path, which sums occupancy popcounts, and would over-report the database for the
    /// width of a tick.</para>
    /// <para><b><see cref="RecordClusterDrain"/> is the leak guard, and it is exactly the right one.</b> If every request targeting this cluster is skipped
    /// — a source slot emptied by a destroy, or by an earlier queued migration — the cluster stays empty, and nothing would ever release a slot in it, which
    /// is the only event that normally schedules a cluster for freeing. Recording the drain here puts it in front of <c>DrainPendingClusterFinalizations</c>,
    /// whose pass already re-reads occupancy and frees only what is still empty, leaving a populated cluster alone. No new sweep, and no special case.</para>
    /// <para><b>Zone maps are invalidated here, and this is their only narrowing point in the engine.</b> A recycled chunk id inherits the min/max of
    /// whatever lived there before, because <c>ZoneMapArray.Widen</c> is the only writer on the hot path and it never narrows — the class carries an
    /// <c>Invalidate</c> for exactly this and, until now, no caller at all. Stale-wide bounds are conservative (<c>MayContain</c> over-reports, so queries
    /// stay correct and merely open clusters they need not), but they are also what would make <c>AC-12.2</c> unmeasurable: the re-pack's locality-grouped
    /// contents can only be observed to shrink a zone map if the map starts from those contents rather than from a previous tenant's.</para>
    /// </remarks>
    private int AllocateEmptyClusterForCell(int cellKey, SpatialGrid grid, ref ChunkAccessor<PersistentStore> accessor)
    {
        var newChunkId = AllocateNewCluster(null);
        if (newChunkId < 0)
        {
            return -1;
        }

        EnsureClusterCellMapCapacity(newChunkId + 1);
        ClusterCellMap[newChunkId] = cellKey;
        CellClusterPool.AddCluster(cellKey, newChunkId);

        ref var cell = ref grid.GetCell(cellKey);
        Interlocked.Increment(ref cell.ClusterCount);

        var newBase = accessor.GetChunkAddress(newChunkId, true);
        Volatile.Write(ref *(ulong*)newBase, 0UL);

        InvalidateClusterZoneMaps(newChunkId);
        RecordClusterDrain(newChunkId);

        TyphonEvent.EmitSpatialGridClusterCellAssign(newChunkId, cellKey, (ushort)Math.Min(ArchetypeId, ushort.MaxValue));
        return newChunkId;
    }

    /// <summary>Drop every indexed field's cached min/max for one cluster, so the next <c>Widen</c> rebuilds them from the cluster's actual contents.</summary>
    private void InvalidateClusterZoneMaps(int clusterChunkId)
    {
        InvalidateZoneMapsIn(IndexSlots, clusterChunkId);
        InvalidateZoneMapsIn(TransientIndexSlots, clusterChunkId);
    }

    /// <inheritdoc cref="InvalidateClusterZoneMaps"/>
    private static void InvalidateZoneMapsIn<TStore>(ClusterIndexSlot<TStore>[] ixSlots, int clusterChunkId) where TStore : struct, IPageStore
    {
        if (ixSlots == null)
        {
            return;
        }

        for (var s = 0; s < ixSlots.Length; s++)
        {
            ref var ixSlot = ref ixSlots[s];
            for (var f = 0; f < ixSlot.Fields.Length; f++)
            {
                ixSlot.Fields[f].ZoneMap?.Invalidate(clusterChunkId);
            }
        }
    }
}
