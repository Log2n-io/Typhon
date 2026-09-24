using NUnit.Framework;

namespace Typhon.Engine.Tests.Runtime.Subscriptions.Oracle;

/// <summary>
/// The flat-world byte gate of Phase 1.5 (<c>claude/design/Subscriptions/10-phase15-3d-groundwork.md</c> § 10): seeded oracle runs whose every delivered
/// frame is folded into a digest, pinned to the value the engine produced before 1.5 rebuilt the push index.
/// </summary>
/// <remarks>
/// <para>
/// <b>What the digest covers.</b> Everything a client decodes from each frame it is delivered (<see cref="FrameHarness.DigestFrames"/>): which entities
/// enter, move, change and leave, in which frame, with their positions, velocities, segment times, epochs and field values — metric values aside. A
/// frame's records are sorted by netId per list, so the order of a cell's events inside the index never reaches the wire; the gate holds any correct
/// index and fails any change a client would see.
/// </para>
/// <para>
/// <b>Pinned on Typhon <c>0740cd37</c></b> (Phase 1.5.0, whose frames are the baseline's): the values below are what that engine produced for these
/// runs, with this digest.
/// </para>
/// <para>
/// <b>When a constant may change.</b> Only with a step that changes frames on purpose and says so (10 § 12: 1.5.3, <c>World</c> pacing on occupied
/// cells). Re-recording one to make a refactor pass defeats the gate.
/// </para>
/// </remarks>
[TestFixture]
[NonParallelizable]
class PushFrameDigestTests : TestBase<PushFrameDigestTests>
{
    private const int Ticks = 160;

    private static ulong Run(OracleHarness oracle, int ticks = Ticks)
    {
        oracle.Frames.DigestFrames = true;
        for (var i = 0; i < ticks; i++)
        {
            oracle.Step();
        }

        oracle.Quiesce();
        oracle.AssertConverged("the digest run");
        return oracle.Frames.Digest;
    }

    [Test]
    public void SpheresUnderChurnAndSkips()
    {
        using var oracle = OracleHarness.Create(ProjectionTestSchema.SetupEngine(ServiceProvider), seed: 1501, [0, 30, 60], nameof(PushFrameDigestTests),
            deterministicProjection: true);

        Assert.That(Run(oracle), Is.EqualTo(9167096882797981372UL));
    }

    [Test]
    public void WorldObserversUnderChurnAndSkips()
    {
        using var oracle = OracleHarness.Create(ProjectionTestSchema.SetupEngine(ServiceProvider), seed: 1502, [0, 60], nameof(PushFrameDigestTests),
            worldObserver: true, deterministicProjection: true);

        Assert.That(Run(oracle), Is.EqualTo(17210926344840522847UL));
    }

    /// <summary>A grid of 129 × 129 cells, so a <c>World</c> session's fill is paced over several frames (4 096 cells visited per frame).</summary>
    [Test]
    public void WorldObserversPacedOverAFineGrid()
    {
        using var oracle = OracleHarness.Create(ProjectionTestSchema.SetupEngine(ServiceProvider), seed: 1505, [0, 30, 60], nameof(PushFrameDigestTests),
            worldObserver: true, bigWorld: true, deterministicProjection: true, replicationCellM: 128);

        Assert.That(Run(oracle), Is.EqualTo(5680082172371474769UL));
    }

    [Test]
    public void WalkingSpheresWithTheDistanceLod([Values(false, true)] bool deterministic)
    {
        using var oracle = OracleHarness.Create(ProjectionTestSchema.SetupEngine(ServiceProvider), seed: 1503, [0, 30, 0, 60], nameof(PushFrameDigestTests),
            walkRadius: 3000, deterministicProjection: deterministic);
        oracle.Push.FarEvery = 4;

        Assert.That(Run(oracle), Is.EqualTo(13328118253220528914UL));
    }

    [Test]
    public void AProfileServedEveryFourthTick([Values(false, true)] bool world)
    {
        using var oracle = OracleHarness.Create(ProjectionTestSchema.SetupEngine(ServiceProvider), seed: 1504, [0, 30], nameof(PushFrameDigestTests),
            worldObserver: world, every: 4, deterministicProjection: true);

        Assert.That(Run(oracle), Is.EqualTo(6800132841681185214UL));
    }
}
