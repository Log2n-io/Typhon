using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using Typhon.Engine.Internals;

namespace Typhon.Engine.Tests.Runtime;

/// <summary>The per-tick cluster snapshot: one worker fills a cluster, every other reads it, and a failed fill never leaves a reader spinning.</summary>
[TestFixture]
unsafe class ClusterSnapshotStoreTests
{
    private const int FieldsOffset = 8;
    private const int Stride = 16;

    private static byte* NewCluster(ulong occupancy)
    {
        var page = (byte*)NativeMemory.AllocZeroed((nuint)(FieldsOffset + (64 * Stride)));
        *(ulong*)page = occupancy;
        return page;
    }

    /// <summary>A claim whose holder fails before filling is released, and a worker waiting on it claims the fill itself rather than spinning forever.</summary>
    [Test]
    public void AnAbandonedClaimIsReclaimedByAWaiter()
    {
        var store = new ClusterSnapshotStore();
        store.EnsureCapacity(4);
        var page = NewCluster(0b101);
        try
        {
            store.TryGet(1, 7, out var first);
            Assert.That(first, Is.True, "the first caller must win the claim");

            var waiter = Task.Run(() =>
            {
                store.TryGet(1, 7, out var mustFill);
                return mustFill;
            });

            store.Abandon(1, 7);
            Assert.That(waiter.Wait(5000), Is.True, "the waiter kept spinning on an abandoned claim");
            Assert.That(waiter.Result, Is.True, "the waiter must re-claim the fill once the claim is abandoned");

            store.Fill(1, 7, page, FieldsOffset, Stride, 0);
            Assert.That(store.TryGet(1, 7, out var again), Is.EqualTo(0b101UL));
            Assert.That(again, Is.False);
        }
        finally
        {
            NativeMemory.Free(page);
        }
    }

    /// <summary>Many workers reaching one cluster in the same tick: exactly one fills it, and every one reads the occupancy the fill published.</summary>
    [Test]
    public void ConcurrentReadersSeeOneFill()
    {
        var store = new ClusterSnapshotStore();
        store.EnsureCapacity(8);
        var page = NewCluster(0xF0F0UL);
        try
        {
            for (var tick = 1L; tick <= 200; tick++)
            {
                var fills = 0;
                var wrong = 0;
                var t = tick;
                Parallel.For(0, 8, _ =>
                {
                    var occ = store.TryGet(3, t, out var mustFill);
                    if (mustFill)
                    {
                        Interlocked.Increment(ref fills);
                        occ = store.Fill(3, t, page, FieldsOffset, Stride, 0);
                    }

                    if (occ != 0xF0F0UL)
                    {
                        Interlocked.Increment(ref wrong);
                    }
                });

                Assert.That(fills, Is.EqualTo(1), $"tick {tick}: the cluster was filled {fills} times");
                Assert.That(wrong, Is.Zero, $"tick {tick}: {wrong} readers saw an occupancy the fill did not publish");
            }
        }
        finally
        {
            NativeMemory.Free(page);
        }
    }

    /// <summary>Tick zero is indistinguishable from "never filled", so it is never shared: every caller opens privately.</summary>
    [Test]
    public void TickZeroIsNeverShared()
    {
        var store = new ClusterSnapshotStore();
        store.EnsureCapacity(4);
        store.TryGet(0, 0, out var a);
        store.TryGet(0, 0, out var b);
        Assert.That(a && b, Is.True);
        Assert.That(store.TryBlockOf(0, 0, out _), Is.False);
    }
}
