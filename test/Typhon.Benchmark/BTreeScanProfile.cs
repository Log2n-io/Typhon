using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System;
using System.IO;

namespace Typhon.Benchmark;

/// <summary>
/// Profiling harness for SequentialScan_100 — mirrors the BDN benchmark exactly.
/// Run with: dotnet run -c Release -- --profile-scan
/// </summary>
public static class BTreeScanProfile
{
    public static unsafe void Run()
    {
        const int preFillCount = 10_000;
        const int scanCount = 100;
        const int iterations = 500_000;
        const int warmup = 50_000;

        var databaseName = $"BTreeScanProfile_{Environment.ProcessId}";
        var dcs = 200 * 1024 * PagedMMF.PageSize;

        var sc = new ServiceCollection();
        sc.AddLogging(b => b.SetMinimumLevel(LogLevel.Critical))
          .AddResourceRegistry()
          .AddMemoryAllocator()
          .AddEpochManager()
          .AddScopedManagedPagedMemoryMappedFile(options =>
          {
              options.DatabaseName = databaseName;
              options.DatabaseCacheSize = (ulong)dcs;
              options.PagesDebugPattern = false;
          });

        using var sp = sc.BuildServiceProvider();
        sp.EnsureFileDeleted<ManagedPagedMMFOptions>();
        var pmmf = sp.GetRequiredService<ManagedPagedMMF>();
        var epochManager = sp.GetRequiredService<EpochManager>();

        var segment = pmmf.AllocateChunkBasedSegment(PageBlockType.None, 500, sizeof(Index64Chunk));
        var epochDepth = epochManager.EnterScope();
        var tree = new LongSingleBTree<PersistentStore>(segment);

        // Pre-fill with keys 1..preFillCount (same as BDN benchmark)
        var accessor = segment.CreateChunkAccessor();
        for (int i = 1; i <= preFillCount; i++)
        {
            tree.Add(i, i * 10, ref accessor);
        }
        accessor.Dispose();

        // Warmup
        for (int iter = 0; iter < warmup; iter++)
        {
            accessor = segment.CreateChunkAccessor();
            for (int i = 1; i <= scanCount; i++)
            {
                tree.TryGet(i, ref accessor);
            }
            accessor.Dispose();
        }

        // Measured iterations — this is the hot loop dotTrace will capture
        for (int iter = 0; iter < iterations; iter++)
        {
            accessor = segment.CreateChunkAccessor();
            for (int i = 1; i <= scanCount; i++)
            {
                tree.TryGet(i, ref accessor);
            }
            accessor.Dispose();
        }

        epochManager.ExitScope(epochDepth);
        epochManager?.Dispose();
        pmmf?.Dispose();

        try { File.Delete($"{databaseName}.bin"); } catch { }
    }
}
