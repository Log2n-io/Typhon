using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;

namespace Typhon.Engine.Tests;

/// <summary>
/// Tests for OR-mode EcsView (WhereField with OR predicates producing multiple DNF branches).
/// Uses CompF: [Index(AllowMultiple)] int Gold, [Index] int Rank
/// </summary>
class EcsOrViewTests : TestBase<EcsOrViewTests>
{
    [OneTimeSetUp]
    public void OneTimeSetup()
    {
    }

    private DatabaseEngine SetupEngine()
    {
        var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<CompF>();
        dbe.InitializeArchetypes();
        return dbe;
    }

    // ═══════════════════════════════════════════════════════════════════════
    // OR predicate initial population
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public void OrView_InitialPopulation_UnionOfBranches()
    {
        using var dbe = SetupEngine();

        using var tx = dbe.CreateQuickTransaction();
        tx.Spawn<CompFArch>(CompFArch.F.Set(new CompF(100, 1)));  // Gold=100, Rank=1 → Gold>=90 passes
        tx.Spawn<CompFArch>(CompFArch.F.Set(new CompF(10, 5)));   // Gold=10,  Rank=5 → Rank>=5 passes
        tx.Spawn<CompFArch>(CompFArch.F.Set(new CompF(50, 3)));   // Gold=50,  Rank=3 → neither passes
        tx.Spawn<CompFArch>(CompFArch.F.Set(new CompF(95, 6)));   // Gold=95,  Rank=6 → both pass
        tx.Commit();

        using var tx2 = dbe.CreateQuickTransaction();
        // OR: Gold >= 90 || Rank >= 5
        using var view = tx2.Query<CompFArch>()
            .WhereField<CompF>(f => f.Gold >= 90 || f.Rank >= 5)
            .ToView();

        Assert.That(view.Count, Is.EqualTo(3), "Entities matching Gold>=90 OR Rank>=5");
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Incremental refresh — branch boundary crossing
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public void OrView_FieldCrossesIn_OneBranch_EntityEntersView()
    {
        using var dbe = SetupEngine();

        // Gold=50, Rank=2 → neither branch passes
        EntityId id;
        using (var tx = dbe.CreateQuickTransaction())
        {
            id = tx.Spawn<CompFArch>(CompFArch.F.Set(new CompF(50, 2)));
            tx.Commit();
        }

        using var txView = dbe.CreateQuickTransaction();
        using var view = txView.Query<CompFArch>()
            .WhereField<CompF>(f => f.Gold >= 90 || f.Rank >= 5)
            .ToView();
        Assert.That(view.Count, Is.EqualTo(0));

        // Update Rank to 5 → Rank>=5 branch now passes
        using (var tx2 = dbe.CreateQuickTransaction())
        {
            tx2.OpenMut(id).Write(CompFArch.F).Rank = 5;
            tx2.Commit();
        }

        using var txR = dbe.CreateQuickTransaction();
        view.Refresh(txR);

        Assert.That(view.Count, Is.EqualTo(1));
        Assert.That(view.Added, Has.Count.EqualTo(1));
        Assert.That(view.Added[0], Is.EqualTo(id));
    }

    [Test]
    public void OrView_FieldCrossesOut_LastBranch_EntityLeavesView()
    {
        using var dbe = SetupEngine();

        // Gold=50, Rank=5 → only Rank>=5 passes
        EntityId id;
        using (var tx = dbe.CreateQuickTransaction())
        {
            id = tx.Spawn<CompFArch>(CompFArch.F.Set(new CompF(50, 5)));
            tx.Commit();
        }

        using var txView = dbe.CreateQuickTransaction();
        using var view = txView.Query<CompFArch>()
            .WhereField<CompF>(f => f.Gold >= 90 || f.Rank >= 5)
            .ToView();
        Assert.That(view.Count, Is.EqualTo(1));

        // Update Rank to 2 → Rank>=5 no longer passes, Gold still 50 (< 90) → no branch passes
        using (var tx2 = dbe.CreateQuickTransaction())
        {
            tx2.OpenMut(id).Write(CompFArch.F).Rank = 2;
            tx2.Commit();
        }

        using var txR = dbe.CreateQuickTransaction();
        view.Refresh(txR);

        Assert.That(view.Count, Is.EqualTo(0));
        Assert.That(view.Removed, Has.Count.EqualTo(1));
    }

    [Test]
    public void OrView_OneBranchFails_OtherKeepsEntity()
    {
        using var dbe = SetupEngine();

        // Gold=100, Rank=5 → both branches pass
        EntityId id;
        using (var tx = dbe.CreateQuickTransaction())
        {
            id = tx.Spawn<CompFArch>(CompFArch.F.Set(new CompF(100, 5)));
            tx.Commit();
        }

        using var txView = dbe.CreateQuickTransaction();
        using var view = txView.Query<CompFArch>()
            .WhereField<CompF>(f => f.Gold >= 90 || f.Rank >= 5)
            .ToView();
        Assert.That(view.Count, Is.EqualTo(1));

        // Update Rank to 2 → Rank>=5 fails, but Gold>=90 still passes → entity stays in view
        using (var tx2 = dbe.CreateQuickTransaction())
        {
            tx2.OpenMut(id).Write(CompFArch.F).Rank = 2;
            tx2.Commit();
        }

        using var txR = dbe.CreateQuickTransaction();
        view.Refresh(txR);

        Assert.That(view.Count, Is.EqualTo(1), "Entity should stay — Gold>=90 branch still passes");
        Assert.That(view.Contains(id), Is.True);
        Assert.That(view.Removed, Has.Count.EqualTo(0));
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Creation and deletion in OR mode
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public void OrView_NewEntityMatchesOneBranch_EntersView()
    {
        using var dbe = SetupEngine();

        using (var tx = dbe.CreateQuickTransaction())
        {
            tx.Spawn<CompFArch>(CompFArch.F.Set(new CompF(100, 1)));
            tx.Commit();
        }

        using var txView = dbe.CreateQuickTransaction();
        using var view = txView.Query<CompFArch>()
            .WhereField<CompF>(f => f.Gold >= 90 || f.Rank >= 5)
            .ToView();
        Assert.That(view.Count, Is.EqualTo(1));

        // Spawn entity matching only the Rank branch
        EntityId newId;
        using (var tx2 = dbe.CreateQuickTransaction())
        {
            newId = tx2.Spawn<CompFArch>(CompFArch.F.Set(new CompF(10, 7)));
            tx2.Commit();
        }

        using var txR = dbe.CreateQuickTransaction();
        view.Refresh(txR);

        Assert.That(view.Count, Is.EqualTo(2));
        Assert.That(view.Contains(newId), Is.True);
    }

    [Test]
    public void OrView_DeleteEntity_LeavesView()
    {
        using var dbe = SetupEngine();

        EntityId id;
        using (var tx = dbe.CreateQuickTransaction())
        {
            id = tx.Spawn<CompFArch>(CompFArch.F.Set(new CompF(100, 5)));
            tx.Commit();
        }

        using var txView = dbe.CreateQuickTransaction();
        using var view = txView.Query<CompFArch>()
            .WhereField<CompF>(f => f.Gold >= 90 || f.Rank >= 5)
            .ToView();
        Assert.That(view.Count, Is.EqualTo(1));

        using (var tx2 = dbe.CreateQuickTransaction())
        {
            tx2.Destroy(id);
            tx2.Commit();
        }

        using var txR = dbe.CreateQuickTransaction();
        view.Refresh(txR);

        Assert.That(view.Count, Is.EqualTo(0));
        Assert.That(view.Removed, Has.Count.EqualTo(1));
    }
}
