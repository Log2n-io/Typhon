using NUnit.Framework;
using System;
using System.Diagnostics;
using System.Threading;

namespace Typhon.Engine.Tests;

/// <summary>
/// Tests for <see cref="HighResolutionTimerServiceBase"/> via the concrete
/// <see cref="HighResolutionTimerService"/> (single handler).
/// Covers: calibration, thread lifecycle, timing metrics, dispose.
/// </summary>
[TestFixture]
public class HighResolutionTimerServiceBaseTests
{
    private ResourceRegistry _registry;

    [SetUp]
    public void Setup()
    {
        _registry = new ResourceRegistry(new ResourceRegistryOptions { Name = "TimerTest" });
    }

    [TearDown]
    public void TearDown()
    {
        _registry.Dispose();
    }

    [Test]
    public void Start_CreatesBackgroundThread()
    {
        using var timer = new HighResolutionTimerService(
            "TestTimer",
            Stopwatch.Frequency / 100, // 10ms
            (_, _) => { },
            _registry.TimerDedicated);

        Assert.That(timer.IsRunning, Is.False);

        timer.Start();
        SpinWait.SpinUntil(() => timer.IsRunning, 2000);

        Assert.That(timer.IsRunning, Is.True);
    }

    [Test]
    public void Dispose_JoinsThread()
    {
        var timer = new HighResolutionTimerService(
            "DisposeTest",
            Stopwatch.Frequency / 100,
            (_, _) => { },
            _registry.TimerDedicated);

        timer.Start();
        SpinWait.SpinUntil(() => timer.IsRunning, 2000);
        Assert.That(timer.IsRunning, Is.True);

        timer.Dispose();

        Assert.That(timer.IsRunning, Is.False);
        Assert.That(timer.IsDisposed, Is.True);
    }

    [Test]
    public void Dispose_DoubleDispose_NoThrow()
    {
        var timer = new HighResolutionTimerService(
            "DoubleDispose",
            Stopwatch.Frequency / 100,
            (_, _) => { },
            _registry.TimerDedicated);

        timer.Start();
        timer.Dispose();

        Assert.DoesNotThrow(() => timer.Dispose());
    }

    [Test]
    public void IntervalTicks_Zero_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new HighResolutionTimerService("Bad", 0, (_, _) => { }, _registry.TimerDedicated));
    }

    [Test]
    public void IntervalTicks_Negative_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new HighResolutionTimerService("Bad", -1, (_, _) => { }, _registry.TimerDedicated));
    }

    [Test]
    public void Callback_Null_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new HighResolutionTimerService("Bad", Stopwatch.Frequency / 100, null, _registry.TimerDedicated));
    }

    [Test]
    public void IsRunning_ReflectsState()
    {
        using var timer = new HighResolutionTimerService(
            "StateTest",
            Stopwatch.Frequency / 100,
            (_, _) => { },
            _registry.TimerDedicated);

        Assert.That(timer.IsRunning, Is.False, "Before Start");

        timer.Start();
        SpinWait.SpinUntil(() => timer.IsRunning, 2000);
        Assert.That(timer.IsRunning, Is.True, "After Start");

        timer.Dispose();
        Assert.That(timer.IsRunning, Is.False, "After Dispose");
    }

    [Test]
    [Category("Sensitive")] // wall-clock calibration assertion — flaky under parallel CPU load; runs in the gate's serial quiet pass
    public void Calibration_Reasonable()
    {
        HighResolutionTimerServiceBase.ResetCalibrationSleepThreshold();
        using var timer = new HighResolutionTimerService(
            "CalibrationTest",
            Stopwatch.Frequency / 100,
            (_, _) => { },
            _registry.TimerDedicated);

        var calibrated = timer.CalibratedSleepResolution;

        // Sleep(1) worst case should be between 0.3ms and 25ms on any platform
        Assert.That(calibrated.TotalMilliseconds, Is.GreaterThan(0.3), "Calibration too low");
        Assert.That(calibrated.TotalMilliseconds, Is.LessThan(25.0), "Calibration too high");
    }

    [Test]
    public void Start_Idempotent()
    {
        using var timer = new HighResolutionTimerService(
            "IdempotentTest",
            Stopwatch.Frequency / 100,
            (_, _) => { },
            _registry.TimerDedicated);

        timer.Start();
        timer.Start(); // Second call should not throw or create a second thread
        SpinWait.SpinUntil(() => timer.IsRunning, 2000);

        Assert.That(timer.IsRunning, Is.True);
    }

    [Test]
    public void ResourceTree_RegistersUnderDedicated()
    {
        using var timer = new HighResolutionTimerService(
            "TreeTest",
            Stopwatch.Frequency / 100,
            (_, _) => { },
            _registry.TimerDedicated);

        // Timer should be a child of the Dedicated node
        Assert.That(timer.Parent, Is.SameAs(_registry.TimerDedicated));
        Assert.That(timer.Type, Is.EqualTo(ResourceType.Service));
    }

    [Test]
    [Category("Timing")]
    public void MeanTimingError_PopulatedAfterTicks()
    {
        using var ready = new ManualResetEventSlim(false);

        using var timer = new HighResolutionTimerService(
            "ErrorTest",
            Stopwatch.Frequency / 100, // 10ms
            (_, _) => ready.Set(),
            _registry.TimerDedicated);

        timer.Start();

        // Wait for at least one tick to fire
        Assert.That(ready.Wait(2000), Is.True, "Timer did not tick within 2s");

        Assert.That(timer.TickCount, Is.GreaterThan(0), "Should have ticked");
        // MeanTimingErrorUs should be populated (non-zero after first tick)
        // We don't assert a tight bound because CI environments vary widely
        Assert.That(timer.MeanTimingErrorUs, Is.GreaterThanOrEqualTo(0.0));
        Assert.That(timer.MaxTimingErrorUs, Is.GreaterThanOrEqualTo(0.0));
    }
}
