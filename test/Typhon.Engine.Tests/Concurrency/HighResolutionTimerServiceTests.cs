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

        using var timer = new HighResolutionTimerService(
            "RateTest",
            Stopwatch.Frequency / 200, // 5ms interval
            (_, _) => Interlocked.Increment(ref count),
            _registry.TimerDedicated);

        timer.Start();

        // Run for 100ms — expect ~20 invocations (±50% margin for CI)
        Thread.Sleep(100);

        // Stop the timer before reading counters to avoid a race where the
        // timer fires between reading `count` and `InvocationCount`.
        timer.Dispose();

        var invocations = Interlocked.Read(ref count);

        Assert.That(invocations, Is.GreaterThanOrEqualTo(10), "Too few invocations");
        Assert.That(invocations, Is.LessThanOrEqualTo(30), "Too many invocations");
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

        using var timer = new HighResolutionTimerService(
            "DriftTest",
            Stopwatch.Frequency / 100, // 10ms interval
            (_, _) =>
            {
                Interlocked.Increment(ref count);
                Thread.SpinWait(10000); // Simulate ~1ms of work per callback
            },
            _registry.TimerDedicated);

        timer.Start();

        // Run for 250ms at a 10ms interval — expect ~25 invocations with metronome-style (no drift). Half the wall-clock of the original 500ms/20ms form for the same tick count and drift signal.
        Thread.Sleep(250);

        var invocations = Interlocked.Read(ref count);

        // With drift, we'd see fewer invocations; metronome keeps the rate steady
        Assert.That(invocations, Is.GreaterThanOrEqualTo(15), "Drift prevention: too few ticks");
        Assert.That(invocations, Is.LessThanOrEqualTo(35), "Drift prevention: too many ticks");
    }
}
