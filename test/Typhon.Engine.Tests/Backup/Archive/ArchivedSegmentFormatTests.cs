using NUnit.Framework;
using System;
using System.Linq;

namespace Typhon.Engine.Tests.Backup;

/// <summary>The <c>.twal</c> format round trip (impl/1418 §3.5): what the writer puts down, the reader reads back, byte for byte and field for field.</summary>
[TestFixture]
internal sealed class ArchivedSegmentFormatTests
{
    private const string Root = "/s";

    private static InMemorySparseFileSystem NewFs()
    {
        var fs = new InMemorySparseFileSystem();
        fs.CreateDirectory("/");
        fs.CreateDirectory(Root);
        return fs;
    }

    [TestCase(8 * 1024)]
    [TestCase(PackFormat.TailBlockSize)]
    [TestCase(PackFormat.TailBlockSize * 2 + 512 * 1024)]
    [CancelAfter(10_000)]
    public void ASegment_RoundTrips_HeaderFooterSamplesAndBytes(int archiveLength)
    {
        var fs = NewFs();
        var segment = ArchiveTestKit.Segment(7, 1_000, archiveLength, declaredSize: 4 << 20, prevSegmentLsn: 999);
        var samples = ArchiveTestKit.Samples(1_000, 1_300, 25);
        var (outcome, info) = ArchiveTestKit.Publish(fs, Root, segment, 1_300, samples);
        Assert.That(outcome, Is.EqualTo(ArchivePublishOutcome.Published));

        var path = new WalArchivePaths(Root).Segment(ArchiveTestKit.Timeline, 7);
        Assert.That(info.File, Is.EqualTo("0000000000000007.twal"));
        Assert.That(ArchiveTestKit.FullCheck(fs, path, segment), Is.Null);
        using var reader = ArchivedSegmentReader.Open(fs, path);
        Assert.That((reader.Header.SegmentId, reader.Header.FirstLsn, reader.Header.DeclaredSize, reader.Header.PrevSegmentLsn),
            Is.EqualTo((7L, 1_000L, 4u << 20, 999L)));
        Assert.That((reader.Header.DatabaseId, reader.Header.StoreId, reader.Header.TimelineId),
            Is.EqualTo((ArchiveTestKit.Database, ArchiveTestKit.Store, ArchiveTestKit.Timeline)));
        Assert.That((reader.Footer.ArchiveLength, reader.Footer.LastLsn, reader.Footer.RawCrc), Is.EqualTo(((long)archiveLength, 1_300L, Crc32CUtil.Compute(segment))));
        Assert.That(reader.ReadSamples(), Is.EqualTo(samples));
        var blocks = reader.ReadBlockDirectory();
        Assert.That(blocks.Length, Is.EqualTo((archiveLength + PackFormat.TailBlockSize - 1) / PackFormat.TailBlockSize));
        Assert.That(blocks.Sum(b => (long)b.RawLength), Is.EqualTo(archiveLength));
        Assert.That(info.FirstSampleLsn == samples[0].Lsn && info.LastSampleLsn == samples[^1].Lsn, Is.True);
        Assert.That(reader.FileLength, Is.EqualTo(info.FileLength));
    }

    [Test]
    [CancelAfter(10_000)]
    public void GapAndDroppedFields_AndFlags_RoundTrip()
    {
        var fs = NewFs();
        var segment = ArchiveTestKit.Segment(3, 500, 8 * 1024);
        using var writer = new ArchivedSegmentWriter(null);
        using var t = WalArchiveTimeline.TryOpen(fs, Root, ArchiveTestKit.Timeline);
        var footer = ArchiveTestKit.Footer(600);
        footer.Flags = ArchiveFormat.FooterFlagGapBefore | ArchiveFormat.FooterFlagDroppedApplicable;
        footer.GapFromLsn = 401;
        footer.GapToLsn = 499;
        footer.GapReason = ArchiveGapReason.BacklogReleased;
        footer.DroppedFromLsn = 590;
        footer.DroppedToLsn = 595;
        footer.LastRecordLsn = 589;
        footer.LastUnitLsn = 580;
        var (_, info) = t.Publish(writer, ArchiveTestKit.Header(segment), segment.AsSpan(0, 4096), segment.AsSpan(4096), footer, []);
        using var reader = ArchivedSegmentReader.Open(fs, new WalArchivePaths(Root).Segment(ArchiveTestKit.Timeline, 3));
        var f = reader.Footer;
        Assert.That((f.Flags, f.GapFromLsn, f.GapToLsn, f.GapReason), Is.EqualTo((footer.Flags, 401L, 499L, ArchiveGapReason.BacklogReleased)));
        Assert.That((f.DroppedFromLsn, f.DroppedToLsn, f.LastRecordLsn, f.LastUnitLsn), Is.EqualTo((590L, 595L, 589L, 580L)));
        Assert.That(f.GapBefore && info.Footer.GapBefore, Is.True);
        Assert.That(reader.ReadSamples(), Is.Empty);
    }

    [Test]
    [CancelAfter(10_000)]
    public void TheWriter_RefusesBytesThatAreNotWholePagesOfTheSegment()
    {
        var fs = NewFs();
        var segment = ArchiveTestKit.Segment(3, 500, 8 * 1024, declaredSize: 8 * 1024);
        using var writer = new ArchivedSegmentWriter(null);
        using var file = fs.OpenCreateNew(Root + "/x.tmp");
        Assert.Throws<InvalidOperationException>(() => writer.Complete(file, ArchiveTestKit.Header(segment), segment.AsSpan(0, 4096),
            segment.AsSpan(4096, 100), ArchiveTestKit.Footer(600), []));
        var longer = ArchiveTestKit.Segment(3, 500, 12 * 1024, declaredSize: 8 * 1024);
        Assert.Throws<InvalidOperationException>(() => writer.Complete(file, ArchiveTestKit.Header(longer), longer.AsSpan(0, 4096), longer.AsSpan(4096),
            ArchiveTestKit.Footer(600), []));
    }

    [Test]
    public void FileNames_ParseBack_AndTempsAreRecognised()
    {
        Assert.That(ArchiveFormat.TryParseFileName("0000000000000042.twal", out var id, out var temp) && id == 42 && !temp, Is.True);
        Assert.That(ArchiveFormat.TryParseFileName("0000000000000042.twal.tmp", out id, out temp) && id == 42 && temp, Is.True);
        Assert.That(ArchiveFormat.TryParseFileName("42.twal", out _, out _), Is.False);
        Assert.That(ArchiveFormat.TryParseFileName("0000000000000042.wal", out _, out _), Is.False);
        Assert.That(ArchiveFormat.TryParseFileName("archive-index.bin", out _, out _), Is.False);
    }
}
