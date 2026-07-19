using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;

namespace Typhon.Engine.Tests;

[NonParallelizable]
class EnableDisableTests : TestBase<EnableDisableTests>
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
    public void EnabledBits_AllEnabled_AfterSpawnWithAllValues()
    {
        using var dbe = SetupEngine();

        using var t = dbe.CreateQuickTransaction();
        var pos = new EcsPosition(1, 2, 3);
        var vel = new EcsVelocity(4, 5, 6);
        var id = t.Spawn<EcsUnit>(EcsUnit.Position.Set(in pos), EcsUnit.Velocity.Set(in vel));

        var entity = t.Open(id);
        Assert.That(entity.IsEnabled(EcsUnit.Position), Is.True);
        Assert.That(entity.IsEnabled(EcsUnit.Velocity), Is.True);
    }

    [Test]
    public void EnabledBits_PartiallyEnabled_WhenNotAllValuesProvided()
    {
        using var dbe = SetupEngine();

        using var t = dbe.CreateQuickTransaction();
        // Only provide Position, not Velocity
        var pos = new EcsPosition(1, 2, 3);
        var id = t.Spawn<EcsUnit>(EcsUnit.Position.Set(in pos));

        var entity = t.Open(id);
        Assert.That(entity.IsEnabled(EcsUnit.Position), Is.True);
        Assert.That(entity.IsEnabled(EcsUnit.Velocity), Is.False);
    }

    [Test]
    public void Disable_Component_BecomesDisabled()
    {
        using var dbe = SetupEngine();

        using var t = dbe.CreateQuickTransaction();
        var pos = new EcsPosition(1, 2, 3);
        var vel = new EcsVelocity(4, 5, 6);
        var id = t.Spawn<EcsUnit>(EcsUnit.Position.Set(in pos), EcsUnit.Velocity.Set(in vel));

        var entity = t.OpenMut(id);
        Assert.That(entity.IsEnabled(EcsUnit.Velocity), Is.True);

        entity.Disable(EcsUnit.Velocity);
        Assert.That(entity.IsEnabled(EcsUnit.Velocity), Is.False);
        Assert.That(entity.IsEnabled(EcsUnit.Position), Is.True); // still enabled
    }

    [Test]
    public void Enable_DisabledComponent_BecomesEnabled()
    {
        using var dbe = SetupEngine();

        using var t = dbe.CreateQuickTransaction();
        var pos = new EcsPosition(1, 2, 3);
        var id = t.Spawn<EcsUnit>(EcsUnit.Position.Set(in pos)); // Velocity not provided = disabled

        var entity = t.OpenMut(id);
        Assert.That(entity.IsEnabled(EcsUnit.Velocity), Is.False);

        entity.Enable(EcsUnit.Velocity);
        Assert.That(entity.IsEnabled(EcsUnit.Velocity), Is.True);
    }

    [Test]
    public void EnableDisable_PersistsAfterCommit()
    {
        using var dbe = SetupEngine();

        EntityId id;
        using (var t = dbe.CreateQuickTransaction())
        {
            var pos = new EcsPosition(1, 2, 3);
            var vel = new EcsVelocity(4, 5, 6);
            id = t.Spawn<EcsUnit>(EcsUnit.Position.Set(in pos), EcsUnit.Velocity.Set(in vel));
            t.Commit();
        }

        // Disable Velocity in new transaction
        using (var t = dbe.CreateQuickTransaction())
        {
            var entity = t.OpenMut(id);
            entity.Disable(EcsUnit.Velocity);
            t.Commit();
        }

        // Verify Velocity is disabled in new transaction
        using (var t = dbe.CreateQuickTransaction())
        {
            var entity = t.Open(id);
            Assert.That(entity.IsEnabled(EcsUnit.Position), Is.True);
            Assert.That(entity.IsEnabled(EcsUnit.Velocity), Is.False);
        }
    }

    [Test]
    public void EnabledBitsOverrides_FastPath_NoOverheadWhenNoOverrides()
    {
        // Verify the fast-path optimization: when OverrideCount == 0,
        // ResolveEnabledBits returns the inline bits directly
        var overrides = new EnabledBitsOverrides();
        Assert.That(overrides._overrideCount, Is.EqualTo(0));

        ushort result = overrides.ResolveEnabledBits(42L, 0b1111, 100);
        Assert.That(result, Is.EqualTo(0b1111));
    }

    [Test]
    public void EnabledBitsHistory_ResolveAt_ReturnsOldBitsForOlderTx()
    {
        var history = new EnabledBitsHistory();
        // At TSN=10, bits changed from 0b11 to 0b01
        history.Record(10, 0b11);

        // A transaction at TSN=5 (before the change) should see the old bits
        ushort resolved = history.ResolveAt(5, currentBits: 0b01);
        Assert.That(resolved, Is.EqualTo(0b11));

        // A transaction at TSN=15 (after the change) should see current bits
        ushort resolvedAfter = history.ResolveAt(15, currentBits: 0b01);
        Assert.That(resolvedAfter, Is.EqualTo(0b01));
    }

    [Test]
    public void EnabledBitsHistory_TryPrune_RemovesOldEntries()
    {
        var history = new EnabledBitsHistory();
        history.Record(5, 0b11);
        history.Record(10, 0b10);
        history.Record(15, 0b01);

        Assert.That(history.Count, Is.EqualTo(3));

        // Prune entries at or below TSN=10
        bool fullyPruned = history.TryPrune(10);
        Assert.That(fullyPruned, Is.False);
        Assert.That(history.Count, Is.EqualTo(1)); // only TSN=15 remains

        // Prune remaining
        fullyPruned = history.TryPrune(20);
        Assert.That(fullyPruned, Is.True);
    }

    [Test]
    public void EnableDisable_MVCC_OlderTxSeesOriginalBits()
    {
        using var dbe = SetupEngine();

        // Phase 1: Spawn entity with both components enabled
        EntityId id;
        using (var t = dbe.CreateQuickTransaction())
        {
            var pos = new EcsPosition(1, 2, 3);
            var vel = new EcsVelocity(4, 5, 6);
            id = t.Spawn<EcsUnit>(EcsUnit.Position.Set(in pos), EcsUnit.Velocity.Set(in vel));
            t.Commit();
        }

        // Phase 2: Open tx1 (older snapshot) — keeps its TSN
        using var tx1 = dbe.CreateQuickTransaction();

        // Phase 3: tx2 disables Velocity and commits (newer TSN)
        using (var tx2 = dbe.CreateQuickTransaction())
        {
            var entity = tx2.OpenMut(id);
            Assert.That(entity.IsEnabled(EcsUnit.Velocity), Is.True);
            entity.Disable(EcsUnit.Velocity);
            tx2.Commit();
        }

        // Phase 4: tx1 (older snapshot) should still see Velocity as ENABLED
        // because the disable happened at a TSN > tx1.TSN
        var entityFromTx1 = tx1.Open(id);
        Assert.That(entityFromTx1.IsEnabled(EcsUnit.Position), Is.True, "Position should be enabled for old tx");
        Assert.That(entityFromTx1.IsEnabled(EcsUnit.Velocity), Is.True, "Velocity should still be enabled for old tx (MVCC)");

        // Phase 5: New tx (newest snapshot) should see Velocity as DISABLED
        using (var tx3 = dbe.CreateQuickTransaction())
        {
            var entityFromTx3 = tx3.Open(id);
            Assert.That(entityFromTx3.IsEnabled(EcsUnit.Velocity), Is.False, "Velocity should be disabled for new tx");
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Gap coverage — additional scenarios
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public void MultipleToggles_SameTx_LastStateWins()
    {
        using var dbe = SetupEngine();

        EntityId id;
        using (var t = dbe.CreateQuickTransaction())
        {
            var pos = new EcsPosition(1, 2, 3);
            var vel = new EcsVelocity(4, 5, 6);
            id = t.Spawn<EcsUnit>(EcsUnit.Position.Set(in pos), EcsUnit.Velocity.Set(in vel));
            t.Commit();
        }

        // Toggle Velocity: disable → enable → disable → enable in same tx
        using (var t = dbe.CreateQuickTransaction())
        {
            var entity = t.OpenMut(id);
            entity.Disable(EcsUnit.Velocity);
            Assert.That(entity.IsEnabled(EcsUnit.Velocity), Is.False);
            entity.Enable(EcsUnit.Velocity);
            Assert.That(entity.IsEnabled(EcsUnit.Velocity), Is.True);
            entity.Disable(EcsUnit.Velocity);
            Assert.That(entity.IsEnabled(EcsUnit.Velocity), Is.False);
            entity.Enable(EcsUnit.Velocity);
            Assert.That(entity.IsEnabled(EcsUnit.Velocity), Is.True);
            t.Commit();
        }

        // Final state: Velocity should be enabled (last toggle was Enable)
        using (var t = dbe.CreateQuickTransaction())
        {
            var entity = t.Open(id);
            Assert.That(entity.IsEnabled(EcsUnit.Velocity), Is.True);
            Assert.That(entity.IsEnabled(EcsUnit.Position), Is.True);
        }
    }

    [Test]
    public void EnableDisable_OnSpawnedEntity_BeforeCommit()
    {
        using var dbe = SetupEngine();

        // Spawn with both components, then disable Velocity before first commit
        EntityId id;
        using (var t = dbe.CreateQuickTransaction())
        {
            var pos = new EcsPosition(10, 20, 30);
            var vel = new EcsVelocity(1, 1, 1);
            id = t.Spawn<EcsUnit>(EcsUnit.Position.Set(in pos), EcsUnit.Velocity.Set(in vel));

            // Disable Velocity on the not-yet-committed entity
            var entity = t.OpenMut(id);
            entity.Disable(EcsUnit.Velocity);
            Assert.That(entity.IsEnabled(EcsUnit.Velocity), Is.False);
            Assert.That(entity.IsEnabled(EcsUnit.Position), Is.True);
            t.Commit();
        }

        // Verify the disable persisted through the first commit
        using (var t = dbe.CreateQuickTransaction())
        {
            var entity = t.Open(id);
            Assert.That(entity.IsEnabled(EcsUnit.Position), Is.True);
            Assert.That(entity.IsEnabled(EcsUnit.Velocity), Is.False);

            // Position data should be readable
            ref readonly var pos = ref entity.Read(EcsUnit.Position);
            Assert.That(pos.X, Is.EqualTo(10));
        }
    }

    [Test]
    public void EnableDisable_ThenDestroy_NoError()
    {
        using var dbe = SetupEngine();

        EntityId id;
        using (var t = dbe.CreateQuickTransaction())
        {
            var pos = new EcsPosition(1, 2, 3);
            var vel = new EcsVelocity(4, 5, 6);
            id = t.Spawn<EcsUnit>(EcsUnit.Position.Set(in pos), EcsUnit.Velocity.Set(in vel));
            t.Commit();
        }

        // Enable/Disable then destroy in same tx — should not crash on commit
        using (var t = dbe.CreateQuickTransaction())
        {
            var entity = t.OpenMut(id);
            entity.Disable(EcsUnit.Velocity);
            entity.Enable(EcsUnit.Position); // no-op (already enabled), but stages a change
            t.Destroy(id);
            t.Commit(); // must not throw
        }

        // Entity should be dead
        using (var t = dbe.CreateQuickTransaction())
        {
            Assert.That(t.IsAlive(id), Is.False);
        }
    }

    [Test]
    public void DisableAll_EntityStillAccessible_AllTryReadFalse()
    {
        using var dbe = SetupEngine();

        EntityId id;
        using (var t = dbe.CreateQuickTransaction())
        {
            var pos = new EcsPosition(1, 2, 3);
            var vel = new EcsVelocity(4, 5, 6);
            id = t.Spawn<EcsUnit>(EcsUnit.Position.Set(in pos), EcsUnit.Velocity.Set(in vel));
            t.Commit();
        }

        // Disable ALL components
        using (var t = dbe.CreateQuickTransaction())
        {
            var entity = t.OpenMut(id);
            entity.Disable(EcsUnit.Position);
            entity.Disable(EcsUnit.Velocity);
            t.Commit();
        }

        // Entity should still be alive and openable, but all TryReads fail
        using (var t = dbe.CreateQuickTransaction())
        {
            Assert.That(t.IsAlive(id), Is.True);
            var entity = t.Open(id);
            Assert.That(entity.IsEnabled(EcsUnit.Position), Is.False);
            Assert.That(entity.IsEnabled(EcsUnit.Velocity), Is.False);
            Assert.That(entity.TryRead<EcsPosition>(out _), Is.False);
            Assert.That(entity.TryRead<EcsVelocity>(out _), Is.False);
        }
    }

    [Test]
    public void Disable_PreservesData_AfterReEnable()
    {
        using var dbe = SetupEngine();

        EntityId id;
        using (var t = dbe.CreateQuickTransaction())
        {
            var pos = new EcsPosition(42, 84, 126);
            var vel = new EcsVelocity(7, 8, 9);
            id = t.Spawn<EcsUnit>(EcsUnit.Position.Set(in pos), EcsUnit.Velocity.Set(in vel));
            t.Commit();
        }

        // Disable Velocity
        using (var t = dbe.CreateQuickTransaction())
        {
            var entity = t.OpenMut(id);
            entity.Disable(EcsUnit.Velocity);
            t.Commit();
        }

        // Re-enable Velocity
        using (var t = dbe.CreateQuickTransaction())
        {
            var entity = t.OpenMut(id);
            Assert.That(entity.IsEnabled(EcsUnit.Velocity), Is.False);
            entity.Enable(EcsUnit.Velocity);
            t.Commit();
        }

        // Data should still be intact after disable → re-enable cycle
        using (var t = dbe.CreateQuickTransaction())
        {
            var entity = t.Open(id);
            Assert.That(entity.IsEnabled(EcsUnit.Velocity), Is.True);
            ref readonly var vel = ref entity.Read(EcsUnit.Velocity);
            Assert.That(vel.Dx, Is.EqualTo(7));
            Assert.That(vel.Dy, Is.EqualTo(8));
            Assert.That(vel.Dz, Is.EqualTo(9));
        }
    }

    [Test]
    public void EnableDisable_Query_SameTx_PendingChangesVisibleToQuery()
    {
        using var dbe = SetupEngine();

        EntityId id1, id2;
        using (var t = dbe.CreateQuickTransaction())
        {
            var pos = new EcsPosition(1, 2, 3);
            var vel = new EcsVelocity(4, 5, 6);
            id1 = t.Spawn<EcsUnit>(EcsUnit.Position.Set(in pos), EcsUnit.Velocity.Set(in vel));
            id2 = t.Spawn<EcsUnit>(EcsUnit.Position.Set(in pos), EcsUnit.Velocity.Set(in vel));
            t.Commit();
        }

        // Pending Enable/Disable changes ARE visible to queries in the same transaction (read-your-own-writes).
        using (var t = dbe.CreateQuickTransaction())
        {
            var entity = t.OpenMut(id1);
            entity.Disable(EcsUnit.Velocity);

            // EntityRef sees the disable immediately (local _enabledBits)
            Assert.That(entity.IsEnabled(EcsUnit.Velocity), Is.False);

            // Query with Enabled<Velocity> should exclude id1 (pending disable)
            var query = t.Query<EcsUnit>().Enabled<EcsVelocity>();
            Assert.That(query.Count(), Is.EqualTo(1));
            var results = query.Execute();
            Assert.That(results.Contains(id2), Is.True);
            Assert.That(results.Contains(id1), Is.False);
        }

        // Also verify with Disabled<T> filter
        using (var t = dbe.CreateQuickTransaction())
        {
            var entity = t.OpenMut(id1);
            entity.Disable(EcsUnit.Velocity);

            // Query with Disabled<Velocity> should include id1
            var query = t.Query<EcsUnit>().Disabled<EcsVelocity>();
            Assert.That(query.Count(), Is.EqualTo(1));
            var results = query.Execute();
            Assert.That(results.Contains(id1), Is.True);
            Assert.That(results.Contains(id2), Is.False);
        }
    }

    [Test]
    public void EnableDisable_SpawnWithPartial_ThenEnableMissing()
    {
        using var dbe = SetupEngine();

        // Spawn with only Position (Velocity disabled by default)
        EntityId id;
        using (var t = dbe.CreateQuickTransaction())
        {
            var pos = new EcsPosition(1, 2, 3);
            id = t.Spawn<EcsUnit>(EcsUnit.Position.Set(in pos));
            t.Commit();
        }

        // Enable the missing component — its data should be zero-initialized
        using (var t = dbe.CreateQuickTransaction())
        {
            var entity = t.OpenMut(id);
            Assert.That(entity.IsEnabled(EcsUnit.Velocity), Is.False);
            entity.Enable(EcsUnit.Velocity);
            Assert.That(entity.IsEnabled(EcsUnit.Velocity), Is.True);

            // Velocity data should be zero (default-initialized from chunk allocation)
            ref readonly var vel = ref entity.Read(EcsUnit.Velocity);
            Assert.That(vel.Dx, Is.EqualTo(0));
            Assert.That(vel.Dy, Is.EqualTo(0));
            Assert.That(vel.Dz, Is.EqualTo(0));
            t.Commit();
        }

        // Verify persisted
        using (var t = dbe.CreateQuickTransaction())
        {
            var entity = t.Open(id);
            Assert.That(entity.IsEnabled(EcsUnit.Velocity), Is.True);
        }
    }
}
