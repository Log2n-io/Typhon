using System;
using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Typhon.Engine;
using Typhon.Schema.Definition;

namespace Typhon.Benchmark;

public static class TriggerDirect
{
    public static void Run(string[] args)
    {
        int entityCount = args.Length > 1 ? int.Parse(args[1]) : 10_000;
        int regionCount = args.Length > 2 ? int.Parse(args[2]) : 1;
        int evalIterations = args.Length > 3 ? int.Parse(args[3]) : 1000;

        Console.WriteLine($"=== Trigger Volume Direct Benchmark ===");
        Console.WriteLine($"Entities: {entityCount}, Regions: {regionCount}, Eval iterations: {evalIterations}");
        Console.WriteLine();

        Archetype<TrigShipArch>.Touch();
        var sc = new ServiceCollection();
        sc.AddLogging(b => b.SetMinimumLevel(LogLevel.Critical))
            .AddResourceRegistry().AddMemoryAllocator().AddEpochManager()
            .AddHighResolutionSharedTimer().AddDeadlineWatchdog()
            .AddScopedManagedPagedMemoryMappedFile(o =>
            {
                o.DatabaseName = $"TrigDirect{entityCount}";
                o.DatabaseCacheSize = (ulong)(128L * 1024 * PagedMMF.PageSize);
                o.TestMode = true;
                o.PagesDebugPattern = false;
            })
            .AddScopedDatabaseEngine();
        var sp = sc.BuildServiceProvider();
        sp.EnsureFileDeleted<ManagedPagedMMFOptions>();
        var dbe = sp.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<TrigShip>();
        dbe.InitializeArchetypes();

        // --- Spawn ---
        var sw = Stopwatch.StartNew();
        var rng = new Random(42);
        int targetOccupants = Math.Min(50, entityCount);
        TriggerBenchHelper.SpawnShips(dbe, entityCount, i =>
        {
            if (i < targetOccupants)
            {
                return ((float)(rng.NextDouble() * 90 + 5), (float)(rng.NextDouble() * 90 + 5));
            }
            return ((float)(200 + rng.NextDouble() * 9800), (float)(200 + rng.NextDouble() * 9800));
        });
        var spawnTime = sw.Elapsed;
        Console.WriteLine($"Spawn {entityCount} entities: {spawnTime.TotalMilliseconds:F1}ms ({spawnTime.TotalMilliseconds / entityCount * 1000:F1}us/entity)");

        var table = dbe.GetComponentTable<TrigShip>();
        var tree = table.SpatialIndex.ActiveTree;
        Console.WriteLine($"Tree: {tree.EntityCount} entities, depth {tree.Depth}, {tree.NodeCount} nodes");

        // --- Create trigger system + regions ---
        var ts = table.SpatialIndex.GetOrCreateTriggerSystem(table);
        var handles = new SpatialRegionHandle[regionCount];
        for (int i = 0; i < regionCount; i++)
        {
            double x = (i % 10) * 1000;
            double y = (i / 10) * 1000;
            handles[i] = ts.CreateRegion(stackalloc double[] { x, y, x + 200, y + 200 });
        }

        // --- Prime all regions ---
        for (int i = 0; i < regionCount; i++)
        {
            ts.EvaluateRegion(handles[i], 0);
        }

        // --- Warmup ---
        for (int iter = 0; iter < 100; iter++)
        {
            for (int i = 0; i < regionCount; i++)
            {
                ts.EvaluateRegion(handles[i], iter + 1);
            }
        }

        // --- Measure steady-state eval ---
        sw.Restart();
        int totalStay = 0;
        for (int iter = 0; iter < evalIterations; iter++)
        {
            int tick = 200 + iter;
            for (int i = 0; i < regionCount; i++)
            {
                var r = ts.EvaluateRegion(handles[i], tick);
                totalStay += r.StayCount;
            }
        }
        var evalTime = sw.Elapsed;
        double totalEvals = (double)evalIterations * regionCount;
        double nsPerEval = evalTime.TotalNanoseconds / totalEvals;
        Console.WriteLine();
        Console.WriteLine($"Steady-state evaluation ({evalIterations} iters x {regionCount} regions = {totalEvals:N0} evals):");
        Console.WriteLine($"  Total: {evalTime.TotalMilliseconds:F2}ms");
        Console.WriteLine($"  Per eval: {nsPerEval:F0}ns ({nsPerEval / 1000:F2}us)");
        Console.WriteLine($"  Per region per tick: {nsPerEval:F0}ns");
        Console.WriteLine($"  Total stays: {totalStay:N0}");

        // --- Measure with enter/leave churn ---
        if (entityCount >= 100)
        {
            sw.Restart();
            int churnIters = Math.Min(evalIterations, 100);
            int churnEnteredTotal = 0, churnLeftTotal = 0;
            for (int iter = 0; iter < churnIters; iter++)
            {
                using (var t = dbe.CreateQuickTransaction())
                {
                    for (int c = 0; c < entityCount / 100; c++)
                    {
                        var ship = new TrigShip
                        {
                            Bounds = new AABB2F
                            {
                                MinX = (float)(rng.NextDouble() * 10000),
                                MinY = (float)(rng.NextDouble() * 10000),
                                MaxX = (float)(rng.NextDouble() * 10000 + 2),
                                MaxY = (float)(rng.NextDouble() * 10000 + 2)
                            }
                        };
                        t.Spawn<TrigShipArch>(TrigShipArch.Ship.Set(in ship));
                    }
                    t.Commit();
                }
                int tick = 10000 + iter;
                for (int i = 0; i < regionCount; i++)
                {
                    var r = ts.EvaluateRegion(handles[i], tick);
                    churnEnteredTotal += r.Entered.Length;
                    churnLeftTotal += r.Left.Length;
                }
            }
            var churnTime = sw.Elapsed;
            double nsPerChurnEval = churnTime.TotalNanoseconds / (churnIters * regionCount);
            Console.WriteLine();
            Console.WriteLine($"Churn evaluation ({churnIters} iters, 1% spawn/iter):");
            Console.WriteLine($"  Per eval (incl. spawn): {nsPerChurnEval:F0}ns ({nsPerChurnEval / 1000:F2}us)");
            Console.WriteLine($"  Total entered: {churnEnteredTotal}, left: {churnLeftTotal}");
        }

        dbe.Dispose();
        sp.Dispose();
        Console.WriteLine();
        Console.WriteLine("Done.");
    }
}
