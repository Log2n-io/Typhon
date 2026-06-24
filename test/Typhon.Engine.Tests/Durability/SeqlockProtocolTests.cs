using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NUnit.Framework;
using System;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;

namespace Typhon.Engine.Tests;

/// <summary>
/// Tests for the seqlock protocol (<see cref="PageBaseHeader.ModificationCounter"/>),
/// checkpoint CRC stamping (<see cref="PagedMMF.WritePagesForCheckpoint"/>),
/// and concurrent snapshot consistency via <see cref="PagedMMF.CopyPageWithSeqlock"/>.
/// </summary>
[TestFixture]
public class SeqlockProtocolTests : AllocatorTestBase
{
    private InMemoryWalFileIO _fileIO;
    private string _walDir;
    private ManagedPagedMMF _mmf;
    private EpochManager _epochManager;
    private WalManager _walManager;

    private static string CurrentDatabaseName
    {
        get
        {
            var testName = TestContext.CurrentContext.Test.Name;
            var databaseName = $"T_SL_{testName}_db";
            if (System.Text.Encoding.UTF8.GetByteCount(databaseName) > PagedMMFOptions.DatabaseNameMaxUtf8Size)
            {
                databaseName = $"T_SL_{testName.Substring(testName.Length - (PagedMMFOptions.DatabaseNameMaxUtf8Size - 8))}_db";
            }
            return databaseName;
        }
    }

    public override void Setup()
    {
        base.Setup();
        _fileIO = new InMemoryWalFileIO();
        _walDir = Path.Combine(Path.GetTempPath(), $"typhon_seqlock_test_{Guid.NewGuid():N}");
    }

    public override void TearDown()
    {
        _walManager?.Dispose();
        _walManager = null;
        _mmf?.Dispose();
        _mmf = null;
        _fileIO?.Dispose();
        _fileIO = null;
        if (Directory.Exists(_walDir))
        {
            Directory.Delete(_walDir, true);
        }

        base.TearDown();
    }

    private void CreateTestInfrastructure()
    {
        _epochManager = new EpochManager("TestEpochManager", AllocationResource);

        var logger = ServiceProvider.GetRequiredService<ILogger<PagedMMF>>();
        var options = new ManagedPagedMMFOptions
        {
            DatabaseDirectory = Path.GetTempPath(),
            DatabaseName = CurrentDatabaseName,
            DatabaseCacheSize = PagedMMF.MinimumCacheSize,
        };
        options.EnsureFileDeleted();

        _mmf = new ManagedPagedMMF(ResourceRegistry, _epochManager, MemoryAllocator, options, AllocationResource, "TestMMF", logger);
    }

    private WalManager CreateWalManager(int commitBufferCapacity = 64 * 1024)
    {
        var options = new WalWriterOptions
        {
            WalDirectory = _walDir,
            GroupCommitIntervalMs = 2,
            SegmentSize = 1024 * 1024,
            PreAllocateSegments = 1,
            StagingBufferSize = 8192,
            UseFUA = false,
        };

        var mgr = new WalManager(options, MemoryAllocator, _fileIO, AllocationResource, commitBufferCapacity);
        mgr.Initialize();
        mgr.Start();

        SpinWait.SpinUntil(() => mgr.IsRunning, 2000);

        return mgr;
    }

    private int LatchPage(int filePageIndex, long epoch)
    {
        _mmf.RequestPageEpoch(filePageIndex, epoch, out var memPageIndex);
        var latched = _mmf.TryLatchPageExclusive(memPageIndex);
        Assert.That(latched, Is.True, $"Failed to latch page {filePageIndex}");
        return memPageIndex;
    }

    /// <summary>
    /// Reads the ModificationCounter from the in-memory page header.
    /// </summary>
    private unsafe int ReadModificationCounter(int memPageIndex)
    {
        var headerAddr = (PageBaseHeader*)_mmf.GetMemPageAddress(memPageIndex);
        return headerAddr->ModificationCounter;
    }

    /// <summary>
    /// Fills the data region of an in-memory page with a uniform byte pattern.
    /// Must be called while the page is exclusively latched.
    /// </summary>
    private unsafe void WritePatternToPage(int memPageIndex, byte pattern)
    {
        var pageAddr = _mmf.GetMemPageAddress(memPageIndex);
        new Span<byte>(pageAddr + PagedMMF.PageHeaderSize, PagedMMF.PageSize - PagedMMF.PageHeaderSize).Fill(pattern);
    }

    /// <summary>
    /// Verifies that a page read from disk has all data bytes equal (consistent snapshot) and a valid CRC.
    /// </summary>
    private static void VerifyPageConsistency(byte[] page)
    {
        // Check all data bytes are the same (no mixed writes)
        var firstDataByte = page[PagedMMF.PageHeaderSize];
        for (int i = PagedMMF.PageHeaderSize + 1; i < PagedMMF.PageSize; i++)
        {
            Assert.That(page[i], Is.EqualTo(firstDataByte),
                $"Page data inconsistency at byte {i}: expected 0x{firstDataByte:X2}, got 0x{page[i]:X2}");
        }

        // Verify CRC
        var storedCrc = Unsafe.As<byte, uint>(ref page[PageBaseHeader.PageChecksumOffset]);
        if (storedCrc != 0)
        {
            var computedCrc = Crc32CUtil.ComputeSkipping(page, PageBaseHeader.PageChecksumOffset, PageBaseHeader.PageChecksumSize);
            Assert.That(computedCrc, Is.EqualTo(storedCrc), "Page CRC mismatch after checkpoint");
        }
    }

    // ═══════════════════════════════════════════════════════════════
    // Seqlock Unit Tests
    // ═══════════════════════════════════════════════════════════════

    [Test]
    [CancelAfter(5000)]
    public void TryLatchPageExclusive_SetsCounterOdd()
    {
        CreateTestInfrastructure();

        int memPageIdx;
        using (var guard = EpochGuard.Enter(_epochManager))
        {
            _mmf.RequestPageEpoch(5, guard.Epoch, out memPageIdx);

            var counterBefore = ReadModificationCounter(memPageIdx);
            Assert.That(counterBefore % 2, Is.EqualTo(0), "Initial counter should be even");

            var latched = _mmf.TryLatchPageExclusive(memPageIdx);
            Assert.That(latched, Is.True);

            var counterDuring = ReadModificationCounter(memPageIdx);
            Assert.That(counterDuring % 2, Is.EqualTo(1), "Counter should be odd while page is latched");
            Assert.That(counterDuring, Is.EqualTo(counterBefore + 1));

            _mmf.UnlatchPageExclusive(memPageIdx);
        }
    }

    [Test]
    [CancelAfter(5000)]
    public void UnlatchPageExclusive_SetsCounterEven()
    {
        CreateTestInfrastructure();

        int memPageIdx;
        using (var guard = EpochGuard.Enter(_epochManager))
        {
            _mmf.RequestPageEpoch(5, guard.Epoch, out memPageIdx);

            var counterBefore = ReadModificationCounter(memPageIdx);

            _mmf.TryLatchPageExclusive(memPageIdx);
            _mmf.UnlatchPageExclusive(memPageIdx);

            var counterAfter = ReadModificationCounter(memPageIdx);
            Assert.That(counterAfter % 2, Is.EqualTo(0), "Counter should be even after unlatch");
            Assert.That(counterAfter, Is.EqualTo(counterBefore + 2));
        }
    }

    [Test]
    [CancelAfter(5000)]
    public void LatchUnlatch_MultipleCycles_CounterIncrementsCorrectly()
    {
        CreateTestInfrastructure();

        int memPageIdx;
        using (var guard = EpochGuard.Enter(_epochManager))
        {
            _mmf.RequestPageEpoch(5, guard.Epoch, out memPageIdx);
            var counterBefore = ReadModificationCounter(memPageIdx);

            for (int cycle = 0; cycle < 3; cycle++)
            {
                _mmf.TryLatchPageExclusive(memPageIdx);

                var odd = ReadModificationCounter(memPageIdx);
                Assert.That(odd % 2, Is.EqualTo(1), $"Cycle {cycle}: counter should be odd while latched");

                _mmf.UnlatchPageExclusive(memPageIdx);

                var even = ReadModificationCounter(memPageIdx);
                Assert.That(even % 2, Is.EqualTo(0), $"Cycle {cycle}: counter should be even after unlatch");
            }

            var counterAfter = ReadModificationCounter(memPageIdx);
            Assert.That(counterAfter, Is.EqualTo(counterBefore + 6), "3 cycles × 2 increments = 6 total");
        }
    }

    [Test]
    [CancelAfter(5000)]
    public void ReentrantLatch_DoesNotIncrementCounter()
    {
        CreateTestInfrastructure();

        int memPageIdx;
        using (var guard = EpochGuard.Enter(_epochManager))
        {
            _mmf.RequestPageEpoch(5, guard.Epoch, out memPageIdx);

            // First latch → counter becomes odd
            _mmf.TryLatchPageExclusive(memPageIdx);
            var counterAfterFirstLatch = ReadModificationCounter(memPageIdx);
            Assert.That(counterAfterFirstLatch % 2, Is.EqualTo(1));

            // Re-entrant latch → counter should NOT change
            var latched = _mmf.TryLatchPageExclusive(memPageIdx);
            Assert.That(latched, Is.True, "Re-entrant latch should succeed");
            var counterAfterReentrant = ReadModificationCounter(memPageIdx);
            Assert.That(counterAfterReentrant, Is.EqualTo(counterAfterFirstLatch), "Re-entrant latch should not increment counter");

            // First unlatch (re-entrance depth decrements, still latched)
            _mmf.UnlatchPageExclusive(memPageIdx);
            var counterAfterFirstUnlatch = ReadModificationCounter(memPageIdx);
            Assert.That(counterAfterFirstUnlatch, Is.EqualTo(counterAfterFirstLatch),
                "First unlatch of re-entrant latch should not change counter (depth > 0)");

            // Second unlatch (actual release, counter becomes even)
            _mmf.UnlatchPageExclusive(memPageIdx);
            var counterAfterSecondUnlatch = ReadModificationCounter(memPageIdx);
            Assert.That(counterAfterSecondUnlatch % 2, Is.EqualTo(0), "Final unlatch should produce even counter");
            Assert.That(counterAfterSecondUnlatch, Is.EqualTo(counterAfterFirstLatch + 1),
                "Only 2 total increments (1 latch + 1 unlatch), not 4");
        }
    }

    // ═══════════════════════════════════════════════════════════════
    // Checkpoint + CRC Integration
    // ═══════════════════════════════════════════════════════════════

    [Test]
    [CancelAfter(5000)]
    public unsafe void Checkpoint_ProducesConsistentSnapshot_WithValidCrc()
    {
        CreateTestInfrastructure();

        const int filePageIndex = 5;
        const byte pattern = 0xBB;

        int memPageIdx;
        using (var guard = EpochGuard.Enter(_epochManager))
        {
            memPageIdx = LatchPage(filePageIndex, guard.Epoch);

            // Write a recognizable pattern into the page data region
            WritePatternToPage(memPageIdx, pattern);

            _mmf.UnlatchPageExclusive(memPageIdx);
        }

        // Mark the page dirty so CollectDirtyMemPageIndices picks it up
        _mmf.IncrementDirty(memPageIdx);

        // Checkpoint: snapshot via seqlock, stamp CRC, write to disk
        using var stagingPool = new StagingBufferPool(MemoryAllocator, AllocationResource, StagingBufferPool.MinCapacity);
        _mmf.WritePagesForCheckpoint([memPageIdx], stagingPool, out _);

        // Read page back from disk and verify
        var diskPage = new byte[PagedMMF.PageSize];
        _mmf.ReadPageDirect(filePageIndex, diskPage);

        // Verify data pattern survived
        for (int i = PagedMMF.PageHeaderSize; i < PagedMMF.PageSize; i++)
        {
            Assert.That(diskPage[i], Is.EqualTo(pattern), $"Data mismatch at offset {i}");
        }

        // Verify CRC is valid and non-zero
        var storedCrc = Unsafe.As<byte, uint>(ref diskPage[PageBaseHeader.PageChecksumOffset]);
        Assert.That(storedCrc, Is.Not.EqualTo(0u), "CRC should be stamped (non-zero)");
        var computedCrc = Crc32CUtil.ComputeSkipping(diskPage, PageBaseHeader.PageChecksumOffset, PageBaseHeader.PageChecksumSize);
        Assert.That(computedCrc, Is.EqualTo(storedCrc), "CRC on disk should match computed CRC");

        // Verify ChangeRevision was incremented
        ref var header = ref Unsafe.As<byte, PageBaseHeader>(ref diskPage[0]);
        Assert.That(header.ChangeRevision, Is.GreaterThanOrEqualTo(1), "ChangeRevision should be incremented by checkpoint");
    }

    // ═══════════════════════════════════════════════════════════════
    // Concurrent Seqlock + Checkpoint Consistency
    // ═══════════════════════════════════════════════════════════════

    [Test]
    [CancelAfter(5000)]
    public void Checkpoint_UnderWriterContention_ProducesConsistentSnapshot()
    {
        CreateTestInfrastructure();

        const int filePageIndex = 5;
        const int checkpointIterations = 20;
        var stopWriter = 0;

        int memPageIdx;
        using (var guard = EpochGuard.Enter(_epochManager))
        {
            _mmf.RequestPageEpoch(filePageIndex, guard.Epoch, out memPageIdx);
        }

        // Mark dirty
        _mmf.IncrementDirty(memPageIdx);

        using var stagingPool = new StagingBufferPool(MemoryAllocator, AllocationResource, StagingBufferPool.MinCapacity);
        using var barrier = new Barrier(2);

        // Writer thread: repeatedly latches, writes a uniform byte, unlatches
        Exception writerException = null;
        var writerThread = new Thread(() =>
        {
            try
            {
                byte pattern = 0;
                barrier.SignalAndWait();

                while (Volatile.Read(ref stopWriter) == 0)
                {
                    pattern++;
                    using var guard = EpochGuard.Enter(_epochManager);
                    _mmf.RequestPageEpoch(filePageIndex, guard.Epoch, out var writerMemIdx);
                    if (_mmf.TryLatchPageExclusive(writerMemIdx))
                    {
                        unsafe
                        {
                            var pageAddr = _mmf.GetMemPageAddress(writerMemIdx);
                            new Span<byte>(pageAddr + PagedMMF.PageHeaderSize, PagedMMF.PageSize - PagedMMF.PageHeaderSize).Fill(pattern);
                        }
                        _mmf.UnlatchPageExclusive(writerMemIdx);
                    }
                }
            }
            catch (Exception ex)
            {
                writerException = ex;
            }
        });
        writerThread.Start();

        // Main thread: checkpoints repeatedly and verifies consistency
        barrier.SignalAndWait();

        for (int i = 0; i < checkpointIterations; i++)
        {
            _mmf.WritePagesForCheckpoint([memPageIdx], stagingPool, out _);

            var diskPage = new byte[PagedMMF.PageSize];
            _mmf.ReadPageDirect(filePageIndex, diskPage);
            VerifyPageConsistency(diskPage);
        }

        Volatile.Write(ref stopWriter, 1);
        writerThread.Join();
        Assert.That(writerException, Is.Null, $"Writer thread threw: {writerException}");
    }

    [Test]
    [CancelAfter(5000)]
    public void Checkpoint_MultiplePages_MultipleWriters_AllConsistent()
    {
        CreateTestInfrastructure();

        const int pageCount = 3;
        const int checkpointIterations = 15;
        var stopWriters = 0;
        var memPageIndices = new int[pageCount];

        // Load pages 5-7 into cache (avoid pages 0-3 which are reserved during ManagedPagedMMF init)
        const int firstPageIndex = 5;
        for (int p = 0; p < pageCount; p++)
        {
            using var guard = EpochGuard.Enter(_epochManager);
            _mmf.RequestPageEpoch(firstPageIndex + p, guard.Epoch, out memPageIndices[p]);
            _mmf.IncrementDirty(memPageIndices[p]);
        }

        using var stagingPool = new StagingBufferPool(MemoryAllocator, AllocationResource, StagingBufferPool.MinCapacity);
        using var barrier = new Barrier(pageCount + 1); // writers + checkpoint thread

        // Spawn one writer per page
        var writers = new Thread[pageCount];
        var writerExceptions = new Exception[pageCount];
        for (int p = 0; p < pageCount; p++)
        {
            var pageFileIndex = firstPageIndex + p;
            var writerByte = (byte)(0xA0 + p); // Each writer uses a unique base byte
            var idx = p;
            writers[p] = new Thread(() =>
            {
                try
                {
                    byte pattern = writerByte;
                    barrier.SignalAndWait();

                    while (Volatile.Read(ref stopWriters) == 0)
                    {
                        pattern = (byte)(writerByte + (pattern + 1) % 100);
                        using var guard = EpochGuard.Enter(_epochManager);
                        _mmf.RequestPageEpoch(pageFileIndex, guard.Epoch, out var writerMemIdx);
                        if (_mmf.TryLatchPageExclusive(writerMemIdx))
                        {
                            unsafe
                            {
                                var pageAddr = _mmf.GetMemPageAddress(writerMemIdx);
                                new Span<byte>(pageAddr + PagedMMF.PageHeaderSize, PagedMMF.PageSize - PagedMMF.PageHeaderSize).Fill(pattern);
                            }
                            _mmf.UnlatchPageExclusive(writerMemIdx);
                        }
                    }
                }
                catch (Exception ex)
                {
                    writerExceptions[idx] = ex;
                }
            });
            writers[p].Start();
        }

        // Checkpoint thread
        barrier.SignalAndWait();

        for (int i = 0; i < checkpointIterations; i++)
        {
            _mmf.WritePagesForCheckpoint(memPageIndices, stagingPool, out _);

            // Verify each page independently
            for (int p = 0; p < pageCount; p++)
            {
                var diskPage = new byte[PagedMMF.PageSize];
                _mmf.ReadPageDirect(firstPageIndex + p, diskPage);
                VerifyPageConsistency(diskPage);
            }
        }

        Volatile.Write(ref stopWriters, 1);
        foreach (var w in writers)
        {
            w.Join();
        }

        for (int i = 0; i < pageCount; i++)
        {
            Assert.That(writerExceptions[i], Is.Null, $"Writer {i} threw: {writerExceptions[i]}");
        }
    }
}
