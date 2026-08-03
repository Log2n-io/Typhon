using NUnit.Framework;
using System;
using System.Threading;

namespace Typhon.Engine.Tests;

/// <summary>
/// Multi-threaded stress tests for <see cref="WalCommitBuffer"/>.
/// All tests use <see cref="CancelAfterAttribute"/> as a safety timeout
/// to prevent hangs from deadlocks or infinite loops.
/// </summary>
[TestFixture]
public class WalCommitBufferConcurrencyTests : AllocatorTestBase
{
    // 256 KB per buffer — small enough to trigger swaps quickly
    private const int TestCapacity = 256 * 1024;

    private WalCommitBuffer CreateBuffer(int capacity = TestCapacity, long initialLSN = 1) =>
        new(MemoryAllocator, AllocationResource, capacity, initialLSN);

    #region NoOverlap — Data Integrity

    [Test]
    [CancelAfter(5000)]
    public void NoOverlap_ConcurrentProducers_NoDataCorruption()
    {
        using var buffer = CreateBuffer();
        const int threadCount = 4;
        const int claimsPerThread = 200;
        const int payloadSize = 64;
        var barrier = new Barrier(threadCount);
        var errors = 0;

        var producerExceptions = new Exception[threadCount];
        var threads = new Thread[threadCount];
        for (var t = 0; t < threadCount; t++)
        {
            var threadId = t;
            threads[t] = new Thread(() =>
            {
                try
                {
                    barrier.SignalAndWait();

                    for (var i = 0; i < claimsPerThread; i++)
                    {
                        var ctx = WaitContext.FromTimeout(TimeSpan.FromSeconds(3));
                        var claim = buffer.TryClaim(payloadSize, 1, ref ctx);
                        if (!claim.IsValid)
                        {
                            Interlocked.Increment(ref errors);
                            continue;
                        }

                        // Write a unique byte pattern: threadId
                        claim.DataSpan.Fill((byte)(threadId + 1));
                        buffer.Publish(ref claim);
                    }
                }
                catch (Exception ex)
                {
                    producerExceptions[threadId] = ex;
                }
            });
            threads[t].IsBackground = true;
            threads[t].Start();
        }

        // Consumer drains everything
        var totalFrames = 0;
        var consumerStop = 0;
        Exception consumerException = null;

        var consumer = new Thread(() =>
        {
            try
            {
                while (Volatile.Read(ref consumerStop) == 0)
                {
                    if (buffer.TryDrain(out var data, out _))
                    {
                        // Verify each frame's data is consistent (all same byte)
                        WalCommitBuffer.WalkFrames(data, (payload, recordCount) =>
                        {
                            if (payload.Length > 0)
                            {
                                var expected = payload[0];
                                for (var j = 1; j < payload.Length; j++)
                                {
                                    if (payload[j] != expected && payload[j] != 0)
                                    {
                                        Interlocked.Increment(ref errors);
                                    }
                                }
                            }

                            Interlocked.Add(ref totalFrames, 1);
                        });
                        buffer.CompleteDrain(data.Length);
                    }
                    else
                    {
                        buffer.WaitForData(10);
                    }
                }

                // Final drain pass after stop signal
                while (buffer.TryDrain(out var remaining, out _))
                {
                    WalCommitBuffer.WalkFrames(remaining, (payload, _) =>
                    {
                        Interlocked.Add(ref totalFrames, 1);
                    });
                    buffer.CompleteDrain(remaining.Length);
                }
            }
            catch (Exception ex)
            {
                consumerException = ex;
            }
        });
        consumer.IsBackground = true;
        consumer.Start();

        foreach (var thread in threads)
        {
            thread.Join();
        }

        // All producers done — signal consumer to do final drain and stop
        Volatile.Write(ref consumerStop, 1);
        consumer.Join();

        Assert.That(consumerException, Is.Null, $"Consumer thread threw: {consumerException}");
        for (var i = 0; i < threadCount; i++)
        {
            Assert.That(producerExceptions[i], Is.Null, $"Producer {i} threw: {producerExceptions[i]}");
        }

        Assert.That(errors, Is.EqualTo(0), "Data corruption detected");
        Assert.That(totalFrames, Is.EqualTo(threadCount * claimsPerThread));
    }

    #endregion

    #region HighContention — Throughput Stress

    [Test]
    [CancelAfter(5000)]
    public void HighContention_ManyProducers_AllComplete()
    {
        using var buffer = CreateBuffer();
        const int threadCount = 16;
        const int claimsPerThread = 100;
        const int payloadSize = 32;
        var completed = 0;
        var barrier = new Barrier(threadCount + 1); // +1 for consumer
        var consumerStop = 0;
        Exception consumerException = null;

        var consumer = new Thread(() =>
        {
            try
            {
                barrier.SignalAndWait();
                while (Volatile.Read(ref consumerStop) == 0)
                {
                    if (buffer.TryDrain(out var data, out _))
                    {
                        buffer.CompleteDrain(data.Length);
                    }
                    else
                    {
                        buffer.WaitForData(5);
                    }
                }

                // Final drain
                while (buffer.TryDrain(out var remaining, out _))
                {
                    buffer.CompleteDrain(remaining.Length);
                }
            }
            catch (Exception ex)
            {
                consumerException = ex;
            }
        });
        consumer.IsBackground = true;
        consumer.Start();

        var producerExceptions = new Exception[threadCount];
        var threads = new Thread[threadCount];
        for (var t = 0; t < threadCount; t++)
        {
            var threadIndex = t;
            threads[t] = new Thread(() =>
            {
                try
                {
                    barrier.SignalAndWait();
                    for (var i = 0; i < claimsPerThread; i++)
                    {
                        var ctx = WaitContext.FromTimeout(TimeSpan.FromSeconds(4));
                        var claim = buffer.TryClaim(payloadSize, 1, ref ctx);
                        claim.DataSpan.Fill(0xFF);
                        buffer.Publish(ref claim);
                        Interlocked.Increment(ref completed);
                    }
                }
                catch (Exception ex)
                {
                    producerExceptions[threadIndex] = ex;
                }
            });
            threads[t].IsBackground = true;
            threads[t].Start();
        }

        foreach (var thread in threads)
        {
            thread.Join();
        }

        Volatile.Write(ref consumerStop, 1);
        consumer.Join();

        Assert.That(consumerException, Is.Null, $"Consumer thread threw: {consumerException}");
        for (var i = 0; i < threadCount; i++)
        {
            Assert.That(producerExceptions[i], Is.Null, $"Producer {i} threw: {producerExceptions[i]}");
        }

        Assert.That(completed, Is.EqualTo(threadCount * claimsPerThread));
    }

    #endregion

    #region ContinuousFlow — Sustained Load

    [Test]
    [CancelAfter(5000)]
    public void ContinuousFlow_ProducersAndConsumer_BytesMatch()
    {
        using var buffer = CreateBuffer();
        const int threadCount = 4;
        const int claimsPerThread = 500;
        const int payloadSize = 48;
        long totalProducedFrames = 0;
        long totalConsumedFrames = 0;
        var consumerStop = 0;
        Exception consumerException = null;

        var consumer = new Thread(() =>
        {
            try
            {
                while (Volatile.Read(ref consumerStop) == 0)
                {
                    if (buffer.TryDrain(out var data, out var frameCount))
                    {
                        Interlocked.Add(ref totalConsumedFrames, frameCount);
                        buffer.CompleteDrain(data.Length);
                    }
                    else
                    {
                        buffer.WaitForData(5);
                    }
                }

                // Final drain — retry a few times because a swap may have just
                // produced an empty new buffer that needs one more scan after
                // producers' last frames arrive.
                for (var pass = 0; pass < 3; pass++)
                {
                    var drained = false;
                    while (buffer.TryDrain(out var remaining, out var fc))
                    {
                        Interlocked.Add(ref totalConsumedFrames, fc);
                        buffer.CompleteDrain(remaining.Length);
                        drained = true;
                    }

                    if (!drained)
                    {
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                consumerException = ex;
            }
        });
        consumer.IsBackground = true;
        consumer.Start();

        var producerExceptions = new Exception[threadCount];
        var threads = new Thread[threadCount];
        for (var t = 0; t < threadCount; t++)
        {
            var threadIndex = t;
            threads[t] = new Thread(() =>
            {
                try
                {
                    for (var i = 0; i < claimsPerThread; i++)
                    {
                        var ctx = WaitContext.FromTimeout(TimeSpan.FromSeconds(2));
                        var claim = buffer.TryClaim(payloadSize, 1, ref ctx);
                        claim.DataSpan.Fill(0xBB);
                        buffer.Publish(ref claim);
                        Interlocked.Increment(ref totalProducedFrames);
                    }
                }
                catch (Exception ex)
                {
                    producerExceptions[threadIndex] = ex;
                }
            });
            threads[t].IsBackground = true;
            threads[t].Start();
        }

        foreach (var thread in threads)
        {
            thread.Join();
        }

        Volatile.Write(ref consumerStop, 1);
        consumer.Join();

        Assert.That(consumerException, Is.Null, $"Consumer thread threw: {consumerException}");
        for (var i = 0; i < threadCount; i++)
        {
            Assert.That(producerExceptions[i], Is.Null, $"Producer {i} threw: {producerExceptions[i]}");
        }

        Assert.That(totalProducedFrames, Is.EqualTo(threadCount * claimsPerThread));
        Assert.That(totalConsumedFrames, Is.EqualTo(totalProducedFrames),
            $"Consumed {totalConsumedFrames} but produced {totalProducedFrames}");
    }

    #endregion

    #region OverflowSwap — Buffer Swap Under Contention

    [Test]
    [CancelAfter(5000)]
    public void OverflowSwap_ConcurrentProducers_AllClaimsSucceed()
    {
        // Small buffer to force frequent swaps
        const int smallCapacity = 64 * 1024;
        using var buffer = CreateBuffer(smallCapacity);
        const int threadCount = 4;
        const int claimsPerThread = 200;
        const int payloadSize = 128; // Large enough to fill buffer quickly
        var completed = 0;
        var consumerStop = 0;
        Exception consumerException = null;

        var consumer = new Thread(() =>
        {
            try
            {
                while (Volatile.Read(ref consumerStop) == 0)
                {
                    if (buffer.TryDrain(out var data, out _))
                    {
                        buffer.CompleteDrain(data.Length);
                    }
                    else
                    {
                        buffer.WaitForData(5);
                    }
                }

                while (buffer.TryDrain(out var remaining, out _))
                {
                    buffer.CompleteDrain(remaining.Length);
                }
            }
            catch (Exception ex)
            {
                consumerException = ex;
            }
        });
        consumer.IsBackground = true;
        consumer.Start();

        var producerExceptions = new Exception[threadCount];
        var threads = new Thread[threadCount];
        for (var t = 0; t < threadCount; t++)
        {
            var threadIndex = t;
            threads[t] = new Thread(() =>
            {
                try
                {
                    for (var i = 0; i < claimsPerThread; i++)
                    {
                        var ctx = WaitContext.FromTimeout(TimeSpan.FromSeconds(4));
                        var claim = buffer.TryClaim(payloadSize, 1, ref ctx);
                        claim.DataSpan.Fill(0xDD);
                        buffer.Publish(ref claim);
                        Interlocked.Increment(ref completed);
                    }
                }
                catch (Exception ex)
                {
                    producerExceptions[threadIndex] = ex;
                }
            });
            threads[t].IsBackground = true;
            threads[t].Start();
        }

        foreach (var thread in threads)
        {
            thread.Join();
        }

        Volatile.Write(ref consumerStop, 1);
        consumer.Join();

        Assert.That(consumerException, Is.Null, $"Consumer thread threw: {consumerException}");
        for (var i = 0; i < threadCount; i++)
        {
            Assert.That(producerExceptions[i], Is.Null, $"Producer {i} threw: {producerExceptions[i]}");
        }

        Assert.That(completed, Is.EqualTo(threadCount * claimsPerThread));
    }

    #endregion

    #region LatePublisher — Slow Producer During Overflow

    [Test]
    [CancelAfter(5000)]
    public void LatePublisher_SlowProducerDuringOverflow_DataIntegrityPreserved()
    {
        const int smallCapacity = 64 * 1024;
        using var buffer = CreateBuffer(smallCapacity);
        var lateByte = (byte)0xFE;
        var latePublished = false;
        var consumerStop = 0;
        var lateFrameSeen = false;
        Exception latePublisherException = null;
        Exception fastProducerException = null;
        Exception consumerException = null;

        // Late publisher: claims, sleeps, then publishes
        var latePublisher = new Thread(() =>
        {
            try
            {
                var ctx = WaitContext.FromTimeout(TimeSpan.FromSeconds(3));
                var claim = buffer.TryClaim(64, 1, ref ctx);
                claim.DataSpan.Fill(lateByte);
                Thread.Sleep(100); // Simulate slow serialization
                buffer.Publish(ref claim);
                latePublished = true;
            }
            catch (Exception ex)
            {
                latePublisherException = ex;
            }
        });
        latePublisher.IsBackground = true;
        latePublisher.Start();

        // Give the late publisher time to claim
        Thread.Sleep(20);

        // Fast producers fill the rest of the buffer
        var fastProducer = new Thread(() =>
        {
            try
            {
                for (var i = 0; i < 100; i++)
                {
                    var ctx = WaitContext.FromTimeout(TimeSpan.FromSeconds(3));
                    try
                    {
                        var claim = buffer.TryClaim(256, 1, ref ctx);
                        claim.DataSpan.Fill(0xAA);
                        buffer.Publish(ref claim);
                    }
                    catch (WalBackPressureTimeoutException)
                    {
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                fastProducerException = ex;
            }
        });
        fastProducer.IsBackground = true;
        fastProducer.Start();

        // Consumer drains and looks for the late publisher's data
        var consumer = new Thread(() =>
        {
            try
            {
                while (Volatile.Read(ref consumerStop) == 0)
                {
                    if (buffer.TryDrain(out var data, out _))
                    {
                        WalCommitBuffer.WalkFrames(data, (payload, _) =>
                        {
                            if (payload.Length > 0 && payload[0] == lateByte)
                            {
                                lateFrameSeen = true;
                            }
                        });
                        buffer.CompleteDrain(data.Length);
                    }
                    else
                    {
                        buffer.WaitForData(10);
                    }
                }

                // Final drain
                while (buffer.TryDrain(out var remaining, out _))
                {
                    WalCommitBuffer.WalkFrames(remaining, (payload, _) =>
                    {
                        if (payload.Length > 0 && payload[0] == lateByte)
                        {
                            lateFrameSeen = true;
                        }
                    });
                    buffer.CompleteDrain(remaining.Length);
                }
            }
            catch (Exception ex)
            {
                consumerException = ex;
            }
        });
        consumer.IsBackground = true;
        consumer.Start();

        latePublisher.Join();
        fastProducer.Join();

        // Wait for consumer to see the late publisher's data, then stop
        SpinWait.SpinUntil(() => lateFrameSeen, 2000);
        Volatile.Write(ref consumerStop, 1);
        consumer.Join();

        Assert.That(latePublisherException, Is.Null, $"Late publisher thread threw: {latePublisherException}");
        Assert.That(fastProducerException, Is.Null, $"Fast producer thread threw: {fastProducerException}");
        Assert.That(consumerException, Is.Null, $"Consumer thread threw: {consumerException}");
        Assert.That(latePublished, Is.True, "Late publisher should have completed");
        Assert.That(lateFrameSeen, Is.True, "Consumer should have seen the late publisher's data");
    }

    #endregion

    #region MultipleSwaps — Sustained Swap Stress

    [Test]
    [CancelAfter(5000)]
    public void MultipleSwaps_ManySwapsUnderLoad_NoDataLoss()
    {
        // Larger buffer (256KB) with enough claims to still trigger multiple swaps.
        // Using 2 producer threads to reduce CPU contention (spin-waiting producers
        // can starve the consumer on machines with few cores).
        using var buffer = CreateBuffer();
        const int threadCount = 2;
        const int claimsPerThread = 1000;
        const int payloadSize = 128;
        long totalProduced = 0;
        long totalConsumed = 0;
        var consumerStop = 0;
        var producerErrors = 0;
        Exception consumerException = null;

        var consumer = new Thread(() =>
        {
            try
            {
                while (Volatile.Read(ref consumerStop) == 0)
                {
                    if (buffer.TryDrain(out var data, out _))
                    {
                        WalCommitBuffer.WalkFrames(data, (_, recordCount) =>
                        {
                            Interlocked.Add(ref totalConsumed, recordCount);
                        });
                        buffer.CompleteDrain(data.Length);
                    }
                    else
                    {
                        buffer.WaitForData(5);
                    }
                }

                // Final drain
                while (buffer.TryDrain(out var remaining, out _))
                {
                    WalCommitBuffer.WalkFrames(remaining, (_, recordCount) =>
                    {
                        Interlocked.Add(ref totalConsumed, recordCount);
                    });
                    buffer.CompleteDrain(remaining.Length);
                }
            }
            catch (Exception ex)
            {
                consumerException = ex;
            }
        });
        consumer.IsBackground = true;
        consumer.Start();

        var threads = new Thread[threadCount];
        for (var t = 0; t < threadCount; t++)
        {
            threads[t] = new Thread(() =>
            {
                for (var i = 0; i < claimsPerThread; i++)
                {
                    try
                    {
                        var ctx = WaitContext.FromTimeout(TimeSpan.FromSeconds(4));
                        var claim = buffer.TryClaim(payloadSize, 1, ref ctx);
                        claim.DataSpan.Fill(0xCC);
                        buffer.Publish(ref claim);
                        Interlocked.Increment(ref totalProduced);
                    }
                    catch (Exception)
                    {
                        Interlocked.Increment(ref producerErrors);
                    }
                }
            });
            threads[t].IsBackground = true;
            threads[t].Start();
        }

        foreach (var thread in threads)
        {
            thread.Join();
        }

        Volatile.Write(ref consumerStop, 1);
        consumer.Join();

        Assert.That(consumerException, Is.Null, $"Consumer thread threw: {consumerException}");
        Assert.That(producerErrors, Is.EqualTo(0), "No producer errors expected");
        Assert.That(totalProduced, Is.EqualTo(threadCount * claimsPerThread), "All producers should complete");
        Assert.That(totalConsumed, Is.EqualTo(totalProduced), $"Consumer should see all {totalProduced} records, but only saw {totalConsumed}");
    }

    #endregion

    #region ClaimOrdering — WP-06 (buffer position order == LSN order)

    /// <summary>
    /// WP-06: a frame's buffer position and its LSN must be allocated so the two orders can never diverge (#581).
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is not a cosmetic ordering property. <c>TryDrain</c> walks frames in POSITION order and stops at the first unpublished one, so position order is
    /// the durability order; the LSN watermark is only a valid proxy for it while the two agree. When they diverge, a position-earlier frame carrying a HIGHER
    /// LSN lets the drain publish <c>DurableLsn</c> past a position-later frame whose bytes were never written — and a <c>DurabilityMode.Immediate</c> commit
    /// waiting on that lower LSN returns success with its record still in volatile memory (WP-02).
    /// </para>
    /// <para>
    /// The test deliberately stays inside ONE buffer generation: offsets restart at 0 on a swap, so a single offset-ordered comparison is only meaningful
    /// without one. The payload and claim count are sized to fit, and the test asserts no swap occurred rather than assuming it.
    /// </para>
    /// </remarks>
    [Test]
    [CancelAfter(30_000)]
    public void Claim_PositionOrderAndLsnOrderAgree_UnderConcurrentProducers()
    {
        using var buffer = CreateBuffer();
        const int threadCount = 8;
        const int claimsPerThread = 1_000;
        const int payloadSize = 8;   // frame = Align8(8 + 8) = 16 B, so 8 x 1000 x 16 = 128 KB fits inside the 256 KB buffer with no swap

        var startIndex = buffer.ActiveBufferIndex;
        var pairs = new (int Offset, long Lsn)[threadCount * claimsPerThread];
        var barrier = new Barrier(threadCount);
        var producerExceptions = new Exception[threadCount];
        var threads = new Thread[threadCount];

        for (var t = 0; t < threadCount; t++)
        {
            var threadId = t;
            threads[t] = new Thread(() =>
            {
                try
                {
                    barrier.SignalAndWait();

                    for (var i = 0; i < claimsPerThread; i++)
                    {
                        var ctx = WaitContext.FromTimeout(TimeSpan.FromSeconds(5));
                        var claim = buffer.TryClaim(payloadSize, 1, ref ctx);
                        pairs[(threadId * claimsPerThread) + i] = (claim.FrameOffset, claim.FirstLSN);
                        claim.DataSpan.Fill((byte)(threadId + 1));
                        buffer.Publish(ref claim);
                    }
                }
                catch (Exception ex)
                {
                    producerExceptions[threadId] = ex;
                }
            })
            { IsBackground = true };
            threads[t].Start();
        }

        foreach (var thread in threads)
        {
            thread.Join();
        }

        for (var t = 0; t < threadCount; t++)
        {
            Assert.That(producerExceptions[t], Is.Null, $"Producer {t} threw: {producerExceptions[t]}");
        }

        // Precondition: a swap would restart offsets at 0 and make the comparison below meaningless.
        Assert.That(buffer.ActiveBufferIndex, Is.EqualTo(startIndex),
            "precondition: the buffer swapped, so offsets are not from a single address space — shrink the workload");

        Array.Sort(pairs, static (a, b) => a.Offset.CompareTo(b.Offset));

        for (var i = 1; i < pairs.Length; i++)
        {
            if (pairs[i].Lsn <= pairs[i - 1].Lsn)
            {
                Assert.Fail(
                    $"WP-06 violated: claim at offset {pairs[i].Offset} holds LSN {pairs[i].Lsn}, but the earlier claim at offset {pairs[i - 1].Offset} "
                    + $"holds LSN {pairs[i - 1].Lsn}. Position order and LSN order have diverged, so DurableLsn can advance past an undrained frame (#581).");
            }
        }
    }

    /// <summary>
    /// LSNs stay globally unique and monotonic across buffer swaps, where the generation's LSN span is folded into the base (#581).
    /// </summary>
    /// <remarks>
    /// The packed claim word holds an LSN <i>offset within the current generation</i>, so every swap must carry that span into <c>_lsnBase</c> at its
    /// quiescent point. Getting the fold wrong — or letting a producer claim across it — reissues LSNs that were already handed to a committed transaction,
    /// which is worse than the ordering bug this change fixes. This drives many swaps under contention and checks the whole issued set.
    /// </remarks>
    [Test]
    [CancelAfter(60_000)]
    public void Claim_LsnsRemainUniqueAndMonotonic_AcrossManyBufferSwaps()
    {
        // Minimum capacity keeps the buffer small so the workload forces many swaps.
        using var buffer = CreateBuffer(64 * 1024);
        const int threadCount = 6;
        const int claimsPerThread = 3_000;
        const int payloadSize = 200;

        var lsns = new long[threadCount * claimsPerThread];
        var barrier = new Barrier(threadCount);
        var producerExceptions = new Exception[threadCount];
        var threads = new Thread[threadCount];
        var consumerStop = 0;

        // A consumer must run, or producers block forever once the buffer fills.
        var consumer = new Thread(() =>
        {
            while (Volatile.Read(ref consumerStop) == 0)
            {
                if (buffer.TryDrain(out var data, out _))
                {
                    buffer.CompleteDrain(data.Length);
                }
                else
                {
                    // Park instead of re-trying hot — every other consumer in this fixture does the same. Without it this loop burns a
                    // whole core polling an empty buffer while `threadCount` producers compete for what is left.
                    //
                    // This was ALSO once credited with fixing this test's CI failures. It did not: it only narrowed the window. The
                    // failure was an ABA in WalCommitBuffer's own back-pressure wait (producers keyed on the ping-pong _activeBufferIndex
                    // and missed a double swap), which is fixed in the engine and pinned by
                    // BackPressureWait_ProducerLappedByTwoSwaps_IsNotStillWaiting below.
                    buffer.WaitForData(5);
                }
            }

            while (buffer.TryDrain(out var remaining, out _))
            {
                buffer.CompleteDrain(remaining.Length);
            }
        })
        { IsBackground = true };
        consumer.Start();

        for (var t = 0; t < threadCount; t++)
        {
            var threadId = t;
            threads[t] = new Thread(() =>
            {
                try
                {
                    barrier.SignalAndWait();

                    for (var i = 0; i < claimsPerThread; i++)
                    {
                        var ctx = WaitContext.FromTimeout(TimeSpan.FromSeconds(20));
                        var claim = buffer.TryClaim(payloadSize, 1, ref ctx);
                        lsns[(threadId * claimsPerThread) + i] = claim.FirstLSN;
                        claim.DataSpan.Fill((byte)(threadId + 1));
                        buffer.Publish(ref claim);
                    }
                }
                catch (Exception ex)
                {
                    producerExceptions[threadId] = ex;
                }
            })
            { IsBackground = true };
            threads[t].Start();
        }

        foreach (var thread in threads)
        {
            thread.Join();
        }

        Volatile.Write(ref consumerStop, 1);
        consumer.Join();

        for (var t = 0; t < threadCount; t++)
        {
            Assert.That(producerExceptions[t], Is.Null, $"Producer {t} threw: {producerExceptions[t]}");
        }

        Array.Sort(lsns);
        for (var i = 1; i < lsns.Length; i++)
        {
            if (lsns[i] == lsns[i - 1])
            {
                Assert.Fail($"LSN {lsns[i]} was issued to two different claims — the generation LSN base was folded incorrectly across a buffer swap (#581).");
            }
        }

        Assert.That(lsns[0], Is.GreaterThanOrEqualTo(1), "LSNs start at the seeded initial value");
    }

    /// <summary>Capacity must fit the position half of the packed claim word (#581).</summary>
    [Test]
    public void Constructor_RejectsCapacityBeyondThePackedPositionField()
    {
        var tooBig = WalCommitBuffer.MaxBufferCapacity + 64;
        var ex = Assert.Throws<ArgumentException>(() => _ = CreateBuffer(tooBig));
        Assert.That(ex.Message, Does.Contain("at most"), "the rejection should name the ceiling");
    }

    /// <summary>
    /// A producer parked in back-pressure must be released by the swap that serves it, even when the active buffer index has since ping-ponged back to the
    /// value the producer started from.
    /// </summary>
    /// <remarks>
    /// Regression test. The wait used to be <c>while (_activeBufferIndex == bufferIndex)</c>, an ABA test on a single bit: a producer that was off-CPU while
    /// TWO swaps completed found the index back at its captured value and went on waiting for an edge that had already passed twice. Nothing recovered it —
    /// the only thing that advances the index is another producer overflowing the buffer — so once every live producer was in that state the buffer sat
    /// drained and idle until each deadline expired and threw <c>WalBackPressureTimeoutException</c> on a commit that had space available throughout.
    /// <para>
    /// The two swaps below are driven deterministically, so this test does not depend on losing a scheduling race to be meaningful. It asserts the predicate
    /// directly rather than trying to reproduce the race: reproducing it needed a producer descheduled across two whole swaps, which took a 2-core machine to
    /// hit and never happened on a developer box (found on a 3-core macOS CI runner; reproduced locally only under a 2-CPU affinity mask).
    /// </para>
    /// </remarks>
    [Test]
    [CancelAfter(10_000)]
    public void BackPressureWait_ProducerLappedByTwoSwaps_IsNotStillWaiting()
    {
        using var buffer = CreateBuffer(64 * 1024);

        var startIndex = buffer.ActiveBufferIndex;
        var producerGeneration = buffer.SwapGeneration;

        DriveOneSwap(buffer);
        DriveOneSwap(buffer);

        Assert.That(buffer.ActiveBufferIndex, Is.EqualTo(startIndex),
            "two swaps must bring the ping-pong index back to where it started — that is the premise of the bug this test guards.");
        Assert.That(buffer.SwapGeneration, Is.EqualTo(producerGeneration + 2), "the generation counter must NOT ping-pong");
        Assert.That(buffer.IsStillWaitingForSwap(producerGeneration), Is.False,
            "a producer that captured the pre-swap generation has been served twice over; keying the wait on the buffer index instead would leave it "
            + "blocked here until its deadline expired.");
    }

    /// <summary>
    /// Fills the active buffer, has a background producer take the over-capacity claim that requests a swap, and drains until the swap completes.
    /// </summary>
    private static void DriveOneSwap(WalCommitBuffer buffer)
    {
        const int payloadSize = 200;
        var frameSize = WalCommitBuffer.Align8(WalFrameHeader.SizeInBytes + payloadSize);

        // Fill to just under capacity. These all take CASE A and never block.
        while (buffer.TailPosition + frameSize <= buffer.BufferCapacity)
        {
            var ctx = WaitContext.FromTimeout(TimeSpan.FromSeconds(5));
            var claim = buffer.TryClaim(payloadSize, 1, ref ctx);
            claim.DataSpan.Fill(0xAB);
            buffer.Publish(ref claim);
        }

        // The next claim overflows: it writes the padding sentinel, requests the swap and parks. It has to run off-thread because this thread is the
        // consumer that has to drain before the swap can happen.
        var startGeneration = buffer.SwapGeneration;
        Exception producerFailure = null;
        var producer = new Thread(() =>
        {
            try
            {
                var ctx = WaitContext.FromTimeout(TimeSpan.FromSeconds(5));
                var claim = buffer.TryClaim(payloadSize, 1, ref ctx);
                claim.DataSpan.Fill(0xCD);
                buffer.Publish(ref claim);
            }
            catch (Exception ex)
            {
                producerFailure = ex;
            }
        })
        { IsBackground = true };
        producer.Start();

        // Drain until the swap lands. TryDrain performs it once the drain position reaches the padding sentinel.
        while (buffer.SwapGeneration == startGeneration)
        {
            if (buffer.TryDrain(out var data, out _))
            {
                buffer.CompleteDrain(data.Length);
            }
            else
            {
                buffer.WaitForData(5);
            }
        }

        Assert.That(producer.Join(TimeSpan.FromSeconds(5)), Is.True, "the producer that requested the swap should have been released by it");
        Assert.That(producerFailure, Is.Null, $"producer failed: {producerFailure}");

        // Leave the fresh buffer drained so the next call starts from a clean position.
        while (buffer.TryDrain(out var rest, out _))
        {
            buffer.CompleteDrain(rest.Length);
        }
    }

    #endregion
}
