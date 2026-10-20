using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Typhon.Engine.Tests;

/// <summary>
/// W1′ at step 8 (BKP-10, impl/1418 §3.2): with archiving configured the checkpoint never reclaims a segment the archive floor holds, the floor alone
/// decides (raised by the archiver, released by the backlog policy, never by anything else), and a raise racing the release wins.
/// </summary>
[NonParallelizable]
class WalArchiveFloorTests : ArchiveTestBase<WalArchiveFloorTests>
{
    private const string Marker = "BKP-10: an un-archived segment was reclaimed";

    protected override long BacklogLimit => TestContext.CurrentContext.Test.Name.Contains("Backlog") || TestContext.CurrentContext.Test.Name.Contains("Race")
        ? 2L * SegmentSize
        : base.BacklogLimit;

    /// <summary>The verifier: every segment whose last LSN is at or above <paramref name="floor"/> is still on disk.</summary>
    private static void AssertHeld(IEnumerable<WalSealedSegment> segments, long floor)
    {
        foreach (var s in segments.Where(s => s.LastLsn >= floor))
        {
            Assert.That(File.Exists(s.Path), Is.True, $"{Marker}: segment {s.Id} (LSN {s.FirstLsn}..{s.LastLsn}) is gone with the floor at {floor}");
        }
    }

    [Test]
    [CancelAfter(15_000)]
    [VerifiesRule("BKP-10")]
    public void UnarchivedSegments_SurviveEveryCheckpoint_UntilTheFloorRisesPastThem()
    {
        using var scope = ServiceProvider.CreateScope();
        var dbe = Open(scope);
        Assert.That(dbe.CheckpointManager.ArchiveFloor, Is.Zero, "engaged at 0 before the first cycle: nothing is archived yet");
        var segments = new[] { SpawnAndSeal(dbe, 20, 0), SpawnAndSeal(dbe, 20, 100), SpawnAndSeal(dbe, 20, 200) };
        Spawn(dbe, 1, 9_000);   // a record past the last seal: the checkpoint may pass every sealed segment (WP-09 keeps lastLSN = C)
        ForceCheckpoint(dbe);
        ForceCheckpoint(dbe);
        Assert.That(dbe.CheckpointManager.CheckpointLsn, Is.GreaterThan(segments[^1].LastLsn), "the checkpoint alone would reclaim all three");
        AssertHeld(segments, 0);

        dbe.CheckpointManager.RaiseArchiveFloor(segments[0].LastLsn + 1);
        ForceCheckpoint(dbe);
        Assert.That(File.Exists(segments[0].Path), Is.False, "archived and checkpointed: reclaimed");
        AssertHeld(segments, segments[0].LastLsn + 1);
        Assert.That(dbe.CheckpointManager.ArchiveReleases, Is.Zero);
    }

    /// <summary>Step 8 without the floor (the reclaim the checkpoint makes on its own): the verifier must see the un-archived segments go.</summary>
    [Test]
    [CancelAfter(15_000)]
    [RuleMutant("BKP-10")]
    public void Mutant_AReclaimIgnoringTheArchiveFloor_IsReported()
    {
        using var scope = ServiceProvider.CreateScope();
        var dbe = Open(scope);
        var segments = new[] { SpawnAndSeal(dbe, 20, 0), SpawnAndSeal(dbe, 20, 100) };
        Spawn(dbe, 1, 9_000);
        ForceCheckpoint(dbe);
        dbe.WalManager.SegmentManager.MarkReclaimable(dbe.CheckpointManager.CheckpointLsn);
        RuleMutants.AssertDetects("BKP-10", Marker, () => AssertHeld(segments, 0));
    }

    [Test]
    [CancelAfter(15_000)]
    public void PastTheBacklogLimit_TheCheckpointReleasesTheFloor_OnceAndCounted_AndOnlyARaiseReengagesIt()
    {
        using var scope = ServiceProvider.CreateScope();
        var dbe = Open(scope);
        var segments = new[] { SpawnAndSeal(dbe, 20, 0), SpawnAndSeal(dbe, 20, 100), SpawnAndSeal(dbe, 20, 200) };
        Spawn(dbe, 1, 9_000);
        ForceCheckpoint(dbe);
        ForceCheckpoint(dbe);

        // Three segments, two allowed: one cycle releases, counts it once, and reclaims as if archiving were off.
        Assert.That(dbe.CheckpointManager.ArchiveReleases, Is.EqualTo(1));
        Assert.That(dbe.CheckpointManager.ArchiveFloor, Is.EqualTo(CheckpointManager.ArchiveFloorReleased));
        Assert.That(segments.All(s => !File.Exists(s.Path)), Is.True, "released: the checkpoint reclaims every checkpointed segment");

        // Released stays released (no second count) until the archiver raises it again.
        var more = SpawnAndSeal(dbe, 20, 300);
        Spawn(dbe, 1, 9_001);
        ForceCheckpoint(dbe);
        Assert.That(dbe.CheckpointManager.ArchiveReleases, Is.EqualTo(1));
        Assert.That(File.Exists(more.Path), Is.False);

        var held = SpawnAndSeal(dbe, 20, 400);
        dbe.CheckpointManager.RaiseArchiveFloor(held.FirstLsn);
        ForceCheckpoint(dbe);
        Assert.That(dbe.CheckpointManager.ArchiveFloor, Is.EqualTo(held.FirstLsn), "re-engaged by the archiver's raise");
        Assert.That(File.Exists(held.Path), Is.True, "held again: one segment is within the limit");
    }

    /// <summary>The archiver raises the floor between step 8's read and its CAS: the CAS fails, the cycle honours the floor it read, nothing is released.</summary>
    [Test]
    [CancelAfter(15_000)]
    public void ARaise_RacingTheReleaseCas_Wins_AndTheCycleDeletesNoMoreThanItsFloorAllowed()
    {
        using var scope = ServiceProvider.CreateScope();
        var dbe = Open(scope);
        var segments = new[] { SpawnAndSeal(dbe, 20, 0), SpawnAndSeal(dbe, 20, 100), SpawnAndSeal(dbe, 20, 200), SpawnAndSeal(dbe, 20, 300) };
        Spawn(dbe, 1, 9_000);
        var raised = segments[1].LastLsn + 1;
        var fired = 0;
        dbe.CheckpointManager.ArchiveFloorProbe = () =>
        {
            if (fired++ == 0)
            {
                dbe.CheckpointManager.RaiseArchiveFloor(raised);
            }
        };

        ForceCheckpoint(dbe);
        Assert.That(fired, Is.GreaterThanOrEqualTo(1), "the probe ran between the read and the CAS");
        Assert.That(dbe.CheckpointManager.ArchiveReleases, Is.Zero, "the CAS lost to the raise");
        Assert.That(dbe.CheckpointManager.ArchiveFloor, Is.EqualTo(raised));
        AssertHeld(segments, raised);

        dbe.CheckpointManager.ArchiveFloorProbe = null;
        ForceCheckpoint(dbe);
        Assert.That(File.Exists(segments[0].Path) || File.Exists(segments[1].Path), Is.False, "the next cycle honours the raise");
        Assert.That(dbe.CheckpointManager.ArchiveReleases, Is.Zero, "two segments held: within the limit");
        AssertHeld(segments, raised);
    }
}

/// <summary>The control: archiving off, the floor is one read and holds nothing.</summary>
[NonParallelizable]
class WalArchiveFloorOffTests : ArchiveTestBase<WalArchiveFloorOffTests>
{
    protected override bool ArchivingOn => false;

    [Test]
    [CancelAfter(15_000)]
    public void ArchivingOff_TheCheckpointReclaimsAsBefore()
    {
        using var scope = ServiceProvider.CreateScope();
        var dbe = Open(scope);
        Assert.That(dbe.CheckpointManager.ArchiveFloor, Is.EqualTo(long.MaxValue));
        var segments = new[] { SpawnAndSeal(dbe, 20, 0), SpawnAndSeal(dbe, 20, 100) };
        Spawn(dbe, 1, 9_000);
        ForceCheckpoint(dbe);
        ForceCheckpoint(dbe);
        Assert.That(segments.All(s => !File.Exists(s.Path)), Is.True);
        Assert.That(dbe.CheckpointManager.ArchiveReleases, Is.Zero);
    }
}
