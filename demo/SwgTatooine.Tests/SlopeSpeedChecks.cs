using System;
using System.Collections.Generic;
using NUnit.Framework;
using SwgTatooine.World.Terrain;
using Typhon.Engine;

namespace SwgTatooine.Tests;

/// <summary>
/// Terrain rung (c): ground that rises costs something to cross.
/// </summary>
/// <remarks>
/// <para>
/// Rung (a) gave the planet relief and rung (b) made the server agree with the client about where the ground is. This is
/// the first rung where the terrain <b>does</b> something rather than being drawn.
/// </para>
/// <para>
/// <b>What each case here is for.</b> The curve is a pure function and is pinned as one. The mover's use of it is pinned
/// by staging a step against the real field and checking the arithmetic exactly — that is the case that fails if the
/// second ground sample is dropped or taken at the wrong point. The last case is the only one that observes the running
/// world, and it exists to catch a mover that never calls any of this; it is deliberately weak about magnitudes, because a
/// run executes a few more ticks than it measures and a case that depended on the count would be measuring the scheduler.
/// </para>
/// <para>
/// Determinism under a seed is <b>not</b> re-proved here.
/// <c>S0Checks.SameSeed_ReproducesTheRun_AndADifferentSeedDoesNot</c> fingerprints a creature population across two seeds
/// and already runs every mover this changed. The first draft of this fixture had its own copy, and it compared two
/// UNPACED runs that had executed 29 and 23 ticks for the same 20-tick request — a case that could only ever have
/// measured the scheduler.
/// </para>
/// </remarks>
[TestFixture]
[NonParallelizable]
public sealed class SlopeSpeedChecks
{
    private string _dir;

    [SetUp]
    public void SetUp() => _dir = Worlds.NewDirectory();

    /// <summary>C1 and C4: the curve falls where it should, never below its floor, and never rewards a descent.</summary>
    [Test]
    public void TheCurveSlowsAClimbAndLeavesEverythingElseAlone()
    {
        Assert.Multiple(() =>
        {
            // Flat and downhill are full speed. A speed-up on descent is what sends creatures off the edges of mesas.
            Assert.That(SlopeSpeed.FactorFor(0f), Is.EqualTo(1f));
            Assert.That(SlopeSpeed.FactorFor(-0.5f), Is.EqualTo(1f));
            Assert.That(SlopeSpeed.FactorFor(-5f), Is.EqualTo(1f), "a cliff going down is still not a shortcut");

            // A gentle rise costs nothing, up to the stated grade.
            Assert.That(SlopeSpeed.FactorFor(SlopeSpeed.FlatGrade), Is.EqualTo(1f));
            Assert.That(SlopeSpeed.FactorFor(SlopeSpeed.FlatGrade - 0.01f), Is.EqualTo(1f));

            // Then it falls, monotonically, to the floor — and stops there however steep the ground gets.
            Assert.That(SlopeSpeed.FactorFor(SlopeSpeed.SteepGrade), Is.EqualTo(SlopeSpeed.MinFactor));
            Assert.That(SlopeSpeed.FactorFor(40f), Is.EqualTo(SlopeSpeed.MinFactor), "a vertical face is not slower than steep");
            Assert.That(SlopeSpeed.FactorFor(float.PositiveInfinity), Is.EqualTo(SlopeSpeed.MinFactor));

            // The floor is the point: an entity whose speed can reach zero never arrives, so its wander leg never ends
            // and it sits against the hill until something else moves it.
            Assert.That(SlopeSpeed.MinFactor, Is.GreaterThan(0.1f));
        });

        // Monotone and bounded across the whole range, which is what "a curve" has to mean.
        float previous = 1f;
        for (float grade = -1f; grade <= 3f; grade += 0.01f)
        {
            float factor = SlopeSpeed.FactorFor(grade);
            Assert.That(factor, Is.InRange(SlopeSpeed.MinFactor, 1f), $"grade {grade:F2}");
            Assert.That(factor, Is.LessThanOrEqualTo(previous + 1e-6f), $"grade {grade:F2} is faster than the grade below it");
            previous = factor;
        }
    }

    /// <summary>The curve's two thresholds are set against the terrain, not guessed.</summary>
    /// <remarks>
    /// Measured over a tick's step on the shipping planet, the uphill grades run p50 0.069, p90 0.176, p99 0.315, with a
    /// tail to 1.12. The first draft used 0.2 and 0.6, which would have engaged the top seven per cent of climbs and put
    /// the floor beyond almost everything — a feature that exists and does not happen. If the layer tree is retuned again,
    /// this is the assertion to re-derive rather than relax.
    /// </remarks>
    [Test]
    public void TheCurveEngagesWhereThisPlanetActuallyHasSlopes()
    {
        Assert.Multiple(() =>
        {
            Assert.That(SlopeSpeed.FlatGrade, Is.LessThan(0.176f), "the threshold must sit below the 90th-percentile climb, or it engages on nothing");
            Assert.That(SlopeSpeed.SteepGrade, Is.GreaterThan(0.315f), "the floor must sit past the 99th percentile, or ordinary ground is treated as a cliff");
            Assert.That(SlopeSpeed.SteepGrade, Is.LessThan(1.12f), "and inside the tail, or the floor is unreachable");
        });
    }

    /// <summary>A NaN grade must not reach a placement. The factor falls back to 1 rather than propagating.</summary>
    /// <remarks>
    /// Unreachable from a finite step over a finite field, and asserted anyway: a NaN factor multiplies the step to NaN,
    /// the position codec carries it, and the entity is then nowhere — a failure that looks like a spatial-index bug three
    /// layers away from its cause.
    /// </remarks>
    [Test]
    public void ANaNGradeIsNotASlowdown()
    {
        Assert.Multiple(() =>
        {
            Assert.That(SlopeSpeed.FactorFor(float.NaN), Is.EqualTo(1f));
            Assert.That(SlopeSpeed.FactorForStep(float.NaN, 0f, 10f), Is.EqualTo(1f));
            Assert.That(SlopeSpeed.FactorForStep(0f, float.NaN, 10f), Is.EqualTo(1f));
        });
    }

    /// <summary>A step with no length has no grade, whatever the two heights say.</summary>
    [Test]
    public void AStepTooShortToHaveADirectionIsNotSlowed()
    {
        Assert.Multiple(() =>
        {
            Assert.That(SlopeSpeed.FactorForStep(0f, 50f, 0f), Is.EqualTo(1f));
            Assert.That(SlopeSpeed.FactorForStep(0f, 50f, 0.0001f), Is.EqualTo(1f));

            // And a real step over the same rise IS slowed, or the guard above would be hiding the feature.
            Assert.That(SlopeSpeed.FactorForStep(0f, 50f, 10f), Is.EqualTo(SlopeSpeed.MinFactor));
        });
    }

    /// <summary>C1 and C3, exactly: the mover shortens a climbing step and re-samples the ground where it now ends.</summary>
    /// <remarks>
    /// <para>
    /// Staged against the real field rather than waiting for a creature to wander onto a slope: over one tick on this
    /// planet only a couple of the five hundred creatures are on anything steep, so a case that waited for one would be a
    /// case about the wander seed.
    /// </para>
    /// <para>
    /// The second assertion is the one that carries the design. The step moves, so the ground under its new end is not the
    /// ground the caller sampled, and rung (b)'s invariant is that the stored <c>Y</c> IS the ground at the stored
    /// position. Leaving the caller's sample in place would satisfy every other case in this fixture and put every slowed
    /// entity slightly off the ground.
    /// </para>
    /// </remarks>
    [Test]
    public void TheMoverShortensAClimbingStepAndResamplesTheGroundUnderIt()
    {
        SimConfig config = Worlds.Small(_dir);
        using TatooineSim sim = new(config);
        sim.Initialize();
        SimBridge bridge = new(config, sim.Map, sim.Index);
        TerrainField terrain = TerrainField.Shared(config.ContentScale);
        float half = config.WorldEdgeM * 0.5f;

        (float x, float z, float dx, float dz) = SteepestStepOnThePlanet(terrain, half);
        var from = default(CreaturePlacement);
        from.SetAt(x, z, terrain.GroundAt(x, z), 1.5f);

        float endX = x + dx;
        float endZ = z + dz;
        float groundY = terrain.GroundAt(endX, endZ);
        float run = MathF.Sqrt((dx * dx) + (dz * dz));
        float expected = SlopeSpeed.FactorForStep(from.Y, groundY, run);
        Assert.That(expected, Is.LessThan(1f), "the premise: this step climbs steeply enough to be slowed");

        bridge.Slow(RealmId.Default, in from, ref endX, ref endZ, ref groundY, from.HalfExtent, half);

        float shortened = MathF.Sqrt(((endX - x) * (endX - x)) + ((endZ - z) * (endZ - z)));
        float groundThere = terrain.GroundAt(endX, endZ);
        Assert.Multiple(() =>
        {
            Assert.That(shortened, Is.EqualTo(run * expected).Within(0.001f), "the step is scaled by exactly the curve");
            Assert.That(groundY, Is.EqualTo(groundThere).Within(0.0001f),
                "the ground is re-sampled where the step NOW ends, or every slowed entity floats or sinks");
        });
    }

    /// <summary>C7: a realm with no terrain cannot slow anything, and costs no second sample.</summary>
    [Test]
    public void AStepInAFlatRealmIsLeftExactlyAlone()
    {
        SimConfig config = Worlds.Small(_dir);
        using TatooineSim sim = new(config);
        sim.Initialize();
        SimBridge bridge = new(config, sim.Map, sim.Index);

        // An interior realm: its floor is flat by construction, so both ends of the step read 0 and no grade exists.
        var from = default(NpcPlacement);
        from.SetAt(30f, 30f, 0f, 0.5f);
        float x = 32f;
        float z = 31f;
        float groundY = 0f;

        bridge.Slow(new RealmId((ushort)config.Planets), in from, ref x, ref z, ref groundY, from.HalfExtent, config.WorldEdgeM * 0.5f);

        Assert.Multiple(() =>
        {
            Assert.That(x, Is.EqualTo(32f), "a flat realm cannot slow anything");
            Assert.That(z, Is.EqualTo(31f));
            Assert.That(groundY, Is.EqualTo(0f));
        });
    }

    /// <summary>C2: the running world slows somebody — a mover that never calls the curve is caught here and nowhere else.</summary>
    /// <remarks>
    /// <para>
    /// Deliberately weak about magnitude. A run executes a few more ticks than it measures, so the displacement between
    /// two samples is some unknown whole number of steps, and a case that asserted a ratio would be asserting the tick
    /// count. What it does assert is the shape: an unimpeded creature covers its velocity every tick, so the largest ratio
    /// of displacement to velocity across the population IS that tick count, and a creature well below it was slowed,
    /// turned, or arrived.
    /// </para>
    /// <para>
    /// The grade is what separates "slowed" from "turned or arrived": the short movers are required to be on steeper
    /// ground than the population, which is the correlation the feature exists to create.
    /// </para>
    /// </remarks>
    [Test]
    public void TheRunningWorldSlowsCreaturesOnSteeperGroundThanAverage()
    {
        // A bigger population than the other cases use, on purpose: the assertion is a ratio of two medians, and at the
        // small world's ~500 creatures the steep bucket holds fifteen-odd samples and the ratio swings between 0.79 and
        // 0.91 run to run. Four times the population quadruples the bucket and the swing closes.
        SimConfig config = Worlds.Small(_dir);
        config.PopulationScale = 4;
        config.WarmTicks = 0;
        config.MeasuredTicks = 8;
        config.Unpaced = true;
        using TatooineSim sim = new(config);
        sim.Initialize();

        List<Sample> before = Read(sim);
        sim.Run();
        List<Sample> after = Read(sim);
        Assert.That(before, Has.Count.EqualTo(after.Count));

        List<double> steep = [];
        List<double> flat = [];
        for (int i = 0; i < before.Count; i++)
        {
            double dx = after[i].X - before[i].X;
            double dz = after[i].Z - before[i].Z;
            double run = Math.Sqrt((dx * dx) + (dz * dz));
            if (run < 0.5)
            {
                continue;   // resting, ambient, or arrived on the first tick
            }

            double grade = Math.Abs(after[i].Y - before[i].Y) / run;
            if (grade > 0.3)
            {
                steep.Add(run);
            }
            else if (grade < 0.05)
            {
                flat.Add(run);
            }
        }

        Assert.That(flat, Has.Count.GreaterThan(20), "the premise: a population crossed flat ground");
        Assert.That(steep, Has.Count.GreaterThan(4), "the premise: some of it crossed ground steep enough to be slowed");

        steep.Sort();
        flat.Sort();
        double steepMedian = steep[steep.Count / 2];
        double flatMedian = flat[flat.Count / 2];
        TestContext.Out.WriteLine($"median displacement: {steepMedian:F2} m over {steep.Count} steep movers, {flatMedian:F2} m over {flat.Count} flat");
        // A RATIO, and a measured one. A bare `steepMedian < flatMedian` passed on code that did nothing at all: with the
        // creature mover's call to the curve deleted, the two medians came out at 3.35 m and 3.36 m, which is still
        // "less". The gate is set from both arms rather than from taste — at this population the ratio is 1.08 / 1.50 =
        // 0.72 as written and 1.36 / 1.50 = 0.907 with the call site removed, each stable to three digits across repeated
        // runs, so 0.82 sits between them with room on both sides.
        Assert.That(steepMedian, Is.LessThan(flatMedian * 0.82),
            $"creatures crossing steeper ground must cover meaningfully less of it; ratio {steepMedian / flatMedian:F3}");
    }

    /// <summary>The steepest uphill step of a fixed length found by a coarse scan of the planet.</summary>
    /// <remarks>
    /// A scan rather than a hand-picked coordinate: a retune of the layer tree moves every slope on the planet, and a case
    /// anchored to a spot that used to be a cliff would start passing for the wrong reason. Coarse on purpose — it has to
    /// find ground steep enough to be slowed, not the true maximum.
    /// </remarks>
    private static (float X, float Z, float DX, float DZ) SteepestStepOnThePlanet(TerrainField terrain, float half)
    {
        const float step = 3f;
        float bestRise = 0f;
        (float X, float Z, float DX, float DZ) best = (0f, 0f, step, 0f);
        for (float z = -half + 64; z < half - 64; z += 97f)
        {
            for (float x = -half + 64; x < half - 64; x += 97f)
            {
                float here = terrain.GroundAt(x, z);
                foreach ((float dx, float dz) in new[] { (step, 0f), (-step, 0f), (0f, step), (0f, -step) })
                {
                    float rise = terrain.GroundAt(x + dx, z + dz) - here;
                    if (rise > bestRise)
                    {
                        bestRise = rise;
                        best = (x, z, dx, dz);
                    }
                }
            }
        }

        Assert.That(bestRise / step, Is.GreaterThan(SlopeSpeed.SteepGrade), "the planet must have somewhere steep, or rung (c) has nothing to act on");
        return best;
    }

    private readonly record struct Sample(float X, float Z, float Y);

    /// <summary>Every creature on the planet, as its placement.</summary>
    private static List<Sample> Read(TatooineSim sim)
    {
        List<Sample> into = [];
        using var tx = sim.Dbe.CreateQuickTransaction();
        Span<ClusterSpatialQueryResult> buffer = new ClusterSpatialQueryResult[256];
        var e = sim.Dbe.ClusterSpatialQuery<Creature>(RealmId.Default).AABB(in Worlds.Everywhere);
        try
        {
            int got;
            while ((got = e.Fill(buffer)) > 0)
            {
                for (int i = 0; i < got; i++)
                {
                    CreaturePlacement p = tx.Open(buffer[i].Entity).Read(Creature.Bounds);
                    into.Add(new Sample(p.X, p.Z, p.Y));
                }
            }
        }
        finally
        {
            e.Dispose();
        }

        return into;
    }
}
