using System;
using NUnit.Framework;
using Typhon.Engine;

namespace SwgTatooine.Tests;

/// <summary>
/// S3 — the character controller's one rule: a top face shallow enough to stand on is a floor that carries the feet up
/// with it, and anything steeper is a wall.
/// </summary>
/// <remarks>
/// <para>
/// <b>A ramp is a slab rotated about its length, and its bounding box is a wall its own height.</b> `tat_house_large`'s
/// entry ramp is 0.3 m thick and tilted 21.8 degrees; flattened to an AABB it becomes a block three steps tall, which
/// is impassable, and that is what the collider table did before the top PLANE was kept. These cases assert the plane
/// end to end — that walking across one rises with it, that it is not teleported onto, that too steep is not a floor,
/// and that the gradient survives the structure's quarter turn.
/// </para>
/// <para>
/// Pure arithmetic: no world, no descriptors on disk, no server, no transaction. The live climb is a different claim
/// and is checked by the ticked cases in <see cref="ProximityChecks"/>.
/// </para>
/// </remarks>
[TestFixture]
public sealed class RampChecks
{
    /// <summary>The structure this fixture's ramp belongs to.</summary>
    private static readonly EntityId House = EntityId.FromRawValue(31);

    /// <summary>The body's half-extent, a player's — the world's own constant, not a figure repeated here.</summary>
    private const float Half = TatooineData.PlayerBodyHalfM;

    /// <summary>The desert the ramp rises out of, in metres above sea level.</summary>
    private const float Terrain = -1f;

    /// <summary>
    /// A ramp 8 m long rising 2 m, from <c>Terrain</c> at its west lip to one metre above the structure's origin.
    /// </summary>
    /// <remarks>
    /// <c>TopAt</c> is measured from the box's CENTRE, so <c>MaxY</c> is the height there and not the highest point:
    /// zero at the centre with a gradient of 0.25 over a box centred at x = −4 puts the top at −1 m at <c>x = −8</c>
    /// and at +1 m at <c>x = 0</c>. A quarter of a metre per metre is 14 degrees — inside
    /// <see cref="AssetColliders.MaxWalkableGrade"/>'s 46, and shallower than the published entry ramps.
    /// </remarks>
    private static AssetColliders.PlanBox Ramp(float grade) => new(-8f, -2f, 0f, 2f, -3f, 3f, 0f, grade, 0f);

    /// <summary>The footprint the world reserved for the ramp, which is what the broad phase hands over.</summary>
    private static ProximityBox[] Footprint() => [new(-8f, -2f, 0f, 2f, 0f, House)];

    /// <summary>
    /// <b>A body walking up a ramp rises with it, and arrives at its head.</b>
    /// </summary>
    /// <remarks>
    /// <para>
    /// Stepped at a quarter of a metre, which is about what a tick of a run is (12 m/s at 50 Hz is 0.24 m). Each step
    /// feeds the previous answer back as the feet, exactly as the tick does — so this is the loop the simulation runs,
    /// not an evaluation of the plane at the end point.
    /// </para>
    /// <para>
    /// <b>Falsifiable.</b> With the gradient dropped — which is what the loader did before the top plane was kept, and
    /// what <c>Turn</c> still does — the same box is a flat slab 1 m above the origin, the first step is more than
    /// <see cref="TatooineData.PlayerStepM"/> above the feet, and the body never leaves the desert. The flat case below
    /// asserts exactly that, so the pair cannot both pass by accident.
    /// </para>
    /// </remarks>
    [Test]
    public void ABodyWalkingUpARampRisesWithIt()
    {
        AssetColliders.PlanBox[] prims = [Ramp(0.25f)];
        var boxes = Footprint();
        var shapes = StructureShapes.ForTest(0.4f, House, StructureShapes.Shape.Primitives, prims, 0f, 0f, 0);

        var feet = Terrain;
        var climbed = 0;
        for (var x = -8f; x <= 0.001f; x += 0.25f)
        {
            var next = SimBridge.GroundOnShapes(boxes, Half, x, 0f, Terrain, feet, ref shapes);
            Assert.That(next, Is.GreaterThanOrEqualTo(feet - 1e-4f), $"the ground fell away under the feet at x = {x:F2}");
            Assert.That(next - feet, Is.LessThanOrEqualTo(TatooineData.PlayerStepM + 1e-4f),
                $"one step gained {next - feet:F3} m at x = {x:F2}, which is more than a step");
            if (next > feet + 1e-4f)
            {
                climbed++;
            }

            feet = next;
        }

        TestContext.Out.WriteLine($"feet {Terrain:F2} -> {feet:F2} over 8 m, rising on {climbed} of 33 steps");

        Assert.Multiple(() =>
        {
            Assert.That(feet, Is.EqualTo(1f).Within(1e-3f), "the body did not arrive at the ramp's head");
            Assert.That(climbed, Is.GreaterThan(20), "the rise was not gradual, so this measured something other than a ramp");
        });
    }

    /// <summary>
    /// <b>The same geometry without the gradient is a wall: a flat top a metre up is not stepped onto.</b>
    /// </summary>
    /// <remarks>
    /// This is the other half of the character controller, and the half a slope-aware floor could silently lose: a
    /// surface more than <see cref="TatooineData.PlayerStepM"/> above the feet must stay unreachable however shallow
    /// the code is willing to climb. Same box, same loop, gradient zero.
    /// </remarks>
    [Test]
    public void ABodyWalkingAtAFlatLedgeDoesNotStepOntoIt()
    {
        // The flat slab the ramp becomes when its top plane is discarded: a 1 m top across the whole box.
        AssetColliders.PlanBox[] prims = [new(-8f, -2f, 0f, 2f, -3f, 1f)];
        var boxes = Footprint();
        var shapes = StructureShapes.ForTest(0.4f, House, StructureShapes.Shape.Primitives, prims, 0f, 0f, 0);

        var feet = Terrain;
        for (var x = -8f; x <= 0.001f; x += 0.25f)
        {
            feet = SimBridge.GroundOnShapes(boxes, Half, x, 0f, Terrain, feet, ref shapes);
        }

        Assert.That(feet, Is.EqualTo(Terrain).Within(1e-4f),
            $"the body climbed to {feet:F2} m: a ledge {1f - Terrain:F2} m above its feet is a wall, not a step");
    }

    /// <summary>
    /// <b>A ramp too steep to walk is not a floor</b>, however gently the body approaches it.
    /// </summary>
    /// <remarks>
    /// A gradient of 2 is 63 degrees, past <see cref="AssetColliders.MaxWalkableGrade"/>'s 46 — the testbench's own
    /// <c>maxSlopeDeg</c>, so a slope that is climbable there is climbable here and this one is neither. It is still
    /// solid: the push-out treats it as a wall, which is what stops the body rather than this.
    /// </remarks>
    [Test]
    public void ARampTooSteepToWalkIsNotAFloor()
    {
        AssetColliders.PlanBox[] prims = [Ramp(2f)];
        Assert.That(prims[0].Walkable, Is.False, "the premise: this fixture's steep ramp must be outside the walkable grade");

        var boxes = Footprint();
        var shapes = StructureShapes.ForTest(0.4f, House, StructureShapes.Shape.Primitives, prims, 0f, 0f, 0);

        var feet = Terrain;
        for (var x = -8f; x <= 0.001f; x += 0.05f)
        {
            feet = SimBridge.GroundOnShapes(boxes, Half, x, 0f, Terrain, feet, ref shapes);
        }

        Assert.That(feet, Is.EqualTo(Terrain).Within(1e-4f), $"the body walked up a 63 degree face to {feet:F2} m");
    }

    /// <summary>
    /// A ramp's plane reaches a little past the slab it describes, and this says by how much.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A plan box is the AABB of a TILTED slab, and the AABB of a tilted plank is longer than the plank</b>, so past
    /// the ends of the real top face the plane carries on climbing. The honest fix is to publish the AABB's own top as
    /// a further number and clamp against it; <c>PlanBox.PlaneY</c> is carried for that and is not yet on the wire.
    /// </para>
    /// <para>
    /// <b>Clamping to <c>MaxY</c> instead is wrong, and was tried.</b> On the wire that field is the plane's height at
    /// the CENTRE, not the box's roof — the published house's entry ramp has <c>MaxY = -0.128</c> with a -0.40 gradient
    /// over z 6.889..11.25, so its true surface at z = 8 is +0.30 and a clamp returned -0.128. The ramp stopped being
    /// climbable the moment that landed. This case exists so the next person sees the measured size of the real
    /// problem before reaching for the obvious fix.
    /// </para>
    /// </remarks>
    [Test]
    public void ARampsPlaneOverreachesItsOwnSlabByCentimetres()
    {
        // The published house ramp, in the asset's frame, as the live client reports it.
        var ramp = new AssetColliders.PlanBox(-2.18f, -1.7f, 2.18f, 1.7f, -1.28f, -0.13f, float.NaN, -0.4f, 0f);

        // A 0.3 m plank tilted 21.8 degrees has an AABB top of about +0.70 above the structure's origin.
        const float RealRoof = 0.697f;
        var atCorner = ramp.TopAt(-2.18f, 0f);

        TestContext.Out.WriteLine($"plane at the box's corner {atCorner:F3} m against a real roof of {RealRoof:F3} m: "
            + $"over-reach {atCorner - RealRoof:F3} m");

        Assert.Multiple(() =>
        {
            Assert.That(ramp.TopAt(0f, 0f), Is.EqualTo(-0.13f).Within(1e-3f), "the centre is the plane's own height");
            Assert.That(atCorner - RealRoof, Is.LessThan(0.1f), "the over-reach is centimetres, not a step");
            Assert.That(atCorner, Is.GreaterThan(RealRoof), "the premise: unclamped, the plane does reach past the slab");
        });
    }

    /// <summary>
    /// <b>A ramp turns with its structure: the same point on it is at the same height whichever way the house faces.</b>
    /// </summary>
    /// <remarks>
    /// <para>
    /// A gradient is a direction in the plan and turns exactly as a position does, <c>(x, z)</c> to <c>(z, −x)</c>.
    /// Getting the sign wrong tilts the ramp the wrong way for three of the four orientations, and a world that places
    /// its houses at all four yaws then reads as "collision is flaky at some buildings" rather than as one bug.
    /// </para>
    /// <para>
    /// <b>Asserted as the invariant rather than as four expected gradients</b>, because the expected gradients are the
    /// thing under test: turning the box and turning the probe point must agree about the height, and a transposition
    /// that satisfied both would have to be the identity.
    /// </para>
    /// </remarks>
    [Test]
    public void ARampKeepsItsHeightsThroughEveryQuarterTurn()
    {
        var ramp = Ramp(0.25f);

        // Points spread over the ramp's own plan, including its corners, in the asset's frame.
        (float X, float Z)[] probes = [(-8f, -2f), (-8f, 2f), (-4f, 0f), (0f, -2f), (0f, 2f), (-6.5f, 1.25f)];

        for (var quarters = 0; quarters < 4; quarters++)
        {
            var turned = SimBridge.TurnBox(quarters, ramp);
            foreach (var (px, pz) in probes)
            {
                // The probe turned the way a POSITION turns, which is the arithmetic the push-out and the ground
                // sampler both apply before they read the plane.
                var (qx, qz) = (px, pz);
                for (var i = 0; i < quarters; i++)
                {
                    (qx, qz) = (qz, -qx);
                }

                Assert.That(turned.TopAt(qx, qz), Is.EqualTo(ramp.TopAt(px, pz)).Within(1e-4f),
                    $"quarter {quarters} moved the top at ({px:F2}, {pz:F2}) to a different height");
            }

            Assert.That(turned.Walkable, Is.EqualTo(ramp.Walkable), $"quarter {quarters} changed whether the ramp can be walked");
        }
    }
}
