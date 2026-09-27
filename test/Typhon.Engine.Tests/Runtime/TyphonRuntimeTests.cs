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
    [Category("Sensitive")] // SpinWait deadline (5s) — starved under parallel CPU load; runs in the gate's serial quiet pass
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

        // Wait for the quantity this test ASSERTS, not for a proxy that merely correlates with it. `executeCount` is
        // incremented inside the system body — i.e. part-way THROUGH a tick — while `TotalTicksRecorded` only moves
        // once a tick has finished being recorded. Waiting on the counter therefore returned while tick 2 was still in
        // flight, `Shutdown()` cut it short before its telemetry was written, and the ring held one tick: exactly the
        // `Expected: >= 2, But was: 1` this failed with on the gate, twice, while passing every time locally. A proxy
        // wait is always green on the machine it was written on.
        var ring = runtime.Telemetry;
        SpinWait.SpinUntil(() => ring.TotalTicksRecorded >= 2 && Volatile.Read(ref executeCount) >= 2, TimeSpan.FromSeconds(5));
        runtime.Shutdown();

        Assert.That(ring.TotalTicksRecorded, Is.GreaterThanOrEqualTo(2));

        // Check per-system entity count. By NAME, not by index 0: system indices are global and assigned in track order, so the engine's own tracks own the
        // low indices whenever they carry a system — the Engine-Pre ingress drain is the first that does.
        var systems = ring.GetSystemMetrics(ring.NewestTick);
        Assert.That(systems[IndexOfSystem(runtime, "Counter")].EntitiesProcessed, Is.EqualTo(3),
            "QuerySystem should report 3 entities processed");

        // Check tick-level aggregate
        ref readonly var tick = ref ring.GetTick(ring.NewestTick);
        Assert.That(tick.TotalEntitiesProcessed, Is.EqualTo(3),
            "Tick should report 3 total entities processed");

        view.Dispose();
    }

    /// <summary>
    /// #ENG-07 — <c>ReadStats</c> answers on an engine with no replication and no application metrics: the two gates that kept these numbers inside the
    /// <c>STATS</c> wire block.
    /// </summary>
    /// <remarks>
    /// This is the whole point of the API. The same figures were computed once a second by the subscriptions runtime, only when the application's catalog
    /// declared metrics, and written into a game client's frame — so a host with replication off had no way to ask "is my tick overrunning". Every assertion
    /// below therefore runs against a runtime with no sessions at all.
    /// </remarks>
    [Test]
    public void ReadStats_AnswersWithoutReplicationOrDeclaredMetrics()
    {
        using var dbe = SetupEngine();
        var executeCount = 0;

        using var runtime = TyphonRuntime.Create(dbe, schedule =>
        {
            schedule.PublicTrack.DeclareDag("Test").CallbackSystem("Noop", _ => Interlocked.Increment(ref executeCount));
        }, new RuntimeOptions { WorkerCount = 1, BaseTickRate = 1000 });

        runtime.Start();
        var ring = runtime.Telemetry;
        SpinWait.SpinUntil(() => ring.TotalTicksRecorded >= 3, TimeSpan.FromSeconds(5));
        runtime.Shutdown();

        // Bracketed, because `Shutdown` is explicitly NOT a quiescence point — it stops new ticks and does not wait for the one in flight, which then finishes
        // and posts its accounting (see its remarks; `Dispose` is what joins the tick thread). Asserting `stats.Tick == ring.NewestTick` therefore compared
        // two reads of a value that was still moving, and failed about one cold run in three at 1000 Hz with the snapshot one tick behind. The property that
        // is actually true, and the one worth pinning, is that the snapshot names a tick the ring held while it was taken.
        var newestBefore = ring.NewestTick;
        var stats = runtime.ReadStats();
        var newestAfter = ring.NewestTick;

        Assert.Multiple(() =>
        {
            Assert.That(stats.TicksInWindow, Is.GreaterThanOrEqualTo(3), "the window covers the ticks that ran");
            Assert.That(stats.Tick, Is.InRange(newestBefore, newestAfter), "the snapshot names the tick it ends at");
            Assert.That(stats.TargetTickMs, Is.EqualTo(1.0).Within(1e-9), "1000 Hz is a 1 ms target");
            Assert.That(stats.TickP50Ms, Is.GreaterThan(0), "a tick that ran took time");
            Assert.That(stats.TickP99Ms, Is.GreaterThanOrEqualTo(stats.TickP50Ms), "p99 cannot be below p50 over one window");
            Assert.That(stats.DurabilityWaitP99Ms, Is.GreaterThan(0), "#CLI-04: the flush is timed unconditionally, so the wait is a real number here");

            // The named system is what makes the figure usable: an index would be meaningless to an operator, and system indices are global so index 0 is an
            // engine track's, not this test's.
            Assert.That(Array.ConvertAll(stats.Systems, x => x.Name), Contains.Item("Noop"), "the system is named");
            Assert.That(stats.Systems.Length, Is.EqualTo(runtime.Systems.Length), "one entry per scheduled system, in schedule order");

            Assert.That(stats.Archetypes, Is.Not.Empty, "the engine has registered archetypes whatever the ring holds");

            // Zero is what tells a reader the session figures are zero because nothing is replicated, not because a replicating server is idle. A
            // subscriptions runtime is built on every Start, so its existence would have said nothing.
            Assert.That(stats.ReplicatedArchetypes, Is.Zero, "this runtime declares no replicated archetype");
            Assert.That(stats.Sessions, Is.Zero);
            Assert.That(stats.NetOutBytesTotal, Is.Zero);
            Assert.That(stats.ReplicationTrackP99Ms, Is.Zero, "no replication track ran, so its cost is zero rather than absent");
        });
    }

    /// <summary>
    /// #ENG-07 — a runtime that has never ticked reports zeros for what it has not measured, and the archetype counts it CAN answer.
    /// </summary>
    /// <remarks>
    /// The distinction matters for a host that is scraped during startup: percentiles over an empty ring must be zero rather than a division by no samples,
    /// and the entity counts come from the engine rather than the ring, so they are real before the first tick. A reader that returned nothing at all here
    /// would make "the server is starting" indistinguishable from "the server is broken".
    /// </remarks>
    [Test]
    public void ReadStats_BeforeTheFirstTick_IsZerosAndStillCountsEntities()
    {
        using var dbe = SetupEngine();
        using var runtime = TyphonRuntime.Create(dbe, schedule =>
        {
            schedule.PublicTrack.DeclareDag("Test").CallbackSystem("Noop", static _ => { });
        }, new RuntimeOptions { WorkerCount = 1, BaseTickRate = 1000 });

        var stats = runtime.ReadStats();

        Assert.Multiple(() =>
        {
            Assert.That(stats.TicksInWindow, Is.Zero);
            Assert.That(stats.Tick, Is.EqualTo(-1), "no tick has been recorded");
            Assert.That(stats.TickP50Ms, Is.Zero);
            Assert.That(stats.TickP99Ms, Is.Zero);
            Assert.That(stats.DurabilityWaitP99Ms, Is.Zero);
            Assert.That(stats.Overruns, Is.Zero);
            Assert.That(stats.TargetTickMs, Is.EqualTo(1.0).Within(1e-9), "the configured target is known before the first tick");
            Assert.That(stats.Archetypes, Is.Not.Empty, "the engine's archetypes are registered, whatever the ring holds");
        });
    }

    /// <summary>
    /// #CLI-04 — every recorded tick carries the duration of its Unit-of-Work flush, which in WAL mode is the tick's durability wait.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The field exists because the metric that needs it, <c>typhon.durability.wait.p99</c>, emitted a hard zero: the flush was timed only inside the
    /// profiler's <c>TickPhase.UowFlush</c> span, which does not exist when the profiler is off. So the assertion that matters is that the number is there
    /// with nothing enabled — no profiler output channel, no subscriptions, no telemetry flags.
    /// </para>
    /// <para>
    /// Asserted as "every recorded tick has a wait" rather than as a threshold. A threshold would be a timing assertion on CI hardware; that EVERY tick
    /// carries one is a statement about the code path — the stamp is in a <c>finally</c> around the flush, so a tick can only miss it by not reaching the
    /// flush at all.
    /// </para>
    /// </remarks>
    [Test]
    public void EveryRecordedTickCarriesItsDurabilityWait()
    {
        using var dbe = SetupEngine();
        var executeCount = 0;

        using var runtime = TyphonRuntime.Create(dbe, schedule =>
        {
            schedule.PublicTrack.DeclareDag("Test").CallbackSystem("Noop", _ => Interlocked.Increment(ref executeCount));
        }, new RuntimeOptions { WorkerCount = 1, BaseTickRate = 1000 });

        runtime.Start();
        var ring = runtime.Telemetry;
        SpinWait.SpinUntil(() => ring.TotalTicksRecorded >= 3, TimeSpan.FromSeconds(5));
        runtime.Shutdown();

        Assert.That(ring.TotalTicksRecorded, Is.GreaterThanOrEqualTo(3), "the runtime ran and recorded");

        var oldest = ring.OldestAvailableTick;
        var newest = ring.NewestTick;
        var withWait = 0;
        var ticks = 0;
        for (var t = oldest; t <= newest; t++)
        {
            ref readonly var tick = ref ring.GetTick(t);
            ticks++;
            if (tick.UowFlushMs > 0f)
            {
                withWait++;
            }
        }

        // Counted, not timed: the claim is that the stamp reaches the ring on every tick, so the count of ticks carrying one equals the count of ticks.
        Assert.That(ticks, Is.GreaterThanOrEqualTo(3), "the window covers the ticks that ran");
        Assert.That(withWait, Is.EqualTo(ticks),
            $"{ticks - withWait} of {ticks} recorded ticks carry no flush duration — the stamp is not reaching the ring, which is the state the metric "
            + "reported as a hard zero before #CLI-04");
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

        Assert.That(systems[IndexOfSystem(runtime, "Noop")].EntitiesProcessed, Is.EqualTo(0),
            "CallbackSystem should report 0 entities (no input View)");
    }

    /// <summary>The global index of a system by name. Indices are assigned in track order, so index 0 is an engine system, not the app's first.</summary>
    private static int IndexOfSystem(TyphonRuntime runtime, string name)
    {
        for (var i = 0; i < runtime.Scheduler.AllSystemCount; i++)
        {
            if (runtime.Scheduler.Systems[i].Name == name)
            {
                return i;
            }
        }

        Assert.Fail($"system '{name}' is not in the schedule");
        return -1;
    }
}
