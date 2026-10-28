using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Typhon.Engine.Internals;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests;

[Component("Typhon.Test.EIS.Rank", 1, StorageMode = StorageMode.Versioned)]
[StructLayout(LayoutKind.Sequential)]
struct EisRank
{
    [Index(AllowMultiple = true)] public int Tier;
    public int Score;
    [Index] public long Seq;
}

[Component("Typhon.Test.EIS.Tag", 1, StorageMode = StorageMode.Versioned)]
[StructLayout(LayoutKind.Sequential)]
struct EisTag
{
    public int V;
    public int Pad;
}

[Component("Typhon.Test.EIS.SvRank", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct EisSvRank
{
    [Index(AllowMultiple = true)] public int Tier;
    public int Score;
}

/// <summary>The only archetype over <see cref="EisRank"/>: <see cref="Transaction.EnumerateIndex{T,TKey}"/> streams off its one tree.</summary>
[Archetype]
partial class EisArch : Archetype<EisArch>
{
    public static readonly Comp<EisRank> Rank = Register<EisRank>();
}

[Component("Typhon.Test.EIS.FanRank", 1, StorageMode = StorageMode.Versioned)]
[StructLayout(LayoutKind.Sequential)]
struct EisFanRank
{
    [Index(AllowMultiple = true)] public int Tier;
    public int Score;
}

/// <summary>Two archetypes over <see cref="EisFanRank"/>: <see cref="Transaction.EnumerateIndex{T,TKey}"/> fans out and materializes.</summary>
[Archetype]
partial class EisFanA : Archetype<EisFanA>
{
    public static readonly Comp<EisFanRank> Rank = Register<EisFanRank>();
}

[Archetype]
partial class EisFanB : Archetype<EisFanB>
{
    public static readonly Comp<EisFanRank> Rank = Register<EisFanRank>();
    public static readonly Comp<EisTag> Tag = Register<EisTag>();
}

/// <summary>An archetype that promises isolation (its Versioned tag) whose indexed component is SingleVersion.</summary>
[Archetype]
partial class EisMixArch : Archetype<EisMixArch>
{
    public static readonly Comp<EisSvRank> Rank = Register<EisSvRank>();
    public static readonly Comp<EisTag> Tag = Register<EisTag>();
}

/// <summary>
/// #1247: <see cref="Transaction.EnumerateIndex{T,TKey}"/> answers at the transaction's snapshot, as its queries do — the entities its snapshot holds, at the
/// keys and values it holds them, in key order; the transaction's own destroys gone and its own spawns in; its own value writes unseen. The trees hold the
/// newest committed keys, and a destroy takes its entries out at its publish, so an older reader lost destroyed entities and paired newer keys with older
/// values. Races are staged between two MoveNext calls: a streaming walk reads its trees as it goes.
/// </summary>
[TestFixture]
[NonParallelizable]
class EnumerateIndexSnapshotTests : TestBase<EnumerateIndexSnapshotTests>
{
    private DatabaseEngine Open()
    {
        var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<EisRank>();
        dbe.RegisterComponentFromAccessor<EisTag>();
        dbe.RegisterComponentFromAccessor<EisSvRank>();
        dbe.RegisterComponentFromAccessor<EisFanRank>();
        dbe.InitializeArchetypes();
        return dbe;
    }

    private static long _seq = 1_000_000;

    /// <summary>Distinct tiers get their tier as their unique <c>Seq</c>, so the unique index orders as the tiers do; repeated ones get fresh keys.</summary>
    private static List<EntityId> Spawn(DatabaseEngine dbe, params int[] tiers)
    {
        var ids = new List<EntityId>();
        var distinct = tiers.Distinct().Count() == tiers.Length;
        using var tx = dbe.CreateQuickTransaction();
        foreach (var tier in tiers)
        {
            ids.Add(tx.Spawn<EisArch>(EisArch.Rank.Set(new EisRank { Tier = tier, Score = tier * 10, Seq = distinct ? tier : Interlocked.Increment(ref _seq) })));
        }

        Assert.That(tx.Commit(), Is.True);
        return ids;
    }

    private static void Commit(DatabaseEngine dbe, Action<Transaction> write)
    {
        // Its own thread: a transaction is thread-affine, and the reader's thread holds the reader.
        Exception fault = null;
        var t = new Thread(() =>
        {
            try
            {
                using var tx = dbe.CreateQuickTransaction();
                write(tx);
                Assert.That(tx.Commit(), Is.True);
            }
            catch (Exception ex)
            {
                fault = ex;
            }
        });
        t.Start();
        t.Join();
        Assert.That(fault, Is.Null, fault?.ToString());
    }

    private static void SetTier(Transaction tx, EntityId id, int tier) =>
        tx.OpenMut(id).Set(EisArch.Rank, new EisRank { Tier = tier, Score = -1, Seq = Interlocked.Increment(ref _seq) });

    private record struct Row(long Pk, int Key, int Tier, int Score);

    private static List<Row> ByTier(Transaction tx, DatabaseEngine dbe, int min, int max, Action<int> afterRow = null)
    {
        var rows = new List<Row>();
        using var e = tx.EnumerateIndex<EisRank, int>(dbe.GetIndexRef<EisRank, int>(x => x.Tier), min, max);
        while (e.MoveNext())
        {
            rows.Add(new Row(e.CurrentEntityPK, e.CurrentKey, e.CurrentComponent.Tier, e.CurrentComponent.Score));
            afterRow?.Invoke(rows.Count);
        }

        return rows;
    }

    private static List<long> BySeq(Transaction tx, DatabaseEngine dbe, long min, long max, Action<int> afterRow = null)
    {
        var rows = new List<long>();
        using var e = tx.EnumerateIndex<EisRank, long>(dbe.GetIndexRef<EisRank, long>(x => x.Seq), min, max);
        while (e.MoveNext())
        {
            Assert.That(e.CurrentComponent.Seq, Is.EqualTo(e.CurrentKey), "the key and the value agree");
            rows.Add(e.CurrentEntityPK);
            afterRow?.Invoke(rows.Count);
        }

        return rows;
    }

    private static long[] Pks(IEnumerable<EntityId> ids) => ids.Select(x => (long)x.RawValue).ToArray();

    private static void AssertSnapshotRows(List<Row> rows, IReadOnlyList<(EntityId Id, int Tier)> expected)
    {
        Assert.That(rows.Select(r => r.Key), Is.Ordered, "key order");
        Assert.That(rows.All(r => r.Key == r.Tier), Is.True, "each row's key is its value's: " + string.Join(",", rows));
        Assert.That(rows.Select(r => (r.Pk, r.Key)), Is.EquivalentTo(expected.Select(x => ((long)x.Id.RawValue, x.Tier))));
    }

    // ── Changes committed before the walk ──────────────────────────────────────────────────────────────────────────────────────────────────────────────

    [Test]
    [VerifiesRule("VIS-13")]
    public void AnOlderReader_FindsADestroyNewerThanItsSnapshot_OnBothIndexKinds()
    {
        using var dbe = Open();
        var ids = Spawn(dbe, 1, 2, 3, 4, 5);
        using var reader = dbe.CreateQuickTransaction();
        Commit(dbe, tx => tx.Destroy(ids[2]));

        AssertSnapshotRows(ByTier(reader, dbe, 1, 5), ids.Select((id, i) => (id, i + 1)).ToList());
        Assert.That(BySeq(reader, dbe, 1, 5), Is.EqualTo(Pks(ids)), "the unique index, in key order");
    }

    [Test]
    [VerifiesRule("VIS-13")]
    public void AnOlderReader_FindsAnUpdatedEntity_AtItsSnapshotKeyAndValue()
    {
        using var dbe = Open();
        var ids = Spawn(dbe, 5, 7);
        using var reader = dbe.CreateQuickTransaction();
        Commit(dbe, tx =>
        {
            SetTier(tx, ids[0], 7);
            SetTier(tx, ids[1], 5);
        });

        AssertSnapshotRows(ByTier(reader, dbe, 5, 5), [(ids[0], 5)]);
        AssertSnapshotRows(ByTier(reader, dbe, 0, 100), [(ids[0], 5), (ids[1], 7)]);
        Assert.That(BySeq(reader, dbe, 0, long.MaxValue), Is.EqualTo(Pks(ids)), "the unique index at the snapshot's keys, not the moved ones");
    }

    [Test]
    [VerifiesRule("VIS-13")]
    public void AnOlderReader_DoesNotFindASpawnNewerThanItsSnapshot()
    {
        using var dbe = Open();
        var ids = Spawn(dbe, 5, 5);
        using var reader = dbe.CreateQuickTransaction();
        Spawn(dbe, 5);

        AssertSnapshotRows(ByTier(reader, dbe, 5, 5), [(ids[0], 5), (ids[1], 5)]);
    }

    [Test]
    [VerifiesRule("VIS-13")]
    public void TheTransactionsOwnChanges_AreSeenAsItsQueriesSeeThem()
    {
        using var dbe = Open();
        var ids = Spawn(dbe, 5, 5, 5);
        using var tx = dbe.CreateQuickTransaction();
        tx.Destroy(ids[0]);
        SetTier(tx, ids[1], 9);   // its own value write: unseen by its field queries
        var spawned = tx.Spawn<EisArch>(EisArch.Rank.Set(new EisRank { Tier = 5, Score = 99, Seq = Interlocked.Increment(ref _seq) }));

        var rows = ByTier(tx, dbe, 0, 100);
        var query = tx.Query<EisArch>().WhereField<EisRank>(x => x.Tier >= 0 && x.Tier <= 100).Execute();
        Assert.That(rows.Select(r => r.Pk), Is.EquivalentTo(Pks(query)), "the same entities as its query");
        AssertSnapshotRows(rows, [(ids[1], 5), (ids[2], 5), (spawned, 5)]);
    }

    [Test]
    [VerifiesRule("VIS-13")]
    public void ASingleVersionKey_InAnArchetypeThatPromisesIsolation_AnswersAtTheSnapshot()
    {
        using var dbe = Open();
        var ids = new List<EntityId>();
        using (var tx = dbe.CreateQuickTransaction())
        {
            for (var i = 0; i < 5; i++)
            {
                ids.Add(tx.Spawn<EisMixArch>(EisMixArch.Rank.Set(new EisSvRank { Tier = 5, Score = i }), EisMixArch.Tag.Set(new EisTag { V = i })));
            }

            Assert.That(tx.Commit(), Is.True);
        }

        using var reader = dbe.CreateQuickTransaction();
        Commit(dbe, tx =>
        {
            tx.Spawn<EisMixArch>(EisMixArch.Rank.Set(new EisSvRank { Tier = 5, Score = 50 }), EisMixArch.Tag.Set(new EisTag { V = 50 }));
            tx.Destroy(ids[1]);
        });

        var found = new List<long>();
        using (var e = reader.EnumerateIndex<EisSvRank, int>(dbe.GetIndexRef<EisSvRank, int>(x => x.Tier), 5, 5))
        {
            while (e.MoveNext())
            {
                found.Add(e.CurrentEntityPK);
            }
        }

        Assert.That(found, Is.EquivalentTo(Pks(ids)), "the five the snapshot holds: not the later spawn, the later destroy still");
        Assert.That(found, Is.EquivalentTo(Pks(reader.Query<EisMixArch>().WhereField<EisSvRank>(x => x.Tier == 5).Execute())), "as the query answers");
    }

    [Test]
    [VerifiesRule("VIS-13")]
    public void AFanOutOverTwoArchetypes_AnswersAtTheSnapshot()
    {
        using var dbe = Open();
        EntityId a1, a3, a5, b2, b4;
        using (var tx = dbe.CreateQuickTransaction())
        {
            a1 = tx.Spawn<EisFanA>(EisFanA.Rank.Set(new EisFanRank { Tier = 1 }));
            a3 = tx.Spawn<EisFanA>(EisFanA.Rank.Set(new EisFanRank { Tier = 3 }));
            a5 = tx.Spawn<EisFanA>(EisFanA.Rank.Set(new EisFanRank { Tier = 5 }));
            b2 = tx.Spawn<EisFanB>(EisFanB.Rank.Set(new EisFanRank { Tier = 2 }), EisFanB.Tag.Set(new EisTag()));
            b4 = tx.Spawn<EisFanB>(EisFanB.Rank.Set(new EisFanRank { Tier = 4 }), EisFanB.Tag.Set(new EisTag()));
            Assert.That(tx.Commit(), Is.True);
        }

        using var reader = dbe.CreateQuickTransaction();
        Commit(dbe, tx =>
        {
            tx.Destroy(a3);
            tx.OpenMut(b2).Set(EisFanB.Rank, new EisFanRank { Tier = 50 });
            tx.Spawn<EisFanB>(EisFanB.Rank.Set(new EisFanRank { Tier = 3 }), EisFanB.Tag.Set(new EisTag()));
        });

        var rows = new List<(long Pk, int Key, int Tier)>();
        using (var e = reader.EnumerateIndex<EisFanRank, int>(dbe.GetIndexRef<EisFanRank, int>(x => x.Tier), 0, 100))
        {
            while (e.MoveNext())
            {
                rows.Add((e.CurrentEntityPK, e.CurrentKey, e.CurrentComponent.Tier));
            }
        }

        Assert.That(rows.Select(r => r.Key), Is.Ordered, "key order across the two archetypes");
        Assert.That(rows.All(r => r.Key == r.Tier), Is.True, "keys agree with values");
        Assert.That(rows.Select(r => r.Pk), Is.EquivalentTo(Pks([a1, b2, a3, b4, a5])), "the five the snapshot holds");
    }

    /// <summary>
    /// Counted, not timed: only the entities a change re-keyed or destroyed are read at the snapshot beside the trees. An update that moves no key leaves
    /// its entity a tree row at its snapshot key, its value read through its chain; a destroy outside the range, in a cluster no newer update touched, is
    /// passed over on its slot's bytes before any probe.
    /// </summary>
    [Test]
    [VerifiesRule("VIS-13")]
    public void OnlyReKeyedAndDestroyedEntitiesInRange_AreReadBesideTheTrees()
    {
        using var dbe = Open();
        var ids = Spawn(dbe, Enumerable.Range(1, 40).ToArray());
        using var reader = dbe.CreateQuickTransaction();
        Commit(dbe, tx =>
        {
            for (var i = 0; i < 10; i++)
            {
                tx.OpenMut(ids[i]).Set(EisArch.Rank, new EisRank { Tier = i + 1, Score = 777, Seq = i + 1 });   // the keys stay
            }
        });

        QueryPathProbe.Reset();
        var rows = ByTier(reader, dbe, 1, 40);
        Assert.That(QueryPathProbe.IndexSideProbes, Is.Zero, "updates that move no key: no entity read beside the trees");
        AssertSnapshotRows(rows, Expected(ids));
        Assert.That(rows.Where(r => r.Pk == (long)ids[3].RawValue).Single().Score, Is.EqualTo(40), "the value at the snapshot, through the chain");

        using var reader2 = dbe.CreateQuickTransaction();
        Commit(dbe, tx => tx.Destroy(ids[35]));
        QueryPathProbe.Reset();
        AssertSnapshotRows(ByTier(reader2, dbe, 1, 10), Expected(ids).Take(10).ToList());
        Assert.That(QueryPathProbe.IndexSideProbes, Is.Zero, "a destroy outside the range, its bytes at the snapshot: passed over unprobed");

        using var reader3 = dbe.CreateQuickTransaction();
        Commit(dbe, tx => SetTier(tx, ids[20], 2));
        QueryPathProbe.Reset();
        AssertSnapshotRows(ByTier(reader3, dbe, 1, 40), Expected(ids).Where(x => x.Id != ids[35]).ToList());
        Assert.That(QueryPathProbe.IndexSideProbes, Is.EqualTo(1), "the one re-keyed entity");
        QueryPathProbe.Reset();
    }

    // ── Changes published during a streaming walk, between two MoveNext calls ──────────────────────────────────────────────────────────────────────────
    // 2 000 distinct keys span many leaves: the change lands in one the cursor has not read yet. In one leaf, read whole at the first MoveNext, the walk
    // already holds the rows the change takes out.

    private const int Wide = 2000;

    private static List<(EntityId Id, int Tier)> Expected(List<EntityId> ids) => ids.Select((id, i) => (id, i + 1)).ToList();

    /// <summary>A destroy of an entity the cursor has not reached yet takes its entries out before the cursor gets there.</summary>
    [Test]
    [VerifiesRule("VIS-13")]
    public void ADestroyAheadOfTheCursor_DuringTheWalk_IsStillFound()
    {
        using var dbe = Open();
        var ids = Spawn(dbe, Enumerable.Range(1, Wide).ToArray());
        using var reader = dbe.CreateQuickTransaction();
        using var reader2 = dbe.CreateQuickTransaction();   // the same snapshot: the first walk's destroy is newer than it too

        var rows = ByTier(reader, dbe, 1, Wide, n =>
        {
            if (n == 1)
            {
                Commit(dbe, tx => tx.Destroy(ids[Wide - 100]));
            }
        });
        AssertSnapshotRows(rows, Expected(ids));

        var seq = BySeq(reader2, dbe, 1, Wide, n =>
        {
            if (n == 1)
            {
                Commit(dbe, tx => tx.Destroy(ids[Wide - 50]));
            }
        });
        Assert.That(seq, Is.EqualTo(Pks(ids)), "the unique index");
    }

    /// <summary>An update moves an entity the cursor has not reached yet behind it: its entry leaves the range ahead before the cursor gets there.</summary>
    [Test]
    [VerifiesRule("VIS-13")]
    public void AnUpdateMovingAnEntityBehindTheCursor_DuringTheWalk_IsFoundAtItsSnapshotKey()
    {
        using var dbe = Open();
        var ids = Spawn(dbe, Enumerable.Range(1, Wide).ToArray());
        using var reader = dbe.CreateQuickTransaction();

        var rows = ByTier(reader, dbe, 1, Wide, n =>
        {
            if (n == 2)
            {
                Commit(dbe, tx => SetTier(tx, ids[Wide - 100], 1));
            }
        });
        AssertSnapshotRows(rows, Expected(ids));
    }

    /// <summary>An update moves an entity the cursor already returned ahead of it: it is not returned twice.</summary>
    [Test]
    [VerifiesRule("VIS-13")]
    public void AnUpdateMovingAReturnedEntityAheadOfTheCursor_DuringTheWalk_IsNotReturnedTwice()
    {
        using var dbe = Open();
        var ids = Spawn(dbe, Enumerable.Range(1, Wide).ToArray());
        using var reader = dbe.CreateQuickTransaction();

        var rows = ByTier(reader, dbe, 1, 2 * Wide, n =>
        {
            if (n == 1)
            {
                Commit(dbe, tx => SetTier(tx, ids[0], Wide + 500));
            }
        });
        AssertSnapshotRows(rows, Expected(ids));
    }

    /// <summary>
    /// A key's entries are read together; a destroy of one of them publishes after that read and before the walk returns it. Its slot is hidden by then:
    /// the row is decided by the entity's record, at the snapshot — the change is newer than the key's read, so no later read accounts for it.
    /// </summary>
    [Test]
    [VerifiesRule("VIS-13")]
    public void ADestroyOfAnEntityOfTheKeyBeingWalked_IsStillFound()
    {
        using var dbe = Open();
        var ids = Spawn(dbe, Enumerable.Repeat(5, 3).Concat(Enumerable.Range(6, 50)).ToArray());
        using var reader = dbe.CreateQuickTransaction();

        var rows = ByTier(reader, dbe, 5, 100, n =>
        {
            if (n == 1)
            {
                Commit(dbe, tx =>
                {
                    tx.Destroy(ids[0]);
                    tx.Destroy(ids[1]);
                    tx.Destroy(ids[2]);
                });
            }
        });
        Assert.That(rows.Select(r => r.Key), Is.Ordered);
        Assert.That(rows.Select(r => r.Pk), Is.EquivalentTo(Pks(ids)), "every entity of key 5, and the rest");
    }

    /// <summary>
    /// A destroy of the entity whose key comes right after the one just returned: its entry leaves the stretch between the key the cursor read last and the
    /// next one it reads. It is read at the snapshot all the same — the floor is the PREVIOUS key read, not the one just read.
    /// </summary>
    [Test]
    [VerifiesRule("VIS-13")]
    public void ADestroyOfTheNextKey_DuringTheWalk_IsStillFound()
    {
        using var dbe = Open();
        var ids = Spawn(dbe, Enumerable.Range(1, Wide).ToArray());
        using var reader = dbe.CreateQuickTransaction();
        using var reader2 = dbe.CreateQuickTransaction();
        var rows = ByTier(reader, dbe, 1, Wide, n =>
        {
            if (n == 1)
            {
                Commit(dbe, tx => tx.Destroy(ids[1]));
            }
        });
        AssertSnapshotRows(rows, Expected(ids));

        var seq = BySeq(reader2, dbe, 1, Wide, n =>
        {
            if (n == 1)
            {
                Commit(dbe, tx => tx.Destroy(ids[2]));
            }
        });
        Assert.That(seq, Is.EqualTo(Pks(ids)), "the unique index");
    }

    /// <summary>
    /// A side row merged between two values of one AllowMultiple key: the cursor's key is its own, so the key's remaining values come back under it, not
    /// under the side row's.
    /// </summary>
    [Test]
    [VerifiesRule("VIS-13")]
    public void ASideRowMergedInsideAMultiValueKey_LeavesTheKeysOtherValuesUnderTheirKey()
    {
        using var dbe = Open();
        var ids = Spawn(dbe, 5, 5);
        using var tx = dbe.CreateQuickTransaction();
        tx.Destroy(ids[0]);
        var spawned = tx.Spawn<EisArch>(EisArch.Rank.Set(new EisRank { Tier = 3, Score = 30, Seq = Interlocked.Increment(ref _seq) }));
        AssertSnapshotRows(ByTier(tx, dbe, 0, 100), [(spawned, 3), (ids[1], 5)]);
    }

    // ── An oracle: the query's answer, under random changes before and during the walk ─────────────────────────────────────────────────────────────────

    [Test]
    [VerifiesRule("VIS-13")]
    public void UnderRandomChangesBeforeAndDuringTheWalk_ItAgreesWithTheQueryAtTheSnapshot([Values(1, 2, 3, 4, 5, 6)] int seed)
    {
        using var dbe = Open();
        var rng = new Random(seed);
        var ids = Spawn(dbe, Enumerable.Range(0, 3000).Select(_ => rng.Next(400)).ToArray());
        using var reader = dbe.CreateQuickTransaction();
        var expected = reader.Query<EisArch>().WhereField<EisRank>(x => x.Tier >= 50 && x.Tier <= 300).OrderByField<EisRank, int>(x => x.Tier).ExecuteOrdered();
        var live = new List<EntityId>(ids);

        void Churn(int n)
        {
            Commit(dbe, tx =>
            {
                for (var k = 0; k < n; k++)
                {
                    var i = rng.Next(live.Count);
                    switch (rng.Next(3))
                    {
                        case 0:
                            tx.Destroy(live[i]);
                            live.RemoveAt(i);
                            break;
                        case 1:
                            SetTier(tx, live[i], rng.Next(400));
                            break;
                        default:
                            live.Add(tx.Spawn<EisArch>(EisArch.Rank.Set(new EisRank { Tier = rng.Next(400), Seq = Interlocked.Increment(ref _seq) })));
                            break;
                    }
                }
            });
        }

        Churn(60);
        var rows = ByTier(reader, dbe, 50, 300, n =>
        {
            if (n % 97 == 0)
            {
                Churn(20);
            }
        });

        Assert.That(rows.Select(r => r.Key), Is.Ordered, "key order");
        Assert.That(rows.All(r => r.Key == r.Tier), Is.True, "keys agree with values");
        Assert.That(rows.Select(r => r.Pk), Is.EquivalentTo(expected.Select(x => (long)x.RawValue)), "the query's answer at the snapshot");
    }
}
