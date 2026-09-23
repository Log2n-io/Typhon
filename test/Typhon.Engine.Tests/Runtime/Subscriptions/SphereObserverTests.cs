using NUnit.Framework;
using System;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests.Runtime;

/// <summary>
/// The <c>Sphere</c> observer: a session holds the entities within a radius of its own viewpoint, and nothing else.
/// </summary>
/// <remarks>
/// <para>
/// <b>What a session holds is geometry.</b> An entity is held when it lies within the radius of the session's anchor and its cell has been delivered to
/// the session; nothing is stored per (session, entity). These cases read what the CLIENT holds after its fill, through the frames it was actually sent.
/// </para>
/// <para>
/// <b>The assertions are counts and memberships, never timings.</b> "The sphere narrows what a session holds" is checked by reading the client's replica
/// back against the arithmetic count of grid points inside the disc.
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

    /// <summary>Ticks after which every cell a disc reaches has been delivered.</summary>
    private const int FillTicks = 6;

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
        using var tx = dbe.CreateQuickTransaction();
        for (var i = 0; i < CreatureCount; i++)
        {
            tx.Spawn<ProjCreature>(ProjCreature.Bounds.Set(PointAt(i % Columns * Spacing, i / Columns * Spacing)));
        }

        tx.Commit();
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

    /// <summary>Runs the fill: enough ticks, every frame delivered, for every cell a disc reaches to have been delivered.</summary>
    private static void Fill(FrameHarness harness, params SessionId[] sessions)
    {
        for (var tick = 1; tick <= FillTicks; tick++)
        {
            harness.RunTick(tick);
            foreach (var session in sessions)
            {
                harness.Deliver(session);
            }
        }
    }

    private static int Held(FrameHarness harness, SessionId session) =>
        harness.Replica(session).NetIds(harness.CatalogPlan.ArchetypeByName(nameof(ProjCreature)).Idx).Length;

    /// <summary>A placed session holds what is near it and nothing else, and the count matches the geometry.</summary>
    /// <remarks>
    /// The comparison is against the arithmetic answer rather than against a recorded number, so the test states the property — a session holds the disc —
    /// rather than pinning whatever the implementation happened to return the day it was written.
    /// </remarks>
    [Test]
    [VerifiesRule("SUB-16")]
    public void APlacedSessionHoldsTheDiscAroundItAndNothingElse()
    {
        var dbe = SetupEngine();
        Populate(dbe);

        using var harness = FrameHarness.Create(dbe, DeclareSphere, nameof(APlacedSessionHoldsTheDiscAroundItAndNothingElse),
            replicationCellM: ProjectionTestSchema.ReplicationCellFor(Radius));
        harness.RunFence = true;
        var session = harness.OpenSessions(1, "near")[0];
        var centre = new Vector3D(200d, 200d, 0d);
        Assert.That(harness.Sessions.SetViewpoint(session, centre), Is.True, "a just-opened session can be placed");

        Fill(harness, session);

        var held = Held(harness, session);
        var expected = PointsWithin(centre.X, centre.Y, Radius);

        Assert.Multiple(() =>
        {
            Assert.That(expected, Is.GreaterThan(0).And.LessThan(CreatureCount / 4),
                "the fixture must put a meaningful minority of the world inside the disc, or it proves nothing about narrowing");
            Assert.That(held, Is.EqualTo(expected),
                $"a session at {centre.X},{centre.Y} with radius {Radius} should hold the {expected} grid points inside that disc, not {held}");
        });
    }

    /// <summary>The same world, a World observer: every entity is held, which is what the sphere is measured against.</summary>
    [Test]
    [VerifiesRule("SUB-16")]
    public void AWorldObserverOverTheSameEntitiesHoldsAllOfThem()
    {
        var dbe = SetupEngine();
        Populate(dbe);

        using var harness = FrameHarness.Create(dbe, DeclareWorld, nameof(AWorldObserverOverTheSameEntitiesHoldsAllOfThem),
            new SubscriptionsOptions
            {
                MaxSessions = 16, EnterBudgetPerFrame = CreatureCount, ReplicationCellM = ProjectionTestSchema.ReplicationCellFor(0),
            });
        harness.RunFence = true;
        var session = harness.OpenSessions(1, "world")[0];

        Fill(harness, session);

        Assert.That(Held(harness, session), Is.EqualTo(CreatureCount), "the World observer holds everything, which is the cost the Sphere avoids");
    }

    /// <summary>Two sessions placed apart hold different entities, which is what makes a per-session region worth anything at all.</summary>
    /// <remarks>
    /// Without this, a sphere that silently ignored the viewpoint and returned the whole world would still pass the count test above if the radius happened
    /// to cover everything. Two disjoint discs cannot both be the whole world, so this discriminates "bounded by the radius" from "bounded at all".
    /// </remarks>
    [Test]
    [VerifiesRule("SUB-16")]
    public void TwoSessionsPlacedApartHoldDisjointSets()
    {
        var dbe = SetupEngine();
        Populate(dbe);

        using var harness = FrameHarness.Create(dbe, DeclareSphere, nameof(TwoSessionsPlacedApartHoldDisjointSets),
            replicationCellM: ProjectionTestSchema.ReplicationCellFor(Radius));
        harness.RunFence = true;
        var sessions = harness.OpenSessions(2, "near");
        harness.Sessions.SetViewpoint(sessions[0], new Vector3D(50d, 50d, 0d));
        harness.Sessions.SetViewpoint(sessions[1], new Vector3D(330d, 330d, 0d));

        Fill(harness, sessions);

        var creature = harness.CatalogPlan.ArchetypeByName(nameof(ProjCreature)).Idx;
        var first = harness.Replica(sessions[0]).NetIds(creature);
        var second = harness.Replica(sessions[1]).NetIds(creature);

        Assert.Multiple(() =>
        {
            Assert.That(first, Has.Length.EqualTo(PointsWithin(50d, 50d, Radius)), "the first session holds its own disc");
            Assert.That(second, Has.Length.EqualTo(PointsWithin(330d, 330d, Radius)), "the second session holds its own disc");
            Assert.That(first, Has.No.AnyOf(second), "two discs this far apart share no entity");
        });
    }

    /// <summary>A session nobody placed holds nothing, rather than everything near the origin.</summary>
    /// <remarks>
    /// A default position is a legal world position, so an implementation that could not tell "never placed" from "placed at zero" would give every
    /// unplaced session a disc around the origin. In this fixture the origin is populated, so that mistake would show up as a client holding entities it was
    /// never pointed at.
    /// </remarks>
    [Test]
    [VerifiesRule("SUB-16")]
    public void AnUnplacedSessionHoldsNothing()
    {
        var dbe = SetupEngine();
        Populate(dbe);

        using var harness = FrameHarness.Create(dbe, DeclareSphere, nameof(AnUnplacedSessionHoldsNothing),
            replicationCellM: ProjectionTestSchema.ReplicationCellFor(Radius));
        harness.RunFence = true;
        var sessions = harness.OpenSessions(2, "near");
        var unplaced = sessions[0];

        // The control: a session placed at the origin, which must hold the origin's disc — so a pipeline that delivered nothing at all fails here.
        var control = sessions[1];
        harness.Sessions.SetViewpoint(control, new Vector3D(0d, 0d, 0d));

        Fill(harness, sessions);

        Assert.Multiple(() =>
        {
            Assert.That(PointsWithin(0d, 0d, Radius), Is.GreaterThan(0), "the origin must be populated, or this test cannot fail");
            Assert.That(Held(harness, control), Is.EqualTo(PointsWithin(0d, 0d, Radius)), "a session placed at the origin holds the origin's disc");
            Assert.That(Held(harness, unplaced), Is.Zero, "a session that was never placed is nowhere, not at the origin");
        });
    }

    /// <summary>A profile whose observers have different shapes is refused, rather than resolved as some union of them.</summary>
    [Test]
    public void AProfileMixingObserverShapesIsRefused()
    {
        var dbe = SetupEngine();

        Assert.That(
            () => FrameHarness.Create(dbe, subs =>
            {
                ProjectionTestSchema.DeclareCreature(subs);
                subs.Profile("mixed", p =>
                {
                    // Two observers on the SAME profile builder, which is what "mixing shapes" means.
                    p.World().Of<ProjCreature>();
                    p.Sphere(Radius).Of<ProjCreature>();
                });
            }, nameof(AProfileMixingObserverShapesIsRefused)),
            Throws.TypeOf<NotSupportedException>().With.Message.Contains("observers"),
            "a profile with a World observer and a Sphere observer is a near/far tier, and the tiers differ in budget, rate and record kind — resolving "
            + "them as one union would be a quiet wrong answer");
    }
}
