using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using System;
using System.IO;
using System.Linq;
using Typhon.Engine.Tests.Backup;

namespace Typhon.Engine.Tests;

/// <summary>
/// <c>verify</c> over the WAL archive (impl/1418 WA-26, §4.4): BKP-VER-15 a <c>.twal</c> unreadable, or at Full not decoding under its CRCs; 16 a timeline
/// whose files disagree with it or the store; 17 a gap (a warning) or two histories in one timeline; 18 a point's tail that is not the archive's history.
/// </summary>
[NonParallelizable]
class BackupVerifyArchiveTests : ArchiveTestBase<BackupVerifyArchiveTests>
{
    private Guid _timeline;
    private uint _point;

    private string TimelineDir => Path.Combine(StorePath, "wal", _timeline.ToString("D"));

    private string[] SegmentFiles => Directory.GetFiles(TimelineDir, "*.twal").OrderBy(f => f, StringComparer.Ordinal).ToArray();

    /// <summary>A full point, then four archived segments, the store closed.</summary>
    private void Build()
    {
        using var scope = ServiceProvider.CreateScope();
        var dbe = Open(scope);
        _timeline = dbe.TimelineId;
        var archiver = NewArchiver(dbe);
        Tick(archiver, 0);
        Spawn(dbe, 10, 9_000);   // the point's tail then holds frames, not only a segment header
        _point = CapturePoint(dbe, BackupKind.Full);
        for (var i = 0; i < 4; i++)
        {
            SpawnAndSeal(dbe, 20, 100 * i);
            Tick(archiver, i + 1);
        }

        Assert.That(SegmentFiles.Length, Is.GreaterThanOrEqualTo(4), "premise: the archive holds the segments");
    }

    private static VerifyReport Verify(VerifyLevel level) => BackupVerifier.VerifyStore(StorePath, level);

    private static string[] ArchiveCodes(VerifyReport report) =>
        report.StoreFindings.Where(f => f.Code is "BKP-VER-15" or "BKP-VER-16" or "BKP-VER-17" or "BKP-VER-18").Select(f => f.Code).Distinct().ToArray();

    [Test]
    [CancelAfter(30_000)]
    public void ACleanArchive_HasNoFinding()
    {
        Build();
        var report = Verify(VerifyLevel.Full);
        Assert.That(ArchiveCodes(report), Is.Empty, string.Join("; ", report.StoreFindings.Select(f => f.Message)));
        Assert.That(report.Verdict, Is.EqualTo(VerifyVerdict.Ok));
        Assert.That(report.BytesRead, Is.GreaterThan(SegmentFiles.Sum(f => new FileInfo(f).Length) - 1), "Full reads every archived byte");
    }

    [Test]
    [CancelAfter(30_000)]
    public void AByteFlippedInASegmentsData_IsDamageAtFull_AndABrokenHeader_AtQuick()
    {
        Build();
        var victim = SegmentFiles[1];
        var bytes = File.ReadAllBytes(victim);
        bytes[ArchiveFormat.HeaderSize + 40] ^= 0x5A;   // inside the first block's stored bytes
        File.WriteAllBytes(victim, bytes);
        Assert.That(ArchiveCodes(Verify(VerifyLevel.Quick)), Is.Empty, "Quick reads headers, footers and samples only");
        var full = Verify(VerifyLevel.Full);
        Assert.That(full.StoreFindings.Single(f => f.Code == "BKP-VER-15").Severity, Is.EqualTo(VerifySeverity.Damage));
        Assert.That(full.Verdict, Is.EqualTo(VerifyVerdict.Damaged));

        bytes[ArchiveFormat.HeaderSize + 40] ^= 0x5A;
        bytes[12] ^= 0x01;   // the header, under its CRC
        File.WriteAllBytes(victim, bytes);
        Assert.That(ArchiveCodes(Verify(VerifyLevel.Quick)), Does.Contain("BKP-VER-15"));
    }

    [Test]
    [CancelAfter(30_000)]
    public void ATimelineWithoutItsFile_IsReported()
    {
        Build();
        File.Delete(Path.Combine(TimelineDir, ArchiveFormat.TimelineFileName));
        var report = Verify(VerifyLevel.Quick);
        Assert.That(report.StoreFindings.Single(f => f.Code == "BKP-VER-16").Severity, Is.EqualTo(VerifySeverity.Damage));
    }

    [Test]
    [CancelAfter(30_000)]
    public void AGap_IsAWarning()
    {
        Build();
        File.Delete(SegmentFiles[1]);
        var report = Verify(VerifyLevel.Full);
        var gap = report.StoreFindings.Single(f => f.Code == "BKP-VER-17");
        Assert.That(gap.Severity, Is.EqualTo(VerifySeverity.Advisory));
        Assert.That(gap.Message, Does.Contain("Unknown").And.Contain("no restore crosses it"));
        Assert.That(report.Verdict, Is.EqualTo(VerifyVerdict.Warning));
    }

    [Test]
    [CancelAfter(30_000)]
    public void APointsTailThatIsAnotherHistory_IsDamage()
    {
        Build();
        var view = StoreView.Open(StorePath, trustCatalog: false);
        var pack = view.Find(_point).Original;
        using (var reader = PackReader.Open(StoreFileSystem.Instance, view.Paths.PackPath(pack.File)))
        {
            var first = reader.LoadTailDirectory().Segments[0];
            var path = Path.Combine(TimelineDir, ArchiveFormat.FileName(first.SegmentId));
            Assert.That(File.Exists(path), Is.True, "premise: the point's tail segment is archived");
            ArchivedSegmentInfo original;
            using (var r = ArchivedSegmentReader.Open(StoreFileSystem.Instance, path))
            {
                original = WalArchiveStore.Describe(Path.GetFileName(path), r);
            }

            File.Delete(path);
            var forged = ArchiveTestKit.Segment(first.SegmentId, first.FirstLsn, (int)original.Footer.ArchiveLength, original.Header.DeclaredSize, seed: 77);
            using var writer = new ArchivedSegmentWriter(null);
            using var t = WalArchiveTimeline.TryOpen(StoreFileSystem.Instance, StorePath, _timeline);
            t.Publish(writer, original.Header, forged.AsSpan(0, 4096), forged.AsSpan(4096), original.Footer, []);
        }

        var report = Verify(VerifyLevel.Quick);
        var finding = report.StoreFindings.Single(f => f.Code == "BKP-VER-18");
        Assert.That((finding.Severity, finding.Message.Contains($"point #{_point}")), Is.EqualTo((VerifySeverity.Damage, true)));
    }
}
