using System;
using NUnit.Framework;
using SwgTatooine.Server;
using Typhon.Engine;

namespace SwgTatooine.Tests;

/// <summary>
/// A round collider is round, and the ten numbers the server publishes are the ten the client reads.
/// </summary>
/// <remarks>
/// <para>
/// <b>Two defects, both of which read as "collision is broken" and neither of which fails anything.</b> A cylinder was
/// loaded as the square around it, so <c>tat_shuttleport</c>'s 21.6 m plaza became a 43.2 m square whose corners stood
/// 8.9 m out in open desert — a 1.3 m slab with a flat top, which is a wall from the ground beside it. Measured before
/// the fix: a body walking in along the diagonal was pushed back to a radius of 25.7 to 30.5 m and could not reach the
/// plaza at all. And the wire grew a ninth number (the top PLANE) that the client's parser did not know about, so it
/// read the plane as <c>slopeX</c> and <c>slopeX</c> as <c>slopeZ</c>: every ramp predicted as a flat slab at its
/// bounding box's top, which is a wall three steps tall, and a body stuck at the foot of one.
/// </para>
/// <para>
/// The client half of these cases is <c>structure-collision.test.ts</c> and <c>server-config.test.ts</c>, asserting the
/// same numbers — because the whole value of client-side collision is that the two sides agree.
/// </para>
/// </remarks>
[TestFixture]
public sealed class RoundColliderChecks
{
    /// <summary>The body's half-extent, the world's own constant.</summary>
    private const float Half = TatooineData.PlayerBodyHalfM;

    /// <summary>The shuttleport's plaza, as it is published: a 21.6 m cylinder 1.3 m thick, its top 0.3 m up.</summary>
    private const string Plaza = """
        {"schema":1,"collision":{"shapes":[{"type":"cylinder","radius":21.6,"half_height":0.65}],
          "colliders":[{"shape":0,"name":"Plaza","pos":[0,-0.35,0],"rot":[0,0,0,1]}]}}
        """;

    [Test]
    public void ACylindersPlanIsItsCircleAndItsBoxIsTheSquareAroundIt()
    {
        var boxes = AssetColliders.ReadBlocking(Plaza);
        Assert.That(boxes, Has.Length.EqualTo(1));

        var plaza = boxes[0];
        Assert.Multiple(() =>
        {
            Assert.That(plaza.Radius, Is.EqualTo(21.6f).Within(1e-4f), "the plan radius, which is what the narrow phase uses");
            Assert.That(plaza.MaxX - plaza.MinX, Is.EqualTo(43.2f).Within(1e-4f),
                "the AABB stays the CIRCUMSCRIBED square: the broad gate has to be conservative, and only the narrow phase is round");
            Assert.That(plaza.MaxY, Is.EqualTo(0.3f).Within(1e-4f), "its top, which a body stands on");
        });
    }

    /// <summary>
    /// <b>The corner of the square is open desert, and a body walks through it.</b>
    /// </summary>
    /// <remarks>
    /// The failing number, kept: the diagonal of a 21.6 m circle reaches (15.27, 15.27), so a point at radius 22 on
    /// that diagonal is 15.56 out on each axis — comfortably inside the 21.6 square, and 0.4 m outside the circle.
    /// Treated as a square the body was ejected; treated as a circle it is not touched at all.
    /// </remarks>
    [Test]
    public void ABodyCrossingTheSquaresCornerIsNotTouched()
    {
        var shapes = Resolver(Plaza, out var boxes);

        var d = 22f / MathF.Sqrt(2f);
        var x = d;
        var z = d;
        Assert.That(MathF.Max(MathF.Abs(x), MathF.Abs(z)), Is.LessThan(21.6f),
            "precondition: this point is INSIDE the bounding square, which is why the bug reached it");

        // Feet on the desert beside the plaza, where its 0.3 m top is a wall rather than a step.
        var moved = SimBridge.PushOut(boxes, Half, 8192f, ref x, ref z, out _, ref shapes, -0.4f, d + 0.7f, d + 0.7f);
        Assert.Multiple(() =>
        {
            Assert.That(moved, Is.False, "the body was ejected from a corner where nothing is drawn");
            Assert.That(MathF.Sqrt((x * x) + (z * z)), Is.EqualTo(22f).Within(1e-3f), "and it is still where it walked to");
        });
    }

    [Test]
    public void ABodyWalkingIntoTheCircleStopsAtItsRealEdge()
    {
        var shapes = Resolver(Plaza, out var boxes);

        // Straight at the centre along +X, from outside.
        var x = 21.0f;
        var z = 0f;
        var moved = SimBridge.PushOut(boxes, Half, 8192f, ref x, ref z, out _, ref shapes, -0.4f, 22.0f, 0f);
        Assert.Multiple(() =>
        {
            Assert.That(moved, Is.True, "the plaza's own edge is a wall from the desert beside it, and was not felt");
            Assert.That(x, Is.EqualTo(21.6f + Half).Within(1e-3f), "stopped a body's half-extent off the circle, not off the square");
            Assert.That(z, Is.EqualTo(0f).Within(1e-3f), "and not shoved along the tangent");
        });
    }

    /// <summary>A body pushed off a circle leaves along its radius, not along whichever axis is shallower.</summary>
    /// <remarks>
    /// The axis-of-least-penetration rule is right for a box and wrong for a circle: near the middle of a 43.2 m square
    /// the shallowest axis is twenty metres away, so a body a centimetre inside the plaza's rim would be carried across
    /// the whole plaza to escape it. Radially it travels a centimetre.
    /// </remarks>
    [Test]
    public void ABodyJustInsideTheRimLeavesByTheNearestWay()
    {
        var shapes = Resolver(Plaza, out var boxes);

        var x = 21.5f;
        var z = 0f;
        SimBridge.PushOut(boxes, Half, 8192f, ref x, ref z, out _, ref shapes, -0.4f);
        Assert.That(MathF.Sqrt((x * x) + (z * z)), Is.EqualTo(21.6f + Half).Within(1e-3f));
        Assert.That(x - 21.5f, Is.LessThan(0.5f), "it was carried across the plaza instead of stepped off its rim");
    }

    /// <summary>The top of a round floor carries the feet over the circle, and over nothing else.</summary>
    [Test]
    public void ARoundFloorIsStoodOnWithinItsCircleOnly()
    {
        var shapes = Resolver(Plaza, out var boxes);

        // On the plaza, feet already at its top: the ground rule must keep them there.
        var on = SimBridge.GroundOnShapes(boxes, Half, 0f, 0f, -0.4f, 0.3f, ref shapes);

        // Out at the square's corner, where there is no plaza: the terrain stands.
        var d = 22f / MathF.Sqrt(2f);
        var off = SimBridge.GroundOnShapes(boxes, Half, d, d, -0.4f, 0.3f, ref shapes);

        Assert.Multiple(() =>
        {
            Assert.That(on, Is.EqualTo(0.3f).Within(1e-4f), "the plaza is a floor");
            Assert.That(off, Is.EqualTo(-0.4f).Within(1e-4f), "and the corner of its bounding square is not");
        });
    }

    /// <summary>
    /// <b>The wire carries ten numbers, and the plane is the seventh.</b>
    /// </summary>
    /// <remarks>
    /// This is the shape of the defect that cost a whole evening: the server added a number in the middle and the
    /// client's parser, written for the shorter box, read everything after it one place out. Nothing threw — the ramps
    /// simply went flat. Pinning the ORDER here, on this side, is what makes the client's own parser test meaningful.
    /// </remarks>
    [Test]
    public void TheWireCarriesTenNumbersInThisOrder()
    {
        var ramp = AssetColliders.ReadBlocking("""
            {"schema":1,"collision":{"shapes":[{"type":"box","half":[1.7,0.15,2.2887]}],
              "colliders":[{"shape":0,"name":"EntryRamp","pos":[1.5,-0.28927,9.06929],"rot":[0.189108,0,0,0.981956]}]}}
            """);

        Assert.That(ramp, Has.Length.EqualTo(1));
        var b = ramp[0];
        Assert.Multiple(() =>
        {
            Assert.That(b.SlopeZ, Is.EqualTo(-0.4005f).Within(1e-3f), "a 21.8 degree tilt about X is a gradient in Z");
            Assert.That(b.SlopeX, Is.EqualTo(0f).Within(1e-4f), "and none in X");
            Assert.That(b.PlaneY, Is.EqualTo(-0.1275f).Within(1e-3f), "the top plane at the box's centre");
            Assert.That(b.MaxY, Is.EqualTo(0.6998f).Within(1e-3f), "which is NOT the bounding box's top, 0.83 m above it");
            Assert.That(b.Radius, Is.EqualTo(0f), "a box has no plan radius");
            Assert.That(b.Walkable, Is.True, "and the published entry ramp is walkable, which is the whole point");
        });

        // The two readings a parser can confuse, 0.83 m apart on this collider: if the client takes MaxY for the plane
        // the ramp is a flat slab 0.70 m up, which is a wall, and if it takes the plane for a slope it is flat as well.
        Assert.That(b.MaxY - b.PlaneY, Is.GreaterThan(0.8f), "the two numbers are far enough apart that confusing them cannot pass unnoticed");
    }

    /// <summary>A tilted cylinder keeps its bounding box, because its plan is an ellipse and a circle would be too small.</summary>
    [Test]
    public void ATiltedCylinderIsNotTreatedAsRound()
    {
        var s = MathF.Sin(MathF.PI / 8f);
        var c = MathF.Cos(MathF.PI / 8f);
        var json = "{\"schema\":1,\"collision\":{\"shapes\":[{\"type\":\"cylinder\",\"radius\":1.0,\"half_height\":3.0}],"
            + "\"colliders\":[{\"shape\":0,\"name\":\"FallenPillar\",\"pos\":[0,1,0],\"rot\":["
            + s.ToString(System.Globalization.CultureInfo.InvariantCulture) + ",0,0,"
            + c.ToString(System.Globalization.CultureInfo.InvariantCulture) + "]}]}}";

        var boxes = AssetColliders.ReadBlocking(json);
        Assert.That(boxes, Has.Length.EqualTo(1));
        Assert.That(boxes[0].Radius, Is.EqualTo(0f),
            "a 45 degree cylinder is 6 m long in plan; calling it a 1 m circle would let a body walk through five of them");
    }

    /// <summary>
    /// A descriptor whose schema this build does not know is not read at all.
    /// </summary>
    /// <remarks>
    /// Rule 1 of <c>ASSET_FORMAT.md</c>. The schema moves only when a field is removed, renamed or changes MEANING, so
    /// a reader that parsed one anyway would be applying today's meanings to tomorrow's numbers — a body pushed out of
    /// walls that are not where it thinks. Keeping the footprint box is the answer every other unreadable descriptor
    /// gets here, and it is what every structure had before the asset pipeline existed.
    /// </remarks>
    [Test]
    public void ADescriptorFromASchemaThisBuildDoesNotKnow_IsNotRead()
    {
        const string Body = """
            "collision":{"shapes":[{"type":"box","half":[1,1,1]}],"colliders":[{"shape":0,"name":"Wall","pos":[0,1,0]}]}
            """;

        Assert.Multiple(() =>
        {
            Assert.That(AssetColliders.ReadBlocking($"{{\"schema\":1,{Body}}}"), Has.Length.EqualTo(1), "the schema this build reads");
            Assert.That(AssetColliders.ReadBlocking($"{{\"schema\":2,{Body}}}"), Is.Empty, "a newer schema was parsed with this build's meanings");
            Assert.That(AssetColliders.ReadBlocking($"{{{Body}}}"), Is.Empty, "a descriptor announcing no schema at all was read anyway");
        });
    }

    /// <summary>
    /// A shape type this build does not know is skipped, not turned into a box of nothing.
    /// </summary>
    /// <remarks>
    /// Rule 3. The unknown case used to fall through to half extents of zero, and a zero box is not "no collider" — it
    /// is a degenerate box AT A POSITION, which a body can be exactly inside and which the push-out can never resolve,
    /// because every direction out of it is zero metres long.
    /// </remarks>
    [Test]
    public void AShapeTypeThisBuildDoesNotKnow_IsSkipped()
    {
        var boxes = AssetColliders.ReadBlocking("""
            {"schema":1,"collision":{"shapes":[{"type":"convex_hull","points":[[0,0,0]]},{"type":"box","half":[1,1,1]}],
              "colliders":[{"shape":0,"name":"Rock","pos":[5,1,5]},{"shape":1,"name":"Wall","pos":[0,1,0]}]}}
            """);

        Assert.That(boxes, Has.Length.EqualTo(1), "the unknown shape was kept, or the known one was dropped with it");
        Assert.That((boxes[0].MinX + boxes[0].MaxX) / 2f, Is.EqualTo(0f).Within(1e-4f), "the surviving box is the wall, not the rock");
    }

    /// <summary>A portal whose forward axis this build cannot read is no portal.</summary>
    /// <remarks>
    /// Rule 3 again, and the dangerous case: every portal published today says "-Z", so a default of -Z would be right
    /// for the whole pack and silently wrong for the first asset that ever differs.
    /// </remarks>
    [Test]
    public void APortalWhoseForwardAxisCannotBeRead_IsNotADoor()
    {
        static string Portal(string forward) => """
            {"schema":1,"portals":[{"id":"front","center":[1.5,2.2,5.45],"rot":[0,0,0,1],"half":[0.95,1.5,0.4],
              "forward":"AXIS","target":"room","target_spawn":"entry","exit_spawn":"front"}],
             "spawns":[{"id":"front","pos":[1.5,0.7,6.3],"rot":[0,0,0,1],"forward":"-Z"}]}
            """.Replace("AXIS", forward, System.StringComparison.Ordinal);

        Assert.Multiple(() =>
        {
            Assert.That(AssetColliders.ReadDoor(Portal("-Z")).Exists, Is.True, "the axis every portal in the pack declares");
            Assert.That(AssetColliders.ReadDoor(Portal("+W")).Exists, Is.False, "an unreadable axis became -Z, which happens to be right today");
        });
    }

    /// <summary>A resolver over one asset's published colliders, with the footprint the broad phase would hand over.</summary>
    /// <param name="json">The asset descriptor.</param>
    /// <param name="boxes">The broad-phase box, sized to contain the asset.</param>
    /// <returns>The resolver.</returns>
    private static StructureShapes Resolver(string json, out ProximityBox[] boxes)
    {
        var port = EntityId.FromRawValue(77);
        var prims = AssetColliders.ReadBlocking(json);

        var minX = float.MaxValue;
        var minZ = float.MaxValue;
        var maxX = float.MinValue;
        var maxZ = float.MinValue;
        foreach (var p in prims)
        {
            minX = MathF.Min(minX, p.MinX);
            minZ = MathF.Min(minZ, p.MinZ);
            maxX = MathF.Max(maxX, p.MaxX);
            maxZ = MathF.Max(maxZ, p.MaxZ);
        }

        boxes = [new(minX, minZ, maxX, maxZ, 0f, port)];
        return StructureShapes.ForTest(0f, port, StructureShapes.Shape.Primitives, prims, 0f, 0f, 0, 0f);
    }
}
