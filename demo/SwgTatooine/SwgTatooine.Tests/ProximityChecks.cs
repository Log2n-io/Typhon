using System;
using System.Collections.Generic;
using System.Numerics;
using NUnit.Framework;
using SwgTatooine.Server;
using Typhon.Engine;
using Typhon.Schema.Definition;
using SwgTatooine.Navigation;

namespace SwgTatooine.Tests;

/// <summary>
/// The static neighbourhood and the collision it carries: a client's body is never inside a building, the disc keeps the
/// cost proportional to movement, and neither claim rests on a world-generation accident.
/// </summary>
/// <remarks>
/// <para>
/// <b>The geometry is asserted as arithmetic and the invariant is asserted against a ticked world.</b> Eject, slide, corners,
/// overlapping footprints and what a disc may promise are pure functions and are staged. The invariant a player meets is
/// checked against <i>every</i> structure, read from the entities rather than from the neighbourhood that produced the
/// answer — a disc that returned the wrong boxes would otherwise be graded by itself.
/// </para>
/// <para>
/// <b>Three of these exist because a review found the first version could not fail.</b> A run with one possessed body never
/// exercises a batch member past index 0, so a transposed offset passed everything; a body walked across empty desert
/// refreshed its disc four times and found <i>zero</i> boxes, so the overflow and ratio assertions graded an empty query;
/// and a disc written with a hardcoded realm passed, because only the pure helper was ever asked about realms.
/// </para>
/// </remarks>
[Category("Integration")]
[TestFixture]
[NonParallelizable]
public sealed class ProximityChecks
{
    /// <summary>
    /// A player's half-extent, as <c>WorldBuilder</c> builds one.
    /// </summary>
    /// <remarks>
    /// <b>The world's constant, because this fixture claims to be testing the body the world has.</b> It said one metre and
    /// argued for it against an earlier half-metre — and by then <c>WorldBuilder</c> had moved to
    /// <see cref="TatooineData.PlayerBodyHalfM"/>, so the fixture was exercising a body three times the real one while its
    /// own comment asserted otherwise. The geometry that argument was protecting is worth keeping and is pinned on its own
    /// terms in <see cref="Abutting"/>, where it is a property of the GEOMETRY rather than a claim about the player.
    /// </remarks>
    private const float Half = TatooineData.PlayerBodyHalfM;

    /// <summary>
    /// The half-extent at which two structures either side of the planner's 2 m street have their grown boxes abut exactly.
    /// </summary>
    /// <remarks>
    /// Kept because that abutment is the case that once broke the push-out, and it is a fact about the street and the
    /// growth rule rather than about any particular body: a body of this size is the one that meets it.
    /// </remarks>
    private const float Abutting = 1f;

    /// <summary>Wide enough that no geometry case here is clamped by the world edge.</summary>
    private const float WorldHalf = 10_000f;

    /// <summary>One 10 x 10 m box with its south-west corner at the origin — a stand-in for a building's footprint.</summary>
    private static readonly ProximityBox[] OneBox = [Box(0f, 0f, 10f, 10f)];

    /// <summary>A footprint, with the squared distance the query would have reported left at zero (only ordering reads it).</summary>
    private static ProximityBox Box(float minX, float minZ, float maxX, float maxZ) => new(minX, minZ, maxX, maxZ, 0f);

    /// <summary>Whether a body of half-extent <paramref name="half"/> at (x, z) is inside <paramref name="box"/>'s footprint.</summary>
    private static bool Touches(ProximityBox box, float x, float z, float half = Half)
        => x > box.MinX - half && x < box.MaxX + half && z > box.MinZ - half && z < box.MaxZ + half;

    [Test]
    public void ABodyOutsideEveryBox_IsLeftExactlyWhereItIs()
    {
        var x = -5f;
        var z = 5f;
        Assert.That(SimBridge.PushOutForTest(OneBox, Half, WorldHalf, ref x, ref z, out var stuck), Is.False, "nothing was touched, so nothing should have moved");
        Assert.Multiple(() =>
        {
            Assert.That(x, Is.EqualTo(-5f));
            Assert.That(z, Is.EqualTo(5f));
            Assert.That(stuck, Is.False);
        });
    }

    [Test]
    public void ABodyInsideABox_IsPushedOutOfIt()
    {
        // Well inside, nearest to the west face.
        var x = 1f;
        var z = 5f;
        Assert.That(SimBridge.PushOutForTest(OneBox, Half, WorldHalf, ref x, ref z, out var stuck), Is.True);
        Assert.Multiple(() =>
        {
            Assert.That(Touches(OneBox[0], x, z), Is.False, $"still inside the building at ({x}, {z})");
            Assert.That(x, Is.EqualTo(-Half).Within(1e-4f), "pushed out along the nearest face, which is the west one");
            Assert.That(z, Is.EqualTo(5f).Within(1e-4f), "pushed along an axis it was not penetrating");
            Assert.That(stuck, Is.False);
        });
    }

    /// <summary>
    /// An inside corner, where resolving one box can push a body into the next one.
    /// </summary>
    /// <remarks>
    /// The assertion is the invariant — out of <b>both</b> — rather than a particular resolved point, because which face a
    /// corner ejects through is an implementation detail and being inside a building is not.
    /// </remarks>
    [Test]
    public void ABodyInACorner_EndsUpOutsideBothBoxes()
    {
        ProximityBox[] corner = [Box(0f, 0f, 20f, 5f), Box(0f, 5f, 5f, 20f)];
        var x = 5.2f;
        var z = 5.2f;
        SimBridge.PushOutForTest(corner, Half, WorldHalf, ref x, ref z, out var stuck);
        Assert.Multiple(() =>
        {
            Assert.That(Touches(corner[0], x, z), Is.False, $"inside the first box at ({x}, {z})");
            Assert.That(Touches(corner[1], x, z), Is.False, $"inside the second box at ({x}, {z})");
            Assert.That(stuck, Is.False, "the corner did not settle within the pass budget");
        });
    }

    /// <summary>
    /// Two footprints whose GROWN boxes overlap: the push cannot satisfy both, and says so instead of pretending.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The case an earlier version declared impossible.</b> Its two-pass loop was justified by "the planner guarantees no
    /// two structures overlap", which is false twice: the boxes tested are grown by the body's own radius, so a 2 m street
    /// between two structures leaves a body 2 m across exactly no room; and <c>SitePlanner.Place</c> deliberately plants a
    /// structure overlapping its neighbour when a site has run out of room, counting it in <c>Crowded</c>. Overlapping grown
    /// boxes make the push oscillate — out of A into B, out of B into A — and two fixed passes ended with the body inside a
    /// wall and nothing recording it.
    /// </para>
    /// <para>
    /// There is no correct resolution here, so the claim is the honest one: the body is reported unresolved. A caller that
    /// sees a non-zero count knows its world is building geometry a body cannot stand in.
    /// </para>
    /// </remarks>
    [Test]
    public void ABodyBetweenTwoFootprintsTooCloseToStandBetween_IsReportedUnresolved()
    {
        // A 0.4 m gap, which the 0.6 m body cannot occupy: the grown boxes overlap by 0.2 m. The figures are the body's,
        // so they moved when the body did — an alley is only too narrow relative to what is walking down it.
        ProximityBox[] alley = [Box(0f, 0f, 10f, 10f), Box(10.4f, 0f, 20.4f, 10f)];
        var x = 10.2f;
        var z = 5f;
        Assert.That(SimBridge.PushOutForTest(alley, Half, WorldHalf, ref x, ref z, out var stuck), Is.True);
        Assert.That(stuck, Is.True, "an alley narrower than the body resolved cleanly, which cannot be true");
    }

    [Test]
    public void ADiscIsTheDistanceToTheNearestSurface()
    {
        // Twelve metres west of the box's west face, with the probe centred where the body is.
        var clear = SimBridge.DiscForForTest(OneBox, -12f, 5f, 50f, -12f, 5f);
        Assert.That(clear, Is.EqualTo(12f).Within(1e-3f));
    }

    /// <summary>
    /// A disc may never promise more than the leash, however empty the ground is.
    /// </summary>
    /// <remarks>
    /// Without the cap a body in open desert would hold one disc for the rest of the run and never notice a structure placed
    /// beside it — which is exactly what the economy's extractors will do.
    /// </remarks>
    [Test]
    public void ADiscWithNothingNearIt_IsCappedByTheLeash()
    {
        var clear = SimBridge.DiscForForTest([], 0f, 0f, 500f, 0f, 0f);
        Assert.That(clear, Is.EqualTo(SimBridge.ProximityLeashM));
    }

    /// <summary>
    /// A disc may never promise more than the probe actually looked at, and the body has already spent part of that budget.
    /// </summary>
    /// <remarks>
    /// This bound is the whole reason the probe does not need to reach a structure's centre: the engine's radius query
    /// reports a box whose surface enters the sphere wherever its centre is, and this cap is what makes
    /// <c>ClearM + |anchor − probe| ≤ probeR</c> true by construction for any probe radius.
    /// </remarks>
    [Test]
    public void ADiscNeverReachesPastWhatTheProbeSaw()
    {
        // A probe of 10 m taken at the origin, for a body that ended up 4 m away: only 6 m of it was ever examined.
        var clear = SimBridge.DiscForForTest([], 0f, 0f, 10f, 4f, 0f);
        Assert.That(clear, Is.EqualTo(6f).Within(1e-3f));
    }

    /// <summary>
    /// A body INSIDE a footprint gets the distance to the structure's walls, not zero.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This is what keeps an indoor body from paying a radius query every tick.</b> A footprint is the ground the
    /// world reserved; the asset standing on it is walls with a gap where its door is, and a body in the middle of a
    /// room is legitimately inside the one while metres from the others. Measured against the footprint its clearance
    /// is zero, the disc is spent on arrival, and the body queries again on the very next tick for as long as it stays
    /// indoors.
    /// </para>
    /// <para>
    /// The doorway below is the published houses' — two walls either side of a 1.2 m gap at z = 0 — so a body standing
    /// 3 m north of it is 2.8 m from the wall face and should be told so.
    /// </para>
    /// </remarks>
    [Test]
    public void ADiscInsideAFootprintIsMeasuredToTheWallsAndNotToTheFootprint()
    {
        var entity = EntityId.FromRawValue(7);
        AssetColliders.PlanBox[] doorway =
        [
            new(-4.6f, -0.2f, -0.6f, 0.2f, 0f, 2.5f),
            new(0.6f, -0.2f, 4.6f, 0.2f, 0f, 2.5f),
        ];

        // A 10 m footprint centred on the origin, which the body is standing in the middle of.
        ProximityBox[] house = [new(-5f, -5f, 5f, 5f, 0f, entity)];
        var shapes = StructureShapes.ForTest(0.4f, entity, StructureShapes.Shape.Primitives, doorway, 0f, 0f, 0);

        // Standing square in front of a wall, 3 m north of a face at z = 0.2.
        var facingWall = SimBridge.DiscFor(house, 2f, 3f, 50f, 2f, 3f, ref shapes);
        Assert.That(facingWall, Is.EqualTo(2.8f).Within(1e-3f), "the distance to the wall face, not zero");

        // Standing in the doorway's own lane, where the nearest wall is the jamb 0.6 m to the side.
        var facingDoor = SimBridge.DiscFor(house, 0f, 3f, 50f, 0f, 3f, ref shapes);
        Assert.That(facingDoor, Is.EqualTo(MathF.Sqrt((0.6f * 0.6f) + (2.8f * 2.8f))).Within(1e-3f), "the corner distance to the jamb");
        Assert.That(facingDoor, Is.GreaterThan(facingWall), "and the open lane is the roomier of the two");

        var footprintOnly = SimBridge.DiscForForTest(house, 0f, 3f, 50f, 0f, 3f);
        Assert.That(footprintOnly, Is.Zero, "and without the asset the footprint still spends the disc, as it must");
    }

    /// <summary>
    /// A footprint whose real shape is not loaded constrains the disc no more than open desert does.
    /// </summary>
    /// <remarks>
    /// The shuttleport reserves a 46 m square at the centre of every city, which is where bodies spawn and crowd. It is
    /// walked through until its walls are loaded (see <c>StructureShapes.Shape.None</c>), so a disc measured against it
    /// would promise nothing while blocking nothing — the busiest ground on the planet never holding a disc.
    /// </remarks>
    [Test]
    public void ADiscIgnoresAFootprintThatBlocksNothing()
    {
        var entity = EntityId.FromRawValue(9);
        ProximityBox[] port = [new(-23f, -23f, 23f, 23f, 0f, entity)];
        var shapes = StructureShapes.ForTest(0.4f, entity, StructureShapes.Shape.None, [], 0f, 0f, 0);

        var clear = SimBridge.DiscFor(port, 0f, 0f, 500f, 0f, 0f, ref shapes);
        Assert.That(clear, Is.EqualTo(SimBridge.ProximityLeashM), "a structure that blocks nothing should leave the leash standing");
    }

    /// <summary>
    /// Outside a footprint the disc is charged the overhang, because a wall may stand that far outside the box measured.
    /// </summary>
    /// <remarks>
    /// The gate grows by the same figure for the same reason. Without the charge a body could hold a disc that reaches
    /// 0.4 m into a wall it was never told about, and spend a tick inside it before the refresh ejected it.
    /// </remarks>
    [Test]
    public void ADiscOutsideAFootprintIsChargedTheOverhang()
    {
        var shapes = StructureShapes.ForTest(0.4f, default, StructureShapes.Shape.Footprint, [], 0f, 0f, 0);
        var clear = SimBridge.DiscFor(OneBox, -12f, 5f, 50f, -12f, 5f, ref shapes);
        Assert.That(clear, Is.EqualTo(11.6f).Within(1e-3f), "twelve metres of ground less the furthest a wall may overhang it");
    }

    /// <summary>
    /// A full neighbour set bounds the disc by the farthest box it kept, because the boxes it dropped are unknown.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>"The nearest sixteen were kept" is measured from the PROBE CENTRE; the disc is measured from where the body
    /// ended up.</b> The push moves the body off the centre by up to its own radius plus the asset overhang, so the
    /// comfortable argument — every dropped box is farther than every kept one, hence farther than the minimum the
    /// disc takes — says nothing about distances from the BODY. A dropped box on the side the body was pushed towards
    /// can be nearer to it than anything kept, and the disc would then promise ground a wall is standing on.
    /// </para>
    /// <para>
    /// The case below is that geometry, staged: every kept box is 10 m from the centre and 12 m from the body, so the
    /// unbounded disc would be 12 m, while a dropped box could sit at 10 m from the centre and therefore 8 m from the
    /// body. The bound — farthest kept, less the drift — gives 8 m, which is exactly the distance a dropped box is
    /// guaranteed not to beat. A run with the set NOT full is unaffected, which the second half asserts.
    /// </para>
    /// </remarks>
    [Test]
    public void ADiscFromAFullSetIsBoundedByTheFarthestBoxItKept()
    {
        // One box 10 m east of the probe centre; the body ended up 2 m WEST of the centre, so 12 m from that box.
        ProximityBox[] kept = [new(10f, -1f, 20f, 1f, 100f)];
        var unbounded = SimBridge.DiscForForTest(kept, 0f, 0f, 50f, -2f, 0f);
        Assert.That(unbounded, Is.EqualTo(12f).Within(1e-3f), "with room to spare the disc is the distance to the surface");

        var bounded = SimBridge.DiscForForTest(kept, 0f, 0f, 50f, -2f, 0f, overflowed: true);
        Assert.That(bounded, Is.EqualTo(8f).Within(1e-3f), "a full set may promise only the farthest kept box less the drift");
        Assert.That(bounded, Is.LessThan(unbounded), "the bound has to cost something or it is not a bound");
    }

    /// <summary>The overflow bound never makes a disc negative, however far the push moved the body.</summary>
    [Test]
    public void ADiscFromAFullSetIsNeverNegative()
    {
        ProximityBox[] kept = [new(1f, -1f, 2f, 1f, 1f)];
        var clear = SimBridge.DiscForForTest(kept, 0f, 0f, 50f, -40f, 0f, overflowed: true);
        Assert.That(clear, Is.Zero);
    }

    /// <summary>A body pushed clear outside its own probe gets no disc at all, rather than a negative one.</summary>
    [Test]
    public void ADiscForABodyPushedOutsideItsProbe_IsZero()
    {
        var clear = SimBridge.DiscForForTest([], 0f, 0f, 10f, 14f, 0f);
        Assert.That(clear, Is.Zero);
    }

    [Test]
    public void ADiscHoldsInsideItselfAndNowhereElse()
    {
        var disc = new PlayerProximity { AnchorX = 100f, AnchorZ = 100f, ClearM = 10f, Realm = 0, Generation = 0 };
        Assert.Multiple(() =>
        {
            Assert.That(SimBridge.DiscHolds(in disc, RealmId.Default, 0, 104f, 100f, Half), Is.True, "four metres inside a ten-metre disc");
            Assert.That(SimBridge.DiscHolds(in disc, RealmId.Default, 0, 112f, 100f, Half), Is.False, "two metres outside it");

            // The disc measures to a SURFACE, so the body's own half-extent is what it may not spend. Written against the
            // constant rather than against numbers that quietly assume a particular body: 0.2 m of slack is less than the
            // body needs whatever the body is, and 2 m is more.
            Assert.That(SimBridge.DiscHolds(in disc, RealmId.Default, 0, 100f + (10f - Half) + 0.2f, 100f, Half), Is.False,
                "the remaining gap to the surface is smaller than the body's own half-extent, so the body has already crossed it");
            Assert.That(SimBridge.DiscHolds(in disc, RealmId.Default, 0, 108f, 100f, Half), Is.True, "eight metres leaves two, and the body needs less");
        });
    }

    /// <summary>
    /// A disc taken in another realm says nothing about this one, and nothing at the teleport site has to remember that.
    /// </summary>
    /// <remarks>
    /// <b>The case geometry alone would get wrong.</b> An interior's coordinates are local and small — a room is tens of
    /// metres across while a planet anchor is thousands of metres out — so a position inside a room can land inside a planet
    /// disc by arithmetic coincidence rather than by proximity. Carrying the realm is what makes a shuttle, a clone and a
    /// portal crossing self-correcting instead of needing an invalidation call at each of the three sites.
    /// </remarks>
    [Test]
    public void ADiscFromAnotherRealm_NeverHolds()
    {
        var disc = new PlayerProximity { AnchorX = 32f, AnchorZ = 6f, ClearM = SimBridge.ProximityLeashM, Realm = 0, Generation = 0 };
        Assert.Multiple(() =>
        {
            Assert.That(SimBridge.DiscHolds(in disc, RealmId.Default, 0, 33f, 6f, Half), Is.True, "the same realm, a metre away");
            Assert.That(SimBridge.DiscHolds(in disc, new RealmId(1), 0, 33f, 6f, Half), Is.False,
                "an interior position inside a planet disc by coincidence was accepted");
        });
    }

    /// <summary>A disc taken before something fixed was placed is retired unread.</summary>
    /// <remarks>
    /// Latent today, because nothing creates a structure after world build — which is exactly why it needs a case: the first
    /// feature that does would otherwise leave bodies standing inside new buildings with nothing to notice.
    /// </remarks>
    [Test]
    public void ADiscFromAnEarlierStaticGeneration_NeverHolds()
    {
        var disc = new PlayerProximity { AnchorX = 0f, AnchorZ = 0f, ClearM = SimBridge.ProximityLeashM, Realm = 0, Generation = 7 };
        Assert.Multiple(() =>
        {
            Assert.That(SimBridge.DiscHolds(in disc, RealmId.Default, 7, 1f, 0f, Half), Is.True, "the generation it was taken in");
            Assert.That(SimBridge.DiscHolds(in disc, RealmId.Default, 8, 1f, 0f, Half), Is.False, "a structure was placed and the disc was still trusted");
        });
    }

    /// <summary>Every structure's box in realm 0, read from the entities rather than from any index.</summary>
    private static List<ProximityBox> Structures(TatooineSim sim)
    {
        var boxes = new List<ProximityBox>();
        using var tx = sim.Dbe.CreateQuickTransaction();
        foreach (var cluster in tx.GetClusterEnumerator<WorldObject>())
        {
            var places = cluster.GetReadOnlySpan(WorldObject.Bounds);
            var realms = cluster.GetReadOnlySpan(WorldObject.Realm);
            for (var bits = cluster.OccupancyBits; bits != 0; bits &= bits - 1)
            {
                var idx = BitOperations.TrailingZeroCount(bits);
                if (realms[idx].Value != 0)
                {
                    continue;
                }

                var b = places[idx].Bounds;
                boxes.Add(Box(b.MinX, b.MinY, b.MaxX, b.MaxY));
            }
        }

        return boxes;
    }

    /// <summary>
    /// A structure with clear ground to its west, so a body can be walked into its west face without starting inside
    /// something else.
    /// </summary>
    private static ProximityBox WithClearGroundWest(List<ProximityBox> boxes, float runway)
    {
        foreach (var box in boxes)
        {
            var z = (box.MinZ + box.MaxZ) * 0.5f;
            var clear = true;
            foreach (var other in boxes)
            {
                if (other.MinX == box.MinX && other.MinZ == box.MinZ && other.MaxX == box.MaxX && other.MaxZ == box.MaxZ)
                {
                    continue;
                }

                // Anything overlapping the lane the body will run along, from the start point up to the target's west face.
                if (other.MaxX > box.MinX - runway - 4f && other.MinX < box.MinX && other.MaxZ > z - 4f && other.MinZ < z + 4f)
                {
                    clear = false;
                    break;
                }
            }

            if (clear)
            {
                return box;
            }
        }

        Assert.Fail("no structure in this world has clear ground to its west, so no case here can be staged");
        return default;
    }

    /// <summary>Makes a player a client's body, parks its decision and gives it a velocity, all in one transaction.</summary>
    private static float Drive(TatooineSim sim, EntityId id, float x, float z, float velX, float velZ)
    {
        using var tx = sim.Dbe.CreateQuickTransaction(DurabilityMode.GroupCommit, CommitDiscipline.Commit);
        var player = tx.OpenMut(id);
        var half = player.Read(Player.Bounds).HalfExtent;

        // Possessed: PlayerThink leaves it alone entirely, so the velocity set here survives every tick. It is also what
        // makes it collide -- the scripted crowd deliberately does not.
        player.Set(Player.Control, new PlayerControl { Kind = ControllerKind.Human });

        var state = player.Read(Player.State);
        state.Activity = PlayerActivity.Travelling;
        state.ActivityTicks = int.MaxValue / 2;
        player.Set(Player.State, state);

        var move = player.Read(Player.Move);
        move.VelX = velX;
        move.VelZ = velZ;
        move.BiasTick = 0;
        move.BiasX = 0f;
        move.BiasZ = 0f;
        move.DestX = x + (velX * 10_000f);
        move.DestZ = z + (velZ * 10_000f);
        player.Set(Player.Move, move);

        var at = default(PlayerPlacement);
        at.SetAt(x, z, 0f, half);
        tx.Teleport(id, Player.Bounds, RealmId.Default, in at);
        Assert.That(tx.Commit(), Is.True);
        return half;
    }

    /// <summary>A small world with the shuttles off, so nothing flies a staged body away mid-case.</summary>
    private static SimConfig Config(string dir, int ticks)
    {
        var config = Worlds.Small(dir);
        config.WarmTicks = 0;
        config.MeasuredTicks = ticks;
        config.Shuttles = false;
        return config;
    }

    /// <summary>
    /// A client's body walked straight into a building stops at its wall instead of passing through it.
    /// </summary>
    /// <remarks>
    /// <b>Falsifiable.</b> With <c>SWG_NO_COLLISION=1</c>, or with the push removed, the body ends east of the building having
    /// walked through it — measured at 35 m past the far face — and three of these assertions fail.
    /// </remarks>
    [Test]
    [Category("Integration")]
    public void AClientsBodyWalkedIntoABuilding_StopsAtItsWall()
    {
        var dir = Worlds.NewDirectory();
        try
        {
            var config = Config(dir, 100);
            using var sim = new TatooineSim(config);
            sim.Initialize();

            var boxes = Structures(sim);
            Assert.That(boxes, Is.Not.Empty, "the world has no structures, so this proves nothing");

            const float Runway = 4f;
            var target = WithClearGroundWest(boxes, Runway);
            var lane = (target.MinZ + target.MaxZ) * 0.5f;
            var startX = target.MinX - Runway;
            var half = Drive(sim, sim.Index.Players[0], startX, lane, TatooineData.PlayerRunSpeedMps / config.TickRateHz, 0f);

            sim.Run();

            float endX, endZ;
            ushort realm;
            PlayerProximity disc;
            using (var tx = sim.Dbe.CreateQuickTransaction())
            {
                var player = tx.Open(sim.Index.Players[0]);
                var at = player.Read(Player.Bounds);
                endX = at.X;
                endZ = at.Z;
                realm = player.Read(Player.Realm).Value;
                disc = player.Read(Player.Proximity);
            }

            var (held, refreshed, _, contacts, _, _, unresolved) = sim.ProximityTotals;
            TestContext.Out.WriteLine($"target ({target.MinX:F1}, {target.MinZ:F1})-({target.MaxX:F1}, {target.MaxZ:F1}); body {startX:F1} -> {endX:F1}; "
                + $"steps {held + refreshed}, contacts {contacts}, unresolved {unresolved}; disc realm {disc.Realm} clear {disc.ClearM:F2}");

            Assert.Multiple(() =>
            {
                Assert.That(contacts, Is.GreaterThan(0), "the body never touched the building: the case did not aim it at one");
                Assert.That(Touches(target, endX, endZ, half), Is.False, $"the body is inside the building at ({endX:F2}, {endZ:F2})");
                Assert.That(endX, Is.LessThan(target.MinX), "the body is east of the building's west face: it walked through it");
                Assert.That(endX, Is.EqualTo(target.MinX - half).Within(0.05f), "the body did not come to rest against the wall");
                Assert.That(unresolved, Is.Zero, "a body against a single wall could not be resolved");

                // The disc the SIMULATION wrote, not the helper's: nothing else here would catch a hardcoded realm.
                Assert.That(disc.Realm, Is.EqualTo(realm), "the disc records a different realm from the body that owns it");
            });
        }
        finally
        {
            Worlds.Delete(dir);
        }
    }

    /// <summary>
    /// A body walking obliquely into a wall keeps its motion along the wall — the claim that makes a doorway usable.
    /// </summary>
    /// <remarks>
    /// <b>Driven through the simulation, because the pure push cannot make this claim.</b> An earlier version asserted it
    /// against <c>PushOut</c>, which takes a position and no velocity: "it stopped dead" is not a behaviour that function can
    /// exhibit, so the case was the ejection case with shallower penetration and could not fail for the reason it named.
    /// Sliding only exists once a velocity is integrated every tick, and that is here.
    /// </remarks>
    [Test]
    [Category("Integration")]
    public void AClientsBodyWalkingObliquelyIntoAWall_SlidesAlongIt()
    {
        var dir = Worlds.NewDirectory();
        try
        {
            var config = Config(dir, 100);
            using var sim = new TatooineSim(config);
            sim.Initialize();

            var target = WithClearGroundWest(Structures(sim), 4f);
            var lane = (target.MinZ + target.MaxZ) * 0.5f;
            var step = TatooineData.PlayerRunSpeedMps / config.TickRateHz;

            // North-east: the east half is into the wall, the north half along it.
            var half = Drive(sim, sim.Index.Players[0], target.MinX - 4f, lane, step, step);

            sim.Run();

            float endX, endZ;
            using (var tx = sim.Dbe.CreateQuickTransaction())
            {
                var at = tx.Open(sim.Index.Players[0]).Read(Player.Bounds);
                endX = at.X;
                endZ = at.Z;
            }

            TestContext.Out.WriteLine($"wall at x {target.MinX:F1}, lane {lane:F1}; body ended ({endX:F1}, {endZ:F1}), advanced {endZ - lane:F1} m along it");

            Assert.Multiple(() =>
            {
                Assert.That(Touches(target, endX, endZ, half), Is.False, $"the body is inside the building at ({endX:F2}, {endZ:F2})");

                // The whole claim: the tangential half of the step survived. A body that stopped dead on contact would have
                // advanced only as far as the wall, a few metres at most.
                Assert.That(endZ - lane, Is.GreaterThan(20f), "the motion along the wall was eaten: this is the sticky-wall defect");
            });
        }
        finally
        {
            Worlds.Delete(dir);
        }
    }

    /// <summary>
    /// Two client bodies in one cluster, so the batch is driven with a member index past zero.
    /// </summary>
    /// <remarks>
    /// <b>The coverage hole a review found.</b> Every other case here possesses one body, so <c>pending</c> is always 1,
    /// <c>member</c> is always 0, and every <c>member * MaxNeighbours + n</c> offset in the sink degenerates to <c>0 + n</c> —
    /// a transposed member index, a stale per-member count or cross-member bleed passed the whole fixture. Batching is the
    /// reason the design is affordable, so it needs a case where two members' neighbour sets must not be confused.
    /// </remarks>
    [Test]
    [Category("Integration")]
    public void TwoClientBodiesInOneCluster_EachGetsItsOwnNeighbours()
    {
        var dir = Worlds.NewDirectory();
        try
        {
            var config = Config(dir, 60);
            using var sim = new TatooineSim(config);
            sim.Initialize();

            var boxes = Structures(sim);
            var target = WithClearGroundWest(boxes, 4f);
            var lane = (target.MinZ + target.MaxZ) * 0.5f;
            var step = TatooineData.PlayerRunSpeedMps / config.TickRateHz;

            // Both in the same place and so in the same cluster (clusters are spatial), one aimed at the wall and one away
            // from it: if their neighbour sets were confused, the one walking away would be stopped by a box it cannot see.
            var a = sim.Index.Players[0];
            var b = sim.Index.Players[1];
            var half = Drive(sim, a, target.MinX - 4f, lane, step, 0f);
            Drive(sim, b, target.MinX - 6f, lane, -step, 0f);

            sim.Run();

            float ax, bx;
            using (var tx = sim.Dbe.CreateQuickTransaction())
            {
                ax = tx.Open(a).Read(Player.Bounds).X;
                bx = tx.Open(b).Read(Player.Bounds).X;
            }

            var (_, _, batches, contacts, _, _, _) = sim.ProximityTotals;
            TestContext.Out.WriteLine($"wall at x {target.MinX:F1}; A ended {ax:F1} (expected ~{target.MinX - half:F1}), B ended {bx:F1}; "
                + $"queries {batches}, contacts {contacts}");

            Assert.Multiple(() =>
            {
                Assert.That(ax, Is.EqualTo(target.MinX - half).Within(0.05f), "the body aimed at the wall did not come to rest against it");
                Assert.That(bx, Is.LessThan(target.MinX - 6f - 20f),
                    "the body walking AWAY from the wall was held up by it: the members' neighbours were confused");
                Assert.That(contacts, Is.GreaterThan(0), "neither body touched anything, so the batch carried nothing to confuse");
            });
        }
        finally
        {
            Worlds.Delete(dir);
        }
    }

    /// <summary>
    /// A body crossing a town: an independent oracle says no position it was ever given was inside a structure, and most of
    /// its steps cost no query.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The oracle is the point, and it is deliberately not the neighbourhood.</b> With
    /// <see cref="SimConfig.CollisionAudit"/> on, every position the move system writes for a colliding body is re-checked
    /// against a <i>fresh</i> spatial query — no disc, no anchor, no cached box. A disc that wrongly decided nothing was
    /// near, a push that stopped half a metre short, or a batch member index transposed inside the sink all show up here,
    /// and every one of them would leave the neighbourhood's own bookkeeping perfectly self-consistent.
    /// </para>
    /// <para>
    /// <b>The route is a whole city diameter, so the geometry is whatever that seed planted</b> rather than a shape chosen to
    /// be passed. It replaces a case that walked a body across open desert, where all four of its refreshes returned zero
    /// boxes — so its overflow assertion graded a query that found nothing and its disc ratio reduced to leash arithmetic a
    /// unit test proves in a millisecond.
    /// </para>
    /// </remarks>
    [Test]
    [Category("Integration")]
    public void ABodyCrossingATown_IsNeverGivenAPositionInsideAStructure()
    {
        var dir = Worlds.NewDirectory();
        try
        {
            var config = Config(dir, 400);
            config.CollisionAudit = true;
            using var sim = new TatooineSim(config);
            sim.Initialize();

            Assert.That(Structures(sim), Is.Not.Empty, "the world has no structures, so this proves nothing");

            // Start outside the city's western edge and run due east through the middle of it.
            var city = Worlds.WidestCity(sim);
            var step = TatooineData.PlayerRunSpeedMps / config.TickRateHz;
            Drive(sim, sim.Index.Players[0], city.X - city.Radius - 20f, city.Z, step, 0f);

            sim.Run();

            var (held, refreshed, batches, contacts, neighbours, overflows, unresolved) = sim.ProximityTotals;
            var (audited, violations) = sim.CollisionAuditTotals;
            var steps = held + refreshed;
            TestContext.Out.WriteLine($"city at ({city.X:F0}, {city.Z:F0}) r{city.Radius:F0}; steps {steps}, held {held}, refreshed {refreshed}, "
                + $"queries {batches}, contacts {contacts}, neighbours/refresh {(refreshed == 0 ? 0d : (double)neighbours / refreshed):F2}, "
                + $"overflows {overflows}, unresolved {unresolved}; audited {audited}, violations {violations}");

            Assert.Multiple(() =>
            {
                Assert.That(steps, Is.GreaterThan(0), "the body never took a step, so nothing was measured");
                Assert.That(audited, Is.EqualTo(steps), "the audit did not see every step, so its silence means less than it looks");
                Assert.That(violations, Is.Zero, $"{violations} of {audited} written positions were inside a structure");

                // The query actually found geometry -- which is what the open-desert version of this case did not.
                Assert.That(neighbours, Is.GreaterThan(0), "no refresh returned a single structure, so the disc was never asked a real question");
                Assert.That(held, Is.GreaterThan(refreshed), "most steps must prove themselves safe without a query, even crossing a town");
            });
        }
        finally
        {
            Worlds.Delete(dir);
        }
    }

    /// <summary>
    /// The scripted crowd is collided, and <c>--no-collide-crowd</c> is what stops it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This asserted the opposite, and the opposite was the default until 2026-10-22.</b> The crowd walked through
    /// walls because it was drawn as boxes and nobody could see it; it is drawn as people now, so it collides. The
    /// behaviour this fixture was built to pin is still worth pinning — it is just the flagged arm rather than the
    /// default one, and both are checked here so neither can drift without a failure.
    /// </para>
    /// <para>
    /// Compared against the ticks the runtime actually <b>ran</b>, not the window the configuration asked for: the runtime
    /// overshoots by a dozen ticks or so, and an earlier version spent most of its slack on that overshoot and would have
    /// failed on a loaded box with a message blaming the controller gate.
    /// </para>
    /// </remarks>
    [Test]
    [Category("Integration")]
    public void TheScriptedCrowdIsCollided_AndTheFlagStopsIt()
    {
        var dir = Worlds.NewDirectory();
        var defaultsDir = Worlds.NewDirectory();
        try
        {
            var config = Config(dir, 200);
            config.CollideCrowd = false;
            using var sim = new TatooineSim(config);
            sim.Initialize();

            var step = TatooineData.PlayerRunSpeedMps / config.TickRateHz;
            Drive(sim, sim.Index.Players[0], 0f, 0f, step, 0f);
            sim.Run();

            var (held, refreshed, _, _, _, _, _) = sim.ProximityTotals;
            var steps = held + refreshed;
            var players = sim.Index.Players.Count;
            TestContext.Out.WriteLine($"{players} players, {steps} colliding steps over {sim.Executed} ticks run ({config.MeasuredTicks} requested)");

            Assert.Multiple(() =>
            {
                Assert.That(players, Is.GreaterThan(1), "a world with one player cannot show that the crowd is left alone");
                Assert.That(steps, Is.GreaterThan(0), "the possessed body never took a step, so nothing was measured");
                Assert.That(steps, Is.LessThanOrEqualTo(sim.Executed),
                    $"--no-collide-crowd, yet {steps} colliding steps over {sim.Executed} ticks for one possessed body: the crowd is being collided too");
            });

            // And the default, which is the arm the world actually runs in: every body is on the collision path, so the
            // step count is a multiple of the population rather than of the one body a client is driving.
            var defaults = Config(defaultsDir, 200);
            using var collided = new TatooineSim(defaults);
            collided.Initialize();
            Drive(collided, collided.Index.Players[0], 0f, 0f, step, 0f);
            collided.Run();
            var (heldAll, refreshedAll, _, _, _, _, _) = collided.ProximityTotals;
            var all = heldAll + refreshedAll;
            TestContext.Out.WriteLine($"colliding by default: {all} steps over {collided.Executed} ticks, {collided.Index.Players.Count} players");
            Assert.That(all, Is.GreaterThan(collided.Executed * 2),
                $"the crowd collides by default, so {all} steps over {collided.Executed} ticks is barely more than the one possessed body would make alone");
        }
        finally
        {
            Worlds.Delete(dir);
            Worlds.Delete(defaultsDir);
        }
    }

    /// <summary>
    /// A structure's own geometry, read from the entity: where it stands, which asset it is, and its quarter turn.
    /// </summary>
    /// <param name="sim">The world.</param>
    /// <param name="box">The footprint to find the entity for.</param>
    /// <returns>The structure behind that footprint.</returns>
    private static (float X, float Z, int Asset, int Yaw, int Kind) StructureAt(TatooineSim sim, in ProximityBox box)
    {
        var cx = (box.MinX + box.MaxX) * 0.5f;
        var cz = (box.MinZ + box.MaxZ) * 0.5f;
        using var tx = sim.Dbe.CreateQuickTransaction();
        foreach (var cluster in tx.GetClusterEnumerator<WorldObject>())
        {
            var places = cluster.GetReadOnlySpan(WorldObject.Bounds);
            var structs = cluster.GetReadOnlySpan(WorldObject.Struct);
            var realms = cluster.GetReadOnlySpan(WorldObject.Realm);
            for (var bits = cluster.OccupancyBits; bits != 0; bits &= bits - 1)
            {
                var idx = System.Numerics.BitOperations.TrailingZeroCount(bits);
                if (realms[idx].Value != 0)
                {
                    continue;
                }

                var p = places[idx];
                if (MathF.Abs(p.X - cx) < 0.01f && MathF.Abs(p.Z - cz) < 0.01f)
                {
                    return (p.X, p.Z, structs[idx].Asset, structs[idx].Yaw, structs[idx].Kind);
                }
            }
        }

        Assert.Fail("no structure entity stands at the footprint this case picked");
        return default;
    }

    /// <summary>How tall the test walls are: above a step and below head height, so they are unambiguously walls.</summary>
    private const float WallTopM = 2.5f;

    /// <summary>
    /// Pre-rotates a box so that the server's own quarter turn maps it to the world box this case wants.
    /// </summary>
    /// <param name="yaw">The structure's quarter turn.</param>
    /// <param name="wanted">The box as it should end up, relative to the structure's origin.</param>
    /// <returns>The box to publish in the asset's own frame.</returns>
    /// <remarks>
    /// Four turns are the identity, so turning by <c>4 - yaw</c> is the inverse. Written this way so the case states
    /// the geometry it is actually testing — a wall across the body's lane — rather than geometry that happens to land
    /// there for one seed's random orientation.
    /// </remarks>
    private static AssetColliders.PlanBox Unturn(int yaw, AssetColliders.PlanBox wanted)
    {
        var b = wanted;
        for (var i = 0; i < (4 - (yaw % 4)) % 4; i++)
        {
            var t = SimBridge.TurnForTest(1, b);
            b = new AssetColliders.PlanBox(t.MinX, t.MinZ, t.MaxX, t.MaxZ, b.MinY, b.MaxY);
        }

        return b;
    }

    /// <summary>
    /// <b>The narrow phase runs: a body stops at the published WALL, metres inside the footprint that contains it.</b>
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This is the case the rest of the fixture could not make.</b> Every other end-to-end case here builds a world
    /// with no asset set, so <c>AssetShapes</c> is null and every structure collides as its footprint box — the whole
    /// suite would stay green with the second level of the test deleted. Loïc asked, in those words, whether anything
    /// tested against the children rather than the root bounding volume. Nothing did.
    /// </para>
    /// <para>
    /// <b>The two predictions are 6.7 m apart, which is what makes it a proof rather than a check.</b> The wall is a
    /// slab 2 m either side of the structure's origin; its footprint is 9 m. A body walked east into it rests at the
    /// SLAB's west face if the asset's primitives are consulted, and at the FOOTPRINT's west face — seven metres
    /// earlier — if they are not. Deleting the <c>Shape.Primitives</c> branch fails this on the first assertion.
    /// </para>
    /// </remarks>
    [Test]
    [Category("Integration")]
    public void AClientsBodyStopsAtThePublishedWall_NotAtTheFootprintAroundIt()
    {
        var dir = Worlds.NewDirectory();
        try
        {
            var config = Config(dir, 140);
            using var sim = new TatooineSim(config);
            sim.Initialize();

            var boxes = Structures(sim);
            Assert.That(boxes, Is.Not.Empty, "the world has no structures, so this proves nothing");

            // <b>A WIDE structure, deliberately.</b> The proof is that the body stops at a wall strictly INSIDE the
            // footprint, so the footprint has to be wider than the slab: picked against a terminal (1.6 m across) the
            // slab sticks out of it, the body stops short of the footprint, and the case asserts the wrong direction.
            const float Runway = 6f;
            const float SlabHalf = 2f;
            var wide = boxes.FindAll(b => b.MaxX - b.MinX >= (4f * SlabHalf) && b.MaxZ - b.MinZ >= (4f * SlabHalf));
            Assert.That(wide, Is.Not.Empty, "no structure in this world is wide enough to hold the test wall inside it");
            var target = WithClearGroundWest(wide, Runway);
            var s = StructureAt(sim, in target);

            // A wall slab 2 m either side of the structure's origin, across the lane the body will run along. Published
            // in the asset's own frame, so the server's quarter turn puts it back where this case wants it.
            var slab = Unturn(s.Yaw, new AssetColliders.PlanBox(-SlabHalf, -SlabHalf, SlabHalf, SlabHalf, 0f, WallTopM));
            var byIndex = new AssetColliders.PlanBox[StructureAssets.Table.Length][];
            for (var i = 0; i < byIndex.Length; i++)
            {
                byIndex[i] = [];
            }

            Assert.That(s.Asset, Is.LessThan(byIndex.Length), "the structure this case picked publishes no asset, so there is no narrow phase to prove");
            byIndex[s.Asset] = [slab];
            sim.AssetShapesOverride = AssetColliders.FromBoxesForTest(byIndex);

            var lane = s.Z;
            var startX = target.MinX - Runway;
            var half = Drive(sim, sim.Index.Players[0], startX, lane, TatooineData.PlayerRunSpeedMps / config.TickRateHz, 0f);

            sim.Run();

            float endX;
            using (var tx = sim.Dbe.CreateQuickTransaction())
            {
                endX = tx.Open(sim.Index.Players[0]).Read(Player.Bounds).X;
            }

            var atTheWall = s.X - SlabHalf - half;
            var atTheFootprint = target.MinX - half;
            var (held, refreshed, _, contacts, _, _, _) = sim.ProximityTotals;
            TestContext.Out.WriteLine($"structure kind {s.Kind} asset {s.Asset} yaw {s.Yaw} at ({s.X:F2}, {s.Z:F2}); footprint west {target.MinX:F2}; "
                + $"body {startX:F2} -> {endX:F2}; wall predicts {atTheWall:F2}, footprint predicts {atTheFootprint:F2}; "
                + $"steps {held + refreshed}, contacts {contacts}");

            Assert.Multiple(() =>
            {
                Assert.That(contacts, Is.GreaterThan(0), "the body never touched anything: the case did not aim it at the structure");
                Assert.That(endX, Is.GreaterThan(atTheFootprint + 1f),
                    $"the body stopped at the FOOTPRINT ({endX:F2}): the narrow phase did not run, and only the root box was tested");
                Assert.That(endX, Is.EqualTo(atTheWall).Within(0.1f), "the body did not come to rest against the published wall");
            });
        }
        finally
        {
            Worlds.Delete(dir);
        }
    }

    /// <summary>
    /// <b>And it is the wall that blocks, not the footprint: a lane with no primitive in it is walked straight through.</b>
    /// </summary>
    /// <remarks>
    /// The other half of the proof. The same structure publishes a wall that stops 3 m short of the body's lane, so a
    /// body aimed down that lane crosses the whole footprint and comes out the far side. If the footprint box were the
    /// collider — or if an asset with primitives silently fell back to it — the body would stop at the west face. This
    /// is what makes a doorway a doorway, stated as the thing that can fail.
    /// </remarks>
    [Test]
    [Category("Integration")]
    public void AClientsBodyWalksThroughAFootprintWhereThePublishedAssetHasNoWall()
    {
        var dir = Worlds.NewDirectory();
        try
        {
            var config = Config(dir, 200);
            using var sim = new TatooineSim(config);
            sim.Initialize();

            var boxes = Structures(sim);
            var target = WithClearGroundWest(boxes, 6f);
            var s = StructureAt(sim, in target);

            // A slab well to the NORTH of the lane: inside the footprint, nowhere near the body's path.
            var slab = Unturn(s.Yaw, new AssetColliders.PlanBox(-2f, 3f, 2f, 7f, 0f, WallTopM));
            var byIndex = new AssetColliders.PlanBox[StructureAssets.Table.Length][];
            for (var i = 0; i < byIndex.Length; i++)
            {
                byIndex[i] = [];
            }

            Assert.That(s.Asset, Is.LessThan(byIndex.Length), "the structure this case picked publishes no asset");
            byIndex[s.Asset] = [slab];
            sim.AssetShapesOverride = AssetColliders.FromBoxesForTest(byIndex);

            var startX = target.MinX - 6f;
            Drive(sim, sim.Index.Players[0], startX, s.Z, TatooineData.PlayerRunSpeedMps / config.TickRateHz, 0f);

            sim.Run();

            float endX;
            using (var tx = sim.Dbe.CreateQuickTransaction())
            {
                endX = tx.Open(sim.Index.Players[0]).Read(Player.Bounds).X;
            }

            TestContext.Out.WriteLine($"structure asset {s.Asset} yaw {s.Yaw} at ({s.X:F2}, {s.Z:F2}); footprint {target.MinX:F2}..{target.MaxX:F2}; "
                + $"body {startX:F2} -> {endX:F2}");

            Assert.That(endX, Is.GreaterThan(s.X),
                $"the body stopped at ({endX:F2}), short of the structure's own origin: an empty lane was treated as solid");
        }
        finally
        {
            Worlds.Delete(dir);
        }
    }
}
