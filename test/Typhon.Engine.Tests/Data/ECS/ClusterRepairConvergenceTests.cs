using System;
using System.Collections.Generic;
using System.Numerics;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Typhon.Engine.Internals;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests;

/// <summary>
/// The repair path's termination and its coexistence with step 10's delta path (#872 step 12).
/// </summary>
/// <remarks>
/// <para><b>Why these are not in <c>ClusterRepairTests</c>.</b> That fixture answers "does one pass do the right thing".
/// These answer "what happens on the ticks after", which is a different failure mode and a quieter one: a repair that
/// re-runs forever is green on every per-pass assertion while costing a full gather and sort per tick, and two
/// mechanisms sharing one queue corrupt each other without either one's own tests noticing.</para>
/// <para><b>The combined fixture exists because the four step-10 fixtures now pin <c>reclusterBudgetMs: 0f</c>.</b> That
/// pinning is correct scoping — repair legitimately preempts relocation, so a fixture measuring the delta path must
/// switch repair off — but it left nothing exercising the two together, and they share one <c>PendingMigrations</c>
/// array, one unstable sort and one Migrate phase. This is the seam that leaves.</para>
/// </remarks>
[TestFixture]
[NonParallelizable]
class ClusterRepairConvergenceTests : TestBase<ClusterRepairConvergenceTests>
{
    [OneTimeSetUp]
    public void OneTimeSetup()
    {
    }

    private const float CellSize = 100f;
    private const float WorldMax = 1000f;
    private const int Population = 1000;

    private static ClMigPos PointAt(float x, float y, int tag = 0) =>
        new() { Bounds = new AABB2F { MinX = x, MinY = y, MaxX = x, MaxY = y }, Tag = tag };

    private static ushort ArchetypeId => Archetype<ClMigUnit>.Metadata.ArchetypeId;

    private static ArchetypeClusterState ClusterStateOf(DatabaseEngine dbe) =>
        dbe._archetypeStates[Archetype<ClMigUnit>.Metadata.ArchetypeId].ClusterState;

    /// <summary>
    /// An engine with repair armed and, optionally, step 10's delta path armed alongside it.
    /// </summary>
    /// <remarks>
    /// <para><paramref name="driftRatio"/> at 100 makes the drift gate unreachable — a cluster confined to one cell can never
    /// exceed a hundred cell-widths — which is how the delta path is switched off without changing anything else.</para>
    /// <para><paramref name="repairCooldownTicks"/> defaults to 0, not to the engine's 50, because every termination test here
    /// pins what stops a converged cell re-packing — RP-03's already-packed check and its geometry memo — and a cooldown
    /// stops the re-packs by itself: with it on, deleting either guard would leave those tests green. The cooldown's own
    /// tests (RP-07) pass their value explicitly.</para>
    /// </remarks>
    private DatabaseEngine SetupEngine(float driftRatio = 100f, int repairCooldownTicks = 0)
    {
        var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<ClMigPos>();
        dbe.RegisterComponentFromAccessor<ClMigScratch>();
        dbe.ConfigureSpatialGrid(SpatialGridConfig.Flat(
            new Vector2(0, 0),
            new Vector2(WorldMax, WorldMax),
            CellSize,
            clusterTargetExtentRatio: driftRatio,
            clusterRepairExtentRatio: 0.75f,
            reclusterBudgetMs: 100f, batchSpawnSortThreshold: 0 /* step 15: this fixture builds its layout by spawn ORDER; the Morton sort would tighten it at birth */,
            repairWorstClustersPerUnit: 0,
            repairCooldownTicks: repairCooldownTicks));
        dbe.InitializeArchetypes();
        return dbe;
    }

    private static List<EntityId> SpawnDegradedCell(DatabaseEngine dbe)
    {
        var ids = new List<EntityId>(Population);
        using (var tx = dbe.CreateQuickTransaction())
        {
            for (var i = 0; i < Population; i++)
            {
                var x = 4f + ((i * 37) % 92) + ((i / 92) % 4) * 0.2f;
                var y = 4f + ((i * 61) % 92) + ((i / 92) % 4) * 0.2f;
                ids.Add(tx.Spawn<ClMigUnit>(ClMigUnit.Pos.Set(PointAt(x, y, i))));
            }
            tx.Commit();
        }

        dbe.WriteTickFence(1);
        return ids;
    }

    private static unsafe int TotalOccupancy(DatabaseEngine dbe)
    {
        var total = 0;
        using var tx = dbe.CreateQuickTransaction();
        var accessor = tx.For<ClMigUnit>();
        try
        {
            foreach (var cluster in accessor.GetClusterEnumerator())
            {
                total += System.Numerics.BitOperations.PopCount(cluster.OccupancyBits);
            }
        }
        finally
        {
            accessor.Dispose();
        }

        return total;
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════════════════════════
    // Termination
    // ══════════════════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// A cell that has been repaired is not repaired again while nothing moves — the repair terminates rather than
    /// re-packing the same layout every tick.
    /// </summary>
    /// <remarks>
    /// <para><b>The livelock this rules out is not hypothetical; it was measured.</b> Nomination fires on extent alone and
    /// a Morton packing does not in general bring every cluster under the threshold, so a converged cell keeps nominating.
    /// Before the guard existed, a 2 000-entity cell re-packed all 2 000 entities on five consecutive ticks with its mean
    /// extent pinned at 23.0 the whole time: full cost, zero gain, and every per-pass assertion green.</para>
    /// <para><b>Mutations this rejects:</b> deleting <c>IsAlreadyPackedInSortOrder</c> (repairs continue forever), and
    /// deleting the geometry memo in <c>RepairOneCell</c> (the moves stop but the gather and sort do not, which the
    /// entity count below cannot see — hence the second assertion, on units rather than entities).</para>
    /// </remarks>
    [Test]
    [VerifiesRule("RP-03")]
    public void ARepairedCellIsNotRepairedAgainWhileNothingMoves()
    {
        var dbe = SetupEngine();
        SpawnDegradedCell(dbe);

        var repairs = 0;
        var lastRepairTick = 0;
        for (var tick = 2; tick <= 12; tick++)
        {
            dbe.WriteTickFence(tick);
            var t = dbe.GetSpatialTelemetry(ArchetypeId);
            if (t.RepairUnitCount > 0)
            {
                repairs++;
                lastRepairTick = tick;
            }
        }

        Assert.That(repairs, Is.GreaterThan(0), "nothing was ever repaired, so termination is not what this test measured");
        Assert.That(repairs, Is.LessThanOrEqualTo(2),
            $"the cell was repaired {repairs} times over 11 still ticks (last on tick {lastRepairTick}) — a converged layout is being re-packed");
        Assert.That(lastRepairTick, Is.LessThanOrEqualTo(4), $"repairs were still happening on tick {lastRepairTick}, well after the layout settled");
        Assert.That(TotalOccupancy(dbe), Is.EqualTo(Population), "the repeated repairs lost or duplicated entities");
    }

    /// <summary>
    /// Destroying a quarter of the population punches holes in the packing, and the cell converges again rather than
    /// oscillating.
    /// </summary>
    /// <remarks>
    /// <c>IsAlreadyPackedInSortOrder</c> tests whether each capacity-sized group draws from one source cluster, which is
    /// exact only while the source clusters are near-full. Holes make the groups straddle sources, so the cell repacks —
    /// correctly, since packing the survivors tightly is an improvement — and the question this pins is whether that
    /// settles. It must, because a re-pack produces full clusters again, but "must" is an argument and this is the
    /// measurement.
    /// </remarks>
    [Test]
    public void ACellConvergesAgainAfterDestroysPunchHolesInThePacking()
    {
        var dbe = SetupEngine();
        var ids = SpawnDegradedCell(dbe);

        for (var tick = 2; tick <= 6; tick++)
        {
            dbe.WriteTickFence(tick);
        }

        using (var tx = dbe.CreateQuickTransaction())
        {
            for (var i = 0; i < ids.Count; i += 4)
            {
                tx.Destroy(ids[i]);
            }
            tx.Commit();
        }

        var survivors = Population - ((Population + 3) / 4);
        var repairs = 0;
        var lastRepairTick = 0;
        for (var tick = 7; tick <= 20; tick++)
        {
            dbe.WriteTickFence(tick);
            if (dbe.GetSpatialTelemetry(ArchetypeId).RepairUnitCount > 0)
            {
                repairs++;
                lastRepairTick = tick;
            }
        }

        Assert.That(TotalOccupancy(dbe), Is.EqualTo(survivors), "the post-destroy repairs lost or duplicated survivors");
        Assert.That(repairs, Is.LessThanOrEqualTo(3),
            $"the cell repaired {repairs} times over 14 ticks after the destroys (last on tick {lastRepairTick}) — it is not converging");
        Assert.That(lastRepairTick, Is.LessThanOrEqualTo(12), $"repairs were still happening on tick {lastRepairTick}, long after the destroys settled");
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════════════════════════
    // Cadence — the cooldown (RP-07)
    // ══════════════════════════════════════════════════════════════════════════════════════════════════════════════

    private const int CooldownTicks = 6;
    private const string RepairedInsideItsCooldown = "repaired inside its cooldown";

    /// <summary>
    /// A cell that re-degrades after every repair is repaired again only once its cooldown has ended — and then promptly.
    /// </summary>
    /// <remarks>
    /// <para><b>The workload is the churn the cooldown exists for, made deterministic.</b> Every entity is scrambled across
    /// the cell before every fence, so a repair's tight packing is undone on the very next tick and the cell is nominated
    /// again straight away. Without a cooldown it is re-packed every other tick for ever — the mutant below shows it —
    /// which is what the SWG Tatooine workload spent its maintenance budget on.</para>
    /// <para><b>Both halves are asserted, exactly.</b> The scenario is deterministic: cooldowns end before the tick's
    /// nominations are absorbed, the budget cannot refuse the unit (the estimate is clamped at 31.2 us an entity, 31 ms
    /// against 100) and the valve cannot fire (a scrambled cluster spans at most 0.96 of the cell). So every repair after
    /// the first lands on the tick its cooldown ends, and a late release fails as surely as an early one.</para>
    /// </remarks>
    [Test]
    [VerifiesRule("RP-07")]
    public void ARepairedCellIsNotRepairedAgainUntilItsCooldownEnds()
    {
        var dbe = SetupEngine(repairCooldownTicks: CooldownTicks);
        SpawnDegradedCell(dbe);

        var (repairTicks, cooling) = AssertRepairsAreSpacedByTheCooldown(dbe, CooldownTicks, (2 * CooldownTicks) + 2);

        // From its first repair on, the world's one cell is always cooling: each cooldown ends on the tick that repairs it again.
        foreach (var (tick, cells) in cooling)
        {
            if (tick >= repairTicks[0])
            {
                Assert.That(cells, Is.EqualTo(1),
                    $"tick {tick}: RepairCellsCooling reads {cells} for a world whose one cell was repaired on tick {repairTicks[0]}");
            }
        }

        Assert.That(TotalOccupancy(dbe), Is.EqualTo(Population), "the repairs lost or duplicated entities");
    }

    /// <summary>
    /// With the cooldown off, the same workload repairs the cell inside the window the rule forbids — so the test above is not green for want of churn.
    /// </summary>
    [Test]
    [RuleMutant("RP-07")]
    public void WithoutTheCooldownTheSameCellIsRepairedInsideIt()
    {
        var dbe = SetupEngine(repairCooldownTicks: 0);
        SpawnDegradedCell(dbe);

        RuleMutants.AssertDetects("RP-07", RepairedInsideItsCooldown, () => AssertRepairsAreSpacedByTheCooldown(dbe, CooldownTicks, CooldownTicks));
    }

    /// <summary>
    /// A cell nominated once while it cools and then left still is repaired on the tick its cooldown ends, although nothing nominates it again.
    /// </summary>
    /// <remarks>
    /// <para><b>The case the hold exists for, staged through the fence.</b> After its first repair the cell is degraded once, which files one nomination
    /// while it cools, and is never written again. The fence is kept alive by rewriting an entity of a second cell whose only cluster is tight and never
    /// nominates, so every later tick has no nomination and, while the cell cools, no candidate. A fence that planned only on nominations or candidates
    /// never released the cell; a queue that dropped held nominations would release it with nothing to repair.</para>
    /// <para><b>Then it settles.</b> On this path nothing nominates the re-packed cell again, so its second cooldown ends with nothing held: no third
    /// repair, and nothing cooling after it.</para>
    /// <para><b>Measured as the fix's ablation:</b> with the fence's early-out reverted to <c>Count</c>, the cell is repaired once and never again.</para>
    /// </remarks>
    [Test]
    [VerifiesRule("RP-07")]
    public void ACellThatGoesStillWhileItCoolsIsRepairedWhenTheCooldownEnds()
    {
        var dbe = SetupEngine(repairCooldownTicks: CooldownTicks);

        // Barrier-only, the write path both demos run, and the only one on which a still cell is never nominated again (RP-04's gap). The legacy refresh
        // re-walks every occupied cluster and re-nominates the still, degraded cell on every tick, which hides the case: measured, with the fence's
        // early-out reverted to Count, the legacy-mode version of this test stayed green.
        dbe.SetSpatialBarrierOnly<ClMigUnit>();
        SpawnDegradedCell(dbe);
        SpawnTightCell(dbe);

        var rng = new Random(20260916);
        var repairTicks = new List<int>();
        var cooling = new List<(int Tick, int Cells)>();
        for (var tick = 2; tick < 3 + (3 * CooldownTicks); tick++)
        {
            // Written until its first repair — on the barrier-only path a spawned layout nobody writes is never nominated (RP-04's gap) — then once
            // more, the tick after it, and never again.
            if (repairTicks.Count == 0 || (repairTicks.Count == 1 && tick == repairTicks[0] + 1))
            {
                ScrambleEveryEntity(dbe, rng, maxTag: Population);
            }
            else
            {
                RewriteOneTightCellEntity(dbe);
            }

            dbe.WriteTickFence(tick);
            var t = dbe.GetSpatialTelemetry(ArchetypeId);
            if (t.RepairUnitCount > 0)
            {
                repairTicks.Add(tick);
            }

            cooling.Add((tick, t.RepairCellsCooling));
        }

        Assert.That(repairTicks, Has.Count.GreaterThanOrEqualTo(2),
            $"repairs on ticks [{string.Join(", ", repairTicks)}]: the cell nominated once while it cooled and then left still was never repaired again");
        Assert.That(repairTicks[1], Is.EqualTo(repairTicks[0] + CooldownTicks),
            $"the cell was repaired on tick {repairTicks[0]} and, nominated once while it cooled, again on tick {repairTicks[1]} — it should have been the "
            + $"tick its {CooldownTicks}-tick cooldown ended");
        Assert.That(repairTicks, Has.Count.EqualTo(2), $"a still, re-packed cell was repaired again: ticks [{string.Join(", ", repairTicks)}]");
        foreach (var (tick, cells) in cooling)
        {
            if (tick >= repairTicks[1] + CooldownTicks)
            {
                Assert.That(cells, Is.Zero, $"tick {tick}: {cells} cell(s) cooling in a still world whose last repair was on tick {repairTicks[1]}");
            }
        }

        Assert.That(TotalOccupancy(dbe), Is.EqualTo(Population + TightCellPopulation), "the repairs lost or duplicated entities");
    }

    /// <summary>
    /// A re-sort that moves nothing starts no cooldown: a still, re-packed cell released from its cooldown re-sorts to a no-op and is not held out again.
    /// </summary>
    /// <remarks>
    /// On the legacy refresh, which re-walks every occupied cluster, a re-packed cell's own clusters nominate it on every tick — a Morton run is not a
    /// square, so some cluster stays over the repair gate (RP-03) — so it is held through its cooldown, asserted, or the clause would pass by default.
    /// Released, it is re-sorted and nothing moves. A cooldown started by that no-op would read as one cell cooling on every tick after it.
    /// </remarks>
    [Test]
    [VerifiesRule("RP-07")]
    public void ARepairThatMovesNothingStartsNoCooldown()
    {
        var dbe = SetupEngine(repairCooldownTicks: CooldownTicks);
        SpawnDegradedCell(dbe);
        SpawnTightCell(dbe);
        var cell = dbe.Realm0Grid.WorldToCellKey(50f, 50f, 0f);

        var repairTicks = new List<int>();
        var heldAtRelease = 0f;
        var coolingAfter = new List<(int Tick, int Cells)>();
        for (var tick = 2; tick < 2 + (2 * CooldownTicks); tick++)
        {
            RewriteOneTightCellEntity(dbe);
            dbe.WriteTickFence(tick);
            var t = dbe.GetSpatialTelemetry(ArchetypeId);
            if (t.RepairUnitCount > 0)
            {
                repairTicks.Add(tick);
            }

            if (repairTicks.Count == 1 && tick == repairTicks[0] + CooldownTicks - 1)
            {
                heldAtRelease = ClusterStateOf(dbe).RepairQueue.HeldDegradationOf(cell);
            }

            if (repairTicks.Count > 0 && tick >= repairTicks[0] + CooldownTicks)
            {
                coolingAfter.Add((tick, t.RepairCellsCooling));
            }
        }

        Assert.That(repairTicks, Has.Count.EqualTo(1), $"repairs on ticks [{string.Join(", ", repairTicks)}]: a still cell was re-packed with entities moving");
        Assert.That(heldAtRelease, Is.GreaterThan(0f),
            "precondition: nothing nominated the re-packed cell while it cooled, so its release re-queues nothing and no re-sort is attempted");
        Assert.That(coolingAfter, Is.Not.Empty, "the run ended before the cooldown did");
        foreach (var (tick, cells) in coolingAfter)
        {
            Assert.That(cells, Is.Zero, $"tick {tick}: a re-sort that moved nothing started a cooldown");
        }
    }

    private const int TightCellTag = 100_000;
    private const int TightCellPopulation = 20;

    /// <summary>Spawn a second, tight cell far from the degraded one: one cluster a few units wide, which never nominates.</summary>
    private static void SpawnTightCell(DatabaseEngine dbe)
    {
        using var tx = dbe.CreateQuickTransaction();
        for (var i = 0; i < TightCellPopulation; i++)
        {
            tx.Spawn<ClMigUnit>(ClMigUnit.Pos.Set(PointAt(550f + (i % 5), 550f + (i / 5), TightCellTag + i)));
        }

        tx.Commit();
    }

    /// <summary>Rewrite one entity of the tight cell unchanged, so the archetype has fence work without anything in the degraded cell being written.</summary>
    private static unsafe void RewriteOneTightCellEntity(DatabaseEngine dbe)
    {
        using var tx = dbe.CreateQuickTransaction();
        var accessor = tx.For<ClMigUnit>();
        try
        {
            foreach (var cluster in accessor.GetClusterEnumerator())
            {
#pragma warning disable TYPHON009 // Read-only: the value is written straight back unchanged.
                var positions = cluster.GetSpan(ClMigUnit.Pos);
#pragma warning restore TYPHON009
                var bits = cluster.OccupancyBits;
                while (bits != 0)
                {
                    var slot = System.Numerics.BitOperations.TrailingZeroCount(bits);
                    bits &= bits - 1;
                    if (positions[slot].Tag >= TightCellTag)
                    {
                        cluster.WriteSpatial(ClMigUnit.Pos, slot, positions[slot]);
                        tx.Commit();
                        return;
                    }
                }
            }
        }
        finally
        {
            accessor.Dispose();
        }

        tx.Commit();
    }

    /// <summary>
    /// Scramble the cell before each of <paramref name="ticks"/> fences and assert RP-07 over the ticks that repaired it: never two repairs within
    /// <paramref name="cooldownTicks"/>, and each one after the first on exactly the tick the cooldown ends. Returns the repair ticks, and the cooling level
    /// the telemetry read after every fence.
    /// </summary>
    private static (List<int> RepairTicks, List<(int Tick, int Cells)> Cooling) AssertRepairsAreSpacedByTheCooldown(DatabaseEngine dbe, int cooldownTicks,
        int ticks)
    {
        var rng = new Random(20260915);
        var repairTicks = new List<int>();
        var cooling = new List<(int, int)>();
        for (var tick = 2; tick < 2 + ticks; tick++)
        {
            ScrambleEveryEntity(dbe, rng);
            dbe.WriteTickFence(tick);
            var t = dbe.GetSpatialTelemetry(ArchetypeId);
            if (t.RepairUnitCount > 0)
            {
                repairTicks.Add(tick);
            }

            cooling.Add((tick, t.RepairCellsCooling));
        }

        Assert.That(repairTicks, Has.Count.GreaterThanOrEqualTo(2),
            $"the cell was repaired {repairTicks.Count} time(s) over {ticks} ticks of re-degradation, so the end of a cooldown was never observed");
        for (var i = 1; i < repairTicks.Count; i++)
        {
            var gap = repairTicks[i] - repairTicks[i - 1];
            Assert.That(gap, Is.GreaterThanOrEqualTo(cooldownTicks),
                $"{RepairedInsideItsCooldown}: the cell was repaired on tick {repairTicks[i - 1]} and again on tick {repairTicks[i]}, {gap} tick(s) later, "
                + $"inside its {cooldownTicks}-tick cooldown");
            Assert.That(gap, Is.EqualTo(cooldownTicks),
                $"the cell was repaired on tick {repairTicks[i - 1]} and not again until tick {repairTicks[i]}, although it was re-degraded on every tick — "
                + $"the {cooldownTicks}-tick cooldown did not end on the tick it should have");
        }

        return (repairTicks, cooling);
    }

    /// <summary>
    /// Move every entity whose tag is below <paramref name="maxTag"/> to a random point of cell (0,0), so every cluster spans it again — a repair's packing
    /// undone in one tick.
    /// </summary>
    private static unsafe void ScrambleEveryEntity(DatabaseEngine dbe, Random rng, int maxTag = int.MaxValue)
    {
        using var tx = dbe.CreateQuickTransaction();
        var accessor = tx.For<ClMigUnit>();
        try
        {
            foreach (var cluster in accessor.GetClusterEnumerator())
            {
#pragma warning disable TYPHON009 // Read for the tag only; the position is replaced.
                var positions = cluster.GetSpan(ClMigUnit.Pos);
#pragma warning restore TYPHON009
                var bits = cluster.OccupancyBits;
                while (bits != 0)
                {
                    var slot = System.Numerics.BitOperations.TrailingZeroCount(bits);
                    bits &= bits - 1;
                    if (positions[slot].Tag >= maxTag)
                    {
                        continue;
                    }

                    cluster.WriteSpatial(ClMigUnit.Pos, slot,
                        PointAt(2f + ((float)rng.NextDouble() * 96f), 2f + ((float)rng.NextDouble() * 96f), positions[slot].Tag));
                }
            }
        }
        finally
        {
            accessor.Dispose();
        }

        tx.Commit();
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════════════════════════
    // Coexistence with the delta path
    // ══════════════════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// With BOTH the repair path and step 10's relocation armed, and the population moving every tick, no entity is lost,
    /// duplicated or left resolving to the wrong slot.
    /// </summary>
    /// <remarks>
    /// The two mechanisms file into one <c>PendingMigrations</c> array, are reordered by one unstable sort and are drained
    /// by one Migrate phase. A repair request pins a slot in a cluster it allocated this tick; a relocation request pins
    /// only a cluster and can fall through to first fit into that same fresh cluster and take the slot. Nothing here
    /// asserts the LAYOUT — that is scheduling-dependent by design and <c>MigrationRequest</c> documents the pin as a
    /// preference — only that the collision resolves without losing anything.
    /// </remarks>
    [Test]
    [CancelAfter(60_000)]
    public void RepairAndRelocationCoexistWithoutLosingAnEntity([Values(0, 50)] int repairCooldownTicks)
    {
        // The default 0.25 drift ratio, so step 10's gate is live alongside repair. Run at the shipped cooldown as well as at none: the other multi-tick
        // repair fixtures here pin it to 0, and the mix of repair and relocation must hold at the setting that ships.
        var dbe = SetupEngine(driftRatio: 0.25f, repairCooldownTicks: repairCooldownTicks);
        var ids = SpawnDegradedCell(dbe);

        var rng = new Random(20260904);
        for (var tick = 2; tick <= 20; tick++)
        {
            JitterEveryEntity(dbe, rng);
            dbe.WriteTickFence(tick);
        }

        Assert.That(TotalOccupancy(dbe), Is.EqualTo(Population), "occupancy and the population disagree after 19 ticks of both mechanisms");

        var telemetry = dbe.GetSpatialTelemetry(ArchetypeId);
        Assert.That(ClusterStateOf(dbe).TotalMigrationCount, Is.GreaterThan(0), "nothing ever migrated, so the two mechanisms were never actually mixed");

        // Every id must still resolve to its own entity. A repair and a relocation racing for one slot would show up here
        // as an id resolving to a neighbour's tag — silent in a storage walk, fatal in production.
        using var tx = dbe.CreateQuickTransaction();
        for (var i = 0; i < ids.Count; i++)
        {
            var eref = tx.Open(ids[i]);
            ref readonly var pos = ref eref.Read(ClMigUnit.Pos);
            Assert.That(pos.Tag, Is.EqualTo(i), $"EntityMap resolved entity {i} to a slot holding tag {pos.Tag}");
        }

        TestContext.Out.WriteLine($"coexistence: {ClusterStateOf(dbe).TotalMigrationCount} migrations, "
            + $"last tick drifters={telemetry.DriftersDetected} repairUnits={telemetry.RepairUnitCount} refused={telemetry.RepairUnitsRefused}");
    }

    /// <summary>Move every entity a short random step, reading the current position so the motion is local rather than a teleport.</summary>
    private static unsafe void JitterEveryEntity(DatabaseEngine dbe, Random rng)
    {
        using var tx = dbe.CreateQuickTransaction();
        var accessor = tx.For<ClMigUnit>();
        try
        {
            foreach (var cluster in accessor.GetClusterEnumerator())
            {
#pragma warning disable TYPHON009 // Read to derive the next position from the current one.
                var positions = cluster.GetSpan(ClMigUnit.Pos);
#pragma warning restore TYPHON009
                var bits = cluster.OccupancyBits;
                while (bits != 0)
                {
                    var slot = System.Numerics.BitOperations.TrailingZeroCount(bits);
                    bits &= bits - 1;

                    ref readonly var b = ref positions[slot].Bounds;
                    var x = Math.Clamp((0.5f * (b.MinX + b.MaxX)) + ((float)rng.NextDouble() - 0.5f) * 4f, 2f, 98f);
                    var y = Math.Clamp((0.5f * (b.MinY + b.MaxY)) + ((float)rng.NextDouble() - 0.5f) * 4f, 2f, 98f);
                    cluster.WriteSpatial(ClMigUnit.Pos, slot, PointAt(x, y, positions[slot].Tag));
                }
            }
        }
        finally
        {
            accessor.Dispose();
        }

        tx.Commit();
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════════════════════════
    // The predicate itself
    // ══════════════════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <c>IsAlreadyPackedInSortOrder</c> answers exactly "would the sorted packing reproduce the current partition".
    /// </summary>
    /// <remarks>
    /// Driven directly rather than through a fence, because this one predicate decides repair-versus-no-op and therefore
    /// carries both failure modes at once: return true too readily and a degraded cell is never repaired; return false too
    /// readily and a converged cell re-packs forever. Neither is visible in a single-pass end-to-end test.
    /// </remarks>
    [Test]
    public void TheAlreadyPackedPredicateAnswersPartitionEquality()
    {
        var dbe = SetupEngine();
        SpawnDegradedCell(dbe);
        var state = ClusterStateOf(dbe);
        var capacity = System.Numerics.BitOperations.PopCount(state.Layout.FullMask);
        Assert.That(capacity, Is.GreaterThan(2), "the construction below needs a cluster to hold at least three entities");

        // Two full groups, each drawn entirely from one source cluster: the packing already IS the sorted one.
        var packed = new ArchetypeClusterState.RepairEntry[capacity * 2];
        for (var i = 0; i < capacity; i++)
        {
            packed[i] = new ArchetypeClusterState.RepairEntry((ulong)i, 7L * 64 + i);
            packed[capacity + i] = new ArchetypeClusterState.RepairEntry((ulong)(capacity + i), 9L * 64 + i);
        }

        Assert.That(state.IsAlreadyPackedInSortOrder(packed, packed.Length), Is.True,
            "two groups each drawn from a single source cluster ARE the sorted partition, so a re-pack would change nothing");

        // One entity of group 0 lives in the other cluster — the groups straddle sources, so a re-pack regroups them.
        var straddling = (ArchetypeClusterState.RepairEntry[])packed.Clone();
        straddling[1] = new ArchetypeClusterState.RepairEntry(straddling[1].MortonKey, 9L * 64 + 63);
        Assert.That(state.IsAlreadyPackedInSortOrder(straddling, straddling.Length), Is.False,
            "a group drawing from two source clusters is not the sorted partition");

        // A trailing partial group is still homogeneous, and must not be read as straddling merely for being short.
        Assert.That(state.IsAlreadyPackedInSortOrder(packed, capacity + 2), Is.True,
            "a partial final group drawn from one cluster is still packed; the length must not decide the verdict");
    }
}
