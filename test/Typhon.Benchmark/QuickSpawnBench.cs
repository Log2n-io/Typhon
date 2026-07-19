using System;
using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Typhon.Engine;

namespace Typhon.Benchmark;

/// <summary>
/// Standalone spawn benchmark that works on both main and feature/ecs-api branches.
/// Uses only APIs that exist on both: Spawn, Open, Read, Write, Destroy, Commit.
/// Self-contained — no BenchmarkDotNet, no external dependencies.
/// </summary>
static class QuickSpawnBench
{
    public static void Run()
    {
        const int warmup = 500;
        const int iterations = 3000;
        const int entitiesPerIter = 100;

        Console.WriteLine($"QuickSpawnBench: {entitiesPerIter} entities/iter, {warmup} warmup, {iterations} measured");
        Console.WriteLine();

        var sc = new ServiceCollection();
        sc.AddLogging(b => b.SetMinimumLevel(LogLevel.Critical))
          .AddResourceRegistry()
          .AddMemoryAllocator()
          .AddEpochManager()
          .AddHighResolutionSharedTimer()
          .AddDeadlineWatchdog()
          .AddScopedManagedPagedMemoryMappedFile(options =>
          {
              options.DatabaseName = $"QuickBench_{Environment.ProcessId}";
              options.DatabaseCacheSize = (ulong)(32 * 1024 * PagedMMF.PageSize);
              options.PagesDebugPattern = false;
          })
          .AddScopedDatabaseEngine();

        using var sp = sc.BuildServiceProvider();
        sp.EnsureFileDeleted<ManagedPagedMMFOptions>();
        using var dbe = sp.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<BenchComp>();
        dbe.InitializeArchetypes();

        // ── Scenario 1: Create N + Commit ──
        RunScenario("Create100_Commit", warmup, iterations, () =>
        {
            using var t = dbe.CreateQuickTransaction();
            for (int i = 0; i < entitiesPerIter; i++)
            {
                var comp = new BenchComp { Value = i, Timestamp = 12345 };
                t.Spawn<BenchArch>(BenchArch.Data.Set(in comp));
            }
            t.Commit();
        });

        // Pre-populate for read/update/destroy benchmarks
        var ids = new EntityId[entitiesPerIter];
        {
            using var t = dbe.CreateQuickTransaction();
            for (int i = 0; i < entitiesPerIter; i++)
            {
                var comp = new BenchComp { Value = i, Timestamp = i * 100L };
                ids[i] = t.Spawn<BenchArch>(BenchArch.Data.Set(in comp));
            }
            t.Commit();
        }

        // ── Scenario 2: Read N (no commit) ──
        RunScenario("Read100", warmup, iterations, () =>
        {
            using var t = dbe.CreateQuickTransaction();
            int sum = 0;
            for (int i = 0; i < entitiesPerIter; i++)
            {
                var entity = t.Open(ids[i]);
                sum += entity.Read(BenchArch.Data).Value;
            }
        });

        // ── Scenario 3: Update N + Commit ──
        RunScenario("Update100_Commit", warmup, iterations, () =>
        {
            using var t = dbe.CreateQuickTransaction();
            for (int i = 0; i < entitiesPerIter; i++)
            {
                var mut = t.OpenMut(ids[i]);
                mut.Write(BenchArch.Data).Value = i + 10000;
            }
            t.Commit();
        });

        // ── Scenario 4: Single CRUD cycle ──
        RunScenario("SingleCRUD_Commit", warmup, iterations, () =>
        {
            using var t = dbe.CreateQuickTransaction();
            var comp = new BenchComp { Value = 42, Timestamp = 99 };
            var id = t.Spawn<BenchArch>(BenchArch.Data.Set(in comp));
            var entity = t.Open(id);
            _ = entity.Read(BenchArch.Data);
            var mut = t.OpenMut(id);
            mut.Write(BenchArch.Data).Value = 999;
            t.Commit();
        });

        // ── Scenario 5: Create 50 + Destroy 25 + Commit ──
        RunScenario("Create50_Destroy25", warmup, iterations, () =>
        {
            using var t = dbe.CreateQuickTransaction();
            Span<EntityId> batch = stackalloc EntityId[50];
            for (int i = 0; i < 50; i++)
            {
                var comp = new BenchComp { Value = i, Timestamp = 12345 };
                batch[i] = t.Spawn<BenchArch>(BenchArch.Data.Set(in comp));
            }
            for (int i = 0; i < 25; i++)
            {
                t.Destroy(batch[i]);
            }
            t.Commit();
        });

        // Cleanup
        try
        {
            var path = $"QuickBench_{Environment.ProcessId}.bin";
            if (System.IO.File.Exists(path))
            {
                System.IO.File.Delete(path);
            }
        }
        catch { }
    }

    static void RunScenario(string name, int warmup, int iterations, Action action)
    {
        // Warmup
        for (int i = 0; i < warmup; i++)
        {
            action();
        }

        // Measured
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < iterations; i++)
        {
            action();
        }
        sw.Stop();

        double usPerIter = sw.Elapsed.TotalMicroseconds / iterations;
        Console.WriteLine($"  {name,-25} {usPerIter,8:F1} us/iter");
    }
}
