using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using System;

namespace Typhon.Engine.Tests;

class UnitOfWorkTests : TestBase<UnitOfWorkTests>
{
    [OneTimeSetUp]
    public void OneTimeSetup()
    {
        Archetype<CompAArch>.Touch();
    }

    [Test]
    public void UoW_Create_Dispose_Lifecycle()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();

        var uow = dbe.CreateUnitOfWork();
        Assert.That(uow.State, Is.EqualTo(UnitOfWorkState.Pending));
        Assert.That(uow.IsDisposed, Is.False);

        uow.Dispose();
        Assert.That(uow.State, Is.EqualTo(UnitOfWorkState.Free));
        Assert.That(uow.IsDisposed, Is.True);
    }

    [Test]
    public void UoW_CreateTransaction_ReturnsValidTx()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        dbe.InitializeArchetypes();

        using var uow = dbe.CreateUnitOfWork();
        using var tx = uow.CreateTransaction();

        Assert.That(tx, Is.Not.Null);
        Assert.That(tx.State, Is.EqualTo(Transaction.TransactionState.Created));
        Assert.That(tx.TSN, Is.GreaterThan(0));

        // Verify the transaction actually works: create, commit, read
        var comp = new CompA(42);
        var entityId = tx.Spawn<CompAArch>(CompAArch.A.Set(in comp));
        Assert.That(entityId.IsNull, Is.False);

        var committed = tx.Commit();
        Assert.That(committed, Is.True);
    }

    [Test]
    public void UoW_MultipleTransactions_ShareIdentity()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        dbe.InitializeArchetypes();

        using var uow = dbe.CreateUnitOfWork();

        using var tx1 = uow.CreateTransaction();
        using var tx2 = uow.CreateTransaction();

        // Both transactions should reference the same UoW
        Assert.That(tx1.OwningUnitOfWork, Is.SameAs(uow));
        Assert.That(tx2.OwningUnitOfWork, Is.SameAs(uow));
        Assert.That(uow.TransactionCount, Is.EqualTo(2));

        tx1.Commit();
        tx2.Commit();
    }

    [Test]
    public void UoW_DisposedUoW_ThrowsOnCreateTx()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();

        var uow = dbe.CreateUnitOfWork();
        uow.Dispose();

        Assert.Throws<ObjectDisposedException>(() =>
        {
#pragma warning disable TYPHON004 // CreateTransaction will throw before returning
            uow.CreateTransaction();
#pragma warning restore TYPHON004
        });
    }

    [Test]
    public void UoW_DurabilityMode_PreservedFromCreation()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();

        using var deferred = dbe.CreateUnitOfWork(DurabilityMode.Deferred);
        Assert.That(deferred.DurabilityMode, Is.EqualTo(DurabilityMode.Deferred));

        using var groupCommit = dbe.CreateUnitOfWork(DurabilityMode.GroupCommit);
        Assert.That(groupCommit.DurabilityMode, Is.EqualTo(DurabilityMode.GroupCommit));

        using var immediate = dbe.CreateUnitOfWork(DurabilityMode.Immediate);
        Assert.That(immediate.DurabilityMode, Is.EqualTo(DurabilityMode.Immediate));
    }

    [Test]
    public void UoW_StateTransitions()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();

        var uow = dbe.CreateUnitOfWork();
        Assert.That(uow.State, Is.EqualTo(UnitOfWorkState.Pending));

        // Flush transitions Pending → WalDurable (no-op semantics until WAL)
        uow.Flush();
        Assert.That(uow.State, Is.EqualTo(UnitOfWorkState.WalDurable));

        // Second flush is a no-op (already WalDurable)
        uow.Flush();
        Assert.That(uow.State, Is.EqualTo(UnitOfWorkState.WalDurable));

        // Dispose transitions → Free
        uow.Dispose();
        Assert.That(uow.State, Is.EqualTo(UnitOfWorkState.Free));
    }

    [Test]
    public void UoW_Flush_EmptyUoW_Completes()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();

        using var uow = dbe.CreateUnitOfWork();

        // Flushing an empty UoW (no committed transactions) is a no-op and must not throw.
        Assert.DoesNotThrow(() => uow.Flush());

        // FlushAsync should return a completed task (nothing durable to wait on).
        var task = uow.FlushAsync();
        Assert.That(task.IsCompleted, Is.True);
    }

    [Test]
    public void UoW_DoubleDispose_NoThrow()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();

        var uow = dbe.CreateUnitOfWork();
        uow.Dispose();

        // Second dispose should not throw
        Assert.DoesNotThrow(() => uow.Dispose());
        Assert.That(uow.IsDisposed, Is.True);
    }

    [Test]
    public void UoW_TransactionCount_Increments()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        dbe.InitializeArchetypes();

        using var uow = dbe.CreateUnitOfWork();
        Assert.That(uow.TransactionCount, Is.EqualTo(0));

        using var tx1 = uow.CreateTransaction();
        Assert.That(uow.TransactionCount, Is.EqualTo(1));

        using var tx2 = uow.CreateTransaction();
        Assert.That(uow.TransactionCount, Is.EqualTo(2));

        tx1.Commit();
        tx2.Commit();
    }

    [Test]
    public void UoW_DefaultDurabilityMode_IsDeferred()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();

        using var uow = dbe.CreateUnitOfWork();
        Assert.That(uow.DurabilityMode, Is.EqualTo(DurabilityMode.Deferred));
    }

    [Test]
    public void UoW_UowId_IsAllocated_FromRegistry()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();

        using var uow = dbe.CreateUnitOfWork();
        Assert.That(uow.UowId, Is.GreaterThan((ushort)0), "UoW ID should be allocated from registry (1+)");
    }

    // ═══════════════════════════════════════════════════════════════
    // Transaction Binding Tests
    // ═══════════════════════════════════════════════════════════════

    [Test]
    public void Tx_OwningUnitOfWork_SetViaInit()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        dbe.InitializeArchetypes();

        using var uow = dbe.CreateUnitOfWork();
        using var tx = uow.CreateTransaction();

        Assert.That(tx.OwningUnitOfWork, Is.SameAs(uow));
    }

    // ═══════════════════════════════════════════════════════════════
    // CreateQuickTransaction Tests
    // ═══════════════════════════════════════════════════════════════

    [Test]
    public void QuickTx_CommitThenDispose_CleanLifecycle()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        dbe.InitializeArchetypes();

        UnitOfWork capturedUow;
        EntityId entityId;

        // Create, use, commit, dispose — all in one block
        {
            var tx = dbe.CreateQuickTransaction();
            capturedUow = tx.OwningUnitOfWork;
            Assert.That(capturedUow, Is.Not.Null);
            Assert.That(capturedUow.State, Is.EqualTo(UnitOfWorkState.Pending));
            Assert.That(tx.OwnsUnitOfWork, Is.True);

            var comp = new CompA(99);
            entityId = tx.Spawn<CompAArch>(CompAArch.A.Set(in comp));
            tx.Commit();
            tx.Dispose();
        }

        // After dispose, the UoW should also be disposed
        Assert.That(capturedUow.IsDisposed, Is.True);
        Assert.That(capturedUow.State, Is.EqualTo(UnitOfWorkState.Free));

        // Verify the data is actually committed and readable
        using var uow2 = dbe.CreateUnitOfWork();
        using var readTx = uow2.CreateTransaction();
        var readComp = readTx.Open(entityId).Read(CompAArch.A);
        Assert.That(readComp.A, Is.EqualTo(99));
    }

    [Test]
    public void QuickTx_DisposesUoW()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();

        var tx = dbe.CreateQuickTransaction();
        var uow = tx.OwningUnitOfWork;

        Assert.That(uow.IsDisposed, Is.False);
        tx.Dispose();
        Assert.That(uow.IsDisposed, Is.True, "QuickTransaction dispose should also dispose the backing UoW");
    }

    [Test]
    public void QuickTx_DurabilityMode_Passthrough()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();

        using var tx = dbe.CreateQuickTransaction(DurabilityMode.Immediate);
        Assert.That(tx.OwningUnitOfWork.DurabilityMode, Is.EqualTo(DurabilityMode.Immediate));
    }

    // ═══════════════════════════════════════════════════════════════
    // Integration: Full CRUD Through UoW
    // ═══════════════════════════════════════════════════════════════

    [Test]
    public void CreateEntity_ThroughUoW_Succeeds()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        dbe.InitializeArchetypes();

        EntityId entityId;
        {
            using var uow = dbe.CreateUnitOfWork();
            using var tx = uow.CreateTransaction();

            var comp = new CompA(123, 4.56f, 7.89);
            entityId = tx.Spawn<CompAArch>(CompAArch.A.Set(in comp));
            Assert.That(entityId.IsNull, Is.False);
            tx.Commit();
        }

        // Read back through a new UoW
        {
            using var uow = dbe.CreateUnitOfWork();
            using var tx = uow.CreateTransaction();

            var read = tx.Open(entityId).Read(CompAArch.A);
            Assert.That(read.A, Is.EqualTo(123));
            Assert.That(read.B, Is.EqualTo(4.56f));
            Assert.That(read.C, Is.EqualTo(7.89));
        }
    }

    [Test]
    public void MultipleUoWs_ConcurrentAccess()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        dbe.InitializeArchetypes();

        // Create data in UoW 1
        EntityId entityId;
        {
            using var uow1 = dbe.CreateUnitOfWork();
            using var tx1 = uow1.CreateTransaction();
            var comp = new CompA(100);
            entityId = tx1.Spawn<CompAArch>(CompAArch.A.Set(in comp));
            tx1.Commit();
        }

        // Read from UoW 2 and UoW 3 simultaneously
        using var uow2 = dbe.CreateUnitOfWork();
        using var uow3 = dbe.CreateUnitOfWork();

        using var tx2 = uow2.CreateTransaction();
        using var tx3 = uow3.CreateTransaction();

        var read2 = tx2.Open(entityId).Read(CompAArch.A);
        var read3 = tx3.Open(entityId).Read(CompAArch.A);

        Assert.That(read2.A, Is.EqualTo(100));
        Assert.That(read3.A, Is.EqualTo(100));

        tx2.Commit();
        tx3.Commit();
    }
}
