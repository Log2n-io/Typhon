using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;

namespace Typhon.Engine.Tests;

[NonParallelizable]
class EcsQueryTests : TestBase<EcsQueryTests>
{
    [OneTimeSetUp]
    public void OneTimeSetup()
    {
        Archetype<EcsUnit>.Touch();
        Archetype<EcsSoldier>.Touch();
        Archetype<CompDArch>.Touch();
    }

    private DatabaseEngine SetupEngine()
    {
        var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<EcsPosition>();
        dbe.RegisterComponentFromAccessor<EcsVelocity>();
        dbe.RegisterComponentFromAccessor<EcsHealth>();
        dbe.RegisterComponentFromAccessor<CompD>();
        dbe.InitializeArchetypes();
        return dbe;
    }

    // ── Tier 1: Polymorphic vs Exact ──

    [Test]
    public void Query_Polymorphic_MatchesSubtree()
    {
        using var dbe = SetupEngine();
        using var tx = dbe.CreateQuickTransaction();

        var pos = new EcsPosition(1, 2, 3);
        var vel = new EcsVelocity(4, 5, 6);
        var unitId = tx.Spawn<EcsUnit>(EcsUnit.Position.Set(in pos), EcsUnit.Velocity.Set(in vel));

        var hp = new EcsHealth(100, 100);
        var soldierId = tx.Spawn<EcsSoldier>(EcsSoldier.Health.Set(in hp), EcsUnit.Position.Set(in pos), EcsUnit.Velocity.Set(in vel));
        tx.Commit();

        using var tx2 = dbe.CreateQuickTransaction();
        var result = tx2.Query<EcsUnit>().Execute();

        // Polymorphic: matches EcsUnit AND EcsSoldier (child)
        Assert.That(result, Has.Count.EqualTo(2));
        Assert.That(result, Does.Contain(unitId));
        Assert.That(result, Does.Contain(soldierId));
    }

    [Test]
    public void QueryExact_MatchesOnlyExact()
    {
        using var dbe = SetupEngine();
        using var tx = dbe.CreateQuickTransaction();

        var pos = new EcsPosition(1, 2, 3);
        var vel = new EcsVelocity(4, 5, 6);
        var unitId = tx.Spawn<EcsUnit>(EcsUnit.Position.Set(in pos), EcsUnit.Velocity.Set(in vel));

        var hp = new EcsHealth(100, 100);
        tx.Spawn<EcsSoldier>(EcsSoldier.Health.Set(in hp), EcsUnit.Position.Set(in pos), EcsUnit.Velocity.Set(in vel));
        tx.Commit();

        using var tx2 = dbe.CreateQuickTransaction();
        var result = tx2.QueryExact<EcsUnit>().Execute();

        // Exact: only EcsUnit, NOT EcsSoldier
        Assert.That(result, Has.Count.EqualTo(1));
        Assert.That(result, Does.Contain(unitId));
    }

    // ── Tier 1: With / Without / Exclude ──

    [Test]
    public void Query_With_FiltersToComponentOwners()
    {
        using var dbe = SetupEngine();
        using var tx = dbe.CreateQuickTransaction();

        var pos = new EcsPosition(1, 2, 3);
        var vel = new EcsVelocity(4, 5, 6);
        tx.Spawn<EcsUnit>(EcsUnit.Position.Set(in pos), EcsUnit.Velocity.Set(in vel));

        var hp = new EcsHealth(100, 100);
        var soldierId = tx.Spawn<EcsSoldier>(EcsSoldier.Health.Set(in hp), EcsUnit.Position.Set(in pos), EcsUnit.Velocity.Set(in vel));
        tx.Commit();

        using var tx2 = dbe.CreateQuickTransaction();
        var result = tx2.Query<EcsUnit>().With<EcsHealth>().Execute();

        // Only EcsSoldier has EcsHealth
        Assert.That(result, Has.Count.EqualTo(1));
        Assert.That(result, Does.Contain(soldierId));
    }

    [Test]
    public void Query_Without_ExcludesComponentOwners()
    {
        using var dbe = SetupEngine();
        using var tx = dbe.CreateQuickTransaction();

        var pos = new EcsPosition(1, 2, 3);
        var vel = new EcsVelocity(4, 5, 6);
        var unitId = tx.Spawn<EcsUnit>(EcsUnit.Position.Set(in pos), EcsUnit.Velocity.Set(in vel));

        var hp = new EcsHealth(100, 100);
        tx.Spawn<EcsSoldier>(EcsSoldier.Health.Set(in hp), EcsUnit.Position.Set(in pos), EcsUnit.Velocity.Set(in vel));
        tx.Commit();

        using var tx2 = dbe.CreateQuickTransaction();
        var result = tx2.Query<EcsUnit>().Without<EcsHealth>().Execute();

        // Only base EcsUnit (no Health)
        Assert.That(result, Has.Count.EqualTo(1));
        Assert.That(result, Does.Contain(unitId));
    }

    [Test]
    public void Query_Exclude_RemovesSubtree()
    {
        using var dbe = SetupEngine();
        using var tx = dbe.CreateQuickTransaction();

        var pos = new EcsPosition(1, 2, 3);
        var vel = new EcsVelocity(4, 5, 6);
        var unitId = tx.Spawn<EcsUnit>(EcsUnit.Position.Set(in pos), EcsUnit.Velocity.Set(in vel));

        var hp = new EcsHealth(100, 100);
        tx.Spawn<EcsSoldier>(EcsSoldier.Health.Set(in hp), EcsUnit.Position.Set(in pos), EcsUnit.Velocity.Set(in vel));
        tx.Commit();

        using var tx2 = dbe.CreateQuickTransaction();
        var result = tx2.Query<EcsUnit>().Exclude<EcsSoldier>().Execute();

        Assert.That(result, Has.Count.EqualTo(1));
        Assert.That(result, Does.Contain(unitId));
    }

    [Test]
    public void Query_Contradiction_ReturnsEmpty()
    {
        using var dbe = SetupEngine();
        using var tx = dbe.CreateQuickTransaction();

        var pos = new EcsPosition(1, 2, 3);
        var vel = new EcsVelocity(4, 5, 6);
        tx.Spawn<EcsUnit>(EcsUnit.Position.Set(in pos), EcsUnit.Velocity.Set(in vel));
        tx.Commit();

        using var tx2 = dbe.CreateQuickTransaction();
        // With + Without same component → empty mask → zero results
        var result = tx2.Query<EcsUnit>().With<EcsHealth>().Without<EcsHealth>().Execute();
        Assert.That(result, Has.Count.EqualTo(0));
    }

    // ── Tier 2: Enabled / Disabled ──

    [Test]
    public void Query_Enabled_FiltersDisabledEntities()
    {
        using var dbe = SetupEngine();

        // Spawn entity, disable Velocity
        using var tx = dbe.CreateQuickTransaction();
        var pos = new EcsPosition(1, 2, 3);
        var vel = new EcsVelocity(4, 5, 6);
        var id1 = tx.Spawn<EcsUnit>(EcsUnit.Position.Set(in pos), EcsUnit.Velocity.Set(in vel));
        var id2 = tx.Spawn<EcsUnit>(EcsUnit.Position.Set(in pos), EcsUnit.Velocity.Set(in vel));

        // Disable Velocity on id2
        var e2 = tx.OpenMut(id2);
        e2.Disable(EcsUnit.Velocity);
        tx.Commit();

        using var tx2 = dbe.CreateQuickTransaction();
        var result = tx2.Query<EcsUnit>().Enabled<EcsVelocity>().Execute();

        // Only id1 has Velocity enabled
        Assert.That(result, Has.Count.EqualTo(1));
        Assert.That(result, Does.Contain(id1));
    }

    [Test]
    public void Query_Disabled_FiltersEnabledEntities()
    {
        using var dbe = SetupEngine();

        using var tx = dbe.CreateQuickTransaction();
        var pos = new EcsPosition(1, 2, 3);
        var vel = new EcsVelocity(4, 5, 6);
        var id1 = tx.Spawn<EcsUnit>(EcsUnit.Position.Set(in pos), EcsUnit.Velocity.Set(in vel));
        var id2 = tx.Spawn<EcsUnit>(EcsUnit.Position.Set(in pos), EcsUnit.Velocity.Set(in vel));

        var e2 = tx.OpenMut(id2);
        e2.Disable(EcsUnit.Velocity);
        tx.Commit();

        using var tx2 = dbe.CreateQuickTransaction();
        var result = tx2.Query<EcsUnit>().Disabled<EcsVelocity>().Execute();

        // Only id2 has Velocity disabled
        Assert.That(result, Has.Count.EqualTo(1));
        Assert.That(result, Does.Contain(id2));
    }

    // ── Execution: Count / Any ──

    [Test]
    public void Count_ReturnsCorrectCount()
    {
        using var dbe = SetupEngine();
        using var tx = dbe.CreateQuickTransaction();

        var pos = new EcsPosition(1, 2, 3);
        var vel = new EcsVelocity(4, 5, 6);
        for (int i = 0; i < 5; i++)
        {
            tx.Spawn<EcsUnit>(EcsUnit.Position.Set(in pos), EcsUnit.Velocity.Set(in vel));
        }
        tx.Commit();

        using var tx2 = dbe.CreateQuickTransaction();
        Assert.That(tx2.Query<EcsUnit>().Count(), Is.EqualTo(5));
    }

    [Test]
    public void Any_ReturnsTrueWhenMatches()
    {
        using var dbe = SetupEngine();
        using var tx = dbe.CreateQuickTransaction();

        var pos = new EcsPosition(1, 2, 3);
        var vel = new EcsVelocity(4, 5, 6);
        tx.Spawn<EcsUnit>(EcsUnit.Position.Set(in pos), EcsUnit.Velocity.Set(in vel));
        tx.Commit();

        using var tx2 = dbe.CreateQuickTransaction();
        Assert.That(tx2.Query<EcsUnit>().Any(), Is.True);
    }

    [Test]
    public void Any_ReturnsFalseWhenEmpty()
    {
        using var dbe = SetupEngine();
        using var tx = dbe.CreateQuickTransaction();

        // Query for EcsSoldier but none spawned
        Assert.That(tx.QueryExact<EcsSoldier>().Any(), Is.False);
    }

    // ── Foreach iteration ──

    [Test]
    public void Foreach_YieldsEntityRefs()
    {
        using var dbe = SetupEngine();
        using var tx = dbe.CreateQuickTransaction();

        var pos = new EcsPosition(10, 20, 30);
        var vel = new EcsVelocity(1, 2, 3);
        tx.Spawn<EcsUnit>(EcsUnit.Position.Set(in pos), EcsUnit.Velocity.Set(in vel));
        tx.Commit();

        using var tx2 = dbe.CreateQuickTransaction();
        int count = 0;
        foreach (var entity in tx2.Query<EcsUnit>())
        {
            ref readonly var p = ref entity.Read(EcsUnit.Position);
            Assert.That(p.X, Is.EqualTo(10f));
            Assert.That(p.Y, Is.EqualTo(20f));
            count++;
        }
        Assert.That(count, Is.EqualTo(1));
    }

    // ── TryRead ──

    [Test]
    public void TryRead_ReturnsFalseForAbsentComponent()
    {
        using var dbe = SetupEngine();
        using var tx = dbe.CreateQuickTransaction();

        var pos = new EcsPosition(1, 2, 3);
        var vel = new EcsVelocity(4, 5, 6);
        var id = tx.Spawn<EcsUnit>(EcsUnit.Position.Set(in pos), EcsUnit.Velocity.Set(in vel));
        tx.Commit();

        using var tx2 = dbe.CreateQuickTransaction();
        var entity = tx2.Open(id);
        // EcsUnit doesn't have EcsHealth
        Assert.That(entity.TryRead<EcsHealth>(out _), Is.False);
    }

    [Test]
    public void TryRead_ReturnsTrueForEnabledComponent()
    {
        using var dbe = SetupEngine();
        using var tx = dbe.CreateQuickTransaction();

        var pos = new EcsPosition(42, 99, 7);
        var vel = new EcsVelocity(1, 2, 3);
        var id = tx.Spawn<EcsUnit>(EcsUnit.Position.Set(in pos), EcsUnit.Velocity.Set(in vel));
        tx.Commit();

        using var tx2 = dbe.CreateQuickTransaction();
        var entity = tx2.Open(id);
        Assert.That(entity.TryRead<EcsPosition>(out var p), Is.True);
        Assert.That(p.X, Is.EqualTo(42f));
    }

    [Test]
    public void TryRead_ReturnsFalseForDisabledComponent()
    {
        using var dbe = SetupEngine();
        using var tx = dbe.CreateQuickTransaction();

        var pos = new EcsPosition(1, 2, 3);
        var vel = new EcsVelocity(4, 5, 6);
        var id = tx.Spawn<EcsUnit>(EcsUnit.Position.Set(in pos), EcsUnit.Velocity.Set(in vel));

        var e = tx.OpenMut(id);
        e.Disable(EcsUnit.Velocity);
        tx.Commit();

        using var tx2 = dbe.CreateQuickTransaction();
        var entity = tx2.Open(id);
        Assert.That(entity.TryRead<EcsVelocity>(out _), Is.False);
    }

    // ── EcsCount (metadata) ──

    [Test]
    public void EcsCount_Polymorphic_SumsSubtree()
    {
        using var dbe = SetupEngine();
        using var tx = dbe.CreateQuickTransaction();

        var pos = new EcsPosition(1, 2, 3);
        var vel = new EcsVelocity(4, 5, 6);
        tx.Spawn<EcsUnit>(EcsUnit.Position.Set(in pos), EcsUnit.Velocity.Set(in vel));
        tx.Spawn<EcsUnit>(EcsUnit.Position.Set(in pos), EcsUnit.Velocity.Set(in vel));

        var hp = new EcsHealth(100, 100);
        tx.Spawn<EcsSoldier>(EcsSoldier.Health.Set(in hp), EcsUnit.Position.Set(in pos), EcsUnit.Velocity.Set(in vel));
        tx.Commit();

        using var tx2 = dbe.CreateQuickTransaction();
        // EcsUnit subtree = 2 units + 1 soldier = 3
        Assert.That(tx2.EcsCount<EcsUnit>(), Is.EqualTo(3));
    }

    // ── Visibility (dead entities) ──

    [Test]
    public void Query_VisibilityCheck_SkipsDeadEntities()
    {
        using var dbe = SetupEngine();

        EntityId aliveId, deadId;
        using (var tx = dbe.CreateQuickTransaction())
        {
            var pos = new EcsPosition(1, 2, 3);
            var vel = new EcsVelocity(4, 5, 6);
            aliveId = tx.Spawn<EcsUnit>(EcsUnit.Position.Set(in pos), EcsUnit.Velocity.Set(in vel));
            deadId = tx.Spawn<EcsUnit>(EcsUnit.Position.Set(in pos), EcsUnit.Velocity.Set(in vel));
            tx.Commit();
        }

        // Destroy one entity
        using (var tx = dbe.CreateQuickTransaction())
        {
            tx.Destroy(deadId);
            tx.Commit();
        }

        // Query should only see alive entity
        using var tx3 = dbe.CreateQuickTransaction();
        var result = tx3.Query<EcsUnit>().Execute();
        Assert.That(result, Has.Count.EqualTo(1));
        Assert.That(result, Does.Contain(aliveId));
    }

    [Test]
    public void WhereField_ExcludesDestroyedEntities()
    {
        using var dbe = SetupEngine();

        EntityId aliveId, deadId;
        using (var tx = dbe.CreateQuickTransaction())
        {
            aliveId = tx.Spawn<CompDArch>(CompDArch.D.Set(new CompD(1.0f, 100, 2.0)));
            deadId = tx.Spawn<CompDArch>(CompDArch.D.Set(new CompD(2.0f, 200, 3.0)));
            tx.Commit();
        }

        // Destroy one entity
        using (var tx = dbe.CreateQuickTransaction())
        {
            tx.Destroy(deadId);
            tx.Commit();
        }

        // Execute with WhereField — destroyed entity must not appear
        using var tx3 = dbe.CreateQuickTransaction();
        var result = tx3.Query<CompDArch>().WhereField<CompD>(d => d.B >= 50).Execute();
        Assert.That(result, Has.Count.EqualTo(1), "Destroyed entity should not appear in WhereField query");
        Assert.That(result, Does.Contain(aliveId));

        // Count should also exclude
        Assert.That(tx3.Query<CompDArch>().WhereField<CompD>(d => d.B >= 50).Count(), Is.EqualTo(1));

        // ExecuteOrdered too
        var ordered = tx3.Query<CompDArch>().WhereField<CompD>(d => d.B >= 50).OrderByField<CompD, int>(d => d.B).ExecuteOrdered();
        Assert.That(ordered, Has.Count.EqualTo(1));
    }

    // Guards: a clause a terminal can't honor must throw, not silently drop (footgun hardening).
    [Test]
    public void QueryGuards_UnsupportedClauseCombosThrow()
    {
        using var dbe = SetupEngine();
        using (var tx = dbe.CreateQuickTransaction())
        {
            tx.Spawn<CompDArch>(CompDArch.D.Set(new CompD(1f, 100, 2.0)));
            tx.Spawn<CompDArch>(CompDArch.D.Set(new CompD(2f, 200, 3.0)));
            tx.Commit();
        }
        using var qtx = dbe.CreateQuickTransaction();

        // foreach ignores WhereField — must throw instead of iterating the whole archetype.
        Assert.Throws<System.InvalidOperationException>(() =>
        {
            foreach (var _ in qtx.Query<CompDArch>().WhereField<CompD>(d => d.B >= 150)) { }
        });

        // Execute ignores OrderBy/Skip/Take — use ExecuteOrdered.
        Assert.Throws<System.InvalidOperationException>(() =>
            qtx.Query<CompDArch>().WhereField<CompD>(d => d.B >= 0).OrderByField<CompD, int>(d => d.B).Execute());

        // Count/Any ignore Skip/Take.
        Assert.Throws<System.InvalidOperationException>(() =>
            qtx.Query<CompDArch>().WhereField<CompD>(d => d.B >= 0).OrderByField<CompD, int>(d => d.B).Take(1).Count());
        Assert.Throws<System.InvalidOperationException>(() =>
            qtx.Query<CompDArch>().WhereField<CompD>(d => d.B >= 0).OrderByField<CompD, int>(d => d.B).Take(1).Any());

        // A View is unordered.
        Assert.Throws<System.InvalidOperationException>(() =>
            qtx.Query<CompDArch>().WhereField<CompD>(d => d.B >= 0).OrderByField<CompD, int>(d => d.B).ToView());

        // An incremental (WhereField) view can't also carry a .Where(lambda).
        Assert.Throws<System.InvalidOperationException>(() =>
            qtx.Query<CompDArch>().WhereField<CompD>(d => d.B >= 0).Where<CompD>(d => d.A > 0f).ToView());

        // Sanity: the supported forms still work.
        Assert.That(qtx.Query<CompDArch>().WhereField<CompD>(d => d.B >= 150).Count(), Is.EqualTo(1));
        Assert.That(
            qtx.Query<CompDArch>().WhereField<CompD>(d => d.B >= 0).OrderByField<CompD, int>(d => d.B).ExecuteOrdered(),
            Has.Count.EqualTo(2));
    }

    // ── WHERE predicates (T3) ──

    [Test]
    public void Where_FiltersByComponentField()
    {
        using var dbe = SetupEngine();
        using var tx = dbe.CreateQuickTransaction();

        var pos1 = new EcsPosition(10, 0, 0);
        var pos2 = new EcsPosition(50, 0, 0);
        var pos3 = new EcsPosition(90, 0, 0);
        var vel = new EcsVelocity(1, 2, 3);
        tx.Spawn<EcsUnit>(EcsUnit.Position.Set(in pos1), EcsUnit.Velocity.Set(in vel));
        var id2 = tx.Spawn<EcsUnit>(EcsUnit.Position.Set(in pos2), EcsUnit.Velocity.Set(in vel));
        var id3 = tx.Spawn<EcsUnit>(EcsUnit.Position.Set(in pos3), EcsUnit.Velocity.Set(in vel));
        tx.Commit();

        using var tx2 = dbe.CreateQuickTransaction();
        var result = tx2.Query<EcsUnit>().Where<EcsPosition>(p => p.X > 30).Execute();

        Assert.That(result, Has.Count.EqualTo(2));
        Assert.That(result, Does.Contain(id2));
        Assert.That(result, Does.Contain(id3));
    }

    [Test]
    public void Where_NoMatches_ReturnsEmpty()
    {
        using var dbe = SetupEngine();
        using var tx = dbe.CreateQuickTransaction();

        var pos = new EcsPosition(10, 0, 0);
        var vel = new EcsVelocity(1, 2, 3);
        tx.Spawn<EcsUnit>(EcsUnit.Position.Set(in pos), EcsUnit.Velocity.Set(in vel));
        tx.Commit();

        using var tx2 = dbe.CreateQuickTransaction();
        var result = tx2.Query<EcsUnit>().Where<EcsPosition>(p => p.X > 1000).Execute();
        Assert.That(result, Has.Count.EqualTo(0));
    }

    [Test]
    public void Where_AllMatch_ReturnsAll()
    {
        using var dbe = SetupEngine();
        using var tx = dbe.CreateQuickTransaction();

        var pos = new EcsPosition(10, 0, 0);
        var vel = new EcsVelocity(1, 2, 3);
        tx.Spawn<EcsUnit>(EcsUnit.Position.Set(in pos), EcsUnit.Velocity.Set(in vel));
        tx.Spawn<EcsUnit>(EcsUnit.Position.Set(in pos), EcsUnit.Velocity.Set(in vel));
        tx.Commit();

        using var tx2 = dbe.CreateQuickTransaction();
        var result = tx2.Query<EcsUnit>().Where<EcsPosition>(p => p.X > 0).Execute();
        Assert.That(result, Has.Count.EqualTo(2));
    }

    [Test]
    public void Where_WithT2_CombinesCorrectly()
    {
        using var dbe = SetupEngine();
        using var tx = dbe.CreateQuickTransaction();

        var pos1 = new EcsPosition(10, 0, 0);
        var pos2 = new EcsPosition(50, 0, 0);
        var vel = new EcsVelocity(1, 2, 3);
        var id1 = tx.Spawn<EcsUnit>(EcsUnit.Position.Set(in pos1), EcsUnit.Velocity.Set(in vel));
        var id2 = tx.Spawn<EcsUnit>(EcsUnit.Position.Set(in pos2), EcsUnit.Velocity.Set(in vel));

        // Disable velocity on id1
        tx.OpenMut(id1).Disable(EcsUnit.Velocity);
        tx.Commit();

        using var tx2 = dbe.CreateQuickTransaction();
        // T2: velocity enabled, T3: position X > 30
        var result = tx2.Query<EcsUnit>()
            .Enabled<EcsVelocity>()
            .Where<EcsPosition>(p => p.X > 30)
            .Execute();

        // id1: velocity disabled (filtered by T2)
        // id2: velocity enabled, X=50 > 30 (passes both)
        Assert.That(result, Has.Count.EqualTo(1));
        Assert.That(result, Does.Contain(id2));
    }

    [Test]
    public void Where_Foreach_FiltersEntities()
    {
        using var dbe = SetupEngine();
        using var tx = dbe.CreateQuickTransaction();

        var pos1 = new EcsPosition(10, 0, 0);
        var pos2 = new EcsPosition(50, 0, 0);
        var vel = new EcsVelocity(1, 2, 3);
        tx.Spawn<EcsUnit>(EcsUnit.Position.Set(in pos1), EcsUnit.Velocity.Set(in vel));
        tx.Spawn<EcsUnit>(EcsUnit.Position.Set(in pos2), EcsUnit.Velocity.Set(in vel));
        tx.Commit();

        using var tx2 = dbe.CreateQuickTransaction();
        int count = 0;
        foreach (var entity in tx2.Query<EcsUnit>().Where<EcsPosition>(p => p.X > 30))
        {
            ref readonly var pos = ref entity.Read(EcsUnit.Position);
            Assert.That(pos.X, Is.GreaterThan(30f));
            count++;
        }
        Assert.That(count, Is.EqualTo(1));
    }

    [Test]
    public void Where_OnView_FiltersSurvivesRefresh()
    {
        using var dbe = SetupEngine();

        using var tx = dbe.CreateQuickTransaction();
        var pos1 = new EcsPosition(10, 0, 0);
        var pos2 = new EcsPosition(50, 0, 0);
        var vel = new EcsVelocity(1, 2, 3);
        tx.Spawn<EcsUnit>(EcsUnit.Position.Set(in pos1), EcsUnit.Velocity.Set(in vel));
        tx.Spawn<EcsUnit>(EcsUnit.Position.Set(in pos2), EcsUnit.Velocity.Set(in vel));
        tx.Commit();

        using var tx2 = dbe.CreateQuickTransaction();
        using var view = tx2.Query<EcsUnit>().Where<EcsPosition>(p => p.X > 30).ToView();
        Assert.That(view.Count, Is.EqualTo(1));

        // Add an entity that matches WHERE
        using var tx3 = dbe.CreateQuickTransaction();
        var pos3 = new EcsPosition(80, 0, 0);
        tx3.Spawn<EcsUnit>(EcsUnit.Position.Set(in pos3), EcsUnit.Velocity.Set(in vel));
        tx3.Commit();

        // Refresh — new entity should appear
        using var tx4 = dbe.CreateQuickTransaction();
        view.Refresh(tx4);
        Assert.That(view.Count, Is.EqualTo(2));
        Assert.That(view.Added, Has.Count.EqualTo(1));
    }
}
