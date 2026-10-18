using NUnit.Framework;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests.Runtime.Subscriptions;

/// <summary>
/// SUB-13 for the repush (#1042): the blocks step reads the chunks pushed again, not every chunk id of the archetype — its work follows what is pushed.
/// </summary>
[TestFixture]
[NonParallelizable]
sealed class RepushSizingTests : TestBase<RepushSizingTests>
{
    private const double Radius = 45d;

    /// <summary>
    /// Forty clusters of creatures, two of them pushed again: the step after visits two entries, not forty chunk ids, pushes those two clusters' slots,
    /// and a drain's forget leaves an entry that reads 0.
    /// </summary>
    [Test]
    [VerifiesRule("SUB-13")]
    public void TheRepushVisitsTheChunksPushedAgainNotTheArchetype()
    {
        var dbe = ProjectionTestSchema.SetupEngine(ServiceProvider);
        using (var tx = dbe.CreateQuickTransaction())
        {
            for (var i = 0; i < 40 * 64; i++)
            {
                var x = (i % 64) * 3f;
                var y = (i / 64) * 3f;
                tx.Spawn<ProjCreature>(ProjCreature.Bounds.Set(new ProjBounds { Bounds = new AABB2F { MinX = x, MinY = y, MaxX = x, MaxY = y }, Speed = 1f }));
            }

            tx.Commit();
        }

        using var harness = FrameHarness.Create(dbe, subs =>
        {
            ProjectionTestSchema.DeclareCreature(subs);
            subs.Profile("near", p => p.Sphere(Radius).Of<ProjCreature>());
        }, nameof(TheRepushVisitsTheChunksPushedAgainNotTheArchetype), replicationCellM: ProjectionTestSchema.ReplicationCellFor(Radius));
        harness.RunFence = true;
        var tick = 0L;
        for (; tick < 4;)
        {
            harness.RunTick(++tick);
        }

        var hub = harness.Subscriptions.Hub;
        var plan = harness.PlanIndex(nameof(ProjCreature));
        var ids = harness.Subscriptions.ReplicationStates[plan].ClusterState.ReadActiveClusterList(out var active);
        Assert.That(active, Is.GreaterThanOrEqualTo(40), "forty clusters or more");

        // A quiet tick visits nothing; then two chunks pushed again, one of them forgotten by a drain before the step.
        harness.RunTick(++tick);
        var before = hub.RepushVisited;
        var pushedBefore = hub.SlotsPushed;
        hub.RepushFromBlocksStep(plan, ids[3], 0b1011UL);
        hub.RepushFromBlocksStep(plan, ids[7], 0b1UL);
        hub.RepushFromBlocksStep(plan, ids[7], 0b10UL);
        hub.RepushFromBlocksStep(plan, ids[9], 0b1UL);
        hub.ForgetRepush(plan, ids[9]);
        harness.RunTick(++tick);

        Assert.Multiple(() =>
        {
            Assert.That(hub.RepushVisited - before, Is.EqualTo(3), "three chunks touched — the third forgotten, read as 0 — of forty and more");
            Assert.That(hub.SlotsPushed - pushedBefore, Is.GreaterThanOrEqualTo(3 + 2), "the two live ones' slots were pushed");
        });

        before = hub.RepushVisited;
        harness.RunTick(++tick);
        Assert.That(hub.RepushVisited - before, Is.Zero, "consumed once: the next step has nothing to read");
    }
}
