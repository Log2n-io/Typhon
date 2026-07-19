using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using System.Threading;
using System.Threading.Tasks;

namespace Typhon.Engine.Tests;

/// <summary>
/// Canonical tests for write-write conflict detection and resolution via <see cref="ConcurrencyConflictHandler"/>.
/// Covers: no-conflict path, "last wins" without handler, handler-based resolution (delta rebase, take-committed, take-read), multi-entity conflicts,
/// and concurrent thread scenarios.
/// </summary>
class ConcurrencyConflictTests : TestBase<ConcurrencyConflictTests>
{
    [OneTimeSetUp]
    public void OneTimeSetup()
    {
    }

    // ═══════════════════════════════════════════════════════════════
    // 1. No Conflict — handler provided but never invoked
    // ═══════════════════════════════════════════════════════════════

    [Test]
    public void NoConflict_WithHandler_CommitsNormally()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        dbe.InitializeArchetypes();

        EntityId entityId;
        {
            using var tCreate = dbe.CreateQuickTransaction();
            var a = new CompA(10);
            entityId = tCreate.Spawn<CompAArch>(CompAArch.A.Set(in a));
            tCreate.Commit();
        }

        // Single writer, no concurrent update — handler should NOT be called
        using var t1 = dbe.CreateQuickTransaction();
        t1.Open(entityId).Read(CompAArch.A);
        ref var w = ref t1.OpenMut(entityId).Write(CompAArch.A);
        w = new CompA(42);

        var handlerCalled = false;

        void ConcurrencyConflictHandler(ref ConcurrencyConflictSolver solver)
        {
            handlerCalled = true;
        }

        Assert.That(t1.Commit(ConcurrencyConflictHandler), Is.True);
        Assert.That(handlerCalled, Is.False, "Handler should not be invoked when there is no conflict");

        using var tRead = dbe.CreateQuickTransaction();
        var result = tRead.Open(entityId).Read(CompAArch.A);
        Assert.That(result.A, Is.EqualTo(42));
    }

    // ═══════════════════════════════════════════════════════════════
    // 2. "Last Wins" — no handler, conflicting update overwrites
    // ═══════════════════════════════════════════════════════════════

    [Test]
    public void Conflict_NoHandler_LastWins()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        dbe.InitializeArchetypes();

        EntityId entityId;
        {
            using var tCreate = dbe.CreateQuickTransaction();
            var a = new CompA(10);
            entityId = tCreate.Spawn<CompAArch>(CompAArch.A.Set(in a));
            tCreate.Commit();
        }

        // T1 reads and updates
        using var t1 = dbe.CreateQuickTransaction();
        t1.Open(entityId).Read(CompAArch.A);
        ref var w1 = ref t1.OpenMut(entityId).Write(CompAArch.A);
        w1 = new CompA(20);

        // T2 updates and commits first
        {
            using var t2 = dbe.CreateQuickTransaction();
            ref var w2 = ref t2.OpenMut(entityId).Write(CompAArch.A);
            w2 = new CompA(30);
            t2.Commit();
        }

        // T1 commits without handler — "last wins", T1's value should overwrite
        Assert.That(t1.Commit(), Is.True);

        using var tRead = dbe.CreateQuickTransaction();
        var result = tRead.Open(entityId).Read(CompAArch.A);
        Assert.That(result.A, Is.EqualTo(20), "Last writer wins: T1's value should be visible");
    }

    // ═══════════════════════════════════════════════════════════════
    // 3. Handler — Delta Rebase (operational transform)
    // ═══════════════════════════════════════════════════════════════

    [Test]
    public void Conflict_WithHandler_DeltaRebase()
    {
        // Entity starts at 100. T2 sets it to 130 (delta +30). T1 sets it to 90 (delta -10).
        // After conflict resolution: committed(130) + delta(-10) = 120.
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        dbe.InitializeArchetypes();

        EntityId entityId;
        {
            using var tCreate = dbe.CreateQuickTransaction();
            var a = new CompA(100);
            entityId = tCreate.Spawn<CompAArch>(CompAArch.A.Set(in a));
            tCreate.Commit();
        }

        long pk = (long)entityId.RawValue;

        // T1 reads (100), will set to 90
        using var t1 = dbe.CreateQuickTransaction();
        t1.Open(entityId).Read(CompAArch.A);
        ref var w1 = ref t1.OpenMut(entityId).Write(CompAArch.A);
        w1 = new CompA(90);

        // T2 reads (100), sets to 130, commits first
        {
            using var t2 = dbe.CreateQuickTransaction();
            t2.Open(entityId).Read(CompAArch.A);
            ref var w2 = ref t2.OpenMut(entityId).Write(CompAArch.A);
            w2 = new CompA(130);
            t2.Commit();
        }

        // T1 commits with delta rebase handler
        var handlerCalled = false;

        void ConcurrencyConflictHandler(ref ConcurrencyConflictSolver solver)
        {
            handlerCalled = true;
            Assert.That(solver.HasConflict, Is.True);
            Assert.That(solver.PrimaryKey, Is.EqualTo(pk));

            var read = solver.ReadData<CompA>();
            var committed = solver.CommittedData<CompA>();
            var committing = solver.CommittingData<CompA>();

            Assert.That(read.A, Is.EqualTo(100), "Read should be the original snapshot value");
            Assert.That(committed.A, Is.EqualTo(130), "Committed should be T2's value");
            Assert.That(committing.A, Is.EqualTo(90), "Committing should be T1's dirty write");

            // Delta rebase: apply our intent on top of latest committed
            var delta = committing.A - read.A; // -10
            solver.ToCommitData<CompA>().A = committed.A + delta; // 130 + (-10) = 120
        }

        Assert.That(t1.Commit(ConcurrencyConflictHandler), Is.True);
        Assert.That(handlerCalled, Is.True, "Handler must be called when a conflict exists");

        using var tRead = dbe.CreateQuickTransaction();
        var result = tRead.Open(entityId).Read(CompAArch.A);
        Assert.That(result.A, Is.EqualTo(120), "Result should be committed(130) + delta(-10) = 120");
    }

    // ═══════════════════════════════════════════════════════════════
    // 4. Handler — TakeCommitted (accept other transaction's value)
    // ═══════════════════════════════════════════════════════════════

    [Test]
    public void Conflict_WithHandler_TakeCommitted()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        dbe.InitializeArchetypes();

        EntityId entityId;
        {
            using var tCreate = dbe.CreateQuickTransaction();
            var a = new CompA(10);
            entityId = tCreate.Spawn<CompAArch>(CompAArch.A.Set(in a));
            tCreate.Commit();
        }

        using var t1 = dbe.CreateQuickTransaction();
        t1.Open(entityId).Read(CompAArch.A);
        ref var w1 = ref t1.OpenMut(entityId).Write(CompAArch.A);
        w1 = new CompA(20);

        {
            using var t2 = dbe.CreateQuickTransaction();
            t2.Open(entityId).Read(CompAArch.A);
            ref var w2 = ref t2.OpenMut(entityId).Write(CompAArch.A);
            w2 = new CompA(30);
            t2.Commit();
        }

        // Handler accepts the committed (T2's) value
        void ConcurrencyConflictHandler(ref ConcurrencyConflictSolver solver)
        {
            solver.TakeCommitted<CompA>();
        }

        Assert.That(t1.Commit(ConcurrencyConflictHandler), Is.True);

        using var tRead = dbe.CreateQuickTransaction();
        var result = tRead.Open(entityId).Read(CompAArch.A);
        Assert.That(result.A, Is.EqualTo(30), "TakeCommitted should preserve T2's value");
    }

    // ═══════════════════════════════════════════════════════════════
    // 5. Handler — TakeRead (revert to snapshot, discard all changes)
    // ═══════════════════════════════════════════════════════════════

    [Test]
    public void Conflict_WithHandler_TakeRead()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        dbe.InitializeArchetypes();

        EntityId entityId;
        {
            using var tCreate = dbe.CreateQuickTransaction();
            var a = new CompA(10);
            entityId = tCreate.Spawn<CompAArch>(CompAArch.A.Set(in a));
            tCreate.Commit();
        }

        using var t1 = dbe.CreateQuickTransaction();
        t1.Open(entityId).Read(CompAArch.A);
        ref var w1 = ref t1.OpenMut(entityId).Write(CompAArch.A);
        w1 = new CompA(20);

        {
            using var t2 = dbe.CreateQuickTransaction();
            t2.Open(entityId).Read(CompAArch.A);
            ref var w2 = ref t2.OpenMut(entityId).Write(CompAArch.A);
            w2 = new CompA(30);
            t2.Commit();
        }

        // Handler reverts to original read snapshot
        void ConcurrencyConflictHandler(ref ConcurrencyConflictSolver solver)
        {
            solver.TakeRead<CompA>();
        }

        Assert.That(t1.Commit(ConcurrencyConflictHandler), Is.True);

        using var tRead = dbe.CreateQuickTransaction();
        var result = tRead.Open(entityId).Read(CompAArch.A);
        Assert.That(result.A, Is.EqualTo(10), "TakeRead should revert to the original value");
    }

    // ═══════════════════════════════════════════════════════════════
    // 6. Multi-Entity — handler called once per conflicting entity
    // ═══════════════════════════════════════════════════════════════

    [Test]
    public void Conflict_MultipleEntities_HandlerCalledPerEntity()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        dbe.InitializeArchetypes();

        EntityId id1, id2;
        {
            using var tCreate = dbe.CreateQuickTransaction();
            var a1 = new CompA(100);
            var a2 = new CompA(200);
            id1 = tCreate.Spawn<CompAArch>(CompAArch.A.Set(in a1));
            id2 = tCreate.Spawn<CompAArch>(CompAArch.A.Set(in a2));
            tCreate.Commit();
        }

        long pk1 = (long)id1.RawValue;

        // T1 reads both, applies deltas: pk1 -= 10, pk2 += 10
        using var t1 = dbe.CreateQuickTransaction();
        t1.Open(id1).Read(CompAArch.A);
        t1.Open(id2).Read(CompAArch.A);
        ref var w1a = ref t1.OpenMut(id1).Write(CompAArch.A);
        w1a = new CompA(90);  // 100 - 10
        ref var w1b = ref t1.OpenMut(id2).Write(CompAArch.A);
        w1b = new CompA(210); // 200 + 10

        // T2 modifies both, commits first: pk1 = 150, pk2 = 250
        {
            using var t2 = dbe.CreateQuickTransaction();
            t2.Open(id1).Read(CompAArch.A);
            t2.Open(id2).Read(CompAArch.A);
            ref var w2a = ref t2.OpenMut(id1).Write(CompAArch.A);
            w2a = new CompA(150);
            ref var w2b = ref t2.OpenMut(id2).Write(CompAArch.A);
            w2b = new CompA(250);
            t2.Commit();
        }

        // T1 commits with delta rebase handler — should be called twice (once per entity)
        var handlerCallCount = 0;

        void ConcurrencyConflictHandler(ref ConcurrencyConflictSolver solver)
        {
            handlerCallCount++;
            var read = solver.ReadData<CompA>();
            var committed = solver.CommittedData<CompA>();
            var committing = solver.CommittingData<CompA>();
            var delta = committing.A - read.A;
            solver.ToCommitData<CompA>().A = committed.A + delta;
        }

        Assert.That(t1.Commit(ConcurrencyConflictHandler), Is.True);
        Assert.That(handlerCallCount, Is.EqualTo(2), "Handler should be called once per conflicting entity");

        // Verify: pk1 = 150 + (-10) = 140, pk2 = 250 + (+10) = 260
        using var tRead = dbe.CreateQuickTransaction();
        var r1 = tRead.Open(id1).Read(CompAArch.A);
        var r2 = tRead.Open(id2).Read(CompAArch.A);
        Assert.That(r1.A, Is.EqualTo(140), "pk1: committed(150) + delta(-10) = 140");
        Assert.That(r2.A, Is.EqualTo(260), "pk2: committed(250) + delta(+10) = 260");
    }

    // ═══════════════════════════════════════════════════════════════
    // 7. Multi-Entity — partial conflict (only one entity conflicts)
    // ═══════════════════════════════════════════════════════════════

    [Test]
    public void Conflict_MultipleEntities_PartialConflict()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        dbe.InitializeArchetypes();

        EntityId id1, id2;
        {
            using var tCreate = dbe.CreateQuickTransaction();
            var a1 = new CompA(100);
            var a2 = new CompA(200);
            id1 = tCreate.Spawn<CompAArch>(CompAArch.A.Set(in a1));
            id2 = tCreate.Spawn<CompAArch>(CompAArch.A.Set(in a2));
            tCreate.Commit();
        }

        long pk1 = (long)id1.RawValue;

        // T1 modifies both entities
        using var t1 = dbe.CreateQuickTransaction();
        t1.Open(id1).Read(CompAArch.A);
        t1.Open(id2).Read(CompAArch.A);
        ref var w1a = ref t1.OpenMut(id1).Write(CompAArch.A);
        w1a = new CompA(110); // +10
        ref var w1b = ref t1.OpenMut(id2).Write(CompAArch.A);
        w1b = new CompA(220); // +20

        // T2 modifies ONLY pk1, commits first
        {
            using var t2 = dbe.CreateQuickTransaction();
            t2.Open(id1).Read(CompAArch.A);
            ref var w2 = ref t2.OpenMut(id1).Write(CompAArch.A);
            w2 = new CompA(150);
            t2.Commit();
        }

        // Handler should only be called for pk1 (conflict), not pk2 (no conflict)
        var handlerCallCount = 0;
        long conflictedPk = 0;

        void ConcurrencyConflictHandler(ref ConcurrencyConflictSolver solver)
        {
            handlerCallCount++;
            conflictedPk = solver.PrimaryKey;
            var read = solver.ReadData<CompA>();
            var committed = solver.CommittedData<CompA>();
            var committing = solver.CommittingData<CompA>();
            var delta = committing.A - read.A;
            solver.ToCommitData<CompA>().A = committed.A + delta;
        }

        Assert.That(t1.Commit(ConcurrencyConflictHandler), Is.True);
        Assert.That(handlerCallCount, Is.EqualTo(1), "Handler should only be called for the conflicting entity");
        Assert.That(conflictedPk, Is.EqualTo(pk1), "The conflicting entity should be pk1");

        // pk1 = 150 + 10 = 160, pk2 = 220 (no conflict, direct commit)
        using var tRead = dbe.CreateQuickTransaction();
        var r1 = tRead.Open(id1).Read(CompAArch.A);
        var r2 = tRead.Open(id2).Read(CompAArch.A);
        Assert.That(r1.A, Is.EqualTo(160), "pk1: committed(150) + delta(+10) = 160");
        Assert.That(r2.A, Is.EqualTo(220), "pk2: no conflict, T1's value committed directly");
    }

    // ═══════════════════════════════════════════════════════════════
    // 8. Concurrent — two threads, both with handlers
    // ═══════════════════════════════════════════════════════════════

    [Test]
    public void Conflict_ConcurrentThreads_DeltaRebase_BothSucceed()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        dbe.InitializeArchetypes();

        EntityId entityId;
        {
            using var tCreate = dbe.CreateQuickTransaction();
            var a = new CompA(100);
            entityId = tCreate.Spawn<CompAArch>(CompAArch.A.Set(in a));
            tCreate.Commit();
        }

        var barrier = new Barrier(2);
        var result1 = false;
        var result2 = false;

        // T1: delta +10, T2: delta +20. One commits first (no conflict), the other rebases.
        // Final value should be 100 + 10 + 20 = 130 regardless of commit order.
        var t1Task = Task.Run(() =>
        {
            using var tx = dbe.CreateQuickTransaction();
            var read = tx.Open(entityId).Read(CompAArch.A);
            ref var w = ref tx.OpenMut(entityId).Write(CompAArch.A);
            w = new CompA(read.A + 10);
            barrier.SignalAndWait();

            void ConcurrencyConflictHandler(ref ConcurrencyConflictSolver solver)
            {
                var r = solver.ReadData<CompA>();
                var c = solver.CommittedData<CompA>();
                var m = solver.CommittingData<CompA>();
                solver.ToCommitData<CompA>().A = c.A + (m.A - r.A);
            }

            result1 = tx.Commit(ConcurrencyConflictHandler);
        });

        var t2Task = Task.Run(() =>
        {
            using var tx = dbe.CreateQuickTransaction();
            var read = tx.Open(entityId).Read(CompAArch.A);
            ref var w = ref tx.OpenMut(entityId).Write(CompAArch.A);
            w = new CompA(read.A + 20);
            barrier.SignalAndWait();

            void ConcurrencyConflictHandler(ref ConcurrencyConflictSolver solver)
            {
                var r = solver.ReadData<CompA>();
                var c = solver.CommittedData<CompA>();
                var m = solver.CommittingData<CompA>();
                solver.ToCommitData<CompA>().A = c.A + (m.A - r.A);
            }

            result2 = tx.Commit(ConcurrencyConflictHandler);
        });

        Task.WaitAll(t1Task, t2Task);

        Assert.That(result1, Is.True, "T1 should commit successfully");
        Assert.That(result2, Is.True, "T2 should commit successfully");

        using var tRead = dbe.CreateQuickTransaction();
        var final = tRead.Open(entityId).Read(CompAArch.A);
        Assert.That(final.A, Is.EqualTo(130), "100 + 10 + 20 = 130 (both deltas applied)");
    }

    // ═══════════════════════════════════════════════════════════════
    // 9. Solver reuse — thread-local instance is reused across commits
    // ═══════════════════════════════════════════════════════════════

    [Test]
    public void Conflict_SolverReuse_ThreadLocal()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        dbe.InitializeArchetypes();

        EntityId entityId;
        {
            using var tCreate = dbe.CreateQuickTransaction();
            var a = new CompA(1);
            entityId = tCreate.Spawn<CompAArch>(CompAArch.A.Set(in a));
            tCreate.Commit();
        }

        // First commit with handler — creates ThreadLocalConflictSolver
        {
            using var t1 = dbe.CreateQuickTransaction();
            ref var w = ref t1.OpenMut(entityId).Write(CompAArch.A);
            w = new CompA(2);

            void ConcurrencyConflictHandler(ref ConcurrencyConflictSolver solver)
            {
            }

            t1.Commit(ConcurrencyConflictHandler);
        }

        // Second commit with handler — reuses solver (Reset path)
        {
            using var t2 = dbe.CreateQuickTransaction();
            ref var w = ref t2.OpenMut(entityId).Write(CompAArch.A);
            w = new CompA(3);

            void ConcurrencyConflictHandler(ref ConcurrencyConflictSolver solver)
            {
            }

            Assert.That(t2.Commit(ConcurrencyConflictHandler), Is.True, "Second commit should succeed (solver reuse via Reset)");
        }

        using var tRead = dbe.CreateQuickTransaction();
        var result = tRead.Open(entityId).Read(CompAArch.A);
        Assert.That(result.A, Is.EqualTo(3));
    }

    // ═══════════════════════════════════════════════════════════════
    // 10. Default handler behavior — TakeCommitting (last writer wins)
    // ═══════════════════════════════════════════════════════════════

    [Test]
    public void Conflict_WithHandler_DefaultIsLastWins()
    {
        // If the handler does nothing, the default ToCommitData is a copy of CommittingData (last wins).
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        dbe.InitializeArchetypes();

        EntityId entityId;
        {
            using var tCreate = dbe.CreateQuickTransaction();
            var a = new CompA(10);
            entityId = tCreate.Spawn<CompAArch>(CompAArch.A.Set(in a));
            tCreate.Commit();
        }

        using var t1 = dbe.CreateQuickTransaction();
        t1.Open(entityId).Read(CompAArch.A);
        ref var w1 = ref t1.OpenMut(entityId).Write(CompAArch.A);
        w1 = new CompA(50);

        {
            using var t2 = dbe.CreateQuickTransaction();
            t2.Open(entityId).Read(CompAArch.A);
            ref var w2 = ref t2.OpenMut(entityId).Write(CompAArch.A);
            w2 = new CompA(99);
            t2.Commit();
        }

        // No-op handler: ToCommitData is pre-initialized with CommittingData (T1's dirty write = 50)
        void ConcurrencyConflictHandler(ref ConcurrencyConflictSolver solver)
        {
            // Intentionally empty — default behavior should be "last wins" (T1's value = 50)
        }

        Assert.That(t1.Commit(ConcurrencyConflictHandler), Is.True);

        using var tRead = dbe.CreateQuickTransaction();
        var result = tRead.Open(entityId).Read(CompAArch.A);
        Assert.That(result.A, Is.EqualTo(50), "Default (no-op handler) should be last writer wins: T1's value");
    }
}
