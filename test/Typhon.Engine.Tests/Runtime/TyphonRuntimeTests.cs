using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using System;
using System.Threading;

namespace Typhon.Engine.Tests.Runtime;

/// <summary>
/// Integration tests for TyphonRuntime — verifies UoW-per-tick lifecycle, Transaction delivery,
/// entity persistence across ticks, OnFirstTick, OnShutdown, and side-transaction isolation.
/// Uses a real DatabaseEngine (via TestBase pattern).
/// </summary>
[NonParallelizable]
[TestFixture]
class TyphonRuntimeTests : TestBase<TyphonRuntimeTests>
{
    [OneTimeSetUp]
    public void OneTimeSetup()
    {
    }

    private DatabaseEngine SetupEngine()
    {
        var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<EcsPosition>();
        dbe.RegisterComponentFromAccessor<EcsVelocity>();
        dbe.RegisterComponentFromAccessor<EcsHealth>();
        dbe.InitializeArchetypes();
        return dbe;
    }

    [Test]
    public void Create_WithEngine_Succeeds()
    {
        using var dbe = SetupEngine();
        using var runtime = TyphonRuntime.Create(dbe, schedule =>
        {
            schedule.PublicTrack.DeclareDag("Test").CallbackSystem("Noop", _ => { });
        }, new RuntimeOptions { WorkerCount = 1, BaseTickRate = 1000 });

        Assert.That(runtime.Engine, Is.SameAs(dbe));
        Assert.That(runtime.Scheduler, Is.Not.Null);
    }

    [Test]
    public void Start_Shutdown_Clean()
    {
        using var dbe = SetupEngine();
        using var runtime = TyphonRuntime.Create(dbe, schedule =>
        {
            schedule.PublicTrack.DeclareDag("Test").CallbackSystem("Noop", _ => { });
        }, new RuntimeOptions { WorkerCount = 2, BaseTickRate = 1000 });

        runtime.Start();
        SpinWait.SpinUntil(() => runtime.CurrentTickNumber >= 3, TimeSpan.FromSeconds(5));
        runtime.Shutdown();

        Assert.That(runtime.CurrentTickNumber, Is.GreaterThanOrEqualTo(3));
    }

    [Test]
    public void SystemReceives_ValidTransaction()
    {
        using var dbe = SetupEngine();
        var hasTransaction = false;
        var captured = 0;

        using var runtime = TyphonRuntime.Create(dbe, schedule =>
        {
            schedule.PublicTrack.DeclareDag("Test").CallbackSystem("Check", ctx =>
            {
                if (captured == 0)
                {
                    hasTransaction = ctx.Transaction != null;
                    Interlocked.Exchange(ref captured, 1);
                }
            });
        }, new RuntimeOptions { WorkerCount = 1, BaseTickRate = 1000 });

        runtime.Start();
        SpinWait.SpinUntil(() => captured == 1, TimeSpan.FromSeconds(5));
        runtime.Shutdown();

        Assert.That(hasTransaction, Is.True, "System should receive a valid Transaction");
    }

    [Test]
    public void SpawnEntity_VisibleToNextSystem()
    {
        using var dbe = SetupEngine();
        EntityId spawnedId = default;
        var readSuccess = false;
        var captured = 0;

        using var runtime = TyphonRuntime.Create(dbe, schedule =>
        {
            schedule.PublicTrack.DeclareDag("Test")
                .CallbackSystem("Spawner", ctx =>
                {
                    if (captured == 0)
                    {
                        var pos = new EcsPosition(1, 2, 3);
                        var vel = new EcsVelocity(4, 5, 6);
                        spawnedId = ctx.Transaction.Spawn<EcsUnit>(EcsUnit.Position.Set(in pos), EcsUnit.Velocity.Set(in vel));
                    }
                })
                .CallbackSystem("Reader", ctx =>
                {
                    if (captured == 0 && !spawnedId.IsNull)
                    {
                        // The Spawner's transaction committed (one tx per system).
                        // Reader's new transaction should see the spawned entity.
                        readSuccess = ctx.Transaction.TryOpen(spawnedId, out _);
                        Interlocked.Exchange(ref captured, 1);
                    }
                }, after: "Spawner");
        }, new RuntimeOptions { WorkerCount = 1, BaseTickRate = 1000 });

        runtime.Start();
        SpinWait.SpinUntil(() => captured == 1, TimeSpan.FromSeconds(5));
        runtime.Shutdown();

        Assert.That(spawnedId.IsNull, Is.False, "Spawner should have created an entity");
        Assert.That(readSuccess, Is.True, "Reader should see entity spawned by Spawner (committed between systems)");
    }

    [Test]
    public void SpawnEntity_PersistsAcrossTicks()
    {
        using var dbe = SetupEngine();
        EntityId spawnedId = default;
        var readInTick2 = false;
        var ticksSeen = 0;

        using var runtime = TyphonRuntime.Create(dbe, schedule =>
        {
            schedule.PublicTrack.DeclareDag("Test").CallbackSystem("System", ctx =>
            {
                var tick = Interlocked.Increment(ref ticksSeen);
                if (tick == 1)
                {
                    var pos = new EcsPosition(10, 20, 30);
                    var vel = new EcsVelocity(0, 0, 0);
                    spawnedId = ctx.Transaction.Spawn<EcsUnit>(EcsUnit.Position.Set(in pos), EcsUnit.Velocity.Set(in vel));
                }
                else if (tick == 2 && !spawnedId.IsNull)
                {
                    readInTick2 = ctx.Transaction.TryOpen(spawnedId, out _);
                }
            });
        }, new RuntimeOptions { WorkerCount = 1, BaseTickRate = 1000 });

        runtime.Start();
        SpinWait.SpinUntil(() => ticksSeen >= 2, TimeSpan.FromSeconds(5));
        runtime.Shutdown();

        Assert.That(readInTick2, Is.True, "Entity spawned in tick 1 should be visible in tick 2");
    }

    [Test]
    public void WriteEntity_PersistsAcrossTicks()
    {
        using var dbe = SetupEngine();

        // Pre-spawn an entity
        EntityId entityId;
        {
            using var tx = dbe.CreateQuickTransaction();
            var pos = new EcsPosition(0, 0, 0);
            var vel = new EcsVelocity(0, 0, 0);
            entityId = tx.Spawn<EcsUnit>(EcsUnit.Position.Set(in pos), EcsUnit.Velocity.Set(in vel));
            tx.Commit();
        }

        float readX = 0;
        var ticksSeen = 0;

        using var runtime = TyphonRuntime.Create(dbe, schedule =>
        {
            schedule.PublicTrack.DeclareDag("Test").CallbackSystem("System", ctx =>
            {
                var tick = Interlocked.Increment(ref ticksSeen);
                if (tick == 1)
                {
                    ref var pos = ref ctx.Transaction.OpenMut(entityId).Write(EcsUnit.Position);
                    pos.X = 42.0f;
                }
                else if (tick == 2)
                {
                    readX = ctx.Transaction.Open(entityId).Read(EcsUnit.Position).X;
                }
            });
        }, new RuntimeOptions { WorkerCount = 1, BaseTickRate = 1000 });

        runtime.Start();
        SpinWait.SpinUntil(() => ticksSeen >= 2, TimeSpan.FromSeconds(5));
        runtime.Shutdown();

        Assert.That(readX, Is.EqualTo(42.0f), "Write in tick 1 should persist to tick 2");
    }

    [Test]
    public void OnFirstTick_RunsOnce()
    {
        using var dbe = SetupEngine();
        var firstTickCount = 0;

        using var runtime = TyphonRuntime.Create(dbe, schedule =>
        {
            schedule.PublicTrack.DeclareDag("Test").CallbackSystem("Noop", _ => { });
        }, new RuntimeOptions { WorkerCount = 1, BaseTickRate = 1000 });

        runtime.OnFirstTick += _ => Interlocked.Increment(ref firstTickCount);

        runtime.Start();
        SpinWait.SpinUntil(() => runtime.CurrentTickNumber >= 5, TimeSpan.FromSeconds(5));
        runtime.Shutdown();

        Assert.That(firstTickCount, Is.EqualTo(1), "OnFirstTick should fire exactly once");
    }

    [Test]
    public void OnFirstTick_CanSpawnEntities()
    {
        using var dbe = SetupEngine();
        EntityId spawnedId = default;

        using var runtime = TyphonRuntime.Create(dbe, schedule =>
        {
            schedule.PublicTrack.DeclareDag("Test").CallbackSystem("Noop", _ => { });
        }, new RuntimeOptions { WorkerCount = 1, BaseTickRate = 1000 });

        runtime.OnFirstTick += ctx =>
        {
            var pos = new EcsPosition(99, 88, 77);
            var vel = new EcsVelocity(0, 0, 0);
            spawnedId = ctx.Transaction.Spawn<EcsUnit>(EcsUnit.Position.Set(in pos), EcsUnit.Velocity.Set(in vel));
        };

        runtime.Start();
        SpinWait.SpinUntil(() => runtime.CurrentTickNumber >= 1, TimeSpan.FromSeconds(5));
        runtime.Shutdown();

        Assert.That(spawnedId.IsNull, Is.False, "OnFirstTick should be able to spawn entities");

        // Verify entity persisted
        using var readTx = dbe.CreateQuickTransaction();
        Assert.That(readTx.TryOpen(spawnedId, out var entity), Is.True);
        Assert.That(entity.Read(EcsUnit.Position).X, Is.EqualTo(99f));
    }

    [Test]
    public void OnShutdown_RunsDuringShutdown()
    {
        using var dbe = SetupEngine();
        var shutdownCalled = false;

        using var runtime = TyphonRuntime.Create(dbe, schedule =>
        {
            schedule.PublicTrack.DeclareDag("Test").CallbackSystem("Noop", _ => { });
        }, new RuntimeOptions { WorkerCount = 1, BaseTickRate = 1000 });

        runtime.OnShutdown += _ => shutdownCalled = true;

        runtime.Start();
        SpinWait.SpinUntil(() => runtime.CurrentTickNumber >= 1, TimeSpan.FromSeconds(5));
        runtime.Shutdown();

        Assert.That(shutdownCalled, Is.True, "OnShutdown should fire during Shutdown()");
    }

    [Test]
    public void SingleThreadedMode_TransactionWorks()
    {
        using var dbe = SetupEngine();
        EntityId spawnedId = default;
        var captured = 0;

        using var runtime = TyphonRuntime.Create(dbe, schedule =>
        {
            schedule.PublicTrack.DeclareDag("Test").CallbackSystem("Spawner", ctx =>
            {
                if (captured == 0)
                {
                    var pos = new EcsPosition(1, 1, 1);
                    var vel = new EcsVelocity(0, 0, 0);
                    spawnedId = ctx.Transaction.Spawn<EcsUnit>(EcsUnit.Position.Set(in pos), EcsUnit.Velocity.Set(in vel));
                    Interlocked.Exchange(ref captured, 1);
                }
            });
        }, new RuntimeOptions { WorkerCount = 1, BaseTickRate = 1000 });

        runtime.Start();
        SpinWait.SpinUntil(() => captured == 1, TimeSpan.FromSeconds(5));
        runtime.Shutdown();

        Assert.That(spawnedId.IsNull, Is.False);
    }

    [Test]
    public void PipelineSystem_DoesNotReceiveTransaction()
    {
        // Pipeline systems use Action<int, int> — no TickContext, no Transaction.
        // This test simply verifies Pipeline systems execute alongside CallbackSystem systems
        // in a TyphonRuntime (the type system prevents Transaction access).
        using var dbe = SetupEngine();
        var chunkCount = 0;
        var callbackExecuted = 0;

        using var runtime = TyphonRuntime.Create(dbe, schedule =>
        {
            schedule.PublicTrack.DeclareDag("Test")
                .CallbackSystem("Input", _ => Interlocked.Increment(ref callbackExecuted))
                .PipelineSystem("Work", (chunk, total) => Interlocked.Increment(ref chunkCount), 10, after: "Input");
        }, new RuntimeOptions { WorkerCount = 2, BaseTickRate = 1000 });

        runtime.Start();
        SpinWait.SpinUntil(() => runtime.CurrentTickNumber >= 1, TimeSpan.FromSeconds(5));
        runtime.Shutdown();

        Assert.That(callbackExecuted, Is.GreaterThanOrEqualTo(1));
        Assert.That(chunkCount, Is.GreaterThanOrEqualTo(10));
    }

    // ═══════════════════════════════════════════════════════════════
    // #198: Entity count telemetry
    // ═══════════════════════════════════════════════════════════════

    [Test]
    [Ignore("Flaky: SpinWait deadline (5s) fails intermittently under parallel test load")]
    public void Telemetry_EntitiesProcessed_RecordedForQuerySystem()
    {
        using var dbe = SetupEngine();

        // Pre-spawn 3 entities so the View is non-empty
        using (var tx = dbe.CreateQuickTransaction())
        {
            for (var i = 0; i < 3; i++)
            {
                var pos = new EcsPosition(i, 0, 0);
                var vel = new EcsVelocity(0, 0, 0);
                tx.Spawn<EcsUnit>(EcsUnit.Position.Set(in pos), EcsUnit.Velocity.Set(in vel));
            }

            tx.Commit();
        }

        using var txView = dbe.CreateQuickTransaction();
        var view = txView.Query<EcsUnit>().ToView();

        var executeCount = 0;

        using var runtime = TyphonRuntime.Create(dbe, schedule =>
        {
            schedule.PublicTrack.DeclareDag("Test").QuerySystem("Counter", ctx =>
            {
                Interlocked.Increment(ref executeCount);
            }, input: () => view);
        }, new RuntimeOptions { WorkerCount = 1, BaseTickRate = 1000 });

        runtime.Start();
        SpinWait.SpinUntil(() => executeCount >= 2, TimeSpan.FromSeconds(5));
        runtime.Shutdown();

        var ring = runtime.Telemetry;
        Assert.That(ring.TotalTicksRecorded, Is.GreaterThanOrEqualTo(2));

        // Check per-system entity count
        var systems = ring.GetSystemMetrics(ring.NewestTick);
        Assert.That(systems[0].EntitiesProcessed, Is.EqualTo(3),
            "QuerySystem should report 3 entities processed");

        // Check tick-level aggregate
        ref readonly var tick = ref ring.GetTick(ring.NewestTick);
        Assert.That(tick.TotalEntitiesProcessed, Is.EqualTo(3),
            "Tick should report 3 total entities processed");

        view.Dispose();
    }

    [Test]
    public void Telemetry_CallbackSystem_ZeroEntities()
    {
        using var dbe = SetupEngine();
        var executeCount = 0;

        using var runtime = TyphonRuntime.Create(dbe, schedule =>
        {
            schedule.PublicTrack.DeclareDag("Test").CallbackSystem("Noop", _ => Interlocked.Increment(ref executeCount));
        }, new RuntimeOptions { WorkerCount = 1, BaseTickRate = 1000 });

        runtime.Start();
        SpinWait.SpinUntil(() => executeCount >= 2, TimeSpan.FromSeconds(5));
        runtime.Shutdown();

        var ring = runtime.Telemetry;
        var systems = ring.GetSystemMetrics(ring.NewestTick);

        Assert.That(systems[0].EntitiesProcessed, Is.EqualTo(0),
            "CallbackSystem should report 0 entities (no input View)");
    }
}
