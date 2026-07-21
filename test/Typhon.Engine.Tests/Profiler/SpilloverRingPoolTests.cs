using System;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using Typhon.Profiler;

namespace Typhon.Engine.Tests.Profiler;

/// <summary>
/// Unit tests for the static <see cref="SpilloverRingPool"/>. Covers lifecycle (Initialize / Shutdown / re-Init),
/// acquire / release semantics including reset-on-release, exhaustion behavior, and concurrent acquire under load.
/// All tests Shutdown the pool in TearDown so cross-test state can't leak.
/// </summary>
[TestFixture]
[NonParallelizable] // shares static SpilloverRingPool state with TraceRecordChainTests; serialize.
public sealed class SpilloverRingPoolTests
{
    private const int BufferSize = 64 * 1024;

    [TearDown]
    public void TearDown()
    {
        SpilloverRingPool.Shutdown();
    }

    [Test]
    public void Initialize_AllocatesExactlyNBuffersOfGivenSize()
    {
        SpilloverRingPool.Initialize(8, BufferSize);
        Assert.That(SpilloverRingPool.IsInitialized, Is.True);
        Assert.That(SpilloverRingPool.InitialCount, Is.EqualTo(8));
        Assert.That(SpilloverRingPool.BufferSizeBytes, Is.EqualTo(BufferSize));
        Assert.That(SpilloverRingPool.InUseCount, Is.EqualTo(0));
        // Acquire all 8 to confirm count is exact
        for (var i = 0; i < 8; i++)
        {
            var ring = SpilloverRingPool.TryAcquire();
            Assert.That(ring, Is.Not.Null, $"buffer {i} should be available");
        }
        Assert.That(SpilloverRingPool.TryAcquire(), Is.Null, "ninth acquire should fail (pool exhausted)");
    }

    [Test]
    public void TryAcquire_DecrementsAvailable_IncrementsAcquired_AndInUse()
    {
        SpilloverRingPool.Initialize(2, BufferSize);
        var r1 = SpilloverRingPool.TryAcquire();
        Assert.That(SpilloverRingPool.AcquiredCount, Is.EqualTo(1));
        Assert.That(SpilloverRingPool.InUseCount, Is.EqualTo(1));
        var r2 = SpilloverRingPool.TryAcquire();
        Assert.That(SpilloverRingPool.AcquiredCount, Is.EqualTo(2));
        Assert.That(SpilloverRingPool.InUseCount, Is.EqualTo(2));
        Assert.That(r1, Is.Not.SameAs(r2));
    }

    [Test]
    public void TryAcquire_ReturnsNull_WhenPoolEmpty_AndIncrementsExhausted()
    {
        SpilloverRingPool.Initialize(1, BufferSize);
        var ring = SpilloverRingPool.TryAcquire();
        Assert.That(ring, Is.Not.Null);
        var second = SpilloverRingPool.TryAcquire();
        Assert.That(second, Is.Null);
        Assert.That(SpilloverRingPool.ExhaustedCount, Is.EqualTo(1));
    }

    [Test]
    public void TryAcquire_ReturnsNull_WhenNotInitialized_AndIncrementsExhausted()
    {
        // No Initialize call — pool is null.
        Assert.That(SpilloverRingPool.IsInitialized, Is.False);
        var ring = SpilloverRingPool.TryAcquire();
        Assert.That(ring, Is.Null);
        Assert.That(SpilloverRingPool.ExhaustedCount, Is.EqualTo(1));
    }

    [Test]
    public void Release_ReturnsBufferToPool()
    {
        SpilloverRingPool.Initialize(1, BufferSize);
        var ring = SpilloverRingPool.TryAcquire();
        Assert.That(SpilloverRingPool.InUseCount, Is.EqualTo(1));
        SpilloverRingPool.Release(ring);
        Assert.That(SpilloverRingPool.InUseCount, Is.EqualTo(0));
        // Re-acquire should succeed
        var second = SpilloverRingPool.TryAcquire();
        Assert.That(second, Is.SameAs(ring), "released buffer should be re-acquirable");
    }

    [Test]
    public void Release_CallsResetOnTheBuffer()
    {
        SpilloverRingPool.Initialize(1, BufferSize);
        var ring = SpilloverRingPool.TryAcquire();
        // Dirty the buffer: write something, drop something, link a Next.
        Assert.That(ring.TryReserve(16, (byte)TraceEventKind.TickStart, out var dst), Is.True);
        // Mark a u16 size in the destination so the record is well-formed for any drain.
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(dst, 16);
        ring.Publish();
        // Use the typed overload to bump per-kind drop counter on a real overflow. Fill the ring up to force
        // the next reservation to fail.
        var hugeKind = (byte)TraceEventKind.SchedulerChunk;
        while (ring.TryReserve(64, hugeKind, out var d))
        {
            System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(d, 64);
            ring.Publish();
        }
        Assert.That(ring.DroppedEvents, Is.GreaterThan(0));
        Assert.That(ring.DroppedEventsForKind(hugeKind), Is.GreaterThan(0));
        // Link a synthetic Next so we can verify Reset clears it
        var stranger = new TraceRecordRing(64 * 1024);
        ring.SetNext(stranger);

        SpilloverRingPool.Release(ring);

        // Re-acquire and verify it's clean.
        var same = SpilloverRingPool.TryAcquire();
        Assert.That(same, Is.SameAs(ring));
        Assert.That(same.IsEmpty, Is.True, "Reset should clear head/tail");
        Assert.That(same.DroppedEvents, Is.EqualTo(0), "Reset should clear dropped count");
        Assert.That(same.DroppedEventsForKind(hugeKind), Is.EqualTo(0), "Reset should clear per-kind drops");
        Assert.That(same.Next, Is.Null, "Reset should clear Next link");
    }

    [Test]
    public void Shutdown_DropsReferences_TryAcquireReturnsNull()
    {
        SpilloverRingPool.Initialize(2, BufferSize);
        SpilloverRingPool.Shutdown();
        Assert.That(SpilloverRingPool.IsInitialized, Is.False);
        Assert.That(SpilloverRingPool.TryAcquire(), Is.Null);
    }

    [Test]
    public void Initialize_AfterShutdown_ReAllocates()
    {
        SpilloverRingPool.Initialize(2, BufferSize);
        var first = SpilloverRingPool.TryAcquire();
        SpilloverRingPool.Shutdown();
        SpilloverRingPool.Initialize(2, BufferSize);
        var second = SpilloverRingPool.TryAcquire();
        Assert.That(second, Is.Not.Null);
        Assert.That(second, Is.Not.SameAs(first), "Shutdown drops references; new Initialize allocates fresh rings");
        Assert.That(SpilloverRingPool.AcquiredCount, Is.EqualTo(1), "Counters reset on Initialize");
    }

    [Test]
    public void Counters_AreAtomicUnderConcurrentAcquireRelease()
    {
        SpilloverRingPool.Initialize(8, BufferSize);
        const int threadsPerWorker = 4;
        const int iterationsPerThread = 1000;
        var tasks = new Task[threadsPerWorker];
        for (var t = 0; t < threadsPerWorker; t++)
        {
            tasks[t] = Task.Run(() =>
            {
                for (var i = 0; i < iterationsPerThread; i++)
                {
                    var ring = SpilloverRingPool.TryAcquire();
                    if (ring != null)
                    {
                        SpilloverRingPool.Release(ring);
                    }
                }
            });
        }
        Task.WaitAll(tasks, TimeSpan.FromSeconds(5));
        Assert.That(SpilloverRingPool.InUseCount, Is.EqualTo(0), "every acquire was paired with a release");
        // We can't assert exact AcquiredCount because some attempts may have raced and gotten exhausted; just sanity.
        Assert.That(SpilloverRingPool.AcquiredCount + SpilloverRingPool.ExhaustedCount,
            Is.EqualTo(threadsPerWorker * iterationsPerThread));
    }

    [Test]
    public void ConcurrentAcquireOnExhaustion_ExactlyOneSucceeds()
    {
        SpilloverRingPool.Initialize(1, BufferSize);
        const int numThreads = 8;
        var winners = 0;
        var barrier = new Barrier(numThreads);
        var tasks = new Task[numThreads];
        for (var t = 0; t < numThreads; t++)
        {
            tasks[t] = Task.Run(() =>
            {
                barrier.SignalAndWait();
                var ring = SpilloverRingPool.TryAcquire();
                if (ring != null)
                {
                    Interlocked.Increment(ref winners);
                }
            });
        }
        Task.WaitAll(tasks, TimeSpan.FromSeconds(5));
        Assert.That(winners, Is.EqualTo(1));
        Assert.That(SpilloverRingPool.ExhaustedCount, Is.EqualTo(numThreads - 1));
    }

    [Test]
    public void Initialize_RejectsBadParams()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => SpilloverRingPool.Initialize(-1, BufferSize));
        Assert.Throws<ArgumentOutOfRangeException>(() => SpilloverRingPool.Initialize(1, 32 * 1024)); // < 64 KiB
        Assert.Throws<ArgumentOutOfRangeException>(() => SpilloverRingPool.Initialize(1, 100 * 1024)); // not power-of-2
    }

    [Test]
    public void Initialize_WithZeroCount_AllAcquiresFail()
    {
        SpilloverRingPool.Initialize(0, BufferSize);
        Assert.That(SpilloverRingPool.IsInitialized, Is.True);
        Assert.That(SpilloverRingPool.InitialCount, Is.EqualTo(0));
        Assert.That(SpilloverRingPool.TryAcquire(), Is.Null);
        Assert.That(SpilloverRingPool.ExhaustedCount, Is.EqualTo(1));
    }
}
