using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;

namespace Typhon.Engine.Tests;

/// <summary>
/// Tests for multi-field predicates on EcsView (ported from ViewTests multi-field section).
/// Uses CompD: [Index(AllowMultiple)] float A, [Index] int B, [Index(AllowMultiple)] double C.
/// Predicate: A > 3.0f AND B > 40 (two indexed fields, both must pass).
/// </summary>
[NonParallelizable]
class EcsViewMultiFieldTests : TestBase<EcsViewMultiFieldTests>
{
    private DatabaseEngine SetupEngine()
    {
        var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<CompD>();
        dbe.InitializeArchetypes();
        return dbe;
    }

    private static EntityId CreateD(DatabaseEngine dbe, float a, int b, double c = 2.0)
    {
        using var tx = dbe.CreateQuickTransaction();
        var d = new CompD(a, b, c);
        var id = tx.Spawn<CompDArch>(CompDArch.D.Set(in d));
        tx.Commit();
        return id;
    }

    private static void UpdateD(DatabaseEngine dbe, EntityId id, float a, int b, double c = 2.0)
    {
        using var tx = dbe.CreateQuickTransaction();
        ref var w = ref tx.OpenMut(id).Write(CompDArch.D);
        w = new CompD(a, b, c);
        tx.Commit();
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Multi-field: A > 3.0 AND B > 40
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public void MultiField_BothPass_EntityInView()
    {
        using var dbe = SetupEngine();

        // A=5.0 > 3.0 AND B=50 > 40 → in view
        var id = CreateD(dbe, 5.0f, 50);

        using var txView = dbe.CreateQuickTransaction();
        using var view = txView.Query<CompDArch>()
            .WhereField<CompD>(d => d.A > 3.0f && d.B > 40)
            .ToView();

        Assert.That(view.Count, Is.EqualTo(1));
        Assert.That(view.Contains(id), Is.True);
    }

    [Test]
    public void MultiField_OneFieldFails_NotInView()
    {
        using var dbe = SetupEngine();

        // A=5.0 > 3.0 (pass) BUT B=30 <= 40 (fail) → not in view
        CreateD(dbe, 5.0f, 30);

        using var txView = dbe.CreateQuickTransaction();
        using var view = txView.Query<CompDArch>()
            .WhereField<CompD>(d => d.A > 3.0f && d.B > 40)
            .ToView();

        Assert.That(view.Count, Is.EqualTo(0));
    }

    [Test]
    public void MultiField_FieldCrossesIn_OtherPasses_Added()
    {
        using var dbe = SetupEngine();

        // A=5.0 (pass), B=30 (fail) → not in view
        var id = CreateD(dbe, 5.0f, 30);

        using var txView = dbe.CreateQuickTransaction();
        using var view = txView.Query<CompDArch>()
            .WhereField<CompD>(d => d.A > 3.0f && d.B > 40)
            .ToView();
        Assert.That(view.Count, Is.EqualTo(0));

        // Update B to 50 → B crosses IN, A still passes via CheckOtherFields → Added
        UpdateD(dbe, id, 5.0f, 50);

        using var txR = dbe.CreateQuickTransaction();
        view.Refresh(txR);

        Assert.That(view.Count, Is.EqualTo(1));
        Assert.That(view.Added, Has.Count.EqualTo(1));
        Assert.That(view.Added[0], Is.EqualTo(id));
    }

    [Test]
    public void MultiField_FieldCrossesIn_OtherFails_NotAdded()
    {
        using var dbe = SetupEngine();

        // A=2.0 (fail), B=30 (fail) → not in view
        var id = CreateD(dbe, 2.0f, 30);

        using var txView = dbe.CreateQuickTransaction();
        using var view = txView.Query<CompDArch>()
            .WhereField<CompD>(d => d.A > 3.0f && d.B > 40)
            .ToView();
        Assert.That(view.Count, Is.EqualTo(0));

        // Update B to 50 → B crosses IN, but A=2.0 still fails → NOT added
        UpdateD(dbe, id, 2.0f, 50);

        using var txR = dbe.CreateQuickTransaction();
        view.Refresh(txR);

        Assert.That(view.Count, Is.EqualTo(0), "A=2.0 fails, so entity should not enter view");
        Assert.That(view.HasChanges, Is.False);
    }

    [Test]
    public void MultiField_FieldCrossesOut_Removed()
    {
        using var dbe = SetupEngine();

        // Both pass: A=5.0, B=50 → in view
        var id = CreateD(dbe, 5.0f, 50);

        using var txView = dbe.CreateQuickTransaction();
        using var view = txView.Query<CompDArch>()
            .WhereField<CompD>(d => d.A > 3.0f && d.B > 40)
            .ToView();
        Assert.That(view.Count, Is.EqualTo(1));

        // Update B to 30 → B crosses OUT → Removed (regardless of A)
        UpdateD(dbe, id, 5.0f, 30);

        using var txR = dbe.CreateQuickTransaction();
        view.Refresh(txR);

        Assert.That(view.Count, Is.EqualTo(0));
        Assert.That(view.Removed, Has.Count.EqualTo(1));
    }

    [Test]
    public void MultiField_BothChangeInSameCommit_CorrectResult()
    {
        using var dbe = SetupEngine();

        // Both fail: A=2.0, B=30
        var id = CreateD(dbe, 2.0f, 30);

        using var txView = dbe.CreateQuickTransaction();
        using var view = txView.Query<CompDArch>()
            .WhereField<CompD>(d => d.A > 3.0f && d.B > 40)
            .ToView();
        Assert.That(view.Count, Is.EqualTo(0));

        // Update both to passing: A=5.0, B=50 → should enter view
        UpdateD(dbe, id, 5.0f, 50);

        using var txR = dbe.CreateQuickTransaction();
        view.Refresh(txR);

        Assert.That(view.Count, Is.EqualTo(1));
        Assert.That(view.Contains(id), Is.True);
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Full CRUD cycle (ported from ViewTests.Integration_FullCRUDCycle)
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public void FullCRUDCycle_EcsView()
    {
        using var dbe = SetupEngine();

        var idMatch = CreateD(dbe, 1.0f, 50);      // B=50 → matches B >= 50
        var idNoMatch = CreateD(dbe, 1.0f, 30);    // B=30 → doesn't match

        using var txView = dbe.CreateQuickTransaction();
        using var view = txView.Query<CompDArch>()
            .WhereField<CompD>(d => d.B >= 50)
            .ToView();

        Assert.That(view.Count, Is.EqualTo(1));
        Assert.That(view.Contains(idMatch), Is.True);

        // Update: idNoMatch enters view, idMatch stays
        UpdateD(dbe, idNoMatch, 1.0f, 55);
        UpdateD(dbe, idMatch, 1.0f, 60);

        using var txR1 = dbe.CreateQuickTransaction();
        view.Refresh(txR1);

        Assert.That(view.Count, Is.EqualTo(2));
        Assert.That(view.Added, Has.Count.EqualTo(1));
        Assert.That(view.Added[0], Is.EqualTo(idNoMatch));

        // Delete idMatch → leaves view
        using (var txDel = dbe.CreateQuickTransaction())
        {
            txDel.Destroy(idMatch);
            txDel.Commit();
        }

        using var txR2 = dbe.CreateQuickTransaction();
        view.Refresh(txR2);

        Assert.That(view.Count, Is.EqualTo(1));
        Assert.That(view.Contains(idMatch), Is.False);
        Assert.That(view.Contains(idNoMatch), Is.True);
        Assert.That(view.Removed, Has.Count.EqualTo(1));
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Game loop pattern (ported from ViewTests.Integration_GameLoopPattern)
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public void GameLoopPattern_EcsView()
    {
        using var dbe = SetupEngine();

        var id = CreateD(dbe, 1.0f, 50);

        using var txView = dbe.CreateQuickTransaction();
        using var view = txView.Query<CompDArch>()
            .WhereField<CompD>(d => d.B > 40)
            .ToView();

        // Frame 1: entity already in view from initial population
        Assert.That(view.Count, Is.EqualTo(1));

        // Frame 2: update B stays in view
        UpdateD(dbe, id, 1.0f, 60);
        using var txR1 = dbe.CreateQuickTransaction();
        view.Refresh(txR1);
        Assert.That(view.Count, Is.EqualTo(1));
        Assert.That(view.Contains(id), Is.True);
        Assert.That(view.Removed, Has.Count.EqualTo(0));
        Assert.That(view.Added, Has.Count.EqualTo(0));

        // Frame 3: no changes
        using var txR2 = dbe.CreateQuickTransaction();
        view.Refresh(txR2);
        Assert.That(view.HasChanges, Is.False);

        // Frame 4: B drops below threshold → leaves view
        UpdateD(dbe, id, 1.0f, 30);
        using var txR3 = dbe.CreateQuickTransaction();
        view.Refresh(txR3);
        Assert.That(view.Count, Is.EqualTo(0));
        Assert.That(view.Removed, Has.Count.EqualTo(1));
    }

    // ═══════════════════════════════════════════════════════════════════════
    // High frequency stress
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public void HighFrequency_ManyCreationsAndRefreshes()
    {
        using var dbe = SetupEngine();

        using var txView = dbe.CreateQuickTransaction();
        using var view = txView.Query<CompDArch>()
            .WhereField<CompD>(d => d.B > 40)
            .ToView();

        var matchingCount = 0;
        for (var i = 0; i < 50; i++)
        {
            var b = (i % 2 == 0) ? 1000 + i : -(1000 + i);
            CreateD(dbe, 1.0f, b);
            if (b > 40) matchingCount++;

            using var txR = dbe.CreateQuickTransaction();
            view.Refresh(txR);
        }

        Assert.That(view.Count, Is.EqualTo(matchingCount));
    }
}
