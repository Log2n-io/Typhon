using System;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests;

[NonParallelizable]
unsafe class SchemaVersioningTests : TestBase<SchemaVersioningTests>
{
    [OneTimeSetUp]
    public void OneTimeSetup()
    {
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Structural tests
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public void ArchetypeR1_SizeOf_Compact()
    {
        // 64B Name + 2B ArchetypeId + 2B ParentArchetypeId + 1B ComponentCount + 3B pad + 4B Revision + 4B ComponentNames + 4B EntityMapSPI + 4B ClusterSegmentSPI
        // + 8B NextEntityKey = 96B, then + 2B AssemblyId rounds up to the struct's 8B alignment (it contains a long) → 104B.
        Assert.That(sizeof(ArchetypeR1), Is.EqualTo(104));
    }

    [Test]
    public void BuildArchetypeR1_FromMetadata_CorrectFields()
    {
        var meta = ArchetypeRegistry.GetMetadata(100); // EcsUnit
        Assert.That(meta, Is.Not.Null);

        var arch = DatabaseEngine.BuildArchetypeR1(meta);
        Assert.That(arch.ArchetypeId, Is.EqualTo(100));
        Assert.That(arch.ComponentCount, Is.EqualTo(2));
        Assert.That(arch.ParentArchetypeId, Is.EqualTo(ArchetypeR1.NoParent));
        Assert.That(arch.Name.AsString, Is.EqualTo("EcsUnit"));
    }

    [Test]
    public void BuildArchetypeR1_InheritedArchetype_HasParentId()
    {
        var meta = ArchetypeRegistry.GetMetadata(101); // EcsSoldier
        var arch = DatabaseEngine.BuildArchetypeR1(meta);
        Assert.That(arch.ArchetypeId, Is.EqualTo(101));
        Assert.That(arch.ParentArchetypeId, Is.EqualTo(100));
        Assert.That(arch.ComponentCount, Is.EqualTo(3));
    }

    [Test]
    public void GetArchetypeComponentNames_CorrectOrder()
    {
        var meta = ArchetypeRegistry.GetMetadata(101); // EcsSoldier
        var names = DatabaseEngine.GetArchetypeComponentNames(meta);

        Assert.That(names.Length, Is.EqualTo(3));
        Assert.That(names[0].AsString, Is.EqualTo("Typhon.Test.ECS.Position"));
        Assert.That(names[1].AsString, Is.EqualTo("Typhon.Test.ECS.Velocity"));
        Assert.That(names[2].AsString, Is.EqualTo("Typhon.Test.ECS.Health"));
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Persistence tests
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public void InitializeArchetypes_PersistsArchetypeR1Entities()
    {
        using (var scope = ServiceProvider.CreateScope())
        {
            using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
            dbe.RegisterComponentFromAccessor<EcsPosition>();
            dbe.RegisterComponentFromAccessor<EcsVelocity>();
            dbe.RegisterComponentFromAccessor<EcsHealth>();
            dbe.InitializeArchetypes();

            // Verify ArchetypeR1 entities were persisted
            var table = dbe.GetComponentTable<ArchetypeR1>();
            Assert.That(table, Is.Not.Null);
            Assert.That(table.ComponentSegment.AllocatedChunkCount, Is.GreaterThan(0),
                "ArchetypeR1 entities should have been persisted");
        }
    }

    [Test]
    public void Persist_ThenReopen_SameSchema_NoError()
    {
        // Phase 1: Create database with archetypes
        using (var scope1 = ServiceProvider.CreateScope())
        {
            using var dbe = scope1.ServiceProvider.GetRequiredService<DatabaseEngine>();
            dbe.RegisterComponentFromAccessor<EcsPosition>();
            dbe.RegisterComponentFromAccessor<EcsVelocity>();
            dbe.RegisterComponentFromAccessor<EcsHealth>();
            dbe.InitializeArchetypes();
        }

        // Phase 2: Reopen with same archetypes — validation should pass
        using (var scope2 = ServiceProvider.CreateScope())
        {
            using var dbe = scope2.ServiceProvider.GetRequiredService<DatabaseEngine>();
            dbe.RegisterComponentFromAccessor<EcsPosition>();
            dbe.RegisterComponentFromAccessor<EcsVelocity>();
            dbe.RegisterComponentFromAccessor<EcsHealth>();
            Assert.DoesNotThrow(() => dbe.InitializeArchetypes());
        }
    }

    [Test]
    public void Persist_ThenReopen_EntityCount_Survives()
    {
        // Regression: the EntityMap's in-memory _entryCount was only flushed to the persisted meta chunk during bucket splits. For small entity counts that
        // never trigger a split, the persisted count stayed at 0 and GetArchetypeEntityCount returned 0 after reopen even though all the bucket chunks had
        // the right data. Fix: PersistArchetypeState now calls EntityMap.FlushMeta on dispose so the count reaches disk.

        const int unitCount = 15;
        const int soldierCount = 7;

        using (var scope1 = ServiceProvider.CreateScope())
        {
            using var dbe = scope1.ServiceProvider.GetRequiredService<DatabaseEngine>();
            dbe.RegisterComponentFromAccessor<EcsPosition>();
            dbe.RegisterComponentFromAccessor<EcsVelocity>();
            dbe.RegisterComponentFromAccessor<EcsHealth>();
            dbe.InitializeArchetypes();

            using (var tx = dbe.CreateQuickTransaction())
            {
                var pos = new EcsPosition(1, 2, 3);
                var vel = new EcsVelocity(4, 5, 6);
                for (int i = 0; i < unitCount; i++)
                {
                    tx.Spawn<EcsUnit>(EcsUnit.Position.Set(in pos), EcsUnit.Velocity.Set(in vel));
                }
                var health = new EcsHealth(100, 100);
                for (int i = 0; i < soldierCount; i++)
                {
                    tx.Spawn<EcsSoldier>(
                        EcsUnit.Position.Set(in pos),
                        EcsUnit.Velocity.Set(in vel),
                        EcsSoldier.Health.Set(in health));
                }
                Assert.That(tx.Commit(), Is.True);
            }
            dbe.ForceCheckpoint();
        }

        using (var scope2 = ServiceProvider.CreateScope())
        {
            using var dbe = scope2.ServiceProvider.GetRequiredService<DatabaseEngine>();
            dbe.RegisterComponentFromAccessor<EcsPosition>();
            dbe.RegisterComponentFromAccessor<EcsVelocity>();
            dbe.RegisterComponentFromAccessor<EcsHealth>();
            dbe.InitializeArchetypes();

            Assert.That(dbe.GetArchetypeEntityCount(100), Is.EqualTo(unitCount),
                "EcsUnit entity count should survive reopen without a bucket split");
            Assert.That(dbe.GetArchetypeEntityCount(101), Is.EqualTo(soldierCount),
                "EcsSoldier entity count should survive reopen without a bucket split");
        }
    }

    [Test]
    public void Persist_ThenTamper_ComponentCount_Throws()
    {
        // Phase 1: Create database with archetypes
        using (var scope1 = ServiceProvider.CreateScope())
        {
            using var dbe = scope1.ServiceProvider.GetRequiredService<DatabaseEngine>();
            dbe.RegisterComponentFromAccessor<EcsPosition>();
            dbe.RegisterComponentFromAccessor<EcsVelocity>();
            dbe.RegisterComponentFromAccessor<EcsHealth>();
            dbe.InitializeArchetypes();
        }

        // Phase 1.5: Reopen and corrupt the persisted ArchetypeR1 by changing ComponentCount
        using (var scope15 = ServiceProvider.CreateScope())
        {
            using var dbe = scope15.ServiceProvider.GetRequiredService<DatabaseEngine>();
            dbe.RegisterComponentFromAccessor<EcsPosition>();
            dbe.RegisterComponentFromAccessor<EcsVelocity>();
            dbe.RegisterComponentFromAccessor<EcsHealth>();

            // Register ArchetypeR1 so we can read/write it
            if (dbe.GetComponentTable<ArchetypeR1>() == null)
            {
                dbe.RegisterComponentFromAccessor<ArchetypeR1>();
            }

            var table = dbe.GetComponentTable<ArchetypeR1>();

            // Find and tamper with the EcsUnit record (ArchetypeId = 100)
            var cs = dbe.MMF.CreateChangeSet();
            var segment = table.ComponentSegment;
            var capacity = segment.ChunkCapacity;
            for (int chunkId = 1; chunkId < capacity; chunkId++)
            {
                if (!segment.IsChunkAllocated(chunkId))
                {
                    continue;
                }

                if (SystemCrud.Read(table, chunkId, out ArchetypeR1 arch, dbe.EpochManager) && arch.ArchetypeId == 100)
                {
                    arch.ComponentCount = 99; // corrupt it
                    SystemCrud.Update(table, chunkId, ref arch, dbe.EpochManager, cs);
                    break;
                }
            }
            cs.SaveChanges();
        }

        // Phase 2: Reopen with real schema — validation should detect mismatch and throw
        using (var scope2 = ServiceProvider.CreateScope())
        {
            using var dbe = scope2.ServiceProvider.GetRequiredService<DatabaseEngine>();
            dbe.RegisterComponentFromAccessor<EcsPosition>();
            dbe.RegisterComponentFromAccessor<EcsVelocity>();
            dbe.RegisterComponentFromAccessor<EcsHealth>();

            var ex = Assert.Throws<InvalidOperationException>(() => dbe.InitializeArchetypes());
            Assert.That(ex.Message, Does.Contain("Schema mismatch"));
            Assert.That(ex.Message, Does.Contain("EcsUnit"));
        }
    }

    [Test]
    public void Persist_ThenTamper_Revision_Throws()
    {
        // Phase 1: Create database
        using (var scope1 = ServiceProvider.CreateScope())
        {
            using var dbe = scope1.ServiceProvider.GetRequiredService<DatabaseEngine>();
            dbe.RegisterComponentFromAccessor<EcsPosition>();
            dbe.RegisterComponentFromAccessor<EcsVelocity>();
            dbe.RegisterComponentFromAccessor<EcsHealth>();
            dbe.InitializeArchetypes();
        }

        // Phase 1.5: Corrupt the revision number
        using (var scope15 = ServiceProvider.CreateScope())
        {
            using var dbe = scope15.ServiceProvider.GetRequiredService<DatabaseEngine>();
            dbe.RegisterComponentFromAccessor<EcsPosition>();
            dbe.RegisterComponentFromAccessor<EcsVelocity>();
            dbe.RegisterComponentFromAccessor<EcsHealth>();

            if (dbe.GetComponentTable<ArchetypeR1>() == null)
            {
                dbe.RegisterComponentFromAccessor<ArchetypeR1>();
            }

            var table = dbe.GetComponentTable<ArchetypeR1>();

            // Corrupt the revision number
            var cs = dbe.MMF.CreateChangeSet();
            var segment = table.ComponentSegment;
            var capacity = segment.ChunkCapacity;
            for (int chunkId = 1; chunkId < capacity; chunkId++)
            {
                if (!segment.IsChunkAllocated(chunkId))
                {
                    continue;
                }

                if (SystemCrud.Read(table, chunkId, out ArchetypeR1 arch, dbe.EpochManager) && arch.ArchetypeId == 100)
                {
                    arch.Revision = 999; // corrupt it
                    SystemCrud.Update(table, chunkId, ref arch, dbe.EpochManager, cs);
                    break;
                }
            }
            cs.SaveChanges();
        }

        // Phase 2: Validation should throw
        using (var scope2 = ServiceProvider.CreateScope())
        {
            using var dbe = scope2.ServiceProvider.GetRequiredService<DatabaseEngine>();
            dbe.RegisterComponentFromAccessor<EcsPosition>();
            dbe.RegisterComponentFromAccessor<EcsVelocity>();
            dbe.RegisterComponentFromAccessor<EcsHealth>();

            var ex = Assert.Throws<InvalidOperationException>(() => dbe.InitializeArchetypes());
            Assert.That(ex.Message, Does.Contain("Schema mismatch"));
            Assert.That(ex.Message, Does.Contain("revision"));
        }
    }

    [Test]
    public void Persist_ComponentNames_StoredInVSBS()
    {
        using (var scope = ServiceProvider.CreateScope())
        {
            using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
            dbe.RegisterComponentFromAccessor<EcsPosition>();
            dbe.RegisterComponentFromAccessor<EcsVelocity>();
            dbe.RegisterComponentFromAccessor<EcsHealth>();
            dbe.InitializeArchetypes();

            // Read back persisted ArchetypeR1 and verify ComponentNames collection is populated
            using var epochGuard = EpochGuard.Enter(dbe.EpochManager);
            var archTable = dbe.GetComponentTable<ArchetypeR1>();
            var archSegment = archTable.ComponentSegment;
            var archCapacity = archSegment.ChunkCapacity;

            bool foundUnit = false;
            for (int chunkId = 1; chunkId < archCapacity; chunkId++)
            {
                if (!archSegment.IsChunkAllocated(chunkId))
                {
                    continue;
                }

                if (!SystemCrud.Read(archTable, chunkId, out ArchetypeR1 arch, dbe.EpochManager))
                {
                    continue;
                }

                if (arch.ArchetypeId != 100)
                {
                    continue; // skip non-EcsUnit
                }

                foundUnit = true;
                Assert.That(arch.ComponentCount, Is.EqualTo(2));

                // Read ComponentNames from VSBS
                Assert.That(arch.ComponentNames._bufferId, Is.Not.EqualTo(0),
                    "ComponentNames collection should be populated");

                var vsbs = dbe.GetComponentCollectionVSBS<String64>();
                var names = new System.Collections.Generic.List<String64>();
                foreach (var name in vsbs.EnumerateBuffer(arch.ComponentNames._bufferId))
                {
                    names.Add(name);
                }

                Assert.That(names.Count, Is.EqualTo(2));
                Assert.That(names[0].AsString, Is.EqualTo("Typhon.Test.ECS.Position"));
                Assert.That(names[1].AsString, Is.EqualTo("Typhon.Test.ECS.Velocity"));
            }

            Assert.That(foundUnit, Is.True, "EcsUnit archetype should have been persisted");
        }
    }

    [Test]
    public void NewArchetype_NotPersisted_NoError()
    {
        // If a runtime archetype has no persisted record, it's a new archetype — should be OK
        using (var scope = ServiceProvider.CreateScope())
        {
            using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
            dbe.RegisterComponentFromAccessor<EcsPosition>();
            dbe.RegisterComponentFromAccessor<EcsVelocity>();
            dbe.RegisterComponentFromAccessor<EcsHealth>();

            // First init — all archetypes are new, nothing persisted yet
            Assert.DoesNotThrow(() => dbe.InitializeArchetypes());
        }
    }
}
