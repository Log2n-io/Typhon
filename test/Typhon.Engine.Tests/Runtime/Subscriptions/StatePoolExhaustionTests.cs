using NUnit.Framework;
using System;
using System.Collections.Generic;
using Typhon.Engine.Internals;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests.Runtime;

/// <summary>
/// SUB-32: a pushed cluster that its archetype's state pool cannot give a block is deferred, counted, and replicated the first tick a block frees.
/// </summary>
/// <remarks>
/// <para>
/// <b>What used to happen.</b> The blocks step looked the cluster up, failed to rent, and left it out of the tick, and nothing put its push back. A cluster
/// with no block has nothing of it described, so the push it was denied was the only record that its entities existed: a static entity in it stayed invisible
/// to every client until some unrelated write happened to push it again, and nothing counted or reported it (#1041).
/// </para>
/// <para>
/// <b>Why the survivors are the DENIED entities.</b> Freeing capacity proves nothing about retry if the entities that survive are the ones that already had
/// blocks: they stay held either way. So the scenario keeps exactly the entities the client never received, destroys everything else to return blocks to the
/// pool, and requires every survivor to arrive. Without the deferral none of them is pushed again, and the client never sees one.
/// </para>
/// </remarks>
[TestFixture]
[NonParallelizable]
class StatePoolExhaustionTests : TestBase<StatePoolExhaustionTests>
{
    private const string Profile = "all";

    /// <summary>Twelve by eight entities, one per spatial cell (256 m cells, 300 m apart), so every entity is a cluster of its own.</summary>
    private const int Columns = 12;
    private const int Rows = 8;
    private const float SpacingM = 300f;

    /// <summary>Covers every entity from the origin: the farthest corner is at about 1 960 m.</summary>
    private const double Radius = 2500d;

    /// <summary>One slab of blocks and not two: whatever the block stride, a 64 KiB slab fits and a second does not, as long as a block is under 32 KiB.</summary>
    private const long OneSlabBudget = 96L * 1024;

    private const string NeverReplicatedMarker = "never replicated once blocks were free";
    private const string OfferedForeverMarker = "offered forever";

    private static void Declare(SubscriptionsRegistry subs)
    {
        ProjectionTestSchema.DeclareCreature(subs);
        subs.Profile(Profile, p => p.Sphere(Radius).Of<ProjCreature>());
    }

    private static ProjBounds PointAt(float x, float y) =>
        new() { Bounds = new AABB2F { MinX = x, MinY = y, MaxX = x, MaxY = y }, Speed = 0f };

    [Test]
    [VerifiesRule("SUB-32")]
    [CancelAfter(15_000)]
    public void AClusterDeniedABlockIsReplicatedOnceOneFrees() => AssertDeniedClustersAreReplicated(null);

    /// <summary>Dropping a denied cluster's push, the move the deferral exists to forbid: the survivors never reach the client.</summary>
    [Test]
    [RuleMutant("SUB-32")]
    [CancelAfter(15_000)]
    public void ADroppedPushIsDetected() =>
        RuleMutants.AssertDetects("SUB-32", NeverReplicatedMarker, () => AssertDeniedClustersAreReplicated(hub => hub.DropDeniedPushesForTest = true));

    /// <summary>Keeping a drained cluster's pending pushes: the dead ids are offered forever, and a freed block would be parked on one.</summary>
    [Test]
    [RuleMutant("SUB-32")]
    [CancelAfter(15_000)]
    public void ADrainedClustersPushOutlivingItIsDetected() =>
        RuleMutants.AssertDetects("SUB-32", OfferedForeverMarker, () => AssertDeniedClustersAreReplicated(hub => hub.KeepDrainedRepushesForTest = true));

    private void AssertDeniedClustersAreReplicated(Action<PushHub> mutate)
    {
        var dbe = ProjectionTestSchema.SetupEngine(ServiceProvider);
        var entities = new List<EntityId>(Columns * Rows);
        using (var tx = dbe.CreateQuickTransaction())
        {
            for (var c = 0; c < Columns; c++)
            {
                for (var r = 0; r < Rows; r++)
                {
                    var x = (c - (Columns - 1) / 2f) * SpacingM;
                    var y = (r - (Rows - 1) / 2f) * SpacingM;
                    entities.Add(tx.Spawn<ProjCreature>(ProjCreature.Bounds.Set(PointAt(x, y))));
                }
            }

            tx.Commit();
        }

        using var harness = FrameHarness.Create(dbe, Declare, nameof(StatePoolExhaustionTests),
            new SubscriptionsOptions
            {
                MaxSessions = 2,
                StatePoolBudgetBytes = OneSlabBudget,
                FramePoolBudgetBytes = 32L * 1024 * 1024,
                EnterBudgetPerFrame = 100_000,
                ReplicationCellM = ProjectionTestSchema.ReplicationCellFor(Radius),
            });
        harness.RunFence = true;

        var session = harness.OpenSessions(1, Profile)[0];
        var archetype = harness.PlanIndex(nameof(ProjCreature));
        var pool = harness.Subscriptions.ReplicationStates[archetype].Pool;
        var hub = harness.Subscriptions.Hub;
        mutate?.Invoke(hub);
        Assert.That(harness.Sessions.SetViewpoint(session, new Vector3D(0d, 0d, 0d)), Is.True);

        var tick = 0L;
        for (var i = 0; i < 5; i++)
        {
            harness.RunTick(++tick);
            harness.Deliver(session);
        }

        var refusalsBefore = pool.RentRefusals;
        harness.RunTick(++tick);
        harness.Deliver(session);

        // The precondition, staged rather than hoped for: the pool is at its one slab, some entities were denied a block, and the client holds the rest.
        var capacity = pool.BlockCount;
        var denied = new List<EntityId>();
        foreach (var e in entities)
        {
            if (harness.NetIdOf(e) == 0u)
            {
                denied.Add(e);
            }
        }

        Assert.Multiple(() =>
        {
            Assert.That(capacity, Is.GreaterThan(0).And.LessThan(entities.Count), "the budget must hold some clusters and not all");
            Assert.That(denied, Is.Not.Empty, "some entities must have been denied a block");
            Assert.That(pool.RentRefusals, Is.GreaterThan(0), "every refusal is counted by the pool");
            Assert.That(harness.Replica(session).NetIds(archetype).Length, Is.EqualTo(entities.Count - denied.Count));

            // The hub's gauge is the last blocks step's refusals, whatever their number: the clusters still waiting.
            Assert.That(hub.BlocklessClusters, Is.EqualTo(pool.RentRefusals - refusalsBefore), "the clusters waiting on the last tick");
        });

        // Keep at most half the pool's worth of the denied entities, destroy everything else: the blocks of the destroyed clusters return to the pool.
        var survivors = denied.GetRange(0, Math.Min(denied.Count, capacity / 2));
        var keep = new HashSet<EntityId>(survivors);
        using (var tx = harness.Engine.CreateQuickTransaction())
        {
            foreach (var e in entities)
            {
                if (!keep.Contains(e))
                {
                    tx.Destroy(e);
                }
            }

            tx.Commit();
        }

        var deferralsBefore = hub.BlockDeferrals;
        for (var i = 0; i < 8; i++)
        {
            harness.RunTick(++tick);
            harness.Deliver(session);
        }

        var unseen = 0;
        foreach (var e in survivors)
        {
            unseen += harness.NetIdOf(e) == 0u ? 1 : 0;
        }

        Assert.Multiple(() =>
        {
            Assert.That(unseen, Is.Zero, $"{unseen} of {survivors.Count} entities denied a block were {NeverReplicatedMarker}");
            Assert.That(harness.Replica(session).NetIds(archetype).Length, Is.EqualTo(survivors.Count), "the client holds exactly the survivors");
            Assert.That(hub.BlocklessClusters, Is.Zero, $"nothing is left waiting: a cluster that drained while it waited is forgotten, not {OfferedForeverMarker}");
            Assert.That(pool.BlockCount - pool.FreeBlockCount, Is.EqualTo(survivors.Count),
                "one block per live cluster and none for a drained one: a deferred push that outlived its cluster would hold a block no drain returns");
            Assert.That(hub.BlockDeferrals, Is.GreaterThanOrEqualTo(deferralsBefore), "the cumulative counter never goes back");
        });
    }
}
