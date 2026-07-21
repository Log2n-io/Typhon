using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System;
using System.IO;

namespace Typhon.Benchmark;

// ═══════════════════════════════════════════════════════════════════════
// Storage: PagedMMF / Page Cache Microbenchmarks
// ═══════════════════════════════════════════════════════════════════════

[SimpleJob(warmupCount: 3, iterationCount: 5)]
[MemoryDiagnoser]
[BenchmarkCategory("Storage", "Regression")]
public class PagedMMFBenchmarks
{
    private ServiceProvider _serviceProvider;
    private ManagedPagedMMF _pmmf;
    private EpochManager _epochManager;
    private LogicalSegment<PersistentStore> _segment;
    private string _databaseName;
    private int _epochDepth;

    // Pre-allocated page indices for cache hit/miss scenarios
    private int _hotPageIndex;
    private int _coldStartIndex;
    private const int ColdPageCount = 64;

    [GlobalSetup]
    public unsafe void GlobalSetup()
    {
        _databaseName = $"PagedMMFBench_{Environment.ProcessId}";

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
        _pmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        _epochManager = _serviceProvider.GetRequiredService<EpochManager>();

        // Create a logical segment with enough pages for benchmarking
        _segment = _pmmf.AllocateSegment(PageBlockType.None, ColdPageCount + 10);

        // Enter epoch scope for the benchmark lifetime
        _epochDepth = _epochManager.EnterScope();

        // Warm the hot page into cache
        _hotPageIndex = 0;
        _segment.GetPageAddress(_hotPageIndex, _epochManager.GlobalEpoch, out _);

        // Cold pages start after the hot page range
        _coldStartIndex = 2;
    }

    [GlobalCleanup]
    public void GlobalCleanup()
    {
        _epochManager.ExitScope(_epochDepth);
        _segment?.Dispose();
        _epochManager?.Dispose();
        _pmmf?.Dispose();
        _serviceProvider?.Dispose();

        try { File.Delete($"{_databaseName}.bin"); } catch { }
    }

    /// <summary>
    /// Read a page that is already in the page cache (hot path).
    /// Measures the cost of a page cache hit: epoch check + memory lookup.
    /// </summary>
    [Benchmark]
    public unsafe void CacheHit()
    {
        _segment.GetPageAddress(_hotPageIndex, _epochManager.GlobalEpoch, out _);
    }

    /// <summary>
    /// Read pages not likely in cache, triggering page resolution.
    /// Cycles through many pages to reduce MRU cache effectiveness.
    /// </summary>
    [Benchmark(OperationsPerInvoke = ColdPageCount)]
    public unsafe void CacheMiss()
    {
        for (int i = 0; i < ColdPageCount; i++)
        {
            _segment.GetPageAddress(_coldStartIndex + i, _epochManager.GlobalEpoch, out _);
        }
    }

    /// <summary>
    /// Allocate a new page via ManagedPagedMMF.
    /// Measures the allocation path: free-list scan + page initialization.
    /// </summary>
    [Benchmark]
    public void PageAllocation()
    {
        _pmmf.AllocatePage();
    }
}
