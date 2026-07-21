using BenchmarkDotNet.Attributes;
using Microsoft.Win32.SafeHandles;
using System;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;

namespace Typhon.Benchmark;

/// <summary>
/// Benchmark comparing different page saving strategies for durability.
///
/// This benchmark measures the performance difference between:
/// 1. Async write (fire-and-forget, no durability guarantee until OS flushes)
/// 2. Sync write without flush (blocking write, but data may be in OS buffer cache)
/// 3. Sync write with flush (fsync - true durability guarantee)
///
/// This is relevant for UoW crash recovery design where we need to understand
/// the cost of different durability modes (Deferred vs Immediate).
/// </summary>
[SimpleJob(warmupCount: 3, iterationCount: 5)]
[MemoryDiagnoser]
[BenchmarkCategory("Persistence")]
public class PageSavingStrategyBenchmark
{
    private const int PageSize = PagedMMF.PageSize; // 8192 bytes

    private string _testFilePath = null!;
    private SafeFileHandle _asyncFileHandle = null!;
    private SafeFileHandle _syncFileHandle = null!;
    private byte[] _pageData = null!;
    private Memory<byte> _pageMemory;

    // Pre-allocated buffer for batched writes (avoids allocation during benchmark)
    private byte[] _batchedPageData = null!;
    private Memory<byte> _batchedPageMemory;

    /// <summary>
    /// Number of pages to write per benchmark iteration.
    /// Simulates a UoW with multiple transaction commits.
    /// </summary>
    [Params(1, 10, 100)]
    public int PageCount { get; set; }

    /// <summary>
    /// Whether to write pages sequentially or randomly scattered.
    /// Sequential writes can benefit from OS coalescing.
    /// </summary>
    [Params(true, false)]
    public bool SequentialWrites { get; set; }

    [GlobalSetup]
    public void GlobalSetup()
    {
        _testFilePath = Path.Combine(Path.GetTempPath(), $"PageSavingBench_{Guid.NewGuid()}.dat");

        // Pre-allocate file with enough space for all pages (max 100 pages * 8KB = 800KB)
        // Plus some extra for random scatter testing
        var fileSize = PageSize * 1000;

        // Create the file and pre-allocate
        using (var fs = File.Create(_testFilePath))
        {
            fs.SetLength(fileSize);
        }

        // Open with async options (like PagedMMF does)
        // Use FileShare.ReadWrite to allow multiple handles on the same file
        _asyncFileHandle = File.OpenHandle(
            _testFilePath,
            FileMode.Open,
            FileAccess.ReadWrite,
            FileShare.ReadWrite,
            FileOptions.Asynchronous | FileOptions.RandomAccess);

        // Open with sync options for comparison
        _syncFileHandle = File.OpenHandle(
            _testFilePath,
            FileMode.Open,
            FileAccess.ReadWrite,
            FileShare.ReadWrite,
            FileOptions.RandomAccess);

        // Create page data with pattern (like PagedMMF does for debugging)
        _pageData = new byte[PageSize];
        for (int i = 0; i < PageSize; i++)
        {
            _pageData[i] = (byte)(i & 0xFF);
        }
        _pageMemory = _pageData.AsMemory();

        // Pre-allocate batched buffer for the batched write benchmark
        // This avoids allocation overhead during the benchmark itself
        _batchedPageData = new byte[PageCount * PageSize];
        for (int i = 0; i < PageCount; i++)
        {
            Array.Copy(_pageData, 0, _batchedPageData, i * PageSize, PageSize);
        }
        _batchedPageMemory = _batchedPageData.AsMemory();
    }

    [GlobalCleanup]
    public void GlobalCleanup()
    {
        _asyncFileHandle?.Dispose();
        _syncFileHandle?.Dispose();

        if (_testFilePath != null && File.Exists(_testFilePath))
        {
            File.Delete(_testFilePath);
        }
    }

    /// <summary>
    /// Async write without waiting - fire and forget.
    /// This is the fastest but provides no durability guarantee until OS flushes.
    /// Used in "Deferred" durability mode where UoW.Flush() will do the actual sync.
    /// </summary>
    [Benchmark(Baseline = true)]
    public async Task AsyncWrite_NoFlush()
    {
        var tasks = new ValueTask[PageCount];

        for (int i = 0; i < PageCount; i++)
        {
            var offset = GetPageOffset(i);
            tasks[i] = RandomAccess.WriteAsync(_asyncFileHandle, _pageMemory, offset);
        }

        // Wait for all writes to complete (but not flushed to disk)
        for (int i = 0; i < PageCount; i++)
        {
            await tasks[i];
        }
    }

    /// <summary>
    /// Async write followed by a single FlushToDisk at the end.
    /// This is what UoW.Flush() would do in Deferred mode.
    /// </summary>
    [Benchmark]
    public async Task AsyncWrite_WithFlushAtEnd()
    {
        var tasks = new ValueTask[PageCount];

        for (int i = 0; i < PageCount; i++)
        {
            var offset = GetPageOffset(i);
            tasks[i] = RandomAccess.WriteAsync(_asyncFileHandle, _pageMemory, offset);
        }

        // Wait for all writes to complete
        for (int i = 0; i < PageCount; i++)
        {
            await tasks[i];
        }

        // Single fsync at the end
        RandomAccess.FlushToDisk(_asyncFileHandle);
    }

    /// <summary>
    /// Sync write without flush - blocking but data may be in OS buffer cache.
    /// This blocks the thread but doesn't guarantee durability.
    /// </summary>
    [Benchmark]
    public void SyncWrite_NoFlush()
    {
        for (int i = 0; i < PageCount; i++)
        {
            var offset = GetPageOffset(i);
            RandomAccess.Write(_syncFileHandle, _pageData.AsSpan(), offset);
        }
    }

    /// <summary>
    /// Sync write with flush after each page.
    /// This is the most durable but slowest - fsync after every write.
    /// Would be used for "Immediate" durability mode on critical data.
    /// </summary>
    [Benchmark]
    public void SyncWrite_FlushEachPage()
    {
        for (int i = 0; i < PageCount; i++)
        {
            var offset = GetPageOffset(i);
            RandomAccess.Write(_syncFileHandle, _pageData.AsSpan(), offset);
            RandomAccess.FlushToDisk(_syncFileHandle);
        }
    }

    /// <summary>
    /// Sync write followed by a single FlushToDisk at the end.
    /// This is what a traditional database commit does.
    /// </summary>
    [Benchmark]
    public void SyncWrite_FlushAtEnd()
    {
        for (int i = 0; i < PageCount; i++)
        {
            var offset = GetPageOffset(i);
            RandomAccess.Write(_syncFileHandle, _pageData.AsSpan(), offset);
        }

        // Single fsync at the end
        RandomAccess.FlushToDisk(_syncFileHandle);
    }

    /// <summary>
    /// Batched async writes - gather multiple pages into one write operation.
    /// This simulates PagedMMF's contiguous page write optimization.
    /// Uses pre-allocated buffer to avoid allocation overhead during benchmark.
    /// </summary>
    [Benchmark]
    public async Task AsyncWrite_Batched_WithFlush()
    {
        if (!SequentialWrites)
        {
            // Batching only makes sense for sequential writes
            await AsyncWrite_WithFlushAtEnd();
            return;
        }

        // Write all pages in a single operation using pre-allocated buffer
        await RandomAccess.WriteAsync(_asyncFileHandle, _batchedPageMemory, 0);
        RandomAccess.FlushToDisk(_asyncFileHandle);
    }

    /// <summary>
    /// Simulates group commit: multiple writes batched together with single fsync.
    /// This is what databases use to amortize fsync cost across transactions.
    /// </summary>
    [Benchmark]
    public async Task GroupCommit_AsyncWritesBatched()
    {
        // Simulate 4 concurrent "transactions" each writing PageCount/4 pages
        // Then single fsync at end (group commit)
        var groupCount = Math.Min(4, PageCount);
        var pagesPerGroup = Math.Max(1, PageCount / groupCount);

        var allTasks = new ValueTask[PageCount];
        var taskIndex = 0;

        for (int g = 0; g < groupCount; g++)
        {
            for (int p = 0; p < pagesPerGroup && taskIndex < PageCount; p++)
            {
                var offset = GetPageOffset(taskIndex);
                allTasks[taskIndex] = RandomAccess.WriteAsync(_asyncFileHandle, _pageMemory, offset);
                taskIndex++;
            }
        }

        // Wait for all writes
        for (int i = 0; i < taskIndex; i++)
        {
            await allTasks[i];
        }

        // Single fsync (group commit)
        RandomAccess.FlushToDisk(_asyncFileHandle);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private long GetPageOffset(int pageIndex)
    {
        if (SequentialWrites)
        {
            return pageIndex * (long)PageSize;
        }
        else
        {
            // Scatter pages randomly across the file (but deterministically for reproducibility)
            // Use a simple hash to scatter while staying within file bounds
            var scattered = ((pageIndex * 17) + 5) % 500;
            return scattered * (long)PageSize;
        }
    }
}
