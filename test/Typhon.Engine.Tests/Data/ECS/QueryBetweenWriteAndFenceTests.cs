using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Typhon.Engine.Internals;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests;

#region Components

/// <summary>SingleVersion, so its indexes are maintained at the tick fence under the default TickFence discipline.</summary>
[Component("Typhon.Test.QFence.Data", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct QfData
{
    [Index(AllowMultiple = true)] public int Group;
    [Index] public int Key;

    public QfData(int group, int key)
    {
        Group = group;
        Key = key;
    }
}

/// <summary>A second SingleVersion component without an index: writing it leaves <see cref="QfData"/>'s indexes current.</summary>
[Component("Typhon.Test.QFence.Other", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct QfOther
{
    public int Hp;
    public int Padding;
}

[Archetype]
class QfUnit : Archetype<QfUnit>
{
    public static readonly Comp<QfData> Data = Register<QfData>();
    public static readonly Comp<QfOther> Other = Register<QfOther>();
}

/// <summary>Versioned: its index is maintained at commit, so it has no window to test — the control.</summary>
[Component("Typhon.Test.QFence.VData", 1, StorageMode = StorageMode.Versioned)]
[StructLayout(LayoutKind.Sequential)]
struct QfVData
{
    [Index] public int Key;
    public int Padding;   // a chunk-based segment needs a stride of at least 8 bytes

    public QfVData(int key)
    {
        Key = key;
        Padding = 0;
    }
}

[Archetype]
class QfVUnit : Archetype<QfVUnit>
{
    public static readonly Comp<QfVData> Data = Register<QfVData>();
}

#endregion

/// <summary>
/// What an indexed query answers between an in-place write and the tick fence that brings the indexes up to date (QFENCE-01): never a row whose current
/// value fails the query's condition; possibly not yet a row whose value started matching since the fence; and, for an ordered query, a written entity
/// that still matches placed by its value as of the fence. Every case runs on the planner's path and on both scan paths forced, because the planner picks
/// between them on an estimate and the answer must not depend on which it picked.
/// </summary>
[TestFixture]
class QueryBetweenWriteAndFenceTests : TestBase<QueryBetweenWriteAndFenceTests>
{
    /// <summary>Clusters spawned: enough that the planner sends a unique point lookup to the index path (one key per 8 clusters).</summary>
    private const int Clusters = 32;

    [TearDown]
    public void ResetProbes() => QueryPathProbe.Reset();

    private DatabaseEngine SetupEngine()
    {
        var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<QfData>();
        dbe.RegisterComponentFromAccessor<QfOther>();
        dbe.RegisterComponentFromAccessor<QfVData>();
        dbe.InitializeArchetypes();
        return dbe;
    }

    /// <summary>
    /// Spawns <see cref="Clusters"/> clusters' worth of entities with <c>Key = 2 * i</c> — even keys, so an odd key inside a cluster's range is free — and
    /// <c>Group = i % 8</c>, then fences: every index is current. Returns the entity holding each key, indexed by <c>Key / 2</c>.
    /// </summary>
    private static EntityId[] Spawn(DatabaseEngine dbe)
    {
        var rows = Clusters * dbe._archetypeStates[ArchetypeRegistry.GetMetadata<QfUnit>().ArchetypeId].ClusterState.Layout.ClusterSize;
        var ids = new EntityId[rows];
        for (var written = 0; written < rows;)
        {
            var batch = Math.Min(500, rows - written);
            using var tx = dbe.CreateQuickTransaction();
            for (var i = 0; i < batch; i++)
            {
                var idx = written + i;
                var d = new QfData(idx % 8, idx * 2);
                var o = new QfOther();
                ids[idx] = tx.Spawn<QfUnit>(QfUnit.Data.Set(in d), QfUnit.Other.Set(in o));
            }

            tx.Commit();
            written += batch;
        }

        dbe.WriteTickFence(0);
        return ids;
    }

    /// <summary>Writes in place under the default TickFence discipline, and does NOT fence: the indexes still hold the old keys.</summary>
    private static void Write(DatabaseEngine dbe, EntityId id, Action<QfData> check, Func<QfData, QfData> change)
    {
        using var tx = dbe.CreateQuickTransaction();
        var entity = tx.OpenMut(id);
        var d = entity.Read(QfUnit.Data);
        check?.Invoke(d);
        d = change(d);
        entity.Set(QfUnit.Data, d);
        tx.Commit();
    }

    private static readonly (string name, ClusterScanPath path)[] Paths =
        [("the planner's path", ClusterScanPath.Planner), ("the index path", ClusterScanPath.Selective), ("the scan path", ClusterScanPath.FullScan)];

    /// <summary>Runs <paramref name="query"/> once per path and hands each result to <paramref name="check"/>, named for the assertion messages.</summary>
    private static void OnEveryPath<T>(DatabaseEngine dbe, Func<Transaction, T> query, Action<string, T, Transaction> check)
    {
        foreach (var (name, path) in Paths)
        {
            QueryPathProbe.Reset();
            QueryPathProbe.Forced = path;
            try
            {
                using var tx = dbe.CreateQuickTransaction();
                check(name, query(tx), tx);
            }
            finally
            {
                QueryPathProbe.Reset();
            }
        }
    }

    private static int KeyOf(Transaction tx, EntityId id) => tx.Open(id).Read(QfUnit.Data).Key;

    /// <summary>The rule's guarantee, checked row by row: every entity returned satisfies the condition on its CURRENT value.</summary>
    private static void NoRowFailsItsCondition(string where, IEnumerable<EntityId> rows, Transaction tx, Func<QfData, bool> condition)
    {
        foreach (var id in rows)
        {
            Assert.That(condition(tx.Open(id).Read(QfUnit.Data)), Is.True, $"{where}: {id} was returned, but its current value fails the condition");
        }
    }

    [Test]
    [VerifiesRule("QFENCE-01")]
    public void TheOldValue_IsReturnedByNoPath()
    {
        var dbe = SetupEngine();
        var ids = Spawn(dbe);
        Write(dbe, ids[100], d => Assert.That(d.Key, Is.EqualTo(200)), d => d with { Key = 201 });

        OnEveryPath(dbe, tx => tx.Query<QfUnit>().WhereField<QfData>(d => d.Key == 200).Execute(),
            (name, rows, _) => Assert.That(rows, Is.Empty, $"{name}: the entity's value left 200; the index's stale key must not bring it back"));
        OnEveryPath(dbe, tx => tx.Query<QfUnit>().WhereField<QfData>(d => d.Key == 200).Count(),
            (name, count, _) => Assert.That(count, Is.Zero, $"{name}: Count"));
        OnEveryPath(dbe, tx => tx.Query<QfUnit>().WhereField<QfData>(d => d.Key == 200).Any(),
            (name, any, _) => Assert.That(any, Is.False, $"{name}: Any"));

        // The premise of the planner's arm: a unique point lookup on this many clusters takes the index path, where the stale key lives.
        QueryPathProbe.Reset();
        using var check = dbe.CreateQuickTransaction();
        check.Query<QfUnit>().WhereField<QfData>(d => d.Key == 200).Execute();
        Assert.That(QueryPathProbe.SelectiveScans, Is.GreaterThan(0), "premise: the planner took the index path");
    }

    [Test]
    [VerifiesRule("QFENCE-01")]
    public void AnEntityThatLeftARange_IsReturnedByNoPath_AndOneThatStillMatches_IsReturnedByEvery()
    {
        var dbe = SetupEngine();
        var ids = Spawn(dbe);
        Write(dbe, ids[100], null, d => d with { Key = 201 });       // 200 → 201: still in [100, 300)
        Write(dbe, ids[110], null, d => d with { Key = 100_001 });   // 220 → 100 001: left it

        OnEveryPath(dbe, tx => tx.Query<QfUnit>().WhereField<QfData>(d => d.Key >= 100 && d.Key < 300).Execute(), (name, rows, tx) =>
        {
            NoRowFailsItsCondition(name, rows, tx, d => d.Key >= 100 && d.Key < 300);
            Assert.That(rows, Does.Contain(ids[100]), $"{name}: the entity still matches, at its new value");
            Assert.That(rows, Does.Not.Contain(ids[110]), $"{name}: the entity left the range");
            Assert.That(rows, Has.Count.EqualTo(99), $"{name}: the 100 even keys in [100, 300), less the one that left");
        });
        OnEveryPath(dbe, tx => tx.Query<QfUnit>().WhereField<QfData>(d => d.Key >= 100 && d.Key < 300).Count(),
            (name, count, _) => Assert.That(count, Is.EqualTo(99), $"{name}: Count"));
    }

    [Test]
    [VerifiesRule("QFENCE-01")]
    public void ANewValue_IsNeverWronglyReturned_AndEveryPathFindsItOnceTheFenceMovesItsKey()
    {
        var dbe = SetupEngine();
        var ids = Spawn(dbe);
        Write(dbe, ids[100], null, d => d with { Key = 201 });       // inside its cluster's old [min, max]
        Write(dbe, ids[200], null, d => d with { Key = 100_001 });   // outside every cluster's

        // Before the fence the new values MAY be missing — the index cannot see them, and a scan's zone maps may rule their cluster out. What may never
        // happen is a row that fails the condition.
        foreach (var key in new[] { 201, 100_001 })
        {
            OnEveryPath(dbe, tx => tx.Query<QfUnit>().WhereField<QfData>(d => d.Key == key).Execute(),
                (name, rows, tx) => NoRowFailsItsCondition($"{name}, Key == {key}, before the fence", rows, tx, d => d.Key == key));
        }

        dbe.WriteTickFence(1);
        OnEveryPath(dbe, tx => tx.Query<QfUnit>().WhereField<QfData>(d => d.Key == 201).Execute(),
            (name, rows, _) => Assert.That(rows, Is.EquivalentTo(new[] { ids[100] }), $"{name}: inside the old range, after the fence"));
        OnEveryPath(dbe, tx => tx.Query<QfUnit>().WhereField<QfData>(d => d.Key == 100_001).Execute(),
            (name, rows, _) => Assert.That(rows, Is.EquivalentTo(new[] { ids[200] }), $"{name}: outside every old range, after the fence"));
        OnEveryPath(dbe, tx => tx.Query<QfUnit>().WhereField<QfData>(d => d.Key == 200).Execute(),
            (name, rows, _) => Assert.That(rows, Is.Empty, $"{name}: the old value, after the fence"));
    }

    [Test]
    [VerifiesRule("QFENCE-01")]
    public void TheAllowMultipleIndex_FollowsTheSameRule()
    {
        var dbe = SetupEngine();
        var ids = Spawn(dbe);
        Write(dbe, ids[3], d => Assert.That(d.Group, Is.EqualTo(3)), d => d with { Group = 9 });   // a group no other entity holds

        OnEveryPath(dbe, tx => tx.Query<QfUnit>().WhereField<QfData>(d => d.Group == 3).Execute(), (name, rows, tx) =>
        {
            NoRowFailsItsCondition(name, rows, tx, d => d.Group == 3);
            Assert.That(rows, Does.Not.Contain(ids[3]), $"{name}: the entity left group 3");
        });
        OnEveryPath(dbe, tx => tx.Query<QfUnit>().WhereField<QfData>(d => d.Group == 9).Execute(),
            (name, rows, tx) => NoRowFailsItsCondition($"{name}, the new group before the fence", rows, tx, d => d.Group == 9));

        dbe.WriteTickFence(1);
        OnEveryPath(dbe, tx => tx.Query<QfUnit>().WhereField<QfData>(d => d.Group == 9).Execute(),
            (name, rows, _) => Assert.That(rows, Is.EquivalentTo(new[] { ids[3] }), $"{name}: the new group, after the fence"));
    }

    /// <summary>
    /// An ordered query drops a written entity that no longer matches, keeps one that still does at its position as of the fence, and orders by current
    /// values once the fence has moved the keys. Skip and Take count only what is returned.
    /// </summary>
    [Test]
    [VerifiesRule("QFENCE-01")]
    public void AnOrderedQuery_DropsWhatLeft_KeepsWhatStillMatchesAtItsFencePosition_AndOrdersByValueAfterTheFence()
    {
        var dbe = SetupEngine();
        var ids = Spawn(dbe);
        Write(dbe, ids[5], null, d => d with { Key = 31 });         // 10 → 31: still in [0, 40)
        Write(dbe, ids[10], null, d => d with { Key = 100_001 });   // 20 → 100 001: left it

        // As of the fence: keys 0, 2, …, 38, without the one that left; the one that moved within the range stays where 10 was.
        var asOfFence = new List<EntityId>();
        for (var k = 0; k < 20; k++)
        {
            if (k != 10)
            {
                asOfFence.Add(ids[k]);
            }
        }

        var descendingAsOfFence = new List<EntityId>(asOfFence);
        descendingAsOfFence.Reverse();

        using (var tx = dbe.CreateQuickTransaction())
        {
            var ordered = tx.Query<QfUnit>().WhereField<QfData>(d => d.Key >= 0 && d.Key < 40).OrderByField<QfData, int>(d => d.Key).ExecuteOrdered();
            Assert.That(ordered, Is.EqualTo(asOfFence), "before the fence: the written entity that still matches is placed by its value as of the fence");
            NoRowFailsItsCondition("ordered, before the fence", ordered, tx, d => d.Key >= 0 && d.Key < 40);

            var descending = tx.Query<QfUnit>().WhereField<QfData>(d => d.Key >= 0 && d.Key < 40).OrderByFieldDescending<QfData, int>(d => d.Key)
                .ExecuteOrdered();
            Assert.That(descending, Is.EqualTo(descendingAsOfFence), "descending, before the fence");

            var page = tx.Query<QfUnit>().WhereField<QfData>(d => d.Key >= 0 && d.Key < 40).OrderByField<QfData, int>(d => d.Key).Skip(8).Take(4)
                .ExecuteOrdered();
            Assert.That(page, Is.EqualTo(asOfFence.GetRange(8, 4)), "Skip and Take count only the rows returned");
        }

        dbe.WriteTickFence(1);
        using (var tx = dbe.CreateQuickTransaction())
        {
            var ordered = tx.Query<QfUnit>().WhereField<QfData>(d => d.Key >= 0 && d.Key < 40).OrderByField<QfData, int>(d => d.Key).ExecuteOrdered();
            var keys = new List<int>();
            foreach (var id in ordered)
            {
                keys.Add(KeyOf(tx, id));
            }

            var expected = new List<int>();
            for (var k = 0; k < 40; k += 2)
            {
                if (k != 10 && k != 20)
                {
                    expected.Add(k);
                }
            }

            expected.Add(31);
            expected.Sort();
            Assert.That(keys, Is.EqualTo(expected), "after the fence: ordered by current values");
        }
    }

    /// <summary>
    /// A destroy leaves the index entries of an entity written earlier in the tick for the fence to remove, and clears its occupancy bit and id at once.
    /// The ordered merge resolved that entry to entity 0; no query may return it.
    /// </summary>
    [Test]
    [VerifiesRule("QFENCE-01")]
    public void AnEntityDestroyedAfterItsWrite_IsReturnedByNoQuery()
    {
        var dbe = SetupEngine();
        var ids = Spawn(dbe);
        Write(dbe, ids[5], null, d => d with { Group = 7 });   // a write: the destroy below leaves its index entries for the fence
        using (var tx = dbe.CreateQuickTransaction())
        {
            tx.Destroy(ids[5]);
            tx.Commit();
        }

        using var rtx = dbe.CreateQuickTransaction();
        var ordered = rtx.Query<QfUnit>().WhereField<QfData>(d => d.Key >= 0 && d.Key < 20).OrderByField<QfData, int>(d => d.Key).ExecuteOrdered();
        Assert.That(ordered, Has.Count.EqualTo(9), "ordered: the ten keys in [0, 20) less the destroyed one");
        Assert.That(ordered, Does.Not.Contain(ids[5]).And.Not.Contain(default(EntityId)), "ordered: neither the destroyed entity nor entity 0");
        OnEveryPath(dbe, tx => tx.Query<QfUnit>().WhereField<QfData>(d => d.Key >= 0 && d.Key < 20).Execute(),
            (name, rows, _) => Assert.That(rows, Has.Count.EqualTo(9).And.Not.Contain(ids[5]), $"{name}"));
    }

    /// <summary>
    /// A write to ANOTHER component still makes a later destroy leave this component's index entries for the fence — the destroy sees a pending shadow
    /// capture, which records every indexed slot. Neither the destroyed entity nor the entity respawned into its slot may come back at its stale key.
    /// </summary>
    [Test]
    [VerifiesRule("QFENCE-01")]
    public void ADestroyAfterAWriteToAnotherComponent_LeavesNoStaleRow_EvenOnceTheSlotIsReused()
    {
        var dbe = SetupEngine();
        var ids = Spawn(dbe);
        using (var tx = dbe.CreateQuickTransaction())
        {
            var target = tx.OpenMut(ids[5]);
            var otherCopy = target.Read(QfUnit.Other);
            otherCopy.Hp = 1;
            target.Set(QfUnit.Other, otherCopy);   // the unindexed component only
            tx.Commit();
        }

        using (var tx = dbe.CreateQuickTransaction())
        {
            tx.Destroy(ids[5]);
            tx.Commit();
        }

        using (var tx = dbe.CreateQuickTransaction())
        {
            var ordered = tx.Query<QfUnit>().WhereField<QfData>(d => d.Key >= 0 && d.Key < 20).OrderByField<QfData, int>(d => d.Key).ExecuteOrdered();
            Assert.That(ordered, Has.Count.EqualTo(9).And.Not.Contain(default(EntityId)).And.Not.Contain(ids[5]), "ordered, after the destroy");
        }

        // Every cluster is full, so the spawn takes the freed slot — where the tree still holds the destroyed entity's keys (10, group 5).
        EntityId reborn;
        using (var tx = dbe.CreateQuickTransaction())
        {
            var d = new QfData(3, 999_999);
            var o = new QfOther();
            reborn = tx.Spawn<QfUnit>(QfUnit.Data.Set(in d), QfUnit.Other.Set(in o));
            tx.Commit();
        }

        OnEveryPath(dbe, tx => tx.Query<QfUnit>().WhereField<QfData>(d => d.Key == 10).Execute(),
            (name, rows, _) => Assert.That(rows, Is.Empty, $"{name}: the reborn entity holds 999 999, not the dead one's key"));
        OnEveryPath(dbe, tx => tx.Query<QfUnit>().WhereField<QfData>(d => d.Group == 5).Execute(),
            (name, rows, tx) => NoRowFailsItsCondition($"{name}, the dead one's group", rows, tx, d => d.Group == 5));
        using (var tx = dbe.CreateQuickTransaction())
        {
            var ordered = tx.Query<QfUnit>().WhereField<QfData>(d => d.Key >= 0 && d.Key < 20).OrderByField<QfData, int>(d => d.Key).ExecuteOrdered();
            Assert.That(ordered, Has.Count.EqualTo(9).And.Not.Contain(reborn), "ordered, after the respawn");
        }

        OnEveryPath(dbe, tx => tx.Query<QfUnit>().WhereField<QfData>(d => d.Key == 999_999).Execute(),
            (name, rows, _) => Assert.That(rows, Is.EquivalentTo(new[] { reborn }), $"{name}: the reborn entity under its own key"));
    }

    /// <summary>
    /// A second predicate on a vectorisable field sends the index path down its SIMD branch, which tests that predicate for all 64 slots at once and the
    /// one the range enforced per slot: the re-check of a written entity must run there too.
    /// </summary>
    [Test]
    [VerifiesRule("QFENCE-01")]
    public void ASecondPredicate_RunsTheVectorisedBranch_WhichAlsoDropsTheOldValue()
    {
        Assume.That(System.Runtime.Intrinsics.X86.Avx2.IsSupported, "the index path vectorises only with AVX2; without it this is the scalar case again");
        var dbe = SetupEngine();
        var ids = Spawn(dbe);
        Write(dbe, ids[100], d => Assert.That(d.Group, Is.EqualTo(4)), d => d with { Key = 201 });

        OnEveryPath(dbe, tx => tx.Query<QfUnit>().WhereField<QfData>(d => d.Key == 200 && d.Group == 4).Execute(),
            (name, rows, _) => Assert.That(rows, Is.Empty, $"{name}: Key left 200; Group still matches"));
        OnEveryPath(dbe, tx => tx.Query<QfUnit>().WhereField<QfData>(d => d.Key == 202 && d.Group == 5).Execute(),
            (name, rows, _) => Assert.That(rows, Is.EquivalentTo(new[] { ids[101] }), $"{name}: an unwritten entity is still found"));
    }

    /// <summary>
    /// The re-check costs a query nothing when the tick wrote only other components: positions written every tick must not tax an index query on a
    /// component nobody wrote. It switches on with the first write to the indexed component, and off at the fence.
    /// </summary>
    [Test]
    [VerifiesRule("QFENCE-01")]
    public void AWriteToAnotherComponent_LeavesTheIndexTrusted_AndAWriteToThisOneSwitchesTheRecheckOn()
    {
        var dbe = SetupEngine();
        var ids = Spawn(dbe);
        var meta = ArchetypeRegistry.GetMetadata<QfUnit>();
        var state = dbe._archetypeStates[meta.ArchetypeId].ClusterState;
        var dataSlot = state.IndexSlots[0].Slot;
        Assert.That(state.MayHaveFenceStaleKeys(dataSlot), Is.False, "premise: nothing written since the fence");

        using (var tx = dbe.CreateQuickTransaction())
        {
            var entity = tx.OpenMut(ids[7]);
            var other = entity.Read(QfUnit.Other);
            other.Hp = 99;
            entity.Set(QfUnit.Other, other);
            tx.Commit();
        }

        Assert.That(state.MayHaveFenceStaleKeys(dataSlot), Is.False, "only the unindexed component was written: the index is current");
        OnEveryPath(dbe, tx => tx.Query<QfUnit>().WhereField<QfData>(d => d.Key == 14).Execute(),
            (name, rows, _) => Assert.That(rows, Is.EquivalentTo(new[] { ids[7] }), name));

        Write(dbe, ids[100], null, d => d with { Key = 201 });
        Assert.That(state.MayHaveFenceStaleKeys(dataSlot), Is.True, "the indexed component was written");
        OnEveryPath(dbe, tx => tx.Query<QfUnit>().WhereField<QfData>(d => d.Key == 200).Execute(), (name, rows, _) => Assert.That(rows, Is.Empty, name));

        dbe.WriteTickFence(1);
        Assert.That(state.MayHaveFenceStaleKeys(dataSlot), Is.False, "the fence brought the indexes current");
    }

    /// <summary>A Commit-discipline write reconciles the index at commit: no window — the new value is found and the old one is not, on every path.</summary>
    [Test]
    [VerifiesRule("QFENCE-01")]
    public void ACommitDisciplineWrite_HasNoWindow()
    {
        var dbe = SetupEngine();
        var ids = Spawn(dbe);
        using (var uow = dbe.CreateUnitOfWork())
        {
            using var tx = uow.CreateTransaction(CommitDiscipline.Commit);
            var opened = tx.OpenMut(ids[100]);
            var dataCopy = opened.Read(QfUnit.Data);
            dataCopy.Key = 100_001;
            opened.Set(QfUnit.Data, dataCopy);
            Assert.That(tx.Commit(), Is.True);
        }

        OnEveryPath(dbe, tx => tx.Query<QfUnit>().WhereField<QfData>(d => d.Key == 100_001).Execute(),
            (name, rows, _) => Assert.That(rows, Is.EquivalentTo(new[] { ids[100] }), $"{name}: the new value, no fence needed"));
        OnEveryPath(dbe, tx => tx.Query<QfUnit>().WhereField<QfData>(d => d.Key == 200).Execute(),
            (name, rows, _) => Assert.That(rows, Is.Empty, $"{name}: the old value"));
    }

    /// <summary>A Versioned write reconciles its index at commit: no window.</summary>
    [Test]
    [VerifiesRule("QFENCE-01")]
    public void AVersionedWrite_HasNoWindow()
    {
        var dbe = SetupEngine();
        var rows = Clusters * dbe._archetypeStates[ArchetypeRegistry.GetMetadata<QfVUnit>().ArchetypeId].ClusterState.Layout.ClusterSize;
        var ids = new EntityId[rows];
        using (var tx = dbe.CreateQuickTransaction())
        {
            for (var i = 0; i < rows; i++)
            {
                var d = new QfVData(i * 2);
                ids[i] = tx.Spawn<QfVUnit>(QfVUnit.Data.Set(in d));
            }

            tx.Commit();
        }

        dbe.WriteTickFence(0);
        using (var tx = dbe.CreateQuickTransaction())
        {
            var entity = tx.OpenMut(ids[100]);
            var data = entity.Read(QfVUnit.Data);
            data.Key = 100_001;
            entity.Set(QfVUnit.Data, data);
            tx.Commit();
        }

        foreach (var (name, path) in Paths)
        {
            QueryPathProbe.Reset();
            QueryPathProbe.Forced = path;
            using var tx = dbe.CreateQuickTransaction();
            Assert.That(tx.Query<QfVUnit>().WhereField<QfVData>(d => d.Key == 100_001).Execute(), Is.EquivalentTo(new[] { ids[100] }),
                $"{name}: the new value, no fence needed");
            Assert.That(tx.Query<QfVUnit>().WhereField<QfVData>(d => d.Key == 200).Execute(), Is.Empty, $"{name}: the old value");
        }
    }
}
