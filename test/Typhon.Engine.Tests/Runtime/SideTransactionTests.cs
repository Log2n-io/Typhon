using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using System;
using System.Threading;

namespace Typhon.Engine.Tests.Runtime;

/// <summary>
/// Tests for side-transaction isolation and durability.
/// Side-transactions use Immediate durability and are NOT visible to the main tick Transaction
/// (snapshot isolation — the main Transaction's TSN is fixed at creation).
/// </summary>
[NonParallelizable]
[TestFixture]
class SideTransactionTests : TestBase<SideTransactionTests>
{
    [OneTimeSetUp]
    public void OneTimeSetup()
    {
        Archetype<EcsUnit>.Touch();
        Archetype<EcsSoldier>.Touch();
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
    public void SideTransaction_CommitsIndependently()
    {
        using var dbe = SetupEngine();
        EntityId sideSpawnedId = default;
        var captured = 0;

        using var runtime = TyphonRuntime.Create(dbe, schedule =>
        {
            schedule.PublicTrack.DeclareDag("Test").CallbackSystem("System", ctx =>
            {
                if (captured == 0)
                {
                    // Create a side-transaction and spawn an entity
                    using var sideTx = ctx.CreateSideTransaction(DurabilityMode.Immediate);
                    var pos = new EcsPosition(100, 200, 300);
                    var vel = new EcsVelocity(0, 0, 0);
                    sideSpawnedId = sideTx.Spawn<EcsUnit>(EcsUnit.Position.Set(in pos), EcsUnit.Velocity.Set(in vel));
                    sideTx.Commit();
                    Interlocked.Exchange(ref captured, 1);
                }
            });
        }, new RuntimeOptions { WorkerCount = 1, BaseTickRate = 1000 });

        runtime.Start();
        SpinWait.SpinUntil(() => captured == 1, TimeSpan.FromSeconds(5));
        runtime.Shutdown();

        // Verify entity persisted (side-tx committed independently)
        using var readTx = dbe.CreateQuickTransaction();
        Assert.That(readTx.TryOpen(sideSpawnedId, out var entity), Is.True,
            "Side-transaction entity should persist independently");
        Assert.That(entity.Read(EcsUnit.Position).X, Is.EqualTo(100f));
    }

    [Test]
    public void SideTransaction_NotVisibleToMainTx()
    {
        using var dbe = SetupEngine();
        EntityId sideSpawnedId = default;
        var mainCanSee = true; // assume true, expect false
        var captured = 0;

        using var runtime = TyphonRuntime.Create(dbe, schedule =>
        {
            schedule.PublicTrack.DeclareDag("Test").CallbackSystem("System", ctx =>
            {
                if (captured == 0)
                {
                    // Create side-tx and spawn
                    using var sideTx = ctx.CreateSideTransaction(DurabilityMode.Immediate);
                    var pos = new EcsPosition(1, 1, 1);
                    var vel = new EcsVelocity(0, 0, 0);
                    sideSpawnedId = sideTx.Spawn<EcsUnit>(EcsUnit.Position.Set(in pos), EcsUnit.Velocity.Set(in vel));
                    sideTx.Commit();

                    // Main transaction should NOT see this (its TSN was fixed before side-tx committed)
                    mainCanSee = ctx.Transaction.TryOpen(sideSpawnedId, out _);
                    Interlocked.Exchange(ref captured, 1);
                }
            });
        }, new RuntimeOptions { WorkerCount = 1, BaseTickRate = 1000 });

        runtime.Start();
        SpinWait.SpinUntil(() => captured == 1, TimeSpan.FromSeconds(5));
        runtime.Shutdown();

        Assert.That(mainCanSee, Is.False,
            "Main Transaction should NOT see side-transaction entity (snapshot isolation)");
    }

    [Test]
    public void SideTransaction_CallerOwnsLifecycle()
    {
        using var dbe = SetupEngine();
        var sideCreated = false;
        var captured = 0;

        using var runtime = TyphonRuntime.Create(dbe, schedule =>
        {
            schedule.PublicTrack.DeclareDag("Test").CallbackSystem("System", ctx =>
            {
                if (captured == 0)
                {
                    using var sideTx = ctx.CreateSideTransaction(DurabilityMode.Immediate);
                    sideCreated = true;
                    sideTx.Commit();
                    // sideTx.Dispose() happens via using — this should not affect main tx
                    Interlocked.Exchange(ref captured, 1);

                    // Main tx should still be usable after side-tx disposal
                    Assert.DoesNotThrow(() => ctx.Transaction.IsAlive(EntityId.Null));
                }
            });
        }, new RuntimeOptions { WorkerCount = 1, BaseTickRate = 1000 });

        runtime.Start();
        SpinWait.SpinUntil(() => captured == 1, TimeSpan.FromSeconds(5));
        runtime.Shutdown();

        Assert.That(sideCreated, Is.True);
    }

    [Test]
    public void SideTransaction_CreateSideTransaction_Available()
    {
        using var dbe = SetupEngine();
        Func<DurabilityMode, Transaction> capturedFactory = null;
        var captured = 0;

        using var runtime = TyphonRuntime.Create(dbe, schedule =>
        {
            schedule.PublicTrack.DeclareDag("Test").CallbackSystem("System", ctx =>
            {
                if (captured == 0)
                {
                    capturedFactory = ctx.CreateSideTransaction;
                    Interlocked.Exchange(ref captured, 1);
                }
            });
        }, new RuntimeOptions { WorkerCount = 1, BaseTickRate = 1000 });

        runtime.Start();
        SpinWait.SpinUntil(() => captured == 1, TimeSpan.FromSeconds(5));
        runtime.Shutdown();

        Assert.That(capturedFactory, Is.Not.Null,
            "TickContext.CreateSideTransaction should be set");
    }
}
