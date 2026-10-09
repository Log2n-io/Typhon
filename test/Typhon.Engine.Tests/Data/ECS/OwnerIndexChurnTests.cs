using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using Typhon.Engine.Internals;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests;

[Component("Typhon.Test.OwnerChurn.Item", 1)]
public struct OcItem
{
    public long ItemNo;
    [Index(AllowMultiple = true)] public long OwnerNo;
    [Index] public long Seq;
}

[Component("Typhon.Test.OwnerChurn.Lore", 1, StorageMode = StorageMode.SingleVersion)]
public struct OcLore
{
    public long A, B, C, D;
}

[Archetype]
class OcItemArch : Archetype<OcItemArch>
{
    public static readonly Comp<OcItem> Item = Register<OcItem>();
    public static readonly Comp<OcLore> Lore = Register<OcLore>();
}

/// <summary>
/// A non-unique index on an owner field, churned the way a market churns it: items bought from key 0, traded between owners, sold back, consumed (destroyed
/// and crafted anew under key 0) — and one change in twenty rolled back after all its writes. After every operation the two keys it touched find exactly
/// the items a model says they hold.
/// </summary>
/// <remarks>
/// Single-threaded, a consume used to fail here within the first few hundred operations — a destroy freed storage a respawn then shared (REAP-02). On
/// several threads the index itself loses moves out of the shared key, and the scan path emits slots a commit has not finished filling (#1232).
/// </remarks>
[TestFixture]
class OwnerIndexChurnTests : TestBase<OwnerIndexChurnTests>
{
    private const int Items = 400, Owners = 20;

    [Test]
    [VerifiesRule("REAP-02")]
    public void AnOwnerIndex_ChurnedLikeAMarket_FindsExactlyEachOwnersItems([Values(false, true)] bool rollbacks, [Values(false, true)] bool consumes,
        [Values(false, true)] bool bulkLoaded)
    {
        var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<OcItem>();
        dbe.RegisterComponentFromAccessor<OcLore>();
        dbe.InitializeArchetypes();

        var ids = new EntityId[Items];
        var owner = new long[Items];
        if (bulkLoaded)
        {
            using var session = dbe.BeginBulkLoad();
            for (var i = 0; i < Items; i++)
            {
                ids[i] = session.Spawn<OcItemArch>(OcItemArch.Item.Set(new OcItem { ItemNo = i, OwnerNo = 0, Seq = -(i + 1L) }));
                session.OpenMut(ids[i]).Enable(OcItemArch.Lore, new OcLore { A = i });
            }

            session.CompleteBulkLoad();
        }
        else
        {
            using var tx = dbe.CreateQuickTransaction();
            for (var i = 0; i < Items; i++)
            {
                ids[i] = tx.Spawn<OcItemArch>(OcItemArch.Item.Set(new OcItem { ItemNo = i, OwnerNo = 0, Seq = -(i + 1L) }));
                tx.OpenMut(ids[i]).Enable(OcItemArch.Lore, new OcLore { A = i });
            }

            tx.Commit();
        }

        var rng = new Random(1);
        long seq = 0;
        for (var op = 0; op < 800; op++)
        {
            var i = rng.Next(Items);
            var from = owner[i];
            var roll = rng.Next(10);
            var consume = consumes && from != 0 && roll < 2;
            long to;
            if (from == 0)
            {
                to = 1 + rng.Next(Owners);
            }
            else if (consume || roll < 5)
            {
                to = 0;
            }
            else
            {
                do
                {
                    to = 1 + rng.Next(Owners);
                } while (to == from);
            }

            var rollBack = rollbacks && rng.Next(20) == 0;
            var newId = ids[i];
            using (var tx = dbe.CreateQuickTransaction())
            {
                var item = tx.Open(ids[i]).Read(OcItemArch.Item);
                Assert.That(item.OwnerNo, Is.EqualTo(from), $"op {op}: item {i} reads owner {item.OwnerNo}, the model has {from}");
                item.OwnerNo = to;
                item.Seq = ++seq;
                if (consume)
                {
                    tx.Destroy(ids[i]);
                    newId = tx.Spawn<OcItemArch>(OcItemArch.Item.Set(item));
                    tx.OpenMut(newId).Enable(OcItemArch.Lore, new OcLore { A = i });
                }
                else
                {
                    tx.OpenMut(ids[i]).Set(OcItemArch.Item, item);
                }

                if (rollBack)
                {
                    tx.Rollback();
                }
                else
                {
                    Assert.That(tx.Commit(), Is.True);
                    owner[i] = to;
                    ids[i] = newId;
                }
            }

            var what = $"op {op}: item {i} {(consume ? "consumed" : "moved")} {from} -> {to}{(rollBack ? ", rolled back" : "")}";
            AssertKeyHoldsExactly(dbe, ids, owner, from, what);
            AssertKeyHoldsExactly(dbe, ids, owner, to, what);
        }
    }

    /// <summary>
    /// A key's value buffer whose last chunk was emptied, then walked by a reader, then appended to: the append must be found. A reader that walks past an
    /// empty chunk unlinks it and parks it for reuse — if that chunk is the buffer's append cursor, the next append lands in a chunk no walk reaches.
    /// </summary>
    [Test]
    [VerifiesRule("IXW-08")]
    public void AKeyWhoseLastChunkWasEmptied_ThenWalked_FindsWhatIsAppendedNext([Values(33, 34, 35, 96)] int movedOut)
    {
        var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<OcItem>();
        dbe.RegisterComponentFromAccessor<OcLore>();
        dbe.InitializeArchetypes();
        var ids = new EntityId[Items];
        var owner = new long[Items];
        using (var tx = dbe.CreateQuickTransaction())
        {
            for (var i = 0; i < Items; i++)
            {
                ids[i] = tx.Spawn<OcItemArch>(OcItemArch.Item.Set(new OcItem { ItemNo = i, OwnerNo = 0, Seq = -(i + 1L) }));
            }

            tx.Commit();
        }

        // The last items appended to key 0 leave it, one by one. A value buffer's root holds 56 ids and each further chunk 62, so 400 leave 34 in the last
        // chunk — the append cursor: 33 moved out leave it one, 34 empty it, 35 and 96 empty it and reach into the chunk before.
        for (var i = Items - movedOut; i < Items; i++)
        {
            Move(dbe, ids, owner, i, 1);
        }

        var indexRef = dbe.GetIndexRef<OcItem, long>(x => x.OwnerNo);
        using (var tx = dbe.CreateReadOnlyTransaction())
        using (var e = tx.EnumerateIndex<OcItem, long>(indexRef, 0, 0))
        {
            while (e.MoveNext())
            {
            }
        }

        // One comes back: appended where key 0's buffer appends. Read through the index itself — a query may scan the clusters instead.
        Move(dbe, ids, owner, Items - 1, 0);
        Assert.That(IndexProblem(dbe, ids, owner, 0), Is.Null, $"{movedOut} moved out, one back");
        Assert.That(IndexProblem(dbe, ids, owner, 1), Is.Null, $"{movedOut} moved out, one back");
    }

    /// <summary>
    /// Each move takes an item out of the shared key 0 into a key of its own, so every move inserts a key — until the leaf holding key 0 is full and
    /// the move must split it. Every key must then find its item, and key 0 the rest. The move used to decide that bail after taking the element out of
    /// key 0's buffer, append it back — to the buffer's last chunk — and retry with the element id of the chunk it had left: the retry found nothing, and
    /// the 19th key never got its item (#1232).
    /// </summary>
    [Test]
    [VerifiesRule("IXW-06")]
    public void MovesThatEachCreateAKey_FillingTheLeaf_LoseNoEntry([Values(0, 1, 7, 150, 300)] int firstItem)
    {
        var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<OcItem>();
        dbe.RegisterComponentFromAccessor<OcLore>();
        dbe.InitializeArchetypes();
        var ids = new EntityId[Items];
        var owner = new long[Items];
        using (var tx = dbe.CreateQuickTransaction())
        {
            for (var i = 0; i < Items; i++)
            {
                ids[i] = tx.Spawn<OcItemArch>(OcItemArch.Item.Set(new OcItem { ItemNo = i, OwnerNo = 0, Seq = -(i + 1L) }));
            }

            tx.Commit();
        }

        for (var k = 0; k < 60; k++)
        {
            Move(dbe, ids, owner, firstItem + k, 1 + k);
            var problem = IndexProblem(dbe, ids, owner, 1 + k);
            Assert.That(problem, Is.Null, $"move {k} (item {firstItem + k} to key {1 + k})");
        }

        Assert.That(IndexProblem(dbe, ids, owner, 0), Is.Null, "key 0 after the moves");
    }

    /// <summary>What <paramref name="key"/> holds through the index itself, against the model, or null when they agree.</summary>
    private static string IndexProblem(DatabaseEngine dbe, EntityId[] ids, long[] owner, long key)
    {
        var expected = new HashSet<long>();
        for (var j = 0; j < ids.Length; j++)
        {
            if (owner[j] == key)
            {
                expected.Add((long)ids[j].RawValue);
            }
        }

        var found = new HashSet<long>();
        using var tx = dbe.CreateReadOnlyTransaction();
        using (var e = tx.EnumerateIndex<OcItem, long>(dbe.GetIndexRef<OcItem, long>(x => x.OwnerNo), key, key))
        {
            while (e.MoveNext())
            {
                found.Add(e.CurrentEntityPK);
            }
        }

        return found.SetEquals(expected) ? null : $"key {key}: the index holds {found.Count}, expected {expected.Count}";
    }

    private static void Move(DatabaseEngine dbe, EntityId[] ids, long[] owner, int i, long to)
    {
        using var tx = dbe.CreateQuickTransaction();
        var item = tx.Open(ids[i]).Read(OcItemArch.Item);
        item.OwnerNo = to;
        tx.OpenMut(ids[i]).Set(OcItemArch.Item, item);
        Assert.That(tx.Commit(), Is.True);
        owner[i] = to;
    }

    /// <summary>
    /// The same churn on four threads, read through the index itself. Items are interleaved across the workers, so every cluster and the market's key
    /// are shared; each worker has owners of its own and checks them after every change it commits — nobody else moves those keys, so the index must
    /// agree with its model exactly. Moves out of the shared key were lost within a dozen operations (#1232, fixed). Quarantined: #1235 — a read of a
    /// populated key still answers empty now and then while other threads split leaves; the same read a moment later finds every entry.
    /// </summary>
    [Test]
    [Category("Quarantine")]
    public void AnOwnerIndex_ChurnedOnSeveralThreads_HoldsExactlyEachOwnersItems([Values(false, true)] bool rollbacks, [Values(false, true)] bool consumes)
        => ChurnOnThreads(rollbacks, consumes, ClusterScanPath.Planner, throughIndex: true);

    /// <summary>
    /// The same churn read through a query, on each scan path. Quarantined: #1234 — a query concurrent with spawns emits half-written cluster slots
    /// (entities that do not match, <c>Entity(Null)</c>) while the index is right.
    /// </summary>
    [Test]
    [Category("Quarantine")]
    public void AQueryOnAChurnedOwnerIndex_OnSeveralThreads_FindsExactlyEachOwnersItems([Values(false, true)] bool rollbacks,
        [Values(false, true)] bool consumes, [Values(ClusterScanPath.Selective, ClusterScanPath.FullScan)] ClusterScanPath path)
        => ChurnOnThreads(rollbacks, consumes, path, throughIndex: false);

    private void ChurnOnThreads(bool rollbacks, bool consumes, ClusterScanPath path, bool throughIndex)
    {
        const int Threads = 4, OwnersPerThread = 5, Ops = 3_000;
        var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<OcItem>();
        dbe.RegisterComponentFromAccessor<OcLore>();
        dbe.InitializeArchetypes();

        var ids = new EntityId[Items];
        var owner = new long[Items];
        using (var tx = dbe.CreateQuickTransaction())
        {
            for (var i = 0; i < Items; i++)
            {
                ids[i] = tx.Spawn<OcItemArch>(OcItemArch.Item.Set(new OcItem { ItemNo = i, OwnerNo = 0, Seq = -(i + 1L) }));
                tx.OpenMut(ids[i]).Enable(OcItemArch.Lore, new OcLore { A = i });
            }

            tx.Commit();
        }

        long seq = 0;
        var failures = new System.Collections.Concurrent.ConcurrentQueue<string>();
        var workers = new System.Threading.Thread[Threads];
        for (var w = 0; w < Threads; w++)
        {
            var worker = w;
            workers[w] = new System.Threading.Thread(() =>
            {
                try
                {
                    QueryPathProbe.Forced = path;
                    var rng = new Random(100 + worker);
                    var firstOwner = 1 + worker * OwnersPerThread;
                    for (var op = 0; op < Ops && failures.IsEmpty; op++)
                    {
                        var i = worker + Threads * rng.Next(Items / Threads);
                        var from = owner[i];
                        var roll = rng.Next(10);
                        var consume = consumes && from != 0 && roll < 2;
                        long to;
                        if (from == 0)
                        {
                            to = firstOwner + rng.Next(OwnersPerThread);
                        }
                        else if (consume || roll < 5)
                        {
                            to = 0;
                        }
                        else
                        {
                            do
                            {
                                to = firstOwner + rng.Next(OwnersPerThread);
                            } while (to == from);
                        }

                        var rollBack = rollbacks && rng.Next(20) == 0;
                        var newId = ids[i];
                        using (var tx = dbe.CreateQuickTransaction())
                        {
                            var item = tx.Open(ids[i]).Read(OcItemArch.Item);
                            if (item.OwnerNo != from)
                            {
                                failures.Enqueue($"worker {worker} op {op}: item {i} reads owner {item.OwnerNo}, the model has {from}");
                                return;
                            }

                            item.OwnerNo = to;
                            item.Seq = System.Threading.Interlocked.Increment(ref seq);
                            if (consume)
                            {
                                tx.Destroy(ids[i]);
                                newId = tx.Spawn<OcItemArch>(OcItemArch.Item.Set(item));
                                tx.OpenMut(newId).Enable(OcItemArch.Lore, new OcLore { A = i });
                            }
                            else
                            {
                                tx.OpenMut(ids[i]).Set(OcItemArch.Item, item);
                            }

                            if (rollBack)
                            {
                                tx.Rollback();
                            }
                            else
                            {
                                if (!tx.Commit())
                                {
                                    failures.Enqueue($"worker {worker} op {op}: item {i}'s commit returned false");
                                    return;
                                }

                                owner[i] = to;
                                ids[i] = newId;
                            }
                        }

                        foreach (var key in new[] { from, to })
                        {
                            if (key != 0)
                            {
                                var problem = throughIndex ? OwnKeyIndexProblem(dbe, ids, owner, key, worker, Threads)
                                    : KeyProblem(dbe, ids, owner, key, worker, Threads);
                                if (problem != null)
                                {
                                    failures.Enqueue($"worker {worker} op {op}: item {i} {(consume ? "consumed" : "moved")} {from} -> {to}"
                                                     + $"{(rollBack ? ", rolled back" : "")}: {problem}");
                                    return;
                                }
                            }
                        }
                    }
                }
                catch (Exception e)
                {
                    failures.Enqueue($"worker {worker}: {e}");
                }
            });
        }

        foreach (var t in workers)
        {
            t.Start();
        }

        foreach (var t in workers)
        {
            t.Join();
        }

        var table = dbe.GetComponentTable<OcItem>();
        var doubleFrees = $"double frees: content {table.ComponentSegment.DoubleFreeCount}, revisions {table.CompRevTableSegment.DoubleFreeCount}";
        Assert.That(failures, Is.Empty, doubleFrees + Environment.NewLine + string.Join(Environment.NewLine, failures));
        Assert.That(table.ComponentSegment.DoubleFreeCount + table.CompRevTableSegment.DoubleFreeCount, Is.Zero, doubleFrees);
        if (throughIndex)
        {
            Assert.That(IndexProblem(dbe, ids, owner, 0), Is.Null, "key 0 after the storm");
        }
        else
        {
            AssertKeyHoldsExactly(dbe, ids, owner, 0, "after the storm");
        }
    }

    /// <summary>What <paramref name="key"/>, one of <paramref name="worker"/>'s owners, holds through the index itself against the model, or null.</summary>
    private static string OwnKeyIndexProblem(DatabaseEngine dbe, EntityId[] ids, long[] owner, long key, int worker, int threads)
    {
        var expected = new HashSet<long>();
        for (var j = worker; j < ids.Length; j += threads)
        {
            if (owner[j] == key)
            {
                expected.Add((long)ids[j].RawValue);
            }
        }

        var found = new HashSet<long>();
        using var tx = dbe.CreateReadOnlyTransaction();
        using (var e = tx.EnumerateIndex<OcItem, long>(dbe.GetIndexRef<OcItem, long>(x => x.OwnerNo), key, key))
        {
            while (e.MoveNext())
            {
                found.Add(e.CurrentEntityPK);
            }
        }

        if (found.SetEquals(expected))
        {
            return null;
        }

        // Read again, in fresh transactions: a reader-side race clears up, a lost or stale entry does not.
        var again = new List<int>();
        for (var retry = 0; retry < 3; retry++)
        {
            using var rtx = dbe.CreateReadOnlyTransaction();
            var n = 0;
            using (var e = rtx.EnumerateIndex<OcItem, long>(dbe.GetIndexRef<OcItem, long>(x => x.OwnerNo), key, key))
            {
                while (e.MoveNext())
                {
                    n++;
                }
            }

            again.Add(n);
        }

        return $"key {key}: the index holds {found.Count}, expected {expected.Count}; re-read {string.Join("/", again)}";
    }

    /// <summary>What is wrong with <paramref name="key"/>, one of <paramref name="worker"/>'s owners, or null. Reads only the worker's own items.</summary>
    private static string KeyProblem(DatabaseEngine dbe, EntityId[] ids, long[] owner, long key, int worker, int threads)
    {
        var expected = new HashSet<EntityId>();
        for (var j = worker; j < ids.Length; j += threads)
        {
            if (owner[j] == key)
            {
                expected.Add(ids[j]);
            }
        }

        using var tx = dbe.CreateReadOnlyTransaction();
        var found = tx.Query<OcItemArch>().WhereField<OcItem>(x => x.OwnerNo == key).Execute();
        if (found.SetEquals(expected))
        {
            return null;
        }

        var viaIndex = 0;
        var indexRef = dbe.GetIndexRef<OcItem, long>(x => x.OwnerNo);
        using (var e = tx.EnumerateIndex<OcItem, long>(indexRef, key, key))
        {
            while (e.MoveNext())
            {
                viaIndex++;
            }
        }

        var detail = new System.Text.StringBuilder();
        foreach (var id in found)
        {
            if (!expected.Contains(id))
            {
                var alive = tx.IsAlive(id);
                var opened = tx.TryOpen(id, out var entity);
                detail.Append($" extra {id} alive={alive} opened={opened}");
                if (opened)
                {
                    var item = entity.Read(OcItemArch.Item);
                    detail.Append($" item={item.ItemNo} owner={item.OwnerNo} model-owner={owner[item.ItemNo]} model-id={ids[item.ItemNo]};");
                }
            }
        }

        foreach (var id in expected)
        {
            if (!found.Contains(id))
            {
                detail.Append($" missing {id} alive={tx.IsAlive(id)} reads-owner={tx.Open(id).Read(OcItemArch.Item).OwnerNo}");
                using (var e0 = tx.EnumerateIndex<OcItem, long>(indexRef, 0, 0))
                {
                    var under0 = false;
                    while (e0.MoveNext())
                    {
                        under0 |= e0.CurrentEntityPK == (long)id.RawValue;
                    }

                    detail.Append($" under-key-0={under0}");
                }

                // Again, in fresh transactions: transient (a reader-side race) or lost (the index never got it)?
                for (var retry = 0; retry < 3; retry++)
                {
                    using var again = dbe.CreateReadOnlyTransaction();
                    var seen = false;
                    using (var e = again.EnumerateIndex<OcItem, long>(indexRef, key, key))
                    {
                        while (e.MoveNext())
                        {
                            seen |= e.CurrentEntityPK == (long)id.RawValue;
                        }
                    }

                    detail.Append($" retry{retry}={(seen ? "found" : "absent")}");
                }

                detail.Append(';');
            }
        }

        return $"key {key} finds {found.Count} through the query, {viaIndex} through the index, expected {expected.Count} (tx {tx.TSN}):{detail}";
    }

    private static void AssertKeyHoldsExactly(DatabaseEngine dbe, EntityId[] ids, long[] owner, long key, string what)
    {
        var expected = new HashSet<EntityId>();
        for (var j = 0; j < ids.Length; j++)
        {
            if (owner[j] == key)
            {
                expected.Add(ids[j]);
            }
        }

        using var tx = dbe.CreateReadOnlyTransaction();
        var found = tx.Query<OcItemArch>().WhereField<OcItem>(x => x.OwnerNo == key).Execute();
        if (!found.SetEquals(expected))
        {
            var missing = new List<string>();
            foreach (var id in expected)
            {
                if (!found.Contains(id))
                {
                    missing.Add(id.ToString());
                }
            }

            var extra = new List<string>();
            foreach (var id in found)
            {
                if (!expected.Contains(id))
                {
                    extra.Add(id.ToString());
                }
            }

            Assert.Fail($"{what}: key {key} finds {found.Count}, expected {expected.Count}; missing [{string.Join(", ", missing)}], "
                        + $"extra [{string.Join(", ", extra)}]");
        }
    }
}
