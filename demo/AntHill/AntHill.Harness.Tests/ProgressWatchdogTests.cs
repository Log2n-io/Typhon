using System;
using NUnit.Framework;

namespace AntHill.Harness.Tests;

[TestFixture]
public sealed class ProgressWatchdogTests
{
    [Test]
    public void Observe_StaysHealthyWhileTicksAdvance()
    {
        var wd = new ProgressWatchdog(TimeSpan.FromSeconds(10), nowMs: 0);
        Assert.That(wd.Observe(currentTick: 1, nowMs: 5_000), Is.True);
        Assert.That(wd.Observe(currentTick: 2, nowMs: 14_000), Is.True);  // progress resets the stall clock
        Assert.That(wd.Observe(currentTick: 3, nowMs: 23_000), Is.True);
    }

    [Test]
    public void Observe_TripsAfterTimeoutWithNoProgress()
    {
        var wd = new ProgressWatchdog(TimeSpan.FromSeconds(10), nowMs: 0);
        Assert.That(wd.Observe(currentTick: 5, nowMs: 1_000), Is.True);
        Assert.That(wd.Observe(currentTick: 5, nowMs: 8_000), Is.True);   // 7s stalled — still under timeout
        Assert.That(wd.Observe(currentTick: 5, nowMs: 12_000), Is.False); // 11s stalled — trips
    }

    [Test]
    public void Observe_ProgressResetsTheStallClock()
    {
        var wd = new ProgressWatchdog(TimeSpan.FromSeconds(10), nowMs: 0);
        wd.Observe(currentTick: 1, nowMs: 9_000);
        Assert.That(wd.Observe(currentTick: 2, nowMs: 18_000), Is.True);   // advanced — clock reset to 18s
        Assert.That(wd.Observe(currentTick: 2, nowMs: 28_500), Is.False);  // 10.5s since the reset — trips
    }
}
