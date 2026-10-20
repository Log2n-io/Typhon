using NUnit.Framework;
using System;
using System.Buffers.Binary;
using System.Linq;

namespace Typhon.Engine.Tests.Backup;

/// <summary>
/// Verify (plan 1413 §2.11): one targeted damage per <c>BKP-VER</c> code, each reported under its own code, at the level that is supposed to
/// see it, with the points that depend on the damaged pack — and only those — not restorable.
/// </summary>
[TestFixture]
internal sealed class BackupVerifyTests
{
    private const string Root = "/s";

    /// <summary>Chain A: #1 full, #2 incremental. Chain B: #3 full. Every point carries the image's WAL as its tail.</summary>
    private static (InMemorySparseFileSystem Fs, BackupPointInfo[] Points) NewStore(bool suspectInB = false)
    {
        var image = BackupTestImages.Unclean;
        var fs = new InMemorySparseFileSystem();
        fs.CreateDirectory("/");
        using var store = BackupStore.OpenForWrite(Root, image.DatabaseId, true, fs);
        var a1 = StoreTestKit.WritePoint(store, image, withTail: true);
        var a2 = StoreTestKit.WritePoint(store, image, a1.ChainId, pages: [2, 3], withTail: true, @override: StoreTestKit.Salted(image, 2));
        BackupPointInfo b3;
        if (suspectInB)
        {
            var sink = store.BeginPoint(StoreTestKit.Plan(store, image));
            sink.SetImage(image.PageCount, image.CheckpointLsn, 1);
            for (var p = 0; p < image.PageCount; p++)
            {
                sink.AddPage(p, image.Pages[p], suspect: p == 3);
            }

            PackTestKit.AddTail(sink, image);
            b3 = sink.Complete(image.CheckpointLsn, 2);
        }
        else
        {
            b3 = StoreTestKit.WritePoint(store, image, withTail: true);
        }

        return (fs, [a1, a2, b3]);
    }

    private static string PathOf(BackupPointInfo p) => InMemorySparseFileSystem.Normalize(p.PackPath);

    private static VerifyReport Verify(InMemorySparseFileSystem fs, VerifyLevel level, VerifyOptions options = null) =>
        BackupVerifier.VerifyStore(Root, level, options, fs);

    private static void Flip(InMemorySparseFileSystem fs, string path, long offset)
    {
        var b = fs.ReadAll(path)[offset];
        fs.Poke(path, offset, [(byte)(b ^ 0x40)]);
    }

    private static void AssertOnly(VerifyReport report, string code, uint damagedPoint, params uint[] notRestorable)
    {
        var codes = report.AllFindings.Where(f => f.Severity == VerifySeverity.Damage).Select(f => f.Code).Distinct().ToArray();
        Assert.That(codes, Does.Contain(code), string.Join("\n", report.AllFindings));
        Assert.That(report.Points.Where(p => !p.Restorable).Select(p => p.Id), Is.EquivalentTo(notRestorable));
        if (damagedPoint != 0)
        {
            Assert.That(report.Packs.Single(p => p.BackupId == damagedPoint).Status, Is.Not.EqualTo(PackVerifyStatus.Ok));
            Assert.That(report.Packs.Where(p => p.BackupId != damagedPoint).All(p => p.Status == PackVerifyStatus.Ok), Is.True);
        }

        Assert.That(report.Verdict, Is.EqualTo(notRestorable.Length == report.Points.Count ? VerifyVerdict.NoRestorablePoint : VerifyVerdict.Damaged));
    }

    [TestCase(VerifyLevel.Quick)]
    [TestCase(VerifyLevel.Full)]
    [CancelAfter(15_000)]
    public void ASoundStore_IsOk(VerifyLevel level)
    {
        var (fs, _) = NewStore();
        var report = Verify(fs, level);

        Assert.That(report.AllFindings, Is.Empty);
        Assert.That(report.Verdict, Is.EqualTo(VerifyVerdict.Ok));
        Assert.That(report.Points.Select(p => (p.Id, p.Restorable)), Is.EqualTo(new[] { (1u, true), (2u, true), (3u, true) }));
        Assert.That(report.BytesRead, Is.GreaterThan(0));
        Assert.That(report.SkippedChecks, Is.Empty, "every check runs, BKP-VER-12 at Full included");
    }

    [Test]
    [CancelAfter(15_000)]
    public void Ver01_APackOfAnotherDatabase()
    {
        var (fs, _) = NewStore();
        var other = BackupTestImages.Clean;
        PackTestKit.Write(fs, Root + "/packs", other, PackTestKit.Identity(other, BackupKind.Full, 9), PackTestKit.Range(0, other.PageCount), false);

        AssertOnly(Verify(fs, VerifyLevel.Quick), "BKP-VER-01", 9, 9);
    }

    [Test]
    [CancelAfter(15_000)]
    public void Ver02_AHeaderFlip()
    {
        var (fs, points) = NewStore();
        Flip(fs, PathOf(points[0]), PackFormat.HBackupId);

        AssertOnly(Verify(fs, VerifyLevel.Quick), "BKP-VER-02", 1, 1, 2);
    }

    [Test]
    [CancelAfter(15_000)]
    public void Ver03_ATruncation()
    {
        var (fs, points) = NewStore();
        fs.Resize(PathOf(points[1]), fs.ReadAll(PathOf(points[1])).Length - 7);

        AssertOnly(Verify(fs, VerifyLevel.Quick), "BKP-VER-03", 2, 2);
    }

    [Test]
    [CancelAfter(15_000)]
    public void Ver04_AnIndexFlip()
    {
        var (fs, points) = NewStore();
        long at;
        using (var reader = PackReader.Open(fs, PathOf(points[0])))
        {
            at = reader.Footer.IndexOffset + PackFormat.PageEntrySize + 20;     // the second entry's Reserved field
        }

        Flip(fs, PathOf(points[0]), at);

        AssertOnly(Verify(fs, VerifyLevel.Quick), "BKP-VER-04", 1, 1, 2);
    }

    [Test]
    [CancelAfter(15_000)]
    public void Ver05_ATailDirectoryFlip()
    {
        var (fs, points) = NewStore();
        long at;
        using (var reader = PackReader.Open(fs, PathOf(points[2])))
        {
            at = reader.Footer.TailDirOffset + PackFormat.SectionPrefixSize + 8;  // the first segment's FirstLsn
        }

        Flip(fs, PathOf(points[2]), at);

        AssertOnly(Verify(fs, VerifyLevel.Quick), "BKP-VER-05", 3, 3);
    }

    [Test]
    [CancelAfter(15_000)]
    public void Ver06_AMissingLink()
    {
        var (fs, points) = NewStore();
        fs.DeleteFile(PathOf(points[0]));

        // #1 is no point any more; #2 needs it.
        var report = Verify(fs, VerifyLevel.Quick);
        AssertOnly(report, "BKP-VER-06", 0, 2);
        Assert.That(report.Points.Single(p => p.Id == 2).BrokenBy, Is.EqualTo(new uint[] { 1 }));
    }

    [Test]
    [CancelAfter(15_000)]
    public void Ver07_APointBelowItsCheckpoint()
    {
        var (fs, points) = NewStore();
        var path = PathOf(points[2]);
        var bytes = fs.ReadAll(path);
        var footerAt = bytes.Length - PackFormat.FooterSize;
        var footer = bytes.AsSpan(footerAt, PackFormat.FooterSize).ToArray();
        var c = BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(PackFormat.HCheckpointLsn));
        BinaryPrimitives.WriteInt64LittleEndian(footer.AsSpan(PackFormat.FPointLsn), c - 1);
        BinaryPrimitives.WriteUInt32LittleEndian(footer.AsSpan(PackFormat.FFooterCrc), Crc32CUtil.Compute(footer.AsSpan(0, PackFormat.FFooterCrc)));
        fs.Poke(path, footerAt, footer);

        AssertOnly(Verify(fs, VerifyLevel.Quick), "BKP-VER-07", 0, 3);
    }

    [Test]
    [CancelAfter(15_000)]
    public void Ver08_AnOrphanTemp_IsAdvisory()
    {
        var (fs, _) = NewStore();
        using (var f = fs.OpenCreateNew(Root + "/packs/00000004-I.tpack.tmp"))
        {
            f.Write([1], 0);
        }

        var report = Verify(fs, VerifyLevel.Quick);
        Assert.That(report.StoreFindings.Single().Code, Is.EqualTo("BKP-VER-08"));
        Assert.That(report.StoreFindings.Single().Severity, Is.EqualTo(VerifySeverity.Advisory));
        Assert.That(report.Verdict, Is.EqualTo(VerifyVerdict.Warning));
        Assert.That(report.Points.All(p => p.Restorable), Is.True);
    }

    [TestCase(VerifyLevel.Quick)]
    [TestCase(VerifyLevel.Full)]
    [CancelAfter(15_000)]
    public void Ver09_ATailBlockFlip(VerifyLevel level)
    {
        var (fs, points) = NewStore();
        long at;
        using (var reader = PackReader.Open(fs, PathOf(points[1])))
        {
            var block = reader.LoadTailDirectory().Blocks[0];
            at = block.DataOffset + (block.StoredLength / 2);
        }

        Flip(fs, PathOf(points[1]), at);

        AssertOnly(Verify(fs, level), "BKP-VER-09", 2, 2);
    }

    [Test]
    [CancelAfter(15_000)]
    public void Ver10_OnlyTheFileCrc_IsCaughtByFullAlone()
    {
        var (fs, points) = NewStore();
        var path = PathOf(points[2]);
        Flip(fs, path, fs.ReadAll(path).Length - 1);

        Assert.That(Verify(fs, VerifyLevel.Quick).Verdict, Is.EqualTo(VerifyVerdict.Ok), "quick does not read every byte");
        AssertOnly(Verify(fs, VerifyLevel.Full), "BKP-VER-10", 3, 3);
    }

    [Test]
    [CancelAfter(15_000)]
    public void Ver11_APageFlip_IsCaughtByFull()
    {
        var (fs, points) = NewStore();
        long at;
        using (var reader = PackReader.Open(fs, PathOf(points[1])))
        {
            var e = reader.LoadIndex().First(x => x.PageId >= 2 && x.StoredLength > 0);
            at = e.DataOffset + (e.StoredLength / 2);
        }

        Flip(fs, PathOf(points[1]), at);

        var report = Verify(fs, VerifyLevel.Full);
        AssertOnly(report, "BKP-VER-11", 2, 2);
        Assert.That(report.AllFindings.Single(f => f.Code == "BKP-VER-11").Message, Does.Contain("1 page(s)"));
    }

    [Test]
    [CancelAfter(15_000)]
    public void Ver12_ATailThatNoLongerYieldsItsPoint()
    {
        var (fs, _) = NewStore();
        var options = new VerifyOptions { TailInspector = (pack, _, _) => pack.Header.BackupId == 2 ? pack.Footer.PointLsn + 1 : pack.Footer.PointLsn };

        var report = Verify(fs, VerifyLevel.Full, options);
        AssertOnly(report, "BKP-VER-12", 2, 2);
        Assert.That(report.SkippedChecks, Is.Empty);
    }

    [Test]
    [CancelAfter(15_000)]
    public void Ver13_AHeaderThatDisagreesWithItsImage()
    {
        var (fs, points) = NewStore();
        var path = PathOf(points[2]);
        var header = fs.ReadAll(path).AsSpan(0, PackFormat.HeaderSize).ToArray();
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(PackFormat.HFormatRevision), 6);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(PackFormat.HHeaderCrc), Crc32CUtil.Compute(header.AsSpan(0, PackFormat.HHeaderCrc)));
        fs.Poke(path, 0, header);

        Assert.That(Verify(fs, VerifyLevel.Quick).AllFindings.Where(f => f.Code != "BKP-VER-10"), Is.Empty, "quick does not compare the meta pair");
        AssertOnly(Verify(fs, VerifyLevel.Full), "BKP-VER-13", 3, 3);
    }

    [Test]
    [CancelAfter(15_000)]
    public void Ver14_ASuspectPoint_IsRestorable_WithAWarning()
    {
        var (fs, _) = NewStore(suspectInB: true);

        var report = Verify(fs, VerifyLevel.Full);
        var finding = report.AllFindings.Single();
        Assert.That((finding.Code, finding.Severity), Is.EqualTo(("BKP-VER-14", VerifySeverity.Advisory)));
        Assert.That(finding.Message, Does.Contain("3"));
        Assert.That(report.Points.All(p => p.Restorable), Is.True);
        Assert.That(report.Verdict, Is.EqualTo(VerifyVerdict.Warning));
    }

    [Test]
    [CancelAfter(15_000)]
    public void VerifyPoint_ReadsOnlyItsChain()
    {
        var (fs, points) = NewStore();
        Flip(fs, PathOf(points[2]), PackFormat.HBackupId);

        var report = BackupVerifier.VerifyPoint(Root, PointSelector.ById(2), VerifyLevel.Full, null, fs);
        Assert.That(report.Packs.Select(p => p.BackupId), Is.EquivalentTo(new uint[] { 1, 2 }));
        Assert.That(report.Points.Single().Id, Is.EqualTo(2u));
        Assert.That(report.Verdict, Is.EqualTo(VerifyVerdict.Ok));
    }

    [Test]
    [CancelAfter(15_000)]
    public void AnEmptyStore_HasNoRestorablePoint()
    {
        var fs = new InMemorySparseFileSystem();
        fs.CreateDirectory("/");
        BackupStore.OpenForWrite(Root, Guid.NewGuid(), true, fs).Dispose();

        Assert.That(Verify(fs, VerifyLevel.Quick).Verdict, Is.EqualTo(VerifyVerdict.NoRestorablePoint));
    }
}
