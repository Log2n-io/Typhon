using NUnit.Framework;

namespace Typhon.Engine.Tests.Profiler;

/// <summary>
/// Unit tests for the pure prune-decision helper of <see cref="EtwSchedulingPump"/> (bug D — <c>_threadStates</c> grew unbounded because closed-slice entries
/// were only zeroed, never removed). The ETW pump itself is not directly unit-testable (needs a live NT Kernel Logger session), so only the extracted
/// <see cref="EtwSchedulingPump.IsStaleEntry"/> predicate is covered here — the periodic sweep wiring inside the pump callback is exercised only in practice.
/// </summary>
[TestFixture]
class EtwSchedulingPumpTests
{
    [Test]
    public void IsStaleEntry_ClosedSlice_IsStale()
    {
        // A closed slice leaves StartTick at the -1 sentinel — that entry belongs to a thread not currently ON-CPU and is a prune candidate.
        var closed = new EtwSchedulingPump.ThreadSliceState { StartTick = -1, ReadyTick = 0, ProcessorNumber = 0 };
        Assert.That(EtwSchedulingPump.IsStaleEntry(closed), Is.True);
    }

    [Test]
    public void IsStaleEntry_OpenSlice_IsNotStale()
    {
        // An open slice has a real QPC StartTick — the thread is ON-CPU, the entry must be kept.
        var open = new EtwSchedulingPump.ThreadSliceState { StartTick = 123_456_789, ReadyTick = 100, ProcessorNumber = 3 };
        Assert.That(EtwSchedulingPump.IsStaleEntry(open), Is.False);
    }

    [Test]
    public void IsStaleEntry_ClosedSlice_StaleEvenWithReadyTickSet()
    {
        // ReadyTick may carry over from a DispatcherReadyThread event; only StartTick decides staleness.
        var closedWithReady = new EtwSchedulingPump.ThreadSliceState { StartTick = -1, ReadyTick = 999, ProcessorNumber = 0 };
        Assert.That(EtwSchedulingPump.IsStaleEntry(closedWithReady), Is.True);
    }

    [Test]
    public void IsStaleEntry_ZeroStartTick_IsNotStale()
    {
        // StartTick == 0 is a degenerate-but-open entry (default struct before any callback); only the -1 sentinel marks a closed slice.
        var zero = new EtwSchedulingPump.ThreadSliceState { StartTick = 0, ReadyTick = 0, ProcessorNumber = 0 };
        Assert.That(EtwSchedulingPump.IsStaleEntry(zero), Is.False);
    }
}
