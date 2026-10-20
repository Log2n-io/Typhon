using System.IO;
using System.Linq;
using System.Threading;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;

namespace Typhon.Engine.Tests;

/// <summary>
/// BKP-04: since <c>P</c> is computed by reading the live segments again after the copy, the reclaim floor must keep them until that read ends. A
/// checkpoint that would reclaim a tail segment, staged between the copy and the read of <c>P</c>, leaves it in place, and the capture completes.
/// </summary>
class TailFloorTests : BackupTestBase<TailFloorTests>
{
    [Test]
    [CancelAfter(20_000)]
    [VerifiesRule("BKP-04")]
    public void AReclaimStagedBeforeTheReadOfP_LeavesTheTailSegments()
    {
        using var scope = ServiceProvider.CreateScope();
        var dbe = Open(scope);
        var coordinator = dbe.BackupCoordinator;
        var ids = Spawn(dbe, 50);
        var wal = dbe.WalManager;
        var cm = dbe.CheckpointManager;

        string[] tailPaths = null;
        WalTailSegment beforeSwitch = default;
        long checkpointAfter = 0;
        coordinator.Probe = (phase, _) =>
        {
            if (phase != CapturePhase.TailBeforePoint)
            {
                return;
            }

            var c = coordinator.LastCapture.CheckpointLsn;
            var tail = WalTailInspector.SelectTail(wal.SegmentManager.SnapshotForTail(), c);
            tailPaths = tail.Select(s => s.Path).ToArray();
            beforeSwitch = tail.Single(s => s.IsActive);

            // Seal the tail's active segment and checkpoint past its last LSN: without the floor, this cycle deletes it.
            Update(dbe, ids, 7);
            var ticket = wal.RequestSegmentSwitch();
            SpinWait.SpinUntil(() => wal.SwitchesServed >= ticket, Budget);
            Update(dbe, ids, 8);
            Assert.That(cm.ForceCheckpointAndWait(Budget), Is.True);
            checkpointAfter = cm.CheckpointLsn;
        };

        var point = Capture(dbe, new FakeBackupStore());

        var sealedNow = wal.SegmentManager.SnapshotForTail().Single(s => s.SegmentId == beforeSwitch.SegmentId);
        Assert.Multiple(() =>
        {
            Assert.That(sealedNow.IsActive, Is.False, "premise: the switch sealed the tail's active segment");
            Assert.That(checkpointAfter, Is.GreaterThan(sealedNow.LastLsn), "premise: the staged checkpoint passed it, so reclaim wanted it gone");
            Assert.That(tailPaths.Where(p => !File.Exists(p)), Is.Empty, "BKP-04: a tail segment was reclaimed before P was read from it");
            Assert.That(point.PointLsn, Is.GreaterThanOrEqualTo(coordinator.LastCapture.CheckpointLsn));
        });
    }
}
