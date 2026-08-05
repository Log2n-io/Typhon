using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;

namespace Typhon.Engine.Tests;

/// <summary>
/// Incremental-view delta coverage for CLUSTER-BACKED (SingleVersion) archetypes — issue #660.
/// </summary>
/// <remarks>
/// <para>
/// Reuses <c>TbSvData</c> / <c>TbSvArch</c> (declared in <c>TickBoundaryIndexTests.cs</c>): a SingleVersion component with an
/// <c>[Index(AllowMultiple = true)]</c> field, which makes its archetype cluster-eligible.
/// </para>
/// <para>
/// Every view test in the suite before this one used a Versioned archetype, so the whole cluster notification path was unexercised.
/// It published a bare 48-bit <c>EntityKey</c> where consumers reconstruct an <c>EntityId</c> and mask-test on its routing id, so
/// every delta was silently discarded and a cluster-backed view never changed.
/// </para>
/// <para>
/// These tests deliberately create the view BEFORE the entities exist and drive it purely through deltas: initial *population* of an
/// incremental view is separately broken for cluster archetypes (it scans the per-ComponentTable index, which is empty for them —
/// issue #663). Once #663 lands, these should be extended to assert correct population too.
/// </para>
/// </remarks>
[TestFixture]
class ClusterViewDeltaTests : TestBase<ClusterViewDeltaTests>
{
    private DatabaseEngine SetupEngine()
    {
        var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<TbSvData>();
        dbe.InitializeArchetypes();
        return dbe;
    }

    [Test]
    public void ClusterView_SpawnDelta_EntersView()
    {
        using var dbe = SetupEngine();
        Assert.That(Archetype<TbSvArch>.Metadata.HasClusterIndexes, Is.True, "fixture must be cluster-backed for this test to mean anything");

        using var txView = dbe.CreateQuickTransaction();
        using var view = txView.Query<TbSvArch>().WhereField<TbSvData>(d => d.Category >= 50).ToView();

        EntityId spawned;
        using (var tx = dbe.CreateQuickTransaction())
        {
            spawned = tx.Spawn<TbSvArch>(TbSvArch.Data.Set(new TbSvData(70, 1)));
            tx.Spawn<TbSvArch>(TbSvArch.Data.Set(new TbSvData(10, 2)));   // below the predicate — must stay out
            tx.Commit();
        }

        using (var txR = dbe.CreateQuickTransaction())
        {
            view.Refresh(txR);
        }

        Assert.That(view.Contains(spawned), Is.True, "spawn delta must carry a full EntityId so the view can admit the entity");
        Assert.That(view.Count, Is.EqualTo(1), "the entity below the predicate must not enter");
    }

    [Test]
    public void ClusterView_FieldChangeDelta_LeavesView()
    {
        using var dbe = SetupEngine();

        using var txView = dbe.CreateQuickTransaction();
        using var view = txView.Query<TbSvArch>().WhereField<TbSvData>(d => d.Category >= 50).ToView();

        EntityId id;
        using (var tx = dbe.CreateQuickTransaction())
        {
            id = tx.Spawn<TbSvArch>(TbSvArch.Data.Set(new TbSvData(70, 1)));
            tx.Commit();
        }
        dbe.WriteTickFence(1);

        using (var txR = dbe.CreateQuickTransaction())
        {
            view.Refresh(txR);
        }
        Assert.That(view.Contains(id), Is.True, "precondition: entity is in the view");

        // In-place SV write — the index and view delta are produced at the tick fence, not at commit.
        using (var tx = dbe.CreateQuickTransaction())
        {
            tx.OpenMut(id).Write(TbSvArch.Data) = new TbSvData(10, 1);
            tx.Commit();
        }
        dbe.WriteTickFence(2);

        using (var txR = dbe.CreateQuickTransaction())
        {
            view.Refresh(txR);
        }

        Assert.That(view.Contains(id), Is.False, "Category dropped below the predicate — the fence delta must evict it");
        Assert.That(view.Count, Is.EqualTo(0));
    }

    [Test]
    public void ClusterView_DestroyDelta_LeavesView()
    {
        using var dbe = SetupEngine();

        using var txView = dbe.CreateQuickTransaction();
        using var view = txView.Query<TbSvArch>().WhereField<TbSvData>(d => d.Category >= 50).ToView();

        EntityId id;
        using (var tx = dbe.CreateQuickTransaction())
        {
            id = tx.Spawn<TbSvArch>(TbSvArch.Data.Set(new TbSvData(70, 1)));
            tx.Commit();
        }
        dbe.WriteTickFence(1);

        using (var txR = dbe.CreateQuickTransaction())
        {
            view.Refresh(txR);
        }
        Assert.That(view.Contains(id), Is.True, "precondition: entity is in the view");

        using (var tx = dbe.CreateQuickTransaction())
        {
            tx.Destroy(id);
            tx.Commit();
        }
        dbe.WriteTickFence(2);

        using (var txR = dbe.CreateQuickTransaction())
        {
            view.Refresh(txR);
        }

        Assert.That(view.Contains(id), Is.False, "destroyed entity must leave the view");
        Assert.That(view.Count, Is.EqualTo(0));
    }
}
