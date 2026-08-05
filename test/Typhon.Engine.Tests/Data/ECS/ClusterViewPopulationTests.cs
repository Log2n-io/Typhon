using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using System.Runtime.InteropServices;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests;

// Pure SingleVersion — cluster-backed, so its Score index lives on the ARCHETYPE and the ComponentTable tree stays empty.
[Component("Typhon.Test.ECS.ViewPop.SvItem", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct VpSvItem
{
    [Index] public int Score;
    public int Rank;

    public VpSvItem(int score, int rank)
    {
        Score = score;
        Rank = rank;
    }
}

[Archetype]
class VpSvArch : Archetype<VpSvArch>
{
    public static readonly Comp<VpSvItem> Item = Register<VpSvItem>();
}

// Versioned component whose archetype is cluster-eligible via an SV sibling — its index moves to the archetype too.
[Component("Typhon.Test.ECS.ViewPop.VerItem", 1, StorageMode = StorageMode.Versioned)]
[StructLayout(LayoutKind.Sequential)]
struct VpVerItem
{
    [Index] public int Level;

    // A chunk-based segment needs a stride of at least 8 bytes, and a component's size comes from its PUBLIC fields.
    public int Pad;

    public VpVerItem(int level)
    {
        Level = level;
        Pad = 0;
    }
}

[Component("Typhon.Test.ECS.ViewPop.SvTag", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct VpSvTag
{
    public int Marker;
    public int Pad;

    public VpSvTag(int marker)
    {
        Marker = marker;
        Pad = 0;
    }
}

[Archetype]
class VpMixedArch : Archetype<VpMixedArch>
{
    public static readonly Comp<VpVerItem> Item = Register<VpVerItem>();
    public static readonly Comp<VpSvTag> Tag = Register<VpSvTag>();
}

// A Versioned component held by TWO archetypes. Both are pure-Versioned, so they share ONE per-ComponentTable index — the case where a view must filter
// results by archetype rather than take everything the shared tree returns.
[Component("Typhon.Test.ECS.ViewPop.VerShared", 1, StorageMode = StorageMode.Versioned)]
[StructLayout(LayoutKind.Sequential)]
struct VpVerShared
{
    // AllowMultiple because the test deliberately stores the same key in two archetypes — the per-ComponentTable index is shared across them, so a unique
    // index would reject the second spawn outright.
    [Index(AllowMultiple = true)] public int Key;
    public int Pad;

    public VpVerShared(int key)
    {
        Key = key;
        Pad = 0;
    }
}

[Archetype]
class VpSharedArchA : Archetype<VpSharedArchA>
{
    public static readonly Comp<VpVerShared> Shared = Register<VpVerShared>();
}

[Archetype]
class VpSharedArchB : Archetype<VpSharedArchB>
{
    public static readonly Comp<VpVerShared> Shared = Register<VpVerShared>();
}

/// <summary>
/// View population across both secondary-index homes — issue #663.
/// </summary>
/// <remarks>
/// <para>
/// <c>ExecuteFullScan</c> had five call sites and exactly one — inside <c>EcsQuery.ExecuteTargeted</c> — knew that a cluster-backed archetype keeps its
/// field indexes on the archetype rather than on the ComponentTable. The other four handed the ComponentTable straight to the pipeline, scanning a tree that
/// is empty for such an archetype. The visible symptom: <c>Execute()</c> returned the right answer while <c>ToView()</c> on the identical query returned a
/// view that was empty at population and stayed empty through every refresh.
/// </para>
/// <para>
/// Assertions go through the view's own <c>Count</c> / <c>Contains</c> — that is the surface that was silently empty — and are cross-checked against
/// <c>Execute()</c> on the same predicate, which is the oracle that always worked.
/// </para>
/// </remarks>
[TestFixture]
class ClusterViewPopulationTests : TestBase<ClusterViewPopulationTests>
{
    private DatabaseEngine SetupEngine()
    {
        var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<VpSvItem>();
        dbe.RegisterComponentFromAccessor<VpVerItem>();
        dbe.RegisterComponentFromAccessor<VpSvTag>();
        dbe.RegisterComponentFromAccessor<VpVerShared>();
        dbe.InitializeArchetypes();
        return dbe;
    }

    /// <summary>Guards the premise: if the fixture archetypes are not cluster-backed, none of these tests exercise #663.</summary>
    /// <remarks>
    /// <c>VpSharedArchA</c> asserted the ComponentTable home until the eligibility flip (#629). It is cluster-backed now like everything else, which makes the
    /// shared-component case MORE interesting rather than less: the component is held by two archetypes, so it has two per-archetype trees and no shared one,
    /// which is precisely the shape #663 was about.
    /// </remarks>
    [Test]
    public void Fixture_ArchetypesAreClusterBacked()
    {
        using var dbe = SetupEngine();

        Assert.That(Archetype<VpSvArch>.Metadata.HasClusterIndexes, Is.True, "the pure-SV fixture must be cluster-backed");
        Assert.That(Archetype<VpMixedArch>.Metadata.HasClusterIndexes, Is.True, "the mixed fixture must be cluster-backed");
        Assert.That(Archetype<VpSharedArchA>.Metadata.HasClusterIndexes, Is.True, "the shared-component fixture is cluster-backed too — there is one home now");
    }

    [Test]
    public void SvArchetype_ToView_PopulatesFromClusterIndex()
    {
        using var dbe = SetupEngine();

        using (var tx = dbe.CreateQuickTransaction())
        {
            for (var i = 0; i < 20; i++)
            {
                tx.Spawn<VpSvArch>(VpSvArch.Item.Set(new VpSvItem(i, i * 2)));
            }
            tx.Commit();
        }
        dbe.WriteTickFence(1);

        using var tx2 = dbe.CreateQuickTransaction();
        var expected = tx2.Query<VpSvArch>().WhereField<VpSvItem>(d => d.Score >= 15).Execute();
        using var view = tx2.Query<VpSvArch>().WhereField<VpSvItem>(d => d.Score >= 15).ToView();

        Assert.That(expected.Count, Is.EqualTo(5), "oracle sanity: Execute() must find scores 15..19");
        Assert.That(view.Count, Is.EqualTo(expected.Count), "the view must populate to the same set Execute() returns");
        foreach (var id in expected)
        {
            Assert.That(view.Contains((long)id.RawValue), Is.True, $"entity {id} is in Execute()'s result but missing from the view");
        }
    }

    [Test]
    public void SvArchetype_OrView_PopulatesEveryBranch()
    {
        using var dbe = SetupEngine();

        var ids = new EntityId[20];
        using (var tx = dbe.CreateQuickTransaction())
        {
            for (var i = 0; i < ids.Length; i++)
            {
                ids[i] = tx.Spawn<VpSvArch>(VpSvArch.Item.Set(new VpSvItem(i, i)));
            }
            tx.Commit();
        }
        dbe.WriteTickFence(1);

        using var tx2 = dbe.CreateQuickTransaction();
        using var view = tx2.Query<VpSvArch>().WhereField<VpSvItem>(d => d.Score <= 2 || d.Score >= 18).ToView();

        // Execute() cannot be the oracle here — it rejects OR predicates outright (it would evaluate only the first DNF branch), which is precisely why
        // ToView() is the only way to run this query and why an OR view populating to nothing had no working alternative.
        // Both branches range over Score because view predicates only accept INDEXED fields, and the defect was per-BRANCH population (every branch came
        // back empty), not per-field — so two ranges on one index exercise it exactly as well as two fields would.
        // Scores 0,1,2 satisfy the first branch; 18,19 the second. Disjoint, so a dropped branch shows up in the count.
        Assert.That(view.Count, Is.EqualTo(5), "both OR branches must populate");
        foreach (var i in new[] { 0, 1, 2, 18, 19 })
        {
            Assert.That(view.Contains((long)ids[i].RawValue), Is.True, $"entity {i} missing from the OR view");
        }
        foreach (var i in new[] { 3, 10, 17 })
        {
            Assert.That(view.Contains((long)ids[i].RawValue), Is.False, $"entity {i} matches neither branch and must not be in the view");
        }
    }

    /// <summary>
    /// Overflow recovery re-populates from scratch through <c>RefreshFull</c>. This is the site whose correctness depended on whether a plan was cached:
    /// the cached-plan branch bypassed the cross-archetype scan, the fallback branch went through <c>EcsQuery.Execute()</c> and was already right.
    /// </summary>
    [Test]
    public void SvArchetype_RefreshFullAfterOverflow_Repopulates()
    {
        using var dbe = SetupEngine();

        var ids = new EntityId[24];
        using (var tx = dbe.CreateQuickTransaction())
        {
            for (var i = 0; i < ids.Length; i++)
            {
                ids[i] = tx.Spawn<VpSvArch>(VpSvArch.Item.Set(new VpSvItem(100 + i, i)));
            }
            tx.Commit();
        }
        dbe.WriteTickFence(1);

        using var tx2 = dbe.CreateQuickTransaction();
        // Tiny delta buffer so the mutation burst below overflows it and forces the full-refresh path.
        using var view = tx2.Query<VpSvArch>().WhereField<VpSvItem>(d => d.Score >= 100).ToView(bufferCapacity: 4);
        Assert.That(view.Count, Is.EqualTo(ids.Length), "initial population");

        using (var tx3 = dbe.CreateQuickTransaction())
        {
            for (var i = 0; i < ids.Length; i++)
            {
                tx3.OpenMut(ids[i]).Write(VpSvArch.Item) = new VpSvItem(100 + i, i + 1000);
            }
            tx3.Commit();
        }
        dbe.WriteTickFence(2);

        using var tx4 = dbe.CreateQuickTransaction();
        view.Refresh(tx4);

        Assert.That(view.Count, Is.EqualTo(ids.Length), "after overflow recovery the view must hold every still-matching entity, not zero");
        for (var i = 0; i < ids.Length; i++)
        {
            Assert.That(view.Contains((long)ids[i].RawValue), Is.True, $"entity {i} lost by RefreshFull");
        }
    }

    [Test]
    public void MixedArchetype_ToView_PopulatesVersionedIndexedField()
    {
        using var dbe = SetupEngine();

        using (var tx = dbe.CreateQuickTransaction())
        {
            for (var i = 0; i < 12; i++)
            {
                tx.Spawn<VpMixedArch>(VpMixedArch.Item.Set(new VpVerItem(i)), VpMixedArch.Tag.Set(new VpSvTag(i)));
            }
            tx.Commit();
        }
        dbe.WriteTickFence(1);

        using var tx2 = dbe.CreateQuickTransaction();
        var expected = tx2.Query<VpMixedArch>().WhereField<VpVerItem>(d => d.Level >= 8).Execute();
        using var view = tx2.Query<VpMixedArch>().WhereField<VpVerItem>(d => d.Level >= 8).ToView();

        Assert.That(expected.Count, Is.EqualTo(4), "oracle sanity: levels 8..11");
        Assert.That(view.Count, Is.EqualTo(expected.Count), "a Versioned component in a cluster-eligible archetype indexes on the ARCHETYPE too");
        foreach (var id in expected)
        {
            Assert.That(view.Contains((long)id.RawValue), Is.True, $"entity {id} missing from the mixed-archetype view");
        }
    }

    /// <summary>
    /// Population and incremental refresh are independent halves of #663 (the other half was #660). A view that populates correctly but then ignores deltas
    /// is still broken, so pin both in one flow.
    /// </summary>
    [Test]
    public void SvArchetype_DeltaAfterPopulation_EntersAndLeavesTheView()
    {
        using var dbe = SetupEngine();

        EntityId mover;
        using (var tx = dbe.CreateQuickTransaction())
        {
            mover = tx.Spawn<VpSvArch>(VpSvArch.Item.Set(new VpSvItem(1, 0)));
            tx.Spawn<VpSvArch>(VpSvArch.Item.Set(new VpSvItem(50, 0)));
            tx.Commit();
        }
        dbe.WriteTickFence(1);

        using var tx2 = dbe.CreateQuickTransaction();
        using var view = tx2.Query<VpSvArch>().WhereField<VpSvItem>(d => d.Score >= 10).ToView();
        Assert.That(view.Count, Is.EqualTo(1), "only the score-50 entity matches at population");
        Assert.That(view.Contains((long)mover.RawValue), Is.False);

        // OUT → IN
        using (var tx3 = dbe.CreateQuickTransaction())
        {
            tx3.OpenMut(mover).Write(VpSvArch.Item) = new VpSvItem(99, 0);
            tx3.Commit();
        }
        dbe.WriteTickFence(2);

        using (var tx4 = dbe.CreateQuickTransaction())
        {
            view.Refresh(tx4);
        }
        Assert.That(view.Contains((long)mover.RawValue), Is.True, "the mutated entity must enter the view");
        Assert.That(view.Count, Is.EqualTo(2));

        // IN → OUT
        using (var tx5 = dbe.CreateQuickTransaction())
        {
            tx5.OpenMut(mover).Write(VpSvArch.Item) = new VpSvItem(0, 0);
            tx5.Commit();
        }
        dbe.WriteTickFence(3);

        using (var tx6 = dbe.CreateQuickTransaction())
        {
            view.Refresh(tx6);
        }
        Assert.That(view.Contains((long)mover.RawValue), Is.False, "the entity must leave the view");
        Assert.That(view.Count, Is.EqualTo(1));
    }

    /// <summary>
    /// A per-ComponentTable index is shared by every archetype holding that component, so its scan returns entities of all of them. <c>Execute()</c> has
    /// always filtered those by routing id; the view-population sites did not, so a view could contain entities of an archetype the query never named.
    /// Routing population through the same helper fixes that — a behaviour change beyond #663's stated scope, pinned here.
    /// </summary>
    [Test]
    public void SharedComponentTable_ToView_ExcludesOtherArchetypesEntities()
    {
        using var dbe = SetupEngine();

        EntityId inA;
        EntityId inB;
        using (var tx = dbe.CreateQuickTransaction())
        {
            inA = tx.Spawn<VpSharedArchA>(VpSharedArchA.Shared.Set(new VpVerShared(7)));
            inB = tx.Spawn<VpSharedArchB>(VpSharedArchB.Shared.Set(new VpVerShared(7)));
            tx.Commit();
        }
        dbe.WriteTickFence(1);

        using var tx2 = dbe.CreateQuickTransaction();
        using var view = tx2.Query<VpSharedArchA>().WhereField<VpVerShared>(d => d.Key == 7).ToView();

        Assert.That(view.Contains((long)inA.RawValue), Is.True, "the queried archetype's entity must be present");
        Assert.That(view.Contains((long)inB.RawValue), Is.False, "an entity of a DIFFERENT archetype sharing the component table must not leak into the view");
        Assert.That(view.Count, Is.EqualTo(1));
    }
}
