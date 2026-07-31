using NUnit.Framework;
using System;
using System.IO;
using System.Threading;

namespace Typhon.Engine.Tests;

/// <summary>
/// Single-threaded unit tests for <see cref="WalWriter"/>.
/// Uses <see cref="InMemoryWalFileIO"/> for isolation from disk I/O.
/// </summary>
[TestFixture]
public class WalWriterTests : AllocatorTestBase
{
    private const int TestCapacity = 64 * 1024;

    private InMemoryWalFileIO _fileIO;
    private string _walDir;

    public override void Setup()
    {
        base.Setup();
        _fileIO = new InMemoryWalFileIO();
        _walDir = Path.Combine(Path.GetTempPath(), $"typhon_wal_writer_test_{Guid.NewGuid():N}");
    }

    public override void TearDown()
    {
        _fileIO?.Dispose();
        if (Directory.Exists(_walDir))
        {
            Directory.Delete(_walDir, true);
        }

        base.TearDown();
    }

    private (WalCommitBuffer buffer, WalWriter writer, WalSegmentManager segMgr) CreateWriterPipeline(
        int bufferCapacity = TestCapacity,
        int groupCommitMs = 5,
        bool useFUA = false)
    {
        var buffer = new WalCommitBuffer(MemoryAllocator, AllocationResource, bufferCapacity);

        var options = new WalWriterOptions
        {
            WalDirectory = _walDir,
            GroupCommitIntervalMs = groupCommitMs,
            SegmentSize = 1024 * 1024, // 1MB for tests
            PreAllocateSegments = 1,
            StagingBufferSize = 8192, // 8KB staging buffer for tests
            UseFUA = useFUA,
        };

        var segMgr = new WalSegmentManager(_fileIO, _walDir, options.SegmentSize, options.PreAllocateSegments, useFUA);
        segMgr.Initialize(lastSegmentId: 0, firstLSN: 1);

        var writer = new WalWriter(buffer, segMgr, _fileIO, options, MemoryAllocator, AllocationResource);

        return (buffer, writer, segMgr);
    }

    #region Lifecycle

    [Test]
    public void Start_SetsIsRunning()
    {
        var (buffer, writer, segMgr) = CreateWriterPipeline();
        try
        {
            writer.Start();
            SpinWait.SpinUntil(() => writer.IsRunning, 2000);

            Assert.That(writer.IsRunning, Is.True);
        }
        finally
        {
            writer.Dispose();
            buffer.Dispose();
            segMgr.Dispose();
        }
    }

    [Test]
    public void Start_Idempotent()
    {
        var (buffer, writer, segMgr) = CreateWriterPipeline();
        try
        {
            writer.Start();
            writer.Start(); // Should not throw or create second thread
            SpinWait.SpinUntil(() => writer.IsRunning, 2000);

            Assert.That(writer.IsRunning, Is.True);
        }
        finally
        {
            writer.Dispose();
            buffer.Dispose();
            segMgr.Dispose();
        }
    }

    [Test]
    [CancelAfter(5000)]
    public void Dispose_StopsThread()
    {
        var (buffer, writer, segMgr) = CreateWriterPipeline();
        writer.Start();
        SpinWait.SpinUntil(() => writer.IsRunning, 2000);

        writer.Dispose(); // Join() inside blocks until the writer thread exits

        Assert.That(writer.IsRunning, Is.False);

        buffer.Dispose();
        segMgr.Dispose();
    }

    #endregion

    #region Basic Drain

    [Test]
    [CancelAfter(5000)]
    public void WriterDrainsPublishedFrames()
    {
        var (buffer, writer, segMgr) = CreateWriterPipeline();
        try
        {
            writer.Start();

            // Produce a frame
            var ctx = WaitContext.FromTimeout(TimeSpan.FromSeconds(2));
            var claim = buffer.TryClaim(64, 1, ref ctx);
            Assert.That(claim.IsValid, Is.True);

            // Write some data
            claim.DataSpan.Fill(0xAA);
            buffer.Publish(ref claim);

            // Wait for writer to drain
            SpinWait.SpinUntil(() => writer.TotalBytesWritten > 0, 2000);

            Assert.That(writer.TotalBytesWritten, Is.GreaterThan(0));
            Assert.That(writer.TotalFlushes, Is.GreaterThan(0));
        }
        finally
        {
            writer.Dispose();
            buffer.Dispose();
            segMgr.Dispose();
        }
    }

    [Test]
    [CancelAfter(5000)]
    public void DurableLsn_AdvancesAfterDrain()
    {
        var (buffer, writer, segMgr) = CreateWriterPipeline();
        try
        {
            writer.Start();

            // Produce a frame with 1 record
            var ctx = WaitContext.FromTimeout(TimeSpan.FromSeconds(2));
            var claim = buffer.TryClaim(64, 1, ref ctx);
            claim.DataSpan.Fill(0xBB);
            buffer.Publish(ref claim);

            // Wait for drain
            SpinWait.SpinUntil(() => writer.DurableLsn > 0, 2000);

            Assert.That(writer.DurableLsn, Is.GreaterThan(0));
        }
        finally
        {
            writer.Dispose();
            buffer.Dispose();
            segMgr.Dispose();
        }
    }

    [Test]
    [CancelAfter(5000)]
    public void MultipleFrames_AllDrained()
    {
        var (buffer, writer, segMgr) = CreateWriterPipeline();
        try
        {
            writer.Start();

            // Produce multiple frames
            for (int i = 0; i < 5; i++)
            {
                var ctx = WaitContext.FromTimeout(TimeSpan.FromSeconds(2));
                var claim = buffer.TryClaim(128, 2, ref ctx);
                claim.DataSpan.Fill((byte)(i + 1));
                buffer.Publish(ref claim);
            }

            // 5 frames × 2 records each = 10 total records → LSNs 1-10
            SpinWait.SpinUntil(() => writer.DurableLsn >= 10, 2000);

            Assert.That(writer.DurableLsn, Is.GreaterThanOrEqualTo(10));
            Assert.That(writer.TotalBytesWritten, Is.GreaterThan(0));
        }
        finally
        {
            writer.Dispose();
            buffer.Dispose();
            segMgr.Dispose();
        }
    }

    #endregion

    #region WaitForDurable

    [Test]
    [CancelAfter(5000)]
    public void WaitForDurable_AlreadyDurable_ReturnsImmediately()
    {
        var (buffer, writer, segMgr) = CreateWriterPipeline();
        try
        {
            writer.Start();

            // Produce and wait for drain
            var ctx = WaitContext.FromTimeout(TimeSpan.FromSeconds(2));
            var claim = buffer.TryClaim(64, 1, ref ctx);
            claim.DataSpan.Fill(0xCC);
            buffer.Publish(ref claim);

            SpinWait.SpinUntil(() => writer.DurableLsn >= 1, 2000);

            // Should return immediately since already durable
            var waitCtx = WaitContext.FromTimeout(TimeSpan.FromSeconds(1));
            writer.WaitForDurable(1, ref waitCtx);

            Assert.That(writer.DurableLsn, Is.GreaterThanOrEqualTo(1));
        }
        finally
        {
            writer.Dispose();
            buffer.Dispose();
            segMgr.Dispose();
        }
    }

    #endregion

    #region Properties

    [Test]
    public void InitialState_PropertiesAreDefault()
    {
        var (buffer, writer, segMgr) = CreateWriterPipeline();
        try
        {
            Assert.That(writer.DurableLsn, Is.EqualTo(0));
            Assert.That(writer.IsRunning, Is.False);
            Assert.That(writer.HasFatalError, Is.False);
            Assert.That(writer.TotalBytesWritten, Is.EqualTo(0));
            Assert.That(writer.TotalFlushes, Is.EqualTo(0));
        }
        finally
        {
            writer.Dispose();
            buffer.Dispose();
            segMgr.Dispose();
        }
    }

    #endregion

    #region RequestFlush

    [Test]
    [CancelAfter(5000)]
    public void RequestFlush_TriggersFlush()
    {
        var (buffer, writer, segMgr) = CreateWriterPipeline(groupCommitMs: 10_000); // Very long interval to prove RequestFlush bypasses it
        try
        {
            writer.Start();

            // Produce a frame
            var ctx = WaitContext.FromTimeout(TimeSpan.FromSeconds(2));
            var claim = buffer.TryClaim(64, 1, ref ctx);
            claim.DataSpan.Fill(0xDD);
            buffer.Publish(ref claim);

            // Wait for drain (writer thread picks up data from buffer)
            SpinWait.SpinUntil(() => writer.DurableLsn >= 1, 2000);

            // Request explicit flush — Signal() wakes the writer immediately
            writer.RequestFlush();
            SpinWait.SpinUntil(() => writer.TotalFlushes > 0, 2000);

            Assert.That(writer.TotalBytesWritten, Is.GreaterThan(0));
            Assert.That(writer.TotalFlushes, Is.GreaterThan(0));
        }
        finally
        {
            writer.Dispose();
            buffer.Dispose();
            segMgr.Dispose();
        }
    }

    #endregion

    #region Shutdown drain (#580)

    /// <summary>
    /// WP-03 regression (#580): the shutdown drain must WRITE a final batch that exceeds <c>StagingBufferSize</c>, not silently discard it.
    /// </summary>
    /// <remarks>
    /// <c>DrainRemaining</c> used to run the staging-buffer copy under an <c>if (bytesToWrite &lt;= _stagingBufferSize)</c> with no <c>else</c>, while
    /// <c>CompleteDrain</c> + <c>AdvanceDurable</c> ran unconditionally underneath it. An oversized final batch — a large last commit, or a fat fence batch at
    /// Dispose — therefore reached neither <c>WriteAligned</c> nor disk, yet the durable watermark advanced over it. Recovery then starts after the watermark
    /// and never looks for the missing records: silent data loss on an *orderly* shutdown. Pre-fix this test sees <c>TotalBytesWritten == 0</c>.
    /// </remarks>
    [Test]
    [CancelAfter(5000)]
    public void DrainRemaining_BatchLargerThanStagingBuffer_IsWrittenNotDiscarded()
    {
        // StagingBufferSize is 8 KB here (see CreateWriterPipeline); claim comfortably past it while staying inside the commit-buffer half.
        const int OversizedFrame = 16 * 1024;

        var (buffer, writer, segMgr) = CreateWriterPipeline();
        try
        {
            // Deliberately do NOT Start() — the shutdown drain is the path under test, and the writer thread would otherwise consume the batch in WriterLoop
            // (which has always handled the oversized case correctly). This mirrors a frame published in the window before the loop's shutdown break.
            var ctx = WaitContext.FromTimeout(TimeSpan.FromSeconds(2));
            var claim = buffer.TryClaim(OversizedFrame, 1, ref ctx);
            Assert.That(claim.IsValid, Is.True, "precondition: the oversized frame must fit the commit buffer");
            claim.DataSpan.Fill(0xBE);
            buffer.Publish(ref claim);

            writer.DrainRemaining();

            Assert.Multiple(() =>
            {
                Assert.That(writer.TotalBytesWritten, Is.GreaterThanOrEqualTo(OversizedFrame),
                    "the oversized final batch must reach WriteAligned — 0 here is the #580 silent drop");
                Assert.That(writer.DurableLsn, Is.GreaterThanOrEqualTo(1),
                    "the durable watermark may only advance over bytes that were actually written");
            });
        }
        finally
        {
            writer.Dispose();
            buffer.Dispose();
            segMgr.Dispose();
        }
    }

    /// <summary>Companion to the above: a final batch that FITS the staging buffer still drains through the original path.</summary>
    [Test]
    [CancelAfter(5000)]
    public void DrainRemaining_BatchWithinStagingBuffer_IsWritten()
    {
        var (buffer, writer, segMgr) = CreateWriterPipeline();
        try
        {
            var ctx = WaitContext.FromTimeout(TimeSpan.FromSeconds(2));
            var claim = buffer.TryClaim(256, 1, ref ctx);
            Assert.That(claim.IsValid, Is.True);
            claim.DataSpan.Fill(0xAB);
            buffer.Publish(ref claim);

            writer.DrainRemaining();

            Assert.That(writer.TotalBytesWritten, Is.GreaterThan(0));
            Assert.That(writer.DurableLsn, Is.GreaterThanOrEqualTo(1));
        }
        finally
        {
            writer.Dispose();
            buffer.Dispose();
            segMgr.Dispose();
        }
    }

    #endregion
}
