using System;
using NUnit.Framework;

namespace Typhon.Engine.Tests.Runtime;

/// <summary>
/// #ENG-07 — the engine's one definition of a percentile over telemetry samples.
/// </summary>
/// <remarks>
/// <para>
/// Worth its own fixture because two paths report percentiles of the same ring to different audiences: the <c>STATS</c> wire block to game clients and
/// <see cref="TyphonRuntime.ReadStats"/> to an HTTP endpoint or a CLI verb. Before they shared this function each had its own rank arithmetic, and a rank of
/// difference is invisible in either figure alone — the reader simply trusts whichever one they happen to see. Pinning the definition here is what makes
/// "they agree" a property of the code rather than of a review.
/// </para>
/// <para>
/// The interesting cases are all at the boundaries: an empty window, a single sample, the rank at exactly 1.0, and the fact that the input is sorted IN PLACE
/// (which the <c>STATS</c> path depends on, because it reuses one buffer sized at construction and must not allocate after it).
/// </para>
/// </remarks>
[TestFixture]
public class TelemetryPercentileTests
{
    [Test]
    public void NearestRank_PicksAMeasuredSample_NeverAnInterpolatedOne()
    {
        // Ten samples, p95. An interpolated percentile would answer 9.5 — a duration no tick had. Nearest rank answers 10.
        var samples = new double[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 };

        Assert.Multiple(() =>
        {
            Assert.That(TelemetryPercentile.NearestRank(samples, 10, 0.95), Is.EqualTo(10), "ceil(0.95 * 10) = 10th sample");
            Assert.That(TelemetryPercentile.NearestRank(samples, 10, 0.50), Is.EqualTo(5), "ceil(0.50 * 10) = 5th sample — the lower median, by rank");
            Assert.That(TelemetryPercentile.NearestRank(samples, 10, 1.0), Is.EqualTo(10), "the maximum, and not an out-of-range rank");
            Assert.That(TelemetryPercentile.NearestRank(samples, 10, 0.0), Is.EqualTo(1), "rank 0 clamps to the first sample rather than underflowing");
        });
    }

    [Test]
    public void NearestRank_SortsInPlace_BecauseTheStatsPathReusesOneBuffer()
    {
        // Not an implementation detail to be papered over: the STATS encoder sizes its sample buffer at construction and must not allocate after it, so this
        // function is allowed to reorder what it is given. A future rewrite that copies instead would break that guarantee silently.
        var samples = new double[] { 5, 1, 4, 2, 3 };

        Assert.That(TelemetryPercentile.NearestRank(samples, 5, 0.6), Is.EqualTo(3));
        Assert.That(samples, Is.EqualTo(new double[] { 1, 2, 3, 4, 5 }), "the caller's array is sorted in place");
    }

    [Test]
    public void NearestRank_ReadsOnlyTheLiveLeadingSamples()
    {
        // The ring hands over a fixed-size buffer whose tail is stale — a window shorter than the buffer is the normal case, not an edge one.
        var samples = new double[] { 3, 1, 2, 999, 999 };

        Assert.That(TelemetryPercentile.NearestRank(samples, 3, 1.0), Is.EqualTo(3), "the stale tail must not become the maximum");
    }

    [Test]
    public void NearestRank_AnEmptyWindowIsZero_WhichIsAReadingAndNotASentinel()
    {
        // A runtime that has not ticked has no duration. Zero says that; throwing would make a scrape during startup fail rather than report a starting
        // server, and a sentinel would have to be special-cased by every consumer.
        Assert.Multiple(() =>
        {
            Assert.That(TelemetryPercentile.NearestRank([], 0, 0.99), Is.Zero);
            Assert.That(TelemetryPercentile.NearestRank([1, 2, 3], 0, 0.99), Is.Zero, "count, not length, decides");
            Assert.That(TelemetryPercentile.NearestRank([1, 2, 3], -1, 0.99), Is.Zero, "a negative count is empty, not an exception");
        });
    }

    [Test]
    public void NearestRank_ASingleSampleIsEveryPercentile()
    {
        var samples = new double[] { 7 };

        Assert.Multiple(() =>
        {
            Assert.That(TelemetryPercentile.NearestRank(samples, 1, 0.50), Is.EqualTo(7));
            Assert.That(TelemetryPercentile.NearestRank(samples, 1, 0.99), Is.EqualTo(7), "one tick's p99 is that tick, not an extrapolation");
        });
    }

    [Test]
    public void NearestRank_ACountPastTheBufferIsClampedRatherThanRead()
    {
        // Defensive, and cheap: a caller that mis-tracks its own sample count would otherwise read past the array.
        var samples = new double[] { 4, 2 };

        Assert.That(TelemetryPercentile.NearestRank(samples, 99, 1.0), Is.EqualTo(4));
    }

    [Test]
    public void NearestRank_RefusesANullBuffer()
    {
        Assert.Throws<ArgumentNullException>(() => TelemetryPercentile.NearestRank(null, 1, 0.5));
    }
}
