using System;
using System.Threading;
using NUnit.Framework;
using Typhon.Engine.Profiler.Gc;

namespace Typhon.Engine.Tests.Profiler;

/// <summary>
/// Bounded MPSC ring-buffer semantics for <see cref="GcEventQueue"/>: enqueue/dequeue order, capacity wrap, drop-newest overflow policy,
/// wake-event signaling.
/// </summary>
[TestFixture]
public class GcEventQueueTests
{
    [Test]
    public void Single_enqueue_is_dequeued_with_same_payload()
    {
        using var wake = new AutoResetEvent(false);
        var queue = new GcEventQueue(capacity: 4, wake);

        Assert.That(queue.TryEnqueue(GcEventRecord.ForGcStart(ts: 100L, gen: 2, reason: 4, type: 1, count: 42u)), Is.True);
        Assert.That(wake.WaitOne(0), Is.True, "wake event should have been signaled");

        Assert.That(queue.TryDequeue(out var record), Is.True);
        Assert.Multiple(() =>
        {
            Assert.That(record.Kind, Is.EqualTo(GcEventRecordKind.GcStart));
            Assert.That(record.Timestamp, Is.EqualTo(100L));
            Assert.That(record.Generation, Is.EqualTo((byte)2));
            Assert.That(record.Reason, Is.EqualTo((byte)4));
            Assert.That(record.Type, Is.EqualTo((byte)1));
            Assert.That(record.Count, Is.EqualTo(42u));
        });

        Assert.That(queue.TryDequeue(out _), Is.False, "queue should be empty after single dequeue");
    }

    [Test]
    public void Fifo_order_is_preserved()
    {
        using var wake = new AutoResetEvent(false);
        var queue = new GcEventQueue(capacity: 8, wake);

        for (var i = 0; i < 5; i++)
        {
            queue.TryEnqueue(GcEventRecord.ForGcStart(ts: i, gen: (byte)i, reason: 0, type: 0, count: (uint)i));
        }

        for (var i = 0; i < 5; i++)
        {
            Assert.That(queue.TryDequeue(out var record), Is.True);
            Assert.That(record.Timestamp, Is.EqualTo((long)i));
            Assert.That(record.Count, Is.EqualTo((uint)i));
        }
    }

    [Test]
    public void Drop_newest_on_overflow_and_bumps_counter()
    {
        using var wake = new AutoResetEvent(false);
        var queue = new GcEventQueue(capacity: 2, wake);

        Assert.That(queue.TryEnqueue(GcEventRecord.ForGcStart(1L, 0, 0, 0, 0)), Is.True);
        Assert.That(queue.TryEnqueue(GcEventRecord.ForGcStart(2L, 0, 0, 0, 0)), Is.True);
        Assert.That(queue.TryEnqueue(GcEventRecord.ForGcStart(3L, 0, 0, 0, 0)), Is.False, "third enqueue should drop");
        Assert.That(queue.DroppedEvents, Is.EqualTo(1L));

        // Verify the two accepted records are still intact (not clobbered by the dropped one).
        Assert.That(queue.TryDequeue(out var r1), Is.True);
        Assert.That(r1.Timestamp, Is.EqualTo(1L));
        Assert.That(queue.TryDequeue(out var r2), Is.True);
        Assert.That(r2.Timestamp, Is.EqualTo(2L));
    }

    [Test]
    public void Wrap_around_after_many_operations()
    {
        using var wake = new AutoResetEvent(false);
        var queue = new GcEventQueue(capacity: 4, wake);

        // Push/pop through the ring several times to ensure head/tail wrap works.
        for (var round = 0; round < 5; round++)
        {
            for (var i = 0; i < 3; i++)
            {
                Assert.That(queue.TryEnqueue(GcEventRecord.ForGcStart(round * 100 + i, 0, 0, 0, 0)), Is.True);
            }
            for (var i = 0; i < 3; i++)
            {
                Assert.That(queue.TryDequeue(out var record), Is.True);
                Assert.That(record.Timestamp, Is.EqualTo(round * 100 + i));
            }
        }
    }

    [Test]
    public void Capacity_must_be_power_of_two_and_at_least_two()
    {
        using var wake = new AutoResetEvent(false);
        Assert.Throws<ArgumentException>(() => new GcEventQueue(0, wake));
        Assert.Throws<ArgumentException>(() => new GcEventQueue(1, wake));
        Assert.Throws<ArgumentException>(() => new GcEventQueue(3, wake));
        Assert.Throws<ArgumentException>(() => new GcEventQueue(6, wake));
        // Valid sizes:
        Assert.DoesNotThrow(() => new GcEventQueue(2, wake));
        Assert.DoesNotThrow(() => new GcEventQueue(1024, wake));
    }
}
