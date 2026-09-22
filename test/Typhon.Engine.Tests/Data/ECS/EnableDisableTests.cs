using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using System;
using System.Runtime.InteropServices;
using System.Threading;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests;

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

    /// <summary>
    /// Named for the case it does NOT exercise: an unsupplied component was never "disabled", it is absent (#845).
    /// </summary>
    /// <remarks>
    /// The distinction this test used to blur is the whole of #845. Disabled means "has a value, hidden" — re-enabling
    /// is free and returns the value. Absent means "no value was ever supplied" — there is nothing to re-enable. Both
    /// used to read as a clear EnabledBits bit, so both took the same path and absence silently borrowed a recycled
    /// chunk. The genuine disable/enable round trip is
    /// <c>UnsuppliedComponentPayloadTests.Versioned_DisableThenEnable_KeepsTheValue_AndNeedsNoNewOne</c>.
    /// </remarks>
    [Test]
    public void Enable_NeverSuppliedComponent_IsRefused()
    {
        using var dbe = SetupEngine();

        using var t = dbe.CreateQuickTransaction();
        var pos = new EcsPosition(1, 2, 3);
        var id = t.Spawn<EcsUnit>(EcsUnit.Position.Set(in pos)); // Velocity not provided = ABSENT, not disabled

        var entity = t.OpenMut(id);
        Assert.That(entity.IsEnabled(EcsUnit.Velocity), Is.False);

        var refused = false;
        try
        {
            entity.Enable(EcsUnit.Velocity);
        }
        catch (System.InvalidOperationException ex)
        {
            refused = true;
            Assert.That(ex.Message, Does.Contain("never supplied"));
        }

        Assert.That(refused, Is.True, "an absent component has no value to enable — Enable(comp, in value) supplies one");
    }

    /// <summary>The complement: supplying the value at the same time is accepted, even before the spawn commits.</summary>
    [Test]
    public void EnableWithValue_NeverSuppliedComponent_IsAccepted()
    {
        using var dbe = SetupEngine();

        using var t = dbe.CreateQuickTransaction();
        var pos = new EcsPosition(1, 2, 3);
        var id = t.Spawn<EcsUnit>(EcsUnit.Position.Set(in pos));

        var vel = new EcsVelocity(9, 8, 7);
        var entity = t.OpenMut(id);
        entity.Enable(EcsUnit.Velocity, in vel);

        Assert.That(entity.IsEnabled(EcsUnit.Velocity), Is.True);

        ref readonly var vr = ref t.Open(id).Read(EcsUnit.Velocity);
        Assert.That(vr.Dx, Is.EqualTo(9f));
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

    /// <summary>
    /// A component the spawn never supplied is ABSENT, so the no-value <c>Enable</c> is refused and the value-supplying
    /// overload is the way in (#845).
    /// </summary>
    /// <remarks>
    /// This asserted the reverse until #845: that enabling an unsupplied component yielded zeros. It never did. The
    /// component's storage was a RECYCLED chunk, so what surfaced was whatever a destroyed entity had last committed
    /// there; the test only read zero because its fixture happened to hand out a fresh chunk. Absence is now a state the
    /// engine represents — no chunk, no chain, root 0 — rather than a value it invents.
    /// </remarks>
    [Test]
    public void EnableDisable_SpawnWithPartial_ThenEnableMissing()
    {
        using var dbe = SetupEngine();

        // Spawn with only Position — Velocity is not merely disabled, it has no storage at all
        EntityId id;
        using (var t = dbe.CreateQuickTransaction())
        {
            var pos = new EcsPosition(1, 2, 3);
            id = t.Spawn<EcsUnit>(EcsUnit.Position.Set(in pos));
            t.Commit();
        }

        // The no-value Enable must refuse: there is no value to enable
        using (var t = dbe.CreateQuickTransaction())
        {
            var entity = t.OpenMut(id);
            Assert.That(entity.IsEnabled(EcsUnit.Velocity), Is.False);

            var refused = false;
            try
            {
                entity.Enable(EcsUnit.Velocity);
            }
            catch (System.InvalidOperationException ex)
            {
                refused = true;
                Assert.That(ex.Message, Does.Contain("never supplied"));
            }

            Assert.That(refused, Is.True,
                "enabling a component the spawn never supplied must be refused — it has no value, and inventing one "
              + "(zero, or whatever the recycled chunk held) is exactly the defect #845 records");
        }

        // The value-supplying overload is the sanctioned way to add it mid-life
        using (var t = dbe.CreateQuickTransaction())
        {
            var vel = new EcsVelocity(4, 5, 6);
            t.OpenMut(id).Enable(EcsUnit.Velocity, in vel);
            t.Commit();
        }

        using (var t = dbe.CreateQuickTransaction())
        {
            var entity = t.Open(id);
            Assert.That(entity.IsEnabled(EcsUnit.Velocity), Is.True);

            ref readonly var vr = ref entity.Read(EcsUnit.Velocity);
            var v = vr;
            Assert.That(v.Dx, Is.EqualTo(4f), "the supplied value must persist across the commit");
        }
    }

    // ── #998: the cluster copy of the enabled state (rule ENABLE-01) ─────────────────────────────────────────────────────────────────────────────────
    //
    // Every assertion below reads the cluster EnabledBits through ClusterSoAProbe. A point read uses the EntityMap record, which was right throughout #998:
    // the defect lived entirely in the copy bulk iteration and the crash rebuild read.

    private static EntityId SpawnUnit(DatabaseEngine dbe)
    {
        using var t = dbe.CreateQuickTransaction();
        var pos = new EcsPosition(1, 2, 3);
        var vel = new EcsVelocity(4, 5, 6);
        var id = t.Spawn<EcsUnit>(EcsUnit.Position.Set(in pos), EcsUnit.Velocity.Set(in vel));
        t.Commit();
        return id;
    }

    /// <summary>
    /// A rolled-back Enable or Disable leaves the cluster EnabledBits at the committed state, as it leaves the record (#998).
    /// </summary>
    /// <remarks>
    /// Enable/Disable used to write the cluster bit at staging, and nothing restored it on rollback. The two copies then disagreed with no crash involved: a
    /// rolled-back Disable hid the component from bulk iteration while the record still said enabled, and a checkpoint made that durable.
    /// </remarks>
    [Test]
    [VerifiesRule("ENABLE-01")]
    public void RolledBackChange_LeavesTheClusterBitAtTheCommittedState([Values] bool enable, [Values] bool explicitRollback)
    {
        using var dbe = SetupEngine();
        var id = SpawnUnit(dbe);
        var meta = Archetype<EcsUnit>.Metadata;
        var velSlot = meta.GetSlot(EcsUnit.Velocity._componentTypeId);

        // Rolling back an Enable needs a committed Disable to start from.
        if (enable)
        {
            using var t = dbe.CreateQuickTransaction();
            t.OpenMut(id).Disable(EcsUnit.Velocity);
            t.Commit();
        }

        var committed = !enable;

        using (var t = dbe.CreateQuickTransaction())
        {
            var entity = t.OpenMut(id);
            if (enable)
            {
                entity.Enable(EcsUnit.Velocity);
            }
            else
            {
                entity.Disable(EcsUnit.Velocity);
            }

            if (explicitRollback)
            {
                t.Rollback();
            }
        }

        AssertBothCopiesAtCommittedState(dbe, id, velSlot, committed);
    }

    /// <summary>
    /// The <see cref="RuleMutantAttribute"/> companion: reproduces the pre-fix staging-time write of the cluster bit, then rolls back, and requires the
    /// rollback verifier's assertion to reject the result.
    /// </summary>
    [Test]
    [RuleMutant("ENABLE-01")]
    public void AClusterBitWrittenAtStaging_IsRejectedByTheRollbackAssertion()
    {
        RuleMutants.AssertDetects("ENABLE-01", "must hold the committed state after a rollback", () =>
        {
            using var dbe = SetupEngine();
            var id = SpawnUnit(dbe);
            var meta = Archetype<EcsUnit>.Metadata;
            var velSlot = meta.GetSlot(EcsUnit.Velocity._componentTypeId);

            using (var t = dbe.CreateQuickTransaction())
            {
                t.OpenMut(id).Disable(EcsUnit.Velocity);
                ClusterSoAProbe.SetEnabled(dbe, meta.ArchetypeId, id, velSlot, false);   // what EntityRef.Disable used to do at staging
                t.Rollback();
            }

            AssertBothCopiesAtCommittedState(dbe, id, velSlot, true);
        });
    }

    private static void AssertBothCopiesAtCommittedState(DatabaseEngine dbe, EntityId id, int velSlot, bool committed)
    {
        using (var t = dbe.CreateQuickTransaction())
        {
            Assert.That(t.Open(id).IsEnabled(EcsUnit.Velocity), Is.EqualTo(committed), "the record must hold the committed state");
        }

        Assert.That(ClusterSoAProbe.IsEnabled(dbe, Archetype<EcsUnit>.Metadata.ArchetypeId, id, velSlot), Is.EqualTo(committed),
            "the cluster copy must hold the committed state after a rollback — bulk iteration reads it, not the record");
    }

    /// <summary>
    /// A staged change reaches the cluster EnabledBits at commit and not before (#998).
    /// </summary>
    /// <remarks>
    /// The cluster words are shared memory read by every bulk scan, unversioned. A bit written at staging was therefore visible to every concurrent
    /// transaction before the change committed — read-uncommitted on the bulk path.
    /// </remarks>
    [Test]
    [VerifiesRule("ENABLE-01")]
    public void StagedChange_ReachesTheClusterBitOnlyAtCommit()
    {
        using var dbe = SetupEngine();
        var id = SpawnUnit(dbe);
        var meta = Archetype<EcsUnit>.Metadata;
        var posSlot = meta.GetSlot(EcsUnit.Position._componentTypeId);
        var velSlot = meta.GetSlot(EcsUnit.Velocity._componentTypeId);

        using (var t = dbe.CreateQuickTransaction())
        {
            t.OpenMut(id).Disable(EcsUnit.Velocity);
            Assert.That(ClusterSoAProbe.IsEnabled(dbe, meta.ArchetypeId, id, velSlot), Is.True,
                "a staged Disable must not reach the cluster copy — every concurrent bulk scan reads it");
            t.Commit();
        }

        using (var t = dbe.CreateQuickTransaction())
        {
            Assert.That(t.Open(id).IsEnabled(EcsUnit.Velocity), Is.False, "the record must hold the committed Disable");
        }

        Assert.Multiple(() =>
        {
            Assert.That(ClusterSoAProbe.IsEnabled(dbe, meta.ArchetypeId, id, velSlot), Is.False, "the commit must publish the Disable to the cluster copy");
            Assert.That(ClusterSoAProbe.IsEnabled(dbe, meta.ArchetypeId, id, posSlot), Is.True, "a slot the change did not touch must stay enabled");
        });
    }

    /// <summary>
    /// Enable/Disable commits racing on different entities of one cluster lose no bit (#998).
    /// </summary>
    /// <remarks>
    /// <para>
    /// One cluster word holds a component's bit for every entity of the cluster, so two commits on different entities each read-modify-write the same word.
    /// A plain <c>|=</c> / <c>&amp;=</c> lets one overwrite the other's bit; the commit path uses <c>Interlocked</c>, as <c>FinalizeSpawns</c> does for the
    /// same words.
    /// </para>
    /// <para>
    /// The copies are compared after EVERY round, not only at the end: the publish writes the absolute mask, so a bit lost in one round is rewritten by the
    /// next commit on that entity and would be invisible to a final-state check. A timing race can only be caught, never forced — this is a guard.
    /// </para>
    /// </remarks>
    [Test]
    [CancelAfter(15_000)]
    public void ConcurrentCommitsInOneCluster_LoseNoBit()
    {
        const int threads = 4;
        const int perThread = 8;
        const int rounds = 8;

        using var dbe = SetupEngine();
        var meta = Archetype<EcsUnit>.Metadata;
        var velSlot = meta.GetSlot(EcsUnit.Velocity._componentTypeId);

        // One spawn transaction fills the archetype's clusters in order; workers take the entities interleaved (i % threads), so any cluster holding two
        // or more of them is written by more than one worker.
        var ids = new EntityId[threads * perThread];
        using (var t = dbe.CreateQuickTransaction())
        {
            for (var i = 0; i < ids.Length; i++)
            {
                var pos = new EcsPosition(i, 0, 0);
                var vel = new EcsVelocity(1, 1, 1);
                ids[i] = t.Spawn<EcsUnit>(EcsUnit.Position.Set(in pos), EcsUnit.Velocity.Set(in vel));
            }

            t.Commit();
        }

        // Premise: the race needs a word shared ACROSS threads. Every cluster the 32 entities occupy must hold entities of at least two workers, or the
        // test would pass while each thread wrote words nobody else touched.
        var workersByCluster = new System.Collections.Generic.Dictionary<int, System.Collections.Generic.HashSet<int>>();
        for (var i = 0; i < ids.Length; i++)
        {
            var chunkId = ClusterSoAProbe.Locate(dbe, meta.ArchetypeId, ids[i]).ChunkId;
            if (!workersByCluster.TryGetValue(chunkId, out var workerSet))
            {
                workersByCluster[chunkId] = workerSet = [];
            }

            workerSet.Add(i % threads);
        }

        foreach (var (chunkId, workerSet) in workersByCluster)
        {
            Assert.That(workerSet.Count, Is.GreaterThan(1), $"premise: cluster {chunkId} holds entities of one worker only, so nothing races on its words");
        }

        // Workers plus this thread: each round starts and ends on the barrier, and the copies are compared in between. A worker that fails keeps
        // signalling, so every round runs to completion and nobody is left blocked on the barrier.
        using var barrier = new Barrier(threads + 1);
        Exception failure = null;
        var workers = new Thread[threads];
        for (var w = 0; w < threads; w++)
        {
            var worker = w;
            workers[w] = new Thread(() =>
            {
                for (var round = 0; round < rounds; round++)
                {
                    barrier.SignalAndWait();
                    try
                    {
                        for (var i = worker; i < ids.Length; i += threads)
                        {
                            using var t = dbe.CreateQuickTransaction();
                            var entity = t.OpenMut(ids[i]);
                            if ((round & 1) == 0)
                            {
                                entity.Disable(EcsUnit.Velocity);
                            }
                            else
                            {
                                entity.Enable(EcsUnit.Velocity);
                            }

                            t.Commit();
                        }
                    }
                    catch (Exception ex)
                    {
                        Interlocked.CompareExchange(ref failure, ex, null);
                    }

                    barrier.SignalAndWait();
                }
            }) { IsBackground = true };
            workers[w].Start();
        }

        var mismatches = 0;
        for (var round = 0; round < rounds; round++)
        {
            barrier.SignalAndWait();
            barrier.SignalAndWait();

            var expected = (round & 1) != 0;
            foreach (var id in ids)
            {
                if (ClusterSoAProbe.IsEnabled(dbe, meta.ArchetypeId, id, velSlot) != expected)
                {
                    mismatches++;
                }
            }
        }

        foreach (var worker in workers)
        {
            worker.Join();
        }

        Assert.That(failure, Is.Null, $"a worker's transaction failed — {failure}");
        Assert.That(mismatches, Is.Zero, "a committed Enable/Disable was lost from the cluster copy: another entity's commit overwrote its bit");
    }

    /// <summary>
    /// A change committed against an entity another transaction destroyed meanwhile must not reach the slot's NEXT occupant (#998 review).
    /// </summary>
    /// <remarks>
    /// The commit-time publish reads the entity's record and writes the cluster slot it names. When a later transaction has destroyed the entity and committed
    /// first, that record is a tombstone and its slot has been released at commit — and a spawn may already hold it. Writing then stamps the dead entity's mask
    /// onto a different live entity. Single-threaded and deterministic: the transactions are interleaved by hand.
    /// </remarks>
    [Test]
    [VerifiesRule("ENABLE-01")]
    public void ACommitAgainstADestroyedEntity_LeavesTheSlotsNextOccupantAlone()
    {
        using var dbe = SetupEngine();
        var meta = Archetype<EcsUnit>.Metadata;
        var posSlot = meta.GetSlot(EcsUnit.Position._componentTypeId);
        var velSlot = meta.GetSlot(EcsUnit.Velocity._componentTypeId);

        // Neighbours keep the cluster alive after the destroy, so the released slot is reused rather than the whole cluster freed.
        var neighbour = SpawnUnit(dbe);
        var victim = SpawnUnit(dbe);
        SpawnUnit(dbe);
        var victimSlot = ClusterSoAProbe.Locate(dbe, meta.ArchetypeId, victim);

        using var stale = dbe.CreateQuickTransaction();
        stale.OpenMut(victim).Disable(EcsUnit.Velocity);

        using (var t = dbe.CreateQuickTransaction())
        {
            t.Destroy(victim);
            t.Commit();
        }

        EntityId occupant;
        using (var t = dbe.CreateQuickTransaction())
        {
            var pos = new EcsPosition(7, 7, 7);
            var vel = new EcsVelocity(8, 8, 8);
            occupant = t.Spawn<EcsUnit>(EcsUnit.Position.Set(in pos), EcsUnit.Velocity.Set(in vel));
            t.Commit();
        }

        Assert.That(ClusterSoAProbe.Locate(dbe, meta.ArchetypeId, occupant), Is.EqualTo(victimSlot),
            "premise: the new entity must reuse the destroyed entity's slot, or nothing here can be overwritten");

        stale.Commit();

        // Premise: the stale commit reached the publish — it rewrote the tombstone's record, which is the step just before the cluster write. Without this,
        // a record already reaped would skip the publish entirely and the assertions below would hold for nothing.
        Assert.That(ClusterSoAProbe.RecordEnabledBits(dbe, meta.ArchetypeId, victim) & (1 << velSlot), Is.Zero,
            "premise: the stale commit must have published its Disable to the tombstone's record");

        Assert.Multiple(() =>
        {
            Assert.That(ClusterSoAProbe.IsEnabled(dbe, meta.ArchetypeId, occupant, velSlot), Is.True,
                "the stale commit wrote the destroyed entity's Disable into the slot's new occupant");
            Assert.That(ClusterSoAProbe.IsEnabled(dbe, meta.ArchetypeId, occupant, posSlot), Is.True);
            Assert.That(ClusterSoAProbe.IsEnabled(dbe, meta.ArchetypeId, neighbour, velSlot), Is.True);
        });
    }

    /// <summary>
    /// A bit left on a freed slot does not reach the slot's next occupant: every claim writes the occupant's full mask (#998 review).
    /// </summary>
    /// <remarks>
    /// A freed slot is not guaranteed clean. <c>ClearSlotMetadata</c> clears a slot's enabled bits before its occupancy bit, so a publish that passes its
    /// occupancy check in between can put a bit back on the slot being freed. The bit is invisible while the slot is empty; the danger is the next claim, which
    /// used to OR the new occupant's bits in and never clear — enabling, for the new entity, a component it may never have supplied. The stray bit is placed by
    /// hand here, because the interleaving that leaves it cannot be forced from a test.
    /// </remarks>
    [Test]
    [VerifiesRule("ENABLE-01")]
    public void AStrayBitOnAFreedSlot_DoesNotReachItsNextOccupant()
    {
        using var dbe = SetupEngine();
        var meta = Archetype<EcsUnit>.Metadata;
        var posSlot = meta.GetSlot(EcsUnit.Position._componentTypeId);
        var velSlot = meta.GetSlot(EcsUnit.Velocity._componentTypeId);

        SpawnUnit(dbe);
        var victim = SpawnUnit(dbe);
        SpawnUnit(dbe);
        var freed = ClusterSoAProbe.Locate(dbe, meta.ArchetypeId, victim);

        using (var t = dbe.CreateQuickTransaction())
        {
            t.Destroy(victim);
            t.Commit();
        }

        ClusterSoAProbe.SetBitAt(dbe, meta.ArchetypeId, freed, velSlot, true);

        // The new occupant never supplies Velocity: an inherited bit would enable a component it does not have.
        EntityId occupant;
        using (var t = dbe.CreateQuickTransaction())
        {
            var pos = new EcsPosition(7, 7, 7);
            occupant = t.Spawn<EcsUnit>(EcsUnit.Position.Set(in pos));
            t.Commit();
        }

        Assert.That(ClusterSoAProbe.Locate(dbe, meta.ArchetypeId, occupant), Is.EqualTo(freed),
            "premise: the new entity must reuse the freed slot that carries the stray bit");
        Assert.Multiple(() =>
        {
            Assert.That(ClusterSoAProbe.IsEnabled(dbe, meta.ArchetypeId, occupant, velSlot), Is.False,
                "the new occupant inherited the previous occupant's Velocity bit — a component it never supplied");
            Assert.That(ClusterSoAProbe.IsEnabled(dbe, meta.ArchetypeId, occupant, posSlot), Is.True);
        });
    }

    /// <summary>
    /// A pure-Transient archetype keeps its cluster metadata in the TransientStore segment; the commit publishes there too (#998).
    /// </summary>
    [Test]
    [VerifiesRule("ENABLE-01")]
    public void PureTransientArchetype_CommitPublishesTheClusterBit()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<EdTrA>();
        dbe.RegisterComponentFromAccessor<EdTrB>();
        dbe.InitializeArchetypes();

        var meta = Archetype<EdTrArch>.Metadata;
        var aSlot = meta.GetSlot(EdTrArch.A._componentTypeId);
        var bSlot = meta.GetSlot(EdTrArch.B._componentTypeId);
        Assert.That(dbe._archetypeStates[meta.ArchetypeId].ClusterState.ClusterSegment, Is.Null,
            "premise: a pure-Transient archetype has no PersistentStore cluster segment");

        EntityId id;
        using (var t = dbe.CreateQuickTransaction())
        {
            id = t.Spawn<EdTrArch>(EdTrArch.A.Set(new EdTrA(1)), EdTrArch.B.Set(new EdTrB(2)));
            t.Commit();
        }

        using (var t = dbe.CreateQuickTransaction())
        {
            t.OpenMut(id).Disable(EdTrArch.B);
            Assert.That(ClusterSoAProbe.IsEnabled(dbe, meta.ArchetypeId, id, bSlot), Is.True, "a staged Disable must not reach the TransientStore copy");
            t.Commit();
        }

        Assert.Multiple(() =>
        {
            Assert.That(ClusterSoAProbe.IsEnabled(dbe, meta.ArchetypeId, id, bSlot), Is.False,
                "the commit must publish the Disable to the TransientStore copy");
            Assert.That(ClusterSoAProbe.IsEnabled(dbe, meta.ArchetypeId, id, aSlot), Is.True);
        });
    }
}

[Component("Typhon.Test.EnableDisable.TrA", 1, StorageMode = StorageMode.Transient)]
[StructLayout(LayoutKind.Sequential)]
struct EdTrA
{
    public int V;
    public EdTrA(int v) { V = v; }
}

[Component("Typhon.Test.EnableDisable.TrB", 1, StorageMode = StorageMode.Transient)]
[StructLayout(LayoutKind.Sequential)]
struct EdTrB
{
    public int V;
    public EdTrB(int v) { V = v; }
}

[Archetype]
class EdTrArch : Archetype<EdTrArch>
{
    public static readonly Comp<EdTrA> A = Register<EdTrA>();
    public static readonly Comp<EdTrB> B = Register<EdTrB>();
}
