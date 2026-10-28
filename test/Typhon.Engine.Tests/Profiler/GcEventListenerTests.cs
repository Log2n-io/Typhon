using System;
using System.Diagnostics;
using System.Threading;
using NUnit.Framework;
using Typhon.Profiler;

namespace Typhon.Engine.Tests.Profiler;

/// <summary>
/// Tests for <see cref="GcEventListener.IsGcSuspendReason"/> — the filter that keeps the GC trace from being flooded by
/// non-GC execution-engine suspensions. <c>GCSuspendEEBegin</c> fires for every EE suspension; the EventPipe CPU sample
/// profiler (#351) suspends the EE ~1000×/s with <see cref="GcSuspendReason.Other"/> to walk stacks, so without this
/// filter every sampled session records ~1000 phantom "GC" events per second.
/// </summary>
[TestFixture]
public class GcEventListenerTests
{
    [TestCase(GcSuspendReason.ForGC, ExpectedResult = true)]
    [TestCase(GcSuspendReason.ForGCPrep, ExpectedResult = true)]
    [TestCase(GcSuspendReason.Other, ExpectedResult = false)]
    [TestCase(GcSuspendReason.ForAppDomainShutdown, ExpectedResult = false)]
    [TestCase(GcSuspendReason.ForCodePitching, ExpectedResult = false)]
    [TestCase(GcSuspendReason.ForShutdown, ExpectedResult = false)]
    [TestCase(GcSuspendReason.ForDebugger, ExpectedResult = false)]
    [TestCase(GcSuspendReason.ForDebuggerSweep, ExpectedResult = false)]
    public bool IsGcSuspendReason_keeps_only_gc_suspensions(GcSuspendReason reason)
        => GcEventListener.IsGcSuspendReason((byte)reason);

    [Test]
    public void IsGcSuspendReason_rejects_unknown_reason_values()
    {
        // Defensive: a reason value outside the known enum (a future runtime adding a code) is not a GC suspension.
        Assert.That(GcEventListener.IsGcSuspendReason(99), Is.False);
        Assert.That(GcEventListener.IsGcSuspendReason(byte.MaxValue), Is.False);
    }

    /// <summary>
    /// The reason is the field NAMED Reason: <c>GCSuspendEEBegin_V1</c> is <c>Reason, Count, ClrInstanceID</c> on .NET 10. From #243 the listener read
    /// position 1 — the GC count — so since #351's filter no GC suspension reached a trace; a count of 175 must not read as a reason of 175.
    /// </summary>
    [Test]
    public void SuspendReasonOf_reads_the_field_named_Reason_not_the_count()
    {
        string[] names = ["Reason", "Count", "ClrInstanceID"];
        Assert.Multiple(() =>
        {
            Assert.That(GcEventListener.SuspendReasonOf(names, [1u, 175u, (ushort)10]), Is.EqualTo((byte)GcSuspendReason.ForGC));
            Assert.That(GcEventListener.SuspendReasonOf(names, [0u, 6u, (ushort)10]), Is.EqualTo((byte)GcSuspendReason.Other),
                "a CPU sample's suspension (Other) with a count of 6 must not read as ForGCPrep");
            Assert.That(GcEventListener.SuspendReasonOf(["Count", "Reason"], [175u, 6u]), Is.EqualTo((byte)GcSuspendReason.ForGCPrep),
                "a schema that moves the field is followed by name");
            Assert.That(GcEventListener.SuspendReasonOf(null, [1u, 175u]), Is.EqualTo((byte)GcSuspendReason.ForGC), "no names: position 0");
            Assert.That(GcEventListener.SuspendReasonOf(["Count"], [175u]), Is.EqualTo((byte)0), "no Reason field: none");
            Assert.That(GcEventListener.SuspendReasonOf(names, []), Is.EqualTo((byte)0));
        });
    }

    [Test]
    public void ToStopwatchTicks_maps_the_wall_clock_onto_the_stopwatch_through_the_anchor()
    {
        var anchor = new DateTime(2026, 10, 27, 12, 0, 0, DateTimeKind.Utc);
        const long sw = 5_000_000;
        const long freq = 1_000_000_000; // 1 ns ticks: 100 per DateTime tick
        var local = anchor.AddMilliseconds(3).ToLocalTime();
        Assert.Multiple(() =>
        {
            Assert.That(GcEventListener.ToStopwatchTicks(anchor, anchor.Ticks, sw, freq), Is.EqualTo(sw));
            Assert.That(GcEventListener.ToStopwatchTicks(anchor.AddMilliseconds(7), anchor.Ticks, sw, freq), Is.EqualTo(sw + 7_000_000));
            Assert.That(GcEventListener.ToStopwatchTicks(anchor.AddMilliseconds(-2), anchor.Ticks, sw, freq), Is.EqualTo(sw - 2_000_000),
                "an event older than the anchor lands before it");
            Assert.That(GcEventListener.ToStopwatchTicks(local, anchor.Ticks, sw, freq), Is.EqualTo(sw + 3_000_000), "a Local stamp is read as UTC");
        });
    }

    /// <summary>
    /// The pause is stamped when the runtime raised it, not when the listener's dispatch thread got to it. That thread is suspended with every other
    /// managed thread, so it used to stamp SuspendEEBegin and RestartEEEnd back to back after the restart, often after <c>GC.Collect</c> had returned:
    /// every pause measured ~0.03 ms. Bracketed by the collection call, every record must fall inside it, in order.
    /// </summary>
    [Test]
    [CancelAfter(15_000)]
    public void A_forced_collection_is_stamped_inside_the_collection_call_in_order()
    {
        using var wake = new AutoResetEvent(false);
        var queue = new GcEventQueue(4096, wake);
        using var listener = new GcEventListener(queue);

        var deadline = Stopwatch.GetTimestamp() + Stopwatch.Frequency * 10;
        while (Stopwatch.GetTimestamp() < deadline)
        {
            while (queue.TryDequeue(out _))
            {
            }

            var t0 = Stopwatch.GetTimestamp();
            GC.Collect(2, GCCollectionMode.Forced, blocking: true);
            var t1 = Stopwatch.GetTimestamp();
            Thread.Sleep(100);

            long suspend = 0, start = 0, end = 0, restart = 0;
            while (queue.TryDequeue(out var r))
            {
                switch (r.Kind)
                {
                    case GcEventRecordKind.SuspendBegin when suspend == 0 && r.Reason == (byte)GcSuspendReason.ForGC: suspend = r.Timestamp; break;
                    case GcEventRecordKind.GcStart when suspend != 0 && start == 0: start = r.Timestamp; break;
                    case GcEventRecordKind.GcEnd when start != 0 && end == 0: end = r.Timestamp; break;
                    case GcEventRecordKind.RestartEnd when end != 0 && restart == 0: restart = r.Timestamp; break;
                }
            }

            if (restart == 0)
            {
                continue; // a GC raced in from elsewhere or the events are late: try again
            }

            // 1 ms slack on each side: the wall clock behind TimeStamp and the Stopwatch are different counters.
            var slack = Stopwatch.Frequency / 1000;
            Assert.Multiple(() =>
            {
                Assert.That(suspend, Is.GreaterThanOrEqualTo(t0 - slack), "suspend before the call");
                Assert.That(start, Is.GreaterThanOrEqualTo(suspend));
                Assert.That(end, Is.GreaterThanOrEqualTo(start));
                Assert.That(restart, Is.GreaterThanOrEqualTo(end));
                Assert.That(restart, Is.LessThanOrEqualTo(t1 + slack), "restart stamped after GC.Collect returned: the dispatch time, not the event's");
            });
            return;
        }

        Assert.Fail("no complete suspend/start/end/restart sequence reached the queue in 10 s");
    }

    /// <summary>
    /// End to end against this runtime's own events: a forced collection reaches the queue as a GC suspension (begin with reason ForGC, then its
    /// restart) — the records the Workbench draws GC pauses from, which no trace carried between #351 and this fix.
    /// </summary>
    [Test]
    [CancelAfter(15_000)]
    public void A_forced_collection_is_recorded_as_a_gc_suspension()
    {
        using var wake = new AutoResetEvent(false);
        var queue = new GcEventQueue(4096, wake);
        using var listener = new GcEventListener(queue);

        var suspendedForGc = false;
        var restarted = false;
        var deadline = Stopwatch.GetTimestamp() + Stopwatch.Frequency * 10;
        while (!(suspendedForGc && restarted) && Stopwatch.GetTimestamp() < deadline)
        {
            GC.Collect(0, GCCollectionMode.Forced, blocking: true);
            Thread.Sleep(50);
            while (queue.TryDequeue(out var record))
            {
                suspendedForGc |= record.Kind == GcEventRecordKind.SuspendBegin && record.Reason == (byte)GcSuspendReason.ForGC;
                restarted |= suspendedForGc && record.Kind == GcEventRecordKind.RestartEnd;
            }
        }

        Assert.Multiple(() =>
        {
            Assert.That(suspendedForGc, Is.True, "no suspend-begin with reason ForGC reached the queue");
            Assert.That(restarted, Is.True, "no restart followed the GC suspension");
        });
    }
}
