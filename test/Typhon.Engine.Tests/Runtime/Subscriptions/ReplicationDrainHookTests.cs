using System.Collections.Generic;
using System.Numerics;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests;

/// <summary>
/// #954 / SUB-09 — the cluster-drain hook: when a cluster drains, its replication block is released BEFORE the chunk id can be reissued.
/// </summary>
/// <remarks>
/// <para>
/// This is the one test that proves the hook runs at all. <c>ArchetypeClusterState.ReplicationState</c> is null everywhere else in the suite, so all three
/// drain sites are no-ops in every other test — a hook wired to nothing passes every unit test ever written about the pieces it connects.
/// </para>
/// <para>
/// It also pins the ordering that makes the hook worth having. Chunk ids are recycled through a LIFO free list, so a block left behind at a drained id is
/// found by hits into whatever cluster inherits that id next. Asserting "the directory emptied" is therefore not bookkeeping: it is the difference between a
/// client being told about a live entity and being told about a dead one wearing its address.
/// </para>
/// <para>
/// <b>Which site this reaches.</b> Since #582 there is one: the fence's reclamation (<c>DrainPendingClusterFinalizations</c>, CLUSTERWALK-03) — a destroy
/// commit only records the drain. This fixture reaches it through a runtime-less <c>WriteTickFence</c>; <c>ReplicationDrainHookParallelFenceTests</c>
/// through the dispatched <c>ArchetypeFinalize</c> item. The pure-Transient release cannot be reached at all, because <c>[SpatialIndex]</c> is rejected on
/// Transient components.
/// </para>
/// </remarks>
[TestFixture]
unsafe class ReplicationDrainHookTests : TestBase<ReplicationDrainHookTests>
{
    private const float CellSize = 100f;
    private const long AmpleBudget = 16L * 1024 * 1024;

    private static ClCohPos PointAt(float x, float y) =>
        new() { Bounds = new AABB2F { MinX = x, MinY = y, MaxX = x, MaxY = y }, Mass = 1.0f };

    private DatabaseEngine SetupEngine(IServiceScope scope)
    {
        var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<ClCohPos>();
        dbe.ConfigureSpatialGrid(SpatialGridConfig.Flat(new Vector2(0, 0), new Vector2(1000f, 1000f), CellSize));
        dbe.InitializeArchetypes();
        return dbe;
    }

    private static ArchetypeClusterState ClusterStateOf(DatabaseEngine dbe) =>
        dbe._archetypeStates[Archetype<ClCohUnit>.Metadata.ArchetypeId].ClusterState;

    [Test]
    [Property("CacheSize", 64 * 1024 * 1024)]
    [CancelAfter(60_000)]
    public void DrainingAClusterReleasesItsReplicationBlock()
    {
        using var scope = ServiceProvider.CreateScope();
        using var dbe = SetupEngine(scope);

        var registry = new ResourceRegistry(new ResourceRegistryOptions { Name = "DrainHookTests" });
        var allocator = new MemoryAllocator(registry, new MemoryAllocatorOptions { Name = "DrainHookAllocator" });
        try
        {
            var cs = ClusterStateOf(dbe);
            // The identity allocator is shared database-wide (netIds are global), so it is built beside the state rather than by it. Disposed by cascade with
            // the registry in the finally below.
            var netIds = new NetIdAllocator("NetIds", registry.Runtime);
            var replication = new ArchetypeReplicationState("Creature", registry.Runtime, allocator,
                new ReplicationBlockLayout(cs.Layout.ClusterSize), new SubscriptionsOptions { StatePoolBudgetBytes = AmpleBudget }, netIds);

            // Everything below the fence sees the hook; without this line all three drain sites stay no-ops. AttachTo rather than a raw field assignment,
            // so disposal detaches automatically — the ECS must not be left holding a disposed state.
            replication.AttachTo(cs);

            var ids = new List<EntityId>();
            using (var tx = dbe.CreateQuickTransaction())
            {
                for (var i = 0; i < 200; i++)
                {
                    ids.Add(tx.Spawn<ClCohUnit>(ClCohUnit.Pos.Set(PointAt(5f + (i % 50), 5f + (i / 50)))));
                }
                tx.Commit();
            }

            var tick = 1;
            dbe.WriteTickFence(tick++);

            // Watch every live cluster.
            var live = cs.ReadActiveClusterList(out var liveCount);
            Assert.That(liveCount, Is.GreaterThan(0), "the spawn should have produced clusters, or this test proves nothing");

            for (var i = 0; i < liveCount; i++)
            {
                Assert.That(replication.TryAttachBlock(live[i], out _), Is.True, $"cluster {live[i]} should take a block");
            }

            var watchedAtPeak = replication.WatchedClusterCount;
            var blocksCarved = replication.Pool.BlockCount;
            Assert.That(watchedAtPeak, Is.EqualTo(liveCount));

            // Destroy the whole population, so every cluster drains.
            using (var tx = dbe.CreateQuickTransaction())
            {
                foreach (var id in ids)
                {
                    tx.Destroy(id);
                }
                tx.Commit();
            }

            // The destroy only recorded the drains (#582): the clusters, and their blocks, are still there until the fence reclaims them.
            Assert.That(replication.WatchedClusterCount, Is.EqualTo(watchedAtPeak), "a drained cluster keeps its block until the fence");
            dbe.WriteTickFence(tick++);

            cs.ReadActiveClusterList(out var liveAfter);

            Assert.Multiple(() =>
            {
                Assert.That(liveAfter, Is.Zero, "every cluster should have drained");
                Assert.That(replication.WatchedClusterCount, Is.Zero,
                    "the drain hook must have released every block — a surviving entry would be found by hits into whatever cluster inherits the id");
                Assert.That(replication.Pool.FreeBlockCount, Is.EqualTo(blocksCarved), "every released block must be back on the free list");
                Assert.That(replication.Pool.CommittedBytes, Is.GreaterThan(0), "the pool should still hold its slabs; releasing is not freeing");
            });

            // The decisive follow-up: the ids are now recyclable, so a fresh population must be able to take them without colliding with stale entries.
            using (var tx = dbe.CreateQuickTransaction())
            {
                for (var i = 0; i < 200; i++)
                {
                    tx.Spawn<ClCohUnit>(ClCohUnit.Pos.Set(PointAt(5f + (i % 50), 5f + (i / 50))));
                }
                tx.Commit();
            }
            dbe.WriteTickFence(tick++);

            var reissued = cs.ReadActiveClusterList(out var reissuedCount);
            Assert.That(reissuedCount, Is.GreaterThan(0));

            for (var i = 0; i < reissuedCount; i++)
            {
                Assert.That(replication.TryAttachBlock(reissued[i], out var block), Is.True,
                    $"reissued cluster {reissued[i]} must attach cleanly — a stale directory entry would refuse it");
                Assert.That(block->ChunkId, Is.EqualTo(reissued[i]));
                Assert.That(block->WatchedMask, Is.Zero, "a recycled block must not inherit the drained cluster's watched mask");
            }

            Assert.That(replication.DrainFaults, Is.Zero, $"a drain release was refused or faulted: {replication.LastDrainFault}");

            replication.Dispose();
            Assert.That(cs.ReplicationState, Is.Null,
                "disposal must detach from the ECS — a stale reference would fault every later cluster drain, and the graph can dispose this by cascade");
        }
        finally
        {
            allocator.Dispose();
            registry.Dispose();
        }
    }
}
