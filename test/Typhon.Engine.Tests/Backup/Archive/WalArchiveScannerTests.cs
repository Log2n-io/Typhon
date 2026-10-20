using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Typhon.Engine.Tests;

/// <summary>
/// <see cref="WalArchiveScanner.Scan"/> (impl/1418 §3.4, BKP-10): what the archive keeps of a segment — up to the end of its last frame read whole, never a
/// byte of a torn frame — and what it records about the rest: the walk's stop, the frame to seal, the fence records recovery applied but the archive lost.
/// </summary>
[NonParallelizable]
class WalArchiveScannerTests : ArchiveTestBase<WalArchiveScannerTests>
{
    protected override bool ArchivingOn => false;

    private static List<(long Id, byte[] Bytes)> _segments;

    /// <summary>The workload's segments, written once per fixture run (a transaction past one chunk, a multi-chunk fence run, small commits).</summary>
    private List<(long Id, byte[] Bytes)> Segments()
    {
        if (_segments == null)
        {
            using var scope = ServiceProvider.CreateScope();
            var dbe = Open(scope);
            WalFrames.Workload(dbe, bigTransaction: 2_000, fencedEntities: 5_000);
            Switch(dbe);
            _segments = WalFrames.ReadSegments(WalDirectoryOf(dbe)).Where(s => WalFrames.Read(s.Bytes, out _).Count > 0).ToList();
        }

        return _segments;
    }

    private static WalSegmentScan Scan(byte[] bytes, out byte[] kept)
    {
        using var reader = WalFrames.Open(bytes, out _);
        var scan = WalArchiveScanner.Scan(reader);
        kept = new byte[scan.ArchiveLength];
        reader.HeaderBytes.CopyTo(kept);
        reader.LoadedData[..(scan.ArchiveLength - WalSegmentHeader.SizeInBytes)].CopyTo(kept.AsSpan(WalSegmentHeader.SizeInBytes));
        return scan;
    }

    [Test]
    [CancelAfter(15_000)]
    [VerifiesRule("BKP-10")]
    public void AClean_SealedSegment_IsKeptToItsLastFrame_WithItsLsns()
    {
        foreach (var (id, bytes) in Segments())
        {
            var frames = WalFrames.Read(bytes, out var truncated);
            Assert.That(truncated, Is.False);
            var scan = Scan(bytes, out var kept);
            Assert.That(scan.StoppedAtCorruption, Is.False);
            Assert.That(scan.SegmentId, Is.EqualTo(id));
            Assert.That(scan.ArchiveLength, Is.EqualTo(AlignUp(frames[^1].End)), "up to the last frame's page, no further");
            Assert.That(scan.LastRecordLsn, Is.EqualTo(frames.SelectMany(f => f.Records).Max(r => r.Lsn)));
            Assert.That(scan.LastUnitLsn, Is.EqualTo(frames.SelectMany(f => f.Records).Where(r => r.IsTxCommit || r.IsFence).Max(r => r.Lsn)));
            Assert.That(scan.DroppedFromLsn + scan.DroppedToLsn + scan.FirstInvalidFrameOffset, Is.Zero);
            Assert.That(kept.AsSpan(0, scan.ArchiveLength).SequenceEqual(bytes.AsSpan(0, scan.ArchiveLength)), Is.True, "a clean segment is kept as is");
            Assert.That(WalFrames.Read(kept, out truncated).Select(f => f.LastLsn), Is.EqualTo(frames.Select(f => f.LastLsn)), "the kept bytes read the same");
            Assert.That(truncated, Is.False);
        }
    }

    [Test]
    [CancelAfter(15_000)]
    public void ATearAtAFrameHeader_KeepsTheFramesBefore_AndNamesTheFrameToSeal()
    {
        var bytes = Segments().First().Bytes;
        var frames = WalFrames.Read(bytes, out _);
        var torn = frames[^2];
        var damaged = (byte[])bytes.Clone();
        BinaryPrimitives.WriteInt32LittleEndian(damaged.AsSpan(torn.Start), int.MaxValue / 2);   // a frame length past the segment

        var scan = Scan(damaged, out var kept);
        Assert.That(scan.StoppedAtCorruption, Is.True);
        Assert.That(scan.FirstInvalidFrameOffset, Is.EqualTo(torn.Start));
        Assert.That(scan.LastRecordLsn, Is.EqualTo(frames[^3].LastLsn));
        Assert.That(scan.ArchiveLength, Is.EqualTo(AlignUp(frames[^3].End)));
        AssertNothingPast(kept, frames[^3].End);
        Assert.That(WalFrames.Read(kept, out var truncated).Count, Is.EqualTo(frames.Count - 2));
        Assert.That(truncated, Is.False, "the archived copy ends cleanly: no byte of the torn frame was kept");
    }

    [Test]
    [CancelAfter(15_000)]
    public void ATearInsideAMultiChunkTransaction_DropsTheFrame_WithNoApplicableRecord()
    {
        var (bytes, frame) = FirstFrameWhere(f => f.Chunks >= 2 && f.Records.Any(r => r.IsTxCommit));
        var scan = Scan(BreakSecondChunk(bytes, frame), out var kept);
        Assert.That(scan.StoppedAtCorruption, Is.True);
        Assert.That(scan.FirstInvalidFrameOffset, Is.EqualTo(frame.Start), "the seal goes at the incomplete frame's start");
        Assert.That(scan.DroppedFromLsn, Is.Zero, "an uncommitted transaction applies nothing: nothing was dropped");
        Assert.That(scan.ArchiveLength, Is.EqualTo(AlignUp(frame.Start)));
        AssertNothingPast(kept, frame.Start);
    }

    [Test]
    [CancelAfter(15_000)]
    public void ATearInsideAMultiChunkFenceRun_RecordsTheFenceRecordsRecoveryApplied()
    {
        var (bytes, frame) = FirstFrameWhere(f => f.Chunks >= 2 && f.Records.All(r => r.IsFence));
        var applied = frame.Records.Where(r => r.Chunk == 0).ToList();
        var scan = Scan(BreakSecondChunk(bytes, frame), out _);
        Assert.That(scan.StoppedAtCorruption, Is.True);
        Assert.That(scan.DroppedFromLsn, Is.EqualTo(applied.Min(r => r.Lsn)));
        Assert.That(scan.DroppedToLsn, Is.EqualTo(applied.Max(r => r.Lsn)));
    }

    /// <summary>
    /// The crash open's seal over a fence run torn inside its frame: the sentinel lands at the frame's start, records the fence LSN recovery applied from
    /// it, and the segment then ends cleanly there for every reader; the archive keeps the same bytes and records the applied range as dropped.
    /// </summary>
    [Test]
    [CancelAfter(15_000)]
    [VerifiesRule("BKP-10")]
    public void ASealedTornTail_EndsCleanlyAtItsSentinel_NamingTheFenceRecordsRecoveryApplied()
    {
        var (bytes, frame) = FirstFrameWhere(f => f.Chunks >= 2 && f.Records.All(r => r.IsFence));
        var damaged = BreakSecondChunk(bytes, frame);
        var torn = Scan(damaged, out var tornKept);
        var dir = Path.Combine(TestStorage.Root, nameof(WalArchiveScannerTests), CurrentDatabaseName + ".seal");
        if (Directory.Exists(dir))
        {
            Directory.Delete(dir, true);
        }

        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, "0000000000000001.wal");
            File.WriteAllBytes(path, damaged);
            using (var io = new WalFileIO())
            {
                var seal = WalTornTailSeal.Seal(io, dir);
                Assert.That(seal, Is.EqualTo(new WalTornTailSealResult(torn.SegmentId, frame.Start, torn.DroppedToLsn, true)));
                Assert.That(WalTornTailSeal.Seal(io, dir).SegmentId, Is.Zero, "sealed, the segment no longer breaks: a second open seals nothing");
            }

            var sealedBytes = File.ReadAllBytes(path);
            Assert.That(sealedBytes.AsSpan(0, frame.Start).SequenceEqual(damaged.AsSpan(0, frame.Start)), Is.True, "nothing before the frame changes");
            Assert.That(sealedBytes.AsSpan(frame.Start + 16).SequenceEqual(damaged.AsSpan(frame.Start + 16)), Is.True, "only the frame header is written");
            Assert.That(WalFrames.Read(sealedBytes, out var truncated).Count, Is.EqualTo(WalFrames.Read(damaged, out _).Count));
            Assert.That(truncated, Is.False, "recovery reads to the sentinel and stops cleanly");

            var scan = Scan(sealedBytes, out var kept);
            Assert.That((scan.StoppedAtCorruption, scan.SealedAtCrash), Is.EqualTo((false, true)));
            Assert.That((scan.DroppedFromLsn, scan.DroppedToLsn), Is.EqualTo((torn.LastRecordLsn + 1, torn.DroppedToLsn)));
            Assert.That((scan.LastRecordLsn, scan.ArchiveLength), Is.EqualTo((torn.LastRecordLsn, torn.ArchiveLength)));
            Assert.That(kept, Is.EqualTo(tornKept), "the archive keeps what it kept of the torn segment");
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Test]
    [CancelAfter(15_000)]
    public void AnEmptySegment_KeepsItsHeaderOnly_AndTrailingZerosAreNeverKept()
    {
        var bytes = Segments().First().Bytes;
        var empty = (byte[])bytes.Clone();
        empty.AsSpan(WalSegmentHeader.SizeInBytes).Clear();
        var scan = Scan(empty, out _);
        Assert.That(scan.ArchiveLength, Is.EqualTo(WalSegmentHeader.SizeInBytes));
        Assert.That(scan.LastRecordLsn + scan.LastUnitLsn, Is.Zero);
        Assert.That(scan.StoppedAtCorruption, Is.False);

        // An adopted segment is read at its declared size (its written length was not recorded): the zeros after its last frame are not kept.
        var frames = WalFrames.Read(bytes, out _);
        Assert.That(bytes.Length, Is.GreaterThan(AlignUp(frames[^1].End)), "premise: the file is longer than its data");
        Assert.That(Scan(bytes, out _).ArchiveLength, Is.EqualTo(AlignUp(frames[^1].End)));
    }

    private (byte[] Bytes, WalFrame Frame) FirstFrameWhere(Func<WalFrame, bool> predicate)
    {
        foreach (var (_, bytes) in Segments())
        {
            var frame = WalFrames.Read(bytes, out _).FirstOrDefault(predicate);
            if (frame != null)
            {
                return (bytes, frame);
            }
        }

        Assert.Fail("premise: the workload wrote such a frame");
        return default;
    }

    /// <summary>A copy of <paramref name="bytes"/> with the CRC of <paramref name="frame"/>'s second chunk broken.</summary>
    private static byte[] BreakSecondChunk(byte[] bytes, WalFrame frame)
    {
        var damaged = (byte[])bytes.Clone();
        var first = frame.Start + 16;   // WalFrameHeader.SizeInBytes
        var firstSize = BinaryPrimitives.ReadUInt16LittleEndian(damaged.AsSpan(first + 2));   // WalChunkHeader.ChunkSize
        var second = first + firstSize;
        damaged[second + 12] ^= 0xFF;   // past its 8-byte header: a body byte, so its CRC fails
        return damaged;
    }

    private static void AssertNothingPast(byte[] kept, int end) =>
        Assert.That(kept.AsSpan(end).IndexOfAnyExcept((byte)0), Is.EqualTo(-1), "the kept page holds no byte past the last complete frame");

    private static int AlignUp(int value) => (value + 4095) & ~4095;
}
