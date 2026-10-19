using NUnit.Framework;
using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

namespace Typhon.Engine.Tests.Integrity;

/// <summary>
/// A drain that carries two frames back to back — what the writer produces when two frames are published before it drains — is a sound log, and the scan
/// walks it the way recovery does.
/// </summary>
/// <remarks>
/// The scan used to jump to the next 4 KiB boundary after every frame. A drain's second frame was skipped, the frame after it then failed the CRC chain it
/// had skipped, and a sound log drew <c>CHK-WAL-02</c>; a segment ending in such a drain had its highest LSN understated, which <c>CHK-WAL-04</c> reads as
/// a hole before the next segment. Found by the backup round trip (#1443), where a capture's stall makes multi-frame drains common.
/// </remarks>
[TestFixture]
internal sealed class WalMultiFrameDrainTests : IntegrityFixtureBase
{
    [Test]
    [CancelAfter(30_000)]
    public void ADrainOfTwoFramesFollowedByMoreDrains_IsASoundChain()
    {
        BuildMultiFrameWalDatabase();
        var before = WalkAsRecovery(WalDir);
        DamageKit.MergeTwoWalDrains(BundlePath, lastPair: false, out _);

        // The premise: recovery reads the rewritten segment exactly as it read the original — every chunk, no break.
        Assert.That(WalkAsRecovery(WalDir), Is.EqualTo(before), "recovery must read the merged drain as the same sound log");
        Assert.That(before.Truncated, Is.False);

        var report = DamageKit.Scan(BundlePath, ScanDepth.Deep);
        Assert.That(report.Findings.Where(f => f.Code == "CHK-WAL-02"), Is.Empty,
            "a drain carrying two frames is how the writer coalesces, not a break:" + IntegrityReportText.Render(report));
    }

    [Test]
    [CancelAfter(30_000)]
    public void ASegmentEndingInADrainOfTwoFrames_EndsAtItsLastFramesLsn()
    {
        BuildMultiFrameWalDatabase();
        DamageKit.MergeTwoWalDrains(BundlePath, lastPair: true, out var highest);

        // The next segment begins right after the merged drain's second frame: contiguous, if that frame is counted.
        var firstLsn = WalSegmentHeaderFirstLsn(BundlePath);
        var added = DamageKit.AppendWalSegmentLeavingAnLsnGap(BundlePath, skippedLsns: highest + 1 - firstLsn);
        Assert.That(added, Is.Not.Null);

        var report = DamageKit.Scan(BundlePath, ScanDepth.Deep);
        Assert.That(report.Findings.Where(f => f.Code == "CHK-WAL-04"), Is.Empty,
            $"'{added}' begins at LSN {highest + 1}, right after the last frame of the segment before it:" + IntegrityReportText.Render(report));
    }

    private static (int Chunks, long LastChunkLsn, bool Truncated) WalkAsRecovery(string walDir)
    {
        using var reader = new WalSegmentReader(new WalFileIO());
        Assert.That(reader.OpenSegment(FirstWrittenSegment(walDir)), Is.True);
        while (reader.TryReadNext(out _, out _))
        {
        }

        return (reader.RecordsRead, reader.LastValidLSN, reader.WasTruncated);
    }

    private static long WalSegmentHeaderFirstLsn(string bundle)
        => MemoryMarshal.Read<WalSegmentHeader>(File.ReadAllBytes(FirstWrittenSegment(Path.Combine(bundle, "wal")))).FirstLSN;

    private static string FirstWrittenSegment(string walDir) => Directory.GetFiles(walDir)
        .OrderBy(p => p, StringComparer.Ordinal)
        .First(p => new FileInfo(p).Length > WalSegmentHeader.SizeInBytes);
}
