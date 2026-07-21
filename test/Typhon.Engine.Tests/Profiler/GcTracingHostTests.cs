using System;
using System.Diagnostics;
using System.Threading;
using NUnit.Framework;
using Typhon.Profiler;

namespace Typhon.Engine.Tests.Profiler;

/// <summary>
/// Lifecycle and in-process drain tests for <see cref="GcTracingHost"/>. Does NOT require a real GC or the
/// <see cref="System.Diagnostics.Tracing.EventListener"/> subscription to fire — records are injected directly into the queue via the
/// <c>QueueForTests</c> internal accessor, exercising the ingestion-thread → <c>TyphonEvent</c> emit path end-to-end.
/// </summary>
/// <remarks>
/// The emit helpers check <see cref="TelemetryConfig.ProfilerActive"/> internally; in tests this is typically <c>false</c>, so the ring
/// buffer is not written. That's fine — we verify the ingestion thread's <i>processing</i> of records (ProcessedEvents counter), not the
/// downstream byte emission which is already covered by the codec round-trip tests.
/// </remarks>
[TestFixture]
[NonParallelizable]
public class GcTracingHostTests
{
    [SetUp]
    public void Setup()
    {
        // The ingestion thread claims its own slot (post the SPSC-fix) — over many tests these accumulate because the
        // consumer thread isn't running to transition Retiring → Free. Reset before each test so GetOrAssignSlot has
        // a known, non-exhausted registry.
        ThreadSlotRegistry.ResetForTests();
    }

    [Test]
    public void Start_assigns_slot_and_Stop_is_idempotent()
    {
        using var host = new GcTracingHost();
        host.Start();
        Assert.That(host.Slot, Is.LessThan(ThreadSlotRegistry.MaxSlots));

        host.Stop();
        host.Stop(); // idempotent
        host.Stop();
    }

    [Test]
    public void Double_Start_throws()
    {
        using var host = new GcTracingHost();
        host.Start();
        Assert.Throws<InvalidOperationException>(host.Start);
    }

    [Test]
    public void Ingestion_thread_drains_queued_records()
    {
        using var host = new GcTracingHost();
        host.Start();
        var ts = Stopwatch.GetTimestamp();

        // Inject a realistic pattern: suspend → start → end → restart. Verify all four get processed.
        host.QueueForTests.TryEnqueue(GcEventRecord.ForSuspendBegin(ts, (byte)GcSuspendReason.ForGC));
        host.QueueForTests.TryEnqueue(GcEventRecord.ForGcStart(ts + 1, gen: 0, reason: 0, type: 0, count: 1));
        host.QueueForTests.TryEnqueue(GcEventRecord.ForGcEnd(ts + 2, gen: 0, count: 1));
        host.QueueForTests.TryEnqueue(GcEventRecord.ForRestartEnd(ts + 3));

        // Give the ingestion thread up to 500 ms to drain. In practice it wakes within microseconds.
        var deadline = DateTime.UtcNow.AddMilliseconds(500);
        while (host.ProcessedEvents < 4 && DateTime.UtcNow < deadline)
        {
            Thread.Sleep(5);
        }

        Assert.That(host.ProcessedEvents, Is.EqualTo(4L));
        Assert.That(host.DroppedEvents, Is.EqualTo(0L));
    }

    [Test]
    public void Dispose_without_Start_does_not_throw()
    {
        var host = new GcTracingHost();
        Assert.DoesNotThrow(() => host.Dispose());
    }
}
