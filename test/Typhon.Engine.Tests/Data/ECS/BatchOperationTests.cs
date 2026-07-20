using System;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;

namespace Typhon.Engine.Tests;

class BatchOperationTests : TestBase<BatchOperationTests>
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
        dbe.RegisterComponentFromAccessor<BagData>();
        dbe.RegisterComponentFromAccessor<ItemData>();
        dbe.InitializeArchetypes();
        return dbe;
    }

    // ═══════════════════════════════════════════════════════════════════════
    // SpawnBatch — shared values (existing API)
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public void SpawnBatch_SharedValues_AllEntitiesHaveSameData()
    {
        using var dbe = SetupEngine();

        using var t = dbe.CreateQuickTransaction();
        var pos = new EcsPosition(10, 20, 30);
        var vel = new EcsVelocity(1, 0, 0);

        Span<EntityId> ids = stackalloc EntityId[5];
        t.SpawnBatch<EcsUnit>(ids, EcsUnit.Position.Set(in pos), EcsUnit.Velocity.Set(in vel));

        for (int i = 0; i < ids.Length; i++)
        {
            Assert.That(ids[i].IsNull, Is.False);
            var entity = t.Open(ids[i]);
            ref readonly var readPos = ref entity.Read(EcsUnit.Position);
            Assert.That(readPos.X, Is.EqualTo(10f));
            Assert.That(readPos.Y, Is.EqualTo(20f));
            Assert.That(readPos.Z, Is.EqualTo(30f));
            ref readonly var readVel = ref entity.Read(EcsUnit.Velocity);
            Assert.That(readVel.Dx, Is.EqualTo(1f));
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // SpawnBatch — SOA per-entity data (source-generated)
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public void SpawnBatch_SOA_PerEntityData()
    {
        using var dbe = SetupEngine();

        using var t = dbe.CreateQuickTransaction();
        var positions = new EcsPosition[]
        {
            new(1, 2, 3),
            new(4, 5, 6),
            new(7, 8, 9),
        };
        var velocities = new EcsVelocity[]
        {
            new(10, 0, 0),
            new(0, 20, 0),
            new(0, 0, 30),
        };

        var ids = EcsUnit.SpawnBatch(t, positions, velocities);

        Assert.That(ids.Length, Is.EqualTo(3));
        for (int i = 0; i < ids.Length; i++)
        {
            Assert.That(ids[i].IsNull, Is.False);
            Assert.That(ids[i].ArchetypeId, Is.EqualTo(dbe.RoutingIdOf(Archetype<EcsUnit>.Metadata)));

            var entity = t.Open(ids[i]);
            ref readonly var p = ref entity.Read(EcsUnit.Position);
            Assert.That(p.X, Is.EqualTo(positions[i].X));
            Assert.That(p.Y, Is.EqualTo(positions[i].Y));
            Assert.That(p.Z, Is.EqualTo(positions[i].Z));

            ref readonly var v = ref entity.Read(EcsUnit.Velocity);
            Assert.That(v.Dx, Is.EqualTo(velocities[i].Dx));
            Assert.That(v.Dy, Is.EqualTo(velocities[i].Dy));
            Assert.That(v.Dz, Is.EqualTo(velocities[i].Dz));
        }
    }

    [Test]
    public void SpawnBatch_SOA_WithInheritance()
    {
        using var dbe = SetupEngine();

        using var t = dbe.CreateQuickTransaction();
        var positions = new EcsPosition[] { new(1, 1, 1), new(2, 2, 2) };
        var velocities = new EcsVelocity[] { new(3, 3, 3), new(4, 4, 4) };
        var healths = new EcsHealth[] { new(100, 100), new(50, 200) };

        var ids = EcsSoldier.SpawnBatch(t, positions, velocities, healths);

        Assert.That(ids.Length, Is.EqualTo(2));
        for (int i = 0; i < ids.Length; i++)
        {
            Assert.That(ids[i].ArchetypeId, Is.EqualTo(dbe.RoutingIdOf(Archetype<EcsSoldier>.Metadata)));

            var entity = t.Open(ids[i]);

            // Inherited components
            ref readonly var p = ref entity.Read(EcsUnit.Position);
            Assert.That(p.X, Is.EqualTo(positions[i].X));

            ref readonly var v = ref entity.Read(EcsUnit.Velocity);
            Assert.That(v.Dx, Is.EqualTo(velocities[i].Dx));

            // Own component
            ref readonly var h = ref entity.Read(EcsSoldier.Health);
            Assert.That(h.Current, Is.EqualTo(healths[i].Current));
            Assert.That(h.Max, Is.EqualTo(healths[i].Max));
        }
    }

    [Test]
    public void SpawnBatch_ZeroCount_NoOp()
    {
        using var dbe = SetupEngine();

        using var t = dbe.CreateQuickTransaction();
        var ids = EcsUnit.SpawnBatch(t, ReadOnlySpan<EcsPosition>.Empty, ReadOnlySpan<EcsVelocity>.Empty);

        Assert.That(ids.Length, Is.EqualTo(0));
    }

    [Test]
    public void SpawnBatch_QuerySameTx_FindsAllEntities()
    {
        using var dbe = SetupEngine();

        using var t = dbe.CreateQuickTransaction();
        var positions = new EcsPosition[10];
        var velocities = new EcsVelocity[10];
        for (int i = 0; i < 10; i++)
        {
            positions[i] = new EcsPosition(i, 0, 0);
            velocities[i] = new EcsVelocity(0, i, 0);
        }

        var ids = EcsUnit.SpawnBatch(t, positions, velocities);

        // Query within same transaction should find all spawned entities
        int count = t.Query<EcsUnit>().Count();
        Assert.That(count, Is.GreaterThanOrEqualTo(10));
    }

    [Test]
    public void SpawnBatch_ThenDestroy_SameTx()
    {
        using var dbe = SetupEngine();

        using var t = dbe.CreateQuickTransaction();
        var positions = new EcsPosition[] { new(1, 0, 0), new(2, 0, 0), new(3, 0, 0), new(4, 0, 0) };
        var velocities = new EcsVelocity[] { new(0, 1, 0), new(0, 2, 0), new(0, 3, 0), new(0, 4, 0) };

        var ids = EcsUnit.SpawnBatch(t, positions, velocities);

        // Destroy first two
        t.Destroy(ids[0]);
        t.Destroy(ids[1]);

        Assert.That(t.TryOpen(ids[0], out _), Is.False);
        Assert.That(t.TryOpen(ids[1], out _), Is.False);
        Assert.That(t.TryOpen(ids[2], out _), Is.True);
        Assert.That(t.TryOpen(ids[3], out _), Is.True);
    }

    // ═══════════════════════════════════════════════════════════════════════
    // DestroyBatch
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public void DestroyBatch_AllEntitiesDead()
    {
        using var dbe = SetupEngine();

        using var t = dbe.CreateQuickTransaction();
        var positions = new EcsPosition[] { new(1, 0, 0), new(2, 0, 0), new(3, 0, 0) };
        var velocities = new EcsVelocity[] { new(0, 1, 0), new(0, 2, 0), new(0, 3, 0) };

        var ids = EcsUnit.SpawnBatch(t, positions, velocities);

        t.DestroyBatch(ids);

        for (int i = 0; i < ids.Length; i++)
        {
            Assert.That(t.TryOpen(ids[i], out _), Is.False, $"Entity {i} should be destroyed");
        }
    }

    [Test]
    public void DestroyBatch_WithCascade()
    {
        using var dbe = SetupEngine();

        using var t = dbe.CreateQuickTransaction();

        // Spawn two bags
        var bag1Data = new BagData { Capacity = 10 };
        var bag2Data = new BagData { Capacity = 20 };
        var bag1Id = t.Spawn<CascadeBag>(CascadeBag.Bag.Set(in bag1Data));
        var bag2Id = t.Spawn<CascadeBag>(CascadeBag.Bag.Set(in bag2Data));

        // Spawn items for each bag
        var item1Data = new ItemData { Owner = bag1Id, Weight = 5 };
        var item2Data = new ItemData { Owner = bag1Id, Weight = 3 };
        var item3Data = new ItemData { Owner = bag2Id, Weight = 7 };
        var item1Id = t.Spawn<CascadeItem>(CascadeItem.Item.Set(in item1Data));
        var item2Id = t.Spawn<CascadeItem>(CascadeItem.Item.Set(in item2Data));
        var item3Id = t.Spawn<CascadeItem>(CascadeItem.Item.Set(in item3Data));

        // DestroyBatch both bags — cascade should delete all items
        t.DestroyBatch(new[] { bag1Id, bag2Id });

        Assert.That(t.TryOpen(bag1Id, out _), Is.False, "Bag 1 should be destroyed");
        Assert.That(t.TryOpen(bag2Id, out _), Is.False, "Bag 2 should be destroyed");
        Assert.That(t.TryOpen(item1Id, out _), Is.False, "Item 1 should be cascade-destroyed");
        Assert.That(t.TryOpen(item2Id, out _), Is.False, "Item 2 should be cascade-destroyed");
        Assert.That(t.TryOpen(item3Id, out _), Is.False, "Item 3 should be cascade-destroyed");
    }

    [Test]
    public void DestroyBatch_EmptySpan_NoOp()
    {
        using var dbe = SetupEngine();

        using var t = dbe.CreateQuickTransaction();
        Assert.DoesNotThrow(() => t.DestroyBatch(ReadOnlySpan<EntityId>.Empty));
    }

    [Test]
    public void DestroyBatch_SomeAlreadyDead_NoError()
    {
        using var dbe = SetupEngine();

        using var t = dbe.CreateQuickTransaction();
        var positions = new EcsPosition[] { new(1, 0, 0), new(2, 0, 0), new(3, 0, 0) };
        var velocities = new EcsVelocity[] { new(0, 1, 0), new(0, 2, 0), new(0, 3, 0) };

        var ids = EcsUnit.SpawnBatch(t, positions, velocities);

        // Kill the first one
        t.Destroy(ids[0]);

        // DestroyBatch all — first is already dead, should not error
        Assert.DoesNotThrow(() => t.DestroyBatch(ids));

        for (int i = 0; i < ids.Length; i++)
        {
            Assert.That(t.TryOpen(ids[i], out _), Is.False, $"Entity {i} should be destroyed");
        }
    }
}
