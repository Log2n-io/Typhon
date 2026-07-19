using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;

namespace Typhon.Engine.Tests;

[NonParallelizable]
class EntityDestroyTests : TestBase<EntityDestroyTests>
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
    public void Destroy_CommittedEntity_BecomesInvisible()
    {
        using var dbe = SetupEngine();

        EntityId id;
        using (var t = dbe.CreateQuickTransaction())
        {
            var pos = new EcsPosition(1, 2, 3);
            var vel = new EcsVelocity(0, 0, 0);
            id = t.Spawn<EcsUnit>(EcsUnit.Position.Set(in pos), EcsUnit.Velocity.Set(in vel));
            t.Commit();
        }

        // Verify alive
        using (var t = dbe.CreateQuickTransaction())
        {
            Assert.That(t.IsAlive(id), Is.True);
        }

        // Destroy
        using (var t = dbe.CreateQuickTransaction())
        {
            t.Destroy(id);
            t.Commit();
        }

        // Should be invisible to new transactions
        using (var t = dbe.CreateQuickTransaction())
        {
            Assert.That(t.IsAlive(id), Is.False);
            Assert.That(t.TryOpen(id, out _), Is.False);
        }
    }

    [Test]
    public void Destroy_PendingInSameTransaction_BecomesInvisible()
    {
        using var dbe = SetupEngine();

        using var t = dbe.CreateQuickTransaction();
        var pos = new EcsPosition(1, 2, 3);
        var vel = new EcsVelocity(0, 0, 0);
        var id = t.Spawn<EcsUnit>(EcsUnit.Position.Set(in pos), EcsUnit.Velocity.Set(in vel));

        Assert.That(t.IsAlive(id), Is.True);

        t.Destroy(id);

        // After destroy in same transaction, entity should not be openable
        Assert.That(t.TryOpen(id, out _), Is.False);
    }

    [Test]
    public void Destroy_EntityLink_Works()
    {
        using var dbe = SetupEngine();

        EntityId id;
        using (var t = dbe.CreateQuickTransaction())
        {
            var pos = new EcsPosition(1, 2, 3);
            var vel = new EcsVelocity(0, 0, 0);
            id = t.Spawn<EcsUnit>(EcsUnit.Position.Set(in pos), EcsUnit.Velocity.Set(in vel));
            t.Commit();
        }

        EntityLink<EcsUnit> link = id;

        using (var t = dbe.CreateQuickTransaction())
        {
            Assert.That(t.IsAlive(link), Is.True);
            t.Destroy(link);
            t.Commit();
        }

        using (var t = dbe.CreateQuickTransaction())
        {
            Assert.That(t.IsAlive(link), Is.False);
        }
    }

    [Test]
    public void Destroy_MultipleEntities_AllBecomesInvisible()
    {
        using var dbe = SetupEngine();

        EntityId id1, id2, id3;
        using (var t = dbe.CreateQuickTransaction())
        {
            var pos = new EcsPosition(0, 0, 0);
            var vel = new EcsVelocity(0, 0, 0);
            id1 = t.Spawn<EcsUnit>(EcsUnit.Position.Set(in pos), EcsUnit.Velocity.Set(in vel));
            id2 = t.Spawn<EcsUnit>(EcsUnit.Position.Set(in pos), EcsUnit.Velocity.Set(in vel));
            id3 = t.Spawn<EcsUnit>(EcsUnit.Position.Set(in pos), EcsUnit.Velocity.Set(in vel));
            t.Commit();
        }

        using (var t = dbe.CreateQuickTransaction())
        {
            t.Destroy(id1);
            t.Destroy(id3);
            t.Commit();
        }

        using (var t = dbe.CreateQuickTransaction())
        {
            Assert.That(t.IsAlive(id1), Is.False);
            Assert.That(t.IsAlive(id2), Is.True);   // not destroyed
            Assert.That(t.IsAlive(id3), Is.False);
        }
    }

    [Test]
    public void Destroy_InheritedArchetype_Works()
    {
        using var dbe = SetupEngine();

        EntityId id;
        using (var t = dbe.CreateQuickTransaction())
        {
            var pos = new EcsPosition(1, 1, 1);
            var vel = new EcsVelocity(0, 0, 0);
            var hp = new EcsHealth(100, 100);
            id = t.Spawn<EcsSoldier>(EcsUnit.Position.Set(in pos), EcsUnit.Velocity.Set(in vel), EcsSoldier.Health.Set(in hp));
            t.Commit();
        }

        using (var t = dbe.CreateQuickTransaction())
        {
            t.Destroy(id);
            t.Commit();
        }

        using (var t = dbe.CreateQuickTransaction())
        {
            Assert.That(t.IsAlive(id), Is.False);
        }
    }
}
