using NUnit.Framework;
using System;
using System.Linq;

namespace Typhon.Engine.Tests.Backup;

/// <summary>
/// P1: reading a pack in page order through a small window, with copy-before-write pre-images written out of place, reads each byte about once.
/// The window used to reload around every out-of-place frame — once to reach it, once to come back — so bytes read grew with the window size times
/// the stray count.
/// </summary>
[TestFixture]
internal sealed class FrameReaderTests
{
    private const string Dir = "/s/packs";
    private const int Pages = 512;

    [Test]
    [CancelAfter(30_000)]
    public void OutOfPlaceFrames_ReadAboutOnce_NotAWindowEach()
    {
        var fs = new InMemorySparseFileSystem();
        fs.CreateDirectory(Dir);
        var image = BackupTestImages.Clean;
        var random = new Random(1413);
        var pages = Enumerable.Range(0, Pages).ToDictionary(p => p, _ =>
        {
            var bytes = new byte[PackFormat.PageSize];
            random.NextBytes(bytes);   // incompressible: every frame is a whole page
            return bytes;
        });

        // Ascending, except every eighth page arrives last: a pre-image copied before the scanner reached it lands where it arrived.
        var stray = Enumerable.Range(2, Pages - 2).Where(p => p % 8 == 0).ToHashSet();
        var order = Enumerable.Range(2, Pages - 2).Where(p => !stray.Contains(p)).Concat(stray);
        var path = PackTestKit.Write(fs, Dir, image, PackTestKit.Identity(image, BackupKind.Full, 1), order, withTail: false, imagePages: Pages,
            @override: p => pages[p]).PackPath;

        using var pack = PackReader.Open(fs, path);
        var dataBytes = pack.Footer.DataRegionEnd - PackFormat.HeaderSize;
        var before = pack.BytesRead;
        using (var target = fs.OpenCreateNew("/t.data"))
        {
            ChainMaterializer.Materialize([pack], target, new MaterializeOptions { ReadBudgetBytes = 64 << 10 });
        }

        var read = pack.BytesRead - before;
        Assert.That(read, Is.LessThan(dataBytes * 3 / 2),
            $"read {read:N0} bytes for a {dataBytes:N0}-byte data region with {stray.Count} out-of-place frames and a 64 KiB window");
        Assert.That(fs.ReadAll("/t.data").AsSpan(8 * PackFormat.PageSize, PackFormat.PageSize).SequenceEqual(pages[8]), Is.True, "and reads them right");
    }

    /// <summary>
    /// DL-2: the stored frame a compaction copies is the pack's own bytes at the entry's offset, prefix included — through the window, a slide, and an
    /// out-of-place read alike — and it decodes to the page it was written from.
    /// </summary>
    [Test]
    [CancelAfter(30_000)]
    public void TryReadFrame_ReturnsThePacksOwnBytes_InAndOutOfTheWindow()
    {
        var fs = new InMemorySparseFileSystem();
        fs.CreateDirectory(Dir);
        var image = BackupTestImages.Clean;
        var random = new Random(1415);
        var pages = Enumerable.Range(0, 64).ToDictionary(p => p, p =>
        {
            var bytes = new byte[PackFormat.PageSize];
            random.NextBytes(bytes.AsSpan(0, p % 2 == 0 ? PackFormat.PageSize : 512));   // raw and LZ4 frames
            return bytes;
        });
        var order = Enumerable.Range(2, 62).Where(p => p % 7 != 0).Concat(Enumerable.Range(2, 62).Where(p => p % 7 == 0));
        var path = PackTestKit.Write(fs, Dir, image, PackTestKit.Identity(image, BackupKind.Full, 1), order, withTail: false, imagePages: 64,
            @override: p => pages[p]).PackPath;
        var file = fs.ReadAll(path);

        using var pack = PackReader.Open(fs, path);
        using var frames = new FrameReader(pack, 3 * FrameReader.MinimumWindowBytes);
        var decoded = new byte[PackFormat.PageSize];
        foreach (var e in pack.LoadIndex().Where(e => e.StoredLength > 0 && e.PageId >= 2))
        {
            Assert.That(frames.TryReadFrame(e, out var frame, out var error), Is.True, error);
            var expected = file.AsSpan((int)e.DataOffset - PackFormat.FramePrefixSize, PackFormat.FramePrefixSize + (int)e.StoredLength);
            Assert.That(frame.SequenceEqual(expected), Is.True, $"page {e.PageId}: the frame is not the pack's bytes");
            Assert.That(PackReader.DecodePage(e, frame, decoded, out error), Is.True, error);
            Assert.That(decoded.AsSpan().SequenceEqual(pages[(int)e.PageId]), Is.True, $"page {e.PageId}");
        }
    }

    [TestCase(64L << 20, 1, 64 << 20)]
    [TestCase(64L << 20, 1000, (64 << 20) / 1000)]
    [TestCase(64L << 20, 100_000, FrameReader.MinimumWindowBytes)]
    [TestCase(1L << 40, 1, 64 << 20)]
    public void TheReadBudget_IsHonouredWhateverTheChainLength(long budget, int packs, int window) =>
        Assert.That(FrameReader.WindowFor(budget, packs), Is.EqualTo(window));
}
