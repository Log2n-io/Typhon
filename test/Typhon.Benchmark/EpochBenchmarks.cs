using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System;
using System.IO;
using System.Runtime.InteropServices;
using Typhon.Engine;
using Typhon.Schema.Definition;

namespace Typhon.Benchmark;

// ═══════════════════════════════════════════════════════════════════════
// Phase 0: EpochGuard Microbenchmarks
// ═══════════════════════════════════════════════════════════════════════

[SimpleJob(warmupCount: 3, iterationCount: 5)]
[MemoryDiagnoser]
[BenchmarkCategory("Epoch", "Regression")]
public class EpochGuardBenchmarks
{
    private EpochManager _epochManager;
    private ServiceProvider _serviceProvider;

    [GlobalSetup]
    public void GlobalSetup()
    {
        var sc = new ServiceCollection();
        sc.AddLogging(b => b.SetMinimumLevel(LogLevel.Critical))
          .AddResourceRegistry()
          .AddEpochManager();

        _serviceProvider = sc.BuildServiceProvider();
        _epochManager = _serviceProvider.GetRequiredService<EpochManager>();
    }

    [GlobalCleanup]
    public void GlobalCleanup()
    {
        _epochManager?.Dispose();
        _serviceProvider?.Dispose();
    }

    /// <summary>
    /// Target: ≤ 5ns. Single outermost enter/exit pair.
    /// </summary>
    [Benchmark]
    public void EnterExit()
    {
        var depth = _epochManager.EnterScope();
        _epochManager.ExitScope(depth);
    }

    /// <summary>
    /// Three nested scopes — only outermost does Interlocked.Increment.
    /// </summary>
    [Benchmark]
    public void NestedThreeLevels()
    {
        var d1 = _epochManager.EnterScope();
        var d2 = _epochManager.EnterScope();
        var d3 = _epochManager.EnterScope();
        _epochManager.ExitScope(d3);
        _epochManager.ExitScope(d2);
        _epochManager.ExitScope(d1);
    }

    /// <summary>
    /// MinActiveEpoch property access — scans registry slots.
    /// </summary>
    [Benchmark]
    public long MinActiveEpoch()
    {
        return _epochManager.MinActiveEpoch;
    }

    /// <summary>
    /// MinActiveEpoch while one thread is pinned (more realistic).
    /// </summary>
    [Benchmark]
    public long MinActiveEpoch_WhilePinned()
    {
        var depth = _epochManager.EnterScope();
        var result = _epochManager.MinActiveEpoch;
        _epochManager.ExitScope(depth);
        return result;
    }
}

// ═══════════════════════════════════════════════════════════════════════
// Phase 2: ChunkAccessor<PersistentStore> Microbenchmarks
// ═══════════════════════════════════════════════════════════════════════

[SimpleJob(warmupCount: 3, iterationCount: 5)]
[BenchmarkCategory("ChunkAccessor<PersistentStore>", "Regression")]
public class ChunkAccessorBenchmarks
{
    private ServiceProvider _serviceProvider;
    private ManagedPagedMMF _pmmf;
    private EpochManager _epochManager;
    private ChunkBasedSegment<PersistentStore> _segment;
    private string _databaseName;

    // Pre-allocated chunk IDs for various access patterns
    private int[] _chunks4;
    private int[] _chunks17;
    private int _singleChunkId;
    private int _epochDepth;

    [GlobalSetup]
    public unsafe void GlobalSetup()
    {
        // Each BDN process needs a unique database file to avoid file locking conflicts
        _databaseName = $"ChunkAccessorBench_{Environment.ProcessId}";

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

        // Allocate a segment with enough pages so 17 chunks span multiple pages
        _segment = _pmmf.AllocateChunkBasedSegment(PageBlockType.None, 100, sizeof(Index64Chunk));

        // Enter epoch scope for the entire benchmark lifetime
        _epochDepth = _epochManager.EnterScope();

        // Pre-allocate chunks
        _singleChunkId = _segment.AllocateChunk(false);

        _chunks4 = new int[4];
        for (int i = 0; i < 4; i++)
        {
            _chunks4[i] = _segment.AllocateChunk(false);
        }

        _chunks17 = new int[17];
        for (int i = 0; i < 17; i++)
        {
            _chunks17[i] = _segment.AllocateChunk(false);
        }
    }

    [GlobalCleanup]
    public void GlobalCleanup()
    {
        _epochManager.ExitScope(_epochDepth);
        _epochManager?.Dispose();
        _pmmf?.Dispose();
        _serviceProvider?.Dispose();

        // Clean up the per-process database file
        try { File.Delete($"{_databaseName}.bin"); } catch { }
    }

    /// <summary>
    /// Target: ≤ 5ns. Repeated access to same chunk (MRU hit every time).
    /// </summary>
    [Benchmark(OperationsPerInvoke = 1000)]
    public unsafe void MRU_Hit()
    {
        var accessor = _segment.CreateChunkAccessor();
        for (int i = 0; i < 1000; i++)
        {
            accessor.GetChunkAddress(_singleChunkId);
        }
        accessor.Dispose();
    }

    /// <summary>
    /// Target: ≤ 8ns. Rotate through 4 cached chunks (SIMD search path).
    /// </summary>
    [Benchmark(OperationsPerInvoke = 1000)]
    public unsafe void SIMD_Hit_4Chunks()
    {
        var accessor = _segment.CreateChunkAccessor();
        var chunks = _chunks4;
        for (int i = 0; i < 1000; i++)
        {
            accessor.GetChunkAddress(chunks[i & 3]);
        }
        accessor.Dispose();
    }

    /// <summary>
    /// Target: ≤ 25ns (excl. I/O). 17 chunks forces eviction on every access after warmup.
    /// </summary>
    [Benchmark(OperationsPerInvoke = 17)]
    public unsafe void Eviction_17Chunks()
    {
        var accessor = _segment.CreateChunkAccessor();
        var chunks = _chunks17;
        for (int i = 0; i < 17; i++)
        {
            accessor.GetChunkAddress(chunks[i]);
        }
        accessor.Dispose();
    }

    /// <summary>
    /// Target: ≤ 100ns. Flush all 16 dirty slots to ChangeSet.
    /// </summary>
    [Benchmark]
    public unsafe void CommitChanges_AllDirty()
    {
        var changeSet = _pmmf.CreateChangeSet();
        var accessor = _segment.CreateChunkAccessor(changeSet);

        // Load and dirty 16 distinct chunks
        for (int i = 0; i < 16; i++)
        {
            accessor.GetChunkAddress(_chunks17[i], dirty: true);
        }

        accessor.CommitChanges();
        accessor.Dispose();
    }

    /// <summary>
    /// Target: ≤ 150ns. Full-cache dispose with 16 slots loaded.
    /// </summary>
    [Benchmark]
    public unsafe void Dispose_16Slots()
    {
        var changeSet = _pmmf.CreateChangeSet();
        var accessor = _segment.CreateChunkAccessor(changeSet);

        // Fill all 16 slots, some dirty
        for (int i = 0; i < 16; i++)
        {
            accessor.GetChunkAddress(_chunks17[i], dirty: (i & 1) == 0);
        }

        accessor.Dispose();
    }
}

// ═══════════════════════════════════════════════════════════════════════
// Phase 3: End-to-End Benchmarks (Transaction + BTree)
// ═══════════════════════════════════════════════════════════════════════

[Component("Typhon.Benchmark.BenchComp", 1)]
[StructLayout(LayoutKind.Sequential)]
public struct BenchComp
{
    [Field]
    public int Value;

    [Field]
    public long Timestamp;
}

[Archetype]
class BenchArch : Archetype<BenchArch>
{
    public static readonly Comp<BenchComp> Data = Register<BenchComp>();
}

[SimpleJob(warmupCount: 2, iterationCount: 3)]
[MemoryDiagnoser]
[BenchmarkCategory("EndToEnd", "Regression")]
public class TransactionBenchmarks
{
    private ServiceProvider _serviceProvider;
    private DatabaseEngine _dbe;
    private EntityId[] _entityIds;
    private string _databaseName;

    [Params(100, 1000)]
    public int EntityCount;

    [GlobalSetup]
    public void GlobalSetup()
    {
        _databaseName = $"TransactionBench_{Environment.ProcessId}";

        var dcs = 200 * 1024;
        dcs *= PagedMMF.PageSize;

        var sc = new ServiceCollection();
        sc.AddLogging(b => b.SetMinimumLevel(LogLevel.Critical))
          .AddResourceRegistry()
          .AddMemoryAllocator()
          .AddEpochManager()
          .AddHighResolutionSharedTimer()
          .AddDeadlineWatchdog()
          .AddScopedManagedPagedMemoryMappedFile(options =>
          {
              options.DatabaseName = _databaseName;
              options.DatabaseCacheSize = (ulong)dcs;
              options.PagesDebugPattern = false;
          })
          .AddScopedDatabaseEngine();

        _serviceProvider = sc.BuildServiceProvider();
        _serviceProvider.EnsureFileDeleted<ManagedPagedMMFOptions>();
        _dbe = _serviceProvider.GetRequiredService<DatabaseEngine>();
        _dbe.RegisterComponentFromAccessor<BenchComp>();

        _dbe.InitializeArchetypes();

        // Pre-populate entities for read/update benchmarks
        _entityIds = new EntityId[EntityCount];
        using var t = _dbe.CreateQuickTransaction();
        for (int i = 0; i < EntityCount; i++)
        {
            var comp = new BenchComp { Value = i, Timestamp = DateTime.UtcNow.Ticks };
            _entityIds[i] = t.Spawn<BenchArch>(BenchArch.Data.Set(in comp));
        }
        t.Commit();
    }

    [GlobalCleanup]
    public void GlobalCleanup()
    {
        _dbe?.Dispose();
        _serviceProvider?.Dispose();

        try { File.Delete($"{_databaseName}.bin"); } catch { }
    }

    /// <summary>
    /// Full spawn-read-commit cycle. Measures epoch overhead vs ref-count savings.
    /// </summary>
    [Benchmark]
    public void Transaction_CreateReadCommit()
    {
        using var t = _dbe.CreateQuickTransaction();
        var comp = new BenchComp { Value = 42, Timestamp = 12345 };
        var eid = t.Spawn<BenchArch>(BenchArch.Data.Set(in comp));
        var entity = t.Open(eid);
        _ = entity.Read(BenchArch.Data);
        t.Commit();
    }

    /// <summary>
    /// Bulk read of pre-existing entities.
    /// </summary>
    [Benchmark]
    public void Transaction_BulkRead()
    {
        using var t = _dbe.CreateQuickTransaction();
        for (int i = 0; i < _entityIds.Length; i++)
        {
            var entity = t.Open(_entityIds[i]);
            _ = entity.Read(BenchArch.Data);
        }
    }

    /// <summary>
    /// Bulk update of pre-existing entities.
    /// </summary>
    [Benchmark]
    public void Transaction_BulkUpdate()
    {
        using var t = _dbe.CreateQuickTransaction();
        for (int i = 0; i < _entityIds.Length; i++)
        {
            var entity = t.OpenMut(_entityIds[i]);
            ref var comp = ref entity.Write(BenchArch.Data);
            comp.Value = i + 1000;
            comp.Timestamp = DateTime.UtcNow.Ticks;
        }
        t.Commit();
    }
}
