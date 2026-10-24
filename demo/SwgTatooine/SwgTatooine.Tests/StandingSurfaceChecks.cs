using System;
using NUnit.Framework;
using SwgTatooine.Server;
using Typhon.Engine;
using SwgTatooine.Navigation;

namespace SwgTatooine.Tests;

/// <summary>
/// S3 — a body's altitude is the floor it is ACTUALLY over, and a disc may not carry one past its edge.
/// </summary>
/// <remarks>
/// <para>
/// <b>The disc is what makes collision cheap, and it is also what can make a body fly.</b> While a body stays inside
/// its disc it runs no spatial query at all; the altitude it stands at comes from <c>PlayerProximity.SurfaceY</c>,
/// recorded when the disc was last refreshed. That is sound only if the disc can never reach past the edge of the
/// floor that produced it — otherwise the body walks off a deck, keeps the deck's altitude, and hangs over the desert.
/// Seen in game on the shuttleport, which is a raised deck with a ramp.
/// </para>
/// <para>
/// These cases are pure arithmetic on <see cref="SimBridge.DiscFor"/>: no world, no server. That is deliberate — the
/// claim is about what a disc may promise, and a ticked world would grade the promise by the same bookkeeping that
/// made it.
/// </para>
/// </remarks>
[TestFixture]
public sealed class StandingSurfaceChecks
{
    /// <summary>The structure whose deck these cases stand on.</summary>
    private static readonly EntityId Deck = EntityId.FromRawValue(41);

    /// <summary>A player's half-extent.</summary>
    private const float Half = 0.3f;

    /// <summary>
    /// A deck 10 m square and 2 m up, with nothing else on it: the shape a body walks onto and then off.
    /// </summary>
    /// <remarks>
    /// A floor, not a wall: its top is what the feet stand on. The vertical span is thin because a deck IS thin — what
    /// matters is that its top is reachable from the body's feet and that it ends somewhere.
    /// </remarks>
    private static AssetColliders.PlanBox[] DeckTop() => [new(-5f, -5f, 5f, 5f, 1.8f, 2f)];

    /// <summary>The footprint the world reserved for it.</summary>
    private static ProximityBox[] Footprint() => [new(-5f, -5f, 5f, 5f, 0f, Deck)];

    /// <summary>
    /// <b>A body standing on a deck holds a disc no larger than its distance to the deck's edge.</b>
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the invariant the cached <c>SurfaceY</c> rests on, and it is the one worth stating out loud: a disc says
    /// "nothing fixed can touch this body", and the floor under its feet is a fixed thing whose presence ENDS at its
    /// edge. A disc that reaches past it promises ground that is not there.
    /// </para>
    /// <para>
    /// Measured at the middle of a 10 m deck: the nearest edge is 5 m away, so that is the most the disc may be.
    /// </para>
    /// </remarks>
    [Test]
    public void ADiscOnADeckIsBoundedByTheDecksEdge()
    {
        var shapes = StructureShapes.ForTest(0f, Deck, StructureShapes.Shape.Primitives, DeckTop(), 0f, 0f, 0);
        var boxes = Footprint();

        // Standing in the middle of the deck, feet on its top.
        var disc = SimBridge.DiscFor(boxes, 0f, 0f, 50f, 0f, 0f, ref shapes, overflowed: false, feetY: 2f);
        TestContext.Out.WriteLine($"disc in the middle of a 10 m deck: {disc:F2} m (the edge is 5 m away)");
        Assert.That(disc, Is.LessThanOrEqualTo(5f + 1e-3f),
            "the disc reaches past the deck's edge, so a body may walk off it while still holding the disc");

        // And two metres from the edge, where only 3 m of deck is left.
        var nearEdge = SimBridge.DiscFor(boxes, 3f, 0f, 50f, 3f, 0f, ref shapes, overflowed: false, feetY: 2f);
        TestContext.Out.WriteLine($"disc 2 m from the edge: {nearEdge:F2} m");
        Assert.That(nearEdge, Is.LessThanOrEqualTo(2f + 1e-3f), "the disc reaches past the near edge of the deck");
    }

    /// <summary>
    /// <b>A body that has stepped off the deck is told the terrain, not the deck it just left.</b>
    /// </summary>
    /// <remarks>
    /// The other half of the same claim, and the one the player sees: <see cref="SimBridge.GroundOnShapes"/> asked
    /// about a position beyond the deck must answer with the ground, however high the feet were a moment ago. If this
    /// passes and the body still flies, the altitude is being carried by something other than the sampler — which is
    /// exactly the distinction the fixture exists to draw.
    /// </remarks>
    [Test]
    public void GroundBeyondADeckIsTheTerrain()
    {
        var shapes = StructureShapes.ForTest(0f, Deck, StructureShapes.Shape.Primitives, DeckTop(), 0f, 0f, 0);
        var boxes = Footprint();
        const float Terrain = 0f;

        var onIt = SimBridge.GroundOnShapes(boxes, Half, 0f, 0f, Terrain, 2f, ref shapes);
        Assert.That(onIt, Is.EqualTo(2f).Within(1e-3f), "the premise: a body on the deck stands on the deck");

        // One stride past the edge, feet still at the deck's height because that is where they were.
        var offIt = SimBridge.GroundOnShapes(boxes, Half, 7f, 0f, Terrain, 2f, ref shapes);
        TestContext.Out.WriteLine($"on the deck {onIt:F2} m; 2 m past its edge {offIt:F2} m; terrain {Terrain:F2} m");
        Assert.That(offIt, Is.EqualTo(Terrain).Within(1e-3f), "a body past the deck's edge was left standing at the deck's altitude");
    }

    /// <summary>
    /// <b>A disc held over the gap beyond a deck promises nothing about the floor.</b>
    /// </summary>
    /// <remarks>
    /// The failing shape, stated as arithmetic: the body refreshes its disc in the middle of the deck and then walks
    /// towards the edge. For the cached surface to stay honest, the body must leave the disc BEFORE it leaves the deck.
    /// Walking a straight line, the step at which <see cref="SimBridge.DiscHolds"/> gives up must come no later than
    /// the step at which the deck runs out.
    /// </remarks>
    [Test]
    public void ABodyLeavesItsDiscNoLaterThanItLeavesTheDeck()
    {
        var shapes = StructureShapes.ForTest(0f, Deck, StructureShapes.Shape.Primitives, DeckTop(), 0f, 0f, 0);
        var boxes = Footprint();

        // The disc taken in the middle of the deck.
        var disc = SimBridge.DiscFor(boxes, 0f, 0f, 50f, 0f, 0f, ref shapes, overflowed: false, feetY: 2f);

        // Walk east in 10 cm steps and find where each ends.
        var leftDisc = float.NaN;
        var leftDeck = float.NaN;
        for (var x = 0f; x < 9f; x += 0.1f)
        {
            // The disc holds while the body's own extent stays inside it.
            if (float.IsNaN(leftDisc) && x + Half > disc)
            {
                leftDisc = x;
            }

            // The deck ends at 5 m; past that the feet are over nothing.
            if (float.IsNaN(leftDeck) && x - Half > 5f)
            {
                leftDeck = x;
            }
        }

        TestContext.Out.WriteLine($"disc {disc:F2} m: left the disc at x = {leftDisc:F2}, left the deck at x = {leftDeck:F2}");
        Assert.That(leftDisc, Is.LessThanOrEqualTo(leftDeck),
            "the body is still holding its disc after it has walked off the deck, so it keeps the deck's altitude in mid-air");
    }
}
