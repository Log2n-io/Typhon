using NUnit.Framework;
using System;
using System.Diagnostics;
using System.Threading;

namespace Typhon.Engine.Tests;

/// <summary>
/// Tests for <see cref="HighResolutionTimerService"/> (single handler, dedicated thread).
/// Covers: callback invocation, timing accuracy, missed tick detection, exception handling.
/// </summary>
[TestFixture]
public class HighResolutionTimerServiceTests
{
    private ResourceRegistry _registry;

    [SetUp]
    public void Setup()
    {
        _registry = new ResourceRegistry(new ResourceRegistryOptions { Name = "SingleTimerTest" });
    }

    [TearDown]
    public void TearDown()
    {
        _registry.Dispose();
    }

    [Test]
    [Category("Timing")]
    public void Single_FiresAtExpectedRate()
    {
        var count = 0L;
        var intervalTicks = Stopwatch.Frequency / 200; // 5ms interval

        using var timer = new HighResolutionTimerService(
            "RateTest",
            intervalTicks,
            (_, _) => Interlocked.Increment(ref count),
            _registry.TimerDedicated);

        timer.Start();

        // The observation window is MEASURED, never assumed. `Thread.Sleep(100)` is a lower bound, not a duration:
        // on a loaded or oversubscribed CI box it routinely overshoots, and against a hard-coded count that surfaces
        // as "too many invocations" — a failure mode indistinguishable, from the count alone, from a timer that is
        // genuinely running fast. Measured on a 3-core macOS runner: 44 invocations, i.e. a ~220 ms window.
        var start = Stopwatch.GetTimestamp();
        Thread.Sleep(100);

        // Stop the timer before reading counters to avoid a race where the
        // timer fires between reading `count` and `InvocationCount`.
        timer.Dispose();
        var elapsedTicks = Stopwatch.GetTimestamp() - start;

        var invocations = Interlocked.Read(ref count);
        var expected = (double)elapsedTicks / intervalTicks;
        var elapsedMs = elapsedTicks * 1000.0 / Stopwatch.Frequency;

        // The upper bound is the real invariant here, and it is TIGHT: a metronome cannot fire more often than the
        // elapsed window allows. ExecuteCallbacks advances _nextTick by exactly one interval and, when it has fallen
        // behind, skips forward to within one interval of now — so catch-up can never burst. The +2 covers the two
        // window boundaries (a tick in flight at Start, and one during Dispose's thread join).
        Assert.That(invocations, Is.LessThanOrEqualTo((long)expected + 2),
            $"Fired {invocations}x in {elapsedMs:F1}ms at a 5ms interval; a metronome cannot exceed {expected:F1}.");

        // The lower bound stays deliberately loose. A starved timer thread legitimately misses ticks — that is a
        // property of the machine, not a defect in the timer.
        Assert.That(invocations, Is.GreaterThanOrEqualTo((long)(expected * 0.5)),
            $"Fired only {invocations}x in {elapsedMs:F1}ms at a 5ms interval; expected at least half of {expected:F1}.");

        Assert.That(timer.InvocationCount, Is.EqualTo(invocations));
    }

    [Test]
    public void Callback_ReceivesTimestamps()
    {
        long receivedScheduled = 0;
        long receivedActual = 0;
        using var ready = new ManualResetEventSlim(false);

        using var timer = new HighResolutionTimerService(
            "TimestampTest",
            Stopwatch.Frequency / 100, // 10ms
            (scheduled, actual) =>
            {
                Interlocked.Exchange(ref receivedScheduled, scheduled);
                Interlocked.Exchange(ref receivedActual, actual);
                ready.Set();
            },
            _registry.TimerDedicated);

        timer.Start();
        Assert.That(ready.Wait(2000), Is.True, "Callback did not fire within 2s");

        Assert.That(Interlocked.Read(ref receivedScheduled), Is.GreaterThan(0), "Scheduled timestamp not received");
        Assert.That(Interlocked.Read(ref receivedActual), Is.GreaterThan(0), "Actual timestamp not received");
        Assert.That(Interlocked.Read(ref receivedActual), Is.GreaterThanOrEqualTo(Interlocked.Read(ref receivedScheduled)),
            "Actual should be >= scheduled");
    }

    [Test]
    public void Callback_ExceptionDoesNotKillTimer()
    {
        var callCount = 0L;

        using var timer = new HighResolutionTimerService(
            "ExceptionTest",
            Stopwatch.Frequency / 100, // 10ms
            (_, _) =>
            {
                Interlocked.Increment(ref callCount);
                throw new InvalidOperationException("Test exception");
            },
            _registry.TimerDedicated);

        timer.Start();
        // Wait until callback has fired multiple times (surviving exceptions each time)
        SpinWait.SpinUntil(() => Interlocked.Read(ref callCount) > 1, 2000);

        // Timer should still be running and have invoked callback multiple times
        Assert.That(timer.IsRunning, Is.True);
        Assert.That(Interlocked.Read(ref callCount), Is.GreaterThan(1), "Timer should continue after exceptions");
    }

    [Test]
    public void Properties_ReflectConfiguration()
    {
        var intervalTicks = Stopwatch.Frequency / 1000; // 1ms

        using var timer = new HighResolutionTimerService(
            "PropsTest",
            intervalTicks,
            (_, _) => { },
            _registry.TimerDedicated);

        Assert.That(timer.Name, Is.EqualTo("PropsTest"));
        Assert.That(timer.IntervalTicks, Is.EqualTo(intervalTicks));

        // Interval should be approximately 1ms
        Assert.That(timer.Interval.TotalMilliseconds, Is.EqualTo(1.0).Within(0.1));
    }

    [Test]
    [Category("Timing")]
    public void CallbackDuration_Tracked()
    {
        using var timer = new HighResolutionTimerService(
            "DurationTest",
            Stopwatch.Frequency / 100, // 10ms
            (_, _) => Thread.SpinWait(1000), // Burn a small amount of time
            _registry.TimerDedicated);

        timer.Start();
        SpinWait.SpinUntil(() => timer.InvocationCount > 0, 2000);

        Assert.That(timer.InvocationCount, Is.GreaterThan(0));
        // LastCallbackDuration should be non-negative
        Assert.That(timer.LastCallbackDuration, Is.GreaterThanOrEqualTo(TimeSpan.Zero));
        Assert.That(timer.MaxCallbackDuration, Is.GreaterThanOrEqualTo(timer.LastCallbackDuration));
    }

    [Test]
    [Category("Timing")]
    public void DriftPrevention_MetronomeStyle()
    {
        // Verify that the timer uses metronome-style advancement:
        // Even if callbacks take some time, the average rate should be close to the configured interval
        var count = 0L;
        var intervalTicks = Stopwatch.Frequency / 100; // 10ms interval

        using var timer = new HighResolutionTimerService(
            "DriftTest",
            intervalTicks,
            (_, _) =>
            {
                Interlocked.Increment(ref count);
                Thread.SpinWait(10000); // Simulate ~1ms of work per callback
            },
            _registry.TimerDedicated);

        timer.Start();

        // Run at a 10ms interval over a MEASURED window (see Single_FiresAtExpectedRate for why the sleep duration is
        // not trustworthy as the window). 250ms nominal — half the wall-clock of the original 500ms/20ms form, for the
        // same tick count and the same drift signal.
        var start = Stopwatch.GetTimestamp();
        Thread.Sleep(250);
        var elapsedTicks = Stopwatch.GetTimestamp() - start;

        var invocations = Interlocked.Read(ref count);
        var expected = (double)elapsedTicks / intervalTicks;
        var elapsedMs = elapsedTicks * 1000.0 / Stopwatch.Frequency;

        // With drift, each callback's ~1ms of work would push the next tick out and the error would compound, so the
        // count falls away from the ideal rate. The metronome advances from the SCHEDULED time, keeping it steady.
        Assert.That(invocations, Is.GreaterThanOrEqualTo((long)(expected * 0.5)),
            $"Drift prevention: fired {invocations}x in {elapsedMs:F1}ms at a 10ms interval; expected near {expected:F1}.");
        Assert.That(invocations, Is.LessThanOrEqualTo((long)expected + 2),
            $"Drift prevention: fired {invocations}x in {elapsedMs:F1}ms at a 10ms interval; cannot exceed {expected:F1}.");
    }
}
