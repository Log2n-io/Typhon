using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System;
using System.IO;

namespace Typhon.Benchmark;

// ═══════════════════════════════════════════════════════════════════════
// Storage: ChangeSet (Page Dirty Tracking + Flush) Microbenchmarks
// ═══════════════════════════════════════════════════════════════════════

[SimpleJob(warmupCount: 3, iterationCount: 5)]
[MemoryDiagnoser]
[BenchmarkCategory("Storage")]
public class ChangeSetBenchmarks
{
    private ServiceProvider _serviceProvider;
    private ManagedPagedMMF _pmmf;
    private EpochManager _epochManager;
    private ChunkBasedSegment<PersistentStore> _segment;
    private string _databaseName;
    private int _epochDepth;

    private int[] _chunks16;
    private int[] _chunks64;

    [GlobalSetup]
    public unsafe void GlobalSetup()
    {
        _databaseName = $"ChangeSetBench_{Environment.ProcessId}";

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

        _segment = _pmmf.AllocateChunkBasedSegment(PageBlockType.None, 200, sizeof(Index64Chunk));
        _epochDepth = _epochManager.EnterScope();

        _chunks16 = new int[16];
        for (int i = 0; i < 16; i++)
        {
            _chunks16[i] = _segment.AllocateChunk(false);
        }

        _chunks64 = new int[64];
        for (int i = 0; i < 64; i++)
        {
            _chunks64[i] = _segment.AllocateChunk(false);
        }
    }

    [GlobalCleanup]
    public void GlobalCleanup()
    {
        _epochManager.ExitScope(_epochDepth);
        _epochManager?.Dispose();
        _pmmf?.Dispose();
        _serviceProvider?.Dispose();

        try { File.Delete($"{_databaseName}.bin"); } catch { }
    }

    /// <summary>
    /// Create a ChangeSet instance. Measures allocation cost of the dirty-page tracker.
    /// </summary>
    [Benchmark]
    public void CreateChangeSet()
    {
        _pmmf.CreateChangeSet();
    }

    /// <summary>
    /// Mark 16 pages dirty + commit + SaveChanges (flush to disk).
    /// Measures the full dirty-tracking and I/O flush path.
    /// </summary>
    [Benchmark]
    public unsafe void SaveChanges_16Pages()
    {
        var cs = _pmmf.CreateChangeSet();
        var accessor = _segment.CreateChunkAccessor(cs);
        for (int i = 0; i < 16; i++)
        {
            accessor.GetChunkAddress(_chunks16[i], dirty: true);
        }
        accessor.CommitChanges();
        cs.SaveChanges();
        accessor.Dispose();
    }

    /// <summary>
    /// Mark 64 pages dirty + SaveChanges. Measures I/O scaling with more dirty pages.
    /// </summary>
    [Benchmark]
    public unsafe void SaveChanges_64Pages()
    {
        var cs = _pmmf.CreateChangeSet();
        var accessor = _segment.CreateChunkAccessor(cs);
        for (int i = 0; i < 64; i++)
        {
            accessor.GetChunkAddress(_chunks64[i], dirty: true);
        }
        accessor.CommitChanges();
        cs.SaveChanges();
        accessor.Dispose();
    }
}
