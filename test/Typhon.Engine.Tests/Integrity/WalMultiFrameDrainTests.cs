using Microsoft.Extensions.DependencyInjection;
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
    /// <summary>
    /// The writer, held inside one write while two commits publish, drains both in one write; later commits drain on their own. Closed cleanly, the
    /// database is Sound — the scan reads the drain's second frame instead of skipping it and then failing the chain at the next drain.
    /// </summary>
    [Test]
    [CancelAfter(30_000)]
    public void TwoCommitsPublishedWhileTheWriterIsHeld_DrainAsOneAndTheLogScansSound()
    {
        GatedWalFileIO gate = null;
        WalFileIOFactory = () => gate = new GatedWalFileIO();
        using (var provider = ReopenProvider())
        using (var scope = provider.CreateScope())
        {
            var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
            dbe.RegisterComponentFromAccessor<CompA>();
            dbe.InitializeArchetypes();
            Spawn(dbe, 1, DurabilityMode.Immediate);

            gate.Hold();
            try
            {
                Spawn(dbe, 2, DurabilityMode.Deferred);
                dbe.WalManager.RequestFlush();
                Assert.That(gate.WriterHeld.Wait(Budget), Is.True, "premise: the writer is held inside a write");
                Spawn(dbe, 3, DurabilityMode.Deferred);
                Spawn(dbe, 4, DurabilityMode.Deferred);
            }
            finally
            {
                gate.Release();
            }

            // Two drains after it: the frame a skipping walk would mis-chain to is then not the last thing in the log.
            Spawn(dbe, 5, DurabilityMode.Immediate);
            Spawn(dbe, 6, DurabilityMode.Immediate);
        }

        Assert.That(LargestDrain(WalDir), Is.GreaterThanOrEqualTo(2), "premise: one drain carries the two commits published while the writer was held");

        var report = DamageKit.Scan(BundlePath, ScanDepth.Deep);
        Assert.That(report.Verdict, Is.EqualTo(IntegrityVerdict.Sound), IntegrityReportText.Render(report));
    }

    [Test]
    [CancelAfter(30_000)]
    public void ASegmentEndingInADrainOfTwoFrames_EndsAtItsLastFramesLsn()
    {
        BuildMultiFrameWalDatabase();
        DamageKit.MergeLastTwoWalDrains(BundlePath, out var highest);

        // The next segment begins right after the merged drain's second frame: contiguous, if that frame is counted.
        var firstLsn = WalSegmentHeaderFirstLsn(BundlePath);
        var added = DamageKit.AppendWalSegmentLeavingAnLsnGap(BundlePath, skippedLsns: highest + 1 - firstLsn);
        Assert.That(added, Is.Not.Null);

        var report = DamageKit.Scan(BundlePath, ScanDepth.Deep);
        Assert.That(report.Findings.Where(f => f.Code == "CHK-WAL-04"), Is.Empty,
            $"'{added}' begins at LSN {highest + 1}, right after the last frame of the segment before it:" + IntegrityReportText.Render(report));
    }

    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(5);

    private static void Spawn(DatabaseEngine dbe, int key, DurabilityMode mode)
    {
        using var t = dbe.CreateQuickTransaction(mode);
        t.Spawn<CompAArch>(CompAArch.A.Set(new CompA(key, key, key)));
        t.Commit();
    }

    /// <summary>The most frames any drain (a 4 KiB-aligned write) of the first written segment carries back to back.</summary>
    private static int LargestDrain(string walDir)
    {
        const int Block = 4096;
        var bytes = File.ReadAllBytes(FirstWrittenSegment(walDir));
        var largest = 0;
        for (var drain = WalSegmentHeader.SizeInBytes; drain + WalFrameHeader.SizeInBytes <= bytes.Length;)
        {
            var frames = 0;
            var at = drain;
            while (at + WalFrameHeader.SizeInBytes <= bytes.Length && MemoryMarshal.Read<int>(bytes.AsSpan(at)) > 0)
            {
                at += MemoryMarshal.Read<int>(bytes.AsSpan(at));
                frames++;
            }

            if (frames == 0)
            {
                break;
            }

            largest = Math.Max(largest, frames);
            drain = (at + Block - 1) / Block * Block;
        }

        return largest;
    }

    private static long WalSegmentHeaderFirstLsn(string bundle)
        => MemoryMarshal.Read<WalSegmentHeader>(File.ReadAllBytes(FirstWrittenSegment(Path.Combine(bundle, "wal")))).FirstLSN;

    private static string FirstWrittenSegment(string walDir) => Directory.GetFiles(walDir)
        .OrderBy(p => p, StringComparer.Ordinal)
        .First(p => new FileInfo(p).Length > WalSegmentHeader.SizeInBytes);
}
