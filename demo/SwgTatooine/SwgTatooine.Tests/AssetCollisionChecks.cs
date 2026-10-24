using System;
using NUnit.Framework;
using SwgTatooine.Server;
using SwgTatooine.Navigation;

namespace SwgTatooine.Tests;

/// <summary>
/// S3 — a body is pushed out of a building's walls rather than out of its bounding box, so a doorway is passable.
/// </summary>
/// <remarks>
/// <para>
/// <b>What makes a door work is that nothing knows what a door is.</b> The walls of an asset are separate primitives,
/// so the opening between them is a place with no box in it. These cases therefore assert on geometry only: a gap is
/// passable, a wall is not, and the two sides agree about which way the building faces.
/// </para>
/// <para>
/// Pure arithmetic — no world, no descriptors on disk, no server, with one exception that says so and carries its own
/// <c>Integration</c> category. The fixture used to carry that category itself, which charged eight staged cases the
/// cost of the one that builds a world and kept them out of the suite that runs on every edit.
/// </para>
/// </remarks>
[TestFixture]
public sealed class AssetCollisionChecks
{
    /// <summary>Two wall boxes with a 1.2 m doorway between them, as the published houses are built.</summary>
    /// <remarks>
    /// Each wall is 4 m of frontage, 0.4 m thick, with the gap from x = −0.6 to +0.6 — the published doorway width.
    /// </remarks>
    private static AssetColliders.PlanBox[] Doorway() =>
    [
        new(-4.6f, -0.2f, -0.6f, 0.2f, 0f, 2.5f),
        new(0.6f, -0.2f, 4.6f, 0.2f, 0f, 2.5f),
    ];

    /// <summary>A body walks through the gap; the same body at the same depth into a wall does not.</summary>
    [Test]
    public void ADoorwayIsPassableAndAWallIsNot()
    {
        var walls = Doorway();
        const float half = TatooineData.PlayerBodyHalfM;

        // Dead centre of the opening, level with the wall: 0.3 m of body against 0.6 m of gap each side.
        var x = 0f;
        var z = 0f;
        var moved = SimBridge.PushOutOfShapesForTest(walls, 0f, 0f, 0, half, 8192f, ref x, ref z);
        Assert.Multiple(() =>
        {
            Assert.That(moved, Is.False, "the opening is 1.2 m and the body is 0.6 m across: it fits");
            Assert.That(x, Is.EqualTo(0f));
            Assert.That(z, Is.EqualTo(0f));
        });

        // Two metres along the frontage is wall, at the same depth.
        x = -2f;
        z = 0f;
        moved = SimBridge.PushOutOfShapesForTest(walls, 0f, 0f, 0, half, 8192f, ref x, ref z);
        Assert.Multiple(() =>
        {
            Assert.That(moved, Is.True, "a wall is a wall");
            Assert.That(MathF.Abs(z), Is.GreaterThanOrEqualTo(0.2f + half - 1e-4f), "pushed clear of the wall's face");
        });
    }

    /// <summary>A body just inside a jamb is pushed into the opening, not through the wall.</summary>
    /// <remarks>
    /// The push is along the shallowest axis, so a body that clipped the jamb leaves sideways into the doorway rather
    /// than being thrown the depth of the wall. This is what stops a body entering a house through its own wall.
    /// </remarks>
    [Test]
    public void AClippedJambPushesSideways()
    {
        var walls = Doorway();
        var x = -0.7f;
        var z = 0f;
        SimBridge.PushOutOfShapesForTest(walls, 0f, 0f, 0, TatooineData.PlayerBodyHalfM, 8192f, ref x, ref z);

        // Out of the wall's x-range, and still at the wall's own depth: it slid into the gap.
        Assert.Multiple(() =>
        {
            Assert.That(x, Is.GreaterThan(-0.6f), "pushed towards the opening");
            Assert.That(z, Is.EqualTo(0f), "and not through the wall");
        });
    }

    /// <summary>A quarter turn moves the doorway with the building, in the direction the renderer turns it.</summary>
    /// <remarks>
    /// <b>This is the case that fails silently if the two sides disagree.</b> The renderer applies the replicated
    /// quarter as Babylon's <c>rotation.y</c>, which sends <c>(x, z)</c> to <c>(z, −x)</c>; collision has to send it
    /// the same way or the wall you cannot walk through is a quarter turn from the wall you can see.
    /// </remarks>
    [Test]
    public void AQuarterTurnTurnsTheDoorwayWithTheBuilding()
    {
        var walls = Doorway();
        const float half = TatooineData.PlayerBodyHalfM;

        // Turned a quarter, the frontage runs along Z and the opening is at z = 0 on the x = 0 line.
        var x = 0f;
        var z = 0f;
        Assert.That(SimBridge.PushOutOfShapesForTest(walls, 0f, 0f, 1, half, 8192f, ref x, ref z), Is.False,
            "the opening is still an opening after the building turns");

        // What was wall at x = -2 is now wall at z = +2.
        x = 0f;
        z = 2f;
        Assert.That(SimBridge.PushOutOfShapesForTest(walls, 0f, 0f, 1, half, 8192f, ref x, ref z), Is.True,
            "the wall turned with it");

        // And the box maths agree with the renderer's rotation, corner for corner.
        var turned = SimBridge.TurnForTest(1, walls[0]);
        Assert.Multiple(() =>
        {
            Assert.That(turned.MinX, Is.EqualTo(-0.2f).Within(1e-5f));
            Assert.That(turned.MaxX, Is.EqualTo(0.2f).Within(1e-5f));
            Assert.That(turned.MinZ, Is.EqualTo(0.6f).Within(1e-5f));
            Assert.That(turned.MaxZ, Is.EqualTo(4.6f).Within(1e-5f));
        });
    }

    /// <summary>Four quarters bring a box back to itself, which no amount of sign juggling survives by accident.</summary>
    [Test]
    public void FourQuartersAreTheIdentity()
    {
        var box = new AssetColliders.PlanBox(-4.6f, -0.2f, -0.6f, 0.2f);
        var once = SimBridge.TurnForTest(1, box);
        var twice = SimBridge.TurnForTest(1, new AssetColliders.PlanBox(once.MinX, once.MinZ, once.MaxX, once.MaxZ));
        var thrice = SimBridge.TurnForTest(1, new AssetColliders.PlanBox(twice.MinX, twice.MinZ, twice.MaxX, twice.MaxZ));
        var full = SimBridge.TurnForTest(1, new AssetColliders.PlanBox(thrice.MinX, thrice.MinZ, thrice.MaxX, thrice.MaxZ));

        Assert.Multiple(() =>
        {
            Assert.That(full.MinX, Is.EqualTo(box.MinX).Within(1e-5f));
            Assert.That(full.MinZ, Is.EqualTo(box.MinZ).Within(1e-5f));
            Assert.That(full.MaxX, Is.EqualTo(box.MaxX).Within(1e-5f));
            Assert.That(full.MaxZ, Is.EqualTo(box.MaxZ).Within(1e-5f));

            // And a single quarter is NOT the identity, or the test above would pass on a no-op.
            Assert.That(once.MinX, Is.Not.EqualTo(box.MinX).Within(1e-5f));
        });
    }

    /// <summary>
    /// Every collider is kept, with the vertical span that later decides whether it is a floor, a step or a wall.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This used to assert that two of these four were DROPPED, and that was the defect.</b> The rule was a fixed
    /// height above the ground: a collider that did not straddle 1.6 m was discarded at load as furniture. It threw
    /// away every floor in the world — which is why a body walked at desert level through a building standing on a
    /// deck — and it threw away everything resting on such a deck as something to walk under.
    /// </para>
    /// <para>
    /// Nothing can decide that here, because the answer depends on where the feet are: the deck is a floor to a body
    /// on the plaza climbing its stairs and a wall to one walking into its side. So the loader keeps the span and the
    /// contact decides, which is what the testbench's floating capsule does.
    /// </para>
    /// </remarks>
    [Test]
    public void EveryColliderIsKeptWithItsVerticalSpan()
    {
        const string json = """
            {"schema":1,"collision":{"band_m":2.0,
              "shapes":[{"type":"box","half":[3.1,1.0,3.65]},{"type":"box","half":[1.9,0.15,2.15]},
                        {"type":"box","half":[2.0,0.12,0.5]},{"type":"cylinder","radius":1.2,"half_height":1.0}],
              "colliders":[
                {"shape":0,"name":"Body","pos":[0,1.6,0],"rot":[0,0,0,1]},
                {"shape":1,"name":"Steps","pos":[0,0.6,5],"rot":[0,0,0,1]},
                {"shape":2,"name":"Lintel","pos":[0,2.58,3],"rot":[0,0,0,1]},
                {"shape":3,"name":"Tower","pos":[4,1.7,0],"rot":[0,0,0,1]}]}}
            """;

        var boxes = AssetColliders.ReadBlocking(json);
        Assert.That(boxes, Has.Length.EqualTo(4), "a collider was dropped at load, where nothing yet knows what it is");

        Assert.Multiple(() =>
        {
            // Body 0.6..2.6, Steps 0.45..0.75, Lintel 2.46..2.70, Tower 0.7..2.7 — the spans the contact rule reads.
            Assert.That(boxes[0].MinY, Is.EqualTo(0.6f).Within(1e-3f), "the body's underside");
            Assert.That(boxes[0].MaxY, Is.EqualTo(2.6f).Within(1e-3f), "the body's top");
            Assert.That(boxes[1].MaxY, Is.EqualTo(0.75f).Within(1e-3f), "the steps are a step, and a step has a top");
            Assert.That(boxes[2].MinY, Is.EqualTo(2.46f).Within(1e-3f), "the lintel starts above a head");
        });

        // Mirrored in X, so the tower at x = +4 stands at x = -4 in the world.
        var tower = boxes[3];
        Assert.Multiple(() =>
        {
            Assert.That((tower.MinX + tower.MaxX) / 2f, Is.EqualTo(-4f).Within(1e-4f), "the glTF frame's X mirror");
            Assert.That(tower.MaxX - tower.MinX, Is.EqualTo(2.4f).Within(1e-4f), "a cylinder is as wide as its diameter");
        });
    }

    /// <summary>A rotated primitive keeps a box that contains it, rather than one that leaks.</summary>
    [Test]
    public void ARotatedPrimitiveIsContained()
    {
        // A 2 x 0.2 x 0.2 bar turned 45 degrees about Y: its plan box must grow to about 1.56 on each axis.
        var s = (float)Math.Sin(Math.PI / 8);
        var c = (float)Math.Cos(Math.PI / 8);
        var json = "{\"schema\":1,\"collision\":{\"shapes\":[{\"type\":\"box\",\"half\":[2.0,1.0,0.2]}],\"colliders\":[{\"shape\":0,"
            + "\"name\":\"Bar\",\"pos\":[0,1.0,0],\"rot\":[0," + s.ToString(System.Globalization.CultureInfo.InvariantCulture)
            + ",0," + c.ToString(System.Globalization.CultureInfo.InvariantCulture) + "]}]}}";

        var boxes = AssetColliders.ReadBlocking(json);
        Assert.That(boxes, Has.Length.EqualTo(1));
        var width = boxes[0].MaxX - boxes[0].MinX;

        // The quaternion carries the HALF angle, so this is a 45 degree turn and the plan box grows by
        // 2 x (2.0 cos45 + 0.2 sin45). Reading the rotation's cosine off the quaternion's own components is the mistake
        // this line exists to not make twice.
        var cos45 = (float)Math.Cos(Math.PI / 4);
        var sin45 = (float)Math.Sin(Math.PI / 4);
        Assert.That(width, Is.EqualTo(2f * ((2.0f * cos45) + (0.2f * sin45))).Within(1e-3f), "|R| applied to the half extents");
        Assert.That(width, Is.GreaterThan(4f * 0.2f), "and it is not the unrotated box");
    }

    /// <summary>
    /// The gate's growth is what an asset sticks out past its OWN reservation, not how big the asset is.
    /// </summary>
    /// <remarks>
    /// <b>This is the regression, and the number it guards is the difference between a gate and no gate.</b> The figure
    /// was briefly the absolute distance from an asset's origin to its furthest primitive. `tat_shuttleport` reaches
    /// 23 m, so every footprint in the world grew by 23 m, every body in a city passed the gate for several structures
    /// at once, and each of those cost a <c>TryOpen</c> and two component reads — on the one path built to avoid them.
    /// The shuttleport's reservation is 23.1 m, so it overhangs nothing and must contribute nothing.
    /// </remarks>
    [Test]
    public void TheGateGrowsByTheOverhangAndNotByTheAssetsSize()
    {
        var byIndex = new AssetColliders.PlanBox[StructureAssets.Table.Length][];
        for (var i = 0; i < byIndex.Length; i++)
        {
            byIndex[i] = [];
        }

        // Index 2 is the shuttleport: a 46 m square asset inside a 46.2 m reservation.
        byIndex[2] = [new AssetColliders.PlanBox(-23f, -23f, 23f, 23f)];
        Assert.That(AssetColliders.OverhangOf(byIndex), Is.Zero, "an asset that fits its reservation asks for no growth");

        // Index 0 is the building: `tat_house_large` is 18.8 m across against an 18 m reservation.
        byIndex[0] = [new AssetColliders.PlanBox(-9.4f, -9.1f, 9.4f, 9.1f)];
        Assert.That(AssetColliders.OverhangOf(byIndex), Is.EqualTo(0.4f).Within(1e-3f), "the house's own overhang, and not the shuttleport's size");
    }

    /// <summary>
    /// <b>A structure is charged its OWN asset's overhang, not the worst asset's.</b>
    /// </summary>
    /// <remarks>
    /// <para>
    /// The gate and the disc both grow by how far an asset can reach past the ground its kind reserved. Taking one
    /// maximum over every asset made a terminal — 1.6 m across, overhanging by nothing — behave like a house with an
    /// entry ramp: its gate grew by the ramp's 2.25 m, and the disc to it was charged 2.25 m it did not owe, so every
    /// body walking past a terminal refreshed sooner than the geometry required.
    /// </para>
    /// <para>
    /// <b>Falsifiable.</b> Every assertion below that expects zero expected 0.4 before the table existed, which is what
    /// <see cref="AssetColliders.MaxOverhangM"/> still reports for the same input — asserted here, so the two cannot
    /// quietly become the same number again.
    /// </para>
    /// </remarks>
    [Test]
    public void AStructureIsChargedItsOwnAssetsOverhangAndNotTheWorstAssets()
    {
        var byIndex = Empty();

        // The building, 18.8 m across against an 18 m reservation: 0.4 m of overhang.
        byIndex[0] = [new AssetColliders.PlanBox(-9.4f, -9.1f, 9.4f, 9.1f)];
        // The shuttleport, 46 m inside a 46.2 m reservation, and a terminal 1.2 m inside a 1.6 m one: neither overhangs.
        byIndex[2] = [new AssetColliders.PlanBox(-23f, -23f, 23f, 23f)];
        byIndex[3] = [new AssetColliders.PlanBox(-0.6f, -0.6f, 0.6f, 0.6f)];

        var colliders = AssetColliders.FromBoxesForTest(byIndex);

        Assert.Multiple(() =>
        {
            Assert.That(colliders.MaxOverhangM, Is.EqualTo(0.4f).Within(1e-3f), "the premise: one asset in this set does overhang");
            Assert.That(colliders.OverhangFor(9f), Is.EqualTo(0.4f).Within(1e-3f), "the building must still be charged its own reach");
            Assert.That(colliders.OverhangFor(0.8f), Is.Zero, "a terminal overhangs nothing, and was charged the house's 0.4 m");
            Assert.That(colliders.OverhangFor(23.1f), Is.Zero, "the shuttleport fits its reservation, and was charged the house's 0.4 m");
        });
    }

    /// <summary>
    /// <b>An asset that publishes no colliders overhangs by nothing, which is a measurement and not an absence.</b>
    /// </summary>
    /// <remarks>
    /// <para>
    /// The three terminals declare no collision at all, and a structure with no collider geometry is collided as its
    /// footprint box — which reaches exactly as far as itself. Treating "no boxes" as "nothing known" instead sent every
    /// terminal to the worst case, and the terminals are the one kind the per-reservation table was built to stop
    /// paying the house ramps' 2.25 m. Measured against the real catalogue, that bug left the published table as
    /// <c>18.0 m +2.25, 14.0 m +1.90, 46.2 m +1.49</c> with the 1.6 m reservation missing entirely.
    /// </para>
    /// <para>
    /// A reservation no asset NAMES is different and still falls back — see
    /// <see cref="AReservationNoAssetStandsOnIsChargedTheWorstCase"/>. The distinction is whether an asset was looked at.
    /// </para>
    /// </remarks>
    [Test]
    public void AnAssetThatPublishesNoCollidersOverhangsByNothing()
    {
        var byIndex = Empty();

        // The building overhangs, so there is a worst case to be wrongly charged. Indices 3 to 5 are the terminals and
        // are left exactly as the catalogue leaves them: empty.
        byIndex[0] = [new AssetColliders.PlanBox(-9.4f, -9.1f, 9.4f, 9.1f)];
        var colliders = AssetColliders.FromBoxesForTest(byIndex);

        Assert.Multiple(() =>
        {
            Assert.That(colliders.MaxOverhangM, Is.EqualTo(0.4f).Within(1e-3f), "the premise: something in this set overhangs");
            Assert.That(colliders.OverhangFor(StructureFootprint.HalfExtentM(StructureKind.CraftingStation)), Is.Zero,
                "a terminal publishes no collider, so it reaches no further than its own footprint");
        });
    }

    /// <summary>
    /// A reservation size no asset stands on is charged the worst case, which is what the whole world used to pay.
    /// </summary>
    /// <remarks>
    /// Six of the eight kinds publish no asset, so a body near one of those meets a box whose size the table does not
    /// know. Answering zero there would be a guess about geometry nothing has measured; answering the maximum is the
    /// behaviour that was correct before the table existed, so an unknown size is no worse than it was.
    /// </remarks>
    [Test]
    public void AReservationNoAssetStandsOnIsChargedTheWorstCase()
    {
        var byIndex = Empty();
        byIndex[0] = [new AssetColliders.PlanBox(-9.4f, -9.1f, 9.4f, 9.1f)];
        var colliders = AssetColliders.FromBoxesForTest(byIndex);

        // A factory reserves 11 m and nothing published stands on it.
        Assert.That(colliders.OverhangFor(StructureFootprint.HalfExtentM(StructureKind.Factory)),
            Is.EqualTo(colliders.MaxOverhangM).Within(1e-6f));
    }

    /// <summary>
    /// <b>No two reservations are close enough to be confused</b>, which is what makes the size a sound key.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The size is measured back out of a broad-phase box rather than read: the index holds <c>centre ± half</c> as two
    /// floats, so a structure 8 km from the origin returns a half-extent a fraction of a millimetre off the constant
    /// that produced it, and the lookup has to match within
    /// <see cref="AssetColliders.HalfExtentToleranceM"/>. If two kinds' reservations were within that of each other, a
    /// structure could be charged the other kind's overhang — and charging too little is a body entering a wall, not a
    /// slower gate. So the premise is pinned rather than assumed.
    /// </para>
    /// <para>
    /// Over every kind, not only the ones with assets, because the fallback for an unknown size is the maximum and a
    /// kind that gains an asset later must not silently collide with one that already has one.
    /// </para>
    /// </remarks>
    [Test]
    public void NoTwoReservationsAreCloseEnoughToBeConfused()
    {
        var worst = float.MaxValue;
        var pair = (0, 0);
        for (var i = 0; i < AllKinds.Length; i++)
        {
            for (var j = i + 1; j < AllKinds.Length; j++)
            {
                var gap = MathF.Abs(StructureFootprint.HalfExtentM(AllKinds[i]) - StructureFootprint.HalfExtentM(AllKinds[j]));
                if (gap < worst)
                {
                    worst = gap;
                    pair = (AllKinds[i], AllKinds[j]);
                }
            }
        }

        TestContext.Out.WriteLine($"closest reservations: kinds {pair.Item1} and {pair.Item2}, {worst:F2} m apart, "
            + $"against a {AssetColliders.HalfExtentToleranceM:F2} m tolerance");
        Assert.That(worst, Is.GreaterThan(2f * AssetColliders.HalfExtentToleranceM),
            "two kinds reserve almost the same square, so a structure of one could be charged the other's overhang");
    }

    /// <summary>
    /// A half-extent recovered from a box at the planet's edge still names its own kind, rounding and all.
    /// </summary>
    /// <remarks>
    /// This is the case the tolerance exists for, and the one an exact comparison would fail: the planet is 16 km across
    /// and <c>centre ± half</c> at that range loses the low bits of a 0.8 m half-extent. Asserted at the edge rather
    /// than at a round number, because the error grows with the coordinate.
    /// </remarks>
    [Test]
    public void AHalfExtentRecoveredFromAFarAwayBoxStillNamesItsKind()
    {
        var byIndex = Empty();
        byIndex[0] = [new AssetColliders.PlanBox(-9.4f, -9.1f, 9.4f, 9.1f)];
        byIndex[3] = [new AssetColliders.PlanBox(-0.6f, -0.6f, 0.6f, 0.6f)];
        var colliders = AssetColliders.FromBoxesForTest(byIndex);

        const float Edge = TatooineData.PlanetHalfExtentM;
        foreach (var kind in new[] { StructureKind.CraftingStation, StructureKind.Building })
        {
            var half = StructureFootprint.HalfExtentM(kind);
            // Exactly how the world stores it and how the broad phase reads it back.
            var recovered = ((Edge + half) - (Edge - half)) * 0.5f;
            Assert.That(colliders.OverhangFor(recovered), Is.EqualTo(colliders.OverhangFor(half)).Within(1e-6f),
                $"kind {kind} at the planet's edge recovered {recovered:R} from a {half:R} reservation and was charged a different overhang");
        }
    }

    /// <summary>Every structure kind, so a premise is pinned over the table rather than over the assets that exist today.</summary>
    private static readonly int[] AllKinds =
    [
        StructureKind.Building, StructureKind.CraftingStation, StructureKind.Shuttleport, StructureKind.PoiProp,
        StructureKind.PlayerHouse, StructureKind.Factory, StructureKind.Harvester, StructureKind.CampObject, StructureKind.Bazaar,
        StructureKind.MissionTerminal,
    ];

    /// <summary>A collider table with a slot per published asset and nothing in any of them.</summary>
    private static AssetColliders.PlanBox[][] Empty()
    {
        var byIndex = new AssetColliders.PlanBox[StructureAssets.Table.Length][];
        for (var i = 0; i < byIndex.Length; i++)
        {
            byIndex[i] = [];
        }

        return byIndex;
    }

    /// <summary>
    /// <see cref="StructureAssets.KindOf"/> is the inverse of <see cref="StructureAssets.Pick"/>, for every asset.
    /// </summary>
    /// <remarks>
    /// The overhang above is measured against the footprint of the kind an asset belongs to, so the two tables being
    /// out of step would reserve the wrong ground for an asset silently. Driven over many seeds because
    /// <c>Pick</c> draws from an rng for the terminals.
    /// </remarks>
    [Test]
    public void EveryAssetIsPickedOnlyByTheKindItNames()
    {
        Assert.That(StructureAssets.KindOf, Has.Length.EqualTo(StructureAssets.Table.Length), "an asset was appended to one table and not the other");

        var seen = new bool[StructureAssets.Table.Length];
        int[] kinds =
        [
            StructureKind.Building, StructureKind.CraftingStation, StructureKind.Shuttleport, StructureKind.PoiProp,
            StructureKind.PlayerHouse, StructureKind.Factory, StructureKind.Harvester, StructureKind.CampObject, StructureKind.Bazaar,
            StructureKind.MissionTerminal,
        ];

        var rng = new Rng(20261022u);
        for (var seed = 0; seed < 256; seed++)
        {
            foreach (var kind in kinds)
            {
                var index = StructureAssets.Pick(kind, ref rng);
                if (index == StructureAssets.NoAsset)
                {
                    continue;
                }

                seen[index] = true;
                Assert.That(StructureAssets.KindOf[index], Is.EqualTo(kind),
                    $"asset {StructureAssets.Table[index]} is picked by kind {kind} but KindOf names {StructureAssets.KindOf[index]}");
            }
        }

        for (var index = 0; index < seen.Length; index++)
        {
            // A room is never picked by kind: it is the asset its building's door names, and it stands in a realm of its own.
            if (StructureAssets.KindOf[index] == StructureKind.Interior)
            {
                Assert.That(seen[index], Is.False, $"{StructureAssets.Table[index]} is a room and was picked by a kind");
                continue;
            }

            Assert.That(seen[index], Is.True, $"no kind ever picks {StructureAssets.Table[index]}, so its KindOf entry is unchecked");
        }
    }

    /// <summary>
    /// A served world carries its collider table on the bridge that collides with it.
    /// </summary>
    /// <remarks>
    /// <b>The one wire the rest of this fixture cannot see, and it was broken.</b> Everything else here is arithmetic
    /// on boxes handed in by hand, so the whole feature passed while the served path threw a
    /// <see cref="System.NullReferenceException"/> at boot on every run with <c>--assets</c> — the only runs collision
    /// is enabled on — because the shapes were assigned to a bridge <c>StartReplication</c> had not built yet. The
    /// property is now <see langword="init"/> and arrives through the constructor, so there is no ordering left to get
    /// wrong; this pins that it arrives at all, and that a run without an asset set still gets none.
    /// </remarks>
    [Test]
    [Category("Integration")]
    public void AServedWorldHandsItsCollidersToTheBridge()
    {
        var dir = Worlds.NewDirectory();
        try
        {
            var config = Worlds.Small(dir);
            config.WarmTicks = 0;
            config.MeasuredTicks = 0;

            using (var bare = new TatooineSim(config))
            {
                bare.Initialize();
                bare.StartReplication();
                Assert.That(bare.Bridge.AssetShapes, Is.Null, "a run without --assets must collide as footprints, as it always did");
            }
        }
        finally
        {
            Worlds.Delete(dir);
        }

        var second = Worlds.NewDirectory();
        try
        {
            var config = Worlds.Small(second);
            config.WarmTicks = 0;
            config.MeasuredTicks = 0;

            using var sim = new TatooineSim(config);
            sim.Initialize();

            // A table with one asset's boxes in it, which is all this asserts about: that what the served path loads is
            // what the tick collides against.
            var byIndex = new AssetColliders.PlanBox[StructureAssets.Table.Length][];
            for (var i = 0; i < byIndex.Length; i++)
            {
                byIndex[i] = [];
            }

            byIndex[0] = Doorway();
            var shapes = AssetColliders.FromBoxesForTest(byIndex);
            sim.StartReplication(shapes);
            Assert.That(sim.Bridge.AssetShapes, Is.SameAs(shapes), "the table the server read never reached the collision path");
        }
        finally
        {
            Worlds.Delete(second);
        }
    }
}
