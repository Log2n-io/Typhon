using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using System;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Threading;
using Typhon.Engine.Internals;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests;

[Component("Typhon.Test.Reclaim.Tr", 1, StorageMode = StorageMode.Transient)]
[StructLayout(LayoutKind.Sequential)]
struct ReclaimTr
{
    [Field]
    public int Value;

    [Field]
    public int Pad;
}

[Archetype]
partial class ReclaimTrUnit : Archetype<ReclaimTrUnit>
{
    public static readonly Comp<ReclaimTr> Data = Register<ReclaimTr>();
}

[Component("Typhon.Test.Reclaim.MixSv", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct ReclaimMixSv
{
    [Field]
    public int Value;

    [Field]
    public int Pad;
}

[Archetype]
partial class ReclaimMixUnit : Archetype<ReclaimMixUnit>
{
    public static readonly Comp<ReclaimMixSv> Sv = Register<ReclaimMixSv>();
    public static readonly Comp<ReclaimTr> Tr = Register<ReclaimTr>();
}

[Component("Typhon.Test.Reclaim.V", 1, StorageMode = StorageMode.Versioned)]
[StructLayout(LayoutKind.Sequential)]
struct ReclaimV
{
    [Field]
    public int Value;

    [Field]
    public int Pad;
}

[Archetype]
partial class ReclaimVUnit : Archetype<ReclaimVUnit>
{
    public static readonly Comp<ReclaimV> Data = Register<ReclaimV>();
}

/// <summary>
/// #582 face 1, CLUSTERWALK-03: a release that empties a cluster only records it; the tick fence reclaims it — sealed, detached, evicted, freed — and a
/// cluster a claim reached before the seal is kept.
/// </summary>
[TestFixture]
[NonParallelizable]
class ClusterDeferredReclamationTests : TestBase<ClusterDeferredReclamationTests>
{
    public enum Storage
    {
        SingleVersion,
        Transient,
        Versioned,
        Mixed,
    }

    /// <summary>
    /// A destroy that empties a cluster leaves it listed, its chunk allocated, until the fence; a runtime-less host's <c>WriteTickFence</c> reclaims it on a
    /// destroy-only tick (branch 0, which reached no Finalize before), for a pure-Transient archetype too, and for a Versioned one, whose drain happens in the
    /// cleanup of the hidden destroy (VIS-09).
    /// </summary>
    [Test]
    [VerifiesRule("CLUSTERWALK-03")]
    public void ADrainingDestroy_LeavesTheClusterListedUntilTheFence([Values] Storage storage)
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterAll(dbe);

        EntityId id;
        using (var tx = dbe.CreateQuickTransaction())
        {
            id = SpawnOne(tx, storage, 1);
            tx.Commit();
        }

        dbe.WriteTickFence(1);
        var state = dbe._archetypeStates[dbe.GetMetaByRouting(id.ArchetypeId).ArchetypeId].ClusterState;
        var cluster = Listed(dbe, state).Single();
        var versionBefore = state.ClusterSetVersion;

        using (var tx = dbe.CreateQuickTransaction())
        {
            tx.Destroy(id);
            tx.Commit();
        }

        dbe.FlushDeferredCleanups();   // a Versioned destroy hides its slot; the cleanup releases it, and that release drains the cluster

        Assert.That(Listed(dbe, state), Is.EqualTo(new[] { cluster }), "the drained cluster stays listed until the fence");
        Assert.That(IsLive(state, cluster), Is.True, "the drained cluster's chunk stays allocated until the fence");
        Assert.That(state.HasPendingClusterFinalizations, Is.True, "the drain is recorded");
        Assert.That(state.ClusterSetVersion, Is.EqualTo(versionBefore), "the cluster set did not change before the fence");

        dbe.WriteTickFence(2);

        Assert.That(Listed(dbe, state), Is.Empty, "the fence reclaimed the drained cluster");
        Assert.That(IsLive(state, cluster), Is.False, "the fence freed the drained cluster's chunk");
        Assert.That(state.HasPendingClusterFinalizations, Is.False);
        Assert.That(state.LastTickClustersReclaimed, Is.EqualTo(1));
        Assert.That(state.ClusterSetVersion, Is.EqualTo(versionBefore + 1), "one version bump for the reclamation pass");
        if (storage == Storage.Mixed)
        {
            Assert.That(state.TransientSegment.IsChunkLive(cluster), Is.False, "the lockstep transient chunk is freed with the persistent one");
        }
    }

    private static void RegisterAll(DatabaseEngine dbe)
    {
        dbe.RegisterComponentFromAccessor<ClusterWalkData>();
        dbe.RegisterComponentFromAccessor<ReclaimTr>();
        dbe.RegisterComponentFromAccessor<ReclaimMixSv>();
        dbe.RegisterComponentFromAccessor<ReclaimV>();
        dbe.InitializeArchetypes();
    }

    private static EntityId SpawnOne(Transaction tx, Storage storage, int value) => storage switch
    {
        Storage.SingleVersion => tx.Spawn<ClusterWalkUnit>(ClusterWalkUnit.Data.Set(new ClusterWalkData { Value = value })),
        Storage.Transient => tx.Spawn<ReclaimTrUnit>(ReclaimTrUnit.Data.Set(new ReclaimTr { Value = value })),
        Storage.Mixed => tx.Spawn<ReclaimMixUnit>(ReclaimMixUnit.Sv.Set(new ReclaimMixSv { Value = value }), ReclaimMixUnit.Tr.Set(new ReclaimTr { Value = value })),
        _ => tx.Spawn<ReclaimVUnit>(ReclaimVUnit.Data.Set(new ReclaimV { Value = value })),
    };

    /// <summary>
    /// A clean close with a drain pending reclaims it before its final checkpoint, so the summary it records holds no empty cluster and the next open loads
    /// from it without the cluster.
    /// </summary>
    [Test]
    [VerifiesRule("CLUSTERWALK-03")]
    public void ACleanCloseWithADrainPending_ReopensWithoutTheCluster()
    {
        int cluster;
        using (var scope = ServiceProvider.CreateScope())
        {
            var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
            RegisterAll(dbe);
            EntityId keep, victim;
            using (var tx = dbe.CreateQuickTransaction(DurabilityMode.Immediate))
            {
                keep = SpawnOne(tx, Storage.SingleVersion, 1);
                tx.Commit();
            }

            dbe.WriteTickFence(1);
            var state = StateOf<ClusterWalkUnit>(dbe);
            using (var tx = dbe.CreateQuickTransaction(DurabilityMode.Immediate))
            {
                // Fill the first cluster, so the victim lands in a second one it drains alone.
                for (var i = 1; i < state.Layout.ClusterSize; i++)
                {
                    SpawnOne(tx, Storage.SingleVersion, i + 1);
                }

                victim = SpawnOne(tx, Storage.SingleVersion, 999);
                tx.Commit();
            }

            dbe.WriteTickFence(2);
            cluster = ClusterOf<ClusterWalkUnit>(dbe, victim).Cluster;
            Assert.That(cluster, Is.Not.EqualTo(ClusterOf<ClusterWalkUnit>(dbe, keep).Cluster), "precondition: the victim has a cluster of its own");
            using (var tx = dbe.CreateQuickTransaction(DurabilityMode.Immediate))
            {
                tx.Destroy(victim);
                tx.Commit();
            }

            Assert.That(state.HasPendingClusterFinalizations, Is.True, "precondition: a drain is pending at the close");
            dbe.Dispose();
        }

        using (var scope = ServiceProvider.CreateScope())
        {
            var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
            RegisterAll(dbe);
            var state = StateOf<ClusterWalkUnit>(dbe);
            Assert.That(state.ActiveListLoadedFromSummary, Is.True, "the close recorded a summary: no drain was left pending");
            Assert.That(Listed(dbe, state), Does.Not.Contain(cluster), "the close reclaimed the drained cluster");
            Assert.That(IsLive(state, cluster), Is.False);
            dbe.Dispose();
        }
    }

    /// <summary>
    /// A crash after a drain's page reached a checkpoint and before the fence: the reopened file holds an allocated cluster with nothing in it, which the
    /// replay never revisits. The open's scan lists and records it, and the end of the crash replay reclaims it — skipped, its chunk stayed allocated for good.
    /// </summary>
    [Test]
    [VerifiesRule("CLUSTERWALK-03")]
    public void ACrashWithADrainPending_ReclaimsTheClusterAtTheNextOpen()
    {
        int cluster;
        using (var scope = ServiceProvider.CreateScope())
        {
            var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
            RegisterAll(dbe);
            EntityId keep, victim;
            using (var tx = dbe.CreateQuickTransaction(DurabilityMode.Immediate))
            {
                keep = SpawnOne(tx, Storage.SingleVersion, 1);
                tx.Commit();
            }

            dbe.WriteTickFence(1);
            var state = StateOf<ClusterWalkUnit>(dbe);
            using (var tx = dbe.CreateQuickTransaction(DurabilityMode.Immediate))
            {
                for (var i = 1; i < state.Layout.ClusterSize; i++)
                {
                    SpawnOne(tx, Storage.SingleVersion, i + 1);
                }

                victim = SpawnOne(tx, Storage.SingleVersion, 999);
                tx.Commit();
            }

            dbe.WriteTickFence(2);
            cluster = ClusterOf<ClusterWalkUnit>(dbe, victim).Cluster;
            Assert.That(cluster, Is.Not.EqualTo(ClusterOf<ClusterWalkUnit>(dbe, keep).Cluster), "precondition: the victim has a cluster of its own");
            using (var tx = dbe.CreateQuickTransaction(DurabilityMode.Immediate))
            {
                tx.Destroy(victim);
                tx.Commit();
            }

            Assert.That(state.HasPendingClusterFinalizations, Is.True, "precondition: a drain is pending at the crash");
            dbe.ForceCheckpoint();   // the drained page, occupancy 0, reaches the file and the checkpoint moves past the destroy: the replay will not redo it
            dbe.SimulateUncleanShutdownForTest = true;
            dbe.Dispose();
        }

        using (var scope = ServiceProvider.CreateScope())
        {
            var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
            RegisterAll(dbe);
            var state = StateOf<ClusterWalkUnit>(dbe);
            Assert.That(Listed(dbe, state), Does.Not.Contain(cluster), "the reopen reclaimed the empty cluster");
            Assert.That(IsLive(state, cluster), Is.False, "the empty cluster's chunk was freed, not leaked");
            Assert.That(state.HasPendingClusterFinalizations, Is.False);
            dbe.Dispose();
        }
    }

    /// <summary>
    /// The last two entities of a cluster destroyed on two threads at once, over many clusters. Deciding the drain on the occupancy word as it was before the
    /// clear let each release see the other's bit and neither record: the cluster stayed empty and listed for good. Decided after the claim release, on a
    /// fresh read, the later release records, and every cluster is reclaimed.
    /// </summary>
    [Test]
    [CancelAfter(20_000)]
    [VerifiesRule("CLUSTERWALK-03")]
    public void TheLastTwoReleasesOfAClusterOnTwoThreads_RecordItsDrain()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterAll(dbe);
        var state = StateOf<ClusterWalkUnit>(dbe);
        var size = state.Layout.ClusterSize;
        const int clusters = 200;
        using (var tx = dbe.CreateQuickTransaction())
        {
            for (var i = 0; i < clusters * size; i++)
            {
                SpawnOne(tx, Storage.SingleVersion, i);
            }

            tx.Commit();
        }

        dbe.WriteTickFence(1);

        // Thin each cluster to two entities.
        var pairs = new System.Collections.Generic.List<(EntityId A, EntityId B)>();
        using (var tx = dbe.CreateQuickTransaction())
        {
            foreach (var cluster in tx.GetClusterEnumerator<ClusterWalkUnit>())
            {
                var bits = cluster.OccupancyBits;
                var a = cluster.GetEntityId(BitOperations.TrailingZeroCount(bits));
                bits &= bits - 1;
                pairs.Add((a, cluster.GetEntityId(BitOperations.TrailingZeroCount(bits))));
            }
        }

        using (var tx = dbe.CreateQuickTransaction())
        {
            var survivors = new System.Collections.Generic.HashSet<EntityId>();
            foreach (var (a, b) in pairs)
            {
                survivors.Add(a);
                survivors.Add(b);
            }

            foreach (var cluster in tx.GetClusterEnumerator<ClusterWalkUnit>())
            {
                var bits = cluster.OccupancyBits;
                while (bits != 0)
                {
                    var e = cluster.GetEntityId(BitOperations.TrailingZeroCount(bits));
                    bits &= bits - 1;
                    if (!survivors.Contains(e))
                    {
                        tx.Destroy(e);
                    }
                }
            }

            tx.Commit();
        }

        dbe.WriteTickFence(2);
        Assert.That(state.HasPendingClusterFinalizations, Is.False, "precondition: thinning must record no drain");
        var listedBefore = Listed(dbe, state).Length;

        using var barrier = new Barrier(2);
        Exception failure = null;
        void Release(bool first)
        {
            try
            {
                foreach (var pair in pairs)
                {
                    barrier.SignalAndWait();
                    using var tx = dbe.CreateQuickTransaction(DurabilityMode.Deferred);
                    tx.Destroy(first ? pair.A : pair.B);
                    tx.Commit();
                }
            }
            catch (Exception e)
            {
                Interlocked.CompareExchange(ref failure, e, null);
                barrier.RemoveParticipant();
            }
        }

        var t1 = new Thread(() => Release(true));
        var t2 = new Thread(() => Release(false));
        t1.Start();
        t2.Start();
        t1.Join();
        t2.Join();
        Assert.That(failure, Is.Null, $"a release thread threw: {failure}");

        dbe.WriteTickFence(3);
        Assert.That(Listed(dbe, state).Length, Is.EqualTo(listedBefore - pairs.Count), "every cluster whose last two entities were released is reclaimed");
    }

    /// <summary>
    /// A throw in the middle of a reclamation leaves no cluster sealed: sealed, every claim would read it as full and no release would record it again, so it
    /// would be lost for good. It is unsealed, still listed and recorded, and the next fence reclaims it.
    /// </summary>
    [Test]
    [VerifiesRule("CLUSTERWALK-03")]
    public void AThrowMidReclamation_LeavesTheClusterUnsealed_AndTheNextFenceReclaimsIt()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterAll(dbe);
        EntityId id;
        using (var tx = dbe.CreateQuickTransaction())
        {
            id = SpawnOne(tx, Storage.SingleVersion, 1);
            tx.Commit();
        }

        dbe.WriteTickFence(1);
        var state = StateOf<ClusterWalkUnit>(dbe);
        var cluster = Listed(dbe, state).Single();
        Destroy(dbe, id);

        state.DrainAfterSealProbe = _ => throw new InvalidOperationException("staged fault mid-reclamation");
        try
        {
            Assert.That(() => dbe.WriteTickFence(2), Throws.InvalidOperationException.With.Message.EqualTo("staged fault mid-reclamation"));
        }
        finally
        {
            state.DrainAfterSealProbe = null;
        }

        Assert.That(state.ClaimedBits(cluster), Is.Zero, "the fault left the cluster sealed");
        Assert.That(Listed(dbe, state), Is.EqualTo(new[] { cluster }), "the cluster is still listed");
        Assert.That(state.HasPendingClusterFinalizations, Is.True, "the cluster is recorded again");

        dbe.WriteTickFence(3);
        Assert.That(Listed(dbe, state), Is.Empty, "the next fence reclaimed it");
        Assert.That(IsLive(state, cluster), Is.False);
    }

    /// <summary>A cluster drained, refilled and drained again before one fence is recorded twice and freed once.</summary>
    [Test]
    [VerifiesRule("CLUSTERWALK-03")]
    public void AClusterDrainedTwiceBeforeOneFence_IsFreedOnce()
    {
        using var dbe = SpatialEngine();
        var state = dbe._archetypeStates[Archetype<ClCohUnit>.Metadata.ArchetypeId].ClusterState;
        var first = Spawn(dbe, 150f, 250f);
        dbe.WriteTickFence(1);
        var cluster = Listed(dbe, state).Single();
        Destroy(dbe, first);
        var second = Spawn(dbe, 151f, 251f);
        Assert.That(ClusterOf<ClCohUnit>(dbe, second).Cluster, Is.EqualTo(cluster), "precondition: the spawn refilled the drained cluster");
        Destroy(dbe, second);
        Assert.That(state.PendingClusterFinalizationCount, Is.EqualTo(2), "precondition: the cluster is recorded twice");

        var doubleFreesBefore = state.ClusterSegment.DoubleFreeCount;
        dbe.WriteTickFence(2);
        Assert.That(state.LastTickClustersReclaimed, Is.EqualTo(1));
        Assert.That(state.LastTickDrainsKept, Is.EqualTo(1), "the duplicate record is dropped");
        Assert.That(IsLive(state, cluster), Is.False);
        Assert.That(state.ClusterSegment.DoubleFreeCount, Is.EqualTo(doubleFreesBefore), "freed once");
    }

    private static ArchetypeClusterState StateOf<TArch>(DatabaseEngine dbe) where TArch : Archetype<TArch> =>
        dbe._archetypeStates[Archetype<TArch>.Metadata.ArchetypeId].ClusterState;


    /// <summary>
    /// The runtime's fences reclaim a drain made between ticks, on a destroy-only tick of a non-spatial archetype — the parallel fence (whose planner emitted
    /// no Finalize item for branch 0) and the serial one alike.
    /// </summary>
    [Test]
    [CancelAfter(20_000)]
    [VerifiesRule("CLUSTERWALK-03")]
    public void ADrainBetweenTicks_IsReclaimedByTheRuntimesFence([Values] bool parallelFence, [Values(Storage.SingleVersion, Storage.Transient)] Storage storage)
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterAll(dbe);
        var ids = new EntityId[200];
        using (var tx = dbe.CreateQuickTransaction())
        {
            for (var i = 0; i < ids.Length; i++)
            {
                ids[i] = SpawnOne(tx, storage, i);
            }

            tx.Commit();
        }

        dbe.WriteTickFence(1);
        var state = storage == Storage.Transient ? StateOf<ReclaimTrUnit>(dbe) : StateOf<ClusterWalkUnit>(dbe);
        var before = Listed(dbe, state);
        Assert.That(before.Length, Is.GreaterThanOrEqualTo(2), "precondition: more than one cluster");

        // Empty the cluster of the first entity.
        var (victimCluster, victims) = storage == Storage.Transient ? ClusterOf<ReclaimTrUnit>(dbe, ids[0]) : ClusterOf<ClusterWalkUnit>(dbe, ids[0]);
        using (var tx = dbe.CreateQuickTransaction())
        {
            foreach (var v in victims)
            {
                tx.Destroy(v);
            }

            tx.Commit();
        }

        Assert.That(state.HasPendingClusterFinalizations, Is.True, "precondition: the destroy drained a cluster");

        using var ticked = new CountdownEvent(3);
        var seen = 0;
        using (var runtime = TyphonRuntime.Create(dbe, schedule =>
        {
            schedule.PublicTrack.DeclareDag("Test").CallbackSystem("Tick", _ =>
            {
                if (Interlocked.Increment(ref seen) <= 3)
                {
                    ticked.Signal();
                }
            });
        }, new RuntimeOptions { WorkerCount = 4, BaseTickRate = 100, EnableParallelFence = parallelFence }))
        {
            runtime.Start();
            Assert.That(ticked.Wait(TimeSpan.FromSeconds(10)), Is.True, "precondition: the runtime did not tick");
            runtime.Shutdown();
        }

        Assert.That(Listed(dbe, state), Is.EquivalentTo(before.Where(c => c != victimCluster)), "the runtime's fence reclaimed the drained cluster");
        Assert.That(IsLive(state, victimCluster), Is.False);
        Assert.That(state.HasPendingClusterFinalizations, Is.False);
    }

    /// <summary>
    /// A drained cluster stays in its cell until the fence, so the next spawn in that cell refills it; the fence then keeps it, and the entity stays
    /// readable. Destroyed again, the next fence frees it.
    /// </summary>
    [Test]
    [VerifiesRule("CLUSTERWALK-03")]
    public void AClusterRefilledBeforeTheFence_IsKept()
    {
        using var dbe = SpatialEngine();
        var state = dbe._archetypeStates[Archetype<ClCohUnit>.Metadata.ArchetypeId].ClusterState;
        var first = Spawn(dbe, 150f, 250f);
        dbe.WriteTickFence(1);
        var cluster = Listed(dbe, state).Single();

        Destroy(dbe, first);
        Assert.That(state.HasPendingClusterFinalizations, Is.True, "precondition: the drain is pending, the cluster still allocated");
        var second = Spawn(dbe, 151f, 251f);
        Assert.That(ClusterOf<ClCohUnit>(dbe, second).Cluster, Is.EqualTo(cluster), "precondition: the spawn refilled the drained cluster");

        dbe.WriteTickFence(2);
        Assert.That(Listed(dbe, state), Is.EqualTo(new[] { cluster }), "the refilled cluster is kept");
        Assert.That(IsLive(state, cluster), Is.True);
        Assert.That(state.LastTickClustersReclaimed, Is.Zero);
        Assert.That(state.LastTickDrainsKept, Is.EqualTo(1));
        using (var tx = dbe.CreateQuickTransaction())
        {
            Assert.That(tx.IsAlive(second), Is.True, "the refilled entity is readable after the fence");
        }

        Destroy(dbe, second);
        dbe.WriteTickFence(3);
        Assert.That(Listed(dbe, state), Is.Empty);
        Assert.That(IsLive(state, cluster), Is.False);
    }

    /// <summary>A claim that lands in the drained cluster just before its seal makes the seal's CAS fail: the cluster is kept, not freed under the claim.</summary>
    [Test]
    [VerifiesRule("CLUSTERWALK-03")]
    public void AClaimBeforeTheSeal_KeepsTheCluster()
    {
        using var dbe = SpatialEngine();
        var state = dbe._archetypeStates[Archetype<ClCohUnit>.Metadata.ArchetypeId].ClusterState;
        var id = Spawn(dbe, 150f, 250f);
        dbe.WriteTickFence(1);
        var cluster = Listed(dbe, state).Single();
        var cellKey = dbe.Realm0Grid.WorldToCellKey(150f, 250f, 0f);
        Destroy(dbe, id);

        var claimed = (-1, -1);
        state.DrainBeforeSealProbe = drained => claimed = Claim(dbe, state, cellKey);
        try
        {
            dbe.WriteTickFence(2);
        }
        finally
        {
            state.DrainBeforeSealProbe = null;
        }

        Assert.That(claimed.Item1, Is.EqualTo(cluster), "precondition: the claim landed in the drained cluster");
        Assert.That(Listed(dbe, state), Is.EqualTo(new[] { cluster }), "a claimed cluster is not reclaimed");
        Assert.That(IsLive(state, cluster), Is.True, "a claimed cluster's chunk is not freed");
        Assert.That(state.LastTickClustersReclaimed, Is.Zero);
    }

    /// <summary>
    /// A claim after the seal reads the sealed cluster as full through the unchanged claim path and lands elsewhere; the sealed cluster is freed, the claimed
    /// one is live. A spawn racing the seal never lands in a freed cluster.
    /// </summary>
    [Test]
    [VerifiesRule("CLUSTERWALK-03")]
    public void AClaimAfterTheSeal_LandsInAnotherCluster()
    {
        using var dbe = SpatialEngine();
        var state = dbe._archetypeStates[Archetype<ClCohUnit>.Metadata.ArchetypeId].ClusterState;
        var id = Spawn(dbe, 150f, 250f);
        dbe.WriteTickFence(1);
        var cluster = Listed(dbe, state).Single();
        var cellKey = dbe.Realm0Grid.WorldToCellKey(150f, 250f, 0f);
        Destroy(dbe, id);

        var sealedId = -1;
        var claimed = (-1, -1);
        state.DrainAfterSealProbe = drained =>
        {
            sealedId = drained;
            claimed = Claim(dbe, state, cellKey);
        };
        try
        {
            dbe.WriteTickFence(2);
        }
        finally
        {
            state.DrainAfterSealProbe = null;
        }

        Assert.That(sealedId, Is.EqualTo(cluster), "precondition: the drained cluster was sealed");
        Assert.That(claimed.Item1, Is.Not.EqualTo(cluster), "a claim after the seal took the sealed cluster");
        Assert.That(IsLive(state, cluster), Is.False, "the sealed cluster was freed");
        Assert.That(IsLive(state, claimed.Item1), Is.True, "the claimed cluster is live");
        Assert.That(Listed(dbe, state), Is.EqualTo(new[] { claimed.Item1 }));
        Assert.That(state.LastTickClustersReclaimed, Is.EqualTo(1));
    }

    private DatabaseEngine SpatialEngine()
    {
        var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<ClCohPos>();
        dbe.ConfigureSpatialGrid(SpatialGridConfig.Flat(worldMin: new Vector2(0, 0), worldMax: new Vector2(1000, 1000), cellSize: 100f));
        dbe.InitializeArchetypes();
        return dbe;
    }

    private static EntityId Spawn(DatabaseEngine dbe, float x, float y)
    {
        using var tx = dbe.CreateQuickTransaction();
        var pos = new ClCohPos { Bounds = new AABB2F { MinX = x, MinY = y, MaxX = x, MaxY = y }, Mass = 1f };
        var id = tx.Spawn<ClCohUnit>(ClCohUnit.Pos.Set(in pos));
        tx.Commit();
        return id;
    }

    private static void Destroy(DatabaseEngine dbe, EntityId id)
    {
        using var tx = dbe.CreateQuickTransaction();
        tx.Destroy(id);
        tx.Commit();
    }

    /// <summary>A run-time claim in <paramref name="cellKey"/>, as a spawn's placement makes it — on the calling thread, here the fence's.</summary>
    private static (int, int) Claim(DatabaseEngine dbe, ArchetypeClusterState state, int cellKey)
    {
        using var guard = EpochGuard.Enter(dbe.EpochManager);
        var changeSet = dbe.MMF.CreateChangeSet();
        var accessor = state.ClusterSegment.CreateChunkAccessor(changeSet);
        try
        {
            return state.ClaimSlotInCell(cellKey, ref accessor, changeSet, dbe.Realm0Grid, 1);
        }
        finally
        {
            accessor.Dispose();
            changeSet.ReleaseDirtyMarks();
        }
    }

    private static int[] Listed(DatabaseEngine dbe, ArchetypeClusterState state)
    {
        using var guard = EpochGuard.Enter(dbe.EpochManager);
        var ids = state.ReadActiveClusterList(out var count);
        return ids.AsSpan(0, count).ToArray();
    }

    private static bool IsLive(ArchetypeClusterState state, int chunkId) =>
        state.ClusterSegment != null ? state.ClusterSegment.IsChunkLive(chunkId) : state.TransientSegment.IsChunkLive(chunkId);

    /// <summary>The cluster holding <paramref name="id"/>, and every entity in it.</summary>
    private static (int Cluster, EntityId[] Members) ClusterOf<TArch>(DatabaseEngine dbe, EntityId id) where TArch : Archetype<TArch>
    {
        using var tx = dbe.CreateQuickTransaction();
        foreach (var cluster in tx.GetClusterEnumerator<TArch>())
        {
            var members = new System.Collections.Generic.List<EntityId>();
            var bits = cluster.OccupancyBits;
            while (bits != 0)
            {
                members.Add(cluster.GetEntityId(BitOperations.TrailingZeroCount(bits)));
                bits &= bits - 1;
            }

            if (members.Contains(id))
            {
                return (cluster.ChunkId, members.ToArray());
            }
        }

        return (-1, []);
    }
}
