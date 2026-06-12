using NUnit.Framework;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Typhon.Engine.Tests;

/// <summary>
/// Verifies the durable-watermark honesty fix (P0.2 / LOG-05 / TXW-2): <see cref="WalWriter.DurableLsn"/> must never exceed the highest LSN physically written to a
/// segment. The pre-fix writer advanced the watermark to <c>NextLsn - 1</c>, which counts claims that were assigned an LSN but whose frames are still sitting unwritten
/// in the commit buffer — a false durability acknowledgment. Acceptance criterion AC-2 (design 08 §2, A0.2).
/// </summary>
[TestFixture]
public class DurableLsnHonestyTests : AllocatorTestBase
{
    private string _walDir;

    public override void Setup()
    {
        base.Setup();
        _walDir = Path.Combine(Path.GetTempPath(), $"typhon_durlsn_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_walDir);
    }

    public override void TearDown()
    {
        if (Directory.Exists(_walDir))
        {
            Directory.Delete(_walDir, true);
        }

        base.TearDown();
    }

    private (WalCommitBuffer buffer, WalWriter writer, WalSegmentManager segMgr) CreatePipeline(IWalFileIO io)
    {
        var buffer = new WalCommitBuffer(MemoryAllocator, AllocationResource, 64 * 1024);
        var options = new WalWriterOptions
        {
            WalDirectory = _walDir,
            GroupCommitIntervalMs = 5,
            SegmentSize = 4 * 1024 * 1024,
            PreAllocateSegments = 1,
            StagingBufferSize = 8192,
            UseFUA = false,
        };
        var segMgr = new WalSegmentManager(io, _walDir, options.SegmentSize, options.PreAllocateSegments, useFUA: false);
        segMgr.Initialize(lastSegmentId: 0, firstLSN: 1);
        var writer = new WalWriter(buffer, segMgr, io, options, MemoryAllocator, AllocationResource);
        return (buffer, writer, segMgr);
    }

    /// <summary>
    /// The deterministic falsifier: publish one record (LSN 1), then claim a second record WITHOUT publishing it — this advances NextLsn to 3 while only LSN 1 is
    /// ever written. A sync drain must leave DurableLsn at 1, the highest LSN actually on media. The pre-fix code set DurableLsn = NextLsn - 1 = 2, exceeding the max
    /// written LSN — this assertion fails against the pre-fix writer.
    /// </summary>
    [Test]
    [CancelAfter(5000)]
    public void DurableLsn_NeverExceedsMaxWrittenLsn_WithUndrainedClaim()
    {
        using var io = new ChaosWalFileIO();
        var (buffer, writer, segMgr) = CreatePipeline(io);
        try
        {
            // Published record → LSN 1.
            WalRecordFactory.PublishTransactionRecord(buffer, entityId: 1);

            // Claimed-but-unpublished record → NextLsn advances to 3, but this frame is never written.
            var ctx = WaitContext.FromTimeout(TimeSpan.FromSeconds(2));
            var pending = buffer.TryClaim(64, 1, ref ctx);
            Assert.That(pending.IsValid, Is.True);
            Assert.That(buffer.NextLsn, Is.EqualTo(3), "the unpublished claim must have advanced NextLsn so NextLsn-1 (=2) over-reports");

            writer.DrainAndWriteSync();

            Assert.That(io.MaxObservedLsn, Is.EqualTo(1), "only LSN 1 was ever written to the segment");
            Assert.That(writer.DurableLsn, Is.LessThanOrEqualTo(io.MaxObservedLsn),
                "DurableLsn must never exceed the highest LSN physically written (LOG-05)");
            Assert.That(writer.DurableLsn, Is.EqualTo(1), "the honest watermark is the drained frame's LSN, not NextLsn-1");

            buffer.AbandonClaim(ref pending);
        }
        finally
        {
            writer.Dispose();
            buffer.Dispose();
            segMgr.Dispose();
        }
    }

    /// <summary>
    /// At quiescence (every committer drained), the watermark must equal the highest written LSN — an honesty sanity check under two concurrent Immediate-style
    /// committers driving the real background writer thread.
    /// </summary>
    [Test]
    [CancelAfter(10_000)]
    public void DurableLsn_EqualsMaxWrittenLsn_AfterConcurrentCommitters()
    {
        using var io = new ChaosWalFileIO();
        var (buffer, writer, segMgr) = CreatePipeline(io);
        try
        {
            writer.Start();

            const int perThread = 250;
            const int threads = 2;
            var barrier = new Barrier(threads);

            Parallel.For(0, threads, t =>
            {
                barrier.SignalAndWait();
                for (var i = 0; i < perThread; i++)
                {
                    WalRecordFactory.PublishTransactionRecord(buffer, entityId: t * 100_000 + i);
                }
            });

            const long total = perThread * threads;

            // Drive durability for the highest LSN, then assert honesty at quiescence.
            var ctx = WaitContext.FromTimeout(TimeSpan.FromSeconds(5));
            writer.WaitForDurable(total, ref ctx);

            Assert.That(writer.DurableLsn, Is.LessThanOrEqualTo(io.MaxObservedLsn), "watermark never exceeds bytes on media (LOG-05)");
            Assert.That(writer.DurableLsn, Is.GreaterThanOrEqualTo(total), "all committed records are durable at quiescence");
            Assert.That(io.MaxObservedLsn, Is.EqualTo(total), "exactly LSNs 1..total were written");
        }
        finally
        {
            writer.Dispose();
            buffer.Dispose();
            segMgr.Dispose();
        }
    }
}
