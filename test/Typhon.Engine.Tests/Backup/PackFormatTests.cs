using NUnit.Framework;
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;

namespace Typhon.Engine.Tests.Backup;

/// <summary>
/// The <c>.tpack</c> writer and reader (plan 1413 §2.2–2.4): every page class round-trips byte-exactly, the index comes out sorted whatever the
/// arrival order, the tail comes back as the bytes that went in, and a reader refuses what it cannot read.
/// </summary>
[TestFixture]
internal sealed class PackFormatTests
{
    private const string Dir = "/s/packs";

    private static InMemorySparseFileSystem NewFs()
    {
        var fs = new InMemorySparseFileSystem();
        fs.CreateDirectory(Dir);
        return fs;
    }

    private static Dictionary<uint, byte[]> ReadAllPages(InMemorySparseFileSystem fs, string path, out PackPageEntry[] index)
    {
        using var reader = PackReader.Open(fs, path);
        index = reader.LoadIndex();
        var pages = new Dictionary<uint, byte[]>();
        foreach (var e in index)
        {
            var page = new byte[PackFormat.PageSize];
            reader.ReadPage(e, page);
            pages[e.PageId] = page;
        }

        Assert.That(reader.VerifyFileCrc(), Is.True, "FileCRC");
        return pages;
    }

    [Test]
    [CancelAfter(15_000)]
    public void AFullPoint_RoundTripsEveryPage_AndItsHeader()
    {
        var image = BackupTestImages.Clean;
        var fs = NewFs();
        var info = PackTestKit.Write(fs, Dir, image, PackTestKit.Identity(image, BackupKind.Full, 1), PackTestKit.Range(0, image.PageCount),
            withTail: false);

        Assert.That(info.PackPath, Is.EqualTo(Dir + "/00000001-F.tpack"));
        Assert.That(fs.FileExists(Dir + "/00000001-F.tpack.tmp"), Is.False);
        Assert.That(info.PageCount, Is.EqualTo(image.PageCount));

        var pages = ReadAllPages(fs, info.PackPath, out var index);
        Assert.That(index.Select(e => (int)e.PageId), Is.EqualTo(Enumerable.Range(0, image.PageCount)));
        for (var p = 0; p < image.PageCount; p++)
        {
            Assert.That(pages[(uint)p], Is.EqualTo(image.Pages[p]), $"page {p}");
        }

        using var reader = PackReader.Open(fs, info.PackPath);
        var h = reader.Header;
        Assert.That((h.Kind, h.BackupId, h.ParentId, h.BaseId, h.ImagePages), Is.EqualTo((BackupKind.Full, 1u, 0u, 1u, image.PageCount)));
        Assert.That((h.DatabaseId, h.CheckpointLsn, h.DatabaseFormatRevision, h.EngineVersion),
            Is.EqualTo((image.DatabaseId, image.CheckpointLsn, image.FormatRevision, "test-1.0")));
        Assert.That(reader.Footer.PointLsn, Is.EqualTo(image.CheckpointLsn));
        Assert.That(reader.LoadTailDirectory().Segments, Is.Empty);
        Assert.That(reader.LoadSuspects(), Is.Empty);
    }

    [Test]
    [CancelAfter(15_000)]
    public void AnIncremental_OfTheMetaPairOnly_RoundTrips()
    {
        var image = BackupTestImages.Clean;
        var fs = NewFs();
        var info = PackTestKit.Write(fs, Dir, image, PackTestKit.Identity(image, BackupKind.Incremental, 2, parent: 1, baseId: 1), [], withTail: false);

        var pages = ReadAllPages(fs, info.PackPath, out var index);
        Assert.That(index.Select(e => e.PageId), Is.EqualTo(new uint[] { 0, 1 }));
        Assert.That(pages[0], Is.EqualTo(image.Pages[0]));
        Assert.That(pages[1], Is.EqualTo(image.Pages[1]));
    }

    [Test]
    [CancelAfter(15_000)]
    public void AZeroPage_HasNoFrame_ButANearlyZeroPageKeepsItsLastByte()
    {
        var image = BackupTestImages.Clean;
        var fs = NewFs();
        const int n = 64;
        var nearlyZero = new byte[PackFormat.PageSize];
        nearlyZero[^1] = 0x5A;
        var info = PackTestKit.Write(fs, Dir, image, PackTestKit.Identity(image, BackupKind.Incremental, 2, 1, 1), [40, 41], false, imagePages: n,
            @override: p => p == 40 ? new byte[PackFormat.PageSize] : nearlyZero);

        var pages = ReadAllPages(fs, info.PackPath, out var index);
        var zero = index.Single(e => e.PageId == 40);
        Assert.That((zero.StoredLength, zero.DataOffset, zero.PageCrc), Is.EqualTo((0u, 0L, PackFormat.ZeroPageCrc)));
        Assert.That(pages[40], Is.EqualTo(new byte[PackFormat.PageSize]));
        Assert.That(index.Single(e => e.PageId == 41).StoredLength, Is.Not.Zero);
        Assert.That(pages[41], Is.EqualTo(nearlyZero), "a page that is zero except its last byte must come back with it");
    }

    [Test]
    [CancelAfter(15_000)]
    public void AnIncompressiblePage_IsStoredRaw_AndRoundTrips()
    {
        var image = BackupTestImages.Clean;
        var fs = NewFs();
        var random = new byte[PackFormat.PageSize];
        new Random(7).NextBytes(random);
        var info = PackTestKit.Write(fs, Dir, image, PackTestKit.Identity(image, BackupKind.Incremental, 2, 1, 1), [5], false, imagePages: 16,
            @override: _ => random);

        var pages = ReadAllPages(fs, info.PackPath, out var index);
        Assert.That(index.Single(e => e.PageId == 5).StoredLength, Is.EqualTo((uint)PackFormat.PageSize));
        Assert.That(pages[5], Is.EqualTo(random));
        Assert.That(index.Single(e => e.PageId == 0).StoredLength, Is.LessThan((uint)PackFormat.PageSize), "a meta page compresses");
    }

    [Test]
    [CancelAfter(15_000)]
    public void PagesArrivingOutOfOrder_AreIndexedInOrder_ThroughSpilledRuns()
    {
        var image = BackupTestImages.Clean;
        var fs = NewFs();
        const int n = 3_000;
        // Scanner order with copy-before-write strays: every 7th page arrives early, out of order.
        var strays = Enumerable.Range(2, n - 2).Where(p => p % 7 == 3).Reverse().ToList();
        var order = new List<int>();
        var strayIndex = 0;
        foreach (var p in Enumerable.Range(2, n - 2).Where(p => p % 7 != 3))
        {
            order.Add(p);
            if (p % 50 == 0 && strayIndex < strays.Count)
            {
                order.Add(strays[strayIndex++]);
            }
        }

        order.AddRange(strays.Skip(strayIndex));
        var options = new PackWriterOptions { IndexHeapEntries = 64 };
        var writer = PackTestKit.Begin(fs, Dir, PackTestKit.Identity(image, BackupKind.Full, 1), options);
        writer.SetImage(n, image.CheckpointLsn, 1);
        writer.AddPage(1, image.Pages[1], false);
        foreach (var p in order)
        {
            writer.AddPage(p, PackTestKit.PageOf(image, p), false);
        }

        Assert.That(fs.AllFiles.Count(f => f.Contains(".tpack.tmp.idx")), Is.GreaterThan(1), "runs must have spilled");
        writer.AddPage(0, image.Pages[0], false);
        var info = writer.Complete(image.CheckpointLsn, 2);

        Assert.That(fs.AllFiles.Where(f => f.Contains(".tmp")), Is.Empty, "sidecars are deleted once the index is written");
        var pages = ReadAllPages(fs, info.PackPath, out var index);
        Assert.That(index.Select(e => (int)e.PageId), Is.EqualTo(Enumerable.Range(0, n)));
        foreach (var p in new[] { 0, 1, 3, 10, 17, 2_999 })
        {
            Assert.That(pages[(uint)p], Is.EqualTo(PackTestKit.PageOf(image, p)), $"page {p}");
        }
    }

    [Test]
    [CancelAfter(15_000)]
    public void TheTail_ComesBackAsTheBytesThatWentIn_WhenInterleavedWithPages()
    {
        var image = BackupTestImages.Unclean;
        Assert.That(image.WalSegments, Is.Not.Empty);
        var fs = NewFs();
        var writer = PackTestKit.Begin(fs, Dir, PackTestKit.Identity(image, BackupKind.Full, 1));
        writer.SetImage(image.PageCount, image.CheckpointLsn, 1);
        var next = 0;
        foreach (var s in image.WalSegments)
        {
            writer.BeginTailSegment(s.SegmentId, s.FirstLsn, s.SegmentSize);
            for (var pos = 0; pos < s.Bytes.Length; pos += 5_000)
            {
                writer.AppendTail(s.Bytes.AsSpan(pos, Math.Min(5_000, s.Bytes.Length - pos)));
                if (next < image.PageCount)
                {
                    writer.AddPage(next, image.Pages[next], false);
                    next++;
                }
            }

            writer.EndTailSegment();
        }

        for (; next < image.PageCount; next++)
        {
            writer.AddPage(next, image.Pages[next], false);
        }

        var info = writer.Complete(image.CheckpointLsn, 2);

        using var reader = PackReader.Open(fs, info.PackPath);
        var dir = reader.LoadTailDirectory();
        Assert.That(dir.Segments.Select(s => s.SegmentId), Is.EqualTo(image.WalSegments.Select(s => s.SegmentId)));
        for (var i = 0; i < dir.Segments.Length; i++)
        {
            var target = NewFs();
            using (var file = target.OpenCreateNew(Dir + "/seg"))
            {
                var declared = reader.ReadTailSegment(dir, i, file);
                Assert.That(declared, Is.EqualTo(image.WalSegments[i].SegmentSize));
            }

            Assert.That(target.ReadAll(Dir + "/seg"), Is.EqualTo(image.WalSegments[i].Bytes), $"segment {i}");
        }

        Assert.That(reader.VerifyFileCrc(), Is.True);
    }

    [Test]
    [CancelAfter(15_000)]
    public void CaptureBugs_AreRefused_AndAbortLeavesNoFile()
    {
        var image = BackupTestImages.Clean;
        var unclean = BackupTestImages.Unclean;
        var fs = NewFs();

        Expect(w => w.AddPage(image.PageCount, image.Pages[0], false), "outside the image");
        Expect(w =>
        {
            w.AddPage(3, image.Pages[3], false);
            w.AddPage(3, image.Pages[3], false);
        }, "added twice");
        Expect(w =>
        {
            w.AddPage(0, image.Pages[0], false);
            w.AddPage(1, image.Pages[1], false);
            w.Complete(image.CheckpointLsn, 0);
        }, "holds 2 of");
        Expect(w =>
        {
            foreach (var p in Enumerable.Range(0, image.PageCount))
            {
                w.AddPage(p, image.Pages[p], false);
            }

            w.Complete(image.CheckpointLsn - 1, 0);
        }, "below CheckpointLSN");

        // An unclean image with no WAL segment would open without recovery (DC-3).
        var writer = PackTestKit.Begin(fs, Dir, PackTestKit.Identity(unclean, BackupKind.Full, 9));
        writer.SetImage(unclean.PageCount, unclean.CheckpointLsn, 1);
        foreach (var p in Enumerable.Range(0, unclean.PageCount))
        {
            writer.AddPage(p, unclean.Pages[p], false);
        }

        var ex = Assert.Throws<InvalidOperationException>(() => writer.Complete(unclean.CheckpointLsn, 0));
        Assert.That(ex.Message, Does.Contain("at least one WAL segment"));
        writer.Abort();
        writer.Abort();
        Assert.That(fs.AllFiles, Is.Empty);
        return;

        void Expect(Action<PackWriter> act, string marker)
        {
            var w = PackTestKit.Begin(fs, Dir, PackTestKit.Identity(image, BackupKind.Full, 1));
            w.SetImage(image.PageCount, image.CheckpointLsn, 1);
            var e = Assert.Throws<InvalidOperationException>(() => act(w));
            Assert.That(e.Message, Does.Contain(marker));
            w.Abort();
            Assert.That(fs.AllFiles, Is.Empty, $"abort after '{marker}' must leave no temp");
        }
    }

    [Test]
    [CancelAfter(15_000)]
    public void AMetaPairOfAnotherCheckpoint_IsRefused()
    {
        var image = BackupTestImages.Clean;
        var fs = NewFs();
        var writer = PackTestKit.Begin(fs, Dir, PackTestKit.Identity(image, BackupKind.Incremental, 2, 1, 1));
        writer.SetImage(image.PageCount, image.CheckpointLsn + 1, 1);
        writer.AddPage(0, image.Pages[0], false);
        writer.AddPage(1, image.Pages[1], false);
        var ex = Assert.Throws<InvalidOperationException>(() => writer.Complete(image.CheckpointLsn + 1, 0));
        Assert.That(ex.Message, Does.Contain("CheckpointLSN"));
        writer.Abort();
    }

    [Test]
    [CancelAfter(15_000)]
    public void AbortAfterComplete_KeepsThePack()
    {
        var image = BackupTestImages.Clean;
        var fs = NewFs();
        var writer = PackTestKit.Begin(fs, Dir, PackTestKit.Identity(image, BackupKind.Incremental, 2, 1, 1));
        writer.SetImage(image.PageCount, image.CheckpointLsn, 1);
        writer.AddPage(0, image.Pages[0], false);
        writer.AddPage(1, image.Pages[1], false);
        var info = writer.Complete(image.CheckpointLsn, 0);
        writer.Abort();

        Assert.That(fs.FileExists(info.PackPath), Is.True);
    }

    [TestCase(PackFormat.HFormatVersion, (byte)2, "FormatVersion")]
    [TestCase(PackFormat.HCompression, (byte)2, "Compression")]
    [CancelAfter(15_000)]
    public void AReader_RefusesAFormatItDoesNotKnow(int offset, byte value, string what)
    {
        var image = BackupTestImages.Clean;
        var fs = NewFs();
        var info = PackTestKit.Write(fs, Dir, image, PackTestKit.Identity(image, BackupKind.Incremental, 2, 1, 1), [], false);
        var header = new byte[PackFormat.HeaderSize];
        using (var file = fs.OpenRead(info.PackPath))
        {
            file.ReadExactly(header, 0);
        }

        // A NEWER writer's valid header, not a damaged one: the CRC is right.
        header[offset] = value;
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(PackFormat.HHeaderCrc), Crc32CUtil.Compute(header.AsSpan(0, PackFormat.HHeaderCrc)));
        fs.Poke(info.PackPath, 0, header);

        var ex = Assert.Throws<BackupFormatUnsupportedException>(() => PackReader.Open(fs, info.PackPath).Dispose());
        Assert.That(ex.Message, Does.Contain(what));
    }

    [Test]
    [CancelAfter(15_000)]
    public void UnknownFlagBits_AreIgnored()
    {
        var image = BackupTestImages.Clean;
        var fs = NewFs();
        var info = PackTestKit.Write(fs, Dir, image, PackTestKit.Identity(image, BackupKind.Incremental, 2, 1, 1), [], false);
        var header = new byte[PackFormat.HeaderSize];
        using (var file = fs.OpenRead(info.PackPath))
        {
            file.ReadExactly(header, 0);
        }

        header[PackFormat.HFlags] |= 0x80;
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(PackFormat.HHeaderCrc), Crc32CUtil.Compute(header.AsSpan(0, PackFormat.HHeaderCrc)));
        fs.Poke(info.PackPath, 0, header);

        using var reader = PackReader.Open(fs, info.PackPath);
        Assert.That(reader.LoadIndex(), Has.Length.EqualTo(2));
    }

    [Test]
    [CancelAfter(15_000)]
    public void OffsetsPastFourGiB_AreAddressed()
    {
        var image = BackupTestImages.Unclean;
        var fs = NewFs();
        var options = new PackWriterOptions { DataGapBytes = (4L << 30) + 4096 };
        var writer = PackTestKit.Begin(fs, Dir, PackTestKit.Identity(image, BackupKind.Full, 1), options);
        writer.SetImage(image.PageCount, image.CheckpointLsn, 1);
        foreach (var p in Enumerable.Range(0, image.PageCount))
        {
            writer.AddPage(p, image.Pages[p], false);
        }

        PackTestKit.AddTail(writer, image);
        var info = writer.Complete(image.CheckpointLsn, 2);

        using var reader = PackReader.Open(fs, info.PackPath);
        Assert.That(reader.Footer.IndexOffset, Is.GreaterThan(uint.MaxValue));
        var index = reader.LoadIndex();
        Assert.That(index.Where(e => e.StoredLength > 0).Min(e => e.DataOffset), Is.GreaterThan(uint.MaxValue));
        var page = new byte[PackFormat.PageSize];
        foreach (var e in index)
        {
            reader.ReadPage(e, page);
            Assert.That(page, Is.EqualTo(image.Pages[e.PageId]), $"page {e.PageId}");
        }

        var dir = reader.LoadTailDirectory();
        Assert.That(dir.Blocks.Min(b => b.DataOffset), Is.GreaterThan(uint.MaxValue));
        for (var i = 0; i < dir.Segments.Length; i++)
        {
            reader.ReadTailSegment(dir, i, null);
        }

        // FileCRC is not streamed here: reading 4 GiB of hole costs seconds; the writer's FileCRC over the gap is AppendZeros, tested apart.
    }

    [Test]
    [CancelAfter(15_000)]
    public void OneFlippedByte_FailsTheFileCrc()
    {
        var image = BackupTestImages.Clean;
        var fs = NewFs();
        var info = PackTestKit.Write(fs, Dir, image, PackTestKit.Identity(image, BackupKind.Full, 1), PackTestKit.Range(0, image.PageCount), false);
        var bytes = fs.ReadAll(info.PackPath);
        fs.Poke(info.PackPath, bytes.Length / 2, [(byte)(bytes[bytes.Length / 2] ^ 0x10)]);

        using var reader = PackReader.Open(fs, info.PackPath);
        Assert.That(reader.VerifyFileCrc(), Is.False);
    }
}
