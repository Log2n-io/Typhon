using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;

namespace Typhon.Engine.Tests;

/// <summary>
/// The WAL touch points of online backup (design 02 §7): the segment-switch request (W2), the reclaim floor (W1), and the segment snapshot a tail is
/// copied from, on a running engine with an on-disk WAL.
/// </summary>
class WalBackupTouchTests : TestBase<WalBackupTouchTests>
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(5);

    protected override IWalFileIO CreateWalFileIO() => new WalFileIO();

    [Test]
    [CancelAfter(10_000)]
    public void Switch_OnAnIdleWriter_SealsTheSegmentAtItsLastRecord_AndAnEmptyOneIsNotRotated()
    {
        using var scope = ServiceProvider.CreateScope();
        var dbe = Open(scope);
        var wal = dbe.WalManager;
        var lastLsn = Commit(dbe, 10);
        var before = wal.SegmentManager.ActiveSegment.SegmentId;

        Serve(wal, wal.RequestSegmentSwitch());

        var snap = wal.SegmentManager.SnapshotForTail();
        var sealedOld = snap.Single(s => s.SegmentId == before);
        var active = snap.Single(s => s.IsActive);
        Assert.Multiple(() =>
        {
            Assert.That(active.SegmentId, Is.GreaterThan(before), "an idle writer must still serve the switch");
            Assert.That(sealedOld.IsActive, Is.False);
            Assert.That(sealedOld.LastLsn, Is.GreaterThanOrEqualTo(lastLsn), "the sealed segment ends at the last record written into it");
            Assert.That(sealedOld.WrittenBytes, Is.GreaterThan(4096).And.LessThanOrEqualTo(sealedOld.DeclaredSize));
        });

        // The new active segment holds nothing: a second request is served without rotating.
        Serve(wal, wal.RequestSegmentSwitch());
        Assert.That(wal.SegmentManager.ActiveSegment.SegmentId, Is.EqualTo(active.SegmentId), "an empty segment is not rotated");

        // Like every rotation, the switch restarts the CRC chain: the new segment's first chunk chains from zero.
        Commit(dbe, 1);
        var head = new byte[WalSegmentHeader.SizeInBytes + WalFrameHeader.SizeInBytes + WalChunkHeader.SizeInBytes];
        using (var fs = new FileStream(active.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        {
            fs.ReadExactly(head);
        }

        var chunk = MemoryMarshal.Read<WalChunkHeader>(head.AsSpan(WalSegmentHeader.SizeInBytes + WalFrameHeader.SizeInBytes));
        Assert.That(chunk.ChunkSize, Is.GreaterThan(0), "premise: the commit landed in the new segment");
        Assert.That(chunk.PrevCRC, Is.Zero, "the switch did not restart the CRC chain");
    }

    /// <summary>
    /// Switches requested while commits stream: every rotation restarts the CRC chain, and recovery after a crash reads every commit across the
    /// segments the switches cut.
    /// </summary>
    [Test]
    [CancelAfter(15_000)]
    public void Switch_UnderLoad_RecoveryReadsEveryCommitAcrossTheSegments()
    {
        int committed;
        using (var scope = ServiceProvider.CreateScope())
        {
            var dbe = Open(scope);
            var wal = dbe.WalManager;
            var first = wal.SegmentManager.ActiveSegment.SegmentId;
            using var stop = new CancellationTokenSource();
            var count = 0;
            var writer = Task.Run(() =>
            {
                while (!stop.IsCancellationRequested)
                {
                    Commit(dbe, 5);
                    Interlocked.Add(ref count, 5);
                }
            });

            for (var i = 0; i < 2; i++)
            {
                var floor = Volatile.Read(ref count);
                while (Volatile.Read(ref count) < floor + 5)
                {
                    Thread.Yield();
                }

                Serve(wal, wal.RequestSegmentSwitch());
            }

            stop.Cancel();
            writer.Wait(Budget);
            committed = Volatile.Read(ref count);
            Assert.That(wal.SegmentManager.ActiveSegment.SegmentId, Is.GreaterThanOrEqualTo(first + 2), "premise: each switch cut a segment");
            dbe.SimulateHardCrash();
        }

        using (var scope = ServiceProvider.CreateScope())
        {
            var dbe = Open(scope);
            Assert.That(dbe.CrashRecoveryAtOpen, Is.True);
            using var t = dbe.CreateQuickTransaction();
            Assert.That(t.Query<CompAArch>().Count(), Is.EqualTo(committed), "recovery lost a commit across a switched segment");
        }
    }

    /// <summary>W1: while a floor is held, a checkpoint keeps every sealed segment whose last LSN reaches it; released, the next one reclaims them.</summary>
    [Test]
    [CancelAfter(10_000)]
    public void ReclaimFloor_KeepsTheSegmentsAtOrAboveIt_UntilReleased()
    {
        using var scope = ServiceProvider.CreateScope();
        var dbe = Open(scope);
        var wal = dbe.WalManager;
        var cm = dbe.CheckpointManager;

        var lsnA = Commit(dbe, 3);
        var segA = wal.SegmentManager.ActiveSegment.SegmentId;
        Serve(wal, wal.RequestSegmentSwitch());
        Commit(dbe, 3);
        var segB = wal.SegmentManager.ActiveSegment.SegmentId;
        Serve(wal, wal.RequestSegmentSwitch());
        Commit(dbe, 3);

        cm.SetReclaimFloor(lsnA);
        try
        {
            Assert.That(cm.ForceCheckpointAndWait(Budget), Is.True);
            Assert.That(cm.CheckpointLsn, Is.GreaterThan(lsnA), "premise: the checkpoint moved past segment A");
            var held = wal.SegmentManager.SnapshotForTail().Select(s => s.SegmentId).ToArray();
            Assert.That(held, Does.Contain(segA).And.Contain(segB), "a segment whose last LSN reaches the floor was reclaimed under it");
        }
        finally
        {
            cm.ClearReclaimFloor();
        }

        Commit(dbe, 3);
        Assert.That(cm.ForceCheckpointAndWait(Budget), Is.True);
        Assert.That(wal.SegmentManager.SnapshotForTail().Select(s => s.SegmentId), Does.Not.Contain(segA), "released, the floor holds nothing back");
    }

    private static DatabaseEngine Open(IServiceScope scope)
    {
        var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<CompA>();
        dbe.InitializeArchetypes();
        return dbe;
    }

    /// <summary>Commits <paramref name="count"/> spawns durably and returns the WAL's durable LSN after it.</summary>
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
        var sw = Stopwatch.StartNew();
        while (wal.SwitchesServed < ticket)
        {
            Assert.That(sw.Elapsed, Is.LessThan(Budget), "the WAL writer did not serve the switch");
            Thread.Yield();
        }
    }
}
