using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests;

// ═══════════════════════════════════════════════════════════════════════
// ECS test component types (must have [Component] for ComponentTable registration)
// ═══════════════════════════════════════════════════════════════════════

[Component("Typhon.Test.ECS.Position", 1)]
[StructLayout(LayoutKind.Sequential)]
public struct EcsPosition
{
    public float X, Y, Z;

    public EcsPosition(float x, float y, float z) { X = x; Y = y; Z = z; }
}

[Component("Typhon.Test.ECS.Velocity", 1)]
[StructLayout(LayoutKind.Sequential)]
public struct EcsVelocity
{
    public float Dx, Dy, Dz;

    public EcsVelocity(float dx, float dy, float dz) { Dx = dx; Dy = dy; Dz = dz; }
}

[Component("Typhon.Test.ECS.Health", 1)]
[StructLayout(LayoutKind.Sequential)]
public struct EcsHealth
{
    public int Current, Max;

    public EcsHealth(int current, int max) { Current = current; Max = max; }
}

// ═══════════════════════════════════════════════════════════════════════
// ECS test archetypes
// ═══════════════════════════════════════════════════════════════════════

[Archetype]
partial class EcsUnit : Archetype<EcsUnit>
{
    public static readonly Comp<EcsPosition> Position = Register<EcsPosition>();
    public static readonly Comp<EcsVelocity> Velocity = Register<EcsVelocity>();
}

[Archetype]
partial class EcsSoldier : Archetype<EcsSoldier, EcsUnit>
{
    public static readonly Comp<EcsHealth> Health = Register<EcsHealth>();
}

[NonParallelizable]
class EntitySpawnTests : TestBase<EntitySpawnTests>
{
    [OneTimeSetUp]
    public void OneTimeSetup()
    {
        // Ensure archetypes are finalized
    }

    private DatabaseEngine SetupEngine()
    {
        var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();

        // Register ECS component types as ComponentTables
        dbe.RegisterComponentFromAccessor<EcsPosition>();
        dbe.RegisterComponentFromAccessor<EcsVelocity>();
        dbe.RegisterComponentFromAccessor<EcsHealth>();

        // Initialize archetypes — connects slots to ComponentTables
        dbe.InitializeArchetypes();

        return dbe;
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Spawn Tests
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public void Spawn_SingleEntity_ReturnsValidEntityId()
    {
        using var dbe = SetupEngine();

        using var t = dbe.CreateQuickTransaction();
        var pos = new EcsPosition(1, 2, 3);
        var vel = new EcsVelocity(4, 5, 6);
        var id = t.Spawn<EcsUnit>(EcsUnit.Position.Set(in pos), EcsUnit.Velocity.Set(in vel));

        Assert.That(id.IsNull, Is.False);
        Assert.That(id.ArchetypeId, Is.EqualTo(dbe.RoutingIdOf(Archetype<EcsUnit>.Metadata)));
        Assert.That(id.EntityKey, Is.GreaterThan(0));
    }

    [Test]
    public void Spawn_ThenOpen_ReadsComponentData()
    {
        using var dbe = SetupEngine();

        using var t = dbe.CreateQuickTransaction();
        var pos = new EcsPosition(10, 20, 30);
        var vel = new EcsVelocity(1, 0, 0);
        var id = t.Spawn<EcsUnit>(EcsUnit.Position.Set(in pos), EcsUnit.Velocity.Set(in vel));

        // Open within same transaction (before commit — reads from pending map)
        var entity = t.Open(id);
        Assert.That(entity.IsValid, Is.True);

        ref readonly var readPos = ref entity.Read(EcsUnit.Position);
        Assert.That(readPos.X, Is.EqualTo(10f));
        Assert.That(readPos.Y, Is.EqualTo(20f));
        Assert.That(readPos.Z, Is.EqualTo(30f));

        ref readonly var readVel = ref entity.Read(EcsUnit.Velocity);
        Assert.That(readVel.Dx, Is.EqualTo(1f));
    }

    [Test]
    public void Spawn_WithInheritedArchetype_HasParentAndOwnComponents()
    {
        using var dbe = SetupEngine();

        using var t = dbe.CreateQuickTransaction();
        var pos = new EcsPosition(1, 1, 1);
        var vel = new EcsVelocity(2, 2, 2);
        var hp = new EcsHealth(100, 100);

        var id = t.Spawn<EcsSoldier>(
            EcsUnit.Position.Set(in pos),
            EcsUnit.Velocity.Set(in vel),
            EcsSoldier.Health.Set(in hp));

        Assert.That(id.ArchetypeId, Is.EqualTo(dbe.RoutingIdOf(Archetype<EcsSoldier>.Metadata)));

        var entity = t.Open(id);

        // Inherited components (from EcsUnit)
        ref readonly var readPos = ref entity.Read(EcsUnit.Position);
        Assert.That(readPos.X, Is.EqualTo(1f));

        // Own component
        ref readonly var readHp = ref entity.Read(EcsSoldier.Health);
        Assert.That(readHp.Current, Is.EqualTo(100));
        Assert.That(readHp.Max, Is.EqualTo(100));
    }

    [Test]
    public void Spawn_OpenMut_WritesComponentData()
    {
        using var dbe = SetupEngine();

        using var t = dbe.CreateQuickTransaction();
        var pos = new EcsPosition(0, 0, 0);
        var vel = new EcsVelocity(0, 0, 0);
        var id = t.Spawn<EcsUnit>(EcsUnit.Position.Set(in pos), EcsUnit.Velocity.Set(in vel));

        var entity = t.OpenMut(id);
        ref var writePos = ref entity.Write(EcsUnit.Position);
        writePos.X = 999;
        writePos.Y = 888;

        // Re-open and verify write persisted
        var entity2 = t.Open(id);
        ref readonly var readPos = ref entity2.Read(EcsUnit.Position);
        Assert.That(readPos.X, Is.EqualTo(999f));
        Assert.That(readPos.Y, Is.EqualTo(888f));
    }

    [Test]
    public void Spawn_MultipleEntities_UniqueIds()
    {
        using var dbe = SetupEngine();

        using var t = dbe.CreateQuickTransaction();
        var pos = new EcsPosition(0, 0, 0);
        var vel = new EcsVelocity(0, 0, 0);

        var id1 = t.Spawn<EcsUnit>(EcsUnit.Position.Set(in pos), EcsUnit.Velocity.Set(in vel));
        var id2 = t.Spawn<EcsUnit>(EcsUnit.Position.Set(in pos), EcsUnit.Velocity.Set(in vel));
        var id3 = t.Spawn<EcsUnit>(EcsUnit.Position.Set(in pos), EcsUnit.Velocity.Set(in vel));

        Assert.That(id1, Is.Not.EqualTo(id2));
        Assert.That(id2, Is.Not.EqualTo(id3));
        Assert.That(id1.ArchetypeId, Is.EqualTo(id2.ArchetypeId));
    }

    [Test]
    public void TryOpen_NonExistentEntity_ReturnsFalse()
    {
        using var dbe = SetupEngine();

        using var t = dbe.CreateQuickTransaction();
        var fakeId = new EntityId(999999, 100);
        bool found = t.TryOpen(fakeId, out var entity);
        Assert.That(found, Is.False);
        Assert.That(entity.IsValid, Is.False);
    }

    [Test]
    public void IsAlive_NullEntity_ReturnsFalse()
    {
        using var dbe = SetupEngine();

        using var t = dbe.CreateQuickTransaction();
        Assert.That(t.IsAlive(EntityId.Null), Is.False);
    }

    [Test]
    public void Spawn_MultipleRollbacks_NoChunkLeak()
    {
        using var dbe = SetupEngine();

        // Perform multiple spawn-then-rollback cycles
        // If chunk cleanup works, the CBS won't grow unboundedly
        for (int cycle = 0; cycle < 10; cycle++)
        {
            using var t = dbe.CreateQuickTransaction();
            var pos = new EcsPosition(cycle, 0, 0);
            var vel = new EcsVelocity(0, 0, 0);
            t.Spawn<EcsUnit>(EcsUnit.Position.Set(in pos), EcsUnit.Velocity.Set(in vel));
            // Don't commit — chunks should be freed on dispose
        }

        // If we get here without running out of pages, cleanup is working
        // Verify we can still spawn normally
        using (var t = dbe.CreateQuickTransaction())
        {
            var pos = new EcsPosition(999, 0, 0);
            var vel = new EcsVelocity(0, 0, 0);
            var id = t.Spawn<EcsUnit>(EcsUnit.Position.Set(in pos), EcsUnit.Velocity.Set(in vel));
            t.Commit();

            Assert.That(id.IsNull, Is.False);
        }

        using (var t = dbe.CreateQuickTransaction())
        {
            // Rolled-back entities should all be invisible
            Assert.That(t.IsAlive(new EntityId(1, 100)), Is.False);
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // SpawnBatch / DestroyBatch tests
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public void SpawnBatch_CreatesAllEntities()
    {
        using var dbe = SetupEngine();

        using var t = dbe.CreateQuickTransaction();
        Span<EntityId> ids = stackalloc EntityId[100];
        var pos = new EcsPosition(1, 2, 3);
        var vel = new EcsVelocity(4, 5, 6);
        t.SpawnBatch<EcsUnit>(ids, EcsUnit.Position.Set(in pos), EcsUnit.Velocity.Set(in vel));

        Assert.That(ids[0].IsNull, Is.False);
        Assert.That(ids[99].IsNull, Is.False);
        Assert.That(ids[0].ArchetypeId, Is.EqualTo(dbe.RoutingIdOf(Archetype<EcsUnit>.Metadata)));

        // All entities should be readable
        for (int i = 0; i < 100; i++)
        {
            var entity = t.Open(ids[i]);
            ref readonly var readPos = ref entity.Read(EcsUnit.Position);
            Assert.That(readPos.X, Is.EqualTo(1f));
        }

        t.Commit();
    }

    [Test]
    public void SpawnBatch_UniqueEntityKeys()
    {
        using var dbe = SetupEngine();

        using var t = dbe.CreateQuickTransaction();
        Span<EntityId> ids = stackalloc EntityId[50];
        t.SpawnBatch<EcsUnit>(ids);

        var keys = new HashSet<long>();
        for (int i = 0; i < 50; i++)
        {
            Assert.That(keys.Add(ids[i].EntityKey), Is.True, $"Entity key {ids[i].EntityKey} is not unique at index {i}");
        }
    }

    [Test]
    public void SpawnBatch_ZeroValues_AllDisabled()
    {
        using var dbe = SetupEngine();

        using var t = dbe.CreateQuickTransaction();
        Span<EntityId> ids = stackalloc EntityId[10];
        t.SpawnBatch<EcsUnit>(ids); // No component values

        // Entities exist but components are disabled (no values provided)
        for (int i = 0; i < 10; i++)
        {
            Assert.That(t.IsAlive(ids[i]), Is.True);
        }
    }

    [Test]
    public void SpawnBatch_CommitThenRead_InNewTransaction()
    {
        using var dbe = SetupEngine();

        EntityId[] ids = new EntityId[20];
        {
            using var t = dbe.CreateQuickTransaction();
            t.SpawnBatch<EcsUnit>(ids, EcsUnit.Position.Set(new EcsPosition(10, 20, 30)));
            t.Commit();
        }

        using var t2 = dbe.CreateQuickTransaction();
        for (int i = 0; i < 20; i++)
        {
            var entity = t2.Open(ids[i]);
            ref readonly var pos = ref entity.Read(EcsUnit.Position);
            Assert.That(pos.X, Is.EqualTo(10f));
        }
    }

    [Test]
    public void DestroyBatch_AllDestroyed()
    {
        using var dbe = SetupEngine();

        EntityId[] ids = new EntityId[30];
        {
            using var t = dbe.CreateQuickTransaction();
            t.SpawnBatch<EcsUnit>(ids, EcsUnit.Position.Set(new EcsPosition(1, 2, 3)));
            t.Commit();
        }

        // Destroy the first 15
        {
            using var t = dbe.CreateQuickTransaction();
            t.DestroyBatch(new ReadOnlySpan<EntityId>(ids, 0, 15));
            t.Commit();
        }

        using var t2 = dbe.CreateQuickTransaction();
        for (int i = 0; i < 15; i++)
        {
            Assert.That(t2.IsAlive(ids[i]), Is.False, $"Entity {i} should be destroyed");
        }
        for (int i = 15; i < 30; i++)
        {
            Assert.That(t2.IsAlive(ids[i]), Is.True, $"Entity {i} should survive");
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Generated ReadAll / ReadWriteAll tests
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public void ReadAll_ReturnsAllComponents_ZeroCopy()
    {
        using var dbe = SetupEngine();

        EntityId id;
        using (var t = dbe.CreateQuickTransaction())
        {
            id = t.Spawn<EcsUnit>(
                EcsUnit.Position.Set(new EcsPosition(10, 20, 30)),
                EcsUnit.Velocity.Set(new EcsVelocity(1, 2, 3)));
            t.Commit();
        }

        using var t2 = dbe.CreateQuickTransaction();
        var refs = EcsUnit.ReadAll(t2, id);

        Assert.That(refs.Position.X, Is.EqualTo(10f));
        Assert.That(refs.Position.Y, Is.EqualTo(20f));
        Assert.That(refs.Position.Z, Is.EqualTo(30f));
        Assert.That(refs.Velocity.Dx, Is.EqualTo(1f));
        Assert.That(refs.Velocity.Dy, Is.EqualTo(2f));
        Assert.That(refs.Velocity.Dz, Is.EqualTo(3f));
    }

    [Test]
    public void ReadAll_InheritedArchetype_IncludesParentComponents()
    {
        using var dbe = SetupEngine();

        EntityId id;
        using (var t = dbe.CreateQuickTransaction())
        {
            id = t.Spawn<EcsSoldier>(
                EcsUnit.Position.Set(new EcsPosition(5, 10, 15)),
                EcsUnit.Velocity.Set(new EcsVelocity(1, 0, 0)),
                EcsSoldier.Health.Set(new EcsHealth(100, 100)));
            t.Commit();
        }

        using var t2 = dbe.CreateQuickTransaction();
        var refs = EcsSoldier.ReadAll(t2, id);

        // Inherited from EcsUnit
        Assert.That(refs.Position.X, Is.EqualTo(5f));
        Assert.That(refs.Velocity.Dx, Is.EqualTo(1f));
        // Own component
        Assert.That(refs.Health.Current, Is.EqualTo(100));
        Assert.That(refs.Health.Max, Is.EqualTo(100));
    }

    [Test]
    public void ReadWriteAll_MutatesComponentsDirectly()
    {
        using var dbe = SetupEngine();

        EntityId id;
        using (var t = dbe.CreateQuickTransaction())
        {
            id = t.Spawn<EcsUnit>(
                EcsUnit.Position.Set(new EcsPosition(0, 0, 0)),
                EcsUnit.Velocity.Set(new EcsVelocity(0, 0, 0)));
            t.Commit();
        }

        using (var t = dbe.CreateQuickTransaction())
        {
            var mut = EcsUnit.ReadWriteAll(t, id);
            mut.Position.X = 999;
            mut.Position.Y = 888;
            mut.Velocity.Dx = 42;
            t.Commit();
        }

        // Verify writes persisted
        using var t2 = dbe.CreateQuickTransaction();
        var refs = EcsUnit.ReadAll(t2, id);
        Assert.That(refs.Position.X, Is.EqualTo(999f));
        Assert.That(refs.Position.Y, Is.EqualTo(888f));
        Assert.That(refs.Velocity.Dx, Is.EqualTo(42f));
    }
}
