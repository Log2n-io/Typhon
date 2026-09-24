using System;
using System.Collections.Generic;
using System.Numerics;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Typhon.Engine.Internals;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests;

/// <summary>
/// <c>AC-11.2</c>, <c>AC-11.3</c> and <c>AC-11.5</c> — the persistent, ranked repair queue (#872 step 11).
/// </summary>
/// <remarks>
/// <para><b>What step 12 could not express, and why a fixture is needed at all.</b> Step 12 kept nominations in a per-tick list, ordered them by cell KEY,
/// and DISCARDED whatever the budget refused. So a cell could be nominated on every tick of a run and never once serviced, purely because its key sorted
/// after someone else's — and nothing observed it, because "was this cell ever repaired" was not a question the telemetry could answer. The queue makes it
/// answerable; these tests ask it.</para>
/// <para><b>Many cells, deliberately.</b> Every other fixture on this branch works in cell (0,0) because the property under test is intra-cell. Here the
/// property is which cell goes first, so the world is a row of degraded cells and the assertions are about the SET that gets serviced over a run.</para>
/// </remarks>
[TestFixture]
[NonParallelizable]
class ClusterRepairQueueTests : TestBase<ClusterRepairQueueTests>
{
    [OneTimeSetUp]
    public void OneTimeSetup()
    {
    }

    private const float CellSize = 100f;
    private const float WorldMax = 1000f;

    /// <summary>Cells degraded in parallel, laid out along X so each gets its own cell key.</summary>
    private const int CellCount = 6;

    /// <summary>Entities per degraded cell — enough to fill several clusters, so a cell has a unit worth repairing.</summary>
    /// <remarks>
    /// 250 at 49 slots per cluster is six clusters, which is below the default unit of eight: one unit therefore takes the whole cell, and "was this cell
    /// serviced" has a clean yes/no answer rather than depending on which of its clusters the unit happened to take.
    /// </remarks>
    private const int PerCell = 250;

    private static ClMigPos PointAt(float x, float y, int tag = 0) =>
        new() { Bounds = new AABB2F { MinX = x, MinY = y, MaxX = x, MaxY = y }, Tag = tag };

    private static ushort ArchetypeId => Archetype<ClMigUnit>.Metadata.ArchetypeId;

    private static ArchetypeClusterState ClusterStateOf(DatabaseEngine dbe) =>
        dbe._archetypeStates[Archetype<ClMigUnit>.Metadata.ArchetypeId].ClusterState;

    private DatabaseEngine SetupEngine(float budgetMs, float criticalRatio = 1.0f, float agingRate = 0.05f, int queueMaxCells = 4096,
        int worstClustersPerUnit = 8)
    {
        var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<ClMigPos>();
        dbe.RegisterComponentFromAccessor<ClMigScratch>();
        dbe.ConfigureSpatialGrid(SpatialGridConfig.Flat(
            new Vector2(0, 0),
            new Vector2(WorldMax, WorldMax),
            CellSize,
            // The drift gate is switched off so relocation cannot repair a cell out from under the queue: this fixture is about which cell the PLANNER
            // picks, and a step-10 relocation quietly tightening a cell would remove candidates for a reason nothing here is asserting about.
            clusterTargetExtentRatio: 100f,
            clusterRepairExtentRatio: 0.75f,
            reclusterBudgetMs: budgetMs, batchSpawnSortThreshold: 0 /* step 15: this fixture builds its layout by spawn ORDER; the Morton sort would tighten it at birth */,
            repairWorstClustersPerUnit: worstClustersPerUnit,
            clusterRepairCriticalExtentRatio: criticalRatio,
            repairAgingRatePerTick: agingRate,
            repairQueueMaxCells: queueMaxCells,
            // Constant-mode target (step 14): at 250 entities per cell the density-derived target is 0.76, which sits above the 0.75 repair
            // gate this fixture drives and would change which cells nominate.
            clusterTargetPackingSlack: 0f));
        dbe.InitializeArchetypes();
        return dbe;
    }

    /// <summary>
    /// Fill <see cref="CellCount"/> cells along X, each with entities spawned in an order uncorrelated with position so first fit degrades them.
    /// </summary>
    /// <remarks>
    /// The two coprime strides put consecutive spawns far apart inside a cell, so each cluster first fit fills ends up holding points from all over it —
    /// the decayed layout reached through the engine's own placement rather than by writing cluster storage directly.
    /// </remarks>
    private static void SpawnDegradedCells(DatabaseEngine dbe, int cells = CellCount)
    {
        using (var tx = dbe.CreateQuickTransaction())
        {
            for (var c = 0; c < cells; c++)
            {
                var originX = c * CellSize;
                for (var i = 0; i < PerCell; i++)
                {
                    var x = originX + 4f + ((i * 37) % 92);
                    var y = 4f + ((i * 61) % 92);
                    tx.Spawn<ClMigUnit>(ClMigUnit.Pos.Set(PointAt(x, y, (c * PerCell) + i)));
                }
            }

            tx.Commit();
        }

        dbe.WriteTickFence(1);
    }

    /// <summary>The set of cells this run actually made tighter — the outcome definition of "serviced".</summary>
    /// <remarks>
    /// <para><b>Measured on the BOUNDS, not on the queue.</b> An earlier version watched cells disappear from the ranked array between ticks and it was
    /// wrong twice over: the ranked view is only meaningful immediately after a re-rank, and — more importantly — a cell leaves the queue whether it was
    /// re-packed or merely declined as a no-op. "Was serviced" has to mean "got tighter", or a queue that services nobody can claim it serviced everybody
    /// by dropping them all.</para>
    /// <para>The tolerance is a strict decrease. A Morton re-pack of a cell whose entities were spawned out of geometric order improves the mean extent by
    /// a wide margin (measured elsewhere at 87.0 -> 23.0), so no epsilon is needed to separate it from float noise.</para>
    /// </remarks>
    private static HashSet<int> RunAndCollectServicedCells(DatabaseEngine dbe, int firstTick, int ticks)
    {
        var before = MeanExtentPerCell(dbe);

        for (var t = 0; t < ticks; t++)
        {
            KeepTheFenceAlive(dbe);
            dbe.WriteTickFence(firstTick + t);
        }

        var after = MeanExtentPerCell(dbe);
        var serviced = new HashSet<int>();
        for (var c = 0; c < CellCount; c++)
        {
            // `after > 0` is not belt-and-braces. MeanExtentPerCell skips a cluster with no recorded bound and returns 0 when it skipped them all, so a
            // repair that LOST a cell's clusters rather than tightening them would read as the largest improvement of the run. That is the same shape as
            // the vacuity found in step 12's own fixtures — a measurement over an empty set scoring better than any real one.
            if (before[c] > 0d && after[c] > 0d && after[c] < before[c])
            {
                serviced.Add(c);
            }
        }

        return serviced;
    }

    /// <summary>
    /// Re-write one entity's position, unchanged, so the archetype has work this tick.
    /// </summary>
    /// <remarks>
    /// <para><b>Not padding: without it the queue is never consulted after the world settles, and that is a real limit of step 11 rather than an
    /// artefact of the fixture.</b> <c>PrepareArchetypeFenceCore</c> returns <c>false</c> for an archetype nothing wrote to, and the wrapper then skips
    /// planning entirely — because a repair plan allocates clusters that THIS tick's Migrate and Finalize must consume, and neither runs for an idle
    /// archetype. So a queue full of candidates in a world that has gone completely still is never drained. Closing that needs the fence re-armed by queue
    /// depth alone, which collides head-on with AC-10.8 ("a tick with no movement does no relocation work and allocates nothing") and is deliberately out
    /// of step 11's scope.</para>
    /// <para><b>Which is also why this is the honest way to test AC-11.3.</b> The AC is about a queue that is "permanently over-subscribed" — a condition
    /// that presupposes candidates keep arriving, i.e. a world still in motion. One unchanged write per tick is the smallest thing that models that: the
    /// fence stays live, and no cell is degraded by it, so a cell repaired on tick 5 is still tight at tick 40 and the before/after comparison stays
    /// meaningful.</para>
    /// </remarks>
    private static unsafe void KeepTheFenceAlive(DatabaseEngine dbe)
    {
        using var tx = dbe.CreateQuickTransaction();
        var accessor = tx.For<ClMigUnit>();
        try
        {
            foreach (var cluster in accessor.GetClusterEnumerator())
            {
                var bits = cluster.OccupancyBits;
                if (bits == 0)
                {
                    continue;
                }

                var slot = System.Numerics.BitOperations.TrailingZeroCount(bits);
#pragma warning disable TYPHON009 // Read-only: the value is written straight back unchanged.
                var positions = cluster.GetSpan(ClMigUnit.Pos);
#pragma warning restore TYPHON009
                cluster.WriteSpatial(ClMigUnit.Pos, slot, positions[slot]);
                break;
            }
        }
        finally
        {
            accessor.Dispose();
        }

        tx.Commit();
    }

    /// <summary>Mean per-cluster maximum axis extent, one entry per degraded cell, indexed by the cell's position in the row.</summary>
    private static double[] MeanExtentPerCell(DatabaseEngine dbe)
    {
        var state = ClusterStateOf(dbe);
        var grid = dbe.SpatialGrid;
        var result = new double[CellCount];

        for (var c = 0; c < CellCount; c++)
        {
            var cellKey = grid.WorldToCellKey((c * CellSize) + 50f, 50f, 0f);
            var clusters = state.CellClusterPool.GetClusters(cellKey);
            var total = 0d;
            var counted = 0;
            for (var i = 0; i < clusters.Length; i++)
            {
                var chunkId = clusters[i];
                if ((uint)chunkId >= (uint)state.ClusterAabbs.Length)
                {
                    continue;
                }

                ref var box = ref state.ClusterAabbs[chunkId];
                if (float.IsPositiveInfinity(box.MinX))
                {
                    continue;
                }

                total += MathF.Max(box.MaxX - box.MinX, box.MaxY - box.MinY);
                counted++;
            }

            result[c] = counted == 0 ? 0d : total / counted;
        }

        return result;
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════════════════════════
    // AC-11.3 — no starvation
    // ══════════════════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// With far more candidates than a one-unit-per-tick budget can service, ageing carries every one of them to the head.
    /// </summary>
    /// <remarks>
    /// <para><b>Driven against <c>CellRepairQueue</c> directly, and that is the only way this can be tested honestly.</b> The engine-level version of
    /// this test cannot be made machine-independent: the budget is spent against a MEASURED per-entity cost, a Debug build migrates an entity in ~24 us and
    /// a Release build in ~1.5 us, so a budget that admits exactly one unit per tick in one configuration admits eighteen in the other. Sizing it for Debug
    /// makes the test vacuous on the gate, which runs Release; sizing it for Release makes it fail outright in Debug. Both were tried, and the second was
    /// observed as a flake in one full-suite run out of three before the cause was understood.</para>
    /// <para><b>The property AC-11.3 is about lives in the queue, not in the engine.</b> "Every candidate is eventually serviced under a permanently
    /// over-subscribed queue" is a statement about the RANKING — that no candidate can be outranked for ever. Servicing one candidate per tick and asking
    /// whether all of them are eventually reached tests exactly that, deterministically, in milliseconds. The engine-level tests either side of this one
    /// establish that the queue is what the planner consults.</para>
    /// <para><b>The candidates are deliberately lopsided.</b> One cell is far more degraded than the rest, so a pure ranking would service it, watch it be
    /// re-nominated, and service it again for ever — which is precisely the starvation step 12 exhibited with its cell-key ordering, and what ageing
    /// exists to break. A uniform set would be serviced round-robin by the tie-break alone and would prove nothing.</para>
    /// </remarks>
    [Test]
    [VerifiesRule("TH-03")]
    public void AgeingCarriesEveryCandidateToTheHeadOfTheQueue()
    {
        using var dbe = SetupEngine(budgetMs: 1.0f);
        SpawnDegradedCells(dbe);
        var state = ClusterStateOf(dbe);
        var grid = dbe.SpatialGrid;

        var queue = new CellRepairQueue(maxCells: 4096, agingRatePerTick: 0.05f);
        var cellKeys = new int[CellCount];
        for (var c = 0; c < CellCount; c++)
        {
            cellKeys[c] = grid.WorldToCellKey((c * CellSize) + 50f, 50f, 0f);
        }

        var serviced = new HashSet<int>();
        var nominations = new List<ArchetypeClusterState.RepairNomination>();

        for (var tick = 1; tick <= 400; tick++)
        {
            // Re-nominated every tick, exactly as a cell that stays degraded would be. Cell 0 is worst by a wide margin, so ranking alone would pick it
            // every single time.
            // A WIDE gap — 0.95 against 0.20, not 0.80. At the narrower spacing the score ratio was 1.19, which an age factor capped at 2x still
            // overcomes within four ticks: `Math.Min(2f, 1 + rate * age)` would have left this test green while contradicting TH-03's claim that the age
            // term is UNBOUNDED in the tick count. The point of the invariant is that no ceiling on ageing exists, so the fixture has to need one high
            // enough that any ceiling would fail.
            nominations.Clear();
            for (var c = 0; c < CellCount; c++)
            {
                nominations.Add(new ArchetypeClusterState.RepairNomination(cellKeys[c], c == 0 ? 0.95f : 0.20f));
            }

            queue.Absorb(nominations, grid, state, tick);
            queue.Rerank(grid, state, tick);

            var ranked = queue.Ranked;
            if (ranked.Length == 0)
            {
                continue;
            }

            // One unit per tick: take the head and forget it, which is what the planner does on a successful service.
            var head = ranked[0];
            serviced.Add(head);
            queue.Remove(head);

            if (serviced.Count == CellCount)
            {
                Assert.Pass($"every one of {CellCount} candidates reached the head within {tick} ticks");
            }
        }

        Assert.Fail($"only {serviced.Count} of {CellCount} candidates ever reached the head over 400 ticks — the rest are starved, which ranking alone "
            + "always does and which the age factor exists to prevent");
    }

    /// <summary>
    /// With ageing switched off, the same scenario starves — the ablation that makes the test above mean something.
    /// </summary>
    /// <remarks>
    /// Written as a test rather than performed by hand because "every candidate was serviced" is satisfied trivially by a queue that happens to hold
    /// candidates of equal score, and this pins that the run above is genuinely lopsided: without the age term, the worst cell wins every tick for ever.
    /// </remarks>
    [Test]
    [RuleMutant("TH-03")]
    public void WithoutAgeingTheWorstCandidateStarvesEveryoneElse()
    {
        using var dbe = SetupEngine(budgetMs: 1.0f);
        SpawnDegradedCells(dbe);
        var state = ClusterStateOf(dbe);
        var grid = dbe.SpatialGrid;

        var queue = new CellRepairQueue(maxCells: 4096, agingRatePerTick: 0f);
        var cellKeys = new int[CellCount];
        for (var c = 0; c < CellCount; c++)
        {
            cellKeys[c] = grid.WorldToCellKey((c * CellSize) + 50f, 50f, 0f);
        }

        var serviced = new HashSet<int>();
        var nominations = new List<ArchetypeClusterState.RepairNomination>();

        for (var tick = 1; tick <= 100; tick++)
        {
            nominations.Clear();
            for (var c = 0; c < CellCount; c++)
            {
                nominations.Add(new ArchetypeClusterState.RepairNomination(cellKeys[c], c == 0 ? 0.95f : 0.20f));
            }

            queue.Absorb(nominations, grid, state, tick);
            queue.Rerank(grid, state, tick);

            var ranked = queue.Ranked;
            if (ranked.Length == 0)
            {
                continue;
            }

            var head = ranked[0];
            serviced.Add(head);
            queue.Remove(head);
        }

        // Exactly the one worst cell, not merely "fewer than six". A regression that let five of six through would still satisfy a LessThan bound while
        // making the positive arm's success attributable to sort tie-breaks rather than to ageing — which is the specific confusion this arm exists to
        // rule out.
        Assert.That(serviced, Is.EquivalentTo(new[] { cellKeys[0] }),
            $"with ageing OFF, {serviced.Count} distinct candidates reached the head. Cell 0 outranks every other by a wide margin and nothing else can "
            + "displace it, so anything but {cell 0} means the scenario is not lopsided and the arm above passes for the wrong reason");
    }

    /// <summary>
    /// A cell nominated while it cools re-enters the queue when its cooldown ends, at the worst degradation it was held at and with no nomination on that
    /// tick; a cell nothing nominated while it cooled does not re-enter (RP-07).
    /// </summary>
    /// <remarks>
    /// <para><b>Driven against <c>CellRepairQueue</c> directly, because the engine cannot stage the case that matters.</b> The hold exists for a cell that
    /// goes still while it cools and is never nominated again. An engine-level fixture has to keep the fence alive with a write, and a write to a degraded
    /// cell nominates it — so there the hold and a fresh nomination are indistinguishable, and a queue that dropped held nominations would pass.</para>
    /// <para><b>The quiet cell is the control.</b> Without it, "the held cell came back" is also satisfied by a release that re-queues every cooled cell,
    /// which would bring back cells with nothing left to repair.</para>
    /// </remarks>
    [Test]
    [VerifiesRule("RP-07")]
    public void ANominationHeldDuringTheCooldownReturnsWhenTheCooldownEnds()
    {
        using var dbe = SetupEngine(budgetMs: 1.0f);
        SpawnDegradedCells(dbe);
        var state = ClusterStateOf(dbe);
        var grid = dbe.SpatialGrid;
        var held = grid.WorldToCellKey(50f, 50f, 0f);
        var quiet = grid.WorldToCellKey(CellSize + 50f, 50f, 0f);

        const int Cooldown = 10;
        var queue = new CellRepairQueue(maxCells: 4096, agingRatePerTick: 0f, cooldownTicks: Cooldown);
        var nominations = new List<ArchetypeClusterState.RepairNomination> { new(held, 0.8f), new(quiet, 0.8f) };
        queue.Absorb(nominations, grid, state, 1);
        queue.MarkRepaired(held, 1);
        queue.MarkRepaired(quiet, 1);
        Assert.That((queue.Count, queue.CoolingCount), Is.EqualTo((0, 2)), "a repaired cell must leave the candidates and start cooling");

        // Nominated on every tick of its cooldown, the worst reading in the middle — then still: nothing nominates it on the tick the cooldown ends.
        for (var tick = 2; tick <= Cooldown; tick++)
        {
            queue.ReleaseCooled(grid, state, tick);
            nominations.Clear();
            nominations.Add(new ArchetypeClusterState.RepairNomination(held, tick == 5 ? 0.95f : 0.85f));
            queue.Absorb(nominations, grid, state, tick);
            Assert.Multiple(() =>
            {
                Assert.That(queue.Count, Is.Zero, $"tick {tick}: a cell inside its {Cooldown}-tick cooldown became a repair candidate");

                // What the safety valve reads: a cooling cell must not be offered it.
                Assert.That(queue.DegradationOf(held), Is.Zero, $"tick {tick}: a cooling cell reports a candidate's degradation to the valve");

                // What the fence's early-out reads: nothing to plan until the cooldown ends.
                Assert.That(queue.NeedsPlanning(tick), Is.False, $"tick {tick}: the queue asks for planning with nothing waiting and no cooldown ending");
            });
        }

        Assert.That(queue.HeldDegradationOf(held), Is.EqualTo(0.95f), "the cooldown must hold the WORST degradation nominated, not the latest");
        Assert.That(queue.NeedsPlanning(1 + Cooldown), Is.True,
            "a cooldown ends on this tick with no nomination and no candidate — the fence must still plan, or the released cell is never re-queued");
        queue.ReleaseCooled(grid, state, 1 + Cooldown);
        queue.Rerank(grid, state, 1 + Cooldown);

        Assert.Multiple(() =>
        {
            Assert.That(queue.Ranked.ToArray(), Is.EqualTo(new[] { held }),
                "on the tick its cooldown ended, the cell nominated while it cooled must be a candidate and the one nothing nominated must not — a held "
                + "nomination dropped instead loses a cell that went still, and a release that re-queues everything brings back cells with nothing to repair");
            Assert.That(queue.DegradationOf(held), Is.EqualTo(0.95f), "the cell must come back at the WORST degradation it was nominated at while it cooled");
            Assert.That(queue.CoolingCount, Is.Zero, "both cooldowns ended, so nothing should still be cooling");
        });
    }

    /// <summary>
    /// A cooling cell takes none of the queue's capacity, <c>Clear</c> drops it with the candidates, and at a cooldown of 0 a repaired cell is forgotten
    /// exactly as <c>Remove</c> forgets it (RP-07).
    /// </summary>
    [Test]
    [VerifiesRule("RP-07")]
    public void ACoolingCellTakesNoCapacityAndClearDropsIt()
    {
        using var dbe = SetupEngine(budgetMs: 1.0f);
        SpawnDegradedCells(dbe);
        var state = ClusterStateOf(dbe);
        var grid = dbe.SpatialGrid;
        var a = grid.WorldToCellKey(50f, 50f, 0f);
        var b = grid.WorldToCellKey(CellSize + 50f, 50f, 0f);
        var nominations = new List<ArchetypeClusterState.RepairNomination> { new(a, 0.9f) };

        // A queue of ONE, whose one place a cooling cell must not occupy.
        var queue = new CellRepairQueue(maxCells: 1, agingRatePerTick: 0f, cooldownTicks: 10);
        queue.Absorb(nominations, grid, state, 1);
        queue.MarkRepaired(a, 1);
        nominations[0] = new ArchetypeClusterState.RepairNomination(b, 0.2f);
        queue.Absorb(nominations, grid, state, 2);
        Assert.That((queue.Count, queue.TotalEvicted, queue.CoolingCount), Is.EqualTo((1, 0L, 1)),
            "a cooling cell took the queue's only place: the newcomer was dropped or something was evicted to make room");

        // After a rebuild a cell key names another cell, so the cooling state goes with the candidates — and leaves no release behind.
        queue.Clear();
        Assert.That((queue.Count, queue.CoolingCount, queue.NeedsPlanning(1_000)), Is.EqualTo((0, 0, false)), "Clear left cooling state behind");
        nominations[0] = new ArchetypeClusterState.RepairNomination(a, 0.9f);
        queue.Absorb(nominations, grid, state, 3);
        Assert.That(queue.Count, Is.EqualTo(1), "a cell cooling before Clear was held after it instead of admitted");

        // Cooldown 0: MarkRepaired is the Remove it replaced, and the next nomination is admitted at once.
        var off = new CellRepairQueue(maxCells: 4096, agingRatePerTick: 0f, cooldownTicks: 0);
        off.Absorb(nominations, grid, state, 1);
        off.MarkRepaired(a, 1);
        off.Absorb(nominations, grid, state, 1);
        Assert.That((off.Count, off.CoolingCount), Is.EqualTo((1, 0)), "with the cooldown off, a repaired cell was not simply forgotten");
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════════════════════════
    // AC-11.2 — degradation is bounded: the safety valve
    // ══════════════════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// A cell past the critical threshold is serviced even when the budget cannot pay for its unit, and the valve fires at most once per tick.
    /// </summary>
    /// <remarks>
    /// <para><b>The budget is set below one unit's cost, so nothing can be admitted normally.</b> That is what makes this a test of the valve rather than
    /// of the ranking: without it, "the critical cell was serviced" would be satisfied by the ordinary path and the valve could be deleted without
    /// breaking anything.</para>
    /// <para><b>The critical threshold is lowered rather than the cells made worse.</b> Degradation is <c>maxAxisExtent / cellSize</c>, and a cluster
    /// confined to its own cell cannot exceed ~1.05 — so a fixture that wanted to reach the shipped default of 1.0 would have to build a cluster straddling
    /// a cell boundary, which is the outlier guard's business and a different mechanism entirely. Lowering the knob tests the same code path against the
    /// same arithmetic.</para>
    /// <para><b>At most once per tick is asserted, not assumed.</b> It is the entire bound on the overshoot AC-11.1 permits: an unbounded number of valve
    /// admissions per tick would be an unbounded overrun wearing a threshold as a disguise.</para>
    /// </remarks>
    [Test]
    [VerifiesRule("RP-01")]
    public void ACriticalCellIsServicedEvenWhenTheBudgetCannotAffordIt()
    {
        // 0.05 ms buys ~33 entities at the seeded cost — far below the 250 a whole-cell unit needs — so every admission below is the valve's.
        using var dbe = SetupEngine(budgetMs: 0.05f, criticalRatio: 0.8f, worstClustersPerUnit: 0);
        SpawnDegradedCells(dbe);

        var repairedTicks = 0;
        var totalRepaired = 0;

        for (var tick = 2; tick <= 12; tick++)
        {
            dbe.WriteTickFence(tick);
            var t = dbe.GetSpatialTelemetry(ArchetypeId);

            Assert.That(t.RepairValveFires, Is.LessThanOrEqualTo(1),
                $"tick {tick} fired the safety valve {t.RepairValveFires} times — the overshoot AC-11.1 permits is ONE unit, not one per critical cell");

            if (t.RepairedEntityCount > 0)
            {
                repairedTicks++;
                totalRepaired += t.RepairedEntityCount;
                Assert.That(t.RepairValveFires, Is.EqualTo(1),
                    $"tick {tick} re-packed {t.RepairedEntityCount} entities on a budget that cannot afford a unit, without the valve accounting for it");
            }
        }

        Assert.Multiple(() =>
        {
            Assert.That(repairedTicks, Is.GreaterThan(0),
                "no cell was ever serviced, so degradation is unbounded under a budget that cannot keep up — which is precisely what the valve exists to "
                + "prevent");
            Assert.That(totalRepaired, Is.GreaterThan(0), "the valve fired but moved nothing");
        });
    }

    /// <summary>
    /// With the valve disabled, the same under-budget scenario services nobody — the ablation that makes the test above mean something.
    /// </summary>
    /// <remarks>
    /// Written as a test rather than performed by hand because the valve is the kind of mechanism whose absence is invisible: every counter reads zero
    /// either way, and "no repair happened" is indistinguishable from "no repair was needed" without an arm that pins the difference.
    /// </remarks>
    [Test]
    [RuleMutant("RP-01")]
    public void WithTheValveDisabledAnUnderBudgetQueueServicesNobody()
    {
        using var dbe = SetupEngine(budgetMs: 0.05f, criticalRatio: 0f, worstClustersPerUnit: 0);
        SpawnDegradedCells(dbe);

        var refusals = 0;
        for (var tick = 2; tick <= 12; tick++)
        {
            dbe.WriteTickFence(tick);
            var t = dbe.GetSpatialTelemetry(ArchetypeId);
            refusals += t.RepairUnitsRefused;

            Assert.That(t.RepairedEntityCount, Is.Zero, $"tick {tick} re-packed entities with the valve off and a budget below one unit");
            Assert.That(t.RepairValveFires, Is.Zero, $"tick {tick} fired a valve that is switched off");
        }

        Assert.That(refusals, Is.GreaterThan(0),
            "no unit was ever refused, so the budget was not actually binding and the arm above proves nothing by contrast");
    }

    /// <summary>
    /// When the budget cannot buy a unit the planner skips the rank and finds the valve's cell by threshold instead (#949) — and it must find the SAME
    /// cell the ranking would have hoisted: the highest-scoring critical candidate, not merely a critical one.
    /// </summary>
    /// <remarks>
    /// <para><b>This pins the equivalence the optimisation rests on.</b> Skipping the sort is only sound if the choice among the cells the valve may take
    /// is unchanged, and the ranked path takes the first critical cell in RANK order — which is the best-scoring one, since
    /// <c>Score = degradation x tierWeight x clusterCount x ageFactor</c>.</para>
    /// <para><b>The first version returned whichever candidate the dictionary yielded first, and the cost was measured rather than argued.</b> An arbitrary
    /// critical cell can be one <c>RepairOneCell</c> declines outright — a cell of one cluster is already its own optimal packing — and declining it spends
    /// the tick's single valve admission on nothing. On SWG Tatooine x16 that cost Creature 5 % of its repaired entities and 5 % of its units, and widened
    /// its run-to-run spread from 0.7 entities to 5.8. No fixture in this repo would have caught it; only the demo did.</para>
    /// <para><b>The degradations ascend with the cell index deliberately</b>, so the worst candidate is the one a dictionary walk reaches LAST. An
    /// implementation that returns the first qualifying entry fails here rather than passing by luck.</para>
    /// </remarks>
    [Test]
    [VerifiesRule("RP-01")]
    public void TheStarvedValvePicksTheCellTheRankingWouldHaveHoisted()
    {
        using var dbe = SetupEngine(budgetMs: 1.0f);
        SpawnDegradedCells(dbe);
        var state = ClusterStateOf(dbe);
        var grid = dbe.SpatialGrid;

        const float Critical = 0.5f;
        var queue = new CellRepairQueue(maxCells: 4096, agingRatePerTick: 0f);
        var nominations = new List<ArchetypeClusterState.RepairNomination>();
        for (var c = 0; c < CellCount; c++)
        {
            nominations.Add(new ArchetypeClusterState.RepairNomination(grid.WorldToCellKey((c * CellSize) + 50f, 50f, 0f), 0.60f + (c * 0.05f)));
        }

        queue.Absorb(nominations, grid, state, 1);
        queue.Rerank(grid, state, 1);

        // What the ranked path would service: the first candidate in rank order that clears the threshold.
        var ranked = queue.Ranked;
        var expected = -1;
        for (var i = 0; i < ranked.Length && expected < 0; i++)
        {
            if (queue.DegradationOf(ranked[i]) >= Critical)
            {
                expected = ranked[i];
            }
        }

        Assert.That(expected, Is.GreaterThanOrEqualTo(0), "precondition: the ranking must hold a critical candidate, or the comparison below is vacuous");
        Assert.Multiple(() =>
        {
            Assert.That(queue.TryFindCritical(Critical, grid, state, 1, out var found), Is.True,
                "the starved path must find the critical candidate the ranked scan can see");
            Assert.That(found, Is.EqualTo(expected),
                "the starved path must service the cell the ranking would have hoisted — an arbitrary critical cell can be one RepairOneCell declines, "
                + "which spends the tick's single valve admission on nothing");
        });
    }

    /// <summary>
    /// With nothing critical queued there is no valve cell to find, so a starved planner does nothing at all rather than servicing the merely bad.
    /// </summary>
    [Test]
    [VerifiesRule("RP-01")]
    public void NothingCriticalMeansTheStarvedPlannerFindsNoValveCell()
    {
        using var dbe = SetupEngine(budgetMs: 1.0f);
        SpawnDegradedCells(dbe);
        var state = ClusterStateOf(dbe);
        var grid = dbe.SpatialGrid;

        var queue = new CellRepairQueue(maxCells: 4096, agingRatePerTick: 0f);
        var nominations = new List<ArchetypeClusterState.RepairNomination>();
        for (var c = 0; c < CellCount; c++)
        {
            nominations.Add(new ArchetypeClusterState.RepairNomination(grid.WorldToCellKey((c * CellSize) + 50f, 50f, 0f), 0.40f));
        }

        queue.Absorb(nominations, grid, state, 1);
        Assert.Multiple(() =>
        {
            Assert.That(queue.Count, Is.EqualTo(CellCount), "precondition: the candidates are queued, they are merely not critical");
            Assert.That(queue.TryFindCritical(0.8f, grid, state, 1, out _), Is.False, "0.40 degradation is below the 0.80 threshold and is not the valve's");
            Assert.That(queue.TryFindCritical(0f, grid, state, 1, out _), Is.False, "a disabled valve (ratio 0) selects nobody, however degraded");
        });
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════════════════════════
    // AC-11.5 — the queue costs less than the work it schedules
    // ══════════════════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Queue maintenance — absorbing nominations and re-ranking — is a small fraction of the budget the queue schedules, and is reported.
    /// </summary>
    /// <remarks>
    /// <para><b>§5.6 states the failure mode outright: "a queue that costs more to maintain than the work it schedules is a net loss".</b> The mechanism
    /// that keeps it cheap is laziness — a re-rank runs only when nominations have arrived or when <c>SpatialGrid.TierVersion</c> has moved, never on a
    /// timer — so what would break this is making the rank unconditional, which is exactly the tempting simplification.</para>
    /// <para><b>Ten per cent, and measured against the budget rather than against elapsed time.</b> The budget is what the queue exists to allocate, so it
    /// is the honest denominator; wall-clock would make the threshold a property of the machine.</para>
    /// </remarks>
    [Test]
    [VerifiesRule("TH-03")]
    public void QueueMaintenanceIsASmallFractionOfTheWorkItSchedules()
    {
        using var dbe = SetupEngine(budgetMs: 1.0f);
        SpawnDegradedCells(dbe);

        var maintenanceMs = 0d;
        var scheduledMs = 0d;

        for (var tick = 2; tick <= 20; tick++)
        {
            dbe.WriteTickFence(tick);
            var t = dbe.GetSpatialTelemetry(ArchetypeId);
            maintenanceMs += t.RepairQueueMaintenanceMs;
            scheduledMs += t.ReclusterBudgetUsedMs;
        }

        Assert.Multiple(() =>
        {
            Assert.That(scheduledMs, Is.GreaterThan(0d), "nothing was ever scheduled, so the ratio below has no denominator and asserts nothing");

            // The ratio alone passes HARDER the less is reported, so zero — an accrual that stopped accruing — would be the best possible score. AC-11.5
            // asks for a cost that is "small AND reported"; without this line the second half is unestablished and the counter could be deleted.
            Assert.That(maintenanceMs, Is.GreaterThan(0d),
                "queue maintenance reported exactly zero over nineteen ticks that scheduled real work — the accrual is not running, so the ratio below is "
                + "measuring nothing");
            Assert.That(maintenanceMs, Is.LessThan(scheduledMs * 0.1d),
                $"queue maintenance cost {maintenanceMs:F3} ms against {scheduledMs:F3} ms of scheduled work — more than a tenth, which is the point at "
                + "which the queue starts costing more than it saves");
        });
    }

    /// <summary>
    /// The safety valve's cluster cap equals the configured unit's default, so a valve admission costs what an ordinary one costs.
    /// </summary>
    /// <remarks>
    /// <c>ValveClustersPerUnit</c> cannot read <c>RepairWorstClustersPerUnit</c> — the branch it serves exists precisely for the configuration where that
    /// setting is <c>0</c>, meaning "the whole cell", which is the unbounded overshoot the cap exists to prevent. So it duplicates the default, and a
    /// duplicated constant whose only tie to its twin is a sentence in a comment drifts the first time somebody tunes one of them. Asserting the equality
    /// costs one line and turns a silent divergence into a red test.
    /// </remarks>
    [Test]
    public void TheValveCapMatchesTheDefaultUnitSize()
    {
        var defaults = SpatialGridConfig.Flat(new Vector2(0, 0), new Vector2(WorldMax, WorldMax), CellSize);
        Assert.That(ArchetypeClusterState.ValveClustersPerUnit, Is.EqualTo(defaults.RepairWorstClustersPerUnit),
            "the valve's cluster cap has drifted from RepairWorstClustersPerUnit's default, so a safety-valve admission no longer costs what an ordinary "
            + "unit costs — and RP-01's bound on the one permitted overshoot is stated in terms of the latter");
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════════════════════════
    // The cap, and the tier fallback
    // ══════════════════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// The queue never exceeds its configured cap, and says so when it has been dropping candidates.
    /// </summary>
    /// <remarks>
    /// <c>AC-11.8</c>'s "does not grow an unbounded queue", asserted against a cap small enough that the fixture actually reaches it. A persistent queue is
    /// the one structure step 11 adds that a per-tick list could not have leaked, so the ceiling is not theoretical.
    /// </remarks>
    [Test]
    [VerifiesRule("TH-03")]
    public void TheQueueStopsAtItsCapAndReportsTheEvictions()
    {
        using var dbe = SetupEngine(budgetMs: 0.05f, criticalRatio: 0f, queueMaxCells: 3);
        SpawnDegradedCells(dbe);

        for (var tick = 2; tick <= 10; tick++)
        {
            dbe.WriteTickFence(tick);
            Assert.That(dbe.GetSpatialTelemetry(ArchetypeId).RepairQueueDepth, Is.LessThanOrEqualTo(3),
                $"tick {tick}: the queue passed the cap of 3 it was configured with");
        }

        Assert.That(dbe.GetSpatialTelemetry(ArchetypeId).RepairQueueEvicted, Is.GreaterThan(0),
            $"{CellCount} cells degraded against a cap of 3 and nothing was evicted — the cap held for want of candidates, not because it was enforced");
    }

    /// <summary>
    /// When the queue is full, the candidate that arrives LAST and scores highest displaces an incumbent rather than being dropped.
    /// </summary>
    /// <remarks>
    /// <b>The cap test above proves the ceiling holds; it says nothing about WHICH candidate goes.</b> An implementation that evicted the first entry the
    /// dictionary happened to enumerate, or that simply refused every newcomer once full, satisfies it exactly — and the second is the more likely
    /// regression, because "the queue is full, drop it" is the obvious reading. Ordering the arrivals worst-first and asserting the best one survives is
    /// what makes the eviction POLICY, rather than the bound, the thing under test.
    /// </remarks>
    [Test]
    [VerifiesRule("TH-03")]
    public void AHighScoringLateArrivalDisplacesAWeakerIncumbent()
    {
        using var dbe = SetupEngine(budgetMs: 1.0f, agingRate: 0f, queueMaxCells: 3);
        SpawnDegradedCells(dbe);
        var state = ClusterStateOf(dbe);
        var grid = dbe.SpatialGrid;

        var cellKeys = new int[CellCount];
        for (var c = 0; c < CellCount; c++)
        {
            cellKeys[c] = grid.WorldToCellKey((c * CellSize) + 50f, 50f, 0f);
        }

        var queue = new CellRepairQueue(maxCells: 3, agingRatePerTick: 0f);

        // Four weak candidates first — the cap of three is reached on the third, so the fourth already forces an eviction — then the strong one LAST, when
        // the queue can only admit it by displacing somebody.
        var nominations = new List<ArchetypeClusterState.RepairNomination>();
        for (var c = 0; c < 4; c++)
        {
            nominations.Add(new ArchetypeClusterState.RepairNomination(cellKeys[c], 0.20f));
        }

        nominations.Add(new ArchetypeClusterState.RepairNomination(cellKeys[4], 0.95f));

        queue.Absorb(nominations, grid, state, 1);
        queue.Rerank(grid, state, 1);

        var ranked = queue.Ranked.ToArray();

        Assert.Multiple(() =>
        {
            Assert.That(ranked, Has.Length.EqualTo(3), "the cap was not enforced, so nothing below is about which candidate it chose to keep");
            Assert.That(ranked, Does.Contain(cellKeys[4]),
                "the highest-scoring candidate arrived last into a full queue and was dropped — eviction is refusing newcomers rather than displacing the "
                + "worst incumbent, which makes the queue first-come-first-served with a ranking bolted on the side");
            Assert.That(ranked[0], Is.EqualTo(cellKeys[4]),
                "the strong candidate was admitted but does not rank first among the survivors, so the score is not what the ordering reads");
        });
    }

    /// <summary>
    /// An untiered cell outranks an equally-degraded TIERED one — the <see cref="SimTier.None"/> fallback, asserted where it can actually be seen.
    /// </summary>
    /// <remarks>
    /// <para><b>The bug this exists to catch is arithmetic.</b> <c>SimTier</c> is a BIT FLAG (<c>None = 0, Tier0 = 1, Tier1 = 2, Tier2 = 4, ...</c>), so
    /// the tier index is <c>TrailingZeroCount</c> of the byte — and <c>TrailingZeroCount(0)</c> is <b>32</b>, not 0. A weight of <c>1 / (1 + index)</c>
    /// without a <c>None</c> guard therefore scores an untiered cell at 1/33, below every real tier. Absent information must discount NOTHING, so
    /// <c>None</c> weighs 1.0.</para>
    /// <para><b>A uniformly untiered world cannot show this, and the first version of this test used one.</b> If every cell is
    /// <see cref="SimTier.None"/> then the wrong weight is a uniform scale factor: the ranking is bit-identical, the sort produces the same order, and
    /// deleting the fallback leaves the test green. The bug only becomes visible when tiered and untiered cells compete — so one cell here is promoted to
    /// <see cref="SimTier.Tier2"/> (weight ⅓) and the rest are left untiered, with identical degradation. With the fallback the untiered cells outrank it;
    /// without it, at 1/33 against ⅓, the tiered cell wins and the assertion fails.</para>
    /// </remarks>
    [Test]
    [VerifiesRule("TH-03")]
    public void AnUntieredCellOutranksAnEquallyDegradedTieredOne()
    {
        using var dbe = SetupEngine(budgetMs: 1.0f);
        SpawnDegradedCells(dbe);
        var state = ClusterStateOf(dbe);
        var grid = dbe.SpatialGrid;

        var cellKeys = new int[CellCount];
        for (var c = 0; c < CellCount; c++)
        {
            cellKeys[c] = grid.WorldToCellKey((c * CellSize) + 50f, 50f, 0f);
            Assert.That(grid.GetCell(cellKeys[c]).Tier, Is.Zero, $"cell {c} already carries a tier, so the contrast below is not the one intended");
        }

        // Cell 0 is the only tiered one. Every cell has the same population and the same nominated degradation, so the tier weight is the ONLY term that
        // differs and the ranking is a direct read of it.
        grid.SetCellTier(cellKeys[0], SimTier.Tier2);

        var queue = new CellRepairQueue(maxCells: 4096, agingRatePerTick: 0f);
        var nominations = new List<ArchetypeClusterState.RepairNomination>();
        for (var c = 0; c < CellCount; c++)
        {
            nominations.Add(new ArchetypeClusterState.RepairNomination(cellKeys[c], 0.85f));
        }

        queue.Absorb(nominations, grid, state, 1);
        queue.Rerank(grid, state, 1);

        var ranked = queue.Ranked;
        Assert.That(ranked.Length, Is.EqualTo(CellCount), "not every cell was queued, so the ordering below is over the wrong set");
        Assert.That(ranked[0], Is.Not.EqualTo(cellKeys[0]),
            "the Tier2 cell outranked five untiered cells of identical degradation and population — the only way that happens is TrailingZeroCount(0) == 32 "
            + "scoring 'no tier information' at 1/33 instead of 1.0, which collapses the ranking in every world whose game code assigns no tier");
        Assert.That(ranked[^1], Is.EqualTo(cellKeys[0]),
            "the tiered cell should rank LAST — a third of the weight of its untiered peers — and does not, so the tier term is not being applied at all");
    }

    /// <summary>
    /// The planner services the queue's HEAD, not an arbitrary member of it.
    /// </summary>
    /// <remarks>
    /// <b>Nothing else in this fixture would notice if it did not.</b> The ageing test drives <c>CellRepairQueue</c> directly, so it proves the ranking is
    /// right without proving anyone reads it; reversing the planner's iteration (<c>for (var i = rankedCount - 1; i >= 0; i--)</c>) leaves every other
    /// assertion here green. With a budget that affords one unit and one cell far worse than the rest, "which cell got tighter first" is a direct read of
    /// which end of the ranking the planner consulted.
    /// </remarks>
    [Test]
    [VerifiesRule("TH-03")]
    public void ThePlannerServicesTheHeadOfTheRanking()
    {
        // 0.2 ms, not 1.0. A two-cluster unit is ~98 entities and the first tick has no measurement yet, so it is projected at the 1 500 ns seed —
        // 147 us each. At 1 ms the budget affords all six cells (882 us), every one is serviced, and head-versus-tail becomes indistinguishable: the
        // reversed-iteration mutation this test exists to catch passes. 0.2 ms affords exactly one.
        using var dbe = SetupEngine(budgetMs: 0.2f, agingRate: 0f, worstClustersPerUnit: 2);
        SpawnDegradedCells(dbe);

        var before = MeanExtentPerCell(dbe);
        var state = ClusterStateOf(dbe);
        var grid = dbe.SpatialGrid;

        // Rank is proportional to cluster count, and every cell here holds the same population — so the tier is the lever that makes one cell the
        // unambiguous head. Tier0 is the highest interest and therefore weight 1; the rest are pushed down to a third.
        for (var c = 1; c < CellCount; c++)
        {
            grid.SetCellTier(grid.WorldToCellKey((c * CellSize) + 50f, 50f, 0f), SimTier.Tier2);
        }

        KeepTheFenceAlive(dbe);
        dbe.WriteTickFence(2);
        KeepTheFenceAlive(dbe);
        dbe.WriteTickFence(3);

        var after = MeanExtentPerCell(dbe);
        var tightened = 0;
        for (var c = 0; c < CellCount; c++)
        {
            if (before[c] > 0d && after[c] > 0d && after[c] < before[c])
            {
                tightened++;
            }
        }

        Assert.Multiple(() =>
        {
            Assert.That(state.RepairQueue, Is.Not.Null, "no queue was ever created, so nothing below is about the ranking");
            Assert.That(tightened, Is.LessThan(CellCount),
                $"{tightened} of {CellCount} cells were repaired, so the budget afforded everybody and which end of the ranking the planner read cannot be "
                + "observed — the reversed-iteration mutation would pass");
            Assert.That(after[0], Is.LessThan(before[0]),
                $"cell 0 outranks every other cell by a factor of three and was not the one repaired (extent {before[0]:F1} -> {after[0]:F1}) — the planner "
                + "is not consulting the head of the ranking");
        });
    }
}
