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
/// runs, with this digest — except <see cref="WorldObserversPacedOverAFineGrid"/>, re-pinned by 1.5.3, whose pacing counts occupied cells only.
/// </para>
/// <para>
/// <b>Both implementations</b> (10 § 3.5, L6): every run is also served by the deep implementation on the same flat world, and must give the flat one's
/// digest — the deep geometry with z = 0 everywhere is the flat geometry, bit for bit (10 § 3.4). The one exception is a <c>World</c> fill the enter
/// budget splits, whose frames follow key order, tile-major in the deep implementation.
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
    public void SpheresUnderChurnAndSkips([Values] bool deep)
    {
        using var oracle = OracleHarness.Create(ProjectionTestSchema.SetupEngine(ServiceProvider), seed: 1501, [0, 30, 60], nameof(PushFrameDigestTests),
            deterministicProjection: true, forceDeep: deep);

        Assert.That(Run(oracle), Is.EqualTo(9167096882797981372UL));
    }

    [Test]
    public void WorldObserversUnderChurnAndSkips([Values] bool deep)
    {
        using var oracle = OracleHarness.Create(ProjectionTestSchema.SetupEngine(ServiceProvider), seed: 1502, [0, 60], nameof(PushFrameDigestTests),
            worldObserver: true, deterministicProjection: true, forceDeep: deep);

        Assert.That(Run(oracle), Is.EqualTo(17210926344840522847UL));
    }

    /// <summary>
    /// A grid of 128 × 128 cells. Before 1.5.3 a <c>World</c> session's fill visited 4 096 cells a frame, empty or not, and took five frames here; it now
    /// visits occupied cells only (1.5.3's pre-registered criterion: fewer frames to <c>VIEW_COMPLETE</c>), so the frames changed on purpose and the
    /// constant was re-pinned (the 1.5.1 engine gave 5680082172371474769).
    /// </summary>
    [Test]
    public void WorldObserversPacedOverAFineGrid([Values] bool deep)
    {
        using var oracle = OracleHarness.Create(ProjectionTestSchema.SetupEngine(ServiceProvider), seed: 1505, [0, 30, 60], nameof(PushFrameDigestTests),
            worldObserver: true, bigWorld: true, deterministicProjection: true, replicationCellM: 128, forceDeep: deep);

        var digest = Run(oracle);
        Assert.Multiple(() =>
        {
            for (var s = 0; s < oracle.Sessions.Length; s++)
            {
                Assert.That(oracle.Frames.FramesToComplete(oracle.Sessions[s]), Is.EqualTo(1), $"session {s}: frames to VIEW_COMPLETE (five before 1.5.3)");
            }

            // The deep implementation walks its keys in tile order, so where the enter budget splits a fill its frames differ from the flat row order's.
            Assert.That(digest, Is.EqualTo(deep ? 4702946978978135846UL : 1442836563172249734UL));
        });
    }

    [Test]
    public void WalkingSpheresWithTheDistanceLod([Values(false, true)] bool deterministic, [Values] bool deep)
    {
        using var oracle = OracleHarness.Create(ProjectionTestSchema.SetupEngine(ServiceProvider), seed: 1503, [0, 30, 0, 60], nameof(PushFrameDigestTests),
            walkRadius: 3000, deterministicProjection: deterministic, forceDeep: deep);
        oracle.Push.FarEvery = 4;

        Assert.That(Run(oracle), Is.EqualTo(13328118253220528914UL));
    }

    [Test]
    public void AProfileServedEveryFourthTick([Values(false, true)] bool world, [Values] bool deep)
    {
        using var oracle = OracleHarness.Create(ProjectionTestSchema.SetupEngine(ServiceProvider), seed: 1504, [0, 30], nameof(PushFrameDigestTests),
            worldObserver: world, every: 4, deterministicProjection: true, forceDeep: deep);

        Assert.That(Run(oracle), Is.EqualTo(6800132841681185214UL));
    }
}
