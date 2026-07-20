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
}
