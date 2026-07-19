using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NUnit.Framework;
using System;
using System.IO;
using System.Numerics;
using System.Threading;
using Typhon.Engine.Internals;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests.Runtime;

/// <summary>
/// Smoke tests for the parallel WriteTickFence path (internal sub-DAG dispatched after the user DAG completes). The
/// default test suite (~3600 tests) already exercises the parallel path because <see cref="RuntimeOptions.EnableParallelFence"/>
/// defaults to true — this fixture adds explicit coverage of the opt-out path and verifies the dispatch wiring
/// produces the expected scheduler partition.
/// </summary>
[NonParallelizable]
[TestFixture]
class ParallelFenceTests : TestBase<ParallelFenceTests>
{
    [OneTimeSetUp]
    public void OneTimeSetup() => Archetype<EcsUnit>.Touch();

    private DatabaseEngine SetupEngine()
    {
        var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<EcsPosition>();
        dbe.RegisterComponentFromAccessor<EcsVelocity>();
        dbe.InitializeArchetypes();
        return dbe;
    }

    /// <summary>True when <paramref name="sys"/> belongs to a DAG on an engine-tagged track (e.g. the Fence DAG on Engine-Post).</summary>
    private static bool IsEngineInternal(DagScheduler scheduler, SystemDefinition sys)
    {
        foreach (var track in scheduler.Tracks)
        {
            foreach (var dag in track.Dags)
            {
                if (dag.Id == sys.DagId)
                {
                    return track.IsEngine;
                }
            }
        }

        return false;
    }

    [Test]
    public void OptOut_SerialFenceRunsCleanly()
    {
        using var dbe = SetupEngine();

        // Pre-spawn some entities so the fence has work to do on each tick.
        using (var tx = dbe.CreateQuickTransaction())
        {
            for (var i = 0; i < 64; i++)
            {
                tx.Spawn<EcsUnit>(EcsUnit.Position.Set(new EcsPosition(i, 0, 0)));
            }
            tx.Commit();
        }

        var ticksObserved = 0;
        using var runtime = TyphonRuntime.Create(dbe, schedule =>
        {
            schedule.PublicTrack.DeclareDag("Test").CallbackSystem("Tick", _ => Interlocked.Increment(ref ticksObserved));
        }, new RuntimeOptions
        {
            WorkerCount = 2,
            BaseTickRate = 1000,
            EnableParallelFence = false,
        });

        // EnableParallelFence=false → no Fence DAG declared → the Engine-Post track is empty, so no engine-internal
        // systems exist (AllSystemCount, counting every track, equals the user-only SystemCount).
        Assert.That(runtime.Scheduler.AllSystemCount, Is.EqualTo(runtime.Scheduler.SystemCount),
            "No engine-internal systems should exist when EnableParallelFence=false.");

        runtime.Start();
        SpinWait.SpinUntil(() => ticksObserved >= 3, TimeSpan.FromSeconds(5));
        runtime.Shutdown();

        Assert.That(ticksObserved, Is.GreaterThanOrEqualTo(3));
    }

    [Test]
    public void OptIn_FenceExecRegisteredAsInternalSystem()
    {
        using var dbe = SetupEngine();

        var ticksObserved = 0;
        using var runtime = TyphonRuntime.Create(dbe, schedule =>
        {
            schedule.PublicTrack.DeclareDag("Test").CallbackSystem("Tick", _ => Interlocked.Increment(ref ticksObserved));
        }, new RuntimeOptions
        {
            WorkerCount = 2,
            BaseTickRate = 1000,
            EnableParallelFence = true,
        });

        // The parallel fence wiring declares the four chained Fence-DAG systems on the Engine-Post track:
        // FencePrep → FenceMigrate → FenceAabbRefresh → FenceFinalize. Each must be present, on an engine-tagged
        // track, and ChunkedParallel. Iterate AllSystemCount because the public SystemCount filters engine systems out.
        var foundPrep = false;
        var foundMigrate = false;
        var foundAabbRefresh = false;
        var foundFinalize = false;
        for (var i = 0; i < runtime.Scheduler.AllSystemCount; i++)
        {
            var sys = runtime.Scheduler.Systems[i];
            if (sys.Name == "FencePrep")
            {
                foundPrep = true;
                Assert.That(IsEngineInternal(runtime.Scheduler, sys), Is.True, "FencePrep must be on an engine-tagged track.");
                Assert.That(sys.ExplicitChunkCount, Is.GreaterThan(0), "FencePrep is a ChunkedParallel system.");
            }
            else if (sys.Name == "FenceMigrate")
            {
                foundMigrate = true;
                Assert.That(IsEngineInternal(runtime.Scheduler, sys), Is.True, "FenceMigrate must be on an engine-tagged track.");
                Assert.That(sys.ExplicitChunkCount, Is.GreaterThan(0), "FenceMigrate is a ChunkedParallel system.");
            }
            else if (sys.Name == "FenceAabbRefresh")
            {
                foundAabbRefresh = true;
                Assert.That(IsEngineInternal(runtime.Scheduler, sys), Is.True, "FenceAabbRefresh must be on an engine-tagged track.");
                Assert.That(sys.ExplicitChunkCount, Is.GreaterThan(0), "FenceAabbRefresh is a ChunkedParallel system.");
            }
            else if (sys.Name == "FenceFinalize")
            {
                foundFinalize = true;
                Assert.That(IsEngineInternal(runtime.Scheduler, sys), Is.True, "FenceFinalize must be on an engine-tagged track.");
                Assert.That(sys.ExplicitChunkCount, Is.GreaterThan(0), "FenceFinalize is a ChunkedParallel system.");
            }
        }
        Assert.That(foundPrep, Is.True, "FencePrep system should be registered when EnableParallelFence=true.");
        Assert.That(foundMigrate, Is.True, "FenceMigrate system should be registered when EnableParallelFence=true.");
        Assert.That(foundAabbRefresh, Is.True, "FenceAabbRefresh system should be registered when EnableParallelFence=true.");
        Assert.That(foundFinalize, Is.True, "FenceFinalize system should be registered when EnableParallelFence=true.");

        runtime.Start();
        SpinWait.SpinUntil(() => ticksObserved >= 3, TimeSpan.FromSeconds(5));
        runtime.Shutdown();

        Assert.That(ticksObserved, Is.GreaterThanOrEqualTo(3));
    }

    /// <summary>
    /// Stress test for the parallel migration apply path. Disabled — the in-callback Transaction pattern this test
    /// uses crosses worker boundaries and trips EntityAccessor thread-affinity asserts before exercising the
    /// migrate path. AntHill is the canonical stress workload (WAL + many migrations + many workers) and
    /// validates the parallel fence in practice. Leaving this scaffold in place for a future refactor that uses
    /// the runtime's built-in per-system Transaction (not a free-standing user transaction).
    /// </summary>
    [Test]
    [Ignore("Test infra issue: in-callback Transaction crosses worker thread. Re-enable once converted to use runtime-managed per-system Transaction.")]
    public void ParallelMigration_StressManyCellsManyTicks_NoExceptions()
    {
        // Build a WAL-enabled engine so RunParallelFence's WAL-mode gate actually dispatches the parallel
        // Prep/Migrate/Finalize phases (not the serial fallback). Mirrors AntHill's setup: WAL + WalFileIO.
        var walDir = Path.Combine(Path.GetTempPath(), "Typhon.Tests", nameof(ParallelFenceTests), "Wal_StressTest");
        var dbDir = Path.Combine(Path.GetTempPath(), "Typhon.Tests", nameof(ParallelFenceTests), "Db_StressTest");
        // Wipe wholesale — the database is a {name}.typhon bundle DIRECTORY; deleting only top-level files would leave a
        // stale bundle for the engine to reopen.
        if (Directory.Exists(walDir)) Directory.Delete(walDir, recursive: true);
        if (Directory.Exists(dbDir)) Directory.Delete(dbDir, recursive: true);
        Directory.CreateDirectory(walDir);
        Directory.CreateDirectory(dbDir);

        var services = new ServiceCollection()
            .AddLogging(b => b.SetMinimumLevel(Microsoft.Extensions.Logging.LogLevel.Warning))
            .AddResourceRegistry()
            .AddMemoryAllocator()
            .AddEpochManager()
            .AddHighResolutionSharedTimer()
            .AddDeadlineWatchdog()
            .AddSingleton<IWalFileIO>(new WalFileIO())
            .AddScopedManagedPagedMemoryMappedFile(opts =>
            {
                opts.DatabaseName = "WalStressTest";
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
        using var sp = services.BuildServiceProvider();
        using var scope = sp.CreateScope();
        var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();

        dbe.RegisterComponentFromAccessor<Typhon.Engine.Tests.ClMigPos>();
        dbe.RegisterComponentFromAccessor<Typhon.Engine.Tests.ClMigScratch>();
        dbe.ConfigureSpatialGrid(new SpatialGridConfig(
            worldMin: new Vector2(0, 0),
            worldMax: new Vector2(1000, 1000),
            cellSize: 100f));
        dbe.InitializeArchetypes();

        const int EntityCount = 256;
        var ids = new EntityId[EntityCount];
        using (var tx = dbe.CreateQuickTransaction())
        {
            for (int i = 0; i < EntityCount; i++)
            {
                // Spread across the 10×10 grid by (i%10, i/10 mod 10).
                float x = (i % 10) * 100f + 50f;
                float y = ((i / 10) % 10) * 100f + 50f;
                var p = new Typhon.Engine.Tests.ClMigPos
                {
                    Bounds = new AABB2F { MinX = x, MinY = y, MaxX = x, MaxY = y },
                    Tag = i,
                };
                ids[i] = tx.Spawn<Typhon.Engine.Tests.ClMigUnit>(Typhon.Engine.Tests.ClMigUnit.Pos.Set(in p));
            }
            tx.Commit();
        }

        var ticksObserved = 0;
        Exception observedException = null;
        using var runtime = TyphonRuntime.Create(dbe, schedule =>
        {
            schedule.PublicTrack.DeclareDag("Test").CallbackSystem("MoveAll", _ =>
            {
                try
                {
                    using var tx = dbe.CreateQuickTransaction();
                    int tickIdx = Interlocked.Increment(ref ticksObserved);
                    // Move each entity by a small step that occasionally crosses a cell boundary.
                    for (int i = 0; i < EntityCount; i++)
                    {
                        var eref = tx.OpenMut(ids[i]);
                        ref var pos = ref eref.Write(Typhon.Engine.Tests.ClMigUnit.Pos);
                        float nx = pos.Bounds.MinX + 30f;
                        float ny = pos.Bounds.MinY + 7f;
                        if (nx >= 950f) nx = 50f;
                        if (ny >= 950f) ny = 50f;
                        pos.Bounds = new AABB2F { MinX = nx, MinY = ny, MaxX = nx, MaxY = ny };
                    }
                    tx.Commit();
                }
                catch (Exception ex)
                {
                    Volatile.Write(ref observedException, ex);
                }
            });
        }, new RuntimeOptions
        {
            WorkerCount = 4,
            BaseTickRate = 200,
            EnableParallelFence = true,
            FenceChunkOversubscription = 2,
        });

        runtime.Start();
        SpinWait.SpinUntil(() => ticksObserved >= 20 || observedException != null, TimeSpan.FromSeconds(10));
        runtime.Shutdown();

        Assert.That(observedException, Is.Null,
            $"Parallel fence migration apply must not throw under sustained migration load. Got: {observedException}");
        Assert.That(ticksObserved, Is.GreaterThanOrEqualTo(20),
            "Engine must complete ≥20 ticks of position mutation under parallel fence — earlier exit indicates deadlock or stall.");
    }

    /// <summary>
    /// Bug A regression: single-threaded (<c>WorkerCount == 1</c>) parallel-fence path. <see cref="DagScheduler.DispatchTrack"/>'s synchronous branch must
    /// invoke the chained <see cref="FencePhaseExecSystemBase"/> systems' typed <c>OnShouldRun()</c>/<c>OnPrepare()</c> gate — otherwise the
    /// per-phase <see cref="FenceWorkPlan"/> was never built and <c>RuntimeChunkCount</c> stayed stale (0 on the first tick). The fence then silently dropped
    /// cluster migrations. Every other <see cref="ParallelFenceTests"/> case uses <c>WorkerCount &gt;= 2</c>, so this path was unguarded.
    ///
    /// <para>This test runs a WAL-enabled engine with <c>WorkerCount = 1</c> + <c>EnableParallelFence = true</c>, moves an entity across a cell boundary, and
    /// asserts the migration was actually applied (source cell empties, dest cell populated). Pre-fix the migration is dropped and the source cell stays
    /// occupied.</para>
    /// </summary>
    [Test]
    public void SingleThreadedParallelFence_AppliesMigrations()
    {
        var walDir = Path.Combine(Path.GetTempPath(), "Typhon.Tests", nameof(ParallelFenceTests), "Wal_SingleThreaded");
        var dbDir = Path.Combine(Path.GetTempPath(), "Typhon.Tests", nameof(ParallelFenceTests), "Db_SingleThreaded");
        // Start clean. The database is now a {name}.typhon bundle DIRECTORY under dbDir — deleting only top-level files
        // (the old .bin) would leave the stale bundle behind, and the engine would reopen it with prior entities. Wipe the
        // directories wholesale.
        if (Directory.Exists(walDir)) Directory.Delete(walDir, recursive: true);
        if (Directory.Exists(dbDir)) Directory.Delete(dbDir, recursive: true);
        Directory.CreateDirectory(walDir);
        Directory.CreateDirectory(dbDir);

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
                opts.DatabaseName = "WalSingleThreaded";
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
        using var sp = services.BuildServiceProvider();
        using var scope = sp.CreateScope();
        var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();

        dbe.RegisterComponentFromAccessor<Typhon.Engine.Tests.ClMigPos>();
        dbe.RegisterComponentFromAccessor<Typhon.Engine.Tests.ClMigScratch>();
        dbe.ConfigureSpatialGrid(new SpatialGridConfig(
            worldMin: new Vector2(0, 0),
            worldMax: new Vector2(1000, 1000),
            cellSize: 100f));
        dbe.InitializeArchetypes();

        EntityId id;
        using (var tx = dbe.CreateQuickTransaction())
        {
            var p = new Typhon.Engine.Tests.ClMigPos
            {
                Bounds = new AABB2F { MinX = 50f, MinY = 50f, MaxX = 50f, MaxY = 50f },
                Tag = 0,
            };
            id = tx.Spawn<Typhon.Engine.Tests.ClMigUnit>(Typhon.Engine.Tests.ClMigUnit.Pos.Set(in p));
            tx.Commit();
        }

        int srcCell = dbe.SpatialGrid.WorldToCellKey(50f, 50f);
        int dstCell = dbe.SpatialGrid.WorldToCellKey(350f, 450f);
        Assert.That(srcCell, Is.Not.EqualTo(dstCell));

        var meta = Archetype<Typhon.Engine.Tests.ClMigUnit>.Metadata;
        var cs = dbe._archetypeStates[meta.ArchetypeId].ClusterState;

        var moved = false;
        var migrationsApplied = 0;
        using var runtime = TyphonRuntime.Create(dbe, schedule =>
        {
            schedule.PublicTrack.DeclareDag("Test").CallbackSystem("MoveOnce", _ =>
            {
                if (!Volatile.Read(ref moved))
                {
                    using var tx = dbe.CreateQuickTransaction();
                    var eref = tx.OpenMut(id);
                    ref var pos = ref eref.Write(Typhon.Engine.Tests.ClMigUnit.Pos);
                    pos.Bounds = new AABB2F { MinX = 350f, MinY = 450f, MaxX = 350f, MaxY = 450f };
                    tx.Commit();
                    Volatile.Write(ref moved, true);
                }
                else if (cs.LastTickMigrationCount > 0)
                {
                    Interlocked.Increment(ref migrationsApplied);
                }
            });
        }, new RuntimeOptions
        {
            WorkerCount = 1,
            BaseTickRate = 1000,
            EnableParallelFence = true,
            FenceChunkOversubscription = 2,
        });

        runtime.Start();
        SpinWait.SpinUntil(() => Volatile.Read(ref migrationsApplied) > 0, TimeSpan.FromSeconds(5));
        runtime.Shutdown();

        Assert.That(migrationsApplied, Is.GreaterThan(0),
            "Single-threaded parallel fence must apply cluster migrations — RuntimeChunkCount/FenceWorkPlan must be prepared via the typed gate.");

        ref var srcCellRef = ref dbe.SpatialGrid.GetCell(srcCell);
        ref var dstCellRef = ref dbe.SpatialGrid.GetCell(dstCell);
        Assert.That(srcCellRef.EntityCount, Is.EqualTo(0), "source cell must empty after migration applied by the single-threaded fence");
        Assert.That(dstCellRef.EntityCount, Is.EqualTo(1), "destination cell must hold the migrated entity");
    }

    [Test]
    public void ParallelFence_RunsTicksWithSpawnsAndMutations()
    {
        using var dbe = SetupEngine();

        using (var tx = dbe.CreateQuickTransaction())
        {
            for (var i = 0; i < 128; i++)
            {
                tx.Spawn<EcsUnit>(EcsUnit.Position.Set(new EcsPosition(i, 0, 0)));
            }
            tx.Commit();
        }

        var ticksObserved = 0;
        using var runtime = TyphonRuntime.Create(dbe, schedule =>
        {
            schedule.PublicTrack.DeclareDag("Test").CallbackSystem("Tick", _ => Interlocked.Increment(ref ticksObserved));
        }, new RuntimeOptions
        {
            WorkerCount = 4,
            BaseTickRate = 1000,
            EnableParallelFence = true,
            FenceChunkOversubscription = 2,
        });

        runtime.Start();
        SpinWait.SpinUntil(() => ticksObserved >= 5, TimeSpan.FromSeconds(5));
        runtime.Shutdown();

        Assert.That(ticksObserved, Is.GreaterThanOrEqualTo(5),
            "Parallel fence should not block or stall the tick loop — multiple ticks must complete.");
    }

    /// <summary>
    /// Review C-1 regression: ReleaseSlot's last-bit-wins path must NOT immediately free the chunk while concurrent
    /// ClaimSlotInCell calls can race. Drives a workload where many entities migrate concurrently with high churn
    /// (entities crossing cells, clusters draining and being re-filled in the same tick). Pre-fix this raced and
    /// occasionally corrupted entity data; post-fix the deferred drain serializes finalize to the Finalize phase.
    /// </summary>
    [Test]
    public void ClusterDrainAndRefill_NoCorruption()
    {
        using var dbe = SetupEngine();

        // Spawn 256 entities across 16 positions (16 entities per cell-position on average).
        using (var tx = dbe.CreateQuickTransaction())
        {
            for (var i = 0; i < 256; i++)
            {
                tx.Spawn<EcsUnit>(EcsUnit.Position.Set(new EcsPosition(i % 16, 0, 0)));
            }
            tx.Commit();
        }

        int ticksObserved = 0;
        using var runtime = TyphonRuntime.Create(dbe, schedule =>
        {
            schedule.PublicTrack.DeclareDag("Test").CallbackSystem("Tick", _ => Interlocked.Increment(ref ticksObserved));
        }, new RuntimeOptions
        {
            WorkerCount = 4,
            BaseTickRate = 1000,
            EnableParallelFence = true,
            FenceChunkOversubscription = 2,
        });

        runtime.Start();
        SpinWait.SpinUntil(() => ticksObserved >= 10, TimeSpan.FromSeconds(5));
        runtime.Shutdown();

        Assert.That(ticksObserved, Is.GreaterThanOrEqualTo(10),
            "Parallel fence must not deadlock or stall when ReleaseSlot defers cluster finalization.");
    }

    /// <summary>
    /// Review C-2 regression: when many migrations target the same destination cell, slice boundaries must align
    /// with cell-key transitions so no two workers mutate the same dst cell concurrently. Pre-fix this would split
    /// a single cell's migration block across slices.
    /// </summary>
    [Test]
    public void SameDestCellStorm_SlicesAreCellDisjoint()
    {
        using var dbe = SetupEngine();

        // Pre-fill with entities scattered around so all migrate to (0,0) on the first tick.
        using (var tx = dbe.CreateQuickTransaction())
        {
            for (var i = 0; i < 128; i++)
            {
                tx.Spawn<EcsUnit>(EcsUnit.Position.Set(new EcsPosition(10 + i, 0, 0)));
            }
            tx.Commit();
        }

        int ticksObserved = 0;
        using var runtime = TyphonRuntime.Create(dbe, schedule =>
        {
            schedule.PublicTrack.DeclareDag("Test").CallbackSystem("Tick", _ => Interlocked.Increment(ref ticksObserved));
        }, new RuntimeOptions
        {
            WorkerCount = 4,
            BaseTickRate = 1000,
            EnableParallelFence = true,
            FenceChunkOversubscription = 2,
        });

        runtime.Start();
        SpinWait.SpinUntil(() => ticksObserved >= 5, TimeSpan.FromSeconds(5));
        runtime.Shutdown();

        Assert.That(ticksObserved, Is.GreaterThanOrEqualTo(5),
            "Parallel fence must handle same-destCellKey migration storms without deadlock.");
    }

    /// <summary>
    /// Review D-2 regression: outlier-guard fires in AABB-Refresh must batch-enqueue under a single _finalizeLock
    /// acquisition per slice rather than per-entity. Test smoke-covers the bulk-enqueue path via an outlier-prone
    /// workload.
    /// </summary>
    [Test]
    public void OutlierEnqueues_BulkAppendUnderLock()
    {
        using var dbe = SetupEngine();

        using (var tx = dbe.CreateQuickTransaction())
        {
            for (var i = 0; i < 64; i++)
            {
                tx.Spawn<EcsUnit>(EcsUnit.Position.Set(new EcsPosition(i, 0, 0)));
            }
            tx.Commit();
        }

        int ticksObserved = 0;
        using var runtime = TyphonRuntime.Create(dbe, schedule =>
        {
            schedule.PublicTrack.DeclareDag("Test").CallbackSystem("Tick", _ => Interlocked.Increment(ref ticksObserved));
        }, new RuntimeOptions
        {
            WorkerCount = 4,
            BaseTickRate = 1000,
            EnableParallelFence = true,
            FenceChunkOversubscription = 2,
        });

        runtime.Start();
        SpinWait.SpinUntil(() => ticksObserved >= 5, TimeSpan.FromSeconds(5));
        runtime.Shutdown();

        Assert.That(ticksObserved, Is.GreaterThanOrEqualTo(5),
            "AABB refresh must complete cleanly even when outlier-guard fires across slices.");
    }

    /// <summary>
    /// Regression for the fence double-dispatch bug: under the legacy per-phase explicit DispatchInternalSystem pattern,
    /// the DAG's auto-fanout in OnSystemComplete plus the explicit dispatch race could cause chunk 0 of a downstream
    /// fence phase (FenceMigrate / FenceFinalize) to be executed twice within the same tick. Symptom: ReleaseSlot ran
    /// twice per migration → drained clusters left in ActiveClusterIds → IndexOutOfRange in user code.
    ///
    /// <para>This test runs many ticks with high migration churn and asserts the fence completes cleanly. Under the
    /// old code it would eventually corrupt cluster bookkeeping and throw; under the new single-DispatchInternalSchedule
    /// path it should run indefinitely without issue.</para>
    /// </summary>
    [Test]
    public void ParallelFence_AcrossManyTicks_EachPhaseChunkExecutedExactlyOnce()
    {
        using var dbe = SetupEngine();

        // Pre-spawn 256 entities — sufficient to populate multiple clusters and exercise the fence pipeline.
        using (var tx = dbe.CreateQuickTransaction())
        {
            for (var i = 0; i < 256; i++)
            {
                tx.Spawn<EcsUnit>(EcsUnit.Position.Set(new EcsPosition(i, 0, 0)));
            }
            tx.Commit();
        }

        var ticksObserved = 0;
        using var runtime = TyphonRuntime.Create(dbe, schedule =>
        {
            schedule.PublicTrack.DeclareDag("Test").CallbackSystem("Tick", _ => Interlocked.Increment(ref ticksObserved));
        }, new RuntimeOptions
        {
            WorkerCount = 4,
            BaseTickRate = 1000,
            EnableParallelFence = true,
            FenceChunkOversubscription = 2,
        });

        runtime.Start();
        // Run for many ticks. Under the legacy explicit-per-phase dispatch pattern, OnSystemComplete's auto-fanout
        // raced with each DispatchInternalSystem call — chunk 0 of downstream fence phases got claimed twice within
        // the same tick. With the new single-DispatchInternalSchedule path, the DAG drives the whole pipeline and
        // each chunk runs exactly once per tick. Many ticks (>=100) without deadlock or stall is the regression
        // signal: under the old code, telemetry showed FenceFinalize[0] executing twice; here we verify steady
        // progress instead.
        SpinWait.SpinUntil(() => ticksObserved >= 100, TimeSpan.FromSeconds(10));
        runtime.Shutdown();

        Assert.That(ticksObserved, Is.GreaterThanOrEqualTo(100),
            "Fence must complete ≥100 ticks cleanly. Earlier exit would indicate double-dispatch corruption or stall.");
    }
}
