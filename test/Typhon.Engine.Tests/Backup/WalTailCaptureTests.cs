using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;

namespace Typhon.Engine.Tests;

/// <summary>
/// BKP-04 on a running engine: <see cref="WalTailInspector"/> copies every record the frozen image can reflect, keeps the segment that ends at the
/// checkpoint, works under the reclaim floor while checkpoints keep running, and computes <c>P</c> the way recovery will apply the tail.
/// </summary>
class WalTailCaptureTests : TestBase<WalTailCaptureTests>
{
    private const string MissMarker = "BKP-04 tail misses";
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(5);
    private GatedWalFileIO _gatedIo;

    protected override IWalFileIO CreateWalFileIO() => _gatedIo = new GatedWalFileIO();

    /// <summary>
    /// C1: a record published but not durable at the freeze (the WAL writer is held) can already be reflected by an image page, because a checkpoint
    /// wave writes before its flush2. The tail copy must wait until it is durable and then hold it.
    /// </summary>
    [Test]
    [CancelAfter(10_000)]
    [VerifiesRule("BKP-04")]
    public void FlushBeforeTail_CopiesARecordPublishedButNotDurableAtTheFreeze()
    {
        using var scope = ServiceProvider.CreateScope();
        var dbe = Open(scope);
        Commit(dbe, 5);
        Assert.That(dbe.CheckpointManager.ForceCheckpointAndWait(Budget), Is.True);
        var checkpointLsn = dbe.CheckpointManager.CheckpointLsn;

        _gatedIo.Hold();
        long publishedAtFreeze;
        Task<WalTailResult> capture;
        var sink = NewSink(dbe);
        try
        {
            using (var t = dbe.CreateQuickTransaction(DurabilityMode.Deferred))
            {
                t.Spawn<CompAArch>(CompAArch.A.Set(new CompA(4242)));
                t.Commit();
            }

            dbe.WalManager.RequestFlush();
            Assert.That(_gatedIo.WriterHeld.Wait(Budget), Is.True, "premise: the WAL writer is held with the commit published");
            publishedAtFreeze = dbe.WalManager.LastPublishedLsn;
            Assert.That(dbe.WalManager.DurableLsn, Is.LessThan(publishedAtFreeze), "premise: the commit is published, not durable");

            capture = Task.Run(() => WalTailInspector.CaptureTail(dbe.WalManager, dbe.MemoryAllocator, AllocationResource, checkpointLsn,
                publishedAtFreeze, sink, null, Budget, CancellationToken.None));
            Assert.That(capture.Wait(50), Is.False, $"{MissMarker}: the copy started before the WAL was durable through the freeze's published LSN");
        }
        finally
        {
            _gatedIo.Release();
        }

        var result = capture.Result;
        Assert.That(result.PointLsn, Is.GreaterThanOrEqualTo(publishedAtFreeze),
            $"{MissMarker} the commit published at the freeze (P = {result.PointLsn}, PubF = {publishedAtFreeze})");
    }

    /// <summary>
    /// C2: an idle engine that switches segments and then checkpoints leaves a sealed segment whose last LSN equals the checkpoint LSN. Recovery resumes
    /// the TSN counter above every commit stamp it scans, at or below the checkpoint too, so that segment must be in the tail.
    /// </summary>
    [Test]
    [CancelAfter(10_000)]
    [VerifiesRule("BKP-04")]
    public void SegmentEndingAtTheCheckpoint_IsInTheTail()
    {
        var (result, sealedId, checkpointLsn) = CaptureAfterSwitchAndCheckpoint();
        AssertTailHolds(result.Segments, sealedId);
        Assert.That(result.PointLsn, Is.EqualTo(checkpointLsn), "nothing past the checkpoint: P = C");
    }

    /// <summary>The verifier above must reject a tail that starts one segment late (the <c>lastLSN &gt; C</c> selection).</summary>
    [Test]
    [CancelAfter(10_000)]
    [RuleMutant("BKP-04")]
    public void Mutant_ATailStartingOneSegmentLate_IsReported()
    {
        var (result, sealedId, _) = CaptureAfterSwitchAndCheckpoint();
        var late = result.Segments.Where(s => s.SegmentId != sealedId).ToList();
        RuleMutants.AssertDetects("BKP-04", MissMarker, () => AssertTailHolds(late, sealedId));
    }

    private (WalTailResult Result, long SealedId, long CheckpointLsn) CaptureAfterSwitchAndCheckpoint()
    {
        using var scope = ServiceProvider.CreateScope();
        var dbe = Open(scope);
        var wal = dbe.WalManager;
        var lastLsn = Commit(dbe, 5);
        var sealedId = wal.SegmentManager.ActiveSegment.SegmentId;
        Serve(wal, wal.RequestSegmentSwitch());
        Assert.That(dbe.CheckpointManager.ForceCheckpointAndWait(Budget), Is.True);
        var checkpointLsn = dbe.CheckpointManager.CheckpointLsn;
        var boundary = wal.SegmentManager.SnapshotForTail().Single(s => s.SegmentId == sealedId);
        Assert.That(boundary.LastLsn, Is.EqualTo(checkpointLsn).And.EqualTo(lastLsn), "premise: the sealed segment ends exactly at the checkpoint");

        var result = WalTailInspector.CaptureTail(wal, dbe.MemoryAllocator, AllocationResource, checkpointLsn, wal.LastPublishedLsn, NewSink(dbe),
            null, Budget, CancellationToken.None);
        return (result, sealedId, checkpointLsn);
    }

    private static void AssertTailHolds(IEnumerable<WalTailSegmentCopy> tail, long segmentId)
        => Assert.That(tail.Select(s => s.SegmentId), Does.Contain(segmentId), $"{MissMarker} the sealed segment whose last LSN equals the checkpoint LSN");

    /// <summary>The reclaim floor keeps every segment the tail needs alive while checkpoints run during the copy.</summary>
    [Test]
    [CancelAfter(10_000)]
    [VerifiesRule("BKP-04")]
    public void FloorHoldsDuringTheCopy_WhileACheckpointRuns()
    {
        using var scope = ServiceProvider.CreateScope();
        var dbe = Open(scope);
        var wal = dbe.WalManager;
        var cm = dbe.CheckpointManager;
        Commit(dbe, 3);
        Serve(wal, wal.RequestSegmentSwitch());
        Assert.That(cm.ForceCheckpointAndWait(Budget), Is.True);
        Commit(dbe, 3);
        Serve(wal, wal.RequestSegmentSwitch());
        Commit(dbe, 3);

        var checkpointLsn = cm.CheckpointLsn;
        var expected = WalTailInspector.SelectTail(wal.SegmentManager.SnapshotForTail(), checkpointLsn).Select(s => s.SegmentId).ToArray();
        Assert.That(expected, Has.Length.GreaterThanOrEqualTo(3), "premise: two sealed segments and the active one");

        var checkpointsDuringCopy = 0;
        cm.SetReclaimFloor(checkpointLsn);
        WalTailResult result;
        try
        {
            result = WalTailInspector.CaptureTail(wal, dbe.MemoryAllocator, AllocationResource, checkpointLsn, wal.LastPublishedLsn, NewSink(dbe),
                () =>
                {
                    if (checkpointsDuringCopy++ == 0)
                    {
                        Commit(dbe, 3);
                        // Without the floor this cycle reclaims the segments being copied: on Windows the delete of the one held open fails the cycle.
                        Assert.That(cm.ForceCheckpointAndWait(Budget), Is.True,
                            $"{MissMarker}: the checkpoint during the copy failed or reclaimed a needed segment");
                    }
                }, Budget, CancellationToken.None);
        }
        catch (BackupAbortedException e)
        {
            Assert.Fail($"{MissMarker} a segment reclaimed during the copy: {e.Message}");
            return;
        }
        finally
        {
            cm.ClearReclaimFloor();
        }

        Assert.That(cm.CheckpointLsn, Is.GreaterThan(checkpointLsn), "premise: the checkpoint during the copy moved past every copied sealed segment");
        Assert.That(result.Segments.Select(s => s.SegmentId), Is.EqualTo(expected), $"{MissMarker} a segment selected before the copy");
    }

    /// <summary>
    /// <c>P</c> is the highest LSN recovery applies as a unit: a commit marker, or a fence record, which applies without one (C10); and the scan stops
    /// for good at the first corruption, as recovery's does.
    /// </summary>
    [Test]
    [CancelAfter(10_000)]
    public void PointLsn_IsTheLastCommitOrFence_AndStopsAtTheFirstCorruption()
    {
        using var scope = ServiceProvider.CreateScope();
        var dbe = Open(scope);
        var wal = dbe.WalManager;
        Assert.That(dbe.CheckpointManager.ForceCheckpointAndWait(Budget), Is.True);
        var checkpointLsn = dbe.CheckpointManager.CheckpointLsn;

        var firstCommit = Commit(dbe, 2);
        Serve(wal, wal.RequestSegmentSwitch());
        var secondCommit = Commit(dbe, 2);

        EntityId sv;
        using (var t = dbe.CreateQuickTransaction(DurabilityMode.Immediate))
        {
            sv = t.Spawn<SvTestArchetype>(SvTestArchetype.SvComp.Set(new CompSmSingleVersion(1)));
            t.Commit();
        }

        var svCommit = wal.DurableLsn;
        using (var t = dbe.CreateQuickTransaction())
        {
            var e = t.OpenMut(sv);
            e.Set(SvTestArchetype.SvComp, new CompSmSingleVersion(2));
            t.Commit();
        }

        dbe.WriteTickFence(1);
        wal.RequestFlush();
        var ctx = WaitContext.FromTimeout(Budget);
        wal.WaitForDurable(wal.LastPublishedLsn, ref ctx);
        var fenceLsn = wal.DurableLsn;
        Assert.That(fenceLsn, Is.GreaterThan(svCommit), "premise: the fence wrote records after the last commit");

        var result = WalTailInspector.CaptureTail(wal, dbe.MemoryAllocator, AllocationResource, checkpointLsn, wal.LastPublishedLsn, NewSink(dbe),
            null, Budget, CancellationToken.None);
        Assert.That(result.PointLsn, Is.EqualTo(fenceLsn), "a fence record applies without a commit marker: it ends P");
        Assert.That(result.Segments, Has.Count.GreaterThanOrEqualTo(2));

        // A corruption in the FIRST segment ends recovery there for good: nothing later counts, however valid.
        var segments = result.Segments.Select(s => s with { Prefix = (byte[])s.Prefix.Clone() }).ToList();
        var first = segments[0];
        Corrupt(first.Prefix);
        var truncated = WalTailInspector.ComputePointLsn(segments, checkpointLsn);
        Assert.That(truncated, Is.LessThan(secondCommit), "records past the first corruption must not count, even in a later intact segment");
        Assert.That(truncated, Is.GreaterThanOrEqualTo(checkpointLsn));
    }

    private static void Corrupt(byte[] prefix)
    {
        // Flip a byte inside the last non-zero 4 KiB page of the written prefix: that page holds the segment's last chunk, whose CRC then fails.
        for (var page = prefix.Length / 4096 - 1; page >= 1; page--)
        {
            var span = prefix.AsSpan(page * 4096, 4096);
            var last = span.LastIndexOfAnyExcept((byte)0);
            if (last >= 0)
            {
                span[last / 2] ^= 0x5A;
                return;
            }
        }

        Assert.Fail("premise: the first segment holds a chunk");
    }

    private DatabaseEngine Open(IServiceScope scope)
    {
        var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<CompA>();
        dbe.RegisterComponentFromAccessor<CompSmSingleVersion>();
        dbe.InitializeArchetypes();
        return dbe;
    }

    private static CapturingSink NewSink(DatabaseEngine dbe)
    {
        var store = new FakeBackupStore();
        using var session = store.OpenSession(dbe.DatabaseId, CancellationToken.None);
        var sink = (CapturingSink)session.BeginPoint(new BackupPointPlan(dbe.DatabaseId, dbe.TimelineId, BackupKind.Full, Guid.Empty, 0, 0, "test", false));
        sink.SetImage(2, 0, DateTime.UtcNow.Ticks);
        return sink;
    }

    private static long Commit(DatabaseEngine dbe, int count)
    {
        using (var t = dbe.CreateQuickTransaction(DurabilityMode.Immediate))
        {
            for (var i = 0; i < count; i++)
            {
                t.Spawn<CompAArch>(CompAArch.A.Set(new CompA(i)));
            }

            t.Commit();
        }

        return dbe.WalManager.DurableLsn;
    }

    private static void Serve(WalManager wal, long ticket)
    {
        var deadline = DateTime.UtcNow + Budget;
        while (wal.SwitchesServed < ticket)
        {
            Assert.That(DateTime.UtcNow, Is.LessThan(deadline), "the WAL writer did not serve the switch");
            Thread.Yield();
        }
    }
}
