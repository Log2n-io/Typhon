using System;
using System.Collections.Generic;
using System.Numerics;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests;

/// <summary>
/// <c>Q2</c> — the escape rate (#872 step 9, <c>AC-9.8</c>): what fraction of per-tick cluster-AABB changes leave the box they replaced.
/// </summary>
/// <remarks>
/// <para><b>Why this exists before the R-Tree does.</b> <c>C5</c> chooses escape-bound maintenance over remove-and-reinsert on an economic argument —
/// ~14 µs/cell/tick against ~94-235 µs — and that argument is entirely a function of this ratio. The measurement does not need a tree: the recompute pass
/// already holds the old box and the new one, so the number is knowable with the linear index still in place. Building the tree first and measuring after
/// would risk discovering the premise was wrong with the implementation already committed to it.</para>
/// <para><b>What is measured is a bound, not the rate.</b> <c>C5</c>'s escape is "left the LEAF NODE's MBR"; this counts "left its own previous box". A leaf
/// MBR is the union of up to eleven entries, so it is never smaller than any one of them — a box still inside its own previous box is certainly still inside
/// the leaf. The reported figure is therefore an <b>upper bound on escapes</b>, and the real in-place rate can only be better.</para>
/// <para>Reported, not asserted against a threshold. A number that decides a design choice should be read, and a threshold picked today would be a guess
/// dressed as a gate — the assertions here only pin that the instrument is wired and non-vacuous.</para>
/// </remarks>
[TestFixture]
class ClusterAabbEscapeRateTests : TestBase<ClusterAabbEscapeRateTests>
{
    [OneTimeSetUp]
    public void OneTimeSetup()
    {
    }

    private const float CellSize = 100f;

    private static ClCohPos PointAt(float x, float y) =>
        new() { Bounds = new AABB2F { MinX = x, MinY = y, MaxX = x, MaxY = y }, Mass = 1.0f };

    private DatabaseEngine SetupEngine(IServiceScope scope)
    {
        var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<ClCohPos>();
        dbe.ConfigureSpatialGrid(SpatialGridConfig.Flat(new Vector2(0, 0), new Vector2(1000f, 1000f), CellSize));
        dbe.InitializeArchetypes();
        return dbe;
    }

    private static ArchetypeClusterState GetClusterState(DatabaseEngine dbe)
    {
        var meta = Archetype<ClCohUnit>.Metadata;
        return dbe._archetypeStates[meta.ArchetypeId].ClusterState;
    }

    /// <summary>
    /// Drift a population inside one cell for a number of ticks and report the escape rate. <paramref name="spread"/> controls cluster tightness: a small
    /// spread keeps every cluster's box small, which is the world <c>C5</c> bets on; a spread near the cell size is the degenerate case the design says
    /// placement decays into without maintenance.
    /// </summary>
    private (long changes, long escapes) MeasureEscapeRate(int entityCount, int ticks, float spread, float stepSize, int seed)
    {
        // A fresh scope per measurement: the fixture-level provider hands back one DatabaseEngine, and a second ConfigureSpatialGrid on an already-initialised
        // engine is refused. Two configurations in one test therefore need two scopes.
        using var scope = ServiceProvider.CreateScope();
        using var dbe = SetupEngine(scope);
        var rng = new Random(seed);

        // All inside cell (0,0) so no migration fires — this measures AABB movement, not cell churn.
        var ids = new List<EntityId>(entityCount);
        var xs = new float[entityCount];
        var ys = new float[entityCount];
        using (var tx = dbe.CreateQuickTransaction())
        {
            for (int i = 0; i < entityCount; i++)
            {
                xs[i] = 5f + ((float)rng.NextDouble() * spread);
                ys[i] = 5f + ((float)rng.NextDouble() * spread);
                ids.Add(tx.Spawn<ClCohUnit>(ClCohUnit.Pos.Set(PointAt(xs[i], ys[i]))));
            }
            tx.Commit();
        }

        dbe.WriteTickFence(1);

        var cs = GetClusterState(dbe);
        cs.ResetEscapeRateCounters();

        for (int tick = 0; tick < ticks; tick++)
        {
            using (var tx = dbe.CreateQuickTransaction())
            {
                for (int i = 0; i < entityCount; i++)
                {
                    // 30 % of the population moves per tick, matching Q2's stated shape.
                    if (rng.NextDouble() >= 0.30) { continue; }

                    xs[i] = Math.Clamp(xs[i] + (((float)rng.NextDouble() - 0.5f) * stepSize), 1f, CellSize - 1f);
                    ys[i] = Math.Clamp(ys[i] + (((float)rng.NextDouble() - 0.5f) * stepSize), 1f, CellSize - 1f);

                    var eref = tx.OpenMut(ids[i]);
                    ref var pos = ref eref.Write(ClCohUnit.Pos);
                    pos.Bounds = new AABB2F { MinX = xs[i], MinY = ys[i], MaxX = xs[i], MaxY = ys[i] };
                }
                tx.Commit();
            }

            dbe.WriteTickFence(tick + 2);
        }

        return (cs.AabbChangeCount, cs.AabbEscapeCount);
    }

    [Test]
    [CancelAfter(30_000)]
    public void Q2_EscapeRate_TightAndLooseClusters()
    {
        // Two populations, same motion. "Tight" is the regime C5 assumes and §5's migration work is meant to produce; "loose" is what placement decays to.
        var tight = MeasureEscapeRate(entityCount: 128, ticks: 24, spread: 12f, stepSize: 2f, seed: 11);
        var loose = MeasureEscapeRate(entityCount: 128, ticks: 24, spread: 90f, stepSize: 2f, seed: 11);

        double tightRate = tight.changes == 0 ? 0 : 100.0 * tight.escapes / tight.changes;
        double looseRate = loose.changes == 0 ? 0 : 100.0 * loose.escapes / loose.changes;

        TestContext.Out.WriteLine($"Q2ESCAPE tight  changes={tight.changes,6} escapes={tight.escapes,6} rate={tightRate,6:F2}%  (spread 12 of {CellSize})");
        TestContext.Out.WriteLine($"Q2ESCAPE loose  changes={loose.changes,6} escapes={loose.escapes,6} rate={looseRate,6:F2}%  (spread 90 of {CellSize})");
        TestContext.Out.WriteLine($"Q2ESCAPE note   upper bound on escapes — leaf MBR is >= any single entry's box, so in-place can only be commoner");

        Assert.Multiple(() =>
        {
            // The instrument, not the number: a counter that never moves reports 0 % just as convincingly as a perfect tree would.
            Assert.That(tight.changes, Is.GreaterThan(0), "the tight workload must actually produce AABB changes, or the rate is vacuous");
            Assert.That(loose.changes, Is.GreaterThan(0), "the loose workload must actually produce AABB changes, or the rate is vacuous");
            Assert.That(tight.escapes, Is.LessThanOrEqualTo(tight.changes));
            Assert.That(loose.escapes, Is.LessThanOrEqualTo(loose.changes));
        });
    }

    /// <summary>
    /// A population that does not move must produce no AABB changes at all — the control that proves the counters follow motion rather than ticks.
    /// </summary>
    [Test]
    [CancelAfter(30_000)]
    public void Q2_StationaryPopulation_ProducesNoChanges()
    {
        var still = MeasureEscapeRate(entityCount: 64, ticks: 8, spread: 40f, stepSize: 0f, seed: 7);

        TestContext.Out.WriteLine($"Q2ESCAPE still  changes={still.changes} escapes={still.escapes}");
        Assert.That(still.changes, Is.Zero, "a stationary population recomputes to the identical box, which the pass skips before it reaches the counter");
    }
}
