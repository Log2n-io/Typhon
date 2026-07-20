using System;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;

namespace Typhon.Engine.Tests;

/// <summary>
/// Negative-path tests verifying correct error behavior for ECS API misuse.
/// Covers: non-existent entities, destroyed entity access, double-destroy, double-commit, post-commit operations.
/// </summary>
class ErrorCaseTests : TestBase<ErrorCaseTests>
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

    // ═══════════════════════════════════════════════════════════════════════
    // Open on non-existent entity
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public void Open_NonExistentEntity_ThrowsInvalidOperation()
    {
        using var dbe = SetupEngine();
        using var t = dbe.CreateQuickTransaction();

        var bogusId = new EntityId(999999, 0);
        Assert.Throws<InvalidOperationException>(() => t.Open(bogusId));
    }

    [Test]
    public void OpenMut_NonExistentEntity_ThrowsInvalidOperation()
    {
        using var dbe = SetupEngine();
        using var t = dbe.CreateQuickTransaction();

        var bogusId = new EntityId(999999, 0);
        Assert.Throws<InvalidOperationException>(() => t.OpenMut(bogusId));
    }

    [Test]
    public void TryOpen_NonExistentEntity_ReturnsFalse()
    {
        using var dbe = SetupEngine();
        using var t = dbe.CreateQuickTransaction();

        var bogusId = new EntityId(999999, 0);
        bool found = t.TryOpen(bogusId, out var entity);
        Assert.That(found, Is.False);
        Assert.That(entity.IsValid, Is.False);
    }

    [Test]
    public void IsAlive_NonExistentEntity_ReturnsFalse()
    {
        using var dbe = SetupEngine();
        using var t = dbe.CreateQuickTransaction();

        var bogusId = new EntityId(999999, 0);
        Assert.That(t.IsAlive(bogusId), Is.False);
    }

    [Test]
    public void IsAlive_NullEntityId_ReturnsFalse()
    {
        using var dbe = SetupEngine();
        using var t = dbe.CreateQuickTransaction();

        Assert.That(t.IsAlive(EntityId.Null), Is.False);
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Open/IsAlive after Destroy in same transaction
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public void Open_AfterDestroySameTx_ThrowsInvalidOperation()
    {
        using var dbe = SetupEngine();

        EntityId id;
        using (var t = dbe.CreateQuickTransaction())
        {
            var pos = new EcsPosition(1, 2, 3);
            id = t.Spawn<EcsUnit>(EcsUnit.Position.Set(in pos));
            t.Commit();
        }

        using (var t = dbe.CreateQuickTransaction())
        {
            // Entity is alive
            Assert.That(t.IsAlive(id), Is.True);

            // Destroy it
            t.Destroy(id);

            // Now Open should throw — entity is pending destroy
            Assert.Throws<InvalidOperationException>(() => t.Open(id));
        }
    }

    [Test]
    public void TryOpen_AfterDestroySameTx_ReturnsFalse()
    {
        using var dbe = SetupEngine();

        EntityId id;
        using (var t = dbe.CreateQuickTransaction())
        {
            var pos = new EcsPosition(1, 2, 3);
            id = t.Spawn<EcsUnit>(EcsUnit.Position.Set(in pos));
            t.Commit();
        }

        using (var t = dbe.CreateQuickTransaction())
        {
            t.Destroy(id);
            bool found = t.TryOpen(id, out _);
            Assert.That(found, Is.False);
        }
    }

    [Test]
    public void IsAlive_AfterDestroySameTx_ReturnsFalse()
    {
        using var dbe = SetupEngine();

        EntityId id;
        using (var t = dbe.CreateQuickTransaction())
        {
            var pos = new EcsPosition(1, 2, 3);
            id = t.Spawn<EcsUnit>(EcsUnit.Position.Set(in pos));
            t.Commit();
        }

        using (var t = dbe.CreateQuickTransaction())
        {
            Assert.That(t.IsAlive(id), Is.True);
            t.Destroy(id);
            Assert.That(t.IsAlive(id), Is.False);
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Double-destroy (same tx)
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public void Destroy_Twice_SameTx_IsNoOp()
    {
        using var dbe = SetupEngine();

        EntityId id;
        using (var t = dbe.CreateQuickTransaction())
        {
            var pos = new EcsPosition(1, 2, 3);
            id = t.Spawn<EcsUnit>(EcsUnit.Position.Set(in pos));
            t.Commit();
        }

        // Double-destroy should not throw
        using (var t = dbe.CreateQuickTransaction())
        {
            t.Destroy(id);
            t.Destroy(id); // second call is a no-op
            t.Commit();    // commit succeeds
        }

        // Entity should be dead
        using (var t = dbe.CreateQuickTransaction())
        {
            Assert.That(t.IsAlive(id), Is.False);
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Double-commit
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public void Commit_Twice_ReturnsFalseOnSecond()
    {
        using var dbe = SetupEngine();

        using var t = dbe.CreateQuickTransaction();
        var pos = new EcsPosition(1, 2, 3);
        t.Spawn<EcsUnit>(EcsUnit.Position.Set(in pos));

        bool first = t.Commit();
        Assert.That(first, Is.True);

        bool second = t.Commit();
        Assert.That(second, Is.False, "Second commit should return false, not throw");
    }

    [Test]
    public void Commit_EmptyTransaction_ReturnsTrue()
    {
        using var dbe = SetupEngine();

        using var t = dbe.CreateQuickTransaction();
        // No operations — transaction is in Created state
        bool result = t.Commit();
        Assert.That(result, Is.True, "Empty transaction commit should succeed");
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Post-commit / post-dispose operations
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public void Spawn_AfterCommit_ThrowsInvalidOperation()
    {
        using var dbe = SetupEngine();

        using var t = dbe.CreateQuickTransaction();
        var pos = new EcsPosition(1, 2, 3);
        t.Spawn<EcsUnit>(EcsUnit.Position.Set(in pos));
        t.Commit();

        Assert.Throws<InvalidOperationException>(() =>
            t.Spawn<EcsUnit>(EcsUnit.Position.Set(in pos)));
    }

    [Test]
    public void Destroy_AfterCommit_ThrowsInvalidOperation()
    {
        using var dbe = SetupEngine();

        EntityId id1, id2;
        using (var t = dbe.CreateQuickTransaction())
        {
            var pos = new EcsPosition(1, 2, 3);
            id1 = t.Spawn<EcsUnit>(EcsUnit.Position.Set(in pos));
            id2 = t.Spawn<EcsUnit>(EcsUnit.Position.Set(in pos));
            t.Commit();
        }

        // Perform a write to transition State to InProgress, then commit
        using var t2 = dbe.CreateQuickTransaction();
        t2.Destroy(id1);
        t2.Commit();

        // Destroy after a real commit should throw (State == Committed)
        Assert.Throws<InvalidOperationException>(() => t2.Destroy(id2));
    }

    [Test]
    public void OpenMut_AfterCommit_ThrowsInvalidOperation()
    {
        using var dbe = SetupEngine();

        EntityId id;
        using (var t = dbe.CreateQuickTransaction())
        {
            var pos = new EcsPosition(1, 2, 3);
            id = t.Spawn<EcsUnit>(EcsUnit.Position.Set(in pos));
            t.Commit();
        }

        // Perform a write to transition State to InProgress, then commit
        using var t2 = dbe.CreateQuickTransaction();
        var pos2 = new EcsPosition(9, 9, 9);
        t2.Spawn<EcsUnit>(EcsUnit.Position.Set(in pos2));
        t2.Commit();

        // OpenMut after a real commit should throw (State == Committed)
        Assert.Throws<InvalidOperationException>(() => t2.OpenMut(id));
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Spawn-then-destroy same transaction (A8 overlap)
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public void SpawnThenDestroy_SameTx_EntityNeverVisible()
    {
        using var dbe = SetupEngine();

        EntityId id;
        using (var t = dbe.CreateQuickTransaction())
        {
            var pos = new EcsPosition(1, 2, 3);
            id = t.Spawn<EcsUnit>(EcsUnit.Position.Set(in pos));

            // Destroy what we just spawned
            t.Destroy(id);

            // Should be dead immediately in same tx
            Assert.That(t.IsAlive(id), Is.False);
            Assert.That(t.TryOpen(id, out _), Is.False);

            t.Commit();
        }

        // Should remain dead in subsequent transaction
        using (var t = dbe.CreateQuickTransaction())
        {
            Assert.That(t.IsAlive(id), Is.False);
        }
    }

    [Test]
    public void SpawnThenDestroy_SameTx_QueryReturnsEmpty()
    {
        using var dbe = SetupEngine();

        using (var t = dbe.CreateQuickTransaction())
        {
            var pos = new EcsPosition(1, 2, 3);
            var id = t.Spawn<EcsUnit>(EcsUnit.Position.Set(in pos));
            t.Destroy(id);

            // Query should not find the spawned-then-destroyed entity
            var query = t.Query<EcsUnit>();
            Assert.That(query.Count(), Is.EqualTo(0));
            Assert.That(query.Any(), Is.False);

            t.Commit();
        }
    }
}
