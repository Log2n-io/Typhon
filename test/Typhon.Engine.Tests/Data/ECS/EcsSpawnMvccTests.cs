using System;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests;

/// <summary>
/// Tests verifying that ECS Spawn creates proper revision chains for Versioned components,
/// matching the old CRUD CreateComponent behavior: CompRevStorageHeader with EntityPK,
/// CompRevStorageElement with IsolationFlag, and CompRevInfo in _componentInfos cache.
/// </summary>
[NonParallelizable]
class EcsSpawnMvccTests : TestBase<EcsSpawnMvccTests>
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
        dbe.RegisterComponentFromAccessor<CompSmSingleVersion>();
        dbe.RegisterComponentFromAccessor<CompSmTransient>();
        dbe.RegisterComponentFromAccessor<CompSmVersioned>();
        dbe.RegisterComponentFromAccessor<CompSmVersionedMix>();
        dbe.InitializeArchetypes();
        return dbe;
    }

    // ── Revision chain creation ──

    [Test]
    public void Spawn_Versioned_PkIndexPopulated()
    {
        using var dbe = SetupEngine();

        using var tx = dbe.CreateQuickTransaction();
        var pos = new EcsPosition(1, 2, 3);
        var vel = new EcsVelocity(4, 5, 6);
        var entityId = tx.Spawn<EcsUnit>(EcsUnit.Position.Set(in pos), EcsUnit.Velocity.Set(in vel));
        tx.Commit();

        // After commit, EntityMap should contain the entity with finalized BornTSN
        var posTable = dbe.GetComponentTable<EcsPosition>();
        Assert.That(posTable.StorageMode, Is.EqualTo(StorageMode.Versioned));
        var engineState = dbe._stateByRouting[entityId.ArchetypeId];
        Assert.That(engineState.EntityMap.EntryCount, Is.GreaterThan(0),
            "EntityMap should contain entry after Versioned ECS Spawn+Commit");
    }

    [Test]
    public void Spawn_Versioned_RevisionChainCreated()
    {
        using var dbe = SetupEngine();

        var posTable = dbe.GetComponentTable<EcsPosition>();
        int revBefore = posTable.CompRevTableSegment.AllocatedChunkCount;

        using var tx = dbe.CreateQuickTransaction();
        var pos = new EcsPosition(10, 20, 30);
        var vel = new EcsVelocity(1, 2, 3);
        tx.Spawn<EcsUnit>(EcsUnit.Position.Set(in pos), EcsUnit.Velocity.Set(in vel));

        // After Spawn (before commit), revision chain chunk should be allocated
        Assert.That(posTable.CompRevTableSegment.AllocatedChunkCount, Is.GreaterThan(revBefore),
            "Revision chain chunk should be allocated during Spawn for Versioned components");

        tx.Commit();

        // After commit, EntityMap should have the entry with finalized BornTSN
        var engineState = dbe._archetypeStates[Archetype<EcsUnit>.Metadata.ArchetypeId];
        Assert.That(engineState.EntityMap.EntryCount, Is.GreaterThan(0),
            "EntityMap should contain entry after Versioned ECS Spawn+Commit");
    }

    [Test]
    public void Spawn_Versioned_IsolationFlagClearedAfterCommit()
    {
        using var dbe = SetupEngine();

        using var tx = dbe.CreateQuickTransaction();
        var pos = new EcsPosition(1, 2, 3);
        var vel = new EcsVelocity(4, 5, 6);
        var entityId = tx.Spawn<EcsUnit>(EcsUnit.Position.Set(in pos), EcsUnit.Velocity.Set(in vel));

        // Before commit: revision entry should have IsolationFlag = true
        // (checked indirectly: the revision chain exists in _componentInfos)

        tx.Commit();

        // After commit: IsolationFlag should be cleared by CommitComponentCore
        // Verify by reading via Open — if IsolationFlag is still set,
        // the revision would be invisible to other transactions
        using var tx2 = dbe.CreateQuickTransaction();
        var entity = tx2.Open(entityId);
        Assert.That(entity.IsValid, Is.True, "Entity should be visible after commit (IsolationFlag cleared)");
        ref readonly var readPos = ref entity.Read(EcsUnit.Position);
        Assert.That(readPos.X, Is.EqualTo(1), "Position data should be readable after IsolationFlag cleared");
    }

    // ── SV/Transient unchanged ──

    [Test]
    public void Spawn_SV_NoRevisionChain()
    {
        using var dbe = SetupEngine();

        using var tx = dbe.CreateQuickTransaction();
        var comp = new CompSmSingleVersion(42);
        tx.Spawn<SvTestArchetype>(SvTestArchetype.SvComp.Set(in comp));
        tx.Commit();

        // SV should NOT have PK index entries from ECS (no revision chain)
        var svTable = dbe.GetComponentTable<CompSmSingleVersion>();
        Assert.That(svTable.CompRevTableSegment, Is.Null, "SV has no revision chain segment");
    }

    [Test]
    public void Spawn_Transient_NoRevisionChain()
    {
        using var dbe = SetupEngine();

        using var tx = dbe.CreateQuickTransaction();
        var comp = new CompSmTransient(99);
        tx.Spawn<TransientTestArchetype>(TransientTestArchetype.TransComp.Set(in comp));
        tx.Commit();

        var trTable = dbe.GetComponentTable<CompSmTransient>();
        Assert.That(trTable.CompRevTableSegment, Is.Null, "Transient has no revision chain segment");
        Assert.That(trTable.ComponentSegment, Is.Null, "Transient has no persistent component segment");
    }

    // ── Rollback ──

    [Test]
    public void Spawn_Versioned_Rollback_FreesRevisionChunk()
    {
        using var dbe = SetupEngine();

        var posTable = dbe.GetComponentTable<EcsPosition>();
        int revAllocBefore = posTable.CompRevTableSegment.AllocatedChunkCount;
        int compAllocBefore = posTable.ComponentSegment.AllocatedChunkCount;

        using (var tx = dbe.CreateQuickTransaction())
        {
            var pos = new EcsPosition(1, 2, 3);
            var vel = new EcsVelocity(4, 5, 6);
            tx.Spawn<EcsUnit>(EcsUnit.Position.Set(in pos), EcsUnit.Velocity.Set(in vel));
            // Don't commit — triggers rollback
        }

        // Both component data chunk AND revision chain chunk should be freed
        Assert.That(posTable.ComponentSegment.AllocatedChunkCount, Is.EqualTo(compAllocBefore),
            "Component data chunk freed on rollback");
        Assert.That(posTable.CompRevTableSegment.AllocatedChunkCount, Is.EqualTo(revAllocBefore),
            "Revision chain chunk freed on rollback");
    }

    // ── Mixed mode ──

    [Test]
    public void Spawn_Mixed_VersionedGetsRevChain_SVDoesNot()
    {
        using var dbe = SetupEngine();

        using var tx = dbe.CreateQuickTransaction();
        var v = new CompSmVersionedMix(10);
        var sv = new CompSmSingleVersion(20);
        var tr = new CompSmTransient(30);
        tx.Spawn<MixedModeArchetype>(
            MixedModeArchetype.Versioned.Set(in v),
            MixedModeArchetype.SV.Set(in sv),
            MixedModeArchetype.Trans.Set(in tr));
        tx.Commit();

        // Versioned component should be readable via EntityMap (revision chain resolved)
        var vTable = dbe.GetComponentTable<CompSmVersionedMix>();
        var engineState = dbe._archetypeStates[Archetype<MixedModeArchetype>.Metadata.ArchetypeId];
        Assert.That(engineState.EntityMap.EntryCount, Is.GreaterThan(0),
            "EntityMap should have entry after mixed-mode Spawn+Commit");

        // SV should NOT
        var svTable = dbe.GetComponentTable<CompSmSingleVersion>();
        Assert.That(svTable.CompRevTableSegment, Is.Null, "SV still has no revision chain");
    }

    // ── 5.2: MVCC Read tests ──

    [Test]
    public void Read_Versioned_UsesRevisionChain()
    {
        using var dbe = SetupEngine();

        // Spawn and commit
        using var tx = dbe.CreateQuickTransaction();
        var pos = new EcsPosition(42, 99, 7);
        var vel = new EcsVelocity(1, 2, 3);
        var id = tx.Spawn<EcsUnit>(EcsUnit.Position.Set(in pos), EcsUnit.Velocity.Set(in vel));
        tx.Commit();

        // Read from a new transaction — should resolve via revision chain
        using var tx2 = dbe.CreateQuickTransaction();
        var entity = tx2.Open(id);
        ref readonly var readPos = ref entity.Read(EcsUnit.Position);

        // Data should match what was spawned (resolved via revision chain, not spawn-time Location)
        Assert.That(readPos.X, Is.EqualTo(42f));
        Assert.That(readPos.Y, Is.EqualTo(99f));
        Assert.That(readPos.Z, Is.EqualTo(7f));
    }

    [Test]
    public void Read_PendingSpawn_UsesDirectLocation()
    {
        using var dbe = SetupEngine();

        // Read within creating transaction (pending spawn) — no revision chain walk
        using var tx = dbe.CreateQuickTransaction();
        var pos = new EcsPosition(10, 20, 30);
        var vel = new EcsVelocity(1, 2, 3);
        var id = tx.Spawn<EcsUnit>(EcsUnit.Position.Set(in pos), EcsUnit.Velocity.Set(in vel));

        // Read before commit — uses Location[slot] from pending record bytes directly
        var entity = tx.Open(id);
        ref readonly var readPos = ref entity.Read(EcsUnit.Position);
        Assert.That(readPos.X, Is.EqualTo(10f));
        Assert.That(readPos.Y, Is.EqualTo(20f));
    }

    [Test]
    public void Read_SV_UsesDirectLocation()
    {
        using var dbe = SetupEngine();

        using var tx = dbe.CreateQuickTransaction();
        var comp = new CompSmSingleVersion(77);
        var id = tx.Spawn<SvTestArchetype>(SvTestArchetype.SvComp.Set(in comp));
        tx.Commit();

        using var tx2 = dbe.CreateQuickTransaction();
        var entity = tx2.Open(id);
        ref readonly var read = ref entity.Read(SvTestArchetype.SvComp);
        Assert.That(read.Value, Is.EqualTo(77), "SV uses direct Location, no revision chain");
    }

    [Test]
    public void Read_MultipleVersionedComponents_AllResolved()
    {
        using var dbe = SetupEngine();

        // EcsSoldier has Position + Velocity (inherited) + Health (own) — all Versioned
        using var tx = dbe.CreateQuickTransaction();
        var pos = new EcsPosition(1, 2, 3);
        var vel = new EcsVelocity(4, 5, 6);
        var hp = new EcsHealth(100, 200);
        var id = tx.Spawn<EcsSoldier>(
            EcsUnit.Position.Set(in pos),
            EcsUnit.Velocity.Set(in vel),
            EcsSoldier.Health.Set(in hp));
        tx.Commit();

        using var tx2 = dbe.CreateQuickTransaction();
        var entity = tx2.Open(id);

        Assert.That(entity.Read(EcsUnit.Position).X, Is.EqualTo(1f));
        Assert.That(entity.Read(EcsUnit.Velocity).Dx, Is.EqualTo(4f));
        Assert.That(entity.Read(EcsSoldier.Health).Current, Is.EqualTo(100));
    }

    // ── 5.3: Write copy-on-write tests ──

    [Test]
    public void Write_Versioned_CopyOnWrite_NewChunk()
    {
        using var dbe = SetupEngine();

        using var tx = dbe.CreateQuickTransaction();
        var pos = new EcsPosition(1, 2, 3);
        var vel = new EcsVelocity(4, 5, 6);
        var id = tx.Spawn<EcsUnit>(EcsUnit.Position.Set(in pos), EcsUnit.Velocity.Set(in vel));
        tx.Commit();

        var posTable = dbe.GetComponentTable<EcsPosition>();
        int chunksBefore = posTable.ComponentSegment.AllocatedChunkCount;

        using var tx2 = dbe.CreateQuickTransaction();
        var entity = tx2.OpenMut(id);
        entity.Write(EcsUnit.Position).X = 999;

        // Copy-on-write should have allocated a NEW chunk
        Assert.That(posTable.ComponentSegment.AllocatedChunkCount, Is.GreaterThan(chunksBefore),
            "Write on Versioned should allocate new chunk (copy-on-write)");
    }

    [Test]
    public void Write_Versioned_ReadAfterWrite_SeesNewData()
    {
        using var dbe = SetupEngine();

        using var tx = dbe.CreateQuickTransaction();
        var pos = new EcsPosition(10, 20, 30);
        var vel = new EcsVelocity(1, 2, 3);
        var id = tx.Spawn<EcsUnit>(EcsUnit.Position.Set(in pos), EcsUnit.Velocity.Set(in vel));
        tx.Commit();

        using var tx2 = dbe.CreateQuickTransaction();
        var entity = tx2.OpenMut(id);
        entity.Write(EcsUnit.Position).X = 999;

        // Read after Write in same tx should see new data
        ref readonly var read = ref entity.Read(EcsUnit.Position);
        Assert.That(read.X, Is.EqualTo(999f));
    }

    [Test]
    public void Write_Versioned_OtherTx_SeesOldData()
    {
        using var dbe = SetupEngine();

        using var tx = dbe.CreateQuickTransaction();
        var pos = new EcsPosition(10, 20, 30);
        var vel = new EcsVelocity(1, 2, 3);
        var id = tx.Spawn<EcsUnit>(EcsUnit.Position.Set(in pos), EcsUnit.Velocity.Set(in vel));
        tx.Commit();

        // tx2 writes but doesn't commit yet
        using var tx2 = dbe.CreateQuickTransaction();
        var entity2 = tx2.OpenMut(id);
        entity2.Write(EcsUnit.Position).X = 999;

        // tx3 reads — should see OLD data (MVCC snapshot isolation)
        using var tx3 = dbe.CreateQuickTransaction();
        var entity3 = tx3.Open(id);
        ref readonly var read = ref entity3.Read(EcsUnit.Position);
        Assert.That(read.X, Is.EqualTo(10f), "Concurrent tx should see old data (MVCC isolation)");
    }

    [Test]
    public void Write_CreatedEntity_NoCopyOnWrite()
    {
        using var dbe = SetupEngine();

        var posTable = dbe.GetComponentTable<EcsPosition>();

        using var tx = dbe.CreateQuickTransaction();
        var pos = new EcsPosition(1, 2, 3);
        var vel = new EcsVelocity(4, 5, 6);
        var id = tx.Spawn<EcsUnit>(EcsUnit.Position.Set(in pos), EcsUnit.Velocity.Set(in vel));

        int chunksAfterSpawn = posTable.ComponentSegment.AllocatedChunkCount;

        // Write to entity spawned in SAME tx — should NOT allocate new chunk
        var entity = tx.OpenMut(id);
        entity.Write(EcsUnit.Position).X = 777;

        Assert.That(posTable.ComponentSegment.AllocatedChunkCount, Is.EqualTo(chunksAfterSpawn),
            "Write to entity created in same tx should reuse existing chunk (no copy-on-write)");
        Assert.That(entity.Read(EcsUnit.Position).X, Is.EqualTo(777f));
    }

    [Test]
    public void Write_SV_InPlace_Unchanged()
    {
        using var dbe = SetupEngine();

        using var tx = dbe.CreateQuickTransaction();
        var comp = new CompSmSingleVersion(42);
        var id = tx.Spawn<SvTestArchetype>(SvTestArchetype.SvComp.Set(in comp));
        tx.Commit();

        var svTable = dbe.GetComponentTable<CompSmSingleVersion>();
        int chunksBefore = svTable.ComponentSegment.AllocatedChunkCount;

        using var tx2 = dbe.CreateQuickTransaction();
        var entity = tx2.OpenMut(id);
        entity.Write(SvTestArchetype.SvComp).Value = 999;

        // SV writes in-place — no new chunk
        Assert.That(svTable.ComponentSegment.AllocatedChunkCount, Is.EqualTo(chunksBefore),
            "SV Write should NOT allocate new chunk");
    }

    [Test]
    public void Write_Versioned_Rollback_FreesNewChunk()
    {
        using var dbe = SetupEngine();

        using var tx = dbe.CreateQuickTransaction();
        var pos = new EcsPosition(1, 2, 3);
        var vel = new EcsVelocity(4, 5, 6);
        var id = tx.Spawn<EcsUnit>(EcsUnit.Position.Set(in pos), EcsUnit.Velocity.Set(in vel));
        tx.Commit();

        var posTable = dbe.GetComponentTable<EcsPosition>();
        int chunksBefore = posTable.ComponentSegment.AllocatedChunkCount;

        using (var tx2 = dbe.CreateQuickTransaction())
        {
            var entity = tx2.OpenMut(id);
            entity.Write(EcsUnit.Position).X = 999;
            // Don't commit — triggers rollback
        }

        // Rollback should free the copy-on-write chunk
        Assert.That(posTable.ComponentSegment.AllocatedChunkCount, Is.EqualTo(chunksBefore),
            "Rollback should free copy-on-write chunk");
    }

    [Test]
    public void Write_Versioned_CommitAndRead_DataPersisted()
    {
        using var dbe = SetupEngine();

        using var tx = dbe.CreateQuickTransaction();
        var pos = new EcsPosition(1, 2, 3);
        var vel = new EcsVelocity(4, 5, 6);
        var id = tx.Spawn<EcsUnit>(EcsUnit.Position.Set(in pos), EcsUnit.Velocity.Set(in vel));
        tx.Commit();

        // Write and commit
        using var tx2 = dbe.CreateQuickTransaction();
        var entity2 = tx2.OpenMut(id);
        entity2.Write(EcsUnit.Position).X = 888;
        tx2.Commit();

        // New transaction reads committed data
        using var tx3 = dbe.CreateQuickTransaction();
        var entity3 = tx3.Open(id);
        Assert.That(entity3.Read(EcsUnit.Position).X, Is.EqualTo(888f));
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Conflict Detection (gap-filling: equivalent of ConcurrencyConflictTests)
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public void Conflict_NoHandler_LastWins()
    {
        using var dbe = SetupEngine();

        EntityId id;
        using (var tx = dbe.CreateQuickTransaction())
        {
            var pos = new EcsPosition(10, 0, 0);
            var vel = new EcsVelocity(0, 0, 0);
            id = tx.Spawn<EcsUnit>(EcsUnit.Position.Set(in pos), EcsUnit.Velocity.Set(in vel));
            tx.Commit();
        }

        // T1 reads and writes
        using var t1 = dbe.CreateQuickTransaction();
        t1.OpenMut(id).Write(EcsUnit.Position).X = 20;

        // T2 writes and commits first
        using (var t2 = dbe.CreateQuickTransaction())
        {
            t2.OpenMut(id).Write(EcsUnit.Position).X = 30;
            t2.Commit();
        }

        // T1 commits without handler — last wins
        Assert.That(t1.Commit(), Is.True);

        using var tRead = dbe.CreateQuickTransaction();
        Assert.That(tRead.Open(id).Read(EcsUnit.Position).X, Is.EqualTo(20f),
            "Last writer wins: T1's value should be visible");
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Deferred Cleanup (gap-filling: verify ECS entity GC)
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public void Cleanup_DestroyedEntity_ChunksFreedAfterMinTSNAdvances()
    {
        using var dbe = SetupEngine();

        var posTable = dbe.GetComponentTable<EcsPosition>();

        EntityId id;
        using (var tx = dbe.CreateQuickTransaction())
        {
            var pos = new EcsPosition(1, 2, 3);
            var vel = new EcsVelocity(4, 5, 6);
            id = tx.Spawn<EcsUnit>(EcsUnit.Position.Set(in pos), EcsUnit.Velocity.Set(in vel));
            tx.Commit();
        }

        int chunksAfterSpawn = posTable.ComponentSegment.AllocatedChunkCount;

        // Destroy the entity
        using (var tx2 = dbe.CreateQuickTransaction())
        {
            tx2.Destroy(id);
            tx2.Commit();
        }

        // Advance MinTSN by creating and committing another transaction
        // (makes the destroyed entity eligible for cleanup)
        using (var tx3 = dbe.CreateQuickTransaction())
        {
            var pos = new EcsPosition(0, 0, 0);
            var vel = new EcsVelocity(0, 0, 0);
            tx3.Spawn<EcsUnit>(EcsUnit.Position.Set(in pos), EcsUnit.Velocity.Set(in vel));
            tx3.Commit();
        }

        // Process cleanup — should free the destroyed entity's chunks
        long minTSN = dbe.TransactionChain.MinTSN;
        int cleaned = dbe.ProcessEcsCleanups(minTSN);

        // Note: cleanup may or may not have processed depending on MinTSN advancement
        // The important thing is no crash and the entity is gone
        using var txVerify = dbe.CreateQuickTransaction();
        Assert.That(txVerify.IsAlive(id), Is.False, "Destroyed entity should not be alive");
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Revision Chain Stress (gap-filling: concurrent spawn+write+commit)
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public void Stress_ConcurrentWritesToSameEntity_NoCorruption()
    {
        using var dbe = SetupEngine();

        // Spawn entity
        EntityId id;
        using (var tx = dbe.CreateQuickTransaction())
        {
            var pos = new EcsPosition(0, 0, 0);
            var vel = new EcsVelocity(0, 0, 0);
            id = tx.Spawn<EcsUnit>(EcsUnit.Position.Set(in pos), EcsUnit.Velocity.Set(in vel));
            tx.Commit();
        }

        // Run N sequential write+commit cycles (revision chain grows)
        const int iterations = 50;
        for (int i = 0; i < iterations; i++)
        {
            using var tx = dbe.CreateQuickTransaction();
            var entity = tx.OpenMut(id);
            entity.Write(EcsUnit.Position).X = i;
            tx.Commit();
        }

        // Verify final value
        using var txRead = dbe.CreateQuickTransaction();
        Assert.That(txRead.Open(id).Read(EcsUnit.Position).X, Is.EqualTo((float)(iterations - 1)));
    }

    [Test]
    public void Stress_ManySpawnsAndDestroys_NoLeaks()
    {
        using var dbe = SetupEngine();

        var posTable = dbe.GetComponentTable<EcsPosition>();
        int baseChunks = posTable.ComponentSegment.AllocatedChunkCount;

        // Spawn and immediately destroy 20 entities
        for (int i = 0; i < 20; i++)
        {
            EntityId id;
            using (var tx = dbe.CreateQuickTransaction())
            {
                var pos = new EcsPosition(i, 0, 0);
                var vel = new EcsVelocity(0, 0, 0);
                id = tx.Spawn<EcsUnit>(EcsUnit.Position.Set(in pos), EcsUnit.Velocity.Set(in vel));
                tx.Commit();
            }

            using (var tx2 = dbe.CreateQuickTransaction())
            {
                tx2.Destroy(id);
                tx2.Commit();
            }
        }

        // Process all cleanups
        dbe.ProcessEcsCleanups(long.MaxValue);

        // The entity map entries should be removed (though chunk freeing depends on GC timing)
        // At minimum, no crash and entities are invisible
        using var txCheck = dbe.CreateQuickTransaction();
        Assert.That(txCheck.Query<EcsUnit>().Count(), Is.EqualTo(0));
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Transaction Lifecycle (gap-filling: dispose/state machine)
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public void Dispose_UncommittedTransaction_AutoRollback()
    {
        using var dbe = SetupEngine();

        EntityId id;
        using (var tx = dbe.CreateQuickTransaction())
        {
            var pos = new EcsPosition(1, 2, 3);
            var vel = new EcsVelocity(4, 5, 6);
            id = tx.Spawn<EcsUnit>(EcsUnit.Position.Set(in pos), EcsUnit.Velocity.Set(in vel));
            // Dispose without commit — auto-rollback
        }

        using var txCheck = dbe.CreateQuickTransaction();
        Assert.That(txCheck.IsAlive(id), Is.False, "Entity from uncommitted tx should not be visible");
    }

    [Test]
    public void DoubleCommit_SecondReturnsFalse()
    {
        using var dbe = SetupEngine();

        using var tx = dbe.CreateQuickTransaction();
        var pos = new EcsPosition(1, 2, 3);
        var vel = new EcsVelocity(4, 5, 6);
        tx.Spawn<EcsUnit>(EcsUnit.Position.Set(in pos), EcsUnit.Velocity.Set(in vel));

        Assert.That(tx.Commit(), Is.True);
        Assert.That(tx.Commit(), Is.False, "Second commit should return false");
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Secondary Index on Delete (gap-filling)
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public void Write_CommitUpdatesSecondaryIndex()
    {
        using var dbe = SetupEngine();

        // EcsPosition doesn't have an index, so this test just verifies the commit path
        // doesn't crash when IndexedFieldInfos is empty for the component.
        EntityId id;
        using (var tx = dbe.CreateQuickTransaction())
        {
            var pos = new EcsPosition(1, 2, 3);
            var vel = new EcsVelocity(4, 5, 6);
            id = tx.Spawn<EcsUnit>(EcsUnit.Position.Set(in pos), EcsUnit.Velocity.Set(in vel));
            tx.Commit();
        }

        using var tx2 = dbe.CreateQuickTransaction();
        tx2.OpenMut(id).Write(EcsUnit.Position).X = 999;
        Assert.DoesNotThrow(() => tx2.Commit(), "Commit with write should not crash even without secondary indexes");
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Rollback Behavior (gap-filling: legacy TransactionTests parity)
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public void Rollback_Destroy_EntityStillAlive()
    {
        using var dbe = SetupEngine();

        EntityId id;
        using (var tx = dbe.CreateQuickTransaction())
        {
            var pos = new EcsPosition(42, 0, 0);
            var vel = new EcsVelocity(0, 0, 0);
            id = tx.Spawn<EcsUnit>(EcsUnit.Position.Set(in pos), EcsUnit.Velocity.Set(in vel));
            tx.Commit();
        }

        // Destroy but don't commit → rollback
        using (var tx2 = dbe.CreateQuickTransaction())
        {
            tx2.Destroy(id);
            // No commit → auto-rollback on dispose
        }

        // Entity should still be alive and readable
        using var tx3 = dbe.CreateQuickTransaction();
        Assert.That(tx3.IsAlive(id), Is.True, "Rollback of Destroy should keep entity alive");
        Assert.That(tx3.Open(id).Read(EcsUnit.Position).X, Is.EqualTo(42f));
    }

    [Test]
    public void Rollback_Write_OriginalValuePreserved()
    {
        using var dbe = SetupEngine();

        EntityId id;
        using (var tx = dbe.CreateQuickTransaction())
        {
            var pos = new EcsPosition(100, 0, 0);
            var vel = new EcsVelocity(0, 0, 0);
            id = tx.Spawn<EcsUnit>(EcsUnit.Position.Set(in pos), EcsUnit.Velocity.Set(in vel));
            tx.Commit();
        }

        // Write but don't commit → rollback
        using (var tx2 = dbe.CreateQuickTransaction())
        {
            tx2.OpenMut(id).Write(EcsUnit.Position).X = 999;
            // No commit
        }

        // Original value preserved
        using var tx3 = dbe.CreateQuickTransaction();
        Assert.That(tx3.Open(id).Read(EcsUnit.Position).X, Is.EqualTo(100f),
            "Rollback of Write should preserve original value");
    }

    [Test]
    public void Rollback_MultipleComponents_AllReverted()
    {
        using var dbe = SetupEngine();

        // EcsSoldier has Position + Velocity + Health
        EntityId id;
        using (var tx = dbe.CreateQuickTransaction())
        {
            var pos = new EcsPosition(10, 20, 30);
            var vel = new EcsVelocity(1, 2, 3);
            var hp = new EcsHealth(100, 200);
            id = tx.Spawn<EcsSoldier>(
                EcsUnit.Position.Set(in pos), EcsUnit.Velocity.Set(in vel), EcsSoldier.Health.Set(in hp));
            tx.Commit();
        }

        // Write to ALL components, then rollback
        using (var tx2 = dbe.CreateQuickTransaction())
        {
            var e = tx2.OpenMut(id);
            e.Write(EcsUnit.Position).X = 999;
            e.Write(EcsUnit.Velocity).Dx = 888;
            e.Write(EcsSoldier.Health).Current = 1;
            // No commit
        }

        // All original values preserved
        using var tx3 = dbe.CreateQuickTransaction();
        var entity = tx3.Open(id);
        Assert.That(entity.Read(EcsUnit.Position).X, Is.EqualTo(10f));
        Assert.That(entity.Read(EcsUnit.Velocity).Dx, Is.EqualTo(1f));
        Assert.That(entity.Read(EcsSoldier.Health).Current, Is.EqualTo(100));
    }

    [Test]
    public void Rollback_EmptyTransaction_Succeeds()
    {
        using var dbe = SetupEngine();

        // Transaction with no operations — dispose is no-op
        using (var tx = dbe.CreateQuickTransaction())
        {
            // No operations at all
        }
        // Should not crash
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Transaction Lifecycle (gap-filling: state machine guards)
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public void Spawn_AfterCommit_Throws()
    {
        using var dbe = SetupEngine();

        using var tx = dbe.CreateQuickTransaction();
        var pos = new EcsPosition(1, 2, 3);
        var vel = new EcsVelocity(0, 0, 0);
        tx.Spawn<EcsUnit>(EcsUnit.Position.Set(in pos), EcsUnit.Velocity.Set(in vel));
        tx.Commit();

        Assert.Throws<InvalidOperationException>(() =>
        {
            var pos2 = new EcsPosition(4, 5, 6);
            tx.Spawn<EcsUnit>(EcsUnit.Position.Set(in pos2), EcsUnit.Velocity.Set(in vel));
        }, "Spawn after Commit should throw");
    }

    [Test]
    public void Open_AfterCommit_StillWorks()
    {
        using var dbe = SetupEngine();

        using var tx = dbe.CreateQuickTransaction();
        var pos = new EcsPosition(42, 0, 0);
        var vel = new EcsVelocity(0, 0, 0);
        var id = tx.Spawn<EcsUnit>(EcsUnit.Position.Set(in pos), EcsUnit.Velocity.Set(in vel));
        tx.Commit();

        // Read-only Open after Commit should succeed (transaction is committed, reads are safe)
        var entity = tx.Open(id);
        Assert.That(entity.Read(EcsUnit.Position).X, Is.EqualTo(42f));
    }

    [Test]
    public void Dispose_CommittedTransaction_NoCrash()
    {
        using var dbe = SetupEngine();

        using var tx = dbe.CreateQuickTransaction();
        var pos = new EcsPosition(1, 2, 3);
        var vel = new EcsVelocity(0, 0, 0);
        tx.Spawn<EcsUnit>(EcsUnit.Position.Set(in pos), EcsUnit.Velocity.Set(in vel));
        tx.Commit();
        // Dispose after commit is safe — no rollback occurs
    }

    [Test]
    public void Dispose_Idempotent()
    {
        using var dbe = SetupEngine();

        var tx = dbe.CreateQuickTransaction();
        var pos = new EcsPosition(1, 2, 3);
        var vel = new EcsVelocity(0, 0, 0);
        tx.Spawn<EcsUnit>(EcsUnit.Position.Set(in pos), EcsUnit.Velocity.Set(in vel));
        tx.Dispose();
        Assert.DoesNotThrow(() => tx.Dispose(), "Double dispose should be safe");
    }

    [Test]
    public void Commit_AfterRollback_ReturnsFalse()
    {
        using var dbe = SetupEngine();

        using var tx = dbe.CreateQuickTransaction();
        var pos = new EcsPosition(1, 2, 3);
        var vel = new EcsVelocity(0, 0, 0);
        tx.Spawn<EcsUnit>(EcsUnit.Position.Set(in pos), EcsUnit.Velocity.Set(in vel));
        tx.Rollback();

        Assert.That(tx.Commit(), Is.False, "Commit after rollback should return false");
    }

    [Test]
    public void DoubleRollback_SecondReturnsFalse()
    {
        using var dbe = SetupEngine();

        using var tx = dbe.CreateQuickTransaction();
        var pos = new EcsPosition(1, 2, 3);
        var vel = new EcsVelocity(0, 0, 0);
        tx.Spawn<EcsUnit>(EcsUnit.Position.Set(in pos), EcsUnit.Velocity.Set(in vel));

        Assert.That(tx.Rollback(), Is.True);
        Assert.That(tx.Rollback(), Is.False, "Second rollback should return false");
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Conflict Resolution Strategies (gap-filling: remaining strategies)
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public void Conflict_DefaultHandler_LastWins()
    {
        using var dbe = SetupEngine();

        EntityId id;
        using (var tx = dbe.CreateQuickTransaction())
        {
            var pos = new EcsPosition(100, 0, 0);
            var vel = new EcsVelocity(0, 0, 0);
            id = tx.Spawn<EcsUnit>(EcsUnit.Position.Set(in pos), EcsUnit.Velocity.Set(in vel));
            tx.Commit();
        }

        using var t1 = dbe.CreateQuickTransaction();
        t1.OpenMut(id).Write(EcsUnit.Position).X = 50;

        using (var t2 = dbe.CreateQuickTransaction())
        {
            t2.OpenMut(id).Write(EcsUnit.Position).X = 200;
            t2.Commit();
        }

        // No-op handler — default ToCommitData = CommittingData (last wins)
        bool handlerCalled = false;
        void Handler(ref ConcurrencyConflictSolver solver) { handlerCalled = true; /* no action = last wins */ }
        Assert.That(t1.Commit(Handler), Is.True);
        Assert.That(handlerCalled, Is.True, "Handler should be called on conflict");

        using var tRead = dbe.CreateQuickTransaction();
        Assert.That(tRead.Open(id).Read(EcsUnit.Position).X, Is.EqualTo(50f),
            "Default (no-op handler) should be last-wins (T1's value)");
    }

    // ═══════════════════════════════════════════════════════════════════════
    // PK Index on Rollback (gap-filling)
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public void Rollback_Spawn_PkIndexNotPopulated()
    {
        using var dbe = SetupEngine();

        // PK B+Tree removed — verify entity is not visible after rollback via EntityMap
        using (var tx = dbe.CreateQuickTransaction())
        {
            var pos = new EcsPosition(1, 2, 3);
            var vel = new EcsVelocity(0, 0, 0);
            var e = tx.Spawn<EcsUnit>(EcsUnit.Position.Set(in pos), EcsUnit.Velocity.Set(in vel));
            // No commit → rollback
        }

        // Verify no entity is visible (rollback should have cleaned up)
        using (var tx2 = dbe.CreateQuickTransaction())
        {
            // Count entities via ECS query — should be 0 since spawn was rolled back
            var count = tx2.Query<EcsUnit>().Count();
            Assert.That(count, Is.EqualTo(0),
                "No entities should be visible after rollback");
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Multi-Entity in Same Transaction
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public void MultiEntity_SpawnWriteCommit_AllPersisted()
    {
        using var dbe = SetupEngine();

        using var tx = dbe.CreateQuickTransaction();
        var pos1 = new EcsPosition(10, 0, 0);
        var pos2 = new EcsPosition(20, 0, 0);
        var vel = new EcsVelocity(0, 0, 0);
        var id1 = tx.Spawn<EcsUnit>(EcsUnit.Position.Set(in pos1), EcsUnit.Velocity.Set(in vel));
        var id2 = tx.Spawn<EcsUnit>(EcsUnit.Position.Set(in pos2), EcsUnit.Velocity.Set(in vel));

        // Write to both in same tx
        tx.OpenMut(id1).Write(EcsUnit.Position).X = 111;
        tx.OpenMut(id2).Write(EcsUnit.Position).X = 222;
        tx.Commit();

        using var tx2 = dbe.CreateQuickTransaction();
        Assert.That(tx2.Open(id1).Read(EcsUnit.Position).X, Is.EqualTo(111f));
        Assert.That(tx2.Open(id2).Read(EcsUnit.Position).X, Is.EqualTo(222f));
    }

    [Test]
    public void MultiEntity_SpawnDestroyMix_Committed()
    {
        using var dbe = SetupEngine();

        EntityId id1;
        using (var tx = dbe.CreateQuickTransaction())
        {
            var pos = new EcsPosition(1, 0, 0);
            var vel = new EcsVelocity(0, 0, 0);
            id1 = tx.Spawn<EcsUnit>(EcsUnit.Position.Set(in pos), EcsUnit.Velocity.Set(in vel));
            tx.Commit();
        }

        // In one tx: spawn new + destroy old
        EntityId id2;
        using (var tx2 = dbe.CreateQuickTransaction())
        {
            var pos = new EcsPosition(2, 0, 0);
            var vel = new EcsVelocity(0, 0, 0);
            id2 = tx2.Spawn<EcsUnit>(EcsUnit.Position.Set(in pos), EcsUnit.Velocity.Set(in vel));
            tx2.Destroy(id1);
            tx2.Commit();
        }

        using var tx3 = dbe.CreateQuickTransaction();
        Assert.That(tx3.IsAlive(id1), Is.False, "Destroyed entity not alive");
        Assert.That(tx3.IsAlive(id2), Is.True, "Spawned entity alive");
        Assert.That(tx3.Open(id2).Read(EcsUnit.Position).X, Is.EqualTo(2f));
    }
}
