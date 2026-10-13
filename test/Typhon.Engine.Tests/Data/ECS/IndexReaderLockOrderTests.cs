using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests;

[Component("Typhon.Test.LockOrder.Tag", 1, StorageMode = StorageMode.SingleVersion)]
public struct LoTag
{
    [Index(AllowMultiple = true)] public long Group;
    public long N;
}

[Archetype]
class LoTagArch : Archetype<LoTagArch>
{
    public static readonly Comp<LoTag> Tag = Register<LoTag>();
}

/// <summary>
/// Rule <b>IXS-10</b> (<c>rules/indexing.md</c>): an index reader never waits on a revision chain's lock while it holds a value buffer's.
/// </summary>
/// <remarks>
/// <para>A transaction's <c>EnumerateIndex</c> over a multi-value key kept the key's value buffer open, under its shared lock, while it walked each entity's
/// chain — a walk that waits when a publish holds the chain exclusively. A publish holding that chain moves the entity's entry off the key, which takes the
/// same buffer exclusively: a cycle, each waiting out the other's timeout. Every concurrent index reader in the suite was partitioned so no writer moved an
/// entity off the key it read, which is why none saw it.</para>
/// <para>Here readers enumerate the one key every writer moves entities on and off, without pause, and nothing may time out.</para>
/// </remarks>
[TestFixture]
class IndexReaderLockOrderTests : TestBase<IndexReaderLockOrderTests>
{
    private const int Items = 2_000, Writers = 3, Readers = 3;
    private static readonly TimeSpan Duration = TimeSpan.FromMilliseconds(400);

    [Test]
    [VerifiesRule("IXS-10")]
    [CancelAfter(30_000)]
    public void ReadersOfASharedMultiValueKey_AndMovesOffIt_NeverWaitEachOtherOut([Values(false, true)] bool handler)
    {
        Run(handler);
    }

    private void Run(bool handler)
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
                ids[i] = tx.Spawn<OcItemArch>(OcItemArch.Item.Set(new OcItem { ItemNo = i, OwnerNo = 0, Seq = i + 1 }));
            }

            tx.Commit();
        }

        var indexRef = dbe.GetIndexRef<OcItem, long>(x => x.OwnerNo);
        var failures = new ConcurrentQueue<string>();
        var stop = 0;
        long moves = 0, reads = 0;
        var threads = new List<Thread>();
        for (var w = 0; w < Writers; w++)
        {
            var writer = w;
            threads.Add(new Thread(() =>
            {
                try
                {
                    var rng = new Random(17 + writer);
                    while (Volatile.Read(ref stop) == 0)
                    {
                        // Each writer owns the items i ≡ writer (mod Writers), and moves one between the shared key 0 and its own key.
                        var i = writer + Writers * rng.Next(Items / Writers);
                        var to = owner[i] == 0 ? 1 + writer : 0;
                        using var tx = dbe.CreateQuickTransaction();
                        var mut = tx.OpenMut(ids[i]);
                        var item = mut.Read(OcItemArch.Item);
                        item.OwnerNo = to;
                        mut.Set(OcItemArch.Item, item);
                        var committed = handler ? tx.Commit((ref ConcurrencyConflictSolver _) => { }) : tx.Commit();
                        if (committed)
                        {
                            owner[i] = to;
                            Interlocked.Increment(ref moves);
                        }
                    }
                }
                catch (Exception ex)
                {
                    failures.Enqueue($"writer {writer}: {ex.GetType().Name}: {ex.Message}");
                }
            }) { IsBackground = true });
        }

        for (var r = 0; r < Readers; r++)
        {
            var reader = r;
            threads.Add(new Thread(() =>
            {
                try
                {
                    while (Volatile.Read(ref stop) == 0)
                    {
                        using var tx = dbe.CreateReadOnlyTransaction();
                        using var e = tx.EnumerateIndex<OcItem, long>(indexRef, 0, 0);
                        while (e.MoveNext())
                        {
                        }

                        Interlocked.Increment(ref reads);
                    }
                }
                catch (Exception ex)
                {
                    failures.Enqueue($"reader {reader}: {ex.GetType().Name}: {ex.Message}");
                }
            }) { IsBackground = true });
        }

        var sw = Stopwatch.StartNew();
        foreach (var t in threads)
        {
            t.Start();
        }

        while (sw.Elapsed < Duration && failures.IsEmpty)
        {
            Thread.Sleep(20);
        }

        Volatile.Write(ref stop, 1);
        foreach (var t in threads)
        {
            Assert.That(t.Join(TimeSpan.FromSeconds(15)), Is.True, "a thread did not finish: a reader and a publish wait on each other");
        }

        TestContext.Out.WriteLine($"{moves:N0} moves, {reads:N0} full reads of key 0 in {sw.Elapsed.TotalMilliseconds:F0} ms");
        Assert.That(failures, Is.Empty, "a reader of the shared key and a move off it timed out on each other: " + string.Join(" | ", failures));
        Assert.That(Interlocked.Read(ref moves), Is.GreaterThan(0), "premise: the writers moved entities");
        Assert.That(Interlocked.Read(ref reads), Is.GreaterThan(0), "premise: the readers read the shared key");

        // Once the writers stop, the index holds each item under exactly its owner.
        for (var key = 0L; key <= Writers; key++)
        {
            var expected = new HashSet<long>();
            for (var i = 0; i < Items; i++)
            {
                if (owner[i] == key)
                {
                    expected.Add((long)ids[i].RawValue);
                }
            }

            var found = new HashSet<long>();
            using (var tx = dbe.CreateReadOnlyTransaction())
            using (var e = tx.EnumerateIndex<OcItem, long>(indexRef, key, key))
            {
                while (e.MoveNext())
                {
                    found.Add(e.CurrentEntityPK);
                }
            }

            Assert.That(found.SetEquals(expected), Is.True, $"key {key}: the index holds {found.Count} items, the owners say {expected.Count}");
        }
    }

    /// <summary>
    /// The reader copies a key's values and releases the buffer before it resolves them, so a SingleVersion destroy — which releases its slot at once — and
    /// a spawn into that slot can land in between. The reader must not yield the new occupant, another entity under another key, as one of this key's.
    /// </summary>
    [Test]
    [VerifiesRule("IXS-10")]
    public void ASlotReusedAfterTheKeyWasCopied_IsNotYieldedUnderTheKey()
    {
        var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<LoTag>();
        dbe.InitializeArchetypes();
        EntityId a, b;
        using (var tx = dbe.CreateQuickTransaction())
        {
            a = tx.Spawn<LoTagArch>(LoTagArch.Tag.Set(new LoTag { Group = 5, N = 1 }));
            b = tx.Spawn<LoTagArch>(LoTagArch.Tag.Set(new LoTag { Group = 5, N = 2 }));
            tx.Commit();
        }

        var indexRef = dbe.GetIndexRef<LoTag, long>(x => x.Group);
        var yielded = new List<long>();
        EntityId c = default;
        var fired = false;
        Transaction.IndexEntityEnumerator<LoTag, long>.KeyCopiedProbe = () =>
        {
            if (fired)
            {
                return;
            }

            fired = true;
            using (var tx = dbe.CreateQuickTransaction())
            {
                tx.Destroy(b);
                tx.Commit();
            }

            using (var tx = dbe.CreateQuickTransaction())
            {
                c = tx.Spawn<LoTagArch>(LoTagArch.Tag.Set(new LoTag { Group = 9, N = 3 }));
                tx.Commit();
            }
        };

        try
        {
            using var reader = dbe.CreateReadOnlyTransaction();
            using var e = reader.EnumerateIndex<LoTag, long>(indexRef, 5, 5);
            while (e.MoveNext())
            {
                yielded.Add(e.CurrentEntityPK);
            }
        }
        finally
        {
            Transaction.IndexEntityEnumerator<LoTag, long>.KeyCopiedProbe = null;
        }

        Assert.That(fired, Is.True, "premise: the destroy and the spawn ran after the key was copied");
        Assert.That(yielded, Does.Not.Contain((long)c.RawValue), "the slot's new occupant, under key 9, was yielded under key 5");
        Assert.That(yielded, Does.Contain((long)a.RawValue), "an entity still under the key was not yielded");
    }
}
