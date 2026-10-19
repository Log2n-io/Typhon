using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;

namespace Typhon.Engine.Tests;

/// <summary>
/// <see cref="WalSegmentManager.SnapshotForTail"/> against a rotation paused between creating the new segment and publishing it: the segment being
/// sealed must be in the snapshot, as the active one before the rotation publishes and as a sealed one after. Making the new segment active before
/// sealing the old one under the lock opens a window in which a snapshot holds neither (BKP-04).
/// </summary>
[TestFixture]
public class SegmentSnapshotTests : AllocatorTestBase
{
    private InMemoryWalFileIO _fileIO;
    private string _walDir;

    public override void Setup()
    {
        base.Setup();
        _fileIO = new InMemoryWalFileIO();
        _walDir = Path.Combine(Path.GetTempPath(), $"typhon_snap_{Guid.NewGuid():N}");
    }

    public override void TearDown()
    {
        _fileIO?.Dispose();
        base.TearDown();
    }

    [Test]
    [CancelAfter(5_000)]
    public void SnapshotDuringARotation_SeesTheSegmentBeingSealed()
    {
        using var mgr = new WalSegmentManager(_fileIO, _walDir, 64 * 1024, 1, false);
        mgr.Initialize(0, 1);
        var first = mgr.ActiveSegment.SegmentId;
        mgr.ActiveSegment.WriteOffset = 3 * 4096;   // as if the writer had written two blocks after the header

        using var paused = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        mgr.RotateProbe = () =>
        {
            paused.Set();
            release.Wait();
        };

        var rotation = Task.Run(() => mgr.RotateSegment(11, 10));
        Assert.That(paused.Wait(TimeSpan.FromSeconds(4)), Is.True, "premise: the rotation reached its pause");
        var during = mgr.SnapshotForTail();
        release.Set();
        rotation.Wait();
        var after = mgr.SnapshotForTail();

        Assert.Multiple(() =>
        {
            Assert.That(during.Select(s => s.SegmentId), Does.Contain(first), "a snapshot taken mid-rotation lost the segment being sealed");
            var sealedFirst = after.Single(s => s.SegmentId == first);
            Assert.That(sealedFirst.IsActive, Is.False);
            Assert.That(sealedFirst.LastLsn, Is.EqualTo(10));
            Assert.That(sealedFirst.WrittenBytes, Is.EqualTo(3 * 4096), "a sealed segment records where its writer stopped");
            Assert.That(after.Single(s => s.IsActive).SegmentId, Is.GreaterThan(first));
            Assert.That(after.Select(s => s.SegmentId), Is.Ordered, "ascending id");
        });
    }

    /// <summary>W2 on the synchronous drain the crash-simulation tests use: a request is served after the batch, or when there is nothing to drain.</summary>
    [Test]
    [CancelAfter(5_000)]
    public void SwitchRequest_IsServedByTheSynchronousDrain()
    {
        using var buffer = new WalCommitBuffer(MemoryAllocator, AllocationResource, 64 * 1024);
        var options = new WalWriterOptions
        {
            WalDirectory = _walDir,
            SegmentSize = 1024 * 1024,
            PreAllocateSegments = 1,
            StagingBufferSize = 8192,
            UseFUA = false,
        };
        using var mgr = new WalSegmentManager(_fileIO, _walDir, options.SegmentSize, options.PreAllocateSegments, false);
        mgr.Initialize(0, 1);
        using var writer = new WalWriter(buffer, mgr, _fileIO, options, MemoryAllocator, AllocationResource);
        var first = mgr.ActiveSegment.SegmentId;

        var ticket = writer.RequestSegmentSwitch();
        writer.DrainAndWriteSync();
        Assert.That(writer.SwitchesServed, Is.EqualTo(ticket));
        Assert.That(mgr.ActiveSegment.SegmentId, Is.EqualTo(first), "an empty segment is not rotated");

        var ctx = WaitContext.FromTimeout(TimeSpan.FromSeconds(2));
        var claim = buffer.TryClaim(64, 1, ref ctx);
        claim.DataSpan.Clear();
        buffer.Publish(ref claim);
        ticket = writer.RequestSegmentSwitch();
        writer.DrainAndWriteSync();

        Assert.That(writer.SwitchesServed, Is.EqualTo(ticket));
        Assert.That(mgr.ActiveSegment.SegmentId, Is.GreaterThan(first), "a segment holding a batch is sealed");
        Assert.That(mgr.SnapshotForTail().Single(s => s.SegmentId == first).LastLsn, Is.EqualTo(writer.DurableLsn));
    }
}
