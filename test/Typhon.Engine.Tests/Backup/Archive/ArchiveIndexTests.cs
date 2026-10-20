using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Typhon.Engine.Tests.Backup;

/// <summary>
/// <c>archive-index.bin</c> is a cache (WA-6): written by the lock holder, used only when it is intact and was written for the files present, and rebuilt
/// from them otherwise — a deleted index, a torn last entry, a listing it does not describe.
/// </summary>
[TestFixture]
internal sealed class ArchiveIndexTests
{
    private const string Root = "/s";
    private InMemorySparseFileSystem _fs;
    private string _index;

    [SetUp]
    public void SetUp()
    {
        _fs = new InMemorySparseFileSystem();
        _fs.CreateDirectory("/");
        _fs.CreateDirectory(Root);
        for (var i = 0; i < 4; i++)
        {
            ArchiveTestKit.Publish(_fs, Root, ArchiveTestKit.Segment(i + 1, 1 + i * 100, 8 * 1024, seed: i), 100 + i * 100);
        }

        using (var t = WalArchiveTimeline.TryOpen(_fs, Root, ArchiveTestKit.Timeline))
        {
            t.LoadAndRefreshIndex();
        }

        _index = new WalArchivePaths(Root).Index(ArchiveTestKit.Timeline);
        Assert.That(_fs.FileExists(_index), Is.True, "premise: the holder wrote the index");
    }

    private static List<(long, long, long, uint)> Shape(ArchivedTimeline t) =>
        t.Segments.Select(s => (s.SegmentId, s.FirstLsn, s.LastLsn, s.Footer.RawCrc)).ToList();

    [Test]
    public void AnIntactIndex_ForThisListing_IsUsed_AndEqualsTheFiles()
    {
        var cached = WalArchiveStore.Load(_fs, Root, ArchiveTestKit.Timeline);
        var files = WalArchiveStore.Load(_fs, Root, ArchiveTestKit.Timeline, trustIndex: false);
        Assert.That(cached.FromIndex, Is.True);
        Assert.That(files.FromIndex, Is.False);
        Assert.That(Shape(cached), Is.EqualTo(Shape(files)));
        Assert.That(cached.Segments.Select(s => (s.FirstSampleLsn, s.LastSampleUtcTicks)), Is.EqualTo(files.Segments.Select(s => (s.FirstSampleLsn, s.LastSampleUtcTicks))));
    }

    [Test]
    public void ADeletedIndex_IsRebuiltFromTheFiles()
    {
        var expected = Shape(WalArchiveStore.Load(_fs, Root, ArchiveTestKit.Timeline, trustIndex: false));
        _fs.DeleteFile(_index);
        var loaded = WalArchiveStore.Load(_fs, Root, ArchiveTestKit.Timeline);
        Assert.That(loaded.FromIndex, Is.False);
        Assert.That(Shape(loaded), Is.EqualTo(expected));
    }

    [Test]
    public void ATornLastEntry_IsNotTrusted()
    {
        var bytes = _fs.ReadAll(_index);
        _fs.Poke(_index, bytes.Length - 10, [(byte)(bytes[^10] ^ 1)]);
        Assert.That(WalArchiveStore.Load(_fs, Root, ArchiveTestKit.Timeline).FromIndex, Is.False);
        _fs.Resize(_index, bytes.Length - 100);
        Assert.That(WalArchiveStore.Load(_fs, Root, ArchiveTestKit.Timeline).FromIndex, Is.False);
    }

    /// <summary>A segment published after the index was written (a crash before the index caught up): the listing's fingerprint differs.</summary>
    [Test]
    public void AnIndexOfAnotherListing_IsNotTrusted_AndTheFilesWin()
    {
        var segment = ArchiveTestKit.Segment(5, 401, 8 * 1024, seed: 5);
        ArchiveTestKit.Publish(_fs, Root, segment, 500);
        var loaded = WalArchiveStore.Load(_fs, Root, ArchiveTestKit.Timeline);
        Assert.That(loaded.FromIndex, Is.False);
        Assert.That(loaded.Segments.Select(s => s.SegmentId), Is.EqualTo(new long[] { 1, 2, 3, 4, 5 }));
        Assert.That(loaded.ArchivedThroughLsn, Is.EqualTo(500));
    }

    /// <summary>A reader that trusted the index by its presence alone (or its time) would list four segments where five are archived.</summary>
    [Test]
    [RuleMutant("BKP-10")]
    public void Mutant_AnIndexTrustedWithoutItsFingerprint_IsReported()
    {
        ArchiveTestKit.Publish(_fs, Root, ArchiveTestKit.Segment(5, 401, 8 * 1024, seed: 5), 500);
        var fingerprint = WalArchiveStore.Fingerprint(WalArchiveStore.SegmentListing(_fs, new WalArchivePaths(Root).Timeline(ArchiveTestKit.Timeline)));
        RuleMutants.AssertDetects("BKP-10", "BKP-10: the archive's listing", () =>
        {
            // The stale index, read as if its fingerprint were the listing's.
            var bytes = _fs.ReadAll(_index);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(32), fingerprint);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(60), Crc32CUtil.Compute(bytes.AsSpan(0, 60)));
            _fs.Poke(_index, 0, bytes);
            var loaded = WalArchiveStore.Load(_fs, Root, ArchiveTestKit.Timeline);
            Assert.That(loaded.ArchivedThroughLsn, Is.EqualTo(500), "BKP-10: the archive's listing misses a published segment");
        });
    }
}
