using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Typhon.Engine.Tests.Profiler;
using Typhon.Profiler;
using Typhon.Profiler.Events;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests.Realms;

/// <summary>
/// Realms C1c: the tick fence runs every realm's spatial maintenance in that realm's grid — crossing detection, the migration drain, the AABB refresh,
/// the finalization drain and the reach. The same population moves identically in three realms (two sharing a cell geometry, one not) through the
/// serial fence and the parallel one; afterwards each realm must hold exactly its own entities, filed in its own cells, and answer its queries like a
/// brute force over the positions written.
/// </summary>
[TestFixture]
[NonParallelizable]
class RealmFenceTests : TestBase<RealmFenceTests>
{
    private const float World = 100f;
    private const int PerRealm = 120;
    private const int RealmCount = 3;
    private const int Rounds = 3;
    private const int SerialArm = 0;

    /// <summary>
    /// Stop and RESET the profiler after every case, not merely detach.
    /// </summary>
    /// <remarks>
    /// Detaching fixes the disposed-exporter hazard; it does not clear session or thread-slot state, and a case that throws before its own cleanup leaves
    /// the profiler started for whatever runs next. Every other profiler fixture already does this — <c>RealmPolicyTests</c> records what it cost to be the
    /// exception: a background thread enumerating a disposed collection kills the test HOST, and the remaining gated tests then report as "not run" rather
    /// than as failures.
    /// </remarks>
    [TearDown]
    public void DetachProfilerExporters()
    {
        try { TyphonProfiler.Stop(); } catch { /* a case that never started one, or already stopped it */ }
        TyphonProfiler.ResetForTests();
    }

    /// <summary>The minimum a profiler session needs to start; this fixture reads records, not the session's own metadata.</summary>
    private static ProfilerSessionMetadata TraceMetadata() => new(
        systems: [], archetypes: [], componentTypes: [], workerCount: 0, baseTickRate: 1000f,
        startTimestamp: System.Diagnostics.Stopwatch.GetTimestamp(), stopwatchFrequency: System.Diagnostics.Stopwatch.Frequency,
        startedUtc: DateTime.UtcNow);

    private static SpatialGridConfig Grid(double cellSize) => SpatialGridConfig.Flat(new Vector2(0, 0), new Vector2(World, World), cellSize);

    private DatabaseEngine ThreeRealms()
    {
        var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<RealmPos>();
        dbe.ConfigureRealms(3);
        dbe.ConfigureSpatialGrid(Grid(10));
        dbe.Realms.Register(new RealmId(1), RealmConfig.SimulatedAlways(Grid(10)));
        dbe.Realms.Register(new RealmId(2), RealmConfig.SimulatedAlways(Grid(25)));
        dbe.InitializeArchetypes();
        return dbe;
    }

    private static ArchetypeClusterState StateOf(DatabaseEngine dbe) => dbe._archetypeStates[Archetype<RealmUnit>.Metadata.ArchetypeId].ClusterState;

    /// <summary>Round <paramref name="round"/>'s box for entity <paramref name="tag"/>: a pure function, the same in every realm.</summary>
    private static AABB2F BoxOf(int round, int tag)
    {
        var rng = new Random(unchecked(round * 7919 + tag * 104729));
        var x = (float)(rng.NextDouble() * (World - 2) + 1);
        var y = (float)(rng.NextDouble() * (World - 2) + 1);
        var half = (float)(rng.NextDouble() * 0.4);
        return new AABB2F { MinX = x - half, MinY = y - half, MaxX = x + half, MaxY = y + half };
    }

    private static void SpawnMirrored(DatabaseEngine dbe)
    {
        using var tx = dbe.CreateQuickTransaction();
        for (var tag = 0; tag < PerRealm; tag++)
        {
            var b = BoxOf(0, tag);
            for (ushort r = 0; r < RealmCount; r++)
            {
                tx.Spawn<RealmUnit>(RealmUnit.Pos.Set(new RealmPos { Bounds = b, Realm = r, Tag = tag }));
            }
        }

        tx.Commit();
    }

    /// <summary>
    /// Every entity to its round-<paramref name="round"/> box, keeping its realm key and tag — through <c>WriteSpatial</c> (the write barrier flags the
    /// crossing, drained per realm) or through <c>OpenMut</c> (no flag: the fence's dirty-bit scan detects it, per cluster realm).
    /// </summary>
    private static void MoveAll(DatabaseEngine dbe, int round, bool viaOpenMut, bool rotate = false)
    {
        if (viaOpenMut)
        {
            var moves = new List<(EntityId id, ushort realm, int tag)>();
            using (var rtx = dbe.CreateQuickTransaction())
            {
                var reader = rtx.For<RealmUnit>();
                try
                {
                    foreach (var cluster in reader.GetClusterEnumerator())
                    {
                        var occupied = cluster.OccupancyBits;
                        while (occupied != 0)
                        {
                            var slot = BitOperations.TrailingZeroCount(occupied);
                            occupied &= occupied - 1;
                            var v = cluster.GetReadOnly(RealmUnit.Pos, slot);
                            moves.Add((cluster.GetEntityId(slot), v.Realm, v.Tag));
                        }
                    }
                }
                finally
                {
                    reader.Dispose();
                }
            }

            using var wtx = dbe.CreateQuickTransaction();
            foreach (var (id, realm, tag) in moves)
            {
                ref var pos = ref wtx.OpenMut(id).Write(RealmUnit.Pos);
                pos = new RealmPos { Bounds = BoxOf(round, tag), Realm = rotate ? (ushort)((realm + 1) % RealmCount) : realm, Tag = tag };
            }

            wtx.Commit();
            return;
        }

        using var tx = dbe.CreateQuickTransaction();
        var accessor = tx.For<RealmUnit>();
        try
        {
            foreach (var cluster in accessor.GetClusterEnumerator())
            {
                var bits = cluster.OccupancyBits;
                while (bits != 0)
                {
                    var slot = BitOperations.TrailingZeroCount(bits);
                    bits &= bits - 1;
                    var current = cluster.GetReadOnly(RealmUnit.Pos, slot);
                    var realm = rotate ? (ushort)((current.Realm + 1) % RealmCount) : current.Realm;
                    cluster.WriteSpatial(RealmUnit.Pos, slot, current with { Bounds = BoxOf(round, current.Tag), Realm = realm });
                }
            }
        }
        finally
        {
            accessor.Dispose();
        }

        tx.Commit();
    }

    private static void RunFences(DatabaseEngine dbe, int workers, ref long tick)
    {
        if (workers == SerialArm)
        {
            // Two fences: the first detects and drains, the second catches what the first's AABB refresh filed.
            dbe.WriteTickFence(++tick);
            dbe.WriteTickFence(++tick);
            return;
        }

        var ticks = 0;
        using var runtime = TyphonRuntime.Create(dbe, schedule =>
        {
            schedule.PublicTrack.DeclareDag("Test").CallbackSystem("Count", _ => Interlocked.Increment(ref ticks));
        }, new RuntimeOptions { WorkerCount = workers, BaseTickRate = 100, EnableParallelFence = true });
        Exception unhandled = null;
        runtime.Scheduler.UnhandledExceptionCallback = (_, _, ex) => Interlocked.CompareExchange(ref unhandled, ex, null);
        runtime.Start();
        SpinWait.SpinUntil(() => Volatile.Read(ref ticks) >= 4 && runtime.CurrentTickNumber >= 4, TimeSpan.FromSeconds(15));
        runtime.Shutdown();
        Assert.That(unhandled, Is.Null, $"the parallel fence threw: {unhandled}");
        Assert.That(runtime.CurrentTickNumber, Is.GreaterThanOrEqualTo(4), "the runtime must have ticked");
    }

    [Test]
    [CancelAfter(60_000)]
    [VerifiesRule("SQ-08")]
    public void MovingPopulations_StayInTheirRealms_FiledInTheirOwnCells([Values(SerialArm, 2, 8)] int workers, [Values] bool viaOpenMut)
    {
        using var dbe = ThreeRealms();
        SpawnMirrored(dbe);
        var tick = 0L;
        dbe.WriteTickFence(++tick);
        for (var round = 1; round <= Rounds; round++)
        {
            MoveAll(dbe, round, viaOpenMut);
            RunFences(dbe, workers, ref tick);
            var arm = workers == SerialArm ? "serial" : $"W={workers}";
            AssertRealmsConsistent(dbe, round, $"round {round}, {arm}, {(viaOpenMut ? "OpenMut" : "WriteSpatial")}");
        }
    }

    /// <summary>
    /// Realms C4 at scale: every entity moves AND changes realm every round (0 → 1 → 2 → 0; realm 2 has another cell geometry), through both fences
    /// and both write paths. Each realm keeps one entity per tag, so the same invariants and the same brute-force oracle hold after every round.
    /// </summary>
    [Test]
    [CancelAfter(60_000)]
    [VerifiesRule("RM-03")]
    public void RotatingRealms_EveryEntityChangesRealmEachRound_TheRealmsStayConsistent([Values(SerialArm, 2, 8)] int workers, [Values] bool viaOpenMut)
    {
        using var dbe = ThreeRealms();
        SpawnMirrored(dbe);
        var tick = 0L;
        dbe.WriteTickFence(++tick);
        var cs = StateOf(dbe);
        for (var round = 1; round <= Rounds; round++)
        {
            MoveAll(dbe, round, viaOpenMut, rotate: true);
            RunFences(dbe, workers, ref tick);
            var arm = workers == SerialArm ? "serial" : $"W={workers}";
            AssertRealmsConsistent(dbe, round, $"rotation round {round}, {arm}, {(viaOpenMut ? "OpenMut" : "WriteSpatial")}");
        }

        Assert.That(cs.LastTickRealmKeyReverts, Is.Zero);
    }

    /// <summary>
    /// Every per-realm maintenance counter sums, across the realms present, to the archetype-wide <c>LastTick*</c> figure it partitions — and more than one
    /// realm carries a share of it.
    /// </summary>
    /// <remarks>
    /// <para><b>The second half is the one that can fail alone.</b> The sum identity holds trivially if the fold attributes everything to realm 0, which is
    /// exactly what a bug that reads the wrong realm id would do; the population here is mirrored across three realms, so a fold that names one realm is
    /// caught by the count of realms that scanned anything, not by the sum.</para>
    /// <para><b>Serial arm.</b> The counters describe the most recently completed tick, and the parallel arm's runtime ticks on its own schedule — so the
    /// tick they would describe is not the tick this test staged. Staging the precondition is the point; waiting for a runtime to happen to be busy on its
    /// last tick would make the assertions vacuous exactly when they passed.</para>
    /// </remarks>
    [Test]
    [CancelAfter(30_000)]
    [VerifiesRule("SO-03")]
    public void PerRealmCounters_SumToTheArchetypeTotals_AndNameMoreThanOneRealm()
    {
        using var dbe = ThreeRealms();
        SpawnMirrored(dbe);
        var tick = 0L;
        dbe.WriteTickFence(++tick);
        var cs = StateOf(dbe);

        MoveAll(dbe, 1, viaOpenMut: false);
        dbe.WriteTickFence(++tick);

        // WHICH identities this scenario actually exercises. An identity over two zeroes holds for any implementation, so the counters this round drives
        // are asserted non-zero by name: a change that stops attributing them shows up as a sum mismatch, and a change that stops PRODUCING them shows up
        // here rather than silently turning the check above vacuous.
        Assert.Multiple(() =>
        {
            Assert.That(cs.LastTickClustersScanned, Is.GreaterThan(0), "AABB refresh / drift scan");
            Assert.That(cs.LastTickMigrationCount, Is.GreaterThan(0), "migration execution");
            Assert.That(cs.LastTickCrossingsExecuted, Is.GreaterThan(0), "migration execution, by kind");
            Assert.That(cs.LastTickJumpCrossings, Is.GreaterThan(0), "crossing detection");
            Assert.That(cs.LastTickCrossingsQueued, Is.GreaterThan(0), "the throttle's cut");
            Assert.That(cs.LastTickArrivalCellsTouched, Is.GreaterThan(0), "the arrivals pass");
            Assert.That(cs.LastTickLargestArrivalRun, Is.GreaterThan(0), "the arrivals pass, maxed rather than summed");
        });

        // NOT exercised by this fixture, and the identity over them is therefore vacuous here: the relocation, repair and cell-tree counters all read zero,
        // and ClampedDestinations needs a write outside the world extent.
        //
        // **The reason is the WORKLOAD, not the configuration** — an earlier note here said repair was off because the grid configures no budget, which is
        // false: SpatialGridConfig.Flat defaults ReclusterBudgetMs to 1.0, so the planner runs every tick. It finds nothing because 120 entities over a
        // 100x100 world never degrade a cluster past ClusterRepairExtentRatio, and the drift scan never nominates a relocation for the same reason. Closing
        // the gap therefore needs a DENSER, more degraded population, not a config change — which is a different fixture, not a parameter on this one.
        AssertRealmCountersPartitionTheTotals(cs);
    }

    /// <summary>
    /// The verifier above, as a method, so the mutant below can drive the SAME assertions with a state that violates the rule. Proving a verifier can fail
    /// is the point of <see cref="RuleMutantAttribute"/>: a green check that cannot go red reports confidence rather than evidence.
    /// </summary>
    private static void AssertRealmCountersPartitionTheTotals(ArchetypeClusterState cs)
    {
        var realmsThatScanned = 0;
        var largestArrivalRun = 0;
        long clustersScanned = 0;
        long slotsScanned = 0;
        long driftersDetected = 0;
        long driftAbsorbed = 0;
        long driftersUnplaced = 0;
        long driftGatedClusters = 0;
        long driftSuppressedByDensity = 0;
        long driftersUnplacedNoCandidate = 0;
        long driftersSpilled = 0;
        long tightnessSamples = 0;
        long migrationCount = 0;
        long crossingsExecuted = 0;
        long relocationsExecuted = 0;
        long repairsExecuted = 0;
        long jumpCrossings = 0;
        long clampedDestinations = 0;
        long staleFlagsDropped = 0;
        long relocationsThrottled = 0;
        long relocationsSuperseded = 0;
        long relocationsAdmitted = 0;
        long crossingsQueued = 0;
        long pinsRejected = 0;
        long repairedEntityCount = 0;
        long repairUnitCount = 0;
        long repairUnitsRefused = 0;
        long repairValveFires = 0;
        long arrivalCellsTouched = 0;
        long cellTreePromotions = 0;
        long cellTreeDemotions = 0;
        var tightnessExtentSum = 0d;
        var tightnessBoundSum = 0d;
        var relocationSpendNs = 0d;
        foreach (var rs in cs.PresentRealmSpatial)
        {
            ref var c = ref rs.Counters.F;
            if (c.ClustersScanned > 0)
            {
                realmsThatScanned++;
            }

            clustersScanned += c.ClustersScanned;
            slotsScanned += c.SlotsScanned;
            driftersDetected += c.DriftersDetected;
            driftAbsorbed += c.DriftAbsorbed;
            driftersUnplaced += c.DriftersUnplaced;
            driftGatedClusters += c.DriftGatedClusters;
            driftSuppressedByDensity += c.DriftSuppressedByDensity;
            driftersUnplacedNoCandidate += c.DriftersUnplacedNoCandidate;
            driftersSpilled += c.DriftersSpilled;
            tightnessSamples += c.TightnessSamples;
            migrationCount += c.MigrationCount;
            crossingsExecuted += c.CrossingsExecuted;
            relocationsExecuted += c.RelocationsExecuted;
            repairsExecuted += c.RepairsExecuted;
            jumpCrossings += c.JumpCrossings;
            clampedDestinations += c.ClampedDestinations;
            staleFlagsDropped += c.StaleFlagsDropped;
            relocationsThrottled += c.RelocationsThrottled;
            relocationsSuperseded += c.RelocationsSuperseded;
            relocationsAdmitted += c.RelocationsAdmitted;
            crossingsQueued += c.CrossingsQueued;
            pinsRejected += c.PinsRejected;
            repairedEntityCount += c.RepairedEntityCount;
            repairUnitCount += c.RepairUnitCount;
            repairUnitsRefused += c.RepairUnitsRefused;
            repairValveFires += c.RepairValveFires;
            arrivalCellsTouched += c.ArrivalCellsTouched;
            cellTreePromotions += c.CellTreePromotions;
            cellTreeDemotions += c.CellTreeDemotions;
            tightnessExtentSum += c.TightnessExtentSum;
            tightnessBoundSum += c.TightnessBoundSum;
            relocationSpendNs += c.RelocationSpendNs;
            // Not a sum — see the counter's own remarks, and SO-01's "fold by KIND, not uniformly".
            if (c.LargestArrivalRun > largestArrivalRun)
            {
                largestArrivalRun = c.LargestArrivalRun;
            }
        }

        Assert.Multiple(() =>
        {
            Assert.That(clustersScanned, Is.EqualTo(cs.LastTickClustersScanned), "clusters scanned");
            Assert.That(slotsScanned, Is.EqualTo(cs.LastTickSlotsScanned), "slots scanned");
            Assert.That(driftersDetected, Is.EqualTo(cs.LastTickDriftersDetected), "drifters detected");
            Assert.That(driftAbsorbed, Is.EqualTo(cs.LastTickDriftAbsorbedCount), "drift absorbed");
            Assert.That(driftersUnplaced, Is.EqualTo(cs.LastTickDriftersUnplaced), "drifters unplaced");
            Assert.That(driftGatedClusters, Is.EqualTo(cs.LastTickDriftGatedClusters), "drift-gated clusters");
            Assert.That(driftSuppressedByDensity, Is.EqualTo(cs.LastTickDriftSuppressedByDensity), "drift suppressed by density");
            Assert.That(driftersUnplacedNoCandidate, Is.EqualTo(cs.LastTickDriftersUnplacedNoCandidate), "unplaced, no candidate");
            Assert.That(driftersSpilled, Is.EqualTo(cs.LastTickDriftersSpilled), "drifters spilled");
            Assert.That(tightnessSamples, Is.EqualTo(cs.LastTickTightnessSamples), "tightness samples");
            Assert.That(migrationCount, Is.EqualTo(cs.LastTickMigrationCount), "migrations executed");
            Assert.That(crossingsExecuted, Is.EqualTo(cs.LastTickCrossingsExecuted), "crossings executed");
            Assert.That(relocationsExecuted, Is.EqualTo(cs.LastTickRelocationsExecuted), "relocations executed");
            Assert.That(repairsExecuted, Is.EqualTo(cs.LastTickRepairsExecuted), "repairs executed");
            Assert.That(jumpCrossings, Is.EqualTo(cs.LastTickJumpCrossings), "jump crossings");
            Assert.That(clampedDestinations, Is.EqualTo(cs.LastTickClampedDestinations), "clamped destinations");
            Assert.That(staleFlagsDropped, Is.EqualTo(cs.LastTickStaleFlagsDropped), "stale flags dropped");
            Assert.That(relocationsThrottled, Is.EqualTo(cs.LastTickRelocationsThrottled), "relocations throttled");
            Assert.That(relocationsSuperseded, Is.EqualTo(cs.LastTickRelocationsSuperseded), "relocations superseded");
            Assert.That(relocationsAdmitted, Is.EqualTo(cs.LastTickRelocationsAdmitted), "relocations admitted");
            Assert.That(crossingsQueued, Is.EqualTo(cs.LastTickCrossingsQueued), "crossings queued");
            Assert.That(pinsRejected, Is.EqualTo(cs.LastTickPinsRejected), "pins rejected");
            Assert.That(repairedEntityCount, Is.EqualTo(cs.LastTickRepairedEntityCount), "entities repaired");
            Assert.That(repairUnitCount, Is.EqualTo(cs.LastTickRepairUnitCount), "repair units");
            Assert.That(repairUnitsRefused, Is.EqualTo(cs.LastTickRepairUnitsRefused), "repair units refused");
            Assert.That(repairValveFires, Is.EqualTo(cs.LastTickRepairValveFires), "repair valve fires");
            Assert.That(arrivalCellsTouched, Is.EqualTo(cs.LastTickArrivalCellsTouched), "arrival cells touched");
            Assert.That(cellTreePromotions, Is.EqualTo(cs.LastTickCellTreePromotions), "cell-tree promotions");
            Assert.That(cellTreeDemotions, Is.EqualTo(cs.LastTickCellTreeDemotions), "cell-tree demotions");
            // Summed in a different association than the archetype-wide fold (per realm run, against per slice), so an exact compare would be asserting
            // something about floating-point grouping rather than about the attribution.
            Assert.That(tightnessExtentSum, Is.EqualTo(cs.LastTickTightnessExtentSum).Within(1e-6), "tightness extent sum");
            Assert.That(tightnessBoundSum, Is.EqualTo(cs.LastTickTightnessBoundSum).Within(1e-6), "tightness bound sum");
            Assert.That(relocationSpendNs, Is.EqualTo(cs.LastTickRelocationSpendNs).Within(1e-6), "relocation spend (ns)");
            Assert.That(largestArrivalRun, Is.EqualTo(cs.LastTickLargestArrivalRun), "largest arrival run (a MAX across realms, not a sum)");
        });

        // OUTSIDE the multiple block, and not only for tidiness. The sums are one claim each and a reader wants every mismatch at once; this is the separate
        // claim that the attribution names more than one realm, and it is the one the mutant below drives — Assert.Multiple would wrap it in a
        // MultipleAssertException, which RuleMutants.AssertDetects reads as a crashed mutant rather than as the verifier rejecting.
        Assert.That(realmsThatScanned, Is.GreaterThan(1), OneRealmMarker);
    }

    private const string OneRealmMarker =
        "the population is mirrored across three realms, so a fold that attributes the whole tick to one realm is the failure this catches";

    /// <summary>
    /// The bug the verifier exists to catch, staged directly: every realm's share folded into realm 0, which is what a fold reading the wrong realm id
    /// produces. The SUM identity still holds under it — nothing was created or lost — so a verifier built only on the identity would stay green here.
    /// That is why the realm count is asserted separately, and this mutant is what shows the difference matters.
    /// </summary>
    [Test]
    [CancelAfter(30_000)]
    [RuleMutant("SO-01")]
    public void Mutant_EveryRealmsShareFoldedIntoOne_IsReported()
    {
        using var dbe = ThreeRealms();
        SpawnMirrored(dbe);
        var tick = 0L;
        dbe.WriteTickFence(++tick);
        var cs = StateOf(dbe);

        MoveAll(dbe, 1, viaOpenMut: false);
        dbe.WriteTickFence(++tick);
        Assert.That(cs.LastTickClustersScanned, Is.GreaterThan(0), "the mutant needs a tick with work in it");

        RuleMutants.AssertDetects("SO-01", OneRealmMarker, () =>
        {
            var sink = cs.RealmSpatial[0];
            foreach (var rs in cs.PresentRealmSpatial)
            {
                if (ReferenceEquals(rs, sink))
                {
                    continue;
                }

                ref var from = ref rs.Counters.F;
                ref var into = ref sink.Counters.F;
                into.ClustersScanned += from.ClustersScanned;
                into.SlotsScanned += from.SlotsScanned;
                into.DriftersDetected += from.DriftersDetected;
                into.DriftAbsorbed += from.DriftAbsorbed;
                into.DriftersUnplaced += from.DriftersUnplaced;
                into.DriftGatedClusters += from.DriftGatedClusters;
                into.DriftSuppressedByDensity += from.DriftSuppressedByDensity;
                into.DriftersUnplacedNoCandidate += from.DriftersUnplacedNoCandidate;
                into.DriftersSpilled += from.DriftersSpilled;
                into.TightnessSamples += from.TightnessSamples;
                into.MigrationCount += from.MigrationCount;
                into.CrossingsExecuted += from.CrossingsExecuted;
                into.RelocationsExecuted += from.RelocationsExecuted;
                into.RepairsExecuted += from.RepairsExecuted;
                into.JumpCrossings += from.JumpCrossings;
                into.ClampedDestinations += from.ClampedDestinations;
                into.StaleFlagsDropped += from.StaleFlagsDropped;
                into.RelocationsThrottled += from.RelocationsThrottled;
                into.RelocationsSuperseded += from.RelocationsSuperseded;
                into.RelocationsAdmitted += from.RelocationsAdmitted;
                into.CrossingsQueued += from.CrossingsQueued;
                into.PinsRejected += from.PinsRejected;
                into.RepairedEntityCount += from.RepairedEntityCount;
                into.RepairUnitCount += from.RepairUnitCount;
                into.RepairUnitsRefused += from.RepairUnitsRefused;
                into.RepairValveFires += from.RepairValveFires;
                into.ArrivalCellsTouched += from.ArrivalCellsTouched;
                into.CellTreePromotions += from.CellTreePromotions;
                into.CellTreeDemotions += from.CellTreeDemotions;
                into.TightnessExtentSum += from.TightnessExtentSum;
                into.TightnessBoundSum += from.TightnessBoundSum;
                into.RelocationSpendNs += from.RelocationSpendNs;
                if (from.LargestArrivalRun > into.LargestArrivalRun)
                {
                    into.LargestArrivalRun = from.LargestArrivalRun;
                }

                from = default;
            }

            AssertRealmCountersPartitionTheTotals(cs);
        });
    }

    /// <summary>
    /// SO-03: a realm the fence did not touch is not marked, and its counters stay zero — so its absence from the rates record means "not measured" rather
    /// than "measured as zero", which is the distinction SO-01 reserves.
    /// </summary>
    /// <remarks>The precondition is STAGED by emptying realm 2, not waited for: a realm that happens to be quiet on some tick would make this pass for a
    /// reason the test does not control.</remarks>
    [Test]
    [CancelAfter(30_000)]
    [VerifiesRule("SO-03")]
    public void ARealmTheFenceDidNotTouch_IsNotMarked_AndItsCountersStayZero()
    {
        using var dbe = ThreeRealms();
        SpawnMirrored(dbe);
        var tick = 0L;
        dbe.WriteTickFence(++tick);
        var cs = StateOf(dbe);

        var realm2 = new List<EntityId>();
        using (var tx = dbe.CreateQuickTransaction())
        {
            var reader = tx.For<RealmUnit>();
            try
            {
                foreach (var cluster in reader.GetClusterEnumerator())
                {
                    var bits = cluster.OccupancyBits;
                    while (bits != 0)
                    {
                        var slot = BitOperations.TrailingZeroCount(bits);
                        bits &= bits - 1;
                        if (cluster.GetReadOnly(RealmUnit.Pos, slot).Realm == 2)
                        {
                            realm2.Add(cluster.GetEntityId(slot));
                        }
                    }
                }
            }
            finally
            {
                reader.Dispose();
            }

            foreach (var id in realm2)
            {
                tx.Destroy(id);
            }

            tx.Commit();
        }

        dbe.WriteTickFence(++tick);
        MoveAll(dbe, 1, viaOpenMut: false);
        dbe.WriteTickFence(++tick);

        var emptied = cs.RealmSpatial[2];
        Assert.Multiple(() =>
        {
            Assert.That(cs.LastTickClustersScanned, Is.GreaterThan(0), "the other realms must still be working, or this proves nothing");
            Assert.That(emptied.Counters.F.Touched, Is.Zero, "a realm the fence did no work in must not be marked, or the gate emits a record of zeros");
            Assert.That(emptied.Counters.F.ClustersScanned, Is.Zero);
            Assert.That(emptied.Counters.F.SlotsScanned, Is.Zero);
            Assert.That(emptied.Counters.F.TightnessSamples, Is.Zero);
        });
    }

    /// <summary>
    /// The per-realm counters describe ONE tick: a second fence over a settled world clears them rather than carrying the previous tick's work.
    /// </summary>
    [Test]
    [CancelAfter(30_000)]
    public void PerRealmCounters_DescribeOneTick_AndAreClearedByTheNextFence()
    {
        using var dbe = ThreeRealms();
        SpawnMirrored(dbe);
        var tick = 0L;
        dbe.WriteTickFence(++tick);
        var cs = StateOf(dbe);

        MoveAll(dbe, 1, viaOpenMut: false);
        dbe.WriteTickFence(++tick);
        var busy = 0;
        foreach (var rs in cs.PresentRealmSpatial)
        {
            busy += rs.Counters.F.ClustersScanned;
        }

        Assert.That(busy, Is.GreaterThan(0), "the staged round must have done work, or the clear below is not observable");

        // Two quiet fences: the first drains whatever the busy one filed, the second sees a settled world.
        dbe.WriteTickFence(++tick);
        dbe.WriteTickFence(++tick);

        foreach (var rs in cs.PresentRealmSpatial)
        {
            Assert.That(rs.Counters.F.ClustersScanned, Is.Zero, $"realm {rs.Realm.Value} carried work across a quiet tick");
            Assert.That(rs.Counters.F.TightnessSamples, Is.Zero, $"realm {rs.Realm.Value} carried tightness across a quiet tick");
        }
    }

    /// <summary>
    /// The per-realm RATE rows (kind 70) reach the trace, for the realms the fence touched and no others, and kind 66's two counts describe them.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>[Category("TelemetryGated")]</c> because it needs the <c>Spatial</c> telemetry subtree on, and a subtree root defaults to off. <c>TelemetryConfig</c>
    /// reads its configuration in a static constructor, before the first test, so no fixture can flip it; the merge gate runs this in a dedicated process
    /// with the flag set. Locally:
    /// <code>
    /// $env:TYPHON__PROFILER__SPATIAL__ENABLED = 'true'; dotnet test --filter "Category=TelemetryGated"
    /// </code>
    /// </para>
    /// <para>
    /// <b>This is also the volume measurement.</b> Trace FILE SIZE cannot measure this record: across three interleaved 300-tick pairs of the 1 236-realm
    /// demo the deltas were +296, -609 and +405 bytes per tick — one of them negative, because other producers' run-to-run variance is larger than the
    /// whole record. Counting the rows is the only honest way to say how many there are, which is what the row count and kind 66's
    /// <c>ratesRealmsEmitted</c> assert here.
    /// </para>
    /// </remarks>
    [Test]
    [CancelAfter(60_000)]
    [Category("TelemetryGated")]
    [VerifiesRule("SO-03")]
    public void TouchedRealmsSendRateRows_AndTheArchetypeCensusDescribesThem()
    {
        var archetypeId = Archetype<RealmUnit>.Metadata.ArchetypeId;
        using var observer = new TraceRingObserver(ResourceRegistry.Profiler, captureRawBytes: true);
        try
        {
            // INSIDE the try, both of them. Attachment is process-global while `using` is scoped to this method, so a throw from Start with the attach
            // already done would leave a disposed observer on the exporter list — the exact hazard the finally below exists to prevent.
            TyphonProfiler.AttachExporter(observer);
            TyphonProfiler.Start(ResourceRegistry.Profiler, TraceMetadata());
            using var dbe = ThreeRealms();
            SpawnMirrored(dbe);
            var tick = 0L;
            dbe.WriteTickFence(++tick);
            for (var round = 1; round <= 2; round++)
            {
                MoveAll(dbe, round, viaOpenMut: false);
                dbe.WriteTickFence(++tick);
                dbe.WriteTickFence(++tick);
            }
        }
        finally
        {
            TyphonProfiler.Stop();
            // DETACH, or the observer stays on the global exporter list after this test disposes it — and the NEXT test's Stop() drains into a disposed
            // BlockingCollection. Attachment is process-global and survives the fixture; `using` only disposes the observer, it does not unregister it.
            TyphonProfiler.DetachExporter(observer);
        }

        var rows = new List<SpatialRealmRatesEventDto>();
        var census = new List<(int Touched, int Emitted)>();
        foreach (var (kind, bytes) in observer.GetRecords())
        {
            if (kind == TraceEventKind.SpatialRealmRates)
            {
                var dto = SpatialRealmRatesEventDto.Decode(bytes, 0, 1);
                if (dto.ArchetypeId == archetypeId)
                {
                    rows.Add(dto);
                }
            }
            else if (kind == TraceEventKind.SpatialArchetypeTelemetry)
            {
                var dto = SpatialArchetypeTelemetryEventDto.Decode(bytes, 0, 1);
                if (dto.ArchetypeId == archetypeId)
                {
                    census.Add((dto.RatesRealmsTouched, dto.RatesRealmsEmitted));
                }
            }
        }

        Assert.That(rows, Is.Not.Empty, $"records seen: {observer.RecordsProcessed}; is TYPHON__PROFILER__SPATIAL__ENABLED set?");
        Assert.Multiple(() =>
        {
            Assert.That(rows.Select(r => r.RealmId).Distinct().Count(), Is.GreaterThan(1),
                "the population is mirrored across three realms, so a row set naming one realm is the attribution failing on the wire");
            // ANY of the counters, not two named ones: the emitter's flag is set when any of the 33 moved, so a realm whose only motion was slots
            // scanned or a tightness sample yields a legal row that a two-counter test would call a bug.
            Assert.That(rows.All(r => r.MigrationCount != 0 || r.ClustersScanned != 0 || r.SlotsScanned != 0 || r.DriftersDetected != 0
                || r.TightnessSamples != 0 || r.CrossingsQueued != 0 || r.RepairUnitCount != 0 || r.CellTreePromotions != 0
                || r.RelocationsAdmitted != 0 || r.ArrivalCellsTouched != 0), Is.True,
                "a row is emitted only for a realm the fence touched, so every row must carry something non-zero");
            Assert.That(census, Is.Not.Empty, "the archetype record carries the census on every tick");
            // Not >= : below the cap the two are equal, and equality is what tells a consumer that an absent realm did no work rather than being truncated.
            Assert.That(census.All(c => c.Emitted == c.Touched), Is.True,
                $"this fixture is far below the row cap, so nothing should be truncated: {string.Join(", ", census.Select(c => $"{c.Emitted}/{c.Touched}"))}");
            Assert.That(census.Any(c => c.Touched > 1), Is.True, "more than one realm works on at least one tick, or the census proves nothing");
        });

        // The measurement, printed rather than asserted: an assertion on it would pin a number that legitimately moves with the workload.
        var ticksWithRows = census.Count(c => c.Emitted > 0);
        TestContext.Out.WriteLine(
            $"kind 70: {rows.Count} rows over {census.Count} archetype-ticks ({ticksWithRows} with rows); "
            + $"max realms touched on one tick = {(census.Count == 0 ? 0 : census.Max(c => c.Touched))}");
    }

    [Test]
    [CancelAfter(30_000)]
    public void DestroyedClusters_AreFinalisedInTheirRealm_AndRecycledIdsDoNotLeak()
    {
        using var dbe = ThreeRealms();
        SpawnMirrored(dbe);
        var tick = 0L;
        dbe.WriteTickFence(++tick);

        // Empty realm 2 entirely: every one of its clusters drains and is freed through the finalization drain, in realm 2's cell state.
        var cs = StateOf(dbe);
        var realm2 = new List<EntityId>();
        using (var tx = dbe.CreateQuickTransaction())
        {
            var accessor = tx.For<RealmUnit>();
            try
            {
                foreach (var cluster in accessor.GetClusterEnumerator())
                {
                    var bits = cluster.OccupancyBits;
                    while (bits != 0)
                    {
                        var slot = BitOperations.TrailingZeroCount(bits);
                        bits &= bits - 1;
                        if (cluster.GetReadOnly(RealmUnit.Pos, slot).Realm == 2)
                        {
                            realm2.Add(cluster.GetEntityId(slot));
                        }
                    }
                }
            }
            finally
            {
                accessor.Dispose();
            }
        }

        Assert.That(realm2, Has.Count.EqualTo(PerRealm));
        using (var tx = dbe.CreateQuickTransaction())
        {
            foreach (var id in realm2)
            {
                tx.Destroy(id);
            }

            tx.Commit();
        }

        dbe.WriteTickFence(++tick);
        var grid2 = dbe.RealmTable.Get(2).Grid;
        for (var cell = 0; cell < grid2.CellCount; cell++)
        {
            Assert.That(grid2.GetCell(cell).EntityCount, Is.Zero, $"realm 2 cell {cell} still counts an entity");
            Assert.That(cs.RealmSpatial[2].CellClusterPool.GetClusters(cell).Length, Is.Zero, $"realm 2 cell {cell} still lists a cluster");
        }

        // Spawn a second population into realm 0: freed realm-2 chunk ids are recycled into realm 0's cells, whose keys overlap realm 2's.
        using (var tx = dbe.CreateQuickTransaction())
        {
            for (var tag = 0; tag < PerRealm; tag++)
            {
                tx.Spawn<RealmUnit>(RealmUnit.Pos.Set(new RealmPos { Bounds = BoxOf(1, tag), Realm = 0, Tag = PerRealm + tag }));
            }

            tx.Commit();
        }

        dbe.WriteTickFence(++tick);
        using var epoch = EpochGuard.Enter(dbe.EpochManager);
        var hits = 0;
        foreach (var _ in dbe.ClusterSpatialQuery<RealmUnit>(new RealmId(2)).AABB(new AABB2F { MinX = 0, MinY = 0, MaxX = World, MaxY = World }))
        {
            hits++;
        }

        Assert.That(hits, Is.Zero, "realm 2 is empty: a recycled chunk id must not answer for it");
        Assert.That(dbe.ClusterSpatialQuery<RealmUnit>(RealmId.Default).AABB(new AABB2F { MinX = 0, MinY = 0, MaxX = World, MaxY = World }).Count(),
            Is.EqualTo(2 * PerRealm));
    }

    /// <summary>
    /// The realm invariants after the fences have drained a round: every cluster's entities carry its realm's key and sit in its cell of its realm's grid
    /// (CC-02, within the hysteresis dead zone); every cell's entity count is its realm's; each realm's reach covers its index; and every query answers
    /// exactly the brute force over the positions written.
    /// </summary>
    private static void AssertRealmsConsistent(DatabaseEngine dbe, int round, string context)
    {
        var cs = StateOf(dbe);
        var perRealmCellCounts = new Dictionary<(int realm, int cell), int>();
        var seen = new int[RealmCount];
        using (var tx = dbe.CreateQuickTransaction())
        {
            var accessor = tx.For<RealmUnit>();
            try
            {
                foreach (var cluster in accessor.GetClusterEnumerator())
                {
                    var chunk = cluster.ChunkId;
                    var realm = cs.ClusterRealmMap[chunk];
                    var cellKey = cs.ClusterCellMap[chunk];
                    var grid = dbe.RealmTable.Get(realm).Grid;
                    var margin = grid.Config.CellSize * grid.Config.MigrationHysteresisRatio;
                    var bits = cluster.OccupancyBits;
                    while (bits != 0)
                    {
                        var slot = BitOperations.TrailingZeroCount(bits);
                        bits &= bits - 1;
                        var v = cluster.GetReadOnly(RealmUnit.Pos, slot);
                        Assert.That(v.Realm, Is.EqualTo(realm), $"an entity of realm {v.Realm} sits in a realm-{realm} cluster, {context}");
                        var cx = (v.Bounds.MinX + v.Bounds.MaxX) * 0.5;
                        var cy = (v.Bounds.MinY + v.Bounds.MaxY) * 0.5;
                        grid.CellOrigin(cellKey, out var ox, out var oy, out _);
                        var inCell = cx >= ox - margin && cx <= ox + grid.Config.CellSize + margin && cy >= oy - margin
                            && cy <= oy + grid.Config.CellSize + margin;
                        Assert.That(inCell, Is.True, $"entity {v.Tag} of realm {realm} at ({cx}, {cy}) is filed in cell {cellKey} at ({ox}, {oy}), {context}");
                        perRealmCellCounts[(realm, cellKey)] = perRealmCellCounts.GetValueOrDefault((realm, cellKey)) + 1;
                        seen[realm]++;
                    }
                }
            }
            finally
            {
                accessor.Dispose();
            }
        }

        for (var r = 0; r < RealmCount; r++)
        {
            Assert.That(seen[r], Is.EqualTo(PerRealm), $"realm {r} holds {seen[r]} entities, {context}");
            var grid = dbe.RealmTable.Get((ushort)r).Grid;
            for (var cell = 0; cell < grid.CellCount; cell++)
            {
                Assert.That(grid.GetCell(cell).EntityCount, Is.EqualTo(perRealmCellCounts.GetValueOrDefault((r, cell))),
                    $"realm {r} cell {cell}'s entity count, {context}");
            }
        }

        Assert.That(cs.ReachCoversIndex(out var violation), Is.True, $"{violation}, {context}");

        var rng = new Random(round);
        using var epoch = EpochGuard.Enter(dbe.EpochManager);
        for (var q = 0; q < 20; q++)
        {
            var cx = rng.NextDouble() * World;
            var cy = rng.NextDouble() * World;
            var ext = rng.NextDouble() * 20 + 1;
            var query = new AABB2F { MinX = (float)(cx - ext), MinY = (float)(cy - ext), MaxX = (float)(cx + ext), MaxY = (float)(cy + ext) };
            var expected = new HashSet<int>();
            for (var tag = 0; tag < PerRealm; tag++)
            {
                var b = BoxOf(round, tag);
                if (b.MinX <= query.MaxX && b.MaxX >= query.MinX && b.MinY <= query.MaxY && b.MaxY >= query.MinY)
                {
                    expected.Add(tag);
                }
            }

            for (ushort r = 0; r < RealmCount; r++)
            {
                var got = new HashSet<int>();
                foreach (var hit in dbe.ClusterSpatialQuery<RealmUnit>(new RealmId(r)).AABB(query))
                {
                    Assert.That(cs.ClusterRealmMap[hit.ClusterChunkId], Is.EqualTo(r), $"query {q} in realm {r} answered another realm's cluster, {context}");
                    Assert.That(got.Add(TagOf(dbe, hit.Entity)), Is.True, $"duplicate hit, query {q}, realm {r}, {context}");
                }

                Assert.That(got, Is.EquivalentTo(expected), $"query {q} in realm {r}, {context}");
            }
        }
    }

    private static int TagOf(DatabaseEngine dbe, EntityId id)
    {
        using var tx = dbe.CreateQuickTransaction();
        return tx.Open(id).Read(RealmUnit.Pos).Tag;
    }
}
