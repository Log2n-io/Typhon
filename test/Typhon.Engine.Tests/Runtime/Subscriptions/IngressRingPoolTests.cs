using System;
using System.Collections.Generic;
using System.Threading;
using NUnit.Framework;

namespace Typhon.Engine.Tests;

/// <summary>
/// #956 — the slab pool that carves per-session ingress rings: native slabs, a free-list bitmap, leases that make a stale return detectable, and an
/// exhaustion path that refuses admission instead of waiting.
/// </summary>
/// <remarks>
/// <para>
/// The properties asserted here are the ones an operator feels. A ring must be a real, distinct buffer — two sessions sharing one would present as crosstalk
/// between clients rather than as a pool bug. The budget must be a hard ceiling, because it is the only thing between untrusted connection counts and the
/// process's memory. And exhaustion must refuse rather than block: the caller is admitting a connection, not running the tick.
/// </para>
/// <para>
/// The memory is real allocator memory rather than a fixture array, for the reason <c>IngressRingTests</c> gives: "not a GC object" is itself part of the
/// contract, and v1's pinned-object-heap buffer is the verified negative precedent.
/// </para>
/// </remarks>
[TestFixture]
class IngressRingPoolTests
{
    private const int RingBytes = 4096;

    private ResourceRegistry _registry;
    private MemoryAllocator _allocator;

    [SetUp]
    public void SetUp()
    {
        _registry = new ResourceRegistry(new ResourceRegistryOptions { Name = "IngressRingPoolTests" });
        _allocator = new MemoryAllocator(_registry, new MemoryAllocatorOptions { Name = "IngressRingPoolAllocator" });
    }

    [TearDown]
    public void TearDown()
    {
        _allocator?.Dispose();
        _registry?.Dispose();
    }

    private IngressRingPool NewPool(long budgetBytes, int ringBytes = RingBytes, string id = "IngressRingPool") =>
        new(id, _registry.Runtime, _allocator, new SubscriptionsOptions
        {
            IngressRingBytes = ringBytes,
            IngressPoolBudgetBytes = budgetBytes,
        });

    private static byte[] Record(int length, byte seed)
    {
        var bytes = new byte[length];
        for (var i = 0; i < length; i++)
        {
            bytes[i] = (byte)(seed + i);
        }

        return bytes;
    }

    private static byte[] DrainOne(IngressRing ring)
    {
        var destination = new byte[512];
        var bytes = ring.Drain(destination, out var records);
        return records == 0 ? [] : IngressRing.ReadFramed(destination.AsSpan(0, bytes), out _).ToArray();
    }

    [Test]
    public void AFreshPoolCommitsNothingAndCapsSessionsAtTheBudget()
    {
        using var pool = NewPool(64 * 1024);

        Assert.Multiple(() =>
        {
            Assert.That(pool.CommittedBytes, Is.Zero, "nothing may be committed before the first session");
            Assert.That(pool.CarvedCount, Is.Zero);
            Assert.That(pool.MaxRingCount, Is.EqualTo(16), "the budget divided by the ring size IS the concurrent-session cap");
            Assert.That(pool.RingBytes, Is.EqualTo(RingBytes));
        });
    }

    /// <summary>
    /// Growth is slab-granular, so a budget that cannot cover a further whole slab yields no further rings however many ring-sized holes remain. Computing the
    /// cap as <c>budget / ringBytes</c> would claim 24 here and deliver 16 — and that number is the documented session cap.
    /// </summary>
    [Test]
    public void ABudgetThatIsNotASlabMultipleReportsTheCapItCanActuallyDeliver()
    {
        using var pool = NewPool(100_000);

        var admitted = 0;
        while (pool.TryAcquire(out _))
        {
            admitted++;
        }

        Assert.Multiple(() =>
        {
            Assert.That(pool.MaxRingCount, Is.EqualTo(16), "one 64 KiB slab fits in 100 000 bytes; a second does not");
            Assert.That(admitted, Is.EqualTo(pool.MaxRingCount), "the advertised cap must be the number of sessions actually admitted");
            Assert.That(pool.CommittedBytes, Is.LessThanOrEqualTo(100_000L));
        });
    }

    [Test]
    public void TheFirstAcquireCommitsASlabAndHandsOutAUsableRing()
    {
        using var pool = NewPool(1024 * 1024);

        Assert.That(pool.TryAcquire(out var lease), Is.True);
        Assert.That(lease.IsValid, Is.True);

        var payload = Record(48, 0x20);
        Assert.That(lease.Ring.TryWrite(payload), Is.True, "a ring straight from the pool must be writable");

        Assert.Multiple(() =>
        {
            Assert.That(DrainOne(lease.Ring), Is.EqualTo(payload));
            Assert.That(lease.Ring.Capacity, Is.EqualTo(RingBytes));
            Assert.That(pool.CommittedBytes, Is.GreaterThan(0), "the first acquire commits a slab");
            Assert.That(pool.InUseCount, Is.EqualTo(1));
            Assert.That(pool.AcquiredCount, Is.EqualTo(1));
        });
    }

    /// <summary>
    /// Two sessions must never share a buffer. Asserted on the BYTES rather than on cursor state: two rings framing one buffer would have independent cursors
    /// and so would both look empty, which is what an <c>IsEmpty</c>-based check would have accepted.
    /// </summary>
    [Test]
    public void EveryRingIsADistinctBuffer()
    {
        using var pool = NewPool(1024 * 1024);
        var leases = new List<IngressRingLease>();
        for (var i = 0; i < 12; i++)
        {
            Assert.That(pool.TryAcquire(out var lease), Is.True);
            leases.Add(lease);
        }

        // A different payload per ring, all written before any is drained: if two rings shared a buffer, the later write would overwrite or interleave with
        // the earlier one and at least one read-back would differ.
        for (var i = 0; i < leases.Count; i++)
        {
            Assert.That(leases[i].Ring.TryWrite(Record(64, (byte)(i * 7))), Is.True);
        }

        for (var i = 0; i < leases.Count; i++)
        {
            Assert.That(DrainOne(leases[i].Ring), Is.EqualTo(Record(64, (byte)(i * 7))), $"ring {i} did not read back its own bytes");
        }
    }

    [Test]
    public void ReleaseResetsTheRingSoTheNextSessionInheritsNothing()
    {
        using var pool = NewPool(64 * 1024);

        Assert.That(pool.TryAcquire(out var first), Is.True);
        var payload = Record(200, 0x5A);
        while (first.Ring.TryWrite(payload))
        {
        }

        Assert.That(first.Ring.DroppedRecords, Is.GreaterThan(0), "precondition: the ring must carry state worth clearing");

        pool.Release(first);
        Assert.That(pool.InUseCount, Is.Zero);

        Assert.That(pool.TryAcquire(out var second), Is.True);
        Assert.Multiple(() =>
        {
            Assert.That(second.Ring.IsEmpty, Is.True, "a recycled ring must carry nothing from the last session");
            Assert.That(second.Ring.DroppedRecords, Is.Zero);
            Assert.That(second.Ring.BytesPending, Is.Zero);
        });
    }

    /// <summary>
    /// The ceiling is the whole point: it is what stops an untrusted connection count from being a memory-exhaustion path. Exhaustion must refuse, count, and
    /// return — never wait, never throw.
    /// </summary>
    [Test]
    public void ABudgetThatBindsRefusesAdmissionWithoutBlockingOrThrowing()
    {
        using var pool = NewPool(64 * 1024);
        var admitted = 0;

        for (var i = 0; i < 64; i++)
        {
            if (!pool.TryAcquire(out _))
            {
                break;
            }

            admitted++;
        }

        Assert.Multiple(() =>
        {
            Assert.That(admitted, Is.EqualTo(16), "the budget permits exactly budget/ringBytes sessions");
            Assert.That(pool.TryAcquire(out var refused), Is.False, "the next session must be refused, not queued");
            Assert.That(refused.IsValid, Is.False);
            Assert.That(pool.ExhaustedCount, Is.GreaterThan(0), "a refusal that is not counted is a refusal nobody can diagnose");
            Assert.That(pool.CommittedBytes, Is.LessThanOrEqualTo(pool.BudgetBytes), "the pool must never commit past its ceiling");
        });
    }

    [Test]
    public void AZeroBudgetAdmitsNothingAndIsNotAnError()
    {
        using var pool = NewPool(0);

        Assert.Multiple(() =>
        {
            Assert.That(pool.MaxRingCount, Is.Zero);
            Assert.That(pool.TryAcquire(out _), Is.False, "zero is a legal budget meaning 'accept no sessions'");
            Assert.That(pool.CommittedBytes, Is.Zero);
        });
    }

    /// <summary>A ring larger than the slab floor gets a slab of its own rather than dividing to zero rings per slab.</summary>
    [Test]
    public void ARingLargerThanTheSlabFloorGetsASlabOfItsOwn()
    {
        using var pool = NewPool(1024 * 1024, 256 * 1024);

        Assert.That(pool.TryAcquire(out var lease), Is.True);
        Assert.Multiple(() =>
        {
            Assert.That(lease.Ring.Capacity, Is.EqualTo(256 * 1024));
            Assert.That(pool.MaxRingCount, Is.EqualTo(4));
            Assert.That(pool.CommittedBytes, Is.EqualTo(256L * 1024));
        });
    }

    [Test]
    public void AMisconfiguredOptionFailsAtConstructionNamingTheOption()
    {
        Assert.Multiple(() =>
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => NewPool(64 * 1024, 4000, "NotPowerOfTwo"), "a non-power-of-two ring size cannot be masked");
            Assert.Throws<ArgumentOutOfRangeException>(() => NewPool(64 * 1024, 32, "TooSmall"), "a ring below 64 bytes cannot hold a framed record");
            Assert.Throws<ArgumentOutOfRangeException>(() => NewPool(-1, RingBytes, "NegativeBudget"), "a negative budget would disable the pool silently");
        });
    }

    [Test]
    public void AReleasedRingCanBeAcquiredAgainAfterTheBudgetBound()
    {
        using var pool = NewPool(64 * 1024);
        var held = new List<IngressRingLease>();
        while (pool.TryAcquire(out var lease))
        {
            held.Add(lease);
        }

        Assert.That(pool.TryAcquire(out _), Is.False, "precondition: the pool must be full");

        pool.Release(held[3]);
        Assert.That(pool.TryAcquire(out var reused), Is.True, "a closed session's ring must become available again");
        Assert.That(reused.Ring, Is.SameAs(held[3].Ring));
    }

    /// <summary>
    /// The defect a bare-object pool cannot catch: release R, let another session take R, then release R a second time. Every check available from the object
    /// alone passes — the slot is occupied, the reference matches, the free bit is clear — so the pool would reset a ring the second session is actively using
    /// and then publish it as free, handing one buffer to two sessions. The lease's generation is what makes it detectable.
    /// </summary>
    [Test]
    public void AStaleReleaseIsRejectedAfterTheRingHasBeenGrantedToAnotherSession()
    {
        using var pool = NewPool(64 * 1024);

        Assert.That(pool.TryAcquire(out var first), Is.True);
        pool.Release(first);
        Assert.That(pool.TryAcquire(out var second), Is.True);
        Assert.That(second.Ring, Is.SameAs(first.Ring), "precondition: the slot must have been handed out again for this to be the stale case");

        // The second session is mid-flight.
        var payload = Record(40, 0x6B);
        Assert.That(second.Ring.TryWrite(payload), Is.True);

        Assert.Throws<ArgumentException>(() => pool.Release(first), "a stale lease must not be honoured");

        Assert.Multiple(() =>
        {
            Assert.That(second.Ring.IsEmpty, Is.False, "the live session's data must survive the rejected release");
            Assert.That(DrainOne(second.Ring), Is.EqualTo(payload));
            Assert.That(pool.InUseCount, Is.EqualTo(1), "the rejected release must not have decremented the in-use count");
        });
    }

    [Test]
    public void ReturningARingTwiceOrReturningAForeignRingThrows()
    {
        using var pool = NewPool(64 * 1024);
        using var other = NewPool(64 * 1024, RingBytes, "OtherPool");

        Assert.That(pool.TryAcquire(out var lease), Is.True);
        pool.Release(lease);

        Assert.Multiple(() =>
        {
            Assert.Throws<ArgumentException>(() => pool.Release(lease), "a double return must not be tolerated");
            Assert.Throws<ArgumentException>(() => pool.Release(default), "a lease naming no ring is a programming error");
            Assert.That(other.TryAcquire(out var foreign), Is.True);
            Assert.Throws<ArgumentException>(() => pool.Release(foreign), "a lease from another pool is not this pool's to honour");
        });
    }

    /// <summary>
    /// Disposal can precede the last session's close, and a release arriving afterwards has nothing to return. Throwing there would turn a shutdown ordering
    /// detail into a failure on a path that must stay quiet.
    /// </summary>
    [Test]
    public void ReleasingAfterDisposalIsQuiet()
    {
        var pool = NewPool(64 * 1024, RingBytes, "DisposeOrder");
        Assert.That(pool.TryAcquire(out var lease), Is.True);

        pool.Dispose();

        Assert.DoesNotThrow(() => pool.Release(lease), "a release after disposal must not throw");
        Assert.That(pool.TryAcquire(out var after), Is.False, "a disposed pool admits nothing");
        Assert.That(after.IsValid, Is.False, "a refused acquire must not hand back a ring over freed memory");
    }

    /// <summary>
    /// The buffers are native, so a collection must not disturb them. This is the property whose absence was the SWG x64 <c>0x80131506</c> crash: a pinned
    /// object-heap array stops the GC moving a buffer, not freeing it, and the pointer outlived the array.
    /// </summary>
    [Test]
    public void RingContentsSurviveAGarbageCollection()
    {
        using var pool = NewPool(64 * 1024);
        Assert.That(pool.TryAcquire(out var lease), Is.True);

        var payload = Record(96, 0x3C);
        Assert.That(lease.Ring.TryWrite(payload), Is.True);

        GC.Collect(2, GCCollectionMode.Forced, true, true);
        GC.WaitForPendingFinalizers();
        GC.Collect(2, GCCollectionMode.Forced, true, true);

        Assert.That(DrainOne(lease.Ring), Is.EqualTo(payload), "the record must survive a full collection — the buffer is not GC memory");
    }

    /// <summary>
    /// Sessions open and close on transport threads, so acquire and release race. No slot may ever be held twice at once, and no acquire may be refused while
    /// the pool has far more rings than there are threads.
    /// </summary>
    [Test]
    [CancelAfter(30_000)]
    public void ConcurrentAcquireAndReleaseNeverHandsOneRingToTwoHolders()
    {
        using var pool = NewPool(256 * 1024);
        const int Threads = 4;
        const int Rounds = 500;

        var live = new int[pool.MaxRingCount];
        var doubleHeld = 0;
        var failures = 0;
        var threads = new Thread[Threads];

        for (var t = 0; t < Threads; t++)
        {
            threads[t] = new Thread(() =>
            {
                for (var i = 0; i < Rounds; i++)
                {
                    if (!pool.TryAcquire(out var lease))
                    {
                        Interlocked.Increment(ref failures);
                        continue;
                    }

                    if (Interlocked.Increment(ref live[lease.Slot]) != 1)
                    {
                        Interlocked.Increment(ref doubleHeld);
                    }

                    // Touch the buffer while holding it: a slot handed out twice shows up as interleaved bytes, not just as a counter.
                    lease.Ring.TryWrite(Record(32, (byte)lease.Slot));

                    Interlocked.Decrement(ref live[lease.Slot]);
                    pool.Release(lease);
                }
            });
        }

        foreach (var thread in threads)
        {
            thread.Start();
        }

        foreach (var thread in threads)
        {
            Assert.That(thread.Join(TimeSpan.FromSeconds(20)), Is.True, "a pool operation blocked far longer than a slot claim should ever take");
        }

        Assert.Multiple(() =>
        {
            Assert.That(doubleHeld, Is.Zero, "a slot was held by two threads at once");
            Assert.That(failures, Is.Zero, "the pool has far more rings than threads, so no acquire should have been refused");
            Assert.That(pool.InUseCount, Is.Zero, "every acquire was released");
        });
    }
}
