using System;
using NUnit.Framework;
using Typhon.Engine.Internals;

namespace Typhon.Engine.Tests.Runtime.Subscriptions.Oracle;

/// <summary>
/// #1615: a Sphere band can coarsen motion — <c>Every(n, beyond, tolerance: k)</c> sends its sessions a segment only when their own extrapolation is off by
/// more than k tolerances. Each distinct tolerance is a band head every moving entity keeps, copied from the base segment when its own error passes its
/// bound; every record a session is sent for the entity reads the head of the band it sees the entity in.
/// </summary>
/// <remarks>
/// What the oracle proves: walking sessions hold exactly their disc once the world is quiet, every entity within ITS band's tolerance — with the anchors
/// carrying entities across boundaries both ways (outward into a coarser head, inward into a finer one), with skipped frames, serial and parallel. That a
/// coarser head refits less often — the saving — is measured on smooth motion in <c>MotionSegmentRateTests</c>: this workload's turns, drifts and teleports
/// break every tolerance at once.
/// </remarks>
[TestFixture]
[NonParallelizable]
sealed class PushBandToleranceTests : TestBase<PushBandToleranceTests>
{
    private const int GateTicks = 200;
    private const int CompareEvery = 40;

    /// <summary>
    /// Three bands that coarsen motion — ×4 every tick beyond 0.3 R, ×8 every 2 beyond 0.55 R, ×16 every 4 beyond 0.8 R: walking sessions converge, each
    /// entity within its band's tolerance, at every skip rate and in both index modes.
    /// </summary>
    /// <param name="skipPercent">The percentage of ticks on which each session's frames are left undrained.</param>
    /// <param name="deterministic">Whether the index and the far flushes are built serially, in the frame prologue.</param>
    [Test]
    [VerifiesRule("SUB-19")]
    public void BandHeadsConvergeWithinEachBandsTolerance([Values(0, 30, 60)] int skipPercent, [Values(false, true)] bool deterministic)
    {
        using var oracle = OracleHarness.Create(ProjectionTestSchema.SetupEngine(ServiceProvider), seed: 16150 + skipPercent,
            [skipPercent, 0, 30, skipPercent], nameof(PushBandToleranceTests), walkRadius: 3000, deterministicProjection: deterministic,
            bands: b => b.Every(1, beyond: 0.3, tolerance: 4).Every(2, beyond: 0.55, tolerance: 8).Every(4, beyond: 0.8, tolerance: 16));
        oracle.Workload.WalkStrideM = 1.5f;
        Assert.That((oracle.Push.FarPhase, oracle.Push.FarWindow), Is.EqualTo((2, 4)), "a period-1 band sets no phase: the fold runs at the next one");

        for (var i = 0; i < GateTicks; i++)
        {
            oracle.Step();
            if ((i + 1) % CompareEvery == 0)
            {
                oracle.Quiesce();
                oracle.AssertConverged($"three tolerance bands, after {i + 1} ticks at a {skipPercent}% skip rate");
            }
        }

        Assert.Multiple(() =>
        {
            Assert.That(oracle.Push.UpdatesDeferred, Is.GreaterThan(20), "the periodic bands deferred nothing, so they were not exercised");
            Assert.That(oracle.Push.FarCrescentStates, Is.GreaterThan(0), "no entity came inward across a boundary with the anchor's move");
        });
    }

    /// <summary>A band's tolerance is checked where it is declared: 1 to 64, never finer than the band inside it, and a band must coarsen something.</summary>
    [Test]
    public void ABadBandToleranceIsRefusedWhereItIsDeclared()
    {
        Assert.Multiple(() =>
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new BandBuilder().Every(2, 0.5, tolerance: 0.5), "finer than the archetype's own");
            Assert.Throws<ArgumentOutOfRangeException>(() => new BandBuilder().Every(2, 0.5, tolerance: 65), "past the cap");
            Assert.Throws<ArgumentOutOfRangeException>(() => new BandBuilder().Every(1, 0.5), "every tick at the archetype's tolerance changes nothing");
            Assert.Throws<ArgumentOutOfRangeException>(() => new BandBuilder().Every(2, 0.3, tolerance: 8).Every(4, 0.6, tolerance: 4), "finer outward");
            Assert.Throws<ArgumentOutOfRangeException>(() => new BandBuilder().Every(2, 0.3, tolerance: 4).Every(2, 0.6, tolerance: 4), "coarsens nothing");
            Assert.DoesNotThrow(() => new BandBuilder().Every(1, 0.3, tolerance: 2).Every(1, 0.6, tolerance: 4).Every(2, 0.8, tolerance: 4));
        });
    }

    /// <summary>The runtime keeps one head per distinct band tolerance, at most three, ascending; a band's head indexes that list.</summary>
    [Test]
    public void BandTolerancesAreDistinctAscendingAndCapped()
    {
        var registry = new SubscriptionsRegistry();
        registry.Profile("a", p => p.Sphere(100).Bands(b => b.Every(2, 0.3, tolerance: 8).Every(4, 0.6, tolerance: 16)));
        registry.Profile("b", p => p.Sphere(100).Bands(b => b.Every(1, 0.5, tolerance: 2).Every(2, 0.7, tolerance: 8)));
        var tolerances = BandTolerances.Of(registry);
        var bands = new LodBands([new DistanceBand(1, 0.5, 2), new DistanceBand(2, 0.7, 8)], tolerances);
        Assert.Multiple(() =>
        {
            Assert.That(tolerances, Is.EqualTo(new[] { 2d, 8d, 16d }));
            Assert.That((bands.H1, bands.H2), Is.EqualTo(((byte)1, (byte)2)));
            Assert.That(bands.MinEvery, Is.EqualTo(2), "the period-1 band sets no fold phase");
        });

        registry.Profile("c", p => p.Sphere(100).Bands(b => b.Every(2, 0.5, tolerance: 32)));
        Assert.That(() => BandTolerances.Of(registry), Throws.InvalidOperationException.With.Message.Contains("at most 3"));
    }
}
