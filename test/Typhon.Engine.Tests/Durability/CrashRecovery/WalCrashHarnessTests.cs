using NUnit.Framework;
using System;
using System.IO;

namespace Typhon.Engine.Tests;

/// <summary>
/// Validates the Layer-1 crash-simulation harness (P0.6): <see cref="ChaosWalFileIO"/> write tracking + crash injection + damage-model reconstruction, driving the
/// real <see cref="WalWriter.DrainAndWriteSync"/> write path, with <see cref="WalSegmentReader"/> as the post-crash oracle. Acceptance criterion AC-H.
/// </summary>
[TestFixture]
public class WalCrashHarnessTests : AllocatorTestBase
{
    private const int Staging = 4096;
    private string _walDir;

    public override void Setup()
    {
        base.Setup();
        _walDir = Path.Combine(Path.GetTempPath(), $"typhon_chaos_{Guid.NewGuid():N}");
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
            StagingBufferSize = Staging,
            UseFUA = false,
        };
        var segMgr = new WalSegmentManager(io, _walDir, options.SegmentSize, options.PreAllocateSegments, useFUA: false);
        segMgr.Initialize(lastSegmentId: 0, firstLSN: 1);
        var writer = new WalWriter(buffer, segMgr, io, options, MemoryAllocator, AllocationResource);
        return (buffer, writer, segMgr);
    }

    private static int ReadAllRecords(IWalFileIO io, string segmentPath, out bool truncated)
    {
        using var reader = new WalSegmentReader(io);
        truncated = false;
        if (!reader.OpenSegment(segmentPath))
        {
            return 0;
        }

        var count = 0;
        while (reader.TryReadNext(out _, out _))
        {
            count++;
        }

        truncated = reader.WasTruncated;
        return count;
    }

    private static string SegmentPath(WalSegmentManager segMgr) => segMgr.ActiveSegment.Path;

    [Test]
    [CancelAfter(5000)]
    public void DrainAndWriteSync_WritesReaderValidSegment()
    {
        using var io = new ChaosWalFileIO();
        var (buffer, writer, segMgr) = CreatePipeline(io);
        try
        {
            const int n = 40;
            for (var i = 0; i < n; i++)
            {
                WalRecordFactory.PublishTransactionRecord(buffer, entityId: 1000 + i, payloadLen: 4);
            }

            writer.DrainAndWriteSync();

            // No crash: every write is durable (the trailing PerformFlush established the barrier).
            var post = io.GetPostCrashState(new DamageModel(DamageType.CleanCut));
            var read = ReadAllRecords(post, SegmentPath(segMgr), out var truncated);

            Assert.That(read, Is.EqualTo(n), "all records should be read back from a contiguous single-drain segment");
            Assert.That(truncated, Is.False);
            Assert.That(writer.DurableLsn, Is.EqualTo(n));
        }
        finally
        {
            writer.Dispose();
            buffer.Dispose();
            segMgr.Dispose();
        }
    }

    [Test]
    [CancelAfter(10_000)]
    public void CrashAtEachWriteChunk_CleanCut_SegmentRemainsReadable()
    {
        // One big drain split into several 4096-byte WriteAligned chunks (contiguous in-file). Crashing at each chunk boundary must leave a readable prefix — the
        // harness drives the real write path and the reader tolerates the torn tail (AC-H).
        const int n = 400; // ~64 B/frame → ~25 KB → ~7 chunks at 4 KB staging

        // Discover how many write chunks a full drain produces, and the segment path.
        int totalChunks;
        string segPath;
        using (var probeIo = new ChaosWalFileIO())
        {
            var (b, w, sm) = CreatePipeline(probeIo);
            try
            {
                for (var i = 0; i < n; i++)
                {
                    WalRecordFactory.PublishTransactionRecord(b, 2000 + i, 4);
                }

                segPath = SegmentPath(sm);
                var baseline = probeIo.TotalWriteCount;
                w.DrainAndWriteSync();
                totalChunks = probeIo.TotalWriteCount - baseline;
            }
            finally
            {
                w.Dispose();
                b.Dispose();
                sm.Dispose();
            }
        }

        Assert.That(totalChunks, Is.GreaterThan(2), "expected the drain to split into several write chunks");

        var lastReadable = -1;
        for (var chunk = 1; chunk <= totalChunks; chunk++)
        {
            using var io = new ChaosWalFileIO();
            var (buffer, writer, segMgr) = CreatePipeline(io);
            try
            {
                var baseline = io.TotalWriteCount; // segment-header setup writes
                io.SetCrashAtWrite(baseline + chunk);

                for (var i = 0; i < n; i++)
                {
                    WalRecordFactory.PublishTransactionRecord(buffer, 2000 + i, 4);
                }

                Assert.Throws<ChaosSimulatedCrashException>(() => writer.DrainAndWriteSync());

                var post = io.GetPostCrashState(new DamageModel(DamageType.CleanCut));
                var read = ReadAllRecords(post, segPath, out _);

                Assert.That(read, Is.InRange(0, n), $"crash at chunk {chunk}: reader must not throw and yields a valid prefix");
                Assert.That(read, Is.GreaterThanOrEqualTo(lastReadable), "more surviving write chunks should never read back fewer records");
                lastReadable = read;
            }
            finally
            {
                writer.Dispose();
                buffer.Dispose();
                segMgr.Dispose();
            }
        }

        Assert.That(lastReadable, Is.GreaterThan(0), "the last crash point (most data written) should recover at least some records");
    }

    [Test]
    [CancelAfter(5000)]
    public void DamageModels_TornAndZero_ProduceReadablePrefix()
    {
        foreach (var type in new[] { DamageType.CleanCut, DamageType.TornWrite, DamageType.ZeroFill, DamageType.Reordered })
        {
            using var io = new ChaosWalFileIO();
            var (buffer, writer, segMgr) = CreatePipeline(io);
            try
            {
                const int n = 400;
                var baseline = io.TotalWriteCount;
                io.SetCrashAtWrite(baseline + 2); // crash on the 2nd write chunk

                for (var i = 0; i < n; i++)
                {
                    WalRecordFactory.PublishTransactionRecord(buffer, 3000 + i, 4);
                }

                Assert.Throws<ChaosSimulatedCrashException>(() => writer.DrainAndWriteSync());

                var post = io.GetPostCrashState(new DamageModel(type, Seed: 42));
                var read = ReadAllRecords(post, SegmentPath(segMgr), out _);

                // The first write chunk (complete frames) was durable before the crash on the 2nd. CleanCut/TornWrite/ZeroFill keep it, so the reader must recover a
                // non-empty prefix; only Reordered may drop the pre-crash chunk via its inclusion coin, so it floors at 0. All must read no more than was published.
                var floor = type == DamageType.Reordered ? 0 : 1;
                Assert.That(read, Is.InRange(floor, n), $"{type}: damaged-tail recovery yields a valid, bounded prefix");
            }
            finally
            {
                writer.Dispose();
                buffer.Dispose();
                segMgr.Dispose();
            }
        }
    }
}
