using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using System;
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
