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
    [VerifiesRule("SUB-09")]
    public void ANetIdAcrossAClusterChange()
    {
        using var harness = FrameHarness.Create(ProjectionTestSchema.SetupEngine(ServiceProvider), Declare, nameof(MigrationIdentityTests));

        Spawn(harness, 0f, 0f);
        var session = harness.OpenSessions(1, Profile)[0];

        for (var tick = 1L; tick <= 4; tick++)
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

        // SUB-09's movement half, asserted rather than characterized since 2026-09-18. Until the migration hook landed this fixture asserted the OPPOSITE
        // and passed: the identity was released with the old cluster's slot and a fresh one leased in the new one, so every watching session was sent a leave
        // and a full enter for an entity that had merely walked over a boundary, and threw away its interpolation state with the netId. It was written
        // inverted on purpose so that the day the hook landed it would go red and force somebody here; it did, and this is that edit.
        Assert.That(after[0], Is.EqualTo(before[0]),
            "the entity was given a NEW netId for walking over a cluster boundary, so every session watching it saw a leave and a full enter for something "
            + "that did not leave. The migration hook beside the component copy in ExecuteMigrations is what carries the entry across.");
    }

    /// <summary>
    /// The identity survives because the ENTRY was carried, and the counters say which way it went.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why the outcome alone is not enough.</b> A netId that is the same before and after is also what you would see if the allocator happened to hand back
    /// the identity it had just released — the free list is LIFO, so that is not a remote possibility, it is the likely one. Reading the migration counters
    /// says the entry was carried rather than reissued, which is the claim SUB-09 actually makes.
    /// </para>
    /// <para>
    /// It asserts the SUM of the two outcomes rather than one of them, because which applies depends on whether the destination cluster already had a block
    /// when the fence ran — a scheduling detail of this fixture, not a property of the hook. What must hold is that exactly one entry moved and that none was
    /// dropped for want of a destination.
    /// </para>
    /// </remarks>
    [Test]
    [VerifiesRule("SUB-09")]
    public void TheEntryIsCarriedAcrossRatherThanReissued()
    {
        using var harness = FrameHarness.Create(ProjectionTestSchema.SetupEngine(ServiceProvider), Declare, nameof(TheEntryIsCarriedAcrossRatherThanReissued));

        Spawn(harness, 0f, 0f);
        var session = harness.OpenSessions(1, Profile)[0];

        for (var tick = 1L; tick <= 4; tick++)
        {
            harness.RunTick(tick);
            harness.Deliver(session);
        }

        var creatures = harness.PlanIndex(nameof(ProjCreature));
        var state = harness.Subscriptions.ReplicationStates[creatures];

        Assert.That(state.EntriesMigrated + state.EntriesParked, Is.Zero, "nothing has migrated yet, so the counters must be untouched");

        MoveSpatial(harness, 4000f, 4000f);
        harness.Engine.WriteTickFence(5);

        for (var tick = 5L; tick <= 9; tick++)
        {
            harness.RunTick(tick);
            harness.Deliver(session);
        }

        TestContext.Out.WriteLine($"migrated {state.EntriesMigrated}, parked {state.EntriesParked}, dropped {state.ParkedDropped}");

        Assert.Multiple(() =>
        {
            Assert.That(state.EntriesMigrated + state.EntriesParked, Is.EqualTo(1),
                "exactly one watched entity changed cluster, so exactly one entry should have been carried or parked");
            Assert.That(state.ParkedDropped, Is.Zero,
                "the destination was watched by the session that followed the entity there, so no parked entry should have been dropped");
        });
    }

    /// <summary>
    /// The slot the entity left describes nothing afterwards, so whoever takes it next is not published under its identity.
    /// </summary>
    /// <remarks>
    /// This is the other direction of SUB-09, and it is the one that is silently WRONG rather than merely expensive: an entry left behind in the source slot
    /// would be inherited by the next entity to occupy it, which would then be sent to every watching session carrying somebody else's netId, baseline and
    /// motion segment. The read path's <c>EntityId</c> compare is the second line of defence; this asserts the first.
    /// </remarks>
    [Test]
    [VerifiesRule("SUB-09")]
    public void TheSlotTheEntityLeftHoldsNoEntryAfterwards()
    {
        using var harness = FrameHarness.Create(ProjectionTestSchema.SetupEngine(ServiceProvider), Declare, nameof(TheSlotTheEntityLeftHoldsNoEntryAfterwards));

        // TWO of them, so the source cluster still has an occupant after one leaves and its block is not released out from under the assertion.
        Spawn(harness, 0f, 0f);
        Spawn(harness, 2f, 2f);
        var session = harness.OpenSessions(1, Profile)[0];

        for (var tick = 1L; tick <= 4; tick++)
        {
            harness.RunTick(tick);
            harness.Deliver(session);
        }

        var creatures = harness.PlanIndex(nameof(ProjCreature));
        var state = harness.Subscriptions.ReplicationStates[creatures];
        var sourceCluster = ClusterOf(harness, creatures);

        Assert.That(state.Directory.TryGetBlock(sourceCluster, out var block), Is.True, "the source cluster must be watched, or this test proves nothing");
        var layout = state.Layout;
        var hotBefore = (ReplicationHotEntry*)((byte*)block + layout.HotOffset);
        var movedNetId = hotBefore->NetId;
        Assert.That(movedNetId, Is.Not.Zero, "slot zero of the source cluster must hold the entity's entry before it moves");

        MoveSpatial(harness, 4000f, 4000f, onlySlot: 0);
        harness.Engine.WriteTickFence(5);

        // Read the SOURCE block straight after the fence and before any tick can re-project it, so what is asserted is what the migration left behind.
        Assert.That(state.Directory.TryGetBlock(sourceCluster, out var sourceAfter), Is.True, "the source block was released, so there is nothing to assert");
        var hotAfter = (ReplicationHotEntry*)((byte*)sourceAfter + layout.HotOffset);

        Assert.Multiple(() =>
        {
            Assert.That(hotAfter->NetId, Is.Zero, "the slot the entity left still holds an identity, which the next occupant would be published under");
            Assert.That(hotAfter->Entity.RawValue, Is.Zero, "the slot the entity left still names it");
        });
    }

    private static int ClusterOf(FrameHarness harness, int plan)
    {
        var clusters = harness.Replication.LiveClusters(plan);
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
    private static void MoveSpatial(FrameHarness harness, float x, float y, int onlySlot = -1)
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
                    if (onlySlot >= 0 && slot != onlySlot)
                    {
                        // Moving ONE occupant is what leaves the source cluster alive: a cluster the last entity leaves is drained and its block released, so
                        // a test that moved everything could never look at the slot the entity left.
                        continue;
                    }

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
