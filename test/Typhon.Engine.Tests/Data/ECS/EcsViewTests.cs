using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;

namespace Typhon.Engine.Tests;

[NonParallelizable]
class EcsViewTests : TestBase<EcsViewTests>
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

    // ── ToView ──

    [Test]
    public void ToView_PopulatesInitialSet()
    {
        using var dbe = SetupEngine();

        using var tx = dbe.CreateQuickTransaction();
        var pos = new EcsPosition(1, 2, 3);
        var vel = new EcsVelocity(4, 5, 6);
        tx.Spawn<EcsUnit>(EcsUnit.Position.Set(in pos), EcsUnit.Velocity.Set(in vel));
        tx.Spawn<EcsUnit>(EcsUnit.Position.Set(in pos), EcsUnit.Velocity.Set(in vel));
        tx.Commit();

        using var tx2 = dbe.CreateQuickTransaction();
        using var view = tx2.Query<EcsUnit>().ToView();

        Assert.That(view.Count, Is.EqualTo(2));
        Assert.That(view.HasChanges, Is.False, "No changes after initial population");
    }

    [Test]
    public void View_Contains_MatchesQuery()
    {
        using var dbe = SetupEngine();

        using var tx = dbe.CreateQuickTransaction();
        var pos = new EcsPosition(1, 2, 3);
        var vel = new EcsVelocity(4, 5, 6);
        var id1 = tx.Spawn<EcsUnit>(EcsUnit.Position.Set(in pos), EcsUnit.Velocity.Set(in vel));
        var id2 = tx.Spawn<EcsUnit>(EcsUnit.Position.Set(in pos), EcsUnit.Velocity.Set(in vel));
        tx.Commit();

        using var tx2 = dbe.CreateQuickTransaction();
        using var view = tx2.Query<EcsUnit>().ToView();

        Assert.That(view.Contains(id1), Is.True);
        Assert.That(view.Contains(id2), Is.True);
    }

    // ── Refresh: detect changes ──

    [Test]
    public void View_Refresh_DetectsNewEntities()
    {
        using var dbe = SetupEngine();

        using var tx = dbe.CreateQuickTransaction();
        var pos = new EcsPosition(1, 2, 3);
        var vel = new EcsVelocity(4, 5, 6);
        tx.Spawn<EcsUnit>(EcsUnit.Position.Set(in pos), EcsUnit.Velocity.Set(in vel));
        tx.Commit();

        using var tx2 = dbe.CreateQuickTransaction();
        using var view = tx2.Query<EcsUnit>().ToView();
        Assert.That(view.Count, Is.EqualTo(1));

        // Spawn a new entity
        using var tx3 = dbe.CreateQuickTransaction();
        var newId = tx3.Spawn<EcsUnit>(EcsUnit.Position.Set(in pos), EcsUnit.Velocity.Set(in vel));
        tx3.Commit();

        // Refresh should detect the new entity
        using var tx4 = dbe.CreateQuickTransaction();
        view.Refresh(tx4);

        Assert.That(view.Count, Is.EqualTo(2));
        Assert.That(view.Added, Has.Count.EqualTo(1));
        Assert.That(view.Added[0], Is.EqualTo(newId));
        Assert.That(view.Removed, Has.Count.EqualTo(0));
    }

    [Test]
    public void View_Refresh_DetectsRemovedEntities()
    {
        using var dbe = SetupEngine();

        EntityId id1, id2;
        using (var tx = dbe.CreateQuickTransaction())
        {
            var pos = new EcsPosition(1, 2, 3);
            var vel = new EcsVelocity(4, 5, 6);
            id1 = tx.Spawn<EcsUnit>(EcsUnit.Position.Set(in pos), EcsUnit.Velocity.Set(in vel));
            id2 = tx.Spawn<EcsUnit>(EcsUnit.Position.Set(in pos), EcsUnit.Velocity.Set(in vel));
            tx.Commit();
        }

        using var txView = dbe.CreateQuickTransaction();
        using var view = txView.Query<EcsUnit>().ToView();
        Assert.That(view.Count, Is.EqualTo(2));

        // Destroy one entity
        using (var txDel = dbe.CreateQuickTransaction())
        {
            txDel.Destroy(id2);
            txDel.Commit();
        }

        // Refresh should detect the removal
        using var txRefresh = dbe.CreateQuickTransaction();
        view.Refresh(txRefresh);

        Assert.That(view.Count, Is.EqualTo(1));
        Assert.That(view.Removed, Has.Count.EqualTo(1));
        Assert.That(view.Removed[0], Is.EqualTo(id2));
        Assert.That(view.Added, Has.Count.EqualTo(0));
    }

    [Test]
    public void View_Refresh_DetectsEnableDisable()
    {
        using var dbe = SetupEngine();

        EntityId id1, id2;
        using (var tx = dbe.CreateQuickTransaction())
        {
            var pos = new EcsPosition(1, 2, 3);
            var vel = new EcsVelocity(4, 5, 6);
            id1 = tx.Spawn<EcsUnit>(EcsUnit.Position.Set(in pos), EcsUnit.Velocity.Set(in vel));
            id2 = tx.Spawn<EcsUnit>(EcsUnit.Position.Set(in pos), EcsUnit.Velocity.Set(in vel));
            tx.Commit();
        }

        // Create view with Enabled<EcsVelocity> constraint
        using var txView = dbe.CreateQuickTransaction();
        using var view = txView.Query<EcsUnit>().Enabled<EcsVelocity>().ToView();
        Assert.That(view.Count, Is.EqualTo(2), "Both entities have Velocity enabled");

        // Disable Velocity on id2
        using (var txDis = dbe.CreateQuickTransaction())
        {
            var entity = txDis.OpenMut(id2);
            entity.Disable(EcsUnit.Velocity);
            txDis.Commit();
        }

        // Refresh — id2 should leave the view
        using var txRefresh = dbe.CreateQuickTransaction();
        view.Refresh(txRefresh);

        Assert.That(view.Count, Is.EqualTo(1));
        Assert.That(view.Contains(id1), Is.True);
        Assert.That(view.Contains(id2), Is.False);
        Assert.That(view.Removed, Has.Count.EqualTo(1));
        Assert.That(view.Removed[0], Is.EqualTo(id2));
    }

    [Test]
    public void View_Refresh_ClearsDelta()
    {
        using var dbe = SetupEngine();

        using (var tx = dbe.CreateQuickTransaction())
        {
            var pos = new EcsPosition(1, 2, 3);
            var vel = new EcsVelocity(4, 5, 6);
            tx.Spawn<EcsUnit>(EcsUnit.Position.Set(in pos), EcsUnit.Velocity.Set(in vel));
            tx.Commit();
        }

        using var txView = dbe.CreateQuickTransaction();
        using var view = txView.Query<EcsUnit>().ToView();

        // Spawn a new entity
        using (var tx2 = dbe.CreateQuickTransaction())
        {
            var pos = new EcsPosition(1, 2, 3);
            var vel = new EcsVelocity(4, 5, 6);
            tx2.Spawn<EcsUnit>(EcsUnit.Position.Set(in pos), EcsUnit.Velocity.Set(in vel));
            tx2.Commit();
        }

        using var txR1 = dbe.CreateQuickTransaction();
        view.Refresh(txR1);
        Assert.That(view.HasChanges, Is.True);

        // Second refresh with no new changes — delta should be empty
        using var txR2 = dbe.CreateQuickTransaction();
        view.Refresh(txR2);
        Assert.That(view.HasChanges, Is.False, "No changes between refreshes → empty delta");
    }

    // ── Polymorphic views ──

    [Test]
    public void View_Polymorphic_IncludesSubtree()
    {
        using var dbe = SetupEngine();

        using var tx = dbe.CreateQuickTransaction();
        var pos = new EcsPosition(1, 2, 3);
        var vel = new EcsVelocity(4, 5, 6);
        tx.Spawn<EcsUnit>(EcsUnit.Position.Set(in pos), EcsUnit.Velocity.Set(in vel));

        var hp = new EcsHealth(100, 100);
        tx.Spawn<EcsSoldier>(EcsSoldier.Health.Set(in hp), EcsUnit.Position.Set(in pos), EcsUnit.Velocity.Set(in vel));
        tx.Commit();

        using var tx2 = dbe.CreateQuickTransaction();
        using var view = tx2.Query<EcsUnit>().ToView();

        Assert.That(view.Count, Is.EqualTo(2), "View<EcsUnit> includes EcsUnit + EcsSoldier");
    }

    [Test]
    public void View_With_FiltersComponents()
    {
        using var dbe = SetupEngine();

        using var tx = dbe.CreateQuickTransaction();
        var pos = new EcsPosition(1, 2, 3);
        var vel = new EcsVelocity(4, 5, 6);
        tx.Spawn<EcsUnit>(EcsUnit.Position.Set(in pos), EcsUnit.Velocity.Set(in vel));

        var hp = new EcsHealth(100, 100);
        tx.Spawn<EcsSoldier>(EcsSoldier.Health.Set(in hp), EcsUnit.Position.Set(in pos), EcsUnit.Velocity.Set(in vel));
        tx.Commit();

        using var tx2 = dbe.CreateQuickTransaction();
        using var view = tx2.Query<EcsUnit>().With<EcsHealth>().ToView();

        Assert.That(view.Count, Is.EqualTo(1), "Only EcsSoldier has EcsHealth");
    }

    [Test]
    public void View_Count_MatchesSetSize()
    {
        using var dbe = SetupEngine();

        using var tx = dbe.CreateQuickTransaction();
        var pos = new EcsPosition(1, 2, 3);
        var vel = new EcsVelocity(4, 5, 6);
        for (int i = 0; i < 10; i++)
        {
            tx.Spawn<EcsUnit>(EcsUnit.Position.Set(in pos), EcsUnit.Velocity.Set(in vel));
        }
        tx.Commit();

        using var tx2 = dbe.CreateQuickTransaction();
        using var view = tx2.Query<EcsUnit>().ToView();
        Assert.That(view.Count, Is.EqualTo(10));
    }

    [Test]
    public void View_Dispose_ClearsState()
    {
        using var dbe = SetupEngine();

        using var tx = dbe.CreateQuickTransaction();
        var pos = new EcsPosition(1, 2, 3);
        var vel = new EcsVelocity(4, 5, 6);
        tx.Spawn<EcsUnit>(EcsUnit.Position.Set(in pos), EcsUnit.Velocity.Set(in vel));
        tx.Commit();

        using var tx2 = dbe.CreateQuickTransaction();
        var view = tx2.Query<EcsUnit>().ToView();
        Assert.That(view.Count, Is.EqualTo(1));

        view.Dispose();
        Assert.That(view.IsDisposed, Is.True);
        Assert.That(view.Count, Is.EqualTo(0));
    }

    // ── Multi-cycle refresh ──

    [Test]
    public void View_MultipleRefresh_TracksChanges()
    {
        using var dbe = SetupEngine();
        var pos = new EcsPosition(1, 2, 3);
        var vel = new EcsVelocity(4, 5, 6);

        // Cycle 1: spawn 2 entities
        EntityId id1, id2;
        using (var tx = dbe.CreateQuickTransaction())
        {
            id1 = tx.Spawn<EcsUnit>(EcsUnit.Position.Set(in pos), EcsUnit.Velocity.Set(in vel));
            id2 = tx.Spawn<EcsUnit>(EcsUnit.Position.Set(in pos), EcsUnit.Velocity.Set(in vel));
            tx.Commit();
        }

        using var txView = dbe.CreateQuickTransaction();
        using var view = txView.Query<EcsUnit>().ToView();
        Assert.That(view.Count, Is.EqualTo(2));

        // Cycle 2: add 1, remove 1
        EntityId id3;
        using (var tx = dbe.CreateQuickTransaction())
        {
            id3 = tx.Spawn<EcsUnit>(EcsUnit.Position.Set(in pos), EcsUnit.Velocity.Set(in vel));
            tx.Destroy(id1);
            tx.Commit();
        }

        using var txR1 = dbe.CreateQuickTransaction();
        view.Refresh(txR1);
        Assert.That(view.Count, Is.EqualTo(2)); // id2 + id3
        Assert.That(view.Added, Has.Count.EqualTo(1));
        Assert.That(view.Removed, Has.Count.EqualTo(1));
        Assert.That(view.Contains(id1), Is.False);
        Assert.That(view.Contains(id3), Is.True);

        // Cycle 3: no changes
        using var txR2 = dbe.CreateQuickTransaction();
        view.Refresh(txR2);
        Assert.That(view.HasChanges, Is.False);
        Assert.That(view.Count, Is.EqualTo(2));
    }
}
