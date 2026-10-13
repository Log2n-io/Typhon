using System;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Typhon.Engine.Internals;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests;

/// <summary>
/// #1187 (IX-07): cluster iteration hands out no writable memory over a component whose indexed fields the tick fence maintains, and writes such a
/// component through <see cref="ClusterRef{TArch}.Set{T}"/>, which records the entity's old keys first. A write through <c>GetSpan</c> / <c>Get</c>
/// recorded nothing, so the fence had no key to move the index entry from and the index kept the old key for good.
/// </summary>
/// <remarks>
/// Reuses <see cref="QfUnit"/>: <see cref="QfData"/> carries a unique <c>Key</c> and a multi-value <c>Group</c>, <see cref="QfOther"/> no index.
/// </remarks>
[TestFixture]
class ClusterRefIndexedWriteTests : TestBase<ClusterRefIndexedWriteTests>
{
    /// <summary>Clusters spawned: enough that the planner sends a unique point lookup to the index path, as in QueryBetweenWriteAndFenceTests.</summary>
    private const int Clusters = 32;

    private delegate void ClusterWrite(ClusterRef<QfUnit> cluster, int slot);

    [TearDown]
    public void ResetProbes() => QueryPathProbe.Reset();

    private DatabaseEngine SetupEngine()
    {
        var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<QfData>();
        dbe.RegisterComponentFromAccessor<QfOther>();
        dbe.RegisterComponentFromAccessor<QfVData>();
        dbe.RegisterComponentFromAccessor<TIdxState>();
        dbe.RegisterComponentFromAccessor<TIdxRuntime>();
        dbe.InitializeArchetypes();
        return dbe;
    }

    private static ArchetypeClusterState ClusterState<TArch>(DatabaseEngine dbe) where TArch : Archetype<TArch>
        => dbe._archetypeStates[Archetype<TArch>.Metadata.ArchetypeId].ClusterState;

    /// <summary>Full clusters of entities with <c>Key = 2 * i</c> and <c>Group = i % 8</c>, fenced: every index current. Indexed by <c>Key / 2</c>.</summary>
    private static EntityId[] Spawn(DatabaseEngine dbe)
    {
        var rows = Clusters * ClusterState<QfUnit>(dbe).Layout.ClusterSize;
        var ids = new EntityId[rows];
        using (var tx = dbe.CreateQuickTransaction())
        {
            for (var i = 0; i < rows; i++)
            {
                var d = new QfData(i % 8, i * 2);
                var o = new QfOther();
                ids[i] = tx.Spawn<QfUnit>(QfUnit.Data.Set(in d), QfUnit.Other.Set(in o));
            }

            tx.Commit();
        }

        dbe.WriteTickFence(0);
        return ids;
    }

    /// <summary>
    /// Runs <paramref name="write"/> on <paramref name="id"/>'s cluster and slot, in a transaction of <paramref name="discipline"/>, and commits.
    /// </summary>
    private static void OnItsCluster(DatabaseEngine dbe, EntityId id, ClusterWrite write,
        CommitDiscipline discipline = CommitDiscipline.TickFence)
    {
        using var tx = dbe.CreateQuickTransaction(DurabilityMode.Deferred, discipline);
        var found = false;
        using (var accessor = tx.For<QfUnit>())
        {
            foreach (var cluster in accessor.GetClusterEnumerator())
            {
                var ids = cluster.EntityIds;
                for (var slot = 0; slot < cluster.ClusterSize; slot++)
                {
                    if ((cluster.OccupancyBits & (1UL << slot)) != 0 && ids[slot] == (long)id.RawValue)
                    {
                        write(cluster, slot);
                        found = true;
                    }
                }
            }
        }

        Assert.That(found, Is.True, $"premise: {id} is in a cluster");
        tx.Commit();
    }

    private static void SetKeyThroughTheCluster(DatabaseEngine dbe, EntityId id, int key, int group) => OnItsCluster(dbe, id, (cluster, slot) =>
    {
        var d = cluster.GetReadOnly(QfUnit.Data, slot);
        d.Key = key;
        d.Group = group;
        cluster.Set(QfUnit.Data, slot, d);
    });

    private static readonly (string name, ClusterScanPath path)[] Paths =
        [("the planner's path", ClusterScanPath.Planner), ("the index path", ClusterScanPath.Selective), ("the scan path", ClusterScanPath.FullScan)];

    private static void OnEveryPath<T>(DatabaseEngine dbe, Func<Transaction, T> query, Action<string, T> check)
    {
        foreach (var (name, path) in Paths)
        {
            QueryPathProbe.Reset();
            QueryPathProbe.Forced = path;
            try
            {
                using var tx = dbe.CreateQuickTransaction();
                check(name, query(tx));
            }
            finally
            {
                QueryPathProbe.Reset();
            }
        }
    }

    [Test]
    [VerifiesRule("IX-07")]
    public void GetSpanAndGet_OnAComponentWithAFenceMaintainedIndex_Throw()
    {
        using var dbe = SetupEngine();
        var ids = Spawn(dbe);
        Exception span = null, single = null;
        var readable = 0;
        OnItsCluster(dbe, ids[3], (cluster, slot) =>
        {
            span = TryGetSpan(cluster, QfUnit.Data);
            single = TryGet(cluster, QfUnit.Data, slot);
            readable = cluster.GetReadOnlySpan(QfUnit.Data)[slot].Key + cluster.GetReadOnly(QfUnit.Data, slot).Key;
        });

        Assert.That(span, Is.InstanceOf<InvalidOperationException>().With.Message.Contains("#1187"), "GetSpan");
        Assert.That(single, Is.InstanceOf<InvalidOperationException>().With.Message.Contains("#1187"), "Get");
        Assert.That(readable, Is.EqualTo(12), "the read-only forms still hand out the column");
    }

    // A ClusterRef is a ref struct, which no lambda can capture: the calls expected to throw run in these.
    private static Exception TryGetSpan<TArch, T>(ClusterRef<TArch> cluster, Comp<T> comp) where TArch : class where T : unmanaged
    {
        try
        {
            cluster.GetSpan(comp);
            return null;
        }
        catch (Exception e)
        {
            return e;
        }
    }

    private static Exception TryGet<TArch, T>(ClusterRef<TArch> cluster, Comp<T> comp, int slot) where TArch : class where T : unmanaged
    {
        try
        {
            cluster.Get(comp, slot);
            return null;
        }
        catch (Exception e)
        {
            return e;
        }
    }

    private static Exception TrySet<TArch, T>(ClusterRef<TArch> cluster, Comp<T> comp, int slot, T value) where TArch : class where T : unmanaged
    {
        try
        {
            cluster.Set(comp, slot, value);
            return null;
        }
        catch (Exception e)
        {
            return e;
        }
    }

    [Test]
    public void GetSpan_OnAComponentWithoutAnIndex_IsStillHandedOut()
    {
        using var dbe = SetupEngine();
        var ids = Spawn(dbe);
        OnItsCluster(dbe, ids[3], (cluster, slot) =>
        {
            cluster.GetSpan(QfUnit.Other)[slot].Hp = 7;
            cluster.MarkDirty(QfUnit.Other);
        });

        using var tx = dbe.CreateQuickTransaction();
        Assert.That(tx.Open(ids[3]).Read(QfUnit.Other).Hp, Is.EqualTo(7));
    }

    /// <summary>
    /// The write the issue reported, through the API that replaces the span: both indexes move at the fence, every path agrees, and between the write and
    /// the fence no path returns the entity at its old key.
    /// </summary>
    [Test]
    [VerifiesRule("IX-07")]
    [VerifiesRule("QFENCE-01")]
    public void Set_MovesTheUniqueAndTheMultiValueKey_AtTheFence()
    {
        using var dbe = SetupEngine();
        var ids = Spawn(dbe);
        SetKeyThroughTheCluster(dbe, ids[100], 100_001, 7);   // Key 200 → 100 001, Group 4 → 7

        OnEveryPath(dbe, tx => tx.Query<QfUnit>().WhereField<QfData>(d => d.Key == 200).Execute(),
            (name, rows) => Assert.That(rows, Is.Empty, $"{name}, before the fence: the entity left 200"));

        dbe.WriteTickFence(1);

        IndexDataOracle.AssertIndexAgreesWithData<QfUnit>(dbe, "after the fence");
        OnEveryPath(dbe, tx => tx.Query<QfUnit>().WhereField<QfData>(d => d.Key == 100_001).Execute(),
            (name, rows) => Assert.That(rows, Is.EquivalentTo(new[] { ids[100] }), $"{name}: the new unique key"));
        OnEveryPath(dbe, tx => tx.Query<QfUnit>().WhereField<QfData>(d => d.Key == 200).Execute(),
            (name, rows) => Assert.That(rows, Is.Empty, $"{name}: the old unique key"));
        OnEveryPath(dbe, tx => tx.Query<QfUnit>().WhereField<QfData>(d => d.Group == 7).Execute(),
            (name, rows) => Assert.That(rows, Does.Contain(ids[100]), $"{name}: the new group"));
        OnEveryPath(dbe, tx => tx.Query<QfUnit>().WhereField<QfData>(d => d.Group == 4).Execute(),
            (name, rows) => Assert.That(rows, Does.Not.Contain(ids[100]), $"{name}: the old group"));

        // The premise of the planner's arm: a unique point lookup on this many clusters takes the index path.
        QueryPathProbe.Reset();
        using var probe = dbe.CreateQuickTransaction();
        probe.Query<QfUnit>().WhereField<QfData>(d => d.Key == 100_001).Execute();
        Assert.That(QueryPathProbe.SelectiveScans, Is.GreaterThan(0), "premise: the planner took the index path");
    }

    /// <summary>
    /// One record per entity per tick, whichever API writes first: the fence moves the key from the one the tree holds to the last value written.
    /// </summary>
    [TestCase(true, TestName = "SetThenOpenMut")]
    [TestCase(false, TestName = "OpenMutThenSet")]
    [VerifiesRule("IX-07")]
    public void Set_AndAPointWrite_InOneTick_LeaveTheKeyOnTheLastValue(bool setFirst)
    {
        using var dbe = SetupEngine();
        var ids = Spawn(dbe);
        var first = setFirst ? 100_001 : 100_003;
        var last = setFirst ? 100_003 : 100_001;
        if (setFirst)
        {
            SetKeyThroughTheCluster(dbe, ids[100], first, 4);
        }

        using (var tx = dbe.CreateQuickTransaction())
        {
            var e = tx.OpenMut(ids[100]);
            var d = e.Read(QfUnit.Data);
            d.Key = setFirst ? last : first;
            e.Set(QfUnit.Data, d);
            tx.Commit();
        }

        if (!setFirst)
        {
            SetKeyThroughTheCluster(dbe, ids[100], last, 4);
        }

        dbe.WriteTickFence(1);

        IndexDataOracle.AssertIndexAgreesWithData<QfUnit>(dbe, "after the fence");
        OnEveryPath(dbe, tx => tx.Query<QfUnit>().WhereField<QfData>(d => d.Key == last).Execute(),
            (name, rows) => Assert.That(rows, Is.EquivalentTo(new[] { ids[100] }), $"{name}: the last value"));
        foreach (var stale in new[] { first, 200 })
        {
            OnEveryPath(dbe, tx => tx.Query<QfUnit>().WhereField<QfData>(d => d.Key == stale).Execute(),
                (name, rows) => Assert.That(rows, Is.Empty, $"{name}: {stale}, neither the first value nor the original"));
        }
    }

    /// <summary>A Set, a destroy, a spawn into the full archetype, the fence: the destroy leaves the keys to the fence, which removes them (IX-06).</summary>
    [Test]
    [VerifiesRule("IX-07")]
    public void Set_ThenDestroy_ThenSpawn_LeavesTheIndexAgreeing()
    {
        using var dbe = SetupEngine();
        var ids = Spawn(dbe);
        SetKeyThroughTheCluster(dbe, ids[5], 100_001, 7);
        using (var tx = dbe.CreateQuickTransaction())
        {
            tx.Destroy(ids[5]);
            tx.Commit();
        }

        EntityId reborn;
        using (var tx = dbe.CreateQuickTransaction())
        {
            var d = new QfData(3, 11);
            var o = new QfOther();
            reborn = tx.Spawn<QfUnit>(QfUnit.Data.Set(in d), QfUnit.Other.Set(in o));
            tx.Commit();
        }

        dbe.WriteTickFence(1);

        IndexDataOracle.AssertIndexAgreesWithData<QfUnit>(dbe, "after the fence");
        foreach (var stale in new[] { 10, 100_001 })
        {
            OnEveryPath(dbe, tx => tx.Query<QfUnit>().WhereField<QfData>(d => d.Key == stale).Execute(),
                (name, rows) => Assert.That(rows, Is.Empty, $"{name}: {stale}, a key of the destroyed entity"));
        }
        OnEveryPath(dbe, tx => tx.Query<QfUnit>().WhereField<QfData>(d => d.Key == 11).Execute(),
            (name, rows) => Assert.That(rows, Is.EquivalentTo(new[] { reborn }), $"{name}: the newcomer"));
    }

    /// <summary>
    /// A Transient indexed component, under either discipline: its index is the fence's under both (FenceMaintainedSlotsUnder), so Set records it even
    /// in a Commit-discipline transaction, which records none of the SingleVersion slots.
    /// </summary>
    [TestCase(CommitDiscipline.TickFence)]
    [TestCase(CommitDiscipline.Commit)]
    [VerifiesRule("IX-07")]
    public void Set_OnATransientIndexedComponent_MovesItsKey(CommitDiscipline discipline)
    {
        using var dbe = SetupEngine();
        EntityId id;
        using (var tx = dbe.CreateQuickTransaction())
        {
            for (var i = 0; i < 100; i++)
            {
                tx.Spawn<TIdxMixedArch>(TIdxMixedArch.State.Set(new TIdxState(i)), TIdxMixedArch.Runtime.Set(new TIdxRuntime(i % 4, 0)));
            }

            id = tx.Spawn<TIdxMixedArch>(TIdxMixedArch.State.Set(new TIdxState(500)), TIdxMixedArch.Runtime.Set(new TIdxRuntime(1, 0)));
            tx.Commit();
        }

        dbe.WriteTickFence(0);
        using (var tx = dbe.CreateQuickTransaction(DurabilityMode.Deferred, discipline))
        {
            using (var accessor = tx.For<TIdxMixedArch>())
            {
                foreach (var cluster in accessor.GetClusterEnumerator())
                {
                    var entityIds = cluster.EntityIds;
                    for (var slot = 0; slot < cluster.ClusterSize; slot++)
                    {
                        if ((cluster.OccupancyBits & (1UL << slot)) != 0 && entityIds[slot] == (long)id.RawValue)
                        {
                            Assert.That(TryGetSpan(cluster, TIdxMixedArch.Runtime), Is.InstanceOf<InvalidOperationException>(),
                                "GetSpan on the indexed Transient column");
                            cluster.Set(TIdxMixedArch.Runtime, slot, new TIdxRuntime(9, 1));
                        }
                    }
                }
            }

            tx.Commit();
        }

        dbe.WriteTickFence(1);
        IndexDataOracle.AssertIndexAgreesWithData<TIdxMixedArch>(dbe, $"after the fence, {discipline}");
        // Read off the Transient tree itself: the oracle checks only the persisted home, and a query at this size scans the data.
        Assert.That(ClusterTransientIndexTests.TransientIndexed(dbe, 9), Is.EquivalentTo(new[] { id }), "the Transient tree, new bucket");
        Assert.That(ClusterTransientIndexTests.TransientIndexed(dbe, 1), Does.Not.Contain(id), "the Transient tree, old bucket");
        using var check = dbe.CreateQuickTransaction();
        Assert.That(check.Query<TIdxMixedArch>().WhereField<TIdxRuntime>(r => r.Bucket == 9).Execute(), Is.EquivalentTo(new[] { id }));
    }

    [Test]
    public void Set_RefusesWhatItCannotStoreInPlace()
    {
        using var dbe = SetupEngine();
        var ids = Spawn(dbe);

        // A SingleVersion component in a Commit-discipline transaction: its writes are staged, and an in-place store would reach neither the commit nor
        // the fence.
        Exception underCommit = null;
        OnItsCluster(dbe, ids[3], (cluster, slot) => underCommit = TrySet(cluster, QfUnit.Other, slot, new QfOther { Hp = 1 }),
            CommitDiscipline.Commit);
        Assert.That(underCommit, Is.InstanceOf<InvalidOperationException>().With.Message.Contains("Commit discipline"));

        // A slot no entity holds.
        using (var tx = dbe.CreateQuickTransaction())
        {
            tx.Destroy(ids[4]);
            tx.Commit();
        }

        // ids[3] and ids[4] were spawned in turn into an empty archetype, so they hold adjacent slots of one cluster.
        Exception empty = null;
        var emptyWasOccupied = true;
        OnItsCluster(dbe, ids[3], (cluster, slot) =>
        {
            emptyWasOccupied = (cluster.OccupancyBits & (1UL << (slot + 1))) != 0;
            empty = TrySet(cluster, QfUnit.Other, slot + 1, new QfOther { Hp = 1 });
        });
        Assert.That(emptyWasOccupied, Is.False, "premise: the slot after ids[3]'s is the destroyed entity's, now empty");
        Assert.That(empty, Is.InstanceOf<ArgumentException>().With.Message.Contains("holds no entity"), "the destroyed entity's slot");

        // A Versioned component: its value is a revision.
        EntityId versioned;
        using (var tx = dbe.CreateQuickTransaction())
        {
            versioned = tx.Spawn<QfVUnit>(QfVUnit.Data.Set(new QfVData(1)));
            tx.Commit();
        }

        Exception revision = null;
        using (var tx = dbe.CreateQuickTransaction())
        {
            using var accessor = tx.For<QfVUnit>();
            foreach (var cluster in accessor.GetClusterEnumerator())
            {
                revision = TrySet(cluster, QfVUnit.Data, 0, new QfVData(2));
            }
        }

        Assert.That(revision, Is.InstanceOf<InvalidOperationException>().With.Message.Contains("Versioned"));
        using var check = dbe.CreateQuickTransaction();
        Assert.That(check.Open(versioned).Read(QfVUnit.Data).Key, Is.EqualTo(1));
    }

    /// <summary>
    /// The fence's clean branch drains records it finds with nothing dirty — a state no write path leaves (each marks the entity dirty in the same call),
    /// staged here by draining the dirty bitmap by hand. The tick before wrote only the unindexed component, so the written-slot union left from it names
    /// another slot: read as this tick's, it made the drain reset the key's records unread.
    /// </summary>
    [Test]
    [VerifiesRule("IX-07")]
    public void TheCleanBranch_DrainsRecordsLeftWithNothingDirty()
    {
        using var dbe = SetupEngine();
        var ids = Spawn(dbe);
        var cs = ClusterState<QfUnit>(dbe);
        OnItsCluster(dbe, ids[100], (cluster, slot) => cluster.Set(QfUnit.Other, slot, new QfOther { Hp = 1 }));
        dbe.WriteTickFence(1);

        SetKeyThroughTheCluster(dbe, ids[100], 100_001, 4);
        cs.ClusterDirtyBitmap.Snapshot();   // the staging: records present, nothing dirty
        Assert.That(cs.ClusterShadowBitmap.AnyTestAndSetSinceClear, Is.True, "premise: the key's records are pending");
        dbe.WriteTickFence(2);

        Assert.That(cs.FenceBranchPath, Is.Not.EqualTo(2), "premise: the fence took its clean branch");
        Assert.That(cs.ClusterShadowBitmap.AnyTestAndSetSinceClear, Is.False, "no record outlives the fence");
        OnEveryPath(dbe, tx => tx.Query<QfUnit>().WhereField<QfData>(d => d.Key == 100_001).Execute(),
            (name, rows) => Assert.That(rows, Is.EquivalentTo(new[] { ids[100] }), $"{name}: the key moved"));
    }

    /// <summary>
    /// A Set on a component without an index still does a write's bookkeeping: the entity dirty for that component, so the fence persists it.
    /// </summary>
    [Test]
    public void Set_OnAComponentWithoutAnIndex_MarksItDirty()
    {
        using var dbe = SetupEngine();
        var ids = Spawn(dbe);
        var cs = ClusterState<QfUnit>(dbe);
        var otherSlot = Archetype<QfUnit>.Metadata.GetSlot(QfUnit.Other._componentTypeId);
        OnItsCluster(dbe, ids[3], (cluster, slot) => cluster.Set(QfUnit.Other, slot, new QfOther { Hp = 9 }));

        Assert.That(cs.ClusterDirtyBitmap.HasDirty, Is.True, "the entity is dirty");
        Assert.That(cs.WrittenSlotUnion & (1 << otherSlot), Is.Not.Zero, "for the component written");
        Assert.That(cs.ClusterShadowBitmap.AnyTestAndSetSinceClear, Is.False, "an unindexed component records no key");
        using var tx = dbe.CreateQuickTransaction();
        Assert.That(tx.Open(ids[3]).Read(QfUnit.Other).Hp, Is.EqualTo(9));
    }
}
