using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Typhon.Engine;

namespace Typhon.Benchmark;

/// <summary>
/// Profiling harness for Spawn performance. Runs WriteHeavy_Batch logic in a tight loop
/// without BDN overhead, suitable for dotTrace sampling.
/// </summary>
static class SpawnProfile
{
    public static void Run()
    {
        Console.WriteLine("SpawnProfile: Setting up engine...");

        var dcs = 200 * 1024 * PagedMMF.PageSize;
        var sc = new ServiceCollection();
        sc.AddLogging(b => b.SetMinimumLevel(LogLevel.Critical))
          .AddResourceRegistry()
          .AddMemoryAllocator()
          .AddEpochManager()
          .AddHighResolutionSharedTimer()
          .AddDeadlineWatchdog()
          .AddScopedManagedPagedMemoryMappedFile(options =>
          {
              options.DatabaseName = $"SpawnProfile_{Environment.ProcessId}";
              options.DatabaseCacheSize = (ulong)dcs;
              options.PagesDebugPattern = false;
          })
          .AddScopedDatabaseEngine();

        var sp = sc.BuildServiceProvider();
        sp.EnsureFileDeleted<ManagedPagedMMFOptions>();
        var dbe = sp.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<WorkComp>();
        dbe.InitializeArchetypes();

        // Warmup
        for (int w = 0; w < 10; w++)
        {
            using var t = dbe.CreateQuickTransaction();
            for (int i = 0; i < 100; i++)
            {
                var comp = new WorkComp { Value = i, Timestamp = 12345 };
                t.Spawn<WorkArch>(WorkArch.Work.Set(in comp));
            }
            t.Commit();
        }

        // Per-iteration timing to measure split spike
        foreach (int n in new[] { 1, 3, 5 })
        {
            var perIterSw = new System.Diagnostics.Stopwatch();
            for (int iter = 0; iter < n; iter++)
            {
                perIterSw.Start();
                using var pt = dbe.CreateQuickTransaction();
                for (int i = 0; i < 100; i++)
                {
                    var comp = new WorkComp { Value = i, Timestamp = 12345 };
                    pt.Spawn<WorkArch>(WorkArch.Work.Set(in comp));
                }
                pt.Commit();
                perIterSw.Stop();
            }
            Console.WriteLine($"SpawnProfile: {n} iter(s): {perIterSw.Elapsed.TotalMicroseconds / n:F1}us/iter");
        }
        Console.WriteLine("SpawnProfile: Running 5000 iterations of 100-entity spawn+commit...");

        var sw = System.Diagnostics.Stopwatch.StartNew();
        for (int iter = 0; iter < 5000; iter++)
        {
            using var t = dbe.CreateQuickTransaction();
            for (int i = 0; i < 100; i++)
            {
                var comp = new WorkComp { Value = i, Timestamp = 12345 };
                t.Spawn<WorkArch>(WorkArch.Work.Set(in comp));
            }
            t.Commit();
        }
        sw.Stop();

        Console.WriteLine($"SpawnProfile: Done. {sw.ElapsedMilliseconds}ms total, {sw.ElapsedMilliseconds * 1000.0 / 5000:F1}us/iter");

        dbe.Dispose();
        sp.Dispose();
    }
}
