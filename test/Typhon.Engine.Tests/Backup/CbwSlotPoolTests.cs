using System;
using System.Buffers.Binary;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using Typhon.Engine.Internals;

namespace Typhon.Engine.Tests;

/// <summary>
/// <see cref="CbwSlotPool"/>: an abort releases a writer waiting on an empty pool at once (BKP-12), and under concurrent renters, publishers and one
/// drainer every published pre-image reaches the drainer exactly once, with its bytes, and every slot comes back.
/// </summary>
[TestFixture]
public class CbwSlotPoolTests : AllocatorTestBase
{
    /// <summary>A writer waits on a pool whose slots are all held; the capture aborts: the writer returns at once, empty-handed, and leaks nothing.</summary>
    [Test]
    [CancelAfter(10_000)]
    [VerifiesRule("BKP-12")]
    public void AnAbort_ReleasesAWriterWaitingOnAnEmptyPool()
    {
        using var pool = new CbwSlotPool(MemoryAllocator, AllocationResource, 1);
        using var abort = new CancellationTokenSource();
        var held = pool.TryRent(0, CancellationToken.None);
        Assert.That(held, Is.EqualTo(0), "premise: the only slot is held");

        var waiter = Task.Factory.StartNew(() => pool.TryRent(30_000, abort.Token), TaskCreationOptions.LongRunning);
        Assert.That(waiter.Wait(50), Is.False, "premise: the writer waits on the empty pool");
        abort.Cancel();
        Assert.That(waiter.Wait(2_000), Is.True, "BKP-12: an abort left a writer waiting on the pool");
        Assert.That(waiter.Result, Is.EqualTo(-1));

        pool.Release(held);
        Assert.That(pool.TryRent(0, CancellationToken.None), Is.EqualTo(0), "the cancelled wait took no slot with it");
    }

    /// <summary>Four writers rent, fill and publish thousands of pre-images through a few slots while one thread drains them.</summary>
    [TestCase(3)]
    [TestCase(70)]   // two ready words
    [CancelAfter(20_000)]
    public void ConcurrentRentPublishDrain_DeliversEveryPreImageOnce(int slots)
    {
        const int Writers = 4;
        const int PerWriter = 5_000;
        using var pool = new CbwSlotPool(MemoryAllocator, AllocationResource, slots);
        var seen = new int[Writers * PerWriter];
        var badBytes = 0;
        var rentTimeouts = 0;

        var producers = new Task[Writers];
        for (var w = 0; w < Writers; w++)
        {
            var writer = w;
            producers[w] = Task.Factory.StartNew(() =>
            {
                for (var i = 0; i < PerWriter; i++)
                {
                    var page = writer * PerWriter + i;
                    var slot = pool.TryRent(5_000, CancellationToken.None);
                    if (slot < 0)
                    {
                        Interlocked.Increment(ref rentTimeouts);
                        return;
                    }

                    var bytes = pool.Span(slot);
                    BinaryPrimitives.WriteInt32LittleEndian(bytes, page);
                    BinaryPrimitives.WriteInt32LittleEndian(bytes[^4..], ~page);
                    pool.Publish(slot, page);
                }
            }, TaskCreationOptions.LongRunning);
        }

        var drained = 0;
        var consumer = Task.Factory.StartNew(() =>
        {
            while (drained < seen.Length && !Task.WhenAll(producers).IsFaulted && Volatile.Read(ref rentTimeouts) == 0)
            {
                pool.Published.Wait(100);
                drained += pool.Drain((page, bytes) =>
                {
                    if (BinaryPrimitives.ReadInt32LittleEndian(bytes) != page || BinaryPrimitives.ReadInt32LittleEndian(bytes[^4..]) != ~page)
                    {
                        Interlocked.Increment(ref badBytes);
                    }

                    seen[page]++;
                });
            }
        }, TaskCreationOptions.LongRunning);

        Assert.That(Task.WaitAll(producers, 15_000), Is.True, "the writers did not finish");
        Assert.That(consumer.Wait(5_000), Is.True, "the drainer did not finish");
        Assert.Multiple(() =>
        {
            Assert.That(rentTimeouts, Is.Zero, "a slot was lost: a writer found the pool empty for 5 s");
            Assert.That(drained, Is.EqualTo(seen.Length));
            Assert.That(Array.FindIndex(seen, n => n != 1), Is.EqualTo(-1), "a pre-image was lost or delivered twice");
            Assert.That(badBytes, Is.Zero, "a drained slot did not hold its page's bytes");
        });

        for (var i = 0; i < slots; i++)
        {
            Assert.That(pool.TryRent(0, CancellationToken.None), Is.GreaterThanOrEqualTo(0), "every slot is back in the pool");
        }
    }
}
