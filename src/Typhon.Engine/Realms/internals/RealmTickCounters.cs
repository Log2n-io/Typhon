using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;

namespace Typhon.Engine.Internals;

/// <summary>
/// One realm's share of an archetype's per-tick maintenance counters — the rates, attributed to the realm that produced them.
/// </summary>
/// <remarks>
/// <para><b>Why this exists.</b> The <c>LastTick*</c> block on <see cref="ArchetypeClusterState"/> is per archetype and SUMMED ACROSS REALMS. Trace kind 67
/// already reports a realm's structure (grid, cells, clusters, reach, escapes, promotions), so a realm reads as a shape with no behaviour: how many of its
/// entities drifted, how tight its clusters are and what its maintenance cost is are all folded into one archetype-wide number that names no realm. Kind 67's
/// own documentation states the failure mode a realm-keyed copy of the archetype totals would produce — it "would report the sum across every realm under one
/// realm's name. That is worse than their absence, because it would look right."</para>
/// <para><b>AoS, and padded, per rule MD-03</b> (<c>rules/spatial.md</c>): "prefer AoS (cluster all per-element state into one padded struct) over SoA +
/// parallel padded arrays when padding is required — same memory cost, fewer cache-line fetches". The access pattern is the inverse of the one SoA serves:
/// ONE realm and ALL of its counters, twice a tick (a flush and a reset), never one counter across many realms. Parallel arrays would need a 64-byte stride
/// per element per array to stop realm <c>r</c> and realm <c>r+1</c> false-sharing inside each of them — that is a cache line touched per counter per flush,
/// against four or five for the whole struct.</para>
/// <para><b>The padding is INSIDE the struct on purpose.</b> <see cref="RealmArchetypeSpatial"/> is a plain sealed class, so the CLR lays it out with
/// <see cref="LayoutKind.Auto"/> and aligns this field to 8, not to a cache line; declaring it last would buy nothing. The leading and trailing reserve here
/// is what isolates the counters from the read-mostly fields — <c>Grid</c>, <c>PerCellIndex</c>, <c>CellClusterPool</c>, <c>ClusterReach</c> — that every
/// spatial query loads on the same object while the fence is mutating these. Same mechanism as <c>ArchetypeClusterState.PaddedFinalizeLock</c>, and the same
/// trap named at <c>DatabaseEngine.ClusterMigration.cs</c>: a plain-class field ORDER buys nothing, a padded struct does.</para>
/// </remarks>
[StructLayout(LayoutKind.Explicit, Size = 320)]
internal struct RealmTickCounters
{
    /// <summary>
    /// The counters, offset a full cache line into the struct so nothing the CLR happens to place before this field can share a line with them.
    /// </summary>
    [FieldOffset(64)]
    internal Fields F;

    /// <summary>
    /// The counter block itself — <see cref="LayoutKind.Sequential"/>, which is the C# default for a struct, so one <see cref="FieldOffsetAttribute"/> covers
    /// the whole set instead of one per counter. Widest members first, so the natural packing leaves no interior holes.
    /// </summary>
    /// <remarks>
    /// The same type serves as a worker's RUN TALLY (a local of <see cref="RealmFold"/>, plain increments, never shared) and as the realm's published block
    /// (shared, written only through <see cref="RealmFold.Flush"/>'s interlocked adds). One declaration for both is deliberate: a counter that exists on one
    /// side and not the other is a counter that silently never reaches the realm it was measured in.
    /// </remarks>
    [StructLayout(LayoutKind.Sequential)]
    internal struct Fields
    {
        /// <summary>Sum of the measured extent ratios behind <see cref="TightnessSamples"/>, for this realm.</summary>
        internal double TightnessExtentSum;

        /// <summary>Sum of the packing bounds behind <see cref="TightnessSamples"/>, for this realm.</summary>
        internal double TightnessBoundSum;

        /// <summary>Budget the admitted relocations of this realm were charged, in nanoseconds.</summary>
        internal double RelocationSpendNs;

        // ── the AABB refresh / drift scan ────────────────────────────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>Clusters of this realm examined by the intra-cell drifter scan this tick.</summary>
        internal int ClustersScanned;

        /// <summary>Entity slots of this realm the AABB refresh actually walked this tick.</summary>
        internal int SlotsScanned;

        /// <summary>Entities of this realm the intra-cell scan found outside their cluster's target region this tick. Detection, not outcome.</summary>
        internal int DriftersDetected;

        /// <summary>Drifters of this realm left in place because they were inside the drift dead zone.</summary>
        internal int DriftAbsorbed;

        /// <summary>Drifters of this realm for which placement found no better cluster.</summary>
        internal int DriftersUnplaced;

        /// <summary>Clusters of this realm that passed the intra-cell drift gate.</summary>
        internal int DriftGatedClusters;

        /// <summary>Clusters of this realm above the configured floor but below their cell's density-derived target, so the drift scan never ran.</summary>
        internal int DriftSuppressedByDensity;

        /// <summary>The subset of <see cref="DriftersUnplaced"/> whose cell offered no candidate at all.</summary>
        internal int DriftersUnplacedNoCandidate;

        /// <summary>Drifters of this realm whose cell had candidates but no capacity left this pass.</summary>
        internal int DriftersSpilled;

        /// <summary>Clusters of this realm that contributed a tightness reading this tick — the denominator of the two sums above.</summary>
        internal int TightnessSamples;

        // ── migration detection and execution ───────────────────────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>Migrations executed into this realm this tick. Its three kinds below sum to it exactly.</summary>
        internal int MigrationCount;

        /// <summary>Cell-crossing migrations executed into this realm.</summary>
        internal int CrossingsExecuted;

        /// <summary>Intra-cell relocations executed in this realm.</summary>
        internal int RelocationsExecuted;

        /// <summary>Repair moves executed in this realm.</summary>
        internal int RepairsExecuted;

        /// <summary>Crossings filed in this realm whose destination cell is not adjacent to the source cell.</summary>
        internal int JumpCrossings;

        /// <summary>Crossings filed in this realm whose position lay outside the grid and were clamped into an edge cell.</summary>
        internal int ClampedDestinations;

        /// <summary>Write-time crossing flags of this realm that the drain found describing an entity that is home, and dropped rather than executed.</summary>
        internal int StaleFlagsDropped;

        // ── the throttle's cut ──────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>Intra-cell relocations of this realm the budget refused.</summary>
        internal int RelocationsThrottled;

        /// <summary>Relocations of this realm dropped because a mandatory request already names the same source slot.</summary>
        internal int RelocationsSuperseded;

        /// <summary>Intra-cell relocations of this realm the throttle admitted into the drain prefix.</summary>
        internal int RelocationsAdmitted;

        /// <summary>Mandatory cell-crossing requests of this realm the throttle found queued and charged.</summary>
        internal int CrossingsQueued;

        /// <summary>Pinned claims in this realm rejected at drain time and therefore executed as first fit.</summary>
        internal int PinsRejected;

        // ── the repair planner ──────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>Entities of this realm re-packed by the repair path this tick.</summary>
        internal int RepairedEntityCount;

        /// <summary>Repair units admitted in this realm this tick.</summary>
        internal int RepairUnitCount;

        /// <summary>Repair units of this realm the remaining budget could not finish, and which were therefore never begun.</summary>
        internal int RepairUnitsRefused;

        /// <summary>Safety-valve admissions in this realm — repair units begun with insufficient budget because the cell was critical.</summary>
        internal int RepairValveFires;

        // ── arrivals and the cell tree ──────────────────────────────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>Distinct destination cells of this realm's drained cell crossings.</summary>
        internal int ArrivalCellsTouched;

        /// <summary>
        /// The most cell crossings into one destination cell of this realm this tick.
        /// </summary>
        /// <remarks>
        /// <b>A MAXIMUM, and the one member of this block that does not sum.</b> SO-01 already requires the telemetry roll-up to fold by KIND rather than
        /// uniformly — it is why <c>ClusterReach</c> maxes where the extensive counters sum — and the same applies to partitioning by realm: adding two
        /// realms' largest arrival runs would report a burst neither cell ever saw. <see cref="RealmFold.Flush"/> folds this one with a max.
        /// </remarks>
        internal int LargestArrivalRun;

        /// <summary>Cell halves of this realm promoted to a tree this tick.</summary>
        internal int CellTreePromotions;

        /// <summary>Cell halves of this realm that fell back from a tree this tick.</summary>
        internal int CellTreeDemotions;

        /// <summary>
        /// Non-zero once the fence has folded anything into this realm this tick. Both the per-tick reset and the emission gate read it, which is what makes
        /// them agree by construction: a realm the fence did not touch is not zeroed (there is nothing to zero) and is not emitted (SO-03).
        /// </summary>
        /// <remarks>
        /// A plain <see cref="byte"/> written without interlocking, because every writer writes the same value. Counters flush from parallel workers, so a
        /// LIST of touched realms would need a lock — new synchronisation for bookkeeping, on the fence's hot path, to save a walk over realms the archetype
        /// already has state in. A per-realm flag pays one load per present realm at reset instead, and nothing at all in the fold.
        /// </remarks>
        internal byte Touched;

        /// <summary>
        /// Fold one cluster's tightness reading in — the same gate and the same arithmetic as <c>ArchetypeClusterState.ClusterTightnessSample.Note</c>,
        /// because it is the same measurement partitioned by realm rather than a second one.
        /// </summary>
        /// <param name="active">The slice's "there is a grid with a positive cell size" flag; false makes this a no-op.</param>
        /// <param name="maxAxisExtent">The cluster's largest axis extent, in world units. Non-finite or negative is skipped.</param>
        /// <param name="inverseCellSize">One over the realm's cell edge, so the extent normalises with a multiply.</param>
        /// <param name="packingBound">The cell's packing bound, as a fraction of the cell edge — the denominator tightness is judged against.</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal void NoteTightness(bool active, float maxAxisExtent, float inverseCellSize, float packingBound)
        {
            // A cluster whose bound is still the Empty sentinel yields a non-finite extent; counting it would poison the mean with an infinity.
            if (!active || !float.IsFinite(maxAxisExtent) || maxAxisExtent < 0f)
            {
                return;
            }

            TightnessSamples++;
            TightnessExtentSum += maxAxisExtent * inverseCellSize;
            TightnessBoundSum += packingBound;
        }
    }
}

/// <summary>
/// The one way a fence phase attributes its counters to a realm: accumulate into a worker-local <see cref="RealmTickCounters.Fields"/> while the realm holds,
/// and fold it into that realm's published block when the realm changes — on the branch the phase already takes for the change.
/// </summary>
/// <remarks>
/// <para><b>Why a run fold and not a per-item atomic or a keyed map.</b> Every producer of these counters already branches when the realm changes from one
/// item to the next — the AABB refresh reloads its whole <c>AabbRealmFrame</c>, the migration loop reloads its grid, the detection scans reload cell size and
/// hysteresis margin — and the migration and repair queues are sorted with the realm in the key's high half (<c>(realm &lt;&lt; 32) | cellKey</c>), so a
/// realm's requests are ONE run there. Riding that branch costs a realm run what an atomic costs, on a branch that was already paying a grid reload; a
/// per-item atomic would pay it per cluster, and a keyed map would pay a probe per item to insure against a run-length degeneration the shipped hot loops
/// already run, more expensively.</para>
/// <para><b>Plain on the left, interlocked on the right.</b> <see cref="T"/> is a local of one worker; the realm's block is shared with every other worker
/// that touched the same realm this tick. That is the same discipline the archetype-wide counters have always used — a local tally, one atomic per counter
/// per slice — partitioned finer.</para>
/// </remarks>
internal struct RealmFold
{
    private RealmArchetypeSpatial _rs;

    /// <summary>This realm run's counts so far. Increment directly; the fold owns when they are published.</summary>
    internal RealmTickCounters.Fields T;

    /// <summary>
    /// Every run this fold has closed, summed — the SLICE's own totals, which are what the archetype-wide <c>LastTick*</c> counters are fed from.
    /// </summary>
    /// <remarks>
    /// Keeping both here is what lets the producer loop increment ONE set of counters. The archetype-wide figures are not moved by this change and not
    /// derived from the per-realm blocks either: they are accumulated alongside, from the same numbers, so a consumer reading them sees exactly what it saw
    /// before realms were partitioned — and the two agreeing is a checkable identity (SO-03) rather than a definition.
    /// </remarks>
    internal RealmTickCounters.Fields Slice;

    /// <summary>
    /// Begin accumulating into <paramref name="next"/>, closing the run in progress first. A no-op when the realm has not changed, which is the common case
    /// and the reason this is safe to call unconditionally from a per-item loop.
    /// </summary>
    internal void Switch(RealmArchetypeSpatial next)
    {
        if (ReferenceEquals(next, _rs))
        {
            return;
        }

        Flush();
        _rs = next;
    }

    /// <summary>Publish the run in progress into its realm and reset it. Idempotent: a second call with nothing accumulated does nothing.</summary>
    /// <remarks>
    /// <b>The slice totals are taken and the run is CLEARED even when there is no realm to publish to.</b> A realm lookup may legitimately answer null — see
    /// <c>ArchetypeClusterState.RealmSpatialForFold</c> — and returning early from the whole method would strand the run in <see cref="T"/>, where the next
    /// <see cref="Switch"/> publishes it under the NEXT realm's name. That is precisely the misattribution this type exists to prevent, and it would also
    /// shorten the archetype-wide totals that <see cref="Slice"/> feeds, silently. Only the publish is skipped.
    /// </remarks>
    internal void Flush()
    {
        ref var t = ref T;

        // The slice's own totals first, plain: this is a local of the one worker running the slice.
        ref var sl = ref Slice;
        sl.TightnessExtentSum += t.TightnessExtentSum;
        sl.TightnessBoundSum += t.TightnessBoundSum;
        sl.RelocationSpendNs += t.RelocationSpendNs;
        sl.ClustersScanned += t.ClustersScanned;
        sl.SlotsScanned += t.SlotsScanned;
        sl.DriftersDetected += t.DriftersDetected;
        sl.DriftAbsorbed += t.DriftAbsorbed;
        sl.DriftersUnplaced += t.DriftersUnplaced;
        sl.DriftGatedClusters += t.DriftGatedClusters;
        sl.DriftSuppressedByDensity += t.DriftSuppressedByDensity;
        sl.DriftersUnplacedNoCandidate += t.DriftersUnplacedNoCandidate;
        sl.DriftersSpilled += t.DriftersSpilled;
        sl.TightnessSamples += t.TightnessSamples;
        sl.MigrationCount += t.MigrationCount;
        sl.CrossingsExecuted += t.CrossingsExecuted;
        sl.RelocationsExecuted += t.RelocationsExecuted;
        sl.RepairsExecuted += t.RepairsExecuted;
        sl.JumpCrossings += t.JumpCrossings;
        sl.ClampedDestinations += t.ClampedDestinations;
        sl.StaleFlagsDropped += t.StaleFlagsDropped;
        sl.RelocationsThrottled += t.RelocationsThrottled;
        sl.RelocationsSuperseded += t.RelocationsSuperseded;
        sl.RelocationsAdmitted += t.RelocationsAdmitted;
        sl.CrossingsQueued += t.CrossingsQueued;
        sl.PinsRejected += t.PinsRejected;
        sl.RepairedEntityCount += t.RepairedEntityCount;
        sl.RepairUnitCount += t.RepairUnitCount;
        sl.RepairUnitsRefused += t.RepairUnitsRefused;
        sl.RepairValveFires += t.RepairValveFires;
        sl.ArrivalCellsTouched += t.ArrivalCellsTouched;
        sl.CellTreePromotions += t.CellTreePromotions;
        sl.CellTreeDemotions += t.CellTreeDemotions;
        // Not a sum here either — the slice's largest run is the largest of its realms' largest, not their total.
        if (t.LargestArrivalRun > sl.LargestArrivalRun)
        {
            sl.LargestArrivalRun = t.LargestArrivalRun;
        }

        var rs = _rs;
        if (rs == null)
        {
            // No realm to attribute this run to, but the slice has already banked it above and the run is cleared below, so nothing is stranded and nothing
            // is double-counted when the next realm opens.
            t = default;
            return;
        }

        ref var c = ref rs.Counters.F;

        // Each counter is gated on its own value rather than the block being gated as a whole. A phase produces a handful of these and leaves the rest at
        // zero, so the gate turns a block of 30-odd atomics into the two or three the phase actually measured — and the compare is register-local.
        var any = false;
        any |= Add(ref c.ClustersScanned, t.ClustersScanned);
        any |= Add(ref c.SlotsScanned, t.SlotsScanned);
        any |= Add(ref c.DriftersDetected, t.DriftersDetected);
        any |= Add(ref c.DriftAbsorbed, t.DriftAbsorbed);
        any |= Add(ref c.DriftersUnplaced, t.DriftersUnplaced);
        any |= Add(ref c.DriftGatedClusters, t.DriftGatedClusters);
        any |= Add(ref c.DriftSuppressedByDensity, t.DriftSuppressedByDensity);
        any |= Add(ref c.DriftersUnplacedNoCandidate, t.DriftersUnplacedNoCandidate);
        any |= Add(ref c.DriftersSpilled, t.DriftersSpilled);
        any |= Add(ref c.TightnessSamples, t.TightnessSamples);
        any |= Add(ref c.MigrationCount, t.MigrationCount);
        any |= Add(ref c.CrossingsExecuted, t.CrossingsExecuted);
        any |= Add(ref c.RelocationsExecuted, t.RelocationsExecuted);
        any |= Add(ref c.RepairsExecuted, t.RepairsExecuted);
        any |= Add(ref c.JumpCrossings, t.JumpCrossings);
        any |= Add(ref c.ClampedDestinations, t.ClampedDestinations);
        any |= Add(ref c.StaleFlagsDropped, t.StaleFlagsDropped);
        any |= Add(ref c.RelocationsThrottled, t.RelocationsThrottled);
        any |= Add(ref c.RelocationsSuperseded, t.RelocationsSuperseded);
        any |= Add(ref c.RelocationsAdmitted, t.RelocationsAdmitted);
        any |= Add(ref c.CrossingsQueued, t.CrossingsQueued);
        any |= Add(ref c.PinsRejected, t.PinsRejected);
        any |= Add(ref c.RepairedEntityCount, t.RepairedEntityCount);
        any |= Add(ref c.RepairUnitCount, t.RepairUnitCount);
        any |= Add(ref c.RepairUnitsRefused, t.RepairUnitsRefused);
        any |= Add(ref c.RepairValveFires, t.RepairValveFires);
        any |= Add(ref c.ArrivalCellsTouched, t.ArrivalCellsTouched);
        any |= Add(ref c.CellTreePromotions, t.CellTreePromotions);
        any |= Add(ref c.CellTreeDemotions, t.CellTreeDemotions);
        any |= AddDouble(ref c.TightnessExtentSum, t.TightnessExtentSum);
        any |= AddDouble(ref c.TightnessBoundSum, t.TightnessBoundSum);
        any |= AddDouble(ref c.RelocationSpendNs, t.RelocationSpendNs);
        // The one member that does not sum — see its own remarks.
        any |= Max(ref c.LargestArrivalRun, t.LargestArrivalRun);

        if (any)
        {
            RealmArchetypeSpatial.AssertNotNone(rs);
            // Plain store, after the counters. Every writer writes 1, so the race is benign; what it must not do is become visible BEFORE the counters it
            // vouches for, and the fence phase barrier — the same one the per-tick reset relies on — is what orders it against the reader.
            c.Touched = 1;
        }

        t = default;
    }

    /// <summary>
    /// Add to one of <paramref name="rs"/>'s counters directly, stamping the realm as touched. For producers with no RUN to fold — a promotion or a
    /// demotion happens once, at a point that already holds the realm, and has no sequence of same-realm items behind it to amortise a fold over.
    /// </summary>
    internal static void Bump(RealmArchetypeSpatial rs, ref int counter, int by = 1)
    {
        if (rs == null || by == 0)
        {
            return;
        }

        RealmArchetypeSpatial.AssertNotNone(rs);
        Interlocked.Add(ref counter, by);
        rs.Counters.F.Touched = 1;
    }

    private static bool Add(ref int target, int addend)
    {
        if (addend == 0)
        {
            return false;
        }

        Interlocked.Add(ref target, addend);
        return true;
    }

    private static bool AddDouble(ref double target, double addend)
    {
        if (addend == 0d)
        {
            return false;
        }

        ArchetypeClusterState.InterlockedAddDouble(ref target, addend);
        return true;
    }

    private static bool Max(ref int target, int candidate)
    {
        if (candidate <= 0)
        {
            return false;
        }

        // No Interlocked.Max in .NET; the CAS loop is the standard shape and this runs once per realm run at most. The return says whether this call
        // STORED, which is what `any` — and therefore the Touched flag — must mean: a candidate that loses to a larger value already published by another
        // worker changed nothing here, and that worker's own flush is what stamped the realm.
        while (true)
        {
            var current = Volatile.Read(ref target);
            if (candidate <= current)
            {
                return false;
            }

            if (Interlocked.CompareExchange(ref target, candidate, current) == current)
            {
                return true;
            }
        }
    }
}
