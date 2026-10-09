using NUnit.Framework;
using System;
using System.Threading;
using Typhon.Engine.Internals;

namespace Typhon.Engine.Tests;

/// <summary>
/// #1136, rule PS-17: the page directory is a native chained hash whose chains run through the slot records, keyed by each slot's file page.
/// Tested on its own, over a slot table and a few made-up keys, so the races can be staged exactly.
/// </summary>
[TestFixture]
internal sealed class PageDirectoryTests : AllocatorTestBase
{
    private PagedMMF.PageSlotTable NewSlots(int count) => new(MemoryAllocator, AllocationResource, count);

    private PageDirectory NewDirectory(PagedMMF.PageSlotTable slots, long buckets = 0) => new(MemoryAllocator, AllocationResource, slots, buckets);

    /// <summary>What a slot's owner does: write the key, then publish.</summary>
    private static int Publish(PageDirectory directory, PagedMMF.PageSlotTable slots, int filePageIndex, int memPageIndex)
    {
        var slot = slots[memPageIndex];
        slot.FilePageIndex = filePageIndex;
        return directory.GetOrAdd(filePageIndex, memPageIndex);
    }

    /// <summary>What a reclaim does: unpublish, then clear the key.</summary>
    private static void Reclaim(PageDirectory directory, PagedMMF.PageSlotTable slots, int memPageIndex)
    {
        Assert.That(directory.TryRemove(slots[memPageIndex].FilePageIndex, memPageIndex), Is.True);
        var slot = slots[memPageIndex];
        slot.FilePageIndex = -1;
    }

    /// <summary>Small keys, the first <paramref name="count"/> that land in <paramref name="bucket"/>.</summary>
    private static int[] KeysInBucket(PageDirectory directory, long bucket, int count)
    {
        var keys = new int[count];
        for (int k = 1, n = 0; n < count; k++)
        {
            if (directory.BucketForTests(k) == bucket)
            {
                keys[n++] = k;
            }
        }

        return keys;
    }

    [Test]
    public void EveryPublishedPage_IsFound_AndRemovingOneSlot_UnpublishesOnlyIt()
    {
        var slots = NewSlots(64);
        var directory = NewDirectory(slots, buckets: 8);   // 8 chains of about 8: collisions on purpose
        for (var i = 0; i < 64; i++)
        {
            Assert.That(Publish(directory, slots, 1000 + i, i), Is.EqualTo(i));
        }

        for (var i = 0; i < 64; i++)
        {
            Assert.That(directory.TryGet(1000 + i, out var slot), Is.True);
            Assert.That(slot, Is.EqualTo(i));
        }

        Assert.That(directory.TryGet(999, out _), Is.False, "a page never published");

        Assert.That(directory.TryRemove(1010, 11), Is.False, "page 1010 is held by slot 10, not 11");
        Assert.That(directory.TryGet(1010, out var still), Is.True);
        Assert.That(still, Is.EqualTo(10));

        Reclaim(directory, slots, 10);
        Assert.That(directory.TryGet(1010, out _), Is.False);
        for (var i = 0; i < 64; i++)
        {
            Assert.That(directory.TryGet(1000 + i, out _), Is.EqualTo(i != 10), $"page {1000 + i}: only the removed one is gone");
        }
    }

    [Test]
    public void AZeroedDirectory_IsEmpty_AndAFreshSlotIsInNoChain()
    {
        var slots = NewSlots(16);
        var directory = NewDirectory(slots);

        Assert.That(directory.BucketCount, Is.EqualTo(64), "at least 64 buckets");
        Assert.That(directory.TryGet(0, out _), Is.False);
        Assert.That(directory.TryGet(-1, out _), Is.False, "a free slot's key, -1, is never published");
        unsafe
        {
            Assert.That(slots.Base[3].DirectoryNext, Is.Zero, "zeroed record: no link");
        }
    }

    [Test]
    [VerifiesRule("PS-17")]
    [CancelAfter(10_000)]
    public void ConcurrentMissesOnOnePage_ConvergeOnOneSlot()
    {
        const int threads = 8, rounds = 300;
        var slots = NewSlots(threads * rounds);
        var directory = NewDirectory(slots, buckets: 16);
        var results = new int[rounds, threads];
        using var barrier = new Barrier(threads);

        // Dedicated threads, not the pool: a barrier needs all of them running at once.
        var workers = new Thread[threads];
        for (var t = 0; t < threads; t++)
        {
            var self = t;
            workers[t] = new Thread(() =>
            {
                for (var r = 0; r < rounds; r++)
                {
                    var mySlot = r * threads + self;
                    var slot = slots[mySlot];
                    slot.FilePageIndex = 5000 + r;
                    barrier.SignalAndWait();
                    results[r, self] = directory.GetOrAdd(5000 + r, mySlot);
                }
            });
            workers[t].Start();
        }

        foreach (var w in workers)
        {
            w.Join();
        }

        for (var r = 0; r < rounds; r++)
        {
            var winner = results[r, 0];
            var winners = 0;
            for (var t = 0; t < threads; t++)
            {
                Assert.That(results[r, t], Is.EqualTo(winner), $"round {r}: every miss converges on one slot");
                winners += results[r, t] == r * threads + t ? 1 : 0;
            }

            Assert.That(winners, Is.EqualTo(1), $"round {r}: exactly one thread published its own slot");
            Assert.That(directory.ChainLengthForTests(5000 + r), Is.GreaterThanOrEqualTo(1));
        }
    }

    /// <summary>
    /// The review's interleaving: a reader reads a node's key, the node is reclaimed and published for a page of ANOTHER bucket (its link now
    /// ends that bucket's chain), then the reader follows the link. The lock-free walk ends early; the locked confirmation still finds the page.
    /// </summary>
    [Test]
    [VerifiesRule("PS-17")]
    public void AReaderPausedOnANodeThatMovesToAnotherChain_StillFindsThePage()
    {
        var slots = NewSlots(4);
        var directory = NewDirectory(slots, buckets: 2);
        var inA = KeysInBucket(directory, 0, 2);
        var inB = KeysInBucket(directory, 1, 1);
        int k = inA[0], x = inA[1], y = inB[0];

        Publish(directory, slots, x, 1);
        Publish(directory, slots, k, 0);   // chain A: slot 0 (k) → slot 1 (x)

        var fired = 0;
        directory.WalkProbe = slot =>
        {
            if (slot == 0 && fired++ == 0)
            {
                Reclaim(directory, slots, 0);
                Publish(directory, slots, y, 0);   // slot 0 now ends chain B: its link is 0
            }
        };

        Assert.That(directory.TryGet(x, out var found), Is.True, "page x was published throughout");
        Assert.That(found, Is.EqualTo(1));
        Assert.That(fired, Is.EqualTo(1), "precondition: the reader was paused on the moving node");
    }

    /// <summary>
    /// Two nodes moved to the head of their chain each time the reader reaches one: the lock-free walk ping-pongs between them for ever and never
    /// reaches the page behind them. The step bound ends it, and the locked walk finds the page.
    /// </summary>
    [Test]
    [VerifiesRule("PS-17")]
    [CancelAfter(10_000)]
    public void AReaderChasingReusedNodes_Ends_AndFindsThePage()
    {
        var slots = NewSlots(3);
        var directory = NewDirectory(slots, buckets: 1);
        Publish(directory, slots, 30, 2);   // x, behind
        Publish(directory, slots, 20, 1);
        Publish(directory, slots, 10, 0);   // chain: 0 → 1 → 2

        var probes = 0;
        directory.WalkProbe = slot =>
        {
            probes++;
            if (slot is 0 or 1)
            {
                var key = slots[slot].FilePageIndex;
                Assert.That(directory.TryRemove(key, slot), Is.True);
                directory.GetOrAdd(key, slot);   // back at the head, linked to the other one
            }
        };

        Assert.That(directory.TryGet(30, out var found), Is.True);
        Assert.That(found, Is.EqualTo(2));
        Assert.That(probes, Is.EqualTo(PageDirectory.MaxLockFreeSteps), "the lock-free walk gave up at its bound");
    }

    /// <summary>
    /// Churn: one page stays published while other slots are published and reclaimed, in its bucket and the other one, as fast as two threads can.
    /// Readers looking the page up concurrently must find it every time, in its own slot.
    /// </summary>
    [Test]
    [VerifiesRule("PS-17")]
    [CancelAfter(20_000)]
    public void APublishedPage_IsAlwaysFound_WhileOtherSlotsChurnThroughItsChain()
    {
        const int churners = 2, readers = 2, cycles = 50_000, slotsPerChurner = 8;
        var slots = NewSlots(1 + churners * slotsPerChurner);
        var directory = NewDirectory(slots, buckets: 2);
        const int sentinel = 424_242;
        Publish(directory, slots, sentinel, 0);

        var stop = 0;
        var misses = 0;
        var wrongSlot = 0;
        var lookups = 0L;
        var threads = new Thread[churners + readers];
        for (var c = 0; c < churners; c++)
        {
            var first = 1 + c * slotsPerChurner;
            var seed = c;
            threads[c] = new Thread(() =>
            {
                var key = 1_000_000 * (seed + 1);
                for (var i = 0; i < cycles; i++)
                {
                    var slot = first + i % slotsPerChurner;
                    var pi = slots[slot];
                    if (pi.FilePageIndex >= 0)
                    {
                        directory.TryRemove(pi.FilePageIndex, slot);
                        pi.FilePageIndex = -1;
                    }

                    pi.FilePageIndex = key++;   // a fresh page each time, in either bucket
                    directory.GetOrAdd(pi.FilePageIndex, slot);
                }
            });
        }

        for (var r = 0; r < readers; r++)
        {
            threads[churners + r] = new Thread(() =>
            {
                long n = 0;
                while (Volatile.Read(ref stop) == 0)
                {
                    if (!directory.TryGet(sentinel, out var slot))
                    {
                        Interlocked.Increment(ref misses);
                    }
                    else if (slot != 0)
                    {
                        Interlocked.Increment(ref wrongSlot);
                    }

                    n++;
                }

                Interlocked.Add(ref lookups, n);
            });
        }

        foreach (var t in threads)
        {
            t.Start();
        }

        for (var c = 0; c < churners; c++)
        {
            threads[c].Join();
        }

        Volatile.Write(ref stop, 1);
        for (var r = 0; r < readers; r++)
        {
            threads[churners + r].Join();
        }

        Assert.That(lookups, Is.GreaterThan(0), "precondition: the readers ran");
        Assert.That(misses, Is.Zero, "the published page was reported absent");
        Assert.That(wrongSlot, Is.Zero, "the published page was reported in another slot");
    }

    [Test]
    public void ItsOperations_AllocateNothing()
    {
        var slots = NewSlots(256);
        var directory = NewDirectory(slots);
        for (var round = 0; round < 2; round++)   // the first round warms up the JIT
        {
            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 0; i < 256; i++)
            {
                Publish(directory, slots, 7000 + i, i);
            }

            var found = 0;
            for (var i = 0; i < 256; i++)
            {
                found += directory.TryGet(7000 + i, out _) ? 1 : 0;
                found += directory.TryGet(9000 + i, out _) ? 1 : 0;
                found += directory.TryRemove(7000 + i, i) ? 1 : 0;
                var slot = slots[i];
                slot.FilePageIndex = -1;
            }

            var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.That(found, Is.EqualTo(512), "each page found, then removed");
            if (round == 1)
            {
                Assert.That(allocated, Is.Zero, "no node object, no resize: nothing for the GC");
            }
        }
    }
}
