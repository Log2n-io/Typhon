using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Typhon.Engine.Tests;

/// <summary>
/// AC-A10 (BKP-10's torn-tail clause, WA-9): with archiving on and the archive unreachable, the archive floor keeps a crash-torn segment past the crash
/// open. Recovery stops for good at a tear, so unsealed it would stop the next recovery before every segment the reopened session wrote: the crash open
/// seals it. Archiving off is <c>RecoveryCorruptionBoundaryTests.TornActiveSegment_ThenASecondCrash_KeepsTheNextSessionsCommits</c>.
/// </summary>
[NonParallelizable]
class ArchiveTornTailTests : ArchiveTestBase<ArchiveTornTailTests>
{
    private const string Marker = "BKP-10: a torn tail kept past the crash open";

    [Test]
    [CancelAfter(60_000)]
    [VerifiesRule("BKP-10")]
    public void ATornCrashThenASecondCrash_WithArchivingStalled_KeepsEveryCommit()
    {
        var (first, second, torn, tornId) = TornCrashThenSecondCrash(sealDisabled: false);
        long archivedThrough;
        using (var scope = ServiceProvider.CreateScope())
        {
            var dbe = Open(scope);
            AssertBothCommits(dbe, first, second);

            // The archiver, once it runs, archives the sealed segment up to its seal, flagged, and the segments after it.
            var archiver = NewArchiver(dbe);
            Switch(dbe);
            Tick(archiver, 0);
            Tick(archiver, 1);
            archivedThrough = archiver.ArchivedThroughLsn;
            var archived = Archived(dbe).Segments;
            var sealedCopy = archived.Single(s => s.SegmentId == tornId);
            Assert.That(sealedCopy.Footer.Flags & ArchiveFormat.FooterFlagTornTrimmed, Is.Not.Zero, "the sealed segment is archived as sealed at a crash");
            Assert.That(sealedCopy.Footer.Flags & ArchiveFormat.FooterFlagCorruptMidSegment, Is.Zero, "a sealed tail is no break");
            Assert.That(archived.Any(s => s.SegmentId > tornId && s.Footer.LastRecordLsn > 0), Is.True, "the later session's segments are archived after it");
            ForceCheckpoint(dbe);
            Assert.That(File.Exists(torn), Is.False, "archived, the torn segment is reclaimed by the next cycle");
        }

        // A restore across the sealed segment, from the point taken before the tear, holds both sessions' commits.
        var (result, versioned, _, _) = RestoreAndRead(PointSelector.AtLsn(archivedThrough), "across-the-seal");
        Assert.That(result.EffectiveLsn, Is.EqualTo(archivedThrough));
        Assert.That(versioned.ContainsKey(first) && versioned.ContainsKey(second), Is.True, $"{Marker}: a restore across the sealed segment");
    }

    /// <summary>The same crash with the seal skipped: the second recovery stops at the kept tear and the reopened session's commit is lost. Must be caught.</summary>
    [Test]
    [CancelAfter(60_000)]
    [RuleMutant("BKP-10")]
    public void Mutant_ACrashOpenThatDoesNotSealTheTornTail_IsReported()
    {
        var (first, second, _, _) = TornCrashThenSecondCrash(sealDisabled: true);
        using var scope = ServiceProvider.CreateScope();
        var dbe = Open(scope);
        RuleMutants.AssertDetects("BKP-10", Marker, () => AssertBothCommits(dbe, first, second));
    }

    /// <summary>
    /// A tear inside the last frame, mid-page: the crash open's sentinel lands mid-page, and the torn bytes after it stay on disk while the archived copy
    /// holds zeros there. Archived, then a crash before any cycle reclaims it: the next archiver's start compares the local segment with its copy over
    /// the bytes the archive keeps, and goes on archiving — not "another history".
    /// </summary>
    [Test]
    [CancelAfter(60_000)]
    public void ASealedTornTail_ArchivedThenACrashBeforeItsReclaim_IsStillTheArchivedSegment()
    {
        string walDir;
        using (var scope = ServiceProvider.CreateScope())
        {
            var dbe = Open(scope);
            walDir = WalDirectoryOf(dbe);
            CapturePoint(dbe, BackupKind.Full);

            // Deferred commits made faster than the writer flushes share a write: their frames pack into one page run, the later ones mid-page.
            for (var i = 0; i < 200; i++)
            {
                using var t = dbe.CreateQuickTransaction(i < 199 ? DurabilityMode.Deferred : DurabilityMode.Immediate);
                t.Spawn<CompAArch>(CompAArch.A.Set(new CompA(10 + i)));
                t.Commit();
            }

            dbe.SimulateHardCrash();
        }

        var torn = DataSegments(walDir)[^1];
        var frames = WalFrames.Read(File.ReadAllBytes(torn), out _);
        var last = frames.LastOrDefault(f => f.Start % 4096 != 0);
        Assert.That(last, Is.Not.Null, "premise: a frame starts mid-page");
        Tear(torn, last.Start + WalFrameHeader.SizeInBytes + 8);

        using (var scope = ServiceProvider.CreateScope())
        {
            var dbe = Open(scope);
            Assert.That((dbe.LastTornTailSeal.Sealed, dbe.LastTornTailSeal.Offset), Is.EqualTo((true, last.Start)), "premise: sealed mid-page");
            var archiver = NewArchiver(dbe);
            Tick(archiver, 0);
            Assert.That(Archived(dbe).Segments.Any(s => s.SegmentId == SegmentId(torn)), Is.True, "premise: the sealed segment is archived");
            dbe.SimulateHardCrash();
        }

        using (var scope = ServiceProvider.CreateScope())
        {
            var dbe = Open(scope);
            Assert.That(File.Exists(torn), Is.True, "premise: no cycle reclaimed the sealed segment before the crash");
            var archiver = NewArchiver(dbe);
            Tick(archiver, 0);
            Tick(archiver, 1);
            Assert.That(archiver.State.Condition, Is.EqualTo(WalArchiveCondition.Archiving), archiver.State.LastError);
        }
    }

    /// <summary>
    /// A tick fence torn inside its frame: recovery applies the valid chunks' fence records (a fence applies without a commit). If the seal's checkpoint
    /// does not complete, CheckpointLSN stays below them, and a sentinel over the frame would hide them from the next recovery, which replays the window
    /// without them: the earlier fence's values overwrite theirs. So that open fails, nothing sealed, and the next one replays them, consolidates and seals.
    /// </summary>
    [Test]
    [CancelAfter(60_000)]
    [VerifiesRule("BKP-10")]
    public void ATornFence_WhoseRecoveryCheckpointDoesNotComplete_IsNotSealedAndIsReplayedByTheNextOpen()
    {
        string walDir;
        var fenced = new List<EntityId>();
        using (var scope = ServiceProvider.CreateScope())
        {
            var dbe = Open(scope);
            walDir = WalDirectoryOf(dbe);
            CapturePoint(dbe, BackupKind.Full);

            // No cycle after the point: the fence reaches the data file only through a recovery's checkpoint.
            dbe.CheckpointManager.CycleFaultInjector = () => throw new WalBackPressureTimeoutException(0, TimeSpan.Zero);
            using (var t = dbe.CreateQuickTransaction(DurabilityMode.Immediate))
            {
                for (var i = 0; i < 4000; i++)
                {
                    fenced.Add(t.Spawn<SvTestArchetype>(SvTestArchetype.SvComp.Set(new CompSmSingleVersion(i))));
                }

                t.Commit();
            }

            // A SingleVersion update reaches the WAL only through a tick fence: two of them, each a multi-chunk fence frame. Replayed without the
            // second, the first one's values overwrite what the second one applied.
            for (var round = 1; round <= 2; round++)
            {
                using (var t = dbe.CreateQuickTransaction())
                {
                    foreach (var id in fenced)
                    {
                        t.OpenMut(id).Set(SvTestArchetype.SvComp, new CompSmSingleVersion(round == 1 ? 5 : -1));
                    }

                    t.Commit();
                }

                dbe.WriteTickFence(round);
            }

            var wal = dbe.WalManager;
            wal.RequestFlush();
            var ctx = WaitContext.FromTimeout(Budget);
            wal.WaitForDurable(wal.LastPublishedLsn, ref ctx);
            dbe.SimulateHardCrash();
        }

        var torn = DataSegments(walDir)[^1];
        var fence = WalFrames.Read(File.ReadAllBytes(torn), out _).LastOrDefault(f => f.Chunks >= 2 && f.Records.All(r => r.IsFence));
        Assert.That(fence, Is.Not.Null, "premise: the last segment holds a multi-chunk fence frame");
        Tear(torn, fence.End - 128);   // inside its last chunk: the chunks before it stay valid
        var tornBytes = File.ReadAllBytes(torn);

        // The crash open, its recovery's checkpoint failing every cycle: the seal's wait times out.
        var opened = false;
        var appliedByTheFirstRecovery = -1;
        using (var scope = ServiceProvider.CreateScope())
        {
            var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
            dbe.CheckpointManager.CycleFaultInjector = () => throw new WalBackPressureTimeoutException(0, TimeSpan.Zero);
            dbe.RecoverySealWaitForTest = TimeSpan.FromMilliseconds(200);
            dbe.RegisterComponentFromAccessor<CompA>();
            dbe.RegisterComponentFromAccessor<CompSmSingleVersion>();
            try
            {
                dbe.InitializeArchetypes();
                opened = true;
                appliedByTheFirstRecovery = CountUpdated(dbe, fenced);
                dbe.SimulateHardCrash();
            }
            catch (InvalidOperationException e)
            {
                Assert.That(e.Message, Does.Contain("cannot be sealed"));
            }

            Assert.That(dbe.LastWalV2RecoveryResult.StoppedAtCorruption, Is.True, "premise: the recovery met the tear");
            Assert.That(dbe.LastWalV2RecoveryCheckpointLsn, Is.LessThan(fence.Records.Min(r => r.Lsn)), "premise: the fence lies in the recovery window");
        }

        if (!opened)
        {
            Assert.That(File.ReadAllBytes(torn), Is.EqualTo(tornBytes), "the failed open sealed nothing");
        }

        using (var scope = ServiceProvider.CreateScope())
        {
            var dbe = Open(scope);
            var updated = CountUpdated(dbe, fenced);
            Assert.That(updated, Is.GreaterThan(0), "the torn frame's valid fence chunks, applied by the first recovery, are applied by this one");
            Assert.That(updated, Is.EqualTo(opened ? appliedByTheFirstRecovery : updated), "the second recovery applies what the first one did");
            Assert.That(dbe.LastTornTailSeal.Sealed, Is.True, $"an open whose checkpoint completed seals the tail ({dbe.LastTornTailSeal})");
        }

        Assert.That(opened, Is.False, "an open whose checkpoint did not cover the fence fails rather than seal");
    }

    private static int CountUpdated(DatabaseEngine dbe, List<EntityId> ids)
    {
        using var t = dbe.CreateQuickTransaction();
        return ids.Count(id => t.Open(id).Read(SvTestArchetype.SvComp).Value == -1);
    }

    [Test]
    [CancelAfter(60_000)]
    public void AMidLogBreak_IsLeftUnsealed()
    {
        string walDir;
        using (var scope = ServiceProvider.CreateScope())
        {
            var dbe = Open(scope);
            walDir = WalDirectoryOf(dbe);
            SpawnAndSeal(dbe, 10, 0);
            SpawnAndSeal(dbe, 10, 100);
            Spawn(dbe, 10, 200);
            dbe.SimulateHardCrash();
        }

        // Three segments: the open keeps the middle one past the break (its LSNs end below the last one's first), and deletes the last (its LSNs lie
        // past the recovered frontier).
        var segments = DataSegments(walDir);
        Assert.That(segments.Length, Is.GreaterThanOrEqualTo(3), "premise: two sealed segments and a later one with data");
        var broken = segments[0];
        var bytes = File.ReadAllBytes(broken);
        Tear(broken, FrameInside(bytes));
        var before = File.ReadAllBytes(broken);

        using (var scope = ServiceProvider.CreateScope())
        {
            var dbe = Open(scope);
            Assert.That(dbe.LastWalV2RecoveryResult.StoppedAtCorruption, Is.True, "premise: the recovery met the break");
            Assert.That((dbe.LastTornTailSeal.Sealed, dbe.LastTornTailSeal.SegmentId), Is.EqualTo((false, SegmentId(broken))),
                "a break followed by data is mid-log corruption, left as it is");
            Assert.That(File.ReadAllBytes(broken), Is.EqualTo(before), "nothing written over it");
        }
    }

    private (EntityId First, EntityId Second, string Torn, long TornId) TornCrashThenSecondCrash(bool sealDisabled)
    {
        EntityId first, second;
        string walDir;
        using (var scope = ServiceProvider.CreateScope())
        {
            var dbe = Open(scope);
            walDir = WalDirectoryOf(dbe);
            CapturePoint(dbe, BackupKind.Full);
            first = Spawn(dbe, 1, 1)[0];
            dbe.SimulateHardCrash();
        }

        // The torn block: the next 4 KiB block after the last durable one, its first bytes landed and the rest did not.
        var torn = DataSegments(walDir)[^1];
        var extent = WrittenExtent(File.ReadAllBytes(torn));
        Tear(torn, WalSegmentHeader.SizeInBytes + (extent - WalSegmentHeader.SizeInBytes + 4095) / 4096 * 4096);

        using (var scope = ServiceProvider.CreateScope())
        {
            var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
            dbe.TornTailSealDisabledForTest = sealDisabled;
            dbe.RegisterComponentFromAccessor<CompA>();
            dbe.RegisterComponentFromAccessor<CompSmSingleVersion>();
            dbe.InitializeArchetypes();
            Assert.That(dbe.LastWalV2RecoveryResult.StoppedAtCorruption, Is.True, "premise: the reopen met the tear");
            Assert.That(dbe.LastTornTailSeal.Sealed, Is.EqualTo(!sealDisabled));
            Assert.That(File.Exists(torn), Is.True, "premise: the archive floor kept the torn segment past the crash open");
            using (var t = dbe.CreateQuickTransaction())
            {
                Assert.That(t.IsAlive(first), Is.True, "premise: the record before the tear was recovered");
            }

            second = Spawn(dbe, 1, 2)[0];
            dbe.SimulateHardCrash();
        }

        return (first, second, torn, SegmentId(torn));
    }

    private static void AssertBothCommits(DatabaseEngine dbe, EntityId first, EntityId second)
    {
        var result = dbe.LastWalV2RecoveryResult;
        using var t = dbe.CreateQuickTransaction();
        Assert.That(t.IsAlive(first), Is.True, "the first session's commit");
        Assert.That(t.IsAlive(second), Is.True,
            $"{Marker}: the second session's commit was lost (recovery scanned {result.SegmentsScanned} segment(s), stopped at corruption = "
            + $"{result.StoppedAtCorruption})");
    }

    private static string[] DataSegments(string walDir) =>
        Directory.GetFiles(walDir, "*.wal").OrderBy(p => p, StringComparer.Ordinal).Where(p => WrittenExtent(File.ReadAllBytes(p)) > 0).ToArray();

    private static long SegmentId(string path) => long.Parse(Path.GetFileNameWithoutExtension(path));

    private static long WrittenExtent(byte[] bytes)
    {
        for (var i = bytes.Length - 1; i >= 0; i--)
        {
            if (bytes[i] != 0)
            {
                return i + 1;
            }
        }

        return 0;
    }

    /// <summary>Inside the first chunk of the first frame: its CRC no longer matches.</summary>
    private static long FrameInside(byte[] bytes) => WalSegmentHeader.SizeInBytes + WalFrameHeader.SizeInBytes + 8;

    private static void Tear(string path, long at)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.ReadWrite);
        Assert.That(at + 64, Is.LessThanOrEqualTo(fs.Length), "premise: the damage lies inside the segment");
        fs.Seek(at, SeekOrigin.Begin);
        fs.Write(Enumerable.Repeat((byte)0xA5, 64).ToArray(), 0, 64);
        fs.Flush(true);
    }
}
