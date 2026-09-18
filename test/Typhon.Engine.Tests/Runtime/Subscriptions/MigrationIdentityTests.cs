using NUnit.Framework;
using System.Numerics;
using Typhon.Engine.Internals;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests.Runtime.Subscriptions;

/// <summary>
/// What happens to a replicated entity's identity when it changes cluster — the question `SUB-09`'s movement half is about.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this needs its own fixture.</b> The differential oracle migrates entities and passes, but it cannot see this: it reads the server's netIds out of
/// the replication blocks and compares them against the client's, so a server that reissues an identity on every migration and a server that carries one
/// across both produce a client that agrees with the server. The oracle proves consistency, not stability, and stability is the property with the cost.
/// </para>
/// <para>
/// <b>Why stability matters.</b> A netId reissued on a cluster crossing is a leave and an enter for every session watching, which costs a full enter record
/// per client per crossing and throws away the client's interpolation state for that entity — a visible hitch on something that merely walked over a
/// boundary. <c>SUB-09</c>'s <c>STILL MISSING</c> item (1) is exactly this: a migration hook that carries the entry across, with per-worker parking when the
/// destination cluster has no block yet.
/// </para>
/// </remarks>
[TestFixture]
[NonParallelizable]
unsafe class MigrationIdentityTests : TestBase<MigrationIdentityTests>
{
    private const string Profile = "god-world";

    /// <summary>
    /// Records what an entity's netId does across a cluster change.
    /// </summary>
    /// <remarks>
    /// Deliberately not an assertion about which behaviour is right: it reports what the engine does today so the answer is written down somewhere other
    /// than in a reader's inference from <c>ProjectionPass</c>. When the migration hook lands, this fixture is where the change becomes visible.
    /// </remarks>
    [Test]
    public void ANetIdAcrossAClusterChange()
    {
        using var harness = FrameHarness.Create(ProjectionTestSchema.SetupEngine(ServiceProvider), Declare, nameof(MigrationIdentityTests));

        var entity = Spawn(harness, 0f, 0f);
        var session = harness.OpenSessions(1, Profile)[0];

        harness.PrimeBlocks();
        for (var tick = 2L; tick <= 4; tick++)
        {
            harness.RunTick(tick);
            harness.Deliver(session);
        }

        var creatures = harness.PlanIndex(nameof(ProjCreature));
        var before = harness.Replica(session).NetIds(creatures);
        Assert.That(before, Has.Length.EqualTo(1), "the session did not receive the one entity there is");

        var clusterBefore = ClusterOf(harness, creatures);

        // Far enough to land in another cluster: the grid's cells are 256 m, so 4 000 m is unambiguous.
        MoveSpatial(harness, 4000f, 4000f);

        // The fence is what migrates. A spatial write marks the entity; the cluster change happens in the tick fence, which this harness does not run as
        // part of RunTick — so without this call the entity's coordinates move and its cluster does not, and the test measures nothing.
        harness.Engine.WriteTickFence(5);

        for (var tick = 5L; tick <= 9; tick++)
        {
            harness.RunTick(tick);
            harness.Deliver(session);
        }

        var clusterAfter = ClusterOf(harness, creatures);
        var after = harness.Replica(session).NetIds(creatures);

        Assert.That(clusterAfter, Is.Not.EqualTo(clusterBefore), "the entity did not change cluster, so this test measured nothing");
        Assert.That(after, Has.Length.EqualTo(1), "the session lost the entity across the migration and never got it back");

        TestContext.Out.WriteLine($"netId before migration: {before[0]}, after: {after[0]}, cluster {clusterBefore} -> {clusterAfter}");

        // CHARACTERIZATION, not an endorsement. Today the identity is released with the old cluster's slot and a fresh one is leased in the new cluster, so
        // every session watching sees a leave and a full enter for an entity that merely walked over a boundary — and loses its interpolation state for it.
        // That is SUB-09's unbuilt movement half (STILL MISSING item 1: the migration hook that carries the entry across, with per-worker parking when the
        // destination has no block). This assertion is written the way the engine behaves so the gap is recorded rather than inferred, and so that the day
        // the hook lands this fixture goes red and somebody has to come here and flip it.
        Assert.That(after[0], Is.Not.EqualTo(before[0]),
            "the entity KEPT its netId across a cluster change — which is what SUB-09's movement half is supposed to achieve. If the migration hook has "
            + "landed, invert this assertion, drop the [UNBUILT] marker from SUB-09 and give it a verified: line.");
    }

    private static int ClusterOf(FrameHarness harness, int plan)
    {
        var clusters = harness.Interest.LiveClusters(plan);
        Assert.That(clusters, Is.Not.Empty, "the archetype has no live cluster at all");
        return clusters[0].ChunkId;
    }

    private static void Declare(SubscriptionsRegistry subs)
    {
        ProjectionTestSchema.DeclareCreature(subs);
        subs.Profile(Profile, p => p.World().Of<ProjCreature>());
    }

    private static EntityId Spawn(FrameHarness harness, float x, float y)
    {
        using var tx = harness.Engine.CreateQuickTransaction();
        var bounds = new ProjBounds { Bounds = new AABB2F { MinX = x, MinY = y, MaxX = x + 1f, MaxY = y + 1f }, Speed = 1f };
        var ai = new ProjAi { Template = 7, Level = 100, Mode = ProjAiMode.Idle };
        var vitals = new ProjVitals { Health = 5, MaxHealth = 10 };
        var entity = tx.Spawn<ProjCreature>(ProjCreature.Bounds.Set(in bounds), ProjCreature.Ai.Set(in ai), ProjCreature.Vitals.Set(in vitals));
        tx.Commit();
        return entity;
    }

    /// <summary>Moves every live creature through <c>WriteSpatial</c>, the write path the spatial index sees.</summary>
    private static void MoveSpatial(FrameHarness harness, float x, float y)
    {
        using var tx = harness.Engine.CreateQuickTransaction();
        var accessor = tx.For<ProjCreature>();
        try
        {
            foreach (var cluster in accessor.GetClusterEnumerator())
            {
                var occupancy = cluster.OccupancyBits;
                while (occupancy != 0)
                {
                    var slot = BitOperations.TrailingZeroCount(occupancy);
                    occupancy &= occupancy - 1;
                    cluster.WriteSpatial(
                        ProjCreature.Bounds,
                        slot,
                        new ProjBounds { Bounds = new AABB2F { MinX = x, MinY = y, MaxX = x + 1f, MaxY = y + 1f }, Speed = 1f });
                }
            }
        }
        finally
        {
            accessor.Dispose();
        }

        tx.Commit();
    }
}
