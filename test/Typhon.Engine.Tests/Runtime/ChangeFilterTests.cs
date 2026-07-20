using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using System;
using System.Threading;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests.Runtime;

/// <summary>
/// Tests for change-filtered system inputs (#197).
/// Verifies that systems with changeFilter only process entities whose filtered components were written,
/// that empty dirty sets cause system skip, and that build-time validations work correctly.
/// </summary>
[NonParallelizable]
[TestFixture]
class ChangeFilterTests : TestBase<ChangeFilterTests>
{
    [OneTimeSetUp]
    public void OneTimeSetup()
    {
    }

    private DatabaseEngine SetupEngine()
    {
        var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        // Register as SingleVersion so DirtyBitmap is available for change filter tracking
        dbe.RegisterComponentFromAccessor<SvEcsPosition>();
        dbe.RegisterComponentFromAccessor<SvEcsVelocity>();
        dbe.RegisterComponentFromAccessor<SvEcsHealth>();
        dbe.InitializeArchetypes();
        return dbe;
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Build-time validation
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public void Build_ChangeFilterWithoutInput_Throws()
    {
        using var dbe = SetupEngine();
        Assert.Throws<InvalidOperationException>(() =>
        {
            TyphonRuntime.Create(dbe, schedule =>
            {
                schedule.PublicTrack.DeclareDag("Test").QuerySystem("Bad", _ => { }, changeFilter: [typeof(SvEcsPosition)]);
            }, new RuntimeOptions { WorkerCount = 1, BaseTickRate = 1000 });
        });
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Runtime resolution
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public void Create_ChangeFilterWithValidInput_Succeeds()
    {
        using var dbe = SetupEngine();
        using var tx = dbe.CreateQuickTransaction();
        var view = tx.Query<SvEcsUnit>().ToView();

        using var runtime = TyphonRuntime.Create(dbe, schedule =>
        {
            schedule.PublicTrack.DeclareDag("Test").QuerySystem("Filtered", _ => { },
                input: () => view,
                changeFilter: [typeof(SvEcsPosition)]);
        }, new RuntimeOptions { WorkerCount = 1, BaseTickRate = 1000 });

        Assert.That(runtime.Scheduler.SystemCount, Is.EqualTo(1));
        view.Dispose();
    }

    [Test]
    public void Create_ChangeFilterWithUnregisteredType_Throws()
    {
        using var dbe = SetupEngine();
        using var tx = dbe.CreateQuickTransaction();
        var view = tx.Query<SvEcsUnit>().ToView();

        var ex = Assert.Throws<InvalidOperationException>(() =>
        {
            TyphonRuntime.Create(dbe, schedule =>
            {
                schedule.PublicTrack.DeclareDag("Test").QuerySystem("Bad", _ => { },
                    input: () => view,
                    changeFilter: [typeof(int)]); // int is not a registered component
            }, new RuntimeOptions { WorkerCount = 1, BaseTickRate = 1000 });
        });
        Assert.That(ex.Message, Does.Contain("not a registered component type"));

        view.Dispose();
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Reactive skip
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public void ChangeFilter_NoWrites_SystemSkipped()
    {
        using var dbe = SetupEngine();

        // Pre-spawn entities so the View is non-empty
        using (var tx = dbe.CreateQuickTransaction())
        {
            var pos = new SvEcsPosition(1, 2, 3);
            var vel = new SvEcsVelocity(0, 0, 0);
            tx.Spawn<SvEcsUnit>(SvEcsUnit.Position.Set(in pos), SvEcsUnit.Velocity.Set(in vel));
            tx.Commit();
        }

        using var txView = dbe.CreateQuickTransaction();
        var view = txView.Query<SvEcsUnit>().ToView();

        var executeCount = 0;
        var ticksSeen = 0;

        using var runtime = TyphonRuntime.Create(dbe, schedule =>
        {
            var dag = schedule.PublicTrack.DeclareDag("Test");
            // Noop callback runs first to burn ticks (ensures tick fence processes dirty bitmaps)
            dag.CallbackSystem("Tick", _ => Interlocked.Increment(ref ticksSeen));

            dag.QuerySystem("Filtered", ctx =>
            {
                Interlocked.Increment(ref executeCount);
            }, input: () => view, changeFilter: [typeof(SvEcsPosition)], after: "Tick");
        }, new RuntimeOptions { WorkerCount = 1, BaseTickRate = 1000 });

        runtime.Start();
        // Wait for several ticks. First tick is conservative (no prior snapshot), so we skip it.
        // After tick 2+, no writes → system should be skipped.
        SpinWait.SpinUntil(() => ticksSeen >= 5, TimeSpan.FromSeconds(5));
        runtime.Shutdown();

        // First tick runs (conservative: no snapshot yet). After that, no writes → skipped.
        Assert.That(executeCount, Is.EqualTo(1),
            "ChangeFilter system should execute once (first tick, conservative) then be skipped (no dirty entities)");

        view.Dispose();
    }

    [Test]
    public void ChangeFilter_WithWrites_SystemExecutes()
    {
        using var dbe = SetupEngine();

        // Pre-spawn entity
        EntityId entityId;
        using (var tx = dbe.CreateQuickTransaction())
        {
            var pos = new SvEcsPosition(1, 2, 3);
            var vel = new SvEcsVelocity(0, 0, 0);
            entityId = tx.Spawn<SvEcsUnit>(SvEcsUnit.Position.Set(in pos), SvEcsUnit.Velocity.Set(in vel));
            tx.Commit();
        }

        using var txView = dbe.CreateQuickTransaction();
        var view = txView.Query<SvEcsUnit>().ToView();

        var executeCount = 0;
        var ticksSeen = 0;

        using var runtime = TyphonRuntime.Create(dbe, schedule =>
        {
            var dag = schedule.PublicTrack.DeclareDag("Test");
            // Writer system modifies Position every tick
            dag.CallbackSystem("Writer", ctx =>
            {
                Interlocked.Increment(ref ticksSeen);
                ref var pos = ref ctx.Transaction.OpenMut(entityId).Write(SvEcsUnit.Position);
                pos.X += 1.0f;
            });

            dag.QuerySystem("Filtered", ctx =>
            {
                Interlocked.Increment(ref executeCount);
            }, input: () => view, changeFilter: [typeof(SvEcsPosition)], after: "Writer");
        }, new RuntimeOptions { WorkerCount = 1, BaseTickRate = 1000 });

        runtime.Start();
        SpinWait.SpinUntil(() => ticksSeen >= 5, TimeSpan.FromSeconds(5));
        runtime.Shutdown();

        // Every tick writes Position, so the filtered system should execute every tick
        // (first tick: conservative, subsequent: dirty bitmap has entities)
        Assert.That(executeCount, Is.GreaterThanOrEqualTo(4),
            "ChangeFilter system should execute on most ticks when Position is written every tick");

        view.Dispose();
    }

    // ═══════════════════════════════════════════════════════════════════════
    // ctx.Entities filtering
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public void DirtyBitmap_SVComponent_TracksWrites()
    {
        // Diagnostic: verify DirtyBitmap works for SV components
        using var dbe = SetupEngine();

        var posTable = dbe.GetComponentTable(typeof(SvEcsPosition));
        Assert.That(posTable, Is.Not.Null, "SvEcsPosition ComponentTable should exist");
        Assert.That(posTable.StorageMode, Is.EqualTo(StorageMode.SingleVersion), "Should be SV");
        Assert.That(posTable.DirtyBitmap, Is.Not.Null, "SV table should have DirtyBitmap");
        Assert.That(posTable.Definition.EntityPKOverheadSize, Is.EqualTo(8), "SV should have EntityPK overhead");

        // Spawn entity
        EntityId e1;
        using (var tx = dbe.CreateQuickTransaction())
        {
            var pos = new SvEcsPosition(1, 2, 3);
            var vel = new SvEcsVelocity(0, 0, 0);
            e1 = tx.Spawn<SvEcsUnit>(SvEcsUnit.Position.Set(in pos), SvEcsUnit.Velocity.Set(in vel));
            tx.Commit();
        }

        // Spawn sets DirtyBitmap for SV components with indexed fields (spawn path writes PK + indexes).
        // For SV components WITHOUT indexed fields, DirtyBitmap IS set by Write<T>() but the spawn path
        // skips them (no indexes to maintain). The PK at chunk offset 0 is only written for indexed SV components.
        // This means precise entity filtering via BuildDirtyEntityPKs works for indexed SV components;
        // non-indexed SV components fall back to full View (conservative).
        // DirtyBitmap tracks chunk-level dirty state regardless.

        // Write a component to trigger DirtyBitmap
        using (var tx2 = dbe.CreateQuickTransaction())
        {
            ref var pos = ref tx2.OpenMut(e1).Write(SvEcsUnit.Position);
            pos.X = 99f;
            tx2.Commit();
        }

        var clusterState = dbe._archetypeStates[Archetype<SvEcsUnit>.Metadata.ArchetypeId]?.ClusterState;
        if (clusterState != null)
        {
            Assert.That(clusterState.ClusterDirtyBitmap.HasDirty, Is.True, "ClusterDirtyBitmap should be dirty after Write<T>");
        }
        else
        {
            Assert.That(posTable.DirtyBitmap.HasDirty, Is.True, "DirtyBitmap should be dirty after Write<T>");
        }

        // WriteTickFence should snapshot the bitmap and set the dirty flag
        dbe.WriteTickFence(0);
        Assert.That(posTable.PreviousTickDirtyBitmap, Is.Not.Null, "Raw bitmap should be stored after tick fence");
        Assert.That(posTable.PreviousTickHadDirtyEntities, Is.True, "Should flag dirty entities after write");
    }

    [Test]
    public void ChangeFilter_CtxEntities_ContainsDirtyEntities()
    {
        using var dbe = SetupEngine();

        // Spawn two entities
        EntityId e1, e2;
        using (var tx = dbe.CreateQuickTransaction())
        {
            var pos1 = new SvEcsPosition(1, 0, 0);
            var pos2 = new SvEcsPosition(2, 0, 0);
            var vel = new SvEcsVelocity(0, 0, 0);
            e1 = tx.Spawn<SvEcsUnit>(SvEcsUnit.Position.Set(in pos1), SvEcsUnit.Velocity.Set(in vel));
            e2 = tx.Spawn<SvEcsUnit>(SvEcsUnit.Position.Set(in pos2), SvEcsUnit.Velocity.Set(in vel));
            tx.Commit();
        }

        using var txView = dbe.CreateQuickTransaction();
        var view = txView.Query<SvEcsUnit>().ToView();

        var maxEntitiesReceived = 0;
        var ticksSeen = 0;
        var writerDone = 0;

        using var runtime = TyphonRuntime.Create(dbe, schedule =>
        {
            var dag = schedule.PublicTrack.DeclareDag("Test");
            // Write only e1's Position on tick 2
            dag.CallbackSystem("Writer", ctx =>
            {
                var tick = Interlocked.Increment(ref ticksSeen);
                if (tick == 2)
                {
                    ref var pos = ref ctx.Transaction.OpenMut(e1).Write(SvEcsUnit.Position);
                    pos.X = 99f;
                    Interlocked.Exchange(ref writerDone, 1);
                }
            });

            // Track max entity count across ticks after the write (tick 3+)
            dag.QuerySystem("Filtered", ctx =>
            {
                if (writerDone == 1 && ticksSeen >= 3)
                {
                    var count = ctx.Entities.Count;
                    int prev;
                    do
                    {
                        prev = maxEntitiesReceived;
                        if (count <= prev) break;
                    } while (Interlocked.CompareExchange(ref maxEntitiesReceived, count, prev) != prev);
                }
            }, input: () => view, changeFilter: [typeof(SvEcsPosition)], after: "Writer");
        }, new RuntimeOptions { WorkerCount = 1, BaseTickRate = 1000 });

        runtime.Start();
        SpinWait.SpinUntil(() => ticksSeen >= 5, TimeSpan.FromSeconds(5));
        runtime.Shutdown();

        // After the write in tick 2, the changeFilter system should receive entities (one tick latency).
        // With precise PK resolution (SV + EntityPK overhead), ctx.Entities should contain only e1.
        Assert.That(maxEntitiesReceived, Is.GreaterThan(0),
            "ChangeFilter system should receive entities when Position was written in previous tick");

        view.Dispose();
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Multiple changeFilter types (OR logic)
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public void ChangeFilter_MultipleTypes_ORLogic()
    {
        using var dbe = SetupEngine();

        // Spawn a soldier (has Position + Velocity + Health)
        EntityId soldier;
        using (var tx = dbe.CreateQuickTransaction())
        {
            var pos = new SvEcsPosition(1, 0, 0);
            var vel = new SvEcsVelocity(0, 0, 0);
            var hp = new SvEcsHealth(100, 100);
            soldier = tx.Spawn<SvEcsSoldier>(
                SvEcsUnit.Position.Set(in pos),
                SvEcsUnit.Velocity.Set(in vel),
                SvEcsSoldier.Health.Set(in hp));
            tx.Commit();
        }

        using var txView = dbe.CreateQuickTransaction();
        var view = txView.Query<SvEcsSoldier>().ToView();

        var executeCount = 0;
        var ticksSeen = 0;

        using var runtime = TyphonRuntime.Create(dbe, schedule =>
        {
            var dag = schedule.PublicTrack.DeclareDag("Test");
            // Write only Health (not Position) on tick 2
            dag.CallbackSystem("Writer", ctx =>
            {
                var tick = Interlocked.Increment(ref ticksSeen);
                if (tick == 2)
                {
                    ref var hp = ref ctx.Transaction.OpenMut(soldier).Write(SvEcsSoldier.Health);
                    hp.Current = 50;
                }
            });

            // changeFilter on [Position, Health] — OR logic
            dag.QuerySystem("Filtered", ctx =>
            {
                if (ticksSeen == 3)
                {
                    Interlocked.Increment(ref executeCount);
                }
            }, input: () => view, changeFilter: [typeof(SvEcsPosition), typeof(SvEcsHealth)], after: "Writer");
        }, new RuntimeOptions { WorkerCount = 1, BaseTickRate = 1000 });

        runtime.Start();
        SpinWait.SpinUntil(() => ticksSeen >= 4, TimeSpan.FromSeconds(5));
        runtime.Shutdown();

        // Health was written on tick 2. On tick 3 (one-tick latency), the system should fire
        // because changeFilter is OR: Position OR Health.
        Assert.That(executeCount, Is.EqualTo(1),
            "System should execute when Health is written (OR logic: Position OR Health)");

        view.Dispose();
    }

    // ═══════════════════════════════════════════════════════════════════════
    // System without changeFilter processes all entities
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public void NoChangeFilter_SystemProcessesAllEntities()
    {
        using var dbe = SetupEngine();

        // Spawn two entities
        using (var tx = dbe.CreateQuickTransaction())
        {
            var pos = new SvEcsPosition(1, 0, 0);
            var vel = new SvEcsVelocity(0, 0, 0);
            tx.Spawn<SvEcsUnit>(SvEcsUnit.Position.Set(in pos), SvEcsUnit.Velocity.Set(in vel));
            tx.Spawn<SvEcsUnit>(SvEcsUnit.Position.Set(in pos), SvEcsUnit.Velocity.Set(in vel));
            tx.Commit();
        }

        using var txView = dbe.CreateQuickTransaction();
        var view = txView.Query<SvEcsUnit>().ToView();

        var entityCount = -1;
        var captured = 0;

        using var runtime = TyphonRuntime.Create(dbe, schedule =>
        {
            schedule.PublicTrack.DeclareDag("Test").QuerySystem("All", ctx =>
            {
                if (captured == 0)
                {
                    entityCount = ctx.Entities.Count;
                    Interlocked.Exchange(ref captured, 1);
                }
            }, input: () => view);
        }, new RuntimeOptions { WorkerCount = 1, BaseTickRate = 1000 });

        runtime.Start();
        SpinWait.SpinUntil(() => captured == 1, TimeSpan.FromSeconds(5));
        runtime.Shutdown();

        Assert.That(entityCount, Is.EqualTo(2),
            "System without changeFilter should receive all entities from the View");

        view.Dispose();
    }

    // ═══════════════════════════════════════════════════════════════════════
    // CallbackSystem gets empty Entities
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public void CallbackSystem_EntitiesIsEmpty()
    {
        using var dbe = SetupEngine();
        var entityCount = -1;
        var captured = 0;

        using var runtime = TyphonRuntime.Create(dbe, schedule =>
        {
            schedule.PublicTrack.DeclareDag("Test").CallbackSystem("Callback", ctx =>
            {
                if (captured == 0)
                {
                    entityCount = ctx.Entities.Count;
                    Interlocked.Exchange(ref captured, 1);
                }
            });
        }, new RuntimeOptions { WorkerCount = 1, BaseTickRate = 1000 });

        runtime.Start();
        SpinWait.SpinUntil(() => captured == 1, TimeSpan.FromSeconds(5));
        runtime.Shutdown();

        Assert.That(entityCount, Is.EqualTo(0),
            "CallbackSystem should receive empty Entities collection");
    }
}
