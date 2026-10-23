using NUnit.Framework;

namespace SwgTatooine.Tests;

/// <summary>
/// The rule that decides a body has gone through a doorway.
/// </summary>
/// <remarks>
/// <para>
/// <b>Written against the figures the pack actually publishes</b> (catalogue <c>cdab4caba0cf</c>), because every trap
/// here is a matter of scale. A doorway is 1.9 m wide and 0.8 m deep and the tick is 10 Hz; an opening that sounds
/// generous is three quarters of one stride.
/// </para>
/// <para>
/// <b>Why a plane and not the box.</b> The obvious rule — "fire while the body is inside the portal box" — is what the
/// asset testbench uses and it cannot work here. The testbench samples every simulation step; this server samples ten
/// times a second, and the gait ladder runs 0.50 m per tick at a run, 0.91 m at a burst run and 1.20 m on a mount. A
/// body in a hurry is in front of a 0.8 m opening on one tick and behind it on the next, having never been inside it.
/// The door would have worked for a walker and failed, silently, for everybody else. The physics session caught this
/// before it shipped.
/// </para>
/// </remarks>
[TestFixture]
public sealed class PortalCrossingChecks
{
    /// <summary>`tat_house_large`'s front door, in the asset's own frame: centre z 5.45, half 0.95 across, forward -Z.</summary>
    private const float DoorX = 1.5f;
    private const float DoorZ = 5.45f;
    private const float HalfX = 0.95f;

    /// <summary>Walking in, along the door's forward.</summary>
    private static bool Crossed(float fromZ, float toZ, float x = DoorX) =>
        SimBridge.CrossedDoorForTest(DoorX, DoorZ, 0f, -1f, HalfX, x, fromZ, x, toZ);

    [Test]
    public void AStepThroughTheDoorway_Crosses()
    {
        Assert.That(Crossed(6.0f, 5.0f), Is.True, "a stride that started outside the plane and ended inside it");
    }

    /// <summary>
    /// The case the whole rule exists for: a stride longer than the opening is deep.
    /// </summary>
    /// <remarks>
    /// A burst run covers 0.91 m per tick against an opening 0.8 m deep, so these two positions are both OUTSIDE the
    /// box — one in front of it, one behind — and a containment test sees nothing at all.
    /// </remarks>
    [Test]
    public void ABurstRunThatSkipsOverTheOpening_StillCrosses()
    {
        const float Front = DoorZ + 0.45f;
        const float Behind = DoorZ - 0.46f;
        Assert.That(Behind, Is.LessThan(DoorZ - 0.4f), "precondition: the far position is past the box, not inside it");
        Assert.That(Front, Is.GreaterThan(DoorZ + 0.4f), "precondition: the near position is in front of the box");
        Assert.That(Crossed(Front, Behind), Is.True, "0.91 m of travel over a 0.8 m opening, which is a burst run at 10 Hz");
    }

    [Test]
    public void LeavingTheWayYouCameIn_DoesNotCross()
    {
        Assert.That(Crossed(5.0f, 6.0f), Is.False, "walking back out is a crossing against the forward, which is not an entry");
    }

    [Test]
    public void TurningRoundInsideTheDoorway_DoesNotCrossAgain()
    {
        // Both ends past the plane: whatever the body does in there, it has already entered.
        Assert.That(Crossed(5.2f, 5.1f), Is.False);
        Assert.That(Crossed(5.1f, 5.2f), Is.False);
    }

    [Test]
    public void WalkingPastTheWallBesideTheDoor_DoesNotCross()
    {
        // Same stride, one metre to the side: through the facade, not through the opening.
        Assert.That(Crossed(6.0f, 5.0f, DoorX + HalfX + 0.05f), Is.False, "just outside the opening's edge");
        Assert.That(Crossed(6.0f, 5.0f, DoorX - HalfX - 0.05f), Is.False, "and just outside the other edge");
    }

    /// <summary>
    /// A body grazing the frame is still going through the door.
    /// </summary>
    /// <remarks>
    /// <b>A centimetre inside the edge, not ON it.</b> The exact edge is not a meaningful question in float: the test
    /// point is built as <c>1.5f + 0.95f</c> and the rule subtracts the centre again, which lands a hair over 0.95 and
    /// answers no. Asserting it either way would pin an artefact of the arithmetic rather than the behaviour, and the
    /// behaviour is that the opening is as wide as it is published to be.
    /// </remarks>
    [Test]
    public void TheEdgeOfTheOpening_Counts()
    {
        Assert.That(Crossed(6.0f, 5.0f, DoorX + HalfX - 0.01f), Is.True);
        Assert.That(Crossed(6.0f, 5.0f, DoorX - HalfX + 0.01f), Is.True);
    }

    [Test]
    public void StandingStill_IsNotACrossing()
    {
        Assert.That(Crossed(6.0f, 6.0f), Is.False);
        Assert.That(Crossed(5.0f, 5.0f), Is.False, "including standing still inside the doorway");
    }

    /// <summary>
    /// A body stopped by the facade has crossed, which is what the published 0.2 m setback is for.
    /// </summary>
    /// <remarks>
    /// The facade is solid at z 4.85 and a 0.3 m body resting against it has its centre at 5.15 — 0.30 m past the plane.
    /// Before the setback the plane was at 5.25 and that same body sat 0.10 m past it, which is the margin
    /// `blender-models` judged too thin for anyone, bot or human, leaning on a door.
    /// </remarks>
    [Test]
    public void ABodyStoppedByTheFacade_HasAlreadyCrossed()
    {
        const float Resting = 5.15f;
        Assert.That(Resting, Is.LessThan(DoorZ), "precondition: resting against the facade is past the mid-plane");
        Assert.That(Crossed(6.0f, Resting), Is.True);
        Assert.That(DoorZ - Resting, Is.EqualTo(0.30f).Within(1e-4f), "the setback's whole purpose, in one number");
    }

    /// <summary>
    /// The way OUT of a room is the same rule with the forward the descriptor's rotation gives.
    /// </summary>
    /// <remarks>
    /// `tat_house_large_interior`'s door declares forward "-Z" and carries a half-turn rotation, so its forward in the
    /// asset's frame is +Z. Reading the string as a direction gets every entrance right and every exit backwards —
    /// nobody would ever leave a room, and nothing would throw.
    /// </remarks>
    [Test]
    public void TheWayOutOfARoom_UsesTheRotatedForward()
    {
        const float RoomDoorZ = 3.5f;
        Assert.That(
            SimBridge.CrossedDoorForTest(1.5f, RoomDoorZ, 0f, 1f, HalfX, 1.5f, 3.0f, 1.5f, 3.8f),
            Is.True,
            "walking from the room towards the door, along the rotated forward");
        Assert.That(
            SimBridge.CrossedDoorForTest(1.5f, RoomDoorZ, 0f, -1f, HalfX, 1.5f, 3.0f, 1.5f, 3.8f),
            Is.False,
            "the same step read with the unrotated axis, which is how an exit silently never fires");
    }

    /// <summary>A door turned by its placement is crossed in the direction it faces, not in +Z.</summary>
    [Test]
    public void ADoorTurnedByItsPlacement_IsCrossedAlongItsOwnForward()
    {
        // A quarter turn puts the forward along +X.
        Assert.That(SimBridge.CrossedDoorForTest(10f, 0f, 1f, 0f, HalfX, 9.5f, 0f, 10.5f, 0f), Is.True);
        Assert.That(SimBridge.CrossedDoorForTest(10f, 0f, 1f, 0f, HalfX, 10.5f, 0f, 9.5f, 0f), Is.False);
    }

    /// <summary>
    /// A placement's quarter turn is the one the renderer draws, measured against the drawn building.
    /// </summary>
    /// <remarks>
    /// <b>These four numbers came out of the browser, not out of a derivation.</b> The door of the placed
    /// <c>tat_house_large</c> at (3646.01, -4599.63) with yaw 1 was transformed through the instance's own world matrix
    /// by Babylon and landed at (3651.46, -4601.13); the server's first table put it at (3640.56, -4598.13), on the
    /// opposite side of the building. Quarters 0 and 2 agree under either sense, so half the world looked right and the
    /// other half had its door in a wall, which is the shape of mistake that survives a reading and fails a walk.
    /// </remarks>
    [Test]
    public void AQuarterTurn_PutsTheDoorWhereTheRendererDraws()
    {
        const float PlacedX = 3646.01f;
        const float PlacedZ = -4599.63f;
        const float LocalX = 1.5f;
        const float LocalZ = 5.45f;

        var (sin, cos) = WorldIndex.QuarterTurn(1);
        Assert.Multiple(() =>
        {
            Assert.That(PlacedX + ((LocalX * cos) - (LocalZ * sin)), Is.EqualTo(3651.46f).Within(0.01f));
            Assert.That(PlacedZ + ((LocalX * sin) + (LocalZ * cos)), Is.EqualTo(-4601.13f).Within(0.01f));

            // And the direction through it, which is what decides the crossing: -Z turned by a quarter is -X here.
            Assert.That((0f * cos) - (-1f * sin), Is.EqualTo(-1f).Within(1e-6f), "the forward Babylon reports for this placement");
            Assert.That((0f * sin) + (-1f * cos), Is.EqualTo(0f).Within(1e-6f));
        });
    }

    /// <summary>Turning a placement four quarters brings a door back where it started.</summary>
    [Test]
    public void FourQuarters_ComeBackRound()
    {
        for (byte q = 0; q < 4; q++)
        {
            var (sin, cos) = WorldIndex.QuarterTurn(q);
            Assert.That((sin * sin) + (cos * cos), Is.EqualTo(1f).Within(1e-6f), $"quarter {q} is not a rotation");
        }

        Assert.That(WorldIndex.QuarterTurn(0), Is.EqualTo((0f, 1f)));
        Assert.That(WorldIndex.QuarterTurn(2), Is.EqualTo((0f, -1f)));
        Assert.That(WorldIndex.QuarterTurn(1), Is.Not.EqualTo(WorldIndex.QuarterTurn(3)), "the two that were swapped");
    }
}
