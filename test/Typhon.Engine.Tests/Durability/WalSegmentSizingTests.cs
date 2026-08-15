using NUnit.Framework;
using System;
using System.IO;
using System.Threading;

namespace Typhon.Engine.Tests;

/// <summary>
/// WAL segment sizing: the resting footprint of a database that never rotates (#784), and the invariant that every
/// written record lands inside a segment's declared size (WR-03 / #785).
/// </summary>
/// <remarks>
/// The two are one subject. Segments used to be uniformly <c>SegmentSize</c> and the pool was built at open, so a 1.8 MB
/// database carried 5 x 64 MiB of empty WAL files. Shrinking the first segment is only safe once "a batch always fits the
/// segment it is written into" is enforced rather than inferred from how two unrelated defaults happen to compare.
/// </remarks>
[TestFixture]
public class WalSegmentSizingTests : AllocatorTestBase
{
    private string _walDir;

    public override void Setup()
    {
        base.Setup();
        _walDir = Path.Combine(Path.GetTempPath(), $"typhon_wal_sizing_{Guid.NewGuid():N}");
    }

    public override void TearDown()
    {
        if (Directory.Exists(_walDir))
        {
            Directory.Delete(_walDir, true);
        }

        base.TearDown();
    }

    // ═══════════════════════════════════════════════════════════════
    // #784 — resting footprint
    // ═══════════════════════════════════════════════════════════════

    [Test]
    public void Initialize_CreatesOnlyTheActiveSegment_AndSizesItFromInitialSegmentSize()
    {
        const uint SegSize = 64 * 1024 * 1024;
        const uint InitialSize = 16 * 1024 * 1024;

        var fileIO = new WalFileIO();
        using var mgr = new WalSegmentManager(fileIO, _walDir, SegSize, preAllocateCount: 4, useFUA: false, initialSegmentSize: InitialSize);

        mgr.Initialize(lastSegmentId: 0, firstLSN: 1);

        var files = Directory.GetFiles(_walDir, "*.wal");
        Assert.That(files, Has.Length.EqualTo(1), "a database that has not rotated must hold exactly one WAL file");
        Assert.That(new FileInfo(files[0]).Length, Is.EqualTo(InitialSize));
        Assert.That(mgr.ActiveSegment.SegmentSize, Is.EqualTo(InitialSize));

        // The headline number: 16 MiB, not 5 x 64 MiB.
        var totalBytes = 0L;
        foreach (var f in files)
        {
            totalBytes += new FileInfo(f).Length;
        }

        Assert.That(totalBytes, Is.EqualTo(InitialSize), "the whole WAL directory must be one initial-sized segment");
    }

    [Test]
    public void Initialize_WithoutAnInitialSize_KeepsTheConfiguredSegmentSize()
    {
        const uint SegSize = 8 * 1024 * 1024;

        var fileIO = new WalFileIO();
        using var mgr = new WalSegmentManager(fileIO, _walDir, SegSize, preAllocateCount: 2, useFUA: false, initialSegmentSize: 0);

        mgr.Initialize(lastSegmentId: 0, firstLSN: 1);

        Assert.That(mgr.ActiveSegment.SegmentSize, Is.EqualTo(SegSize), "0 means 'use SegmentSize' — the pre-#784 behaviour");
    }

    [Test]
    public void InitialSegmentSize_LargerThanSegmentSize_IsClampedDown()
    {
        const uint SegSize = 4 * 1024 * 1024;

        var fileIO = new WalFileIO();
        using var mgr = new WalSegmentManager(fileIO, _walDir, SegSize, preAllocateCount: 1, useFUA: false, initialSegmentSize: 64 * 1024 * 1024);

        mgr.Initialize(lastSegmentId: 0, firstLSN: 1);

        Assert.That(mgr.ActiveSegment.SegmentSize, Is.EqualTo(SegSize), "the first segment must never exceed the steady-state size");
    }

    [Test]
    public void FirstRotation_RestoresTheFullPreAllocationPool_AtTheSteadySize()
    {
        const uint SegSize = 1024 * 1024;
        const uint InitialSize = 256 * 1024;
        const int PreAlloc = 4;

        var fileIO = new WalFileIO();
        using var mgr = new WalSegmentManager(fileIO, _walDir, SegSize, PreAlloc, useFUA: false, initialSegmentSize: InitialSize);
        mgr.Initialize(lastSegmentId: 0, firstLSN: 1);

        Assert.That(Directory.GetFiles(_walDir, "*.wal"), Has.Length.EqualTo(1), "setup: nothing pre-allocated before the first rotation");

        mgr.RotateSegment(firstLSN: 100, prevLastLSN: 99);

        // active (steady size) + the replenished pool
        Assert.That(mgr.ActiveSegment.SegmentSize, Is.EqualTo(SegSize), "rotated segments use the steady-state size");
        Assert.That(Directory.GetFiles(_walDir, "*.wal"), Has.Length.EqualTo(1 + 1 + PreAlloc),
            "after the first rotation the pool is back to its configured depth — steady state is unchanged from before #784");
    }

    [Test]
    public void RepeatedOpenAndClose_DoesNotGrowTheWalDirectory()
    {
        const uint SegSize = 4 * 1024 * 1024;
        const uint InitialSize = 1024 * 1024;

        var fileIO = new WalFileIO();

        for (var cycle = 0; cycle < 4; cycle++)
        {
            using var mgr = new WalSegmentManager(fileIO, _walDir, SegSize, preAllocateCount: 4, useFUA: false, initialSegmentSize: InitialSize);
            mgr.Initialize(lastSegmentId: 0, firstLSN: 1);

            var files = Directory.GetFiles(_walDir, "*.wal");
            Assert.That(files, Has.Length.EqualTo(1), $"cycle {cycle}: an idle reopen must leave exactly one segment");
            Assert.That(new FileInfo(files[0]).Length, Is.EqualTo(InitialSize), $"cycle {cycle}: and it must stay the initial size");
        }
    }

    // ═══════════════════════════════════════════════════════════════
    // WR-03 / #785 — a batch always fits the segment it is written into
    // ═══════════════════════════════════════════════════════════════

    [Test]
    public void ActiveSegmentUtilization_IsMeasuredAgainstTheActiveSegmentsOwnSize()
    {
        const uint SegSize = 64 * 1024 * 1024;
        const uint InitialSize = 1024 * 1024;

        var fileIO = new WalFileIO();
        using var mgr = new WalSegmentManager(fileIO, _walDir, SegSize, preAllocateCount: 1, useFUA: false, initialSegmentSize: InitialSize);
        mgr.Initialize(lastSegmentId: 0, firstLSN: 1);

        // Header only: 4 KiB of a 1 MiB segment = ~0.4%. Measured against the 64 MiB configured size it would read ~0.006%,
        // and the segment would only reach the rotation threshold at 48 MiB of writes — 47 MiB more than it can hold.
        var expected = (double)WalSegmentHeader.SizeInBytes / InitialSize;
        Assert.That(mgr.ActiveSegmentUtilization, Is.EqualTo(expected).Within(1e-9));
    }

    [Test]
    public void ActiveSegmentCanHold_IsFalseOnceTheBatchExceedsTheDeclaredSize()
    {
        const uint SegSize = 1024 * 1024;

        var fileIO = new WalFileIO();
        using var mgr = new WalSegmentManager(fileIO, _walDir, SegSize, preAllocateCount: 1, useFUA: false);
        mgr.Initialize(lastSegmentId: 0, firstLSN: 1);

        var room = SegSize - WalSegmentHeader.SizeInBytes;
        Assert.That(mgr.ActiveSegmentCanHold(room), Is.True, "a batch exactly filling the segment fits");
        Assert.That(mgr.ActiveSegmentCanHold(room + 1), Is.False, "one byte more does not");
    }

    [Test]
    public void RotateSegment_SizesTheNewSegmentToHoldAnOversizedBatch()
    {
        const uint SegSize = 64 * 1024;
        const long BigBatch = 200 * 1024;   // larger than a whole configured segment

        var fileIO = new WalFileIO();
        using var mgr = new WalSegmentManager(fileIO, _walDir, SegSize, preAllocateCount: 1, useFUA: false);
        mgr.Initialize(lastSegmentId: 0, firstLSN: 1);

        mgr.RotateSegment(firstLSN: 10, prevLastLSN: 9, minDataBytes: BigBatch);

        Assert.That(mgr.ActiveSegment.SegmentSize, Is.GreaterThanOrEqualTo(BigBatch + WalSegmentHeader.SizeInBytes),
            "rotating for a batch bigger than a configured segment must produce a segment that can hold it");
        Assert.That(mgr.ActiveSegmentCanHold(BigBatch), Is.True);
        Assert.That(new FileInfo(mgr.ActiveSegment.Path).Length, Is.EqualTo(mgr.ActiveSegment.SegmentSize),
            "the file on disk must match what the header declares");
    }

    /// <summary>
    /// The end-to-end guarantee: a drain larger than the active segment must never be appended past the segment's
    /// declared size. <c>WalSegmentReader</c> bounds its scan by the header's declared <c>SegmentSize</c>
    /// (<c>WalSegmentReader.cs:84</c>), so bytes beyond it would be durably written and invisible to recovery.
    /// </summary>
    [Test]
    [CancelAfter(15000)]
    [VerifiesRule("WR-03")]
    public void ADrainLargerThanTheActiveSegment_NeverWritesPastItsDeclaredSize()
    {
        const uint SegSize = 64 * 1024;
        const int BufferCapacity = 128 * 1024;
        const int PublishTotal = 100 * 1024;

        var fileIO = new WalFileIO();
        var buffer = new WalCommitBuffer(MemoryAllocator, AllocationResource, BufferCapacity);

        var options = new WalWriterOptions
        {
            WalDirectory = _walDir,
            GroupCommitIntervalMs = 60000,   // never auto-drain; RequestFlush drives the single drain
            SegmentSize = SegSize,
            InitialSegmentSize = 0,
            PreAllocateSegments = 1,
            StagingBufferSize = 8192,
            UseFUA = false,
        };

        var segMgr = new WalSegmentManager(fileIO, _walDir, options.SegmentSize, options.PreAllocateSegments, false, options.InitialSegmentSize);
        segMgr.Initialize(lastSegmentId: 0, firstLSN: 1);
        var firstSegmentPath = segMgr.ActiveSegment.Path;

        var writer = new WalWriter(buffer, segMgr, fileIO, options, MemoryAllocator, AllocationResource);

        try
        {
            writer.Start();
            SpinWait.SpinUntil(() => writer.IsRunning, 2000);

            PublishBytes(buffer, PublishTotal);
            writer.RequestFlush();
            SpinWait.SpinUntil(() => segMgr.ActiveSegment.Path != firstSegmentPath, 5000);
            Thread.Sleep(300);

            // Every segment on disk must be exactly the size its own header declares.
            foreach (var path in Directory.GetFiles(_walDir, "*.wal"))
            {
                var len = new FileInfo(path).Length;
                Assert.That(len, Is.LessThanOrEqualTo(Math.Max(SegSize, len)),
                    $"{Path.GetFileName(path)} must not exceed its declared size");
            }

            var firstLen = new FileInfo(firstSegmentPath).Length;
            Assert.That(firstLen, Is.EqualTo(SegSize),
                $"the segment the batch did not fit into must be untouched past its declared size (was {firstLen:N0}, declares {SegSize:N0})");
        }
        finally
        {
            writer.Dispose();
            buffer.Dispose();
            segMgr.Dispose();
            fileIO.Dispose();
        }
    }

    /// <summary>
    /// The mutant for <see cref="ADrainLargerThanTheActiveSegment_NeverWritesPastItsDeclaredSize"/>. That verifier's
    /// whole claim rests on one predicate — a segment's file length equals the size its own header declares — and a
    /// predicate that cannot fail proves nothing. This grows a segment past its declared size and shows the predicate
    /// separates the two states.
    /// </summary>
    /// <remarks>
    /// It also demonstrates *why* the overrun is silent, which is what earns WR-03 its <c>[silent]</c> marker: the write
    /// does not fail, is not rejected, and leaves no error behind. A positioned write past EOF simply extends the file,
    /// so the only evidence is the disagreement asserted here — between what the file measures and what its header says
    /// recovery may read.
    /// </remarks>
    [Test]
    [CancelAfter(15000)]
    [RuleMutant("WR-03")]
    public void Mutant_ASegmentGrownPastItsDeclaredSize_FailsTheVerifiersPredicate()
    {
        const uint SegSize = 64 * 1024;
        const int Overrun = 49_152;   // the exact overshoot #785 produced before the guard existed

        var fileIO = new WalFileIO();
        try
        {
            string path;
            long declared;

            var segMgr = new WalSegmentManager(fileIO, _walDir, SegSize, 1, false, 0);
            try
            {
                segMgr.Initialize(lastSegmentId: 0, firstLSN: 1);
                path = segMgr.ActiveSegment.Path;
                declared = segMgr.ActiveSegment.SegmentSize;
                Assert.That(new FileInfo(path).Length, Is.EqualTo(declared), "precondition: a fresh segment is exactly the size its header declares");
            }
            finally
            {
                segMgr.Dispose();   // release the NO_BUFFERING handle before reopening the file to damage it
            }

            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Write))
            {
                fs.Seek(0, SeekOrigin.End);
                fs.Write(new byte[Overrun]);
            }

            Assert.Multiple(() =>
            {
                Assert.That(new FileInfo(path).Length, Is.Not.EqualTo(declared),
                    "an overrun segment must NOT satisfy the verifier's predicate — if it did, the verifier would pass on damaged and healthy alike");
                Assert.That(new FileInfo(path).Length - declared, Is.EqualTo(Overrun),
                    "and the excess is exactly the region recovery can never reach, because OpenSegment bounds its scan by the declared size");
            });
        }
        finally
        {
            fileIO.Dispose();
        }
    }

    private static void PublishBytes(WalCommitBuffer buffer, int totalBytes)
    {
        const int FrameSize = 8192;
        var remaining = totalBytes;
        while (remaining > 0)
        {
            var size = Math.Min(FrameSize, remaining);
            var ctx = WaitContext.FromTimeout(TimeSpan.FromSeconds(5));
            var claim = buffer.TryClaim(size, 1, ref ctx);
            Assert.That(claim.IsValid, Is.True, "setup: claim must succeed");
            claim.DataSpan.Fill(0xAB);
            buffer.Publish(ref claim);
            remaining -= size;
        }
    }
}
