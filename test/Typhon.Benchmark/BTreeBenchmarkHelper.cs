using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Typhon.Benchmark;

// ═══════════════════════════════════════════════════════════════════════
// BTree Benchmark Infrastructure
// ═══════════════════════════════════════════════════════════════════════

/// <summary>
/// Shared setup/teardown for BTree benchmarks. Manages DI, PMMF, epoch scope.
/// Each benchmark class creates an instance and calls Setup/Cleanup.
/// </summary>
internal sealed class BTreeBenchmarkHelper : IDisposable
{
    private ServiceProvider _serviceProvider;
    private string _databaseName;
    private int _epochDepth;

    public ManagedPagedMMF Pmmf { get; private set; }
    public EpochManager EpochManager { get; private set; }

    /// <summary>
    /// Initialize the database engine with enough cache for benchmark workloads.
    /// </summary>
    /// <param name="segmentPages">Number of pages for the segment allocation (default 500 = ~2MB of chunks).</param>
    public void Setup(int segmentPages = 500)
    {
        _databaseName = $"BTreeBench_{Environment.ProcessId}_{Thread.CurrentThread.ManagedThreadId}";

        var dcs = 200 * 1024;
        dcs *= PagedMMF.PageSize;

        var sc = new ServiceCollection();
        sc.AddLogging(b => b.SetMinimumLevel(LogLevel.Critical))
          .AddResourceRegistry()
          .AddMemoryAllocator()
          .AddEpochManager()
          .AddScopedManagedPagedMemoryMappedFile(options =>
          {
              options.DatabaseName = _databaseName;
              options.DatabaseCacheSize = (ulong)dcs;
              options.PagesDebugPattern = false;
          });

        _serviceProvider = sc.BuildServiceProvider();
        _serviceProvider.EnsureFileDeleted<ManagedPagedMMFOptions>();
        Pmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        EpochManager = _serviceProvider.GetRequiredService<EpochManager>();
        _epochDepth = EpochManager.EnterScope();
    }

    public void Dispose()
    {
        EpochManager.ExitScope(_epochDepth);
        EpochManager?.Dispose();
        Pmmf?.Dispose();
        _serviceProvider?.Dispose();

        try { File.Delete($"{_databaseName}.bin"); } catch { /* best effort */ }
    }

    /// <summary>
    /// Allocate a ChunkBasedSegment<PersistentStore> for the given chunk type.
    /// </summary>
    public unsafe ChunkBasedSegment<PersistentStore> AllocateSegment<TChunk>(int pageCount = 500) where TChunk : unmanaged
        => Pmmf.AllocateChunkBasedSegment(PageBlockType.None, pageCount, sizeof(TChunk));

    /// <summary>
    /// Pre-fill a LongSingleBTree<PersistentStore> with sequential keys [1..count].
    /// </summary>
    public static void PreFillLong(LongSingleBTree<PersistentStore> tree, ChunkBasedSegment<PersistentStore> segment, int count)
    {
        var accessor = segment.CreateChunkAccessor();
        for (int i = 1; i <= count; i++)
        {
            tree.Add(i, i * 10, ref accessor);
        }
        accessor.Dispose();
    }

    /// <summary>
    /// Pre-fill an IntSingleBTree<PersistentStore> with sequential keys [1..count].
    /// </summary>
    public static void PreFillInt(IntSingleBTree<PersistentStore> tree, ChunkBasedSegment<PersistentStore> segment, int count)
    {
        var accessor = segment.CreateChunkAccessor();
        for (int i = 1; i <= count; i++)
        {
            tree.Add(i, i * 10, ref accessor);
        }
        accessor.Dispose();
    }

    /// <summary>
    /// Pre-fill a ShortSingleBTree<PersistentStore> with sequential keys [1..count].
    /// </summary>
    public static void PreFillShort(ShortSingleBTree<PersistentStore> tree, ChunkBasedSegment<PersistentStore> segment, int count)
    {
        var accessor = segment.CreateChunkAccessor();
        for (short i = 1; i <= count; i++)
        {
            tree.Add(i, i * 10, ref accessor);
        }
        accessor.Dispose();
    }

    /// <summary>
    /// Pre-fill a String64SingleBTree<PersistentStore> with keys "key_00001".."key_NNNNN".
    /// </summary>
    public static void PreFillString64(String64SingleBTree<PersistentStore> tree, ChunkBasedSegment<PersistentStore> segment, int count)
    {
        var accessor = segment.CreateChunkAccessor();
        for (int i = 1; i <= count; i++)
        {
            tree.Add($"key_{i:D5}", i * 10, ref accessor);
        }
        accessor.Dispose();
    }

    /// <summary>
    /// Pre-fill an IntMultipleBTree<PersistentStore> with sequential keys, each key getting one value.
    /// </summary>
    public static void PreFillIntMultiple(IntMultipleBTree<PersistentStore> tree, ChunkBasedSegment<PersistentStore> segment, int count)
    {
        var accessor = segment.CreateChunkAccessor();
        for (int i = 1; i <= count; i++)
        {
            tree.Add(i, i * 10, ref accessor);
        }
        accessor.Dispose();
    }

    /// <summary>
    /// Generate an array of random longs in [1..maxKey] with a fixed seed for reproducibility.
    /// </summary>
    public static long[] GenerateRandomLongKeys(int count, long maxKey, int seed = 42)
    {
        var keys = new long[count];
        var rng = new Random(seed);
        for (int i = 0; i < count; i++)
        {
            keys[i] = rng.NextInt64(1, maxKey + 1);
        }
        return keys;
    }

    /// <summary>
    /// Generate an array of random ints in [1..maxKey] with a fixed seed for reproducibility.
    /// </summary>
    public static int[] GenerateRandomIntKeys(int count, int maxKey, int seed = 42)
    {
        var keys = new int[count];
        var rng = new Random(seed);
        for (int i = 0; i < count; i++)
        {
            keys[i] = rng.Next(1, maxKey + 1);
        }
        return keys;
    }

    /// <summary>
    /// Run a concurrent workload: spawn ThreadCount tasks, each executing the given action.
    /// </summary>
    public static void RunConcurrent(int threadCount, Action<int> perThreadAction)
    {
        if (threadCount == 1)
        {
            perThreadAction(0);
            return;
        }

        var tasks = new Task[threadCount];
        for (int t = 0; t < threadCount; t++)
        {
            var threadId = t;
            tasks[t] = Task.Run(() => perThreadAction(threadId));
        }
        Task.WaitAll(tasks);
    }

    /// <summary>
    /// Run a concurrent workload with a barrier start so all threads begin simultaneously.
    /// </summary>
    public static void RunConcurrentWithBarrier(int threadCount, EpochManager epochManager, Action<int> perThreadAction)
    {
        if (threadCount == 1)
        {
            perThreadAction(0);
            return;
        }

        using var barrier = new Barrier(threadCount);
        var tasks = new Task[threadCount];
        for (int t = 0; t < threadCount; t++)
        {
            var threadId = t;
            tasks[t] = Task.Run(() =>
            {
                // Each thread needs its own epoch scope
                var depth = epochManager.EnterScope();
                try
                {
                    barrier.SignalAndWait();
                    perThreadAction(threadId);
                }
                finally
                {
                    epochManager.ExitScope(depth);
                }
            });
        }
        Task.WaitAll(tasks);
    }
}
