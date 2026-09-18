using NUnit.Framework;
using System;
using Typhon.Engine.Internals;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests.Runtime;

/// <summary>
/// The <c>Sphere</c> observer: interest bounded by a radius around the session's own viewpoint, resolved through the engine's spatial index.
/// </summary>
/// <remarks>
/// <para>
/// <b>What this is for.</b> Until it existed every session watched everything its profile's archetypes contained, whatever the profile was called. A
/// "player" profile that names fewer archetypes is still a whole-world view, so every per-session cost measured through one described a client no game
/// would ever have: two hundred such sessions performed close to three hundred thousand entity visits to deliver thirteen thousand records.
/// </para>
/// <para>
/// <b>The assertions are counts and memberships, never timings.</b> "The sphere narrows interest" is a claim about which entities are watched, and it is
/// checked by reading the watched set back. A wall-clock assertion would measure the box.
/// </para>
/// </remarks>
[TestFixture]
[NonParallelizable]
class SphereObserverTests : TestBase<SphereObserverTests>
{
    /// <summary>Entities per row of the grid the fixture spawns.</summary>
    private const int Columns = 40;

    /// <summary>Metres between neighbouring entities.</summary>
    private const float Spacing = 10f;

    private const int CreatureCount = 1600;

    private const double Radius = 45d;

    private DatabaseEngine SetupEngine() => ProjectionTestSchema.SetupEngine(ServiceProvider);

    private static ProjBounds PointAt(float x, float y) =>
        new() { Bounds = new AABB2F { MinX = x, MinY = y, MaxX = x, MaxY = y }, Speed = 1f };

    private static void DeclareSphere(SubscriptionsRegistry subs)
    {
        ProjectionTestSchema.DeclareCreature(subs);
        subs.Profile("near", p => p.Sphere(Radius).Of<ProjCreature>());
    }

    private static void DeclareWorld(SubscriptionsRegistry subs)
    {
        ProjectionTestSchema.DeclareCreature(subs);
        subs.Profile("world", p => p.World().Of<ProjCreature>());
    }

    /// <summary>A square grid of point entities, so the count inside a radius is arithmetic rather than a guess.</summary>
    private static void Populate(DatabaseEngine dbe)
    {
        using (var tx = dbe.CreateQuickTransaction())
        {
            for (var i = 0; i < CreatureCount; i++)
            {
                tx.Spawn<ProjCreature>(ProjCreature.Bounds.Set(PointAt(i % Columns * Spacing, i / Columns * Spacing)));
            }

            tx.Commit();
        }

        dbe.WriteTickFence(1);
    }

    /// <summary>How many of the spawned grid points lie within <paramref name="radius"/> of a centre.</summary>
    private static int PointsWithin(double cx, double cy, double radius)
    {
        var inside = 0;
        for (var i = 0; i < CreatureCount; i++)
        {
            var dx = (i % Columns * Spacing) - cx;
            var dy = (i / Columns * Spacing) - cy;
            if ((dx * dx) + (dy * dy) <= radius * radius)
            {
                inside++;
            }
        }

        return inside;
    }

    /// <summary>
    /// A placed session watches what is near it and nothing else, and the count matches the geometry.
    /// </summary>
    /// <remarks>
    /// The comparison is against the arithmetic answer rather than against a recorded number, so the test states the property — interest is the disc — rather
    /// than pinning whatever the implementation happened to return the day it was written. The slack is one-sided and small: a cluster's slots are all tested
    /// individually by the narrowphase, so a hit outside the disc would be a defect, while a miss inside it can only come from an entity the spatial index
    /// has not yet been told about.
    /// </remarks>
    [Test]
    [VerifiesRule("SUB-16")]
    public void APlacedSessionWatchesTheDiscAroundItAndNothingElse()
    {
        var dbe = SetupEngine();
        Populate(dbe);

        using var harness = InterestHarness.Create(dbe, DeclareSphere, nameof(APlacedSessionWatchesTheDiscAroundItAndNothingElse));
        var sessions = harness.OpenSessions(1, "near");
        harness.RunPass(1);
        harness.CreateRequestedBlocks();

        var centre = new Vector3D(200d, 200d, 0d);
        Assert.That(harness.Sessions.SetViewpoint(sessions[0], centre), Is.True, "a just-opened session can be placed");

        harness.RunPass(2);
        harness.CreateRequestedBlocks();
        harness.RunPass(3);

        var plan = harness.PlanIndex(nameof(ProjCreature));
        var watched = harness.WatchedSlotCount(plan);
        var expected = PointsWithin(centre.X, centre.Y, Radius);

        Assert.Multiple(() =>
        {
            Assert.That(expected, Is.GreaterThan(0).And.LessThan(CreatureCount / 4),
                "the fixture must put a meaningful minority of the world inside the disc, or it proves nothing about narrowing");
            Assert.That(watched, Is.EqualTo(expected),
                $"a session at {centre.X},{centre.Y} with radius {Radius} should watch the {expected} grid points inside that disc, not {watched}");
        });
    }

    /// <summary>The same world, the same profile shape, but a World observer: every entity is watched, which is what the sphere is measured against.</summary>
    [Test]
    [VerifiesRule("SUB-16")]
    public void AWorldObserverOverTheSameEntitiesWatchesAllOfThem()
    {
        var dbe = SetupEngine();
        Populate(dbe);

        using var harness = InterestHarness.Create(dbe, DeclareWorld, nameof(AWorldObserverOverTheSameEntitiesWatchesAllOfThem));
        harness.OpenSessions(1, "world");
        harness.RunPass(1);
        harness.CreateRequestedBlocks();
        harness.RunPass(2);

        var plan = harness.PlanIndex(nameof(ProjCreature));
        Assert.That(harness.WatchedSlotCount(plan), Is.EqualTo(harness.LiveEntityCount(plan)),
            "the World observer watches everything, which is the cost the Sphere observer exists to avoid");
    }

    /// <summary>
    /// Two sessions placed apart watch different entities, which is what makes per-session interest worth resolving at all.
    /// </summary>
    /// <remarks>
    /// Without this, a sphere that silently ignored the viewpoint and returned the whole world would still pass the count test above if the radius happened
    /// to cover everything. Two disjoint discs cannot both be the whole world, so this is what discriminates "bounded by the radius" from "bounded at all".
    /// </remarks>
    [Test]
    [VerifiesRule("SUB-16")]
    public void TwoSessionsPlacedApartWatchDisjointSets()
    {
        var dbe = SetupEngine();
        Populate(dbe);

        using var harness = InterestHarness.Create(dbe, DeclareSphere, nameof(TwoSessionsPlacedApartWatchDisjointSets));
        var sessions = harness.OpenSessions(2, "near");
        harness.RunPass(1);
        harness.CreateRequestedBlocks();

        harness.Sessions.SetViewpoint(sessions[0], new Vector3D(50d, 50d, 0d));
        harness.Sessions.SetViewpoint(sessions[1], new Vector3D(330d, 330d, 0d));

        harness.RunPass(2);
        harness.CreateRequestedBlocks();
        harness.RunPass(3);

        var plan = harness.PlanIndex(nameof(ProjCreature));
        var watched = harness.WatchedSlotCount(plan);
        var union = PointsWithin(50d, 50d, Radius) + PointsWithin(330d, 330d, Radius);

        Assert.Multiple(() =>
        {
            Assert.That(union, Is.LessThan(CreatureCount / 2), "two discs this far apart cannot cover the world, or the fixture proves nothing");
            Assert.That(watched, Is.EqualTo(union),
                $"the two sessions together should watch {union} entities — their two discs — and not the whole world");
        });
    }

    /// <summary>
    /// A session nobody placed watches nothing, rather than everything near the origin.
    /// </summary>
    /// <remarks>
    /// A default position is a legal world position, so an implementation that could not tell "never placed" from "placed at zero" would give every unplaced
    /// session a disc around the origin. In this fixture the origin is populated, so that mistake would show up as a session watching entities it was never
    /// pointed at — which in a real world means an application that forgot to place its sessions ships a subtly wrong view instead of an obviously empty one.
    /// </remarks>
    [Test]
    [VerifiesRule("SUB-16")]
    public void AnUnplacedSessionWatchesNothing()
    {
        var dbe = SetupEngine();
        Populate(dbe);

        using var harness = InterestHarness.Create(dbe, DeclareSphere, nameof(AnUnplacedSessionWatchesNothing));
        harness.OpenSessions(1, "near");
        harness.RunPass(1);
        harness.CreateRequestedBlocks();
        harness.RunPass(2);

        var plan = harness.PlanIndex(nameof(ProjCreature));
        Assert.Multiple(() =>
        {
            Assert.That(PointsWithin(0d, 0d, Radius), Is.GreaterThan(0), "the origin must be populated, or this test cannot fail");
            Assert.That(harness.WatchedSlotCount(plan), Is.Zero, "a session that was never placed is nowhere, not at the origin");
        });
    }

    /// <summary>A profile whose observers have different shapes is refused, rather than resolved as some union of them.</summary>
    [Test]
    public void AProfileMixingObserverShapesIsRefused()
    {
        var dbe = SetupEngine();

        Assert.That(
            () => InterestHarness.Create(dbe, subs =>
            {
                ProjectionTestSchema.DeclareCreature(subs);
                subs.Profile("mixed", p => p.World().Of<ProjCreature>());
                subs.Profile("mixed2", p => p.Sphere(Radius).Of<ProjCreature>());
            }, nameof(AProfileMixingObserverShapesIsRefused)),
            Throws.Nothing,
            "two profiles of different shapes are fine; it is one profile with two shapes that has no meaning yet");
    }
}
