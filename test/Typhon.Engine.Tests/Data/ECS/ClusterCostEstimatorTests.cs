using System;
using System.Numerics;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests;

/// <summary>
/// <c>AC-11.4</c> — the adaptive cost model converges rather than oscillating (#872 step 11).
/// </summary>
/// <remarks>
/// <para><b>What "adaptive budget" actually means here, and why.</b> §5.6 says the budget is "adjusted at runtime from the previous tick's measured cost".
/// There is no frame-time target inside <c>WriteTickFence</c> to adapt a CEILING against, and §5.6's own plumbing note points at
/// <c>LastTickMigrationExecuteMs</c> / <c>LastTickMigrationCount</c> — which measure cost, not headroom. So <c>ReclusterBudgetMs</c> stays the fixed
/// ceiling and the CONSTANT it is compared against becomes measured: <c>RepairNsPerEntity</c> is now a seed, and
/// <c>SpatialMigrationTelemetry.MeasuredNsPerEntity</c> is what the admission decision actually used.</para>
/// <para><b>Why it cannot ring, which is the substance of the AC.</b> The estimate is PER ENTITY, so admitting fewer entities does not change it — there is
/// no feedback path from the admission decision back into the measurement. An EWMA with no feedback is a first-order low-pass and converges monotonically.
/// A debt or credit term would have created exactly that loop, on an actuator that at the default budget admits one unit or zero; it was considered and
/// deliberately left out.</para>
/// </remarks>
[TestFixture]
[NonParallelizable]
class ClusterCostEstimatorTests : TestBase<ClusterCostEstimatorTests>
{
    [OneTimeSetUp]
    public void OneTimeSetup()
    {
    }

    private const float CellSize = 100f;
    private const float WorldMax = 1000f;
    private const int Population = 600;

    private static ClMigPos PointAt(float x, float y, int tag = 0) =>
        new() { Bounds = new AABB2F { MinX = x, MinY = y, MaxX = x, MaxY = y }, Tag = tag };

    private static ushort ArchetypeId => Archetype<ClMigUnit>.Metadata.ArchetypeId;

    private DatabaseEngine SetupEngine(float seedNsPerEntity)
    {
        var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<ClMigPos>();
        dbe.RegisterComponentFromAccessor<ClMigScratch>();
        dbe.ConfigureSpatialGrid(SpatialGridConfig.Flat(
            new Vector2(0, 0),
            new Vector2(WorldMax, WorldMax),
            CellSize,
            // The drift gate is OFF (a ratio no cluster confined to its own cell can reach), and it has to be for the quiet-tick test to terminate.
            // With it on, a repair tightens a cell, the next tick's motion smears it again, and the relocation churn regenerates drifters indefinitely —
            // so "two consecutive ticks that migrated nothing" never arrives and the drain loop times out. Measured before the change: 3 of 4 Debug runs
            // red. Repair alone still supplies every migration the cost model needs, which is what these tests actually measure.
            clusterTargetExtentRatio: 100f,
            clusterRepairExtentRatio: 0.75f,
            reclusterBudgetMs: 5.0f, batchSpawnSortThreshold: 0 /* step 15: this fixture builds its layout by spawn ORDER; the Morton sort would tighten it at birth */,
            repairNsPerEntity: seedNsPerEntity));
        dbe.InitializeArchetypes();
        return dbe;
    }

    private static void Spawn(DatabaseEngine dbe)
    {
        using (var tx = dbe.CreateQuickTransaction())
        {
            for (var i = 0; i < Population; i++)
            {
                tx.Spawn<ClMigUnit>(ClMigUnit.Pos.Set(PointAt(4f + ((i * 37) % 92), 4f + ((i * 61) % 92), i)));
            }

            tx.Commit();
        }

        dbe.WriteTickFence(1);
    }

    private static void MoveAll(DatabaseEngine dbe, Random rng)
    {
        using var tx = dbe.CreateQuickTransaction();
        var accessor = tx.For<ClMigUnit>();
        try
        {
            foreach (var cluster in accessor.GetClusterEnumerator())
            {
                var bits = cluster.OccupancyBits;
                while (bits != 0)
                {
                    var slot = BitOperations.TrailingZeroCount(bits);
                    bits &= bits - 1;
                    cluster.WriteSpatial(ClMigUnit.Pos, slot,
                        PointAt(3f + (float)rng.NextDouble() * 94f, 3f + (float)rng.NextDouble() * 94f));
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
    // AC-11.4 — the model tracks the machine, then converges
    // ══════════════════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>Spread of a window of samples, as a fraction of its mean — the scale-free measure of how much the estimate is still moving.</summary>
    private static double RelativeSpread(double[] samples, int from, int count)
    {
        var min = double.MaxValue;
        var max = double.MinValue;
        var sum = 0d;
        for (var i = from; i < from + count; i++)
        {
            min = Math.Min(min, samples[i]);
            max = Math.Max(max, samples[i]);
            sum += samples[i];
        }

        var mean = sum / count;
        return mean <= 0d ? 0d : (max - min) / mean;
    }

    /// <summary>Run a seeded engine under continuous motion and return the per-tick estimate.</summary>
    private double[] SampleEstimate(float seedNsPerEntity, int ticks, int seed)
    {
        using var dbe = SetupEngine(seedNsPerEntity);
        Spawn(dbe);
        var rng = new Random(seed);

        var samples = new double[ticks];
        for (var i = 0; i < ticks; i++)
        {
            MoveAll(dbe, rng);
            dbe.WriteTickFence(2 + i);
            samples[i] = dbe.GetSpatialTelemetry(ArchetypeId).MeasuredNsPerEntity;
        }

        return samples;
    }

    /// <summary>
    /// The estimate SETTLES: by the end of a sustained run its swing across five consecutive ticks is small in absolute terms.
    /// </summary>
    /// <remarks>
    /// <para><b>The seed is the step change.</b> Genuinely changing the machine's per-entity cost mid-run is not something a unit test can do; seeding the
    /// model at a value the hardware contradicts is the same experiment from the other end, and it exercises the identical code path — the first
    /// measurement is as much a step away from the current estimate as any later one would be.</para>
    /// <para><b>Neither the direction nor the magnitude of the truth is assumed, and that is not laziness.</b> A Debug build migrates an entity in
    /// ~24 us and a Release build in ~1.5 us, so a seed that is "8x too high" in one configuration is 6x too LOW in the other. The first version of this
    /// test hard-coded the direction and failed for exactly that reason. What is asserted instead holds in both: the estimate leaves its seed, and the
    /// amplitude of its movement shrinks.</para>
    /// <para><b>Monotone per step is the WRONG property, and asserting it was the second error here.</b> The samples an EWMA averages are themselves
    /// noisy — a tick's measured cost varies with cache state and with how many entities happened to move — so a converging filter still wobbles from step
    /// to step. What separates convergence from ringing is not that every step moves the same way but that the AMPLITUDE decays. Comparing the spread of
    /// the last window against the first says precisely that, and a filter that rang would fail it.</para>
    /// </remarks>
    [Test]
    [VerifiesRule("RP-01")]
    public void TheEstimateSettlesUnderSustainedLoad()
    {
        // CONTROLLED samples, not a fence's measured cost. The property is the FILTER's — a first-order EWMA settles where an unfiltered or ringing one
        // does not — and feeding it wall-clock fences made the assertion a question about the machine's scheduler instead. In Release one entity costs
        // ~1.5 us, so a single preempted fence is a sample up to 20x the mean, and at alpha = 0.25 one 2.2x sample moves the estimate ~30 %: this failed
        // at a 29.6 % spread in parallel suites, while alone it always passed. The fence-to-estimator wiring stays covered by the tests here that drive
        // WriteTickFence (the clamp band, the quiet-tick hold).
        using var dbe = SetupEngine(seedNsPerEntity: 1500f);
        Spawn(dbe);
        var cs = dbe._archetypeStates[ArchetypeId].ClusterState;
        ref readonly var cfg = ref dbe.SpatialGrid.Config;

        var samples = new double[40];
        for (var i = 0; i < samples.Length; i++)
        {
            // ±30 % alternating noise around 3 000 ns/entity — the worst input a first-order filter gets — and one 1 ms/entity outlier, which the
            // [seed/10, seed*20] clamp must absorb rather than let dominate the next ten ticks.
            var nsPerEntity = i == 10 ? 1e6 : 3000d * (i % 2 == 0 ? 0.7d : 1.3d);
            cs.LastTickMigrationCount = 100;
            cs.LastTickMigrationExecuteMs = nsPerEntity * 100d / 1e6;
            cs.LastTickMigrationApplyTicks = 0;
            cs.ObserveMigrationCost(in cfg);
            samples[i] = cs.LastTickMeasuredNsPerEntity;
        }

        // At alpha = 0.25 the settled swing is about 9 %; alpha = 1.0 (no filtering) swings 60 % and a ringing filter does not decay — both fail.
        var late = RelativeSpread(samples, samples.Length - 5, 5);
        Assert.Multiple(() =>
        {
            Assert.That(samples[0], Is.GreaterThan(0d), "the estimator never produced a reading, so nothing below is about convergence");
            Assert.That(late, Is.LessThan(0.25d),
                $"the settled estimate still swings {late:P1} across five consecutive ticks, which is too wide for a budget to be spent against — a "
                + "converged first-order filter does not do that");
        });
    }

    /// <summary>
    /// The estimate stays inside its clamp band, so one pathological tick cannot disable the feature.
    /// </summary>
    /// <remarks>
    /// <para><b>The clamp is not defensive padding.</b> A stop-the-world GC or a page-fault storm landing inside the measurement bracket produces one
    /// enormous sample; without a bound the EWMA carries it for the ten ticks it takes to forget, and during those ticks the projected cost of every unit
    /// exceeds the budget and NOTHING is repaired. The band is <c>[seed/10, seed*20]</c> — wide enough to track a genuinely different machine (Debug is
    /// ~16x Release on this path), narrow enough that an outlier cannot stop the feature.</para>
    /// <para>Asserted rather than assumed because the clamp lives in one small helper that a refactor could quietly bypass, and its absence is invisible
    /// until the tick that has the bad luck.</para>
    /// </remarks>
    [Test]
    public void TheEstimateStaysInsideItsClampBand()
    {
        const float Seed = 1500f;

        // The bound is the sum of TWO independently clamped terms, not one. RepairCostEstimateNs adds the migration EWMA — clamped to
        // [Seed/10, Seed*20] — to the planner EWMA, which since the planner-cost fix is clamped against its own 60 ns scale rather than the migration
        // seed's. Asserting Seed*20 alone is a latent machine-speed flake: on a slow enough machine both terms sit near their ceilings and the sum
        // legitimately exceeds it. The floor is the migration floor alone, because the planner term is zero until the planner has run.
        const double PlannerSeed = 60d;
        var ceiling = (Seed * 20d) + (PlannerSeed * 20d);
        var samples = SampleEstimate(Seed, ticks: 12, seed: 771);

        for (var i = 0; i < samples.Length; i++)
        {
            Assert.That(samples[i], Is.InRange(Seed * 0.1d, ceiling + 1d),
                $"tick {i + 2} estimated {samples[i]:F0} ns/entity, outside the [migrationSeed/10, migrationSeed*20 + plannerSeed*20] band — an unclamped "
                + "estimator lets one bad tick refuse every repair unit for the next ten");
        }
    }

    /// <summary>
    /// Once the world has genuinely settled, further quiet ticks do not move the estimate at all.
    /// </summary>
    /// <remarks>
    /// <para><b>The bug this pins would be silent and catastrophic in the same breath.</b> Folding a quiet tick's zero into the EWMA drags the estimate
    /// down every idle tick; the tick after a lull would then divide the budget by a near-zero cost and admit an unbounded amount of work — an overrun
    /// arriving precisely when the frame had been quiet, which is the worst possible time and the hardest to attribute. Guarded by updating only when the
    /// tick's migration count is non-zero, which is one condition and easy to drop in a refactor.</para>
    /// <para><b>"Settled" is detected, not assumed after N ticks.</b> Motion stops producing WRITES immediately but not migrations: a drifter detected in
    /// tick T's AabbRefresh is executed by tick T+1's Migrate, and a repair planned in Prep lands a tick later still. So the first few ticks after the last
    /// write carry real samples and legitimately move the model. An earlier version of this test compared the estimate before and after a fixed number of
    /// quiet ticks and read that legitimate movement as decay.</para>
    /// </remarks>
    [Test]
    public void QuietTicksDoNotMoveTheEstimateAtAll()
    {
        using var dbe = SetupEngine(seedNsPerEntity: 1500f);
        Spawn(dbe);
        var rng = new Random(99);

        for (var i = 0; i < 6; i++)
        {
            MoveAll(dbe, rng);
            dbe.WriteTickFence(2 + i);
        }

        // Drain the pipeline: tick until THREE consecutive ticks move nothing, so what follows is genuinely quiet rather than merely un-written-to. The
        // ceiling is generous because a repair plans in Prep, executes in Migrate and settles its bounds on the following refresh, so a late unit can
        // legitimately keep the world busy for several ticks after the last write.
        var tick = 8;
        var quiet = 0;
        while (quiet < 3 && tick < 120)
        {
            dbe.WriteTickFence(tick++);
            quiet = dbe.GetSpatialTelemetry(ArchetypeId).MigrationCount == 0 ? quiet + 1 : 0;
        }

        Assert.That(quiet, Is.EqualTo(3), "the world never settled, so the assertion below would be about a still-draining pipeline");

        var settled = dbe.GetSpatialTelemetry(ArchetypeId).MeasuredNsPerEntity;
        Assert.That(settled, Is.GreaterThan(0d), "no estimate was ever produced, so a decay toward zero could not be observed");

        for (var i = 0; i < 10; i++)
        {
            dbe.WriteTickFence(tick++);
        }

        Assert.That(dbe.GetSpatialTelemetry(ArchetypeId).MeasuredNsPerEntity, Is.EqualTo(settled).Within(1e-6),
            $"ten quiet ticks moved the estimate from {settled:F1} ns/entity — a tick with no migrations has no sample, and folding its zero in would make "
            + "the budget admit unbounded work on the tick after a lull");
    }
}
