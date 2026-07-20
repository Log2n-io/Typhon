using System;
using System.IO;
using System.Numerics;
using System.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NUnit.Framework;
using Typhon.Engine.Internals;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests.Runtime;

/// <summary>
/// End-to-end tests for the adaptive fence cost model. Uses a WAL-enabled engine so <c>RunParallelFence</c> dispatches
/// the parallel sub-DAG (which is gated on <c>WalManager != null</c>) — only then do per-chunk timings flow into
/// <see cref="LiveFenceCostModel"/> via <c>UpdatePhase</c>.
/// </summary>
[TestFixture]
class AdaptiveFenceCostTests
{
    private static string MakeDir(string suffix)
    {
        var dir = Path.Combine(Path.GetTempPath(), "Typhon.Tests", nameof(AdaptiveFenceCostTests), suffix);
        // Wipe wholesale — a database is now a {name}.typhon bundle DIRECTORY, so deleting only top-level files would
        // leave a stale bundle behind for the engine to reopen.
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static ServiceProvider BuildWalProvider(string suffix)
    {
        var walDir = MakeDir("Wal_" + suffix);
        var dbDir = MakeDir("Db_" + suffix);
        var services = new ServiceCollection()
            .AddLogging(b => b.SetMinimumLevel(LogLevel.Warning))
            .AddResourceRegistry()
            .AddMemoryAllocator()
            .AddEpochManager()
            .AddHighResolutionSharedTimer()
            .AddDeadlineWatchdog()
            .AddSingleton<IWalFileIO>(new WalFileIO())
            .AddScopedManagedPagedMemoryMappedFile(opts =>
            {
                opts.DatabaseName = "Adaptive_" + suffix;
                opts.DatabaseDirectory = dbDir;
                opts.DatabaseCacheSize = (ulong)PagedMMF.MinimumCacheSize * 4;
            })
            .AddScopedDatabaseEngine(opts =>
            {
                opts.Wal = new WalWriterOptions
                {
                    WalDirectory = walDir,
                    GroupCommitIntervalMs = 5,
                    UseFUA = false,
                    SegmentSize = 4 * 1024 * 1024,
                    PreAllocateSegments = 1,
                };
            });
        return services.BuildServiceProvider();
    }

    private static DatabaseEngine SetupSpatialEngine(IServiceProvider sp)
    {
        var dbe = sp.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<ClMigPos>();
        dbe.RegisterComponentFromAccessor<ClMigScratch>();
        dbe.ConfigureSpatialGrid(new SpatialGridConfig(
            worldMin: new Vector2(0, 0),
            worldMax: new Vector2(1000, 1000),
            cellSize: 100f));
        dbe.InitializeArchetypes();
        return dbe;
    }

    private static void SpawnGrid(DatabaseEngine dbe, int count)
    {
        using var tx = dbe.CreateQuickTransaction();
        for (int i = 0; i < count; i++)
        {
            float x = (i % 10) * 100f + 50f;
            float y = ((i / 10) % 10) * 100f + 50f;
            var p = new ClMigPos
            {
                Bounds = new AABB2F { MinX = x, MinY = y, MaxX = x, MaxY = y },
                Tag = i,
            };
            tx.Spawn<ClMigUnit>(ClMigUnit.Pos.Set(in p));
        }
        tx.Commit();
    }

    [Test]
    public void AdaptiveCost_OffByFlag_PreservesStaticDefaults()
    {
        using var sp = BuildWalProvider("Off");
        using var scope = sp.CreateScope();
        var dbe = SetupSpatialEngine(scope.ServiceProvider);
        SpawnGrid(dbe, 256);

        int ticksObserved = 0;
        using var runtime = TyphonRuntime.Create(dbe, schedule =>
        {
            schedule.PublicTrack.DeclareDag("Test").CallbackSystem("Tick", _ => Interlocked.Increment(ref ticksObserved));
        }, new RuntimeOptions
        {
            WorkerCount = 2,
            BaseTickRate = 200,
            EnableParallelFence = true,
            AdaptiveFenceCost = false,
        });

        var seed = FenceCostModel.Default;
        var live = runtime.LiveFenceCost;
        Assert.That(live, Is.Not.Null);
        Assert.That(live.MigrationCost, Is.EqualTo(seed.MigrationCost));
        Assert.That(live.AabbCost, Is.EqualTo(seed.AabbCost));

        runtime.Start();
        SpinWait.SpinUntil(() => ticksObserved >= 10, TimeSpan.FromSeconds(5));
        runtime.Shutdown();

        Assert.That(ticksObserved, Is.GreaterThanOrEqualTo(10));
        // With adaptive OFF, the live model must remain pinned to the seed even after dispatch produced measurements.
        Assert.That(live.MigrationCost, Is.EqualTo(seed.MigrationCost), "MigrationCost must stay at seed when AdaptiveFenceCost=false");
        Assert.That(live.AabbCost, Is.EqualTo(seed.AabbCost), "AabbCost must stay at seed when AdaptiveFenceCost=false");
    }

    [Test]
    public void AdaptiveCost_OnByDefault_AabbCostShiftsFromSeed()
    {
        using var sp = BuildWalProvider("OnAabb");
        using var scope = sp.CreateScope();
        var dbe = SetupSpatialEngine(scope.ServiceProvider);
        SpawnGrid(dbe, 256);

        int ticksObserved = 0;
        using var runtime = TyphonRuntime.Create(dbe, schedule =>
        {
            schedule.PublicTrack.DeclareDag("Test").CallbackSystem("Tick", _ => Interlocked.Increment(ref ticksObserved));
        }, new RuntimeOptions
        {
            WorkerCount = 2,
            BaseTickRate = 200,
            EnableParallelFence = true,
            // AdaptiveFenceCost defaults to true.
        });

        var seed = FenceCostModel.Default;
        var live = runtime.LiveFenceCost;
        Assert.That(live, Is.Not.Null);
        Assert.That(live.AabbCost, Is.EqualTo(seed.AabbCost), "before Start, live cost must equal seed");

        runtime.Start();
        // Need enough ticks for at least one AABB-refresh dispatch carrying measurable cluster count. The first
        // tick's fence sees dirty clusters from the spawn and runs AABB refresh, producing a UpdatePhase call.
        SpinWait.SpinUntil(() => ticksObserved >= 5, TimeSpan.FromSeconds(5));
        runtime.Shutdown();

        Assert.That(ticksObserved, Is.GreaterThanOrEqualTo(5));
        Assert.That(live.AabbCost, Is.Not.EqualTo(seed.AabbCost),
            $"AabbCost must shift from the seed (was {seed.AabbCost}) after measurements; observed {live.AabbCost}");
        Assert.That(live.AabbCost, Is.GreaterThan(0f));
    }

    [Test]
    public void AdaptiveCost_DefaultFlag_IsOn()
    {
        var opts = new RuntimeOptions();
        Assert.That(opts.AdaptiveFenceCost, Is.True);
    }
}
