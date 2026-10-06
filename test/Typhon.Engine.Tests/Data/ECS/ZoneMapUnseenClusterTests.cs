using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using System.Runtime.InteropServices;
using Typhon.Engine.Internals;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests;

[Component("Typhon.Test.ECS.ZoneMapUnseen.Item", 1)]
[StructLayout(LayoutKind.Sequential)]
struct ZmUnseenItem
{
    [Index] public long Key;
    public long Payload;
}

[Archetype]
class ZmUnseenArch : Archetype<ZmUnseenArch>
{
    public static readonly Comp<ZmUnseenItem> Item = Register<ZmUnseenItem>();
}

/// <summary>
/// Rule <b>IXS-08</b> (<c>rules/indexing.md</c>): a zone map bounds a cluster only after seeing every value the cluster holds (#1151).
/// </summary>
/// <remarks>
/// <para>
/// Zone maps are not persisted and nothing rebuilds them at open, so every cluster that holds entities when a database is opened starts with no bounds.
/// "No bounds" used to be one state that meant both "nothing here yet" and "never seen", and every widen treated it as the first: the first update after a
/// reopen recorded <c>[newValue, newValue]</c> over a cluster holding dozens of other values, and <see cref="ZoneMapArray.MayContain"/> then pruned that
/// cluster for each of them. Path B dropped the rows silently — the tree was intact and every structural validator passed. Without a runtime no fence
/// recomputed the cluster, so they stayed gone for the session. The market hardening run found it as "index: key … should find item … alone, found 0".
/// </para>
/// <para>
/// The fixture drives both the class (verifier and mutant on one assertion) and the engine (a real reopen through Path B, which is where the rows went).
/// </para>
/// </remarks>
[TestFixture]
unsafe class ZoneMapUnseenClusterTests : TestBase<ZoneMapUnseenClusterTests>
{
    private const string Ixs08Marker = "IXS-08 violated: the zone map prunes a cluster for a value it holds";

    private const int Cluster = 5;

    private static void WidenLong(ZoneMapArray map, int clusterChunkId, long value)
    {
        var v = value;
        map.Widen(clusterChunkId, (byte*)&v);
    }

    /// <summary>The shared assertion: a cluster holding <paramref name="held"/> must not be pruned for any of them.</summary>
    private static void AssertNeverPrunedFor(ZoneMapArray map, int clusterChunkId, long[] held)
    {
        foreach (var v in held)
        {
            Assert.That(map.MayContain(clusterChunkId, v, v), Is.True,
                $"{Ixs08Marker}: cluster {clusterChunkId} holds {v}, but its zone map answers no for it. A widen that started the bounds of a cluster it had "
                + "never seen recorded only the value it carried, and every other row of the cluster vanishes from Path-B scans (#1151).");
        }
    }

    private static long[] Held()
    {
        var held = new long[16];   // fits one cluster of the 8-byte test layout (19 slots)
        for (var i = 0; i < held.Length; i++)
        {
            held[i] = 100 + i;
        }

        return held;
    }

    [Test]
    [VerifiesRule("IXS-08")]
    public void Widen_OnAClusterItHasNotSeen_LeavesItUnbounded()
    {
        // The cluster holds 100..115 when the map is built, as a reopened cluster does; the map has seen none of them.
        var map = new ZoneMapArray(16, sizeof(long), false, false);
        map.MarkUnknown([Cluster]);

        WidenLong(map, Cluster, 1200);

        AssertNeverPrunedFor(map, Cluster, Held());
        Assert.That(map.StateOf(Cluster), Is.EqualTo(ZoneMapArray.Unknown), "a widen cannot leave Unknown — only a recompute, which reads every slot, can");
        Assert.That(map.TryGetBounds(Cluster, out _, out _), Is.False, "an Unknown cluster reports no bounds");
    }

    /// <summary>
    /// The <see cref="RuleMutantAttribute"/> companion: the pre-fix open, where a cluster holding values starts in the same state as an empty one, and the
    /// first widen starts its bounds. The verifier's own assertion must reject it.
    /// </summary>
    [Test]
    [RuleMutant("IXS-08")]
    public void Widen_OnAnUnseenClusterLeftUnmarked_PrunesTheValuesItHolds()
    {
        RuleMutants.AssertDetects("IXS-08", Ixs08Marker, () =>
        {
            var map = new ZoneMapArray(16, sizeof(long), false, false);
            WidenLong(map, Cluster, 1200);
            AssertNeverPrunedFor(map, Cluster, Held());
        });
    }

    /// <summary>One indexed <c>long</c> per entity: the layout every cluster buffer below is built against.</summary>
    private static readonly ArchetypeClusterInfo Layout = ArchetypeClusterInfo.Compute(1, [sizeof(long)]);

    /// <summary>A zeroed native cluster whose slots <c>0..values.Length-1</c> hold <paramref name="values"/> and are occupied. Free with <c>NativeMemory.Free</c>.</summary>
    private static byte* NewCluster(long[] values)
    {
        Assert.That(values.Length, Is.LessThanOrEqualTo(Layout.ClusterSize), "the test cluster must fit the layout");
        var cluster = (byte*)NativeMemory.AllocZeroed((nuint)Layout.ClusterStride);
        ulong occupancy = 0;
        for (var i = 0; i < values.Length; i++)
        {
            *(long*)(cluster + Layout.ComponentOffset(0) + i * Layout.ComponentSize(0)) = values[i];
            occupancy |= 1UL << i;
        }

        *(ulong*)cluster = occupancy;
        return cluster;
    }

    /// <summary>
    /// Every widen form honours Unknown, not just the one the commit path calls: the fence uses the batch forms, and <c>WidenMaskedInto</c> is the one it
    /// runs for every dirty cluster after a reopen. Each widens a value that would bound the cluster to <c>[1200, 1200]</c> if it were allowed to.
    /// </summary>
    [Test]
    public void EveryWidenForm_LeavesUnknownAlone()
    {
        var map = new ZoneMapArray(16, sizeof(long), false, false);
        map.MarkUnknown([Cluster]);
        var v = 1200L;
        var changed = NewCluster([1200L]);
        try
        {
            WidenLong(map, Cluster, v);
            map.WidenMasked(Cluster, 1UL, changed, Layout, 0, 0);

            var store = map.BeginBatch(16);
            try
            {
                Assert.That(map.WidenInto(store, Cluster, (byte*)&v), Is.True, "WidenInto handles an Unknown cluster rather than reporting it out of range");
                map.WidenMaskedInto(store, Cluster, 1UL, changed, Layout, 0, 0);
            }
            finally
            {
                map.EndBatch();
            }
        }
        finally
        {
            NativeMemory.Free(changed);
        }

        Assert.That(map.StateOf(Cluster), Is.EqualTo(ZoneMapArray.Unknown), "no widen form may bound an Unknown cluster");
        AssertNeverPrunedFor(map, Cluster, Held());
    }

    /// <summary>
    /// The only way out of Unknown: a recompute reads every occupied slot, so the bounds it records are the cluster's contents — exact, and pruning again.
    /// Both forms: the fence's batch form and the per-cluster one.
    /// </summary>
    [Test]
    public void Recompute_BoundsAnUnknownCluster_FromEveryOccupiedSlot()
    {
        var held = Held();
        var cluster = NewCluster(held);
        try
        {
            var batched = new ZoneMapArray(16, sizeof(long), false, false);
            batched.MarkUnknown([Cluster]);
            var store = batched.BeginBatch(16);
            try
            {
                batched.RecomputeInto(store, Cluster, cluster, cluster, Layout, 0, 0);
            }
            finally
            {
                batched.EndBatch();
            }

            var single = new ZoneMapArray(16, sizeof(long), false, false);
            single.MarkUnknown([Cluster]);
            single.Recompute(Cluster, cluster, Layout, 0, 0);

            foreach (var map in new[] { batched, single })
            {
                Assert.That(map.StateOf(Cluster), Is.EqualTo(ZoneMapArray.Bounded));
                Assert.That(map.TryGetBounds(Cluster, out var min, out var max), Is.True);
                Assert.That((min, max), Is.EqualTo((100L, 115L)), "the bounds are exactly the cluster's contents");
                AssertNeverPrunedFor(map, Cluster, held);
                Assert.That(map.MayContain(Cluster, 1200, 1200), Is.False, "a recomputed cluster prunes again");
            }
        }
        finally
        {
            NativeMemory.Free(cluster);
        }
    }

    /// <summary><c>Invalidate</c> resets any state, Unknown included, to Unset: its contract is that the caller knows the cluster is empty.</summary>
    [Test]
    public void Invalidate_ResetsAnUnknownClusterToUnset()
    {
        var map = new ZoneMapArray(16, sizeof(long), false, false);
        map.MarkUnknown([Cluster]);

        map.Invalidate(Cluster);

        Assert.That(map.StateOf(Cluster), Is.EqualTo(ZoneMapArray.Unset));
    }

    /// <summary>The empty-cluster case keeps its pruning: a cluster that starts Unset is bounded by its first widen, exactly as before.</summary>
    [Test]
    public void Widen_OnAnUnsetCluster_StillStartsTheBounds()
    {
        var map = new ZoneMapArray(16, sizeof(long), false, false);
        WidenLong(map, Cluster, 1200);

        Assert.That(map.TryGetBounds(Cluster, out var min, out var max), Is.True);
        Assert.That((min, max), Is.EqualTo((1200L, 1200L)));
        Assert.That(map.MayContain(Cluster, 100, 100), Is.False, "a new cluster's bounds are its contents, so pruning still works where it is sound");
    }

    /// <summary>
    /// The engine shape of the bug, through Path B: reopen, update one entity's indexed field, and look every cluster-mate up by its unchanged key.
    /// Before the fix 63 of the 200 returned nothing: the updated entity's whole cluster.
    /// </summary>
    [Test]
    [VerifiesRule("IXS-08")]
    public void AfterAReopen_UpdatingOneIndexedField_KeepsEveryClusterMateFindable()
    {
        const int count = 200;
        const int target = 100;
        var ids = new EntityId[count];

        using (var scope = ServiceProvider.CreateScope())
        {
            using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
            dbe.RegisterComponentFromAccessor<ZmUnseenItem>();
            dbe.InitializeArchetypes();
            using var tx = dbe.CreateQuickTransaction(DurabilityMode.Immediate);
            for (var i = 0; i < count; i++)
            {
                ids[i] = tx.Spawn<ZmUnseenArch>(ZmUnseenArch.Item.Set(new ZmUnseenItem { Key = i + 1, Payload = i }));
            }

            tx.Commit();
        }

        using (var scope = ServiceProvider.CreateScope())
        {
            using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
            dbe.RegisterComponentFromAccessor<ZmUnseenItem>();
            dbe.InitializeArchetypes();

            using (var tx = dbe.CreateQuickTransaction(DurabilityMode.Immediate))
            {
                tx.OpenMut(ids[target]).Write(ZmUnseenArch.Item).Key = count + 1000;
                tx.Commit();
            }

            ZoneMapUnseenClusterTests.AssertEveryEntityFoundByItsKeyThroughPathB(dbe, ids, target, count + 1000L);
        }
    }

    /// <summary>
    /// The shared engine assertion: every entity is found by its own key through a forced full scan — the path zone maps prune — and the scan really ran.
    /// </summary>
    internal static void AssertEveryEntityFoundByItsKeyThroughPathB(DatabaseEngine dbe, EntityId[] ids, int updated, long updatedKey)
    {
        QueryPathProbe.Reset();
        QueryPathProbe.Forced = ClusterScanPath.FullScan;
        try
        {
            var missing = 0;
            var first = -1;
            for (var i = 0; i < ids.Length; i++)
            {
                var key = i == updated ? updatedKey : i + 1L;
                using var r = dbe.CreateReadOnlyTransaction();
                var found = r.Query<ZmUnseenArch>().WhereField<ZmUnseenItem>(x => x.Key == key).Execute();
                if (found.Count != 1 || !found.Contains(ids[i]))
                {
                    missing++;
                    first = first < 0 ? i : first;
                }
            }

            Assert.That(QueryPathProbe.FullScans, Is.EqualTo(ids.Length), "premise: every lookup must run Path B, the path zone maps prune");
            Assert.That(missing, Is.Zero,
                $"{Ixs08Marker}: {missing} of {ids.Length} entities are not found by their own key through Path B after one update (first: entity {first})");
        }
        finally
        {
            QueryPathProbe.Reset();
        }
    }
}

/// <summary>
/// IXS-08 on the open paths that fill clusters after the zone maps are built: a hard crash before the first checkpoint leaves the cluster segment empty at
/// open, and the WAL replay then claims every spawn back into clusters the maps were never told about (#1151 review).
/// </summary>
/// <remarks>
/// Marking the populated clusters where the maps are built, as the first version of the fix did, missed every one of them: the replay ran later, the replayed
/// clusters started Unset, and the first update after the reopen bounded its cluster to the one value it carried. The schema-migration rebuild
/// (<c>RebuildClusterFromChains</c>) fills clusters at the same point in the open and is covered by the same placement, at the end of the open.
/// </remarks>
[TestFixture]
[NonParallelizable]
class ZoneMapUnseenClusterRecoveryTests : TestBase<ZoneMapUnseenClusterRecoveryTests>
{
    /// <summary>Reopen needs WAL segments that outlive an engine dispose; the base class defaults to an in-memory backend that does not.</summary>
    protected override IWalFileIO CreateWalFileIO() => new WalFileIO();

    /// <summary>No periodic checkpoint: one landing between the commit and the crash would empty the replay window.</summary>
    protected override void ConfigureEngineOptions(DatabaseEngineOptions o)
    {
        base.ConfigureEngineOptions(o);
        o.Resources.CheckpointIntervalMs = int.MaxValue;
    }

    [Test]
    [VerifiesRule("IXS-08")]
    public void AfterACrashRecovery_UpdatingOneIndexedField_KeepsEveryClusterMateFindable()
    {
        const int count = 200;
        const int target = 100;
        var ids = new EntityId[count];

        using (var scope = ServiceProvider.CreateScope())
        {
            var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
            dbe.RegisterComponentFromAccessor<ZmUnseenItem>();
            dbe.InitializeArchetypes();
            using (var tx = dbe.CreateQuickTransaction(DurabilityMode.Immediate))
            {
                for (var i = 0; i < count; i++)
                {
                    ids[i] = tx.Spawn<ZmUnseenArch>(ZmUnseenArch.Item.Set(new ZmUnseenItem { Key = i + 1, Payload = i }));
                }

                Assert.That(tx.Commit(), Is.True);
            }

            // Power cut before any checkpoint: the cluster pages are lost, the WAL holds the spawns, and the reopen replays them into fresh clusters.
            dbe.SimulateHardCrash();
        }

        using (var scope = ServiceProvider.CreateScope())
        {
            using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
            dbe.RegisterComponentFromAccessor<ZmUnseenItem>();
            dbe.InitializeArchetypes();

            using (var tx = dbe.CreateQuickTransaction(DurabilityMode.Immediate))
            {
                Assert.That(tx.Open(ids[0]).Read(ZmUnseenArch.Item).Key, Is.EqualTo(1L), "premise: the replay restored the spawns");
                tx.OpenMut(ids[target]).Write(ZmUnseenArch.Item).Key = count + 1000;
                tx.Commit();
            }

            ZoneMapUnseenClusterTests.AssertEveryEntityFoundByItsKeyThroughPathB(dbe, ids, target, count + 1000L);
        }
    }
}
