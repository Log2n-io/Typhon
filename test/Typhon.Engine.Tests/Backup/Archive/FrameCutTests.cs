using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Typhon.Engine.Tests;

/// <summary>
/// AC-A3 and the cut (WA-10, BKP-11): over a real workload's WAL, every frame holds one batch — a transaction ending with its TxCommit, or one fence
/// emission — and a restore to any LSN cuts at the end of the last frame ending at or below it, never at a chunk inside a frame.
/// </summary>
[NonParallelizable]
class FrameCutTests : ArchiveTestBase<FrameCutTests>
{
    private const string Marker = "BKP-11: the cut";

    protected override bool ArchivingOn => false;

    private static List<(long Id, byte[] Bytes)> _segments;

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

    [Test]
    [CancelAfter(15_000)]
    [VerifiesRule("BKP-11")]
    public void EveryFrame_HoldsOneBatch_AndATransactionsCommitIsItsLastRecord()
    {
        var frames = Segments().SelectMany(s => WalFrames.Read(s.Bytes, out _)).ToList();
        var multiChunkTx = 0;
        var multiChunkFence = 0;
        foreach (var f in frames.Where(f => f.Records.Count > 0))
        {
            Assert.That(f.LastLsn, Is.EqualTo(f.Records.Max(r => r.Lsn)), "a frame's header names its highest LSN");
            if (f.Records.Any(r => r.IsTxCommit))
            {
                Assert.That(f.Records[^1].IsTxCommit, Is.True, $"frame at {f.Start}: the TxCommit is the frame's last record");
                Assert.That(f.Records.Count(r => r.IsTxCommit), Is.EqualTo(1), $"frame at {f.Start}: one transaction per frame");
                Assert.That(f.Records[0].IsTxBegin, Is.True, $"frame at {f.Start}: the TxBegin is its first record");
                Assert.That(f.Records.Select(r => r.Tsn).Distinct().Count(), Is.EqualTo(1), $"frame at {f.Start}: one transaction's records");
                multiChunkTx += f.Chunks >= 2 ? 1 : 0;
            }
            else if (f.Records.Any(r => r.IsFence))
            {
                Assert.That(f.Records.All(r => r.IsFence), Is.True, $"frame at {f.Start}: a fence emission holds fence records only");
                Assert.That(f.Records.Select(r => r.Tsn).Distinct().Count(), Is.EqualTo(1), $"frame at {f.Start}: one fence emission");
                multiChunkFence += f.Chunks >= 2 ? 1 : 0;
            }
        }

        Assert.That(multiChunkTx, Is.GreaterThanOrEqualTo(1), "a transaction past 65 528 B spans several chunks of one frame");
        Assert.That(multiChunkFence, Is.GreaterThanOrEqualTo(1), "premise: a fence run past one chunk");
    }

    [Test]
    [CancelAfter(15_000)]
    [VerifiesRule("BKP-11")]
    public void TheCut_AtEveryFrameEnd_AndBetweenFrames_KeepsExactlyTheFramesEndingAtOrBelowTheTarget()
    {
        var targets = 0;
        foreach (var (_, bytes) in Segments())
        {
            var frames = WalFrames.Read(bytes, out _).Where(f => f.LastLsn > 0).ToList();
            AssertCut(bytes, frames, frames[0].LastLsn - 1, cutOffset: null);
            for (var i = 0; i < frames.Count; i++)
            {
                AssertCut(bytes, frames, frames[i].LastLsn, cutOffset: null);
                targets++;
                if (i + 1 < frames.Count && frames[i + 1].Records.Count > 1)
                {
                    AssertCut(bytes, frames, frames[i + 1].Records[0].Lsn, cutOffset: null);   // inside the next frame: it is not kept
                    targets++;
                }
            }
        }

        Assert.That(targets, Is.GreaterThan(10));
    }

    /// <summary>A cut after the first chunk of a multi-chunk frame (a chunk boundary is not a unit boundary): the verifier must refuse it.</summary>
    [Test]
    [CancelAfter(15_000)]
    [RuleMutant("BKP-11")]
    public void Mutant_ACutAtAChunkEnd_IsReported()
    {
        foreach (var (_, bytes) in Segments())
        {
            var frames = WalFrames.Read(bytes, out _).Where(f => f.LastLsn > 0).ToList();
            var i = frames.FindIndex(f => f.Chunks >= 2);
            if (i < 0)
            {
                continue;
            }

            var firstChunkEnd = frames[i].Start + 16 + BitConverter.ToUInt16(bytes, frames[i].Start + 16 + 2);
            RuleMutants.AssertDetects("BKP-11", Marker, () => AssertCut(bytes, frames, frames[i].LastLsn, firstChunkEnd));
            return;
        }

        Assert.Fail("premise: a multi-chunk frame");
    }

    /// <summary>
    /// The verifier: the restored segment — <c>[0, cut)</c> then zeros — reads cleanly, holds exactly the frames ending at or below
    /// <paramref name="target"/>, and the cut names the last of them. <paramref name="cutOffset"/> overrides the cut (the mutant).
    /// </summary>
    private static void AssertCut(byte[] bytes, List<WalFrame> frames, long target, int? cutOffset)
    {
        var expected = frames.Where(f => f.LastLsn <= target).ToList();
        WalCut cut;
        using (var reader = WalFrames.Open(bytes, out _))
        {
            cut = WalArchiveScanner.FindCut(reader, target);
        }

        var offset = cutOffset ?? cut.CutOffset;
        Assert.That(cut.EffectiveLsn, Is.EqualTo(expected.Count == 0 ? 0 : expected[^1].LastLsn), $"{Marker}: L' for target {target}");
        var restored = new byte[bytes.Length];
        bytes.AsSpan(0, offset).CopyTo(restored);
        var read = WalFrames.Read(restored, out var truncated);
        Assert.That(truncated, Is.False, $"{Marker} at {offset} for target {target} leaves a torn frame: recovery would stop there for good");
        Assert.That(read.Where(f => f.LastLsn > 0).Select(f => f.LastLsn), Is.EqualTo(expected.Select(f => f.LastLsn)),
            $"{Marker} for target {target} keeps other frames than those ending at or below it");
    }
}
