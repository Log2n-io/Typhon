using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using Typhon.Engine.Internals;
using static Typhon.Engine.Internals.ClusterSnapshotStore;

namespace Typhon.Engine.Tests.Runtime;

/// <summary>The per-tick cluster snapshot: one worker fills a cluster, every other reads it, and a failed fill never strands the cluster for the tick.</summary>
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

    /// <summary>
    /// A claim whose holder fails before filling is released, and the next caller claims the fill itself instead of reading the cluster's page for the rest
    /// of the tick.
    /// </summary>
    [Test]
    public void AnAbandonedClaimIsReclaimedByTheNextCaller()
    {
        var store = new ClusterSnapshotStore();
        store.EnsureCapacity(4);
        var page = NewCluster(0b101);
        try
        {
            store.TryGet(1, 7, out var first);
            Assert.That(first, Is.EqualTo(SnapshotClaim.Fill), "the first caller must win the claim");

            store.TryGet(1, 7, out var busy);
            Assert.That(busy, Is.EqualTo(SnapshotClaim.Busy), "a fill in progress must be reported to the next caller");

            store.Abandon(1, 7);
            store.TryGet(1, 7, out var second);
            Assert.That(second, Is.EqualTo(SnapshotClaim.Fill), "an abandoned claim must be reclaimable");

            store.Fill(1, 7, page, FieldsOffset, Stride, 0);
            Assert.That(store.TryGet(1, 7, out var again), Is.EqualTo(0b101UL));
            Assert.That(again, Is.EqualTo(SnapshotClaim.Ready));
        }
        finally
        {
            NativeMemory.Free(page);
        }
    }

    /// <summary>
    /// A caller that finds a fill in progress is told so at once and writes nothing: the claim stays the filler's, and once the fill publishes the next
    /// caller reads it from the store.
    /// </summary>
    [Test]
    public void ACallerFindingAFillInProgressIsToldBusyAndLeavesTheClaimAlone()
    {
        var store = new ClusterSnapshotStore();
        store.EnsureCapacity(4);
        var page = NewCluster(0b110);
        try
        {
            store.TryGet(2, 5, out var first);
            Assert.That(first, Is.EqualTo(SnapshotClaim.Fill));

            var occ = store.TryGet(2, 5, out var second);
            Assert.That(second, Is.EqualTo(SnapshotClaim.Busy), "a fill in progress must be reported, not waited for");
            Assert.That(occ, Is.Zero);
            Assert.That(store.TryBlockOf(2, 5, out _), Is.False, "a busy answer must not publish anything");

            // Every caller that meets the held claim, at once and from several threads, is told the same thing and none of them blocks. The claim is held
            // for the whole of this, so the answer does not depend on a race being won: it is what the never-waiting claim promises.
            var busies = 0;
            Parallel.For(0, 8, _ =>
            {
                store.TryGet(2, 5, out var concurrent);
                if (concurrent == SnapshotClaim.Busy)
                {
                    Interlocked.Increment(ref busies);
                }
            });

            Assert.That(busies, Is.EqualTo(8), "a held claim must be reported busy to every caller");

            store.Fill(2, 5, page, FieldsOffset, Stride, 0);
            Assert.That(store.TryGet(2, 5, out var third), Is.EqualTo(0b110UL));
            Assert.That(third, Is.EqualTo(SnapshotClaim.Ready));
        }
        finally
        {
            NativeMemory.Free(page);
        }
    }

    /// <summary>
    /// Many workers reaching one cluster in the same tick: exactly one fills it, every other either reads what the fill published or is told busy and reads
    /// the page itself, and none of them blocks.
    /// </summary>
    [Test]
    public void ConcurrentReadersFillOnceAndNoneWaits()
    {
        var store = new ClusterSnapshotStore();
        store.EnsureCapacity(8);
        var page = NewCluster(0x0FF0UL);
        try
        {
            for (var tick = 1L; tick <= 200; tick++)
            {
                var fills = 0;
                var wrong = 0;
                var t = tick;
                Parallel.For(0, 8, _ =>
                {
                    var occ = store.TryGet(5, t, out var claim);
                    if (claim == SnapshotClaim.Fill)
                    {
                        Interlocked.Increment(ref fills);
                        occ = store.Fill(5, t, page, FieldsOffset, Stride, 0);
                    }
                    else if (claim == SnapshotClaim.Busy)
                    {
                        // What a worker does when the store cannot serve it: read the cluster's page itself. Whether this happens at all depends on the
                        // threads overlapping, which is why the busy answer is asserted deterministically in the fixture above rather than here.
                        occ = Volatile.Read(ref *(ulong*)page);
                    }

                    if (occ != 0x0FF0UL)
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

    /// <summary>A fill keeps each occupied slot's box as four columns, which is what the block kernel reads.</summary>
    [Test]
    public void AFillTransposesTheOccupiedBoxesIntoColumns()
    {
        var store = new ClusterSnapshotStore();
        store.EnsureCapacity(4);
        var page = NewCluster(0b1001UL);
        try
        {
            var boxes = (float*)(page + FieldsOffset);
            for (var slot = 0; slot < 64; slot++)
            {
                boxes[(slot * 4) + 0] = slot;
                boxes[(slot * 4) + 1] = slot + 100f;
                boxes[(slot * 4) + 2] = slot + 200f;
                boxes[(slot * 4) + 3] = slot + 300f;
            }

            store.TryGet(2, 9, out var claim);
            Assert.That(claim, Is.EqualTo(SnapshotClaim.Fill));
            Assert.That(store.Fill(2, 9, page, FieldsOffset, Stride, 0), Is.EqualTo(0b1001UL));

            ref var columns = ref store.Columns(2);
            foreach (var slot in new[] { 0, 3 })
            {
                Assert.That(System.Runtime.CompilerServices.Unsafe.Add(ref columns, slot), Is.EqualTo((float)slot));
                Assert.That(System.Runtime.CompilerServices.Unsafe.Add(ref columns, 64 + slot), Is.EqualTo(slot + 100f));
                Assert.That(System.Runtime.CompilerServices.Unsafe.Add(ref columns, 128 + slot), Is.EqualTo(slot + 200f));
                Assert.That(System.Runtime.CompilerServices.Unsafe.Add(ref columns, 192 + slot), Is.EqualTo(slot + 300f));
            }

            Assert.That(System.Runtime.CompilerServices.Unsafe.Add(ref columns, 1), Is.Zero, "an unoccupied slot must not be copied");
        }
        finally
        {
            NativeMemory.Free(page);
        }
    }

    /// <summary>A read stamps its cluster once per tick: the first read reports itself, the rest do not, and the next tick starts over.</summary>
    [Test]
    public void AReadStampsItsClusterOncePerTick()
    {
        var store = new ClusterSnapshotStore();
        store.EnsureCapacity(4);
        Assert.That(store.MarkRead(1, 5), Is.True);
        Assert.That(store.MarkRead(1, 5), Is.False);
        Assert.That(store.MarkRead(1, 6), Is.True);
        Assert.That(store.MarkRead(99, 6), Is.False, "a cluster beyond the store is never stamped");
    }

    /// <summary>Tick zero is indistinguishable from "never filled", so it is never shared: every caller opens privately.</summary>
    [Test]
    public void TickZeroIsNeverShared()
    {
        var store = new ClusterSnapshotStore();
        store.EnsureCapacity(4);
        store.TryGet(0, 0, out var a);
        store.TryGet(0, 0, out var b);
        Assert.That(a == SnapshotClaim.Fill && b == SnapshotClaim.Fill, Is.True);
        Assert.That(store.TryBlockOf(0, 0, out _), Is.False);
    }
}
