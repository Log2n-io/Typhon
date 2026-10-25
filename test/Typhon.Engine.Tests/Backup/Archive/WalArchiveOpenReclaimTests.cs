using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Typhon.Engine.Tests;

/// <summary>
/// WA-3 (BKP-10): the open's reconcile is the only other reclaim site, and it runs before any archiver exists. With archiving configured it reclaims
/// nothing, so a close with the store unreachable loses none of its un-archived segments to the next open (AC-A5).
/// </summary>
[NonParallelizable]
class WalArchiveOpenReclaimTests : ArchiveTestBase<WalArchiveOpenReclaimTests>
{
    private const string Marker = "BKP-10: the open reclaimed an un-archived segment";

    [Test]
    [CancelAfter(15_000)]
    [VerifiesRule("BKP-10")]
    public void ACloseWithNothingArchived_ThenAReopen_KeepsEverySegmentForTheArchive()
    {
        List<WalSealedSegment> before;
        long checkpointLsn;
        using (var scope = ServiceProvider.CreateScope())
        {
            var dbe = Open(scope);
            SpawnAndSeal(dbe, 20, 0);
            SpawnAndSeal(dbe, 20, 100);
            Spawn(dbe, 1, 9_000);
            ForceCheckpoint(dbe);
            before = Sealed(dbe);
            checkpointLsn = dbe.CheckpointManager.CheckpointLsn;
        }

        Assert.That(before.Count, Is.EqualTo(2));
        Assert.That(before.All(s => s.LastLsn < checkpointLsn), Is.True, "the checkpoint alone would reclaim them at the open");
        using (var scope = ServiceProvider.CreateScope())
        {
            var dbe = Open(scope);
            ForceCheckpoint(dbe);
            foreach (var s in before)
            {
                Assert.That(File.Exists(s.Path), Is.True, $"{Marker}: segment {s.Id}");
            }

            var adopted = Sealed(dbe);
            Assert.That(adopted.Take(2).Select(s => (s.Id, s.FirstLsn, s.LastLsn)), Is.EqualTo(before.Select(s => (s.Id, s.FirstLsn, s.LastLsn))));
            Assert.That(adopted.Take(2).All(s => s.Adopted && s.SealedUtcTicks == 0), Is.True, "adopted from the previous session, seal time unknown");
        }
    }

    /// <summary>The reconcile as it was (the checkpoint frontier alone): the verifier must see the un-archived segments go.</summary>
    [Test]
    [CancelAfter(15_000)]
    [RuleMutant("BKP-10")]
    public void Mutant_AnOpenReclaimingBelowTheCheckpointOnly_IsReported()
    {
        var segments = SealTwoSegmentsOnDisk(out var dir, out var frontier);
        try
        {
            using var io = new WalFileIO();
            using var manager = new WalSegmentManager(io, dir, SegmentSize, 1, false);
            manager.Initialize(0, frontier + 1, frontier + 1, reclaimFloorAtOpen: long.MaxValue);
            RuleMutants.AssertDetects("BKP-10", Marker, () =>
            {
                foreach (var s in segments)
                {
                    Assert.That(File.Exists(s), Is.True, $"{Marker}: {Path.GetFileName(s)}");
                }
            });
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Test]
    [CancelAfter(15_000)]
    public void TheReconcile_WithTheFloorAtZero_AdoptsEverySegment_AndReclaimsNone()
    {
        var segments = SealTwoSegmentsOnDisk(out var dir, out var frontier);
        try
        {
            using var io = new WalFileIO();
            using var manager = new WalSegmentManager(io, dir, SegmentSize, 1, false);
            manager.Initialize(0, frontier + 1, frontier + 1, reclaimFloorAtOpen: 0);
            Assert.That(segments.All(File.Exists), Is.True);
            Assert.That(manager.SealedSegmentCount, Is.EqualTo(2));
            Assert.That(manager.BytesHeldBetween(0, frontier + 1), Is.EqualTo(2L * SegmentSize));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    /// <summary>Two sealed segments on disk, LSNs 1..10 and 11..20, as a previous session leaves them; returns their paths and the frontier (20).</summary>
    private static string[] SealTwoSegmentsOnDisk(out string dir, out long frontier)
    {
        dir = Path.Combine(TestStorage.Root, nameof(WalArchiveOpenReclaimTests), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        using var io = new WalFileIO();
        using var manager = new WalSegmentManager(io, dir, SegmentSize, 1, false);
        manager.Initialize(0, 1);
        var first = manager.ActiveSegment.Path;
        manager.RotateSegment(11, 10);
        var second = manager.ActiveSegment.Path;
        frontier = 20;
        return [first, second];
    }
}
