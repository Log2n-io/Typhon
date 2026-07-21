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
}
