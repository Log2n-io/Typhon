using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System;
using System.Diagnostics;
using System.IO;

namespace Typhon.Benchmark;

/// <summary>
/// Stopwatch-based profiler to isolate Remove vs Insert costs.
/// Uses batch timing to avoid per-iteration Stopwatch overhead.
/// Run with: dotnet run -c Release -- --profile-delete
/// </summary>
public static class BTreeDeleteProfile
{
    public static void Run()
    {
        const int preFillCount = 10_000;
        const int batchSize = 1_000; // remove N keys, then reinsert N keys
        const int batches = 200;
        const int warmupBatches = 20;

        var databaseName = $"BTreeProfile_{Environment.ProcessId}";
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

        unsafe
        {
            var segment = pmmf.AllocateChunkBasedSegment(PageBlockType.None, 500, sizeof(Index64Chunk));
            var epochDepth = epochManager.EnterScope();
            var tree = new LongSingleBTree<PersistentStore>(segment);

            // Pre-fill with keys 1..preFillCount
            var accessor = segment.CreateChunkAccessor();
            for (int i = 1; i <= preFillCount; i++)
            {
                tree.Add(i, i * 10, ref accessor);
            }
            accessor.Dispose();

            // Generate unique random keys within existing range for each batch
            // Each batch removes batchSize distinct keys, then reinserts them
            var rng = new Random(42);
            var batchKeys = new long[batchSize];
            var batchVals = new int[batchSize];

            long removeTotal = 0;
            long insertTotal = 0;
            int totalOps = 0;

            for (int b = 0; b < batches + warmupBatches; b++)
            {
                // Pick batchSize random distinct keys from [1..preFillCount]
                // Simple approach: pick random keys (may repeat, that's OK — Remove returns false for missing)
                for (int i = 0; i < batchSize; i++)
                {
                    batchKeys[i] = rng.NextInt64(1, preFillCount + 1);
                }

                // ── Batch Remove ──────────────────────────────────
                accessor = segment.CreateChunkAccessor();
                var t0 = Stopwatch.GetTimestamp();
                int removed = 0;
                for (int i = 0; i < batchSize; i++)
                {
                    if (tree.Remove(batchKeys[i], out batchVals[i], ref accessor))
                    {
                        removed++;
                    }
                }
                var t1 = Stopwatch.GetTimestamp();

                // ── Batch Reinsert ──────────────────────────────────
                var t2 = Stopwatch.GetTimestamp();
                for (int i = 0; i < batchSize; i++)
                {
                    if (batchVals[i] != 0) // only reinsert keys that were actually removed
                    {
                        tree.Add(batchKeys[i], batchVals[i], ref accessor);
                    }
                }
                var t3 = Stopwatch.GetTimestamp();
                accessor.Dispose();

                // Clear vals for next batch
                Array.Clear(batchVals);

                if (b >= warmupBatches)
                {
                    removeTotal += (t1 - t0);
                    insertTotal += (t3 - t2);
                    totalOps += removed;
                }
            }

            var freq = (double)Stopwatch.Frequency;
            var toNs = 1_000_000_000.0 / freq;

            Console.WriteLine($"=== BTree Delete vs Insert Profile ===");
            Console.WriteLine($"    {batches} batches × {batchSize} keys, {preFillCount:N0} entry tree");
            Console.WriteLine($"    Successful operations: {totalOps:N0}");
            Console.WriteLine();
            Console.WriteLine($"  Remove (batch avg):    {removeTotal * toNs / totalOps,8:F1} ns/op");
            Console.WriteLine($"  Insert (batch avg):    {insertTotal * toNs / totalOps,8:F1} ns/op");
            Console.WriteLine($"  Combined:              {(removeTotal + insertTotal) * toNs / totalOps,8:F1} ns/op");
            Console.WriteLine();
            Console.WriteLine($"  Remove / Insert ratio: {(double)removeTotal / insertTotal:F2}x");
            Console.WriteLine();

            // ── Baseline: Lookup ──────────────────────────────────
            long lookupTotal = 0;
            int lookupOps = batches * batchSize;

            // warmup
            for (int i = 0; i < warmupBatches * batchSize; i++)
            {
                accessor = segment.CreateChunkAccessor();
                tree.TryGet(rng.NextInt64(1, preFillCount + 1), ref accessor);
                accessor.Dispose();
            }

            accessor = segment.CreateChunkAccessor();
            var lt0 = Stopwatch.GetTimestamp();
            for (int i = 0; i < lookupOps; i++)
            {
                tree.TryGet(batchKeys[i % batchSize], ref accessor);
            }
            var lt1 = Stopwatch.GetTimestamp();
            accessor.Dispose();
            lookupTotal = lt1 - lt0;

            Console.WriteLine($"  Lookup (baseline):     {lookupTotal * toNs / lookupOps,8:F1} ns/op");

            // ── Baseline: Insert sequential ──────────────────────
            long appendTotal = 0;
            long nextKey = preFillCount + 1_000_000; // avoid collisions

            accessor = segment.CreateChunkAccessor();
            var at0 = Stopwatch.GetTimestamp();
            for (int i = 0; i < lookupOps; i++)
            {
                tree.Add(nextKey++, 42, ref accessor);
            }
            var at1 = Stopwatch.GetTimestamp();
            accessor.Dispose();
            appendTotal = at1 - at0;

            Console.WriteLine($"  Insert (append):       {appendTotal * toNs / lookupOps,8:F1} ns/op");
            Console.WriteLine();

            epochManager.ExitScope(epochDepth);
        }

        epochManager?.Dispose();
        pmmf?.Dispose();

        try { File.Delete($"{databaseName}.bin"); } catch { }
    }
}
