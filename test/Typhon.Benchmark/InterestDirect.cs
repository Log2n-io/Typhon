using System;
using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Typhon.Engine;

namespace Typhon.Benchmark;

public static class InterestDirect
{
    public static void Run(string[] args)
    {
        int entityCount = args.Length > 1 ? int.Parse(args[1]) : 10_000;
        int dirtyPerTick = args.Length > 2 ? int.Parse(args[2]) : 100;
        int observerCount = args.Length > 3 ? int.Parse(args[3]) : 10;
        int ticks = args.Length > 4 ? int.Parse(args[4]) : 5;

        Console.WriteLine($"=== Interest Management Direct Benchmark ===");
        Console.WriteLine($"Entities: {entityCount}, Dirty/tick: {dirtyPerTick}, Observers: {observerCount}, Ticks: {ticks}");
        Console.WriteLine();

        var sc = new ServiceCollection();
        sc.AddLogging(b => b.SetMinimumLevel(LogLevel.Critical))
            .AddResourceRegistry().AddMemoryAllocator().AddEpochManager()
            .AddHighResolutionSharedTimer().AddDeadlineWatchdog()
            .AddScopedManagedPagedMemoryMappedFile(o =>
            {
                o.DatabaseName = $"IntDirect{entityCount}";
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

        // Spawn entities
        var sw = Stopwatch.StartNew();
        var rng = new Random(42);
        var entityIds = TriggerBenchHelper.SpawnShips(dbe, entityCount, _ =>
            ((float)(rng.NextDouble() * 10000), (float)(rng.NextDouble() * 10000)));
        Console.WriteLine($"Spawn: {sw.ElapsedMilliseconds}ms");

        var table = dbe.GetComponentTable<TrigShip>();
        var ims = table.SpatialIndex.GetOrCreateInterestSystem(table);

        // Register observers covering overlapping regions
        var observers = new SpatialObserverHandle[observerCount];
        for (int i = 0; i < observerCount; i++)
        {
            double cx = (i % 10) * 1000 + 500;
            double cy = (i / 10) * 1000 + 500;
            observers[i] = ims.RegisterObserver(stackalloc double[] { cx - 1000, cy - 1000, cx + 1000, cy + 1000 }, initialTick: 0);
        }

        // Dirty entities + archive tick fences
        sw.Restart();
        for (int tick = 1; tick <= ticks; tick++)
        {
            // Make dirtyPerTick entities dirty
            using (var t = dbe.CreateQuickTransaction())
            {
                for (int d = 0; d < dirtyPerTick; d++)
                {
                    int idx = rng.Next(entityCount);
                    if (!entityIds[idx].IsNull)
                    {
                        var eref = t.OpenMut(entityIds[idx]);
                        var ship = eref.Read(TrigShipArch.Ship);
                        ship.Bounds.MinX += 0.01f;
                        eref.Write(TrigShipArch.Ship) = ship;
                    }
                }
                t.Commit();
            }
            dbe.WriteTickFence(tick);
        }
        Console.WriteLine($"Dirty+Archive ({ticks} ticks × {dirtyPerTick} dirty): {sw.ElapsedMilliseconds}ms");

        // Warmup GetSpatialChanges
        for (int i = 0; i < observerCount; i++)
        {
            ims.GetSpatialChanges(observers[i], ticks);
        }

        // Now create fresh dirty data and measure
        for (int tick = ticks + 1; tick <= ticks + 5; tick++)
        {
            using (var t = dbe.CreateQuickTransaction())
            {
                for (int d = 0; d < dirtyPerTick; d++)
                {
                    int idx = rng.Next(entityCount);
                    if (!entityIds[idx].IsNull)
                    {
                        var eref = t.OpenMut(entityIds[idx]);
                        var ship = eref.Read(TrigShipArch.Ship);
                        ship.Bounds.MinX += 0.01f;
                        eref.Write(TrigShipArch.Ship) = ship;
                    }
                }
                t.Commit();
            }
            dbe.WriteTickFence(tick);
        }

        // Measure GetSpatialChanges
        int measureTick = ticks + 5;
        sw.Restart();
        const int measureIters = 100;
        int totalChanged = 0;
        int fullSyncs = 0;
        for (int iter = 0; iter < measureIters; iter++)
        {
            for (int i = 0; i < observerCount; i++)
            {
                // Reset observer tick so each iteration does a fresh accumulation
                var r = ims.GetSpatialChanges(observers[i], measureTick + iter);
                totalChanged += r.ChangedEntities.Length;
                if (r.IsFullSync)
                {
                    fullSyncs++;
                }
            }
        }
        var elapsed = sw.Elapsed;
        double nsPerCall = elapsed.TotalNanoseconds / (measureIters * observerCount);
        Console.WriteLine();
        Console.WriteLine($"GetSpatialChanges ({measureIters} iters × {observerCount} observers = {measureIters * observerCount} calls):");
        Console.WriteLine($"  Total: {elapsed.TotalMilliseconds:F2}ms");
        Console.WriteLine($"  Per call: {nsPerCall:F0}ns ({nsPerCall / 1000:F2}us)");
        Console.WriteLine($"  Total changed entities reported: {totalChanged}");
        Console.WriteLine($"  Full syncs: {fullSyncs}");

        // Measure full-sync cost separately (small region → ~50 results, matching the target scenario)
        sw.Restart();
        int fullSyncResults = 0;
        int fullSyncCalls = Math.Max(observerCount, 20);
        for (int i = 0; i < fullSyncCalls; i++)
        {
            double cx = rng.NextDouble() * 9000 + 500;
            double cy = rng.NextDouble() * 9000 + 500;
            // ~700×700 region in a 10000×10000 world ≈ ~0.5% area ≈ ~50 entities
            var freshObs = ims.RegisterObserver(stackalloc double[] { cx - 350, cy - 350, cx + 350, cy + 350 }, initialTick: 0);
            var r = ims.GetSpatialChanges(freshObs, measureTick + 200);
            fullSyncResults += r.ChangedEntities.Length;
            ims.UnregisterObserver(freshObs);
        }
        var fullSyncElapsed = sw.Elapsed;
        Console.WriteLine();
        Console.WriteLine($"Full-sync path ({fullSyncCalls} calls, avg {fullSyncResults / fullSyncCalls} results/call):");
        Console.WriteLine($"  Per call: {fullSyncElapsed.TotalNanoseconds / fullSyncCalls:F0}ns ({fullSyncElapsed.TotalMilliseconds / fullSyncCalls:F2}ms)");

        dbe.Dispose();
        sp.Dispose();
        Console.WriteLine();
        Console.WriteLine("Done.");
    }
}
