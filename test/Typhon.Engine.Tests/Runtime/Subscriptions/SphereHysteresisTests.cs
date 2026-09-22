using NUnit.Framework;
using System;
using Typhon.Engine.Internals;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests.Runtime;

/// <summary>
/// A sphere observer's hysteresis band: an entity is admitted at the ENTER radius and kept until it passes the LEAVE radius.
/// </summary>
/// <remarks>
/// <para>
/// <b>Without the band a boundary is a coin toss taken every tick.</b> An entity sitting at the radius, or an observer jittering across it, enters and
/// leaves on alternate ticks — and the two directions are not symmetric in cost: a leave is a netId, an enter is the entity's whole state. Measured on the
/// SWG demo before the band was built, every LEAVE the subsystem sent was a boundary crossing (17 § 19), at 165 to 212 per published frame against views of
/// seven to nine thousand entities.
/// </para>
/// <para>
/// <b>The band was declared and not built.</b> <c>ProfileBuilder.Sphere</c> has taken an enter radius and a leave radius since the API was written, and
/// says in its own remarks why; profile compilation then folded the two into one query radius and kept nothing that could tell them apart. These fixtures
/// exist so that the day someone folds them together again, something says so.
/// </para>
/// <para>
/// <b>Asserted against the CLIENT's store.</b> What matters is what the client is told and keeps being told — a run the frame stage then declines to
/// describe would satisfy an interest-level assertion and leave the client's world wrong.
/// </para>
/// <para>
/// <b>Every walk starts by proving itself live.</b> A cluster no session has reached has no replication block, and a session that reaches one for the
/// first time gets nothing that tick — so a fixture that merely checked "the entity was not sent" would pass against an engine that could never have sent
/// it at all. Each walk therefore brings the observer inside the enter radius first and asserts the entity ARRIVES before it asserts anything about the
/// band.
/// </para>
/// </remarks>
[TestFixture]
[NonParallelizable]
class SphereHysteresisTests : TestBase<SphereHysteresisTests>
{
    private const string Profile = "banded";
    private const double EnterRadius = 100d;
    private const double LeaveRadius = 140d;

    /// <summary>Inside the enter radius.</summary>
    private const double Near = 80d;

    /// <summary>Between the two radii: the band.</summary>
    private const double Band = 120d;

    /// <summary>Beyond the leave radius.</summary>
    private const double Far = 200d;

    /// <summary>
    /// Where the one entity sits, well away from the origin.
    /// </summary>
    /// <remarks>
    /// Away from the origin because that is where an UNPLACED session sits, and a viewpoint set before a pass is staged for the next tick's prologue
    /// rather than applied at once — so the first tick runs with the session at the origin whatever the test asked for.
    /// </remarks>
    private const float EntityX = 2000f;

    private static void Declare(SubscriptionsRegistry subs)
    {
        ProjectionTestSchema.DeclareCreature(subs);
        subs.Profile(Profile, p => p.Sphere(EnterRadius, LeaveRadius).Of<ProjCreature>());
    }

    /// <summary>The same disc with no band: one hard edge where the band's INNER boundary is.</summary>
    private static void DeclareNoBand(SubscriptionsRegistry subs)
    {
        ProjectionTestSchema.DeclareCreature(subs);
        subs.Profile(Profile, p => p.Sphere(EnterRadius).Of<ProjCreature>());
    }

    private static ProjBounds PointAt(float x, float y) =>
        new() { Bounds = new AABB2F { MinX = x, MinY = y, MaxX = x, MaxY = y }, Speed = 1f };

    private sealed class WalkResult
    {
        /// <summary>Whether the client held the entity after each step.</summary>
        public bool[] Held;

        /// <summary>ENTER records the session received over the walk, not counting the settling steps.</summary>
        public long Enters;
    }

    /// <summary>
    /// Settles a session with the entity in view, then walks its observer through a sequence of distances.
    /// </summary>
    /// <param name="declare">Which profile shape to run.</param>
    /// <param name="distances">The observer's distance from the entity, step by step, AFTER the settling steps.</param>
    /// <returns>Whether the client held the entity after each step, and the enters the walk itself cost.</returns>
    private WalkResult Run(Action<SubscriptionsRegistry> declare, params double[] distances)
    {
        (ServiceProvider as IDisposable)?.Dispose();
        Setup();

        var dbe = ProjectionTestSchema.SetupEngine(ServiceProvider);
        using (var tx = dbe.CreateQuickTransaction())
        {
            tx.Spawn<ProjCreature>(ProjCreature.Bounds.Set(PointAt(EntityX, 0f)));
            tx.Commit();
        }

        dbe.WriteTickFence(1);

        using var harness = FrameHarness.Create(dbe, declare, nameof(SphereHysteresisTests), new SubscriptionsOptions
        {
            MaxSessions = 4,
            StatePoolBudgetBytes = 8L * 1024 * 1024,
            FramePoolBudgetBytes = 8L * 1024 * 1024,

            // Nothing may be deferred: an entity the budget held back is absent from the client for a reason that has nothing to do with the band.
            EnterBudgetPerFrame = 1000,
        });

        var session = harness.OpenSessions(1, Profile)[0];
        var archetype = harness.PlanIndex(nameof(ProjCreature));
        var tick = 1L;

        bool Step(double distance)
        {
            Assert.That(harness.Sessions.SetViewpoint(session, new Vector3D(EntityX + distance, 0d, 0d)), Is.True);
            harness.RunTick(++tick);
            harness.Deliver(session);
            return harness.Replica(session).NetIds(archetype).Length > 0;
        }

        // The session's first frame carries RESET whether or not it describes anything, so it is drained rather than asserted away.
        harness.RunTick(tick);
        harness.Deliver(session);

        // SETTLE, and prove the walk is live. Three steps inside the enter radius: the first reaches a cluster with no replication block and describes
        // nothing, the second is the one that can describe it, and the third leaves no doubt. Without this, a fixture asserting "the band kept it out"
        // would pass against an engine that never had anything to send.
        Step(Near);
        Step(Near);
        Assert.That(Step(Near), Is.True,
            "the entity never reached the client even with the observer inside the ENTER radius, so this walk proves nothing about the band");

        var held = new bool[distances.Length];
        var before = harness.Assembler.EnterFlow.Entered;
        for (var i = 0; i < distances.Length; i++)
        {
            held[i] = Step(distances[i]);
        }

        return new WalkResult { Held = held, Enters = harness.Assembler.EnterFlow.Entered - before };
    }

    /// <summary>
    /// An entity the client holds stays while it is inside the band, and goes when it passes the leave radius.
    /// </summary>
    [Test]
    public void AnEntityTheClientHoldsSurvivesTheBandAndLeavesBeyondIt()
    {
        var walk = Run(Declare, Band, Band, Far);

        Assert.Multiple(() =>
        {
            Assert.That(walk.Held[0], Is.True,
                "the entity was retracted the tick it crossed the ENTER radius, so the band did nothing. That is the flap this exists to stop: the client "
                + "is told to forget an entity it can still see, and told about it again in full the moment it comes back.");
            Assert.That(walk.Held[1], Is.True, "the entity was retracted on its second tick in the band, so the band holds for one tick and no more");
            Assert.That(walk.Held[2], Is.False, "the entity was kept past the LEAVE radius, so the band never ends and a client's world only grows");
            Assert.That(walk.Enters, Is.Zero, "an entity that never left the band was entered again, which is the cost the band exists to remove");
        });
    }

    /// <summary>
    /// An entity in the band is not admitted to a client that has dropped it.
    /// </summary>
    [Test]
    public void AnEntityInsideTheBandIsNotReadmittedToAClientThatDroppedIt()
    {
        //                      beyond the band   back into it, twice
        var walk = Run(Declare, Far, Far, Band, Band);

        Assert.Multiple(() =>
        {
            Assert.That(walk.Held[1], Is.False,
                "the entity was still held beyond the leave radius, so this walk never dropped it and the rest of it proves nothing");
            Assert.That(walk.Held[2], Is.False,
                "an entity between the enter and leave radii was sent to a client that no longer held it. The band's purpose is that the ENTER radius is "
                + "the one that admits: admitting at the leave radius sends every client a ring it never asked for.");
            Assert.That(walk.Held[3], Is.False,
                "the entity was admitted on its second tick in the band, so the admission test holds for one tick and no more");
        });
    }

    /// <summary>
    /// An observer crossing the enter radius back and forth costs nothing with the band and one enter per crossing without it.
    /// </summary>
    /// <remarks>
    /// This is the measurement that sent the work, reduced to one entity. Counting ENTER records rather than asserting membership is what makes the flap
    /// visible: a run that flaps still ends holding the entity, so the final state says nothing about what it cost to get there.
    /// </remarks>
    [Test]
    public void CrossingTheEnterRadiusCostsNothingWithTheBandAndAnEnterPerCrossingWithout()
    {
        // Never leaves the band, so under the band this is one continuous residency and costs nothing at all. Under a hard edge at the enter radius each
        // return is a fresh enter — which is what the demo was doing 165 times a frame.
        double[] flap = [Band, Near, Band, Near, Band, Near];

        var banded = Run(Declare, flap).Enters;
        var hard = Run(DeclareNoBand, flap).Enters;

        TestContext.Out.WriteLine($"enters over the same walk: banded {banded}, hard edge {hard}");

        Assert.Multiple(() =>
        {
            Assert.That(banded, Is.Zero,
                $"the band let one entity be entered {banded} times over a walk that never leaves the band, which is exactly the case it is for");
            Assert.That(hard, Is.GreaterThan(banded),
                "the hard edge cost no more enters than the band, so this fixture is not observing the flap it claims to and would pass with the band "
                + "removed");
        });
    }
}
