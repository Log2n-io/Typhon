using System;
using System.Collections.Generic;
using NUnit.Framework;
using SwgTatooine.World.Terrain;
using Typhon.Engine;

namespace SwgTatooine.Tests;

/// <summary>
/// Every entity on the planet stands on the ground, and the server is the one that knows where that is.
/// </summary>
/// <remarks>
/// <para>
/// Terrain rung (a) gave the browser relief and left the server believing everything sat at altitude 0. The client hid
/// that by treating the server's <c>y</c> as an offset and adding its own ground underneath, which looks right and is
/// right the way a stopped clock is: the server had no opinion, so it could not be contradicted. Rung (b) gives it one.
/// </para>
/// <para>
/// <b>Altitude is a property of the realm, not of the coordinate</b>, which is the half of this that is easy to get
/// wrong. An interior is its own realm with a flat floor a few metres across, and its local (x, z) collides with a point
/// on the planet that may have 200 m of mesa under it.
/// </para>
/// </remarks>
[TestFixture]
[NonParallelizable]
public sealed class TerrainAltitudeChecks
{
    private string _dir;

    [SetUp]
    public void SetUp() => _dir = Worlds.NewDirectory();

    [Test]
    public void EveryPlanetPlacementStandsOnTheGround()
    {
        // The AC: no placement reads 0 unless the ground there really is 0. Asserted against the field itself rather
        // than against a tolerance band, because the world builder and this test ask the same function the same question
        // — so the only thing that can separate them is a site that forgot to ask.
        SimConfig config = Worlds.Small(_dir);
        using TatooineSim sim = new(config);
        sim.Initialize();

        TerrainField terrain = TerrainField.Shared(config.ContentScale);
        List<(string What, float X, float Z, float Y)> placed = [];
        Read<WorldObject, StructurePlacement>(sim, "structure", WorldObject.Bounds, placed);
        Read<CreatureLair, LairPlacement>(sim, "lair", CreatureLair.Bounds, placed);
        Read<CityNpc, NpcPlacement>(sim, "city npc", CityNpc.Bounds, placed);
        Read<Creature, CreaturePlacement>(sim, "creature", Creature.Bounds, placed);
        Read<Player, PlayerPlacement>(sim, "player", Player.Bounds, placed);

        int offGround = 0;
        float worst = 0;
        string worstWhat = string.Empty;
        foreach ((string what, float x, float z, float y) in placed)
        {
            float d = Math.Abs(y - terrain.GroundAt(x, z));
            if (d > worst)
            {
                worst = d;
                worstWhat = what;
            }

            if (d > 0.001f)
            {
                offGround++;
            }
        }

        Assert.That(placed.Count, Is.GreaterThan(1000), "a world this size should have placed thousands of things");
        Assert.That(offGround, Is.Zero, $"{offGround} of {placed.Count} placements are off the ground, worst a {worstWhat} by {worst:F2} m");
    }

    [Test]
    public void AnAltitudeIsResampled_NeverIntegrated()
    {
        // A creature walked across relief must carry the ground's altitude at every step, not a smoothed or accumulated
        // one. The distinction matters because an integrated Y drifts: it would look right for a hundred ticks and be
        // metres wrong after a thousand, which no screenshot catches.
        SimConfig config = Worlds.Small(_dir);
        config.WarmTicks = 0;
        config.MeasuredTicks = 40;
        config.Unpaced = true;
        using TatooineSim sim = new(config);
        sim.Initialize();
        sim.Run();

        TerrainField terrain = TerrainField.Shared(config.ContentScale);
        List<(string What, float X, float Z, float Y)> after = [];
        Read<Creature, CreaturePlacement>(sim, "creature", Creature.Bounds, after);
        Read<Player, PlayerPlacement>(sim, "player", Player.Bounds, after);

        int wrong = 0;
        float worst = 0;
        foreach ((_, float x, float z, float y) in after)
        {
            float d = Math.Abs(y - terrain.GroundAt(x, z));
            if (d > worst)
            {
                worst = d;
            }

            if (d > 0.001f)
            {
                wrong++;
            }
        }

        Assert.That(after.Count, Is.GreaterThan(100));
        Assert.That(wrong, Is.Zero, $"{wrong} of {after.Count} movers carry an altitude that is not the ground under them, worst by {worst:F3} m");
    }

    [Test]
    public void TheGroundIsNotFlat_OrNothingAboveProvesAnything()
    {
        // Every assertion in this fixture compares a stored altitude against the field. If the field were flat they
        // would all pass against a server that still wrote zeros, so the premise is asserted rather than assumed.
        TerrainField terrain = TerrainField.Shared(1.0);
        Assert.That(terrain.MaxHeightM - terrain.MinHeightM, Is.GreaterThan(300f), "relief");
        Assert.That(Math.Abs(terrain.GroundAt(0f, 0f)), Is.GreaterThan(1f), "the planet's origin is not at sea level");
    }

    [Test]
    public void TheFieldFollowsContentScale_AndIsBakedOnlyOnce()
    {
        // `ContentScale` widens the whole map, so the terrain stretches with it rather than staying a 16 km island in the
        // middle of a 32 km world. At scale 1 — the only scale the browser client knows — it samples the authored field.
        //
        // The sample loop below CANNOT FAIL on its own, and that is worth saying rather than leaving for the next reader
        // to work out: `two.GroundAt(2x)` is `HeightAt(2x / 2)` by construction, so it equals `one.GroundAt(x)` whatever
        // the second field holds — or whether there is a second field at all. It asserts a division.
        //
        // The assertion that carries the claim is the identity one. Content scale never reaches `BandBake.Bake`, which
        // reads only the seed, so a field per scale was two byte-identical 64 MB arrays and two 320 ms bakes — three of
        // each across a sweep, whose `SweepWorlds` has three entries.
        TerrainField one = TerrainField.Shared(1.0);
        TerrainField two = TerrainField.Shared(2.0);
        Assert.That(two.Field, Is.SameAs(one.Field), "one bake, one array: the scale belongs on the wrapper, not on the field");

        foreach ((float x, float z) in new[] { (1530f, 3175f), (-2890f, 2198f), (0f, 0f), (4000f, -4000f) })
        {
            Assert.That(two.GroundAt(x * 2, z * 2), Is.EqualTo(one.GroundAt(x, z)), $"scaled sample at ({x}, {z})");
        }

        // And the scale is not a no-op: at the SAME coordinate the two disagree, or `GroundAt` would be ignoring it.
        Assert.That(two.GroundAt(1530f, 3175f), Is.Not.EqualTo(one.GroundAt(1530f, 3175f)));
    }

    [Test]
    public void TheWireRangeCoversTheFieldWithMargin_AndItsStepIsInvisible()
    {
        // B5's two numbers. The codec is 16 bits over [Ground.MinM, Ground.MaxM] and carries the altitude OUTSIDE the
        // position codec, so a walk up a cliff is not measured as a horizontal teleport.
        TerrainField terrain = TerrainField.Shared(1.0);
        Assert.That((double)terrain.MinHeightM, Is.GreaterThan(Ground.MinM), "the planet's floor must sit inside the wire range");
        Assert.That((double)terrain.MaxHeightM, Is.LessThan(Ground.MaxM), "the planet's ceiling must sit inside the wire range");

        // Margin, so a retune of the layer tree does not silently start clamping. 25 m at each end is about 7 % of the
        // relief and far more than any single edit has moved it.
        Assert.That(terrain.MinHeightM - Ground.MinM, Is.GreaterThan(25d), "floor margin");
        Assert.That(Ground.MaxM - terrain.MaxHeightM, Is.GreaterThan(25d), "ceiling margin");

        double step = (Ground.MaxM - Ground.MinM) / ((1 << 16) - 1);
        Assert.That(step, Is.LessThan(0.01d), $"a {step * 1000:F1} mm step is what the client will see the ground move by");
    }

    [Test]
    public void NothingIsPlacedOutsideTheWorld_AndAClampSaysSo()
    {
        // #1073. Several spawn regions reach past the planet on their own geometry — `Southern Wastes` is centred at
        // z = −6 800 with a 2 300 m radius against a half-extent of 8 192 — and the lair path did not clamp, so the
        // engine clamped the write instead and said nothing. The result was a knot of creatures pressed against the
        // boundary that the cell-size sweep then measured as real geography, and a fixture that failed four runs in
        // thirty because a player it had placed two metres from a creature was a hundred metres away by the first tick.
        SimConfig config = Worlds.Small(_dir);
        WorldBuilder.ResetClampedPlacements();
        using TatooineSim sim = new(config);
        sim.Initialize();

        List<(string What, float X, float Z, float Y)> placed = [];
        Read<CreatureLair, LairPlacement>(sim, "lair", CreatureLair.Bounds, placed);
        Read<Creature, CreaturePlacement>(sim, "creature", Creature.Bounds, placed);
        Read<WorldObject, StructurePlacement>(sim, "structure", WorldObject.Bounds, placed);
        // Players and NPCs were missing, and both have a path that reached the engine unbounded: a player-city structure
        // site draws a centre at 0.97 of the half-extent and then a radius of up to 450 m, and a roaming player is placed
        // within SIX city radii of its city. The shipped map left the second 688 m of margin, which is not a guarantee.
        Read<Player, PlayerPlacement>(sim, "player", Player.Bounds, placed);
        Read<CityNpc, NpcPlacement>(sim, "city npc", CityNpc.Bounds, placed);

        float half = config.WorldEdgeM * 0.5f;
        int outside = 0;
        float worst = 0;
        foreach ((string what, float x, float z, float _) in placed)
        {
            float over = Math.Max(Math.Abs(x), Math.Abs(z)) - half;
            if (over > 0)
            {
                outside++;
                worst = Math.Max(worst, over);
                TestContext.Out.WriteLine($"{what} at ({x:F1}, {z:F1}) is {over:F1} m outside a {half:F0} m half-extent");
            }
        }

        Assert.That(placed.Count, Is.GreaterThan(1000));
        Assert.That(outside, Is.Zero, $"{outside} placements are outside the world, worst by {worst:F1} m");

        // Read back, every position is inside — but the engine CLAMPS a write outside its grid and says nothing, so a
        // reader alone cannot tell a placement that was always inside from one the engine dragged in. `ClampedPlacements`
        // is the builder's own count, taken before the write, and it is the only evidence that survives.
        //
        // Non-zero is expected and correct: several authored spawn regions genuinely overhang the planet, which the
        // deterministic sibling test below asserts outright. What must hold is that it is BOUNDED — a builder that had
        // started clamping everything would still leave every position inside, and would look exactly like this one.
        Assert.That(WorldBuilder.ClampedPlacements, Is.LessThan(placed.Count / 10),
            $"{WorldBuilder.ClampedPlacements} of {placed.Count} placements had to be moved: a region's geometry is wrong, not just overhanging");
    }

    [Test]
    public void TheAuthoredSpawnRegionsOverhangThePlanet_AndTheClampBringsThemBack()
    {
        // The deterministic half of #1073, and the one that carries the claim.
        //
        // The world-level net above samples a 5 % population, so whether any single draw lands outside is luck — it
        // passed with the clamp removed, which is exactly the kind of test that looks like evidence and is not. This
        // one asks the geometry instead: which authored regions reach past the planet, and does the clamp bring a point
        // on their far edge back inside.
        SimConfig config = Worlds.Small(_dir);
        float half = config.WorldEdgeM * 0.5f;

        List<string> overhanging = [];
        foreach (SpawnRegionDef region in TatooineData.SpawnRegions)
        {
            float reach = Math.Max(
                Math.Abs(region.X) + region.Radius,
                Math.Abs(region.Z) + region.Radius);
            if (reach > half)
            {
                overhanging.Add($"{region.Name} reaches {reach - half:F0} m past the edge");
            }
        }

        Assert.That(overhanging, Is.Not.Empty, "the premise: at least one authored region does not fit the planet");
        TestContext.Out.WriteLine(string.Join(Environment.NewLine, overhanging));

        // Every corner of every region's disc, clamped, lands inside — and the clamp counts itself when it moves one.
        WorldBuilder.ResetClampedPlacements();
        int moved = 0;
        foreach (SpawnRegionDef region in TatooineData.SpawnRegions)
        {
            foreach ((float dx, float dz) in new[] { (1f, 0f), (-1f, 0f), (0f, 1f), (0f, -1f) })
            {
                float px = region.X + (dx * region.Radius);
                float pz = region.Z + (dz * region.Radius);
                (float cx, float cz) = WorldBuilder.Inside(px, pz, config);
                Assert.That(Math.Abs(cx), Is.LessThanOrEqualTo(half), $"{region.Name} x");
                Assert.That(Math.Abs(cz), Is.LessThanOrEqualTo(half), $"{region.Name} z");
                if (cx != px || cz != pz)
                {
                    moved++;
                }
            }
        }

        Assert.That(moved, Is.GreaterThan(0), "at least one edge point had to move");
        Assert.That(WorldBuilder.ClampedPlacements, Is.EqualTo(moved), "every move must be counted, or a bad region stays invisible");
    }

    /// <summary>Every entity of an archetype on the planet realm, as its position and the altitude stored beside it.</summary>
    private static void Read<TArch, TPlace>(TatooineSim sim, string what, Comp<TPlace> bounds,
        List<(string, float, float, float)> into)
        where TArch : Archetype<TArch>, new()
        where TPlace : unmanaged, IGroundPlacement
    {
        using var tx = sim.Dbe.CreateQuickTransaction();
        Span<ClusterSpatialQueryResult> buffer = new ClusterSpatialQueryResult[256];
        var e = sim.Dbe.ClusterSpatialQuery<TArch>(RealmId.Default).AABB(in Worlds.Everywhere);
        try
        {
            int got;
            while ((got = e.Fill(buffer)) > 0)
            {
                for (int i = 0; i < got; i++)
                {
                    TPlace p = tx.Open(buffer[i].Entity).Read(bounds);
                    into.Add((what, p.X, p.Z, p.Y));
                }
            }
        }
        finally
        {
            e.Dispose();
        }
    }
}
