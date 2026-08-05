using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;

namespace Typhon.Engine.Tests;

[TestFixture]
public class ViewDeltaRingBufferTests
{
    // #660: the buffer is now typed on EntityId. These keep the synthetic ids in these mechanics tests readable.
    private static EntityId E(long raw) => EntityId.FromRaw(raw);
    private static long PK(ViewDeltaEntry e) => (long)e.EntityPK.RawValue;

    private ServiceProvider _sp;
    private IMemoryAllocator _allocator;
    private IResource _parent;

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        var sc = new ServiceCollection();
        sc.AddResourceRegistry()
          .AddMemoryAllocator();
        _sp = sc.BuildServiceProvider();
        _allocator = _sp.GetRequiredService<IMemoryAllocator>();
        _parent = _sp.GetRequiredService<IResourceRegistry>().Allocation;
    }

    [OneTimeTearDown]
    public void OneTimeTearDown()
    {
        _sp?.Dispose();
    }

    private ViewDeltaRingBuffer CreateBuffer(int capacity = ViewDeltaRingBuffer.DefaultCapacity, long baseTSN = 0)
        => new(_allocator, _parent, capacity, baseTSN);

    // ========================================
    // Constructor
    // ========================================

    [Test]
    public void Constructor_DefaultCapacity_Is4096()
    {
        using var buffer = CreateBuffer();
        Assert.That(buffer.Capacity, Is.EqualTo(ViewDeltaRingBuffer.DefaultCapacity));
        Assert.That(buffer.Capacity, Is.EqualTo(4096));
    }

    [Test]
    public void Constructor_CustomCapacity_Accepted()
    {
        using var buffer = CreateBuffer(256);
        Assert.That(buffer.Capacity, Is.EqualTo(256));
    }

    [Test]
    public void Constructor_NonPowerOfTwo_Throws()
    {
        Assert.Throws<ArgumentException>(() => CreateBuffer(100));
    }

    [Test]
    public void Constructor_Zero_Throws()
    {
        Assert.Throws<ArgumentException>(() => CreateBuffer(0));
    }

    [Test]
    public void Constructor_Negative_Throws()
    {
        Assert.Throws<ArgumentException>(() => CreateBuffer(-4));
    }

    [Test]
    public void Constructor_BaseTSN_Stored()
    {
        using var buffer = CreateBuffer(64, baseTSN: 1000);
        Assert.That(buffer.BaseTSN, Is.EqualTo(1000));
    }

    // ========================================
    // Basic Append / Peek / Advance
    // ========================================

    [Test]
    public void AppendPeekAdvance_SingleEntry_Roundtrip()
    {
        using var buffer = CreateBuffer(64, baseTSN: 100);

        var before = KeyBytes8.FromInt(42);
        var after = KeyBytes8.FromInt(99);
        Assert.That(buffer.TryAppend(E(1L), before, after, 105, 0x01), Is.True);
        Assert.That(buffer.Count, Is.EqualTo(1));

        Assert.That(buffer.TryPeek(200, out var entry, out var flags, out var tsn, out var componentTag), Is.True);
        Assert.That(PK(entry), Is.EqualTo(1L));
        Assert.That(entry.BeforeKey.AsInt(), Is.EqualTo(42));
        Assert.That(entry.AfterKey.AsInt(), Is.EqualTo(99));
        Assert.That(flags, Is.EqualTo(0x01));
        Assert.That(tsn, Is.EqualTo(105));
        Assert.That(componentTag, Is.EqualTo(0), "Default componentTag should be 0");

        buffer.Advance();
        Assert.That(buffer.Count, Is.EqualTo(0));
    }

    [Test]
    public void TryPeek_EmptyBuffer_ReturnsFalse()
    {
        using var buffer = CreateBuffer(64);
        Assert.That(buffer.TryPeek(long.MaxValue, out _, out _, out _, out _), Is.False);
    }

    [Test]
    public void ComponentTag_RoundTrips()
    {
        using var buffer = CreateBuffer(64);

        Assert.That(buffer.TryAppend(E(1L), default, default, 1, 0x01, componentTag: 0), Is.True);
        Assert.That(buffer.TryAppend(E(2L), default, default, 2, 0x02, componentTag: 1), Is.True);
        Assert.That(buffer.TryAppend(E(3L), default, default, 3, 0x03, componentTag: 255), Is.True);

        Assert.That(buffer.TryPeek(long.MaxValue, out var e1, out _, out _, out var tag1), Is.True);
        Assert.That(PK(e1), Is.EqualTo(1L));
        Assert.That(tag1, Is.EqualTo(0));
        buffer.Advance();

        Assert.That(buffer.TryPeek(long.MaxValue, out var e2, out _, out _, out var tag2), Is.True);
        Assert.That(PK(e2), Is.EqualTo(2L));
        Assert.That(tag2, Is.EqualTo(1));
        buffer.Advance();

        Assert.That(buffer.TryPeek(long.MaxValue, out var e3, out _, out _, out var tag3), Is.True);
        Assert.That(PK(e3), Is.EqualTo(3L));
        Assert.That(tag3, Is.EqualTo(255));
        buffer.Advance();
    }

    // ========================================
    // Sequential batch
    // ========================================

    // ========================================
    // DeltaTSN compression
    // ========================================

    [Test]
    public void DeltaTSN_StoredAsOffsetFromBaseTSN()
    {
        using var buffer = CreateBuffer(64, baseTSN: 1_000_000);

        buffer.TryAppend(E(1), default, default, 1_000_042, 0);
        Assert.That(buffer.TryPeek(long.MaxValue, out _, out _, out var tsn, out _), Is.True);
        Assert.That(tsn, Is.EqualTo(1_000_042));
        buffer.Advance();
    }

    // ========================================
    // Wrap-around
    // ========================================

    // ========================================
    // Full buffer / overflow
    // ========================================

    [Test]
    public void FullBuffer_TryAppend_ReturnsFalse_OverflowSticky()
    {
        const int capacity = 16;
        using var buffer = CreateBuffer(capacity);

        for (var i = 0; i < capacity; i++)
        {
            Assert.That(buffer.TryAppend(E(i), default, default, i, 0), Is.True);
        }

        Assert.That(buffer.HasOverflow, Is.False);
        Assert.That(buffer.TryAppend(E(999), default, default, 999, 0), Is.False);
        Assert.That(buffer.HasOverflow, Is.True);

        // Overflow is sticky — consuming entries doesn't clear it
        buffer.TryPeek(long.MaxValue, out _, out _, out _, out _);
        buffer.Advance();
        Assert.That(buffer.HasOverflow, Is.True);
    }

    // ========================================
    // Reset
    // ========================================

    [Test]
    public void Reset_ClearsEverything()
    {
        const int capacity = 64;
        using var buffer = CreateBuffer(capacity);

        for (var i = 0; i < 10; i++)
        {
            buffer.TryAppend(E(i), default, default, i, 0);
        }

        // Force overflow
        for (var i = 0; i < capacity; i++)
        {
            buffer.TryAppend(E(i), default, default, i, 0);
        }

        buffer.Reset();
        Assert.That(buffer.Count, Is.EqualTo(0));
        Assert.That(buffer.HasOverflow, Is.False);
        Assert.That(buffer.TryPeek(long.MaxValue, out _, out _, out _, out _), Is.False);
    }

    // ========================================
    // Dispose
    // ========================================

    [Test]
    public void Dispose_SetsDisposedFlag()
    {
        var buffer = CreateBuffer(64);
        Assert.That(buffer.IsDisposed, Is.False);
        buffer.Dispose();
        Assert.That(buffer.IsDisposed, Is.True);
    }

    [Test]
    public void Dispose_DoubleDispose_NoThrow()
    {
        var buffer = CreateBuffer(64);
        buffer.Dispose();
        Assert.DoesNotThrow(() => buffer.Dispose());
    }

    // ========================================
    // TSN filtering
    // ========================================

    [Test]
    public void TryPeek_TSNBeyondTarget_ReturnsFalse()
    {
        using var buffer = CreateBuffer(64, baseTSN: 100);

        buffer.TryAppend(E(1), default, default, 110, 0);
        buffer.TryAppend(E(2), default, default, 120, 0);
        buffer.TryAppend(E(3), default, default, 130, 0);

        // Target = 115: should see entry with TSN 110 but not 120 or 130
        Assert.That(buffer.TryPeek(115, out var entry, out _, out _, out _), Is.True);
        Assert.That(PK(entry), Is.EqualTo(1));
        buffer.Advance();

        // Now head is at TSN 120, which is > 115
        Assert.That(buffer.TryPeek(115, out _, out _, out _, out _), Is.False);

        // But with higher target, we can see it
        Assert.That(buffer.TryPeek(125, out var entry2, out _, out _, out _), Is.True);
        Assert.That(PK(entry2), Is.EqualTo(2));
    }

    // ========================================
    // Properties
    // ========================================

    [Test]
    public void Count_TracksAppendAndAdvance()
    {
        using var buffer = CreateBuffer(64);

        Assert.That(buffer.Count, Is.EqualTo(0));

        buffer.TryAppend(E(1), default, default, 0, 0);
        Assert.That(buffer.Count, Is.EqualTo(1));

        buffer.TryAppend(E(2), default, default, 1, 0);
        Assert.That(buffer.Count, Is.EqualTo(2));

        buffer.TryPeek(long.MaxValue, out _, out _, out _, out _);
        buffer.Advance();
        Assert.That(buffer.Count, Is.EqualTo(1));
    }

    // ========================================
    // Concurrent Tests
    // ========================================

    [Test]
    [CancelAfter(5000)]
    public void Concurrent_4Producers_1Consumer_AllEntriesReceived()
    {
        const int entriesPerProducer = 1000;
        const int producerCount = 4;
        const int totalEntries = producerCount * entriesPerProducer;
        using var buffer = CreateBuffer(8192);

        var received = new ConcurrentBag<long>();
        var producersDone = new CountdownEvent(producerCount);
        var startBarrier = new ManualResetEventSlim(false);

        // Consumer task
        var consumerTask = Task.Run(() =>
        {
            var consumed = 0;
            while (consumed < totalEntries)
            {
                if (buffer.TryPeek(long.MaxValue, out var entry, out _, out _, out _))
                {
                    received.Add(PK(entry));
                    buffer.Advance();
                    consumed++;
                }
                else if (producersDone.IsSet)
                {
                    // All producers done — try one more time
                    if (!buffer.TryPeek(long.MaxValue, out entry, out _, out _, out _))
                    {
                        break;
                    }

                    received.Add(PK(entry));
                    buffer.Advance();
                    consumed++;
                }
                else
                {
                    Thread.SpinWait(10);
                }
            }
        });

        // Producer tasks
        var producers = new Task[producerCount];
        for (var p = 0; p < producerCount; p++)
        {
            var producerId = p;
            producers[p] = Task.Run(() =>
            {
                startBarrier.Wait();
                for (var i = 0; i < entriesPerProducer; i++)
                {
                    var entityPK = producerId * entriesPerProducer + i;
                    while (!buffer.TryAppend(E(entityPK), default, default, entityPK, 0))
                    {
                        Thread.SpinWait(10);
                    }
                }
                producersDone.Signal();
            });
        }

        startBarrier.Set();
        Task.WaitAll(producers);
        consumerTask.Wait();

        Assert.That(received.Count, Is.EqualTo(totalEntries));

        // Verify all entity PKs received (no duplicates, no loss)
        var sorted = received.ToArray();
        Array.Sort(sorted);
        for (var i = 0; i < totalEntries; i++)
        {
            Assert.That(sorted[i], Is.EqualTo(i));
        }
    }

    [Test]
    [CancelAfter(5000)]
    public void Concurrent_SmallBuffer_OverflowDetected_NoCrash()
    {
        const int capacity = 64;
        const int producerCount = 4;
        const int entriesPerProducer = 500;
        using var buffer = CreateBuffer(capacity);

        var startBarrier = new ManualResetEventSlim(false);
        var appendedCount = 0;

        var producers = new Task[producerCount];
        for (var p = 0; p < producerCount; p++)
        {
            var producerId = p;
            producers[p] = Task.Run(() =>
            {
                startBarrier.Wait();
                for (var i = 0; i < entriesPerProducer; i++)
                {
                    if (buffer.TryAppend(E(producerId * entriesPerProducer + i), default, default, i, 0))
                    {
                        Interlocked.Increment(ref appendedCount);
                    }
                }
            });
        }

        startBarrier.Set();
        Task.WaitAll(producers);

        // Some entries should have been rejected
        Assert.That(appendedCount, Is.LessThan(producerCount * entriesPerProducer));
        Assert.That(buffer.HasOverflow, Is.True);

        // Consume all appended entries without crash
        var consumed = 0;
        while (buffer.TryPeek(long.MaxValue, out _, out _, out _, out _))
        {
            buffer.Advance();
            consumed++;
        }

        Assert.That(consumed, Is.EqualTo(appendedCount));
    }

    [Test]
    [CancelAfter(5000)]
    public void Concurrent_SingleProducer_OrderPreserved()
    {
        const int entryCount = 2000;
        using var buffer = CreateBuffer(4096);

        var consumerDone = new ManualResetEventSlim(false);
        var orderViolation = false;

        var consumer = Task.Run(() =>
        {
            var lastPK = -1L;
            var consumed = 0;
            while (consumed < entryCount)
            {
                if (buffer.TryPeek(long.MaxValue, out var entry, out _, out _, out _))
                {
                    if (PK(entry) <= lastPK)
                    {
                        orderViolation = true;
                    }
                    lastPK = PK(entry);
                    buffer.Advance();
                    consumed++;
                }
                else
                {
                    Thread.SpinWait(10);
                }
            }
            consumerDone.Set();
        });

        // Single producer
        for (var i = 0; i < entryCount; i++)
        {
            while (!buffer.TryAppend(E(i), default, default, i, 0))
            {
                Thread.SpinWait(10);
            }
        }

        consumerDone.Wait();
        consumer.Wait();

        Assert.That(orderViolation, Is.False);
    }
}
