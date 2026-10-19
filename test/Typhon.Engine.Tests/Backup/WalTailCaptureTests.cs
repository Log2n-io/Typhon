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
    /// C24: a write the WAL writer has issued but not completed is never in the copy. The active segment's bound is its write offset, which the writer
    /// advances only once the write returns, so a write in flight lies wholly past it — staged here with the write's first block already on disk.
    /// </summary>
    [Test]
    [CancelAfter(10_000)]
    public void ACopyTakenWhileAWriteIsInFlight_HoldsNoneOfItsBytes()
    {
        using var scope = ServiceProvider.CreateScope();
        var dbe = Open(scope);
        var wal = dbe.WalManager;
        Commit(dbe, 5);
        Assert.That(dbe.CheckpointManager.ForceCheckpointAndWait(Budget), Is.True);
        var checkpointLsn = dbe.CheckpointManager.CheckpointLsn;
        var durable = wal.DurableLsn;

        _gatedIo.HoldTorn();
        try
        {
            using (var t = dbe.CreateQuickTransaction(DurabilityMode.Deferred))
            {
                for (var i = 0; i < 400; i++)
                {
                    t.Spawn<CompAArch>(CompAArch.A.Set(new CompA(10_000 + i)));
                }

                t.Commit();
            }

            wal.RequestFlush();
            Assert.That(_gatedIo.WriterHeld.Wait(Budget), Is.True, "premise: the WAL writer is held inside its write");
            var (heldAt, heldLength) = _gatedIo.HeldWrite;
            Assert.That(heldLength, Is.GreaterThan(4096), "premise: the held write spans blocks, so its first one is on disk while it is in flight");
            var active = wal.SegmentManager.ActiveSegment;
            var landed = new byte[4096];
            using (var handle = wal.FileIO.OpenSegmentForRead(active.Path))
            {
                wal.FileIO.ReadAligned(handle, heldAt, landed);
            }

            Assert.That(landed.AsSpan().IndexOfAnyExcept((byte)0), Is.GreaterThanOrEqualTo(0), "premise: the in-flight write is partly on disk");

            var result = WalTailInspector.CaptureTail(wal, dbe.MemoryAllocator, AllocationResource, checkpointLsn, durable, NewSink(dbe), null, Budget,
                CancellationToken.None);
            var copy = result.Segments.Single(s => s.SegmentId == active.SegmentId);
            Assert.That(copy.CopiedBytes, Is.LessThanOrEqualTo(heldAt),
                $"the copy read {copy.CopiedBytes - heldAt} byte(s) of the write in flight at {heldAt}");
        }
        finally
        {
            _gatedIo.Release();
        }
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

        var sink = NewSink(dbe);
        var result = WalTailInspector.CaptureTail(wal, dbe.MemoryAllocator, AllocationResource, checkpointLsn, wal.LastPublishedLsn, sink,
            null, Budget, CancellationToken.None);
        Assert.That(result.PointLsn, Is.EqualTo(fenceLsn), "a fence record applies without a commit marker: it ends P");
        Assert.That(result.Segments, Has.Count.GreaterThanOrEqualTo(2));

        // P over the bytes the sink received, as a stored point holds them, is the P computed over the live segments.
        var stored = sink.Tail.Select(s => (Id: s.SegmentId, Bytes: s.Bytes.ToArray())).ToList();
        Assert.That(WalTailInspector.ComputePointLsn(stored.Select(s => new WalTailPrefix(s.Id, s.Bytes)).ToList(), checkpointLsn),
            Is.EqualTo(result.PointLsn), "P over the stored tail differs from P over the live segments it was copied from");

        // A corruption in the FIRST segment ends recovery there for good: nothing later counts, however valid.
        Corrupt(stored[0].Bytes);
        var truncated = WalTailInspector.ComputePointLsn(stored.Select(s => new WalTailPrefix(s.Id, s.Bytes)).ToList(), checkpointLsn);
        Assert.That(truncated, Is.LessThan(secondCommit), "records past the first corruption must not count, even in a later intact segment");
        Assert.That(truncated, Is.GreaterThanOrEqualTo(checkpointLsn));
    }

    /// <summary>
    /// A batch larger than the staging buffer is written in slices, each advancing the write offset, cut anywhere in a chunk. A tail taken between two
    /// slices ends where the batch began, never inside it (C24 per batch, not per write): a stored point never carries a torn chunk.
    /// </summary>
    [Test]
    [CancelAfter(10_000)]
    [VerifiesRule("BKP-04")]
    public void ATailTakenBetweenTheSlicesOfABatch_EndsWhereTheBatchBegan()
    {
        using var scope = ServiceProvider.CreateScope();
        var dbe = Open(scope);
        var wal = dbe.WalManager;
        Assert.That(dbe.CheckpointManager.ForceCheckpointAndWait(Budget), Is.True);
        var checkpointLsn = dbe.CheckpointManager.CheckpointLsn;
        var durable = Commit(dbe, 5);
        var active = wal.SegmentManager.ActiveSegment;

        _gatedIo.HoldAfter(1);
        try
        {
            using (var t = dbe.CreateQuickTransaction(DurabilityMode.Deferred))
            {
                for (var i = 0; i < 8000; i++)
                {
                    t.Spawn<CompAArch>(CompAArch.A.Set(new CompA(i)));
                }

                t.Commit();
            }

            wal.RequestFlush();
            Assert.That(_gatedIo.WriterHeld.Wait(Budget), Is.True, "premise: the WAL writer is held");
            var slice = _gatedIo.PassedWrites.Single();
            Assert.That(slice.Length, Is.EqualTo(new WalWriterOptions().StagingBufferSize), "premise: the write let through is a batch's first slice");
            Assert.That(wal.SegmentManager.ActiveSegment, Is.SameAs(active), "premise: no rotation");
            Assert.That(active.WriteOffset, Is.GreaterThan(slice.Offset), "premise: the slice advanced the write offset");

            var sink = NewSink(dbe);
            var result = WalTailInspector.CaptureTail(wal, dbe.MemoryAllocator, AllocationResource, checkpointLsn, durable, sink, null, Budget,
                CancellationToken.None);
            var copy = result.Segments.Single(s => s.SegmentId == active.SegmentId);
            Assert.That(copy.CopiedBytes, Is.EqualTo(slice.Offset),
                $"the tail ends {copy.CopiedBytes - slice.Offset} byte(s) inside a batch written in slices");
            Assert.That(result.PointLsn, Is.EqualTo(durable));
        }
        finally
        {
            _gatedIo.Release();
        }
    }

    /// <summary>
    /// The copy streams through one native buffer and <c>P</c> reads the source through a clamped view: the managed allocations are the segment reader's
    /// own (one data area per segment, as recovery's), never a copy of the tail. Measured on this thread with a sink that keeps no bytes.
    /// </summary>
    [Test]
    [CancelAfter(10_000)]
    public void TheTailCopy_MakesNoManagedCopyOfTheTail()
    {
        using var scope = ServiceProvider.CreateScope();
        var dbe = Open(scope);
        var wal = dbe.WalManager;
        Assert.That(dbe.CheckpointManager.ForceCheckpointAndWait(Budget), Is.True);
        var checkpointLsn = dbe.CheckpointManager.CheckpointLsn;
        for (var i = 0; i < 4; i++)
        {
            Commit(dbe, 8000);
        }

        var selected = WalTailInspector.SelectTail(wal.SegmentManager.SnapshotForTail(), checkpointLsn);
        var written = selected.Sum(s => s.WrittenBytes);
        Assert.That(written, Is.GreaterThan(2L << 20), "premise: a tail of megabytes");
        var readerOwn = selected.Sum(s => (long)s.DeclaredSize) + (256 << 10);   // per segment its header and data area; one read buffer

        var sink = NewSink(dbe);
        sink.DiscardTailBytes = true;
        var before = GC.GetAllocatedBytesForCurrentThread();
        var result = WalTailInspector.CaptureTail(wal, dbe.MemoryAllocator, AllocationResource, checkpointLsn, wal.LastPublishedLsn, sink, null, Budget,
            CancellationToken.None);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.That(result.BytesCopied, Is.EqualTo(written), "premise: the whole tail was copied");
        Assert.That(allocated, Is.LessThan(readerOwn + (512 << 10)),
            $"the copy allocated {allocated - readerOwn} byte(s) beyond the segment reader's own, for a tail of {written}");
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
