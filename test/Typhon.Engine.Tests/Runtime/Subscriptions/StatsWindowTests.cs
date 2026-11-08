using NUnit.Framework;
using System;

namespace Typhon.Engine.Tests.Runtime.Subscriptions;

/// <summary>
/// The STATS tick figures' windows: <c>typhon.tick.p99</c> is a percentile over ten seconds of ticks, and <c>typhon.tick.max</c> is the longest tick of the
/// last second. A p99 over one emission window, thirty samples at 30 Hz, was that window's maximum under a percentile's name: one slow tick a second read
/// as a 30 ms tail.
/// </summary>
[TestFixture]
class StatsWindowTests
{
    private const int Hz = 30;

    private static TickTelemetryRing Ring(int ticks, Func<int, float> durationMs)
    {
        var ring = new TickTelemetryRing(1024, 0);
        for (var t = 0; t < ticks; t++)
        {
            ring.Record(new TickTelemetry { TickNumber = t, ActualDurationMs = durationMs(t) }, ReadOnlySpan<SystemTelemetry>.Empty);
        }

        return ring;
    }

    [Test]
    public void TheP99WindowIsTenEmissions()
    {
        Assert.That(StatsEncoder.PercentileWindowOf(Hz), Is.EqualTo(300));
        Assert.That(StatsEncoder.PercentileWindowOf(60), Is.EqualTo(600));
        Assert.That(StatsEncoder.PercentileWindowOf(0), Is.EqualTo(StatsEncoder.PercentileWindowSeconds), "a degenerate rate still looks somewhere");
    }

    [Test]
    public void OneSlowTickASecondIsTheMaxButNotTheP99()
    {
        // Ten seconds at 30 Hz, 1 ms ticks, and one 30 ms tick each second: 10 of 300 samples, 3.3 %, above the 1 % tail.
        var ring = Ring(300, t => t % Hz == 7 ? 30f : 1f);
        var window = StatsEncoder.PercentileWindowOf(Hz);
        var samples = new double[window];
        const long now = 300;

        Assert.Multiple(() =>
        {
            Assert.That(StatsEncoder.TickMax(ring, now, Hz), Is.EqualTo(30), "the second's slowest tick, named for what it is");
            Assert.That(StatsEncoder.TickPercentile(ring, now, window, 0.99, samples), Is.EqualTo(30),
                "3 % of ticks at 30 ms is in the 1 % tail: the p99 says so");
        });

        // One slow tick in ten seconds: 1 of 300, under the 1 % tail. The old one-second p99 read 30 ms here, every second it held the spike.
        ring = Ring(300, t => t == 295 ? 30f : 1f);
        Assert.Multiple(() =>
        {
            Assert.That(StatsEncoder.TickMax(ring, now, Hz), Is.EqualTo(30));
            Assert.That(StatsEncoder.TickPercentile(ring, now, window, 0.99, samples), Is.EqualTo(1), "a single spike in 300 ticks is not the p99");
            Assert.That(StatsEncoder.TickPercentile(ring, now, Hz, 0.99, samples), Is.EqualTo(30), "which a one-second window could not tell from the max");
        });
    }

    [Test]
    public void AWindowWiderThanTheScratchKeepsTheNewestTicks()
    {
        // The first 200 ticks were slow, the last 100 fast: with 100 slots of scratch, the percentile is the recent ticks', not the oldest ones'.
        var ring = Ring(300, t => t < 200 ? 50f : 1f);
        var samples = new double[100];
        Assert.That(StatsEncoder.TickPercentile(ring, 300, 300, 0.99, samples), Is.EqualTo(1));
    }

    [Test]
    public void NoTelemetryReadsZero()
    {
        Assert.That(StatsEncoder.TickMax(null, 10, Hz), Is.Zero);
        Assert.That(StatsEncoder.TickPercentile(null, 10, 300, 0.99, new double[300]), Is.Zero);
    }
}
