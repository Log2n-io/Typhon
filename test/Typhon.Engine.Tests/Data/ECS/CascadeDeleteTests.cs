using System.Collections.Generic;
using System.Runtime.InteropServices;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests;

// ═══════════════════════════════════════════════════════════════════════
// Cascade test component + archetype types
// ═══════════════════════════════════════════════════════════════════════

[Component("Typhon.Test.ECS.BagData", 1)]
[StructLayout(LayoutKind.Sequential)]
struct BagData
{
    public int Capacity;
    public int _pad;
}

[Component("Typhon.Test.ECS.ItemData", 1)]
[StructLayout(LayoutKind.Sequential, Pack = 4)]
struct ItemData
{
    [Index(AllowMultiple = true, OnParentDelete = CascadeAction.Delete)]
    public EntityLink<CascadeBag> Owner;
    public int Weight;
}

[Archetype]
class CascadeBag : Archetype<CascadeBag>
{
    public static readonly Comp<BagData> Bag = Register<BagData>();
}

[Archetype]
class CascadeItem : Archetype<CascadeItem>
{
    public static readonly Comp<ItemData> Item = Register<ItemData>();
}

// ═══════════════════════════════════════════════════════════════════════
// #664 — cascade across BOTH secondary-index homes
//
// The fixture above is Versioned-only, so every archetype in it keeps its field indexes on the shared ComponentTable. An archetype with at least one SV or
// Transient slot is cluster-backed instead, and its indexes move onto the ARCHETYPE — which is where cascade used to find nothing at all.
// ═══════════════════════════════════════════════════════════════════════

[Component("Typhon.Test.ECS.ClusterBagData", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct ClusterBagData
{
    public int Capacity;
    public int _pad;
}

// SV FK source. Its archetypes below decide the index home, not this storage mode — the same component sits in a cluster-backed archetype AND a non-cluster
// one, and its entries are split between the two trees accordingly.
[Component("Typhon.Test.ECS.SvItemData", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct SvItemData
{
    [Index(AllowMultiple = true, OnParentDelete = CascadeAction.Delete)]
    public EntityLink<ClusterBag> Owner;
    public int Weight;
    public int _pad;
}

// VERSIONED FK source in a cluster-backed archetype: the component's own storage mode says "ComponentTable", the archetype's composition says otherwise.
// This is the case no storage-mode guard can catch.
[Component("Typhon.Test.ECS.MixedItemData", 1, StorageMode = StorageMode.Versioned)]
[StructLayout(LayoutKind.Sequential)]
struct MixedItemData
{
    [Index(AllowMultiple = true, OnParentDelete = CascadeAction.Delete)]
    public EntityLink<ClusterBag> Owner;
    public int Weight;
    public int _pad;
}

/// <summary>SV sibling whose only job is to make <see cref="MixedItem"/> cluster-eligible.</summary>
[Component("Typhon.Test.ECS.ItemTagData", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct ItemTagData
{
    public int Marker;
    public int _pad;
}

/// <summary>
/// Transient component with an INDEXED field. This USED to disqualify an archetype from cluster storage outright, and was the only way to build a non-cluster
/// archetype holding an SV component — the shape that made the null-<c>CompRevTableSegment</c> path reachable. #655 admits it to cluster storage, so that
/// shape no longer exists and this component now just adds a second index home to the archetype.
/// </summary>
[Component("Typhon.Test.ECS.ItemAuditData", 1, StorageMode = StorageMode.Transient)]
[StructLayout(LayoutKind.Sequential)]
struct ItemAuditData
{
    [Index(AllowMultiple = true)]
    public int Code;
    public int _pad;
}

/// <summary>Grandchild, to prove recursion still descends THROUGH a cluster-backed layer.</summary>
[Component("Typhon.Test.ECS.PartData", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct PartData
{
    [Index(AllowMultiple = true, OnParentDelete = CascadeAction.Delete)]
    public EntityLink<ClusterItem> Item;
    public int Serial;
    public int _pad;
}

/// <summary>SV-only parent — cluster-backed.</summary>
[Archetype]
class ClusterBag : Archetype<ClusterBag>
{
    public static readonly Comp<ClusterBagData> Bag = Register<ClusterBagData>();
}

/// <summary>Pure-SV child: cluster-eligible, so its FK index lives on the archetype.</summary>
[Archetype]
class ClusterItem : Archetype<ClusterItem>
{
    public static readonly Comp<SvItemData> Item = Register<SvItemData>();
}

/// <summary>Mixed child: a Versioned FK component with an SV sibling — cluster-eligible, FK index on the archetype.</summary>
[Archetype]
class MixedItem : Archetype<MixedItem>
{
    public static readonly Comp<MixedItemData> Item = Register<MixedItemData>();
    public static readonly Comp<ItemTagData> Tag = Register<ItemTagData>();
}

/// <summary>Same SV FK component as <see cref="ClusterItem"/>, but the Transient indexed sibling forces this archetype onto the ComponentTable home.</summary>
[Archetype]
class FlatSvItem : Archetype<FlatSvItem>
{
    public static readonly Comp<SvItemData> Item = Register<SvItemData>();
    public static readonly Comp<ItemAuditData> Audit = Register<ItemAuditData>();
}

/// <summary>Cluster-backed grandchild of <see cref="ClusterBag"/> via <see cref="ClusterItem"/>.</summary>
[Archetype]
class ClusterPart : Archetype<ClusterPart>
{
    public static readonly Comp<PartData> Part = Register<PartData>();
}

// [NonParallelizable] removed (#514 Phase 3): it was an incomplete mitigation for the cascade-diamond registry race (Face B).
// The cascade graph is now built once under the registration lock inside ArchetypeRegistry.Freeze, so these fixtures are parallel-safe.
class CascadeDeleteTests : TestBase<CascadeDeleteTests>
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
        dbe.RegisterComponentFromAccessor<ClusterBagData>();
        dbe.RegisterComponentFromAccessor<SvItemData>();
        dbe.RegisterComponentFromAccessor<MixedItemData>();
        dbe.RegisterComponentFromAccessor<ItemTagData>();
        dbe.RegisterComponentFromAccessor<ItemAuditData>();
        dbe.RegisterComponentFromAccessor<PartData>();
        dbe.InitializeArchetypes();
        return dbe;
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Graph validation tests
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public void CascadeGraph_BagHasCascadeTargets()
    {
        // Build cascade graph (requires InitializeArchetypes or explicit call)
        using var dbe = SetupEngine();

        var bagMeta = ArchetypeRegistry.GetMetadata<CascadeBag>();
        Assert.That(bagMeta, Is.Not.Null);
        Assert.That(bagMeta._cascadeTargets, Is.Not.Null);
        Assert.That(bagMeta._cascadeTargets.Count, Is.GreaterThanOrEqualTo(1));

        var target = bagMeta._cascadeTargets[0];
        Assert.That(target.ChildArchetypeId, Is.EqualTo(ArchetypeRegistry.GetMetadata<CascadeItem>().ArchetypeId));
    }

    [Test]
    public void CascadeGraph_ItemHasNoCascadeTargets()
    {
        using var dbe = SetupEngine();
        var itemMeta = ArchetypeRegistry.GetMetadata<CascadeItem>();
        Assert.That(itemMeta, Is.Not.Null);
        // Item has no children with cascade delete
        Assert.That(itemMeta._cascadeTargets == null || itemMeta._cascadeTargets.Count == 0, Is.True);
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Cascade delete execution tests
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public void Destroy_BagWithPendingItems_CascadeDeletesItems()
    {
        using var dbe = SetupEngine();

        using var t = dbe.CreateQuickTransaction();

        // Spawn a bag
        var bagData = new BagData { Capacity = 10 };
        var bagId = t.Spawn<CascadeBag>(CascadeBag.Bag.Set(in bagData));

        // Spawn items pointing to the bag
        var item1Data = new ItemData { Owner = bagId, Weight = 5 };
        var item2Data = new ItemData { Owner = bagId, Weight = 3 };
        var item1Id = t.Spawn<CascadeItem>(CascadeItem.Item.Set(in item1Data));
        var item2Id = t.Spawn<CascadeItem>(CascadeItem.Item.Set(in item2Data));

        // Destroy bag — should cascade to items
        t.Destroy(bagId);

        // All should be marked for destruction
        Assert.That(t.TryOpen(bagId, out _), Is.False, "Bag should be destroyed");
        Assert.That(t.TryOpen(item1Id, out _), Is.False, "Item 1 should be cascade-destroyed");
        Assert.That(t.TryOpen(item2Id, out _), Is.False, "Item 2 should be cascade-destroyed");
    }

    [Test]
    public void Destroy_BagWithoutItems_NoError()
    {
        using var dbe = SetupEngine();

        using var t = dbe.CreateQuickTransaction();
        var bagData = new BagData { Capacity = 5 };
        var bagId = t.Spawn<CascadeBag>(CascadeBag.Bag.Set(in bagData));

        // Destroy bag with no items — should work fine
        Assert.DoesNotThrow(() => t.Destroy(bagId));
        Assert.That(t.TryOpen(bagId, out _), Is.False);
    }

    [Test]
    public void Destroy_BagWithCommittedItems_CascadeDeletesItems()
    {
        using var dbe = SetupEngine();

        // Spawn bag + items and COMMIT them
        EntityId bagId, item1Id, item2Id;
        using (var t1 = dbe.CreateQuickTransaction())
        {
            var bagData = new BagData { Capacity = 10 };
            bagId = t1.Spawn<CascadeBag>(CascadeBag.Bag.Set(in bagData));

            var item1Data = new ItemData { Owner = bagId, Weight = 5 };
            var item2Data = new ItemData { Owner = bagId, Weight = 3 };
            item1Id = t1.Spawn<CascadeItem>(CascadeItem.Item.Set(in item1Data));
            item2Id = t1.Spawn<CascadeItem>(CascadeItem.Item.Set(in item2Data));

            t1.Commit();
        }

        // Now destroy the bag in a new transaction — cascade should find committed items
        using var t2 = dbe.CreateQuickTransaction();
        t2.Destroy(bagId);

        Assert.That(t2.TryOpen(bagId, out _), Is.False, "Bag should be destroyed");
        Assert.That(t2.TryOpen(item1Id, out _), Is.False, "Item 1 should be cascade-destroyed");
        Assert.That(t2.TryOpen(item2Id, out _), Is.False, "Item 2 should be cascade-destroyed");
    }

    [Test]
    public void Destroy_BagWithMixedItems_OnlyOwnerItemsDeleted()
    {
        using var dbe = SetupEngine();

        using var t = dbe.CreateQuickTransaction();

        // Create two bags
        var bag1Data = new BagData { Capacity = 10 };
        var bag2Data = new BagData { Capacity = 20 };
        var bag1Id = t.Spawn<CascadeBag>(CascadeBag.Bag.Set(in bag1Data));
        var bag2Id = t.Spawn<CascadeBag>(CascadeBag.Bag.Set(in bag2Data));

        // Create items for bag1
        var item1Data = new ItemData { Owner = bag1Id, Weight = 5 };
        var item2Data = new ItemData { Owner = bag1Id, Weight = 3 };
        var item1Id = t.Spawn<CascadeItem>(CascadeItem.Item.Set(in item1Data));
        var item2Id = t.Spawn<CascadeItem>(CascadeItem.Item.Set(in item2Data));

        // Create items for bag2
        var item3Data = new ItemData { Owner = bag2Id, Weight = 7 };
        var item4Data = new ItemData { Owner = bag2Id, Weight = 1 };
        var item3Id = t.Spawn<CascadeItem>(CascadeItem.Item.Set(in item3Data));
        var item4Id = t.Spawn<CascadeItem>(CascadeItem.Item.Set(in item4Data));

        // Destroy bag1 — only bag1's items should be cascade-deleted
        t.Destroy(bag1Id);

        Assert.That(t.TryOpen(bag1Id, out _), Is.False, "Bag1 should be destroyed");
        Assert.That(t.TryOpen(item1Id, out _), Is.False, "Item 1 (bag1) should be cascade-destroyed");
        Assert.That(t.TryOpen(item2Id, out _), Is.False, "Item 2 (bag1) should be cascade-destroyed");

        Assert.That(t.TryOpen(bag2Id, out _), Is.True, "Bag2 should survive");
        Assert.That(t.TryOpen(item3Id, out _), Is.True, "Item 3 (bag2) should survive");
        Assert.That(t.TryOpen(item4Id, out _), Is.True, "Item 4 (bag2) should survive");
    }

    [Test]
    public void Destroy_BagWithUnrelatedItems_UnrelatedSurvive()
    {
        using var dbe = SetupEngine();

        using var t = dbe.CreateQuickTransaction();

        // Create a bag
        var bagData = new BagData { Capacity = 10 };
        var bagId = t.Spawn<CascadeBag>(CascadeBag.Bag.Set(in bagData));

        // Create items owned by the bag
        var ownedItemData = new ItemData { Owner = bagId, Weight = 5 };
        var ownedItemId = t.Spawn<CascadeItem>(CascadeItem.Item.Set(in ownedItemData));

        // Create items with null owner (unrelated)
        var unrelatedItemData = new ItemData { Owner = EntityId.Null, Weight = 9 };
        var unrelatedItemId = t.Spawn<CascadeItem>(CascadeItem.Item.Set(in unrelatedItemData));

        // Destroy bag — only owned items should be cascade-deleted
        t.Destroy(bagId);

        Assert.That(t.TryOpen(bagId, out _), Is.False, "Bag should be destroyed");
        Assert.That(t.TryOpen(ownedItemId, out _), Is.False, "Owned item should be cascade-destroyed");
        Assert.That(t.TryOpen(unrelatedItemId, out _), Is.True, "Unrelated item (null owner) should survive");
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Edge case: partial cascade (some children already dead)
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public void Cascade_SomeChildrenAlreadyDead_RemainingDie()
    {
        using var dbe = SetupEngine();

        EntityId bagId, item1Id, item2Id, item3Id;
        using (var t = dbe.CreateQuickTransaction())
        {
            var bagData = new BagData { Capacity = 10 };
            bagId = t.Spawn<CascadeBag>(CascadeBag.Bag.Set(in bagData));

            var i1 = new ItemData { Owner = bagId, Weight = 1 };
            var i2 = new ItemData { Owner = bagId, Weight = 2 };
            var i3 = new ItemData { Owner = bagId, Weight = 3 };
            item1Id = t.Spawn<CascadeItem>(CascadeItem.Item.Set(in i1));
            item2Id = t.Spawn<CascadeItem>(CascadeItem.Item.Set(in i2));
            item3Id = t.Spawn<CascadeItem>(CascadeItem.Item.Set(in i3));
            t.Commit();
        }

        // Destroy item2 independently (before cascade)
        using (var t = dbe.CreateQuickTransaction())
        {
            t.Destroy(item2Id);
            t.Commit();
        }

        // Now cascade-destroy the bag — item1 and item3 should die, item2 already dead
        using (var t = dbe.CreateQuickTransaction())
        {
            t.Destroy(bagId);
            t.Commit();
        }

        using (var t = dbe.CreateQuickTransaction())
        {
            Assert.That(t.IsAlive(bagId), Is.False);
            Assert.That(t.IsAlive(item1Id), Is.False);
            Assert.That(t.IsAlive(item2Id), Is.False);
            Assert.That(t.IsAlive(item3Id), Is.False);
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Edge case: child FK rekeyed to different parent before cascade
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public void Cascade_ChildRekeyedToDifferentParent_NotCascaded()
    {
        using var dbe = SetupEngine();

        EntityId bag1Id, bag2Id, itemId;
        using (var t = dbe.CreateQuickTransaction())
        {
            var b1 = new BagData { Capacity = 10 };
            var b2 = new BagData { Capacity = 20 };
            bag1Id = t.Spawn<CascadeBag>(CascadeBag.Bag.Set(in b1));
            bag2Id = t.Spawn<CascadeBag>(CascadeBag.Bag.Set(in b2));

            // Item initially belongs to bag1
            var itemData = new ItemData { Owner = bag1Id, Weight = 5 };
            itemId = t.Spawn<CascadeItem>(CascadeItem.Item.Set(in itemData));
            t.Commit();
        }

        // Rekey item to bag2
        using (var t = dbe.CreateQuickTransaction())
        {
            var entity = t.OpenMut(itemId);
            ref var item = ref entity.Write(CascadeItem.Item);
            item.Owner = bag2Id;
            t.Commit();
        }

        // Destroy bag1 — item should NOT be cascaded (it now belongs to bag2)
        using (var t = dbe.CreateQuickTransaction())
        {
            t.Destroy(bag1Id);
            t.Commit();
        }

        using (var t = dbe.CreateQuickTransaction())
        {
            Assert.That(t.IsAlive(bag1Id), Is.False, "Bag1 should be dead");
            Assert.That(t.IsAlive(bag2Id), Is.True, "Bag2 should survive");
            Assert.That(t.IsAlive(itemId), Is.True, "Item should survive (rekeyed to bag2)");
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Edge case: child modified then parent cascade in same tx
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public void Cascade_ChildModifiedThenParentDestroyed_SameTx()
    {
        using var dbe = SetupEngine();

        EntityId bagId, itemId;
        using (var t = dbe.CreateQuickTransaction())
        {
            var bagData = new BagData { Capacity = 10 };
            bagId = t.Spawn<CascadeBag>(CascadeBag.Bag.Set(in bagData));

            var itemData = new ItemData { Owner = bagId, Weight = 5 };
            itemId = t.Spawn<CascadeItem>(CascadeItem.Item.Set(in itemData));
            t.Commit();
        }

        // In same tx: modify item's Weight, then destroy parent bag
        using (var t = dbe.CreateQuickTransaction())
        {
            var entity = t.OpenMut(itemId);
            ref var item = ref entity.Write(CascadeItem.Item);
            item.Weight = 99;

            // Now destroy the parent — cascade should find the item despite the write
            t.Destroy(bagId);

            Assert.That(t.IsAlive(bagId), Is.False);
            Assert.That(t.IsAlive(itemId), Is.False, "Modified child should still be cascade-destroyed");
            t.Commit();
        }

        using (var t = dbe.CreateQuickTransaction())
        {
            Assert.That(t.IsAlive(bagId), Is.False);
            Assert.That(t.IsAlive(itemId), Is.False);
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Edge case: large fan-out cascade
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public void Cascade_LargeFanOut_AllChildrenDestroyed()
    {
        using var dbe = SetupEngine();

        const int childCount = 100;
        EntityId bagId;
        var childIds = new EntityId[childCount];

        using (var t = dbe.CreateQuickTransaction())
        {
            var bagData = new BagData { Capacity = childCount };
            bagId = t.Spawn<CascadeBag>(CascadeBag.Bag.Set(in bagData));

            for (int i = 0; i < childCount; i++)
            {
                var itemData = new ItemData { Owner = bagId, Weight = i + 1 };
                childIds[i] = t.Spawn<CascadeItem>(CascadeItem.Item.Set(in itemData));
            }
            t.Commit();
        }

        // Destroy parent — all 100 children should cascade
        using (var t = dbe.CreateQuickTransaction())
        {
            t.Destroy(bagId);
            t.Commit();
        }

        using (var t = dbe.CreateQuickTransaction())
        {
            Assert.That(t.IsAlive(bagId), Is.False);
            int deadCount = 0;
            for (int i = 0; i < childCount; i++)
            {
                if (!t.IsAlive(childIds[i]))
                {
                    deadCount++;
                }
            }
            Assert.That(deadCount, Is.EqualTo(childCount), $"All {childCount} children should be cascade-destroyed");
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // #664 — cluster-backed child archetypes
    //
    // FindCascadeChildren resolved the FK index on the child's ComponentTable and then dereferenced CompRevTableSegment unconditionally. For a cluster-backed
    // child that tree is empty, so the cascade destroyed NOTHING and orphaned the children — no exception, no log, nothing to notice. Every test below fails
    // on the pre-#664 code, most of them by finding the children still alive.
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Guards the premise. Without these four assertions the tests below could pass while exercising an archetype whose FK index is empty.
    /// </summary>
    /// <remarks>
    /// These four shapes used to straddle two index homes, and that was the point — cascade delete had to find children through either. The eligibility flip
    /// (#629) collapsed them onto one, so the assertion that still earns its place is that every child archetype really does own a per-archetype FK tree. A
    /// child that silently had none would make its cascade test vacuous rather than failing.
    /// </remarks>
    [Test]
    public void Fixture_ChildArchetypesAllIndexOnTheArchetype()
    {
        using var dbe = SetupEngine();

        Assert.Multiple(() =>
        {
            Assert.That(Archetype<ClusterItem>.Metadata.HasClusterIndexes, Is.True, "pure-SV child must index on the ARCHETYPE");
            Assert.That(Archetype<MixedItem>.Metadata.HasClusterIndexes, Is.True, "a Versioned FK component with an SV sibling must index on the ARCHETYPE");
            Assert.That(Archetype<FlatSvItem>.Metadata.HasClusterIndexes, Is.True,
                "cluster-backed since #655 — a Transient indexed sibling no longer forces the archetype onto the ComponentTable home");
            Assert.That(Archetype<CascadeItem>.Metadata.HasClusterIndexes, Is.True, "the Versioned-only child is cluster-backed too since #629");
        });
    }

    /// <summary>AC: cascade destroys committed children of a pure-SV, cluster-backed child archetype.</summary>
    [Test]
    public void Destroy_ClusterBackedSvChildren_CascadeDeletes()
    {
        using var dbe = SetupEngine();

        EntityId bagId, item1Id, item2Id;
        using (var t = dbe.CreateQuickTransaction())
        {
            bagId = t.Spawn<ClusterBag>(ClusterBag.Bag.Set(new ClusterBagData { Capacity = 10 }));
            item1Id = t.Spawn<ClusterItem>(ClusterItem.Item.Set(new SvItemData { Owner = bagId, Weight = 5 }));
            item2Id = t.Spawn<ClusterItem>(ClusterItem.Item.Set(new SvItemData { Owner = bagId, Weight = 3 }));
            t.Commit();
        }
        dbe.WriteTickFence(1);

        using (var t = dbe.CreateQuickTransaction())
        {
            t.Destroy(bagId);
            t.Commit();
        }

        using (var t = dbe.CreateQuickTransaction())
        {
            Assert.Multiple(() =>
            {
                Assert.That(t.IsAlive(bagId), Is.False, "bag should be destroyed");
                Assert.That(t.IsAlive(item1Id), Is.False, "cluster-backed child 1 should be cascade-destroyed");
                Assert.That(t.IsAlive(item2Id), Is.False, "cluster-backed child 2 should be cascade-destroyed");
            });
        }
    }

    /// <summary>
    /// AC: cascade destroys committed children of a MIXED SV+Versioned child archetype. The FK component is Versioned, so any guard that tested the
    /// component's storage mode would wave this through — and then read an empty ComponentTable tree.
    /// </summary>
    [Test]
    public void Destroy_ClusterBackedMixedChildren_CascadeDeletes()
    {
        using var dbe = SetupEngine();

        EntityId bagId, item1Id, item2Id;
        using (var t = dbe.CreateQuickTransaction())
        {
            bagId = t.Spawn<ClusterBag>(ClusterBag.Bag.Set(new ClusterBagData { Capacity = 10 }));
            item1Id = t.Spawn<MixedItem>(MixedItem.Item.Set(new MixedItemData { Owner = bagId, Weight = 7 }),
                MixedItem.Tag.Set(new ItemTagData { Marker = 1 }));
            item2Id = t.Spawn<MixedItem>(MixedItem.Item.Set(new MixedItemData { Owner = bagId, Weight = 9 }),
                MixedItem.Tag.Set(new ItemTagData { Marker = 2 }));
            t.Commit();
        }
        dbe.WriteTickFence(1);

        using (var t = dbe.CreateQuickTransaction())
        {
            t.Destroy(bagId);
            t.Commit();
        }

        using (var t = dbe.CreateQuickTransaction())
        {
            Assert.Multiple(() =>
            {
                Assert.That(t.IsAlive(bagId), Is.False, "bag should be destroyed");
                Assert.That(t.IsAlive(item1Id), Is.False, "mixed cluster-backed child 1 should be cascade-destroyed");
                Assert.That(t.IsAlive(item2Id), Is.False, "mixed cluster-backed child 2 should be cascade-destroyed");
            });
        }
    }

    /// <summary>
    /// AC: one parent whose children straddle BOTH index homes. Cascade walks each edge separately, so this proves the cluster phase and the ComponentTable
    /// phase both run — and, via the second bag, that neither over-reaches into another parent's children.
    /// </summary>
    [Test]
    public void Destroy_ChildrenSplitAcrossBothIndexHomes_AllCascadeAndOnlyOwners()
    {
        using var dbe = SetupEngine();

        EntityId bag1Id, bag2Id, clusterChild, mixedChild, flatChild, otherBagChild;
        using (var t = dbe.CreateQuickTransaction())
        {
            bag1Id = t.Spawn<ClusterBag>(ClusterBag.Bag.Set(new ClusterBagData { Capacity = 10 }));
            bag2Id = t.Spawn<ClusterBag>(ClusterBag.Bag.Set(new ClusterBagData { Capacity = 20 }));

            clusterChild = t.Spawn<ClusterItem>(ClusterItem.Item.Set(new SvItemData { Owner = bag1Id, Weight = 1 }));
            mixedChild = t.Spawn<MixedItem>(MixedItem.Item.Set(new MixedItemData { Owner = bag1Id, Weight = 2 }),
                MixedItem.Tag.Set(new ItemTagData { Marker = 1 }));
            flatChild = t.Spawn<FlatSvItem>(FlatSvItem.Item.Set(new SvItemData { Owner = bag1Id, Weight = 3 }),
                FlatSvItem.Audit.Set(new ItemAuditData { Code = 42 }));

            // Same component type, same shared ComponentTable tree, different parent — the routing/key filters must exclude it.
            otherBagChild = t.Spawn<FlatSvItem>(FlatSvItem.Item.Set(new SvItemData { Owner = bag2Id, Weight = 4 }),
                FlatSvItem.Audit.Set(new ItemAuditData { Code = 43 }));
            t.Commit();
        }
        dbe.WriteTickFence(1);

        using (var t = dbe.CreateQuickTransaction())
        {
            t.Destroy(bag1Id);
            t.Commit();
        }

        using (var t = dbe.CreateQuickTransaction())
        {
            Assert.Multiple(() =>
            {
                Assert.That(t.IsAlive(bag1Id), Is.False, "bag1 should be destroyed");
                Assert.That(t.IsAlive(clusterChild), Is.False, "archetype-homed child should be cascade-destroyed");
                Assert.That(t.IsAlive(mixedChild), Is.False, "mixed archetype-homed child should be cascade-destroyed");
                Assert.That(t.IsAlive(flatChild), Is.False, "ComponentTable-homed child should be cascade-destroyed");

                Assert.That(t.IsAlive(bag2Id), Is.True, "bag2 must survive");
                Assert.That(t.IsAlive(otherBagChild), Is.True, "bag2's child must survive — it shares the tree with bag1's flat child");
            });
        }
    }

    /// <summary>
    /// AC: a SingleVersion FK component whose archetype also carries a Transient indexed one. Before #655 that combination was forced off the cluster path,
    /// which made it the only reachable route to the old unconditional <c>table.CompRevTableSegment.CreateChunkAccessor()</c> — null for SingleVersion, so a
    /// <see cref="NullReferenceException"/>, the loud half of #664's defect. The archetype is cluster-backed now, so this stands as a cascade test over an
    /// archetype with BOTH index homes rather than as the non-cluster repro it originally was.
    /// </summary>
    [Test]
    public void Destroy_SingleVersionChildWithTransientSibling_DoesNotThrowAndCascades()
    {
        using var dbe = SetupEngine();

        EntityId bagId, childId;
        using (var t = dbe.CreateQuickTransaction())
        {
            bagId = t.Spawn<ClusterBag>(ClusterBag.Bag.Set(new ClusterBagData { Capacity = 4 }));
            childId = t.Spawn<FlatSvItem>(FlatSvItem.Item.Set(new SvItemData { Owner = bagId, Weight = 8 }),
                FlatSvItem.Audit.Set(new ItemAuditData { Code = 7 }));
            t.Commit();
        }
        dbe.WriteTickFence(1);

        using (var t = dbe.CreateQuickTransaction())
        {
            Assert.DoesNotThrow(() => t.Destroy(bagId), "an SV child component has no CompRev table — the old code dereferenced it unconditionally");
            t.Commit();
        }

        using (var t = dbe.CreateQuickTransaction())
        {
            Assert.Multiple(() =>
            {
                Assert.That(t.IsAlive(bagId), Is.False);
                Assert.That(t.IsAlive(childId), Is.False, "SV child should be cascade-destroyed");
            });
        }
    }

    /// <summary>
    /// AC: same-tx pending children of a cluster-backed archetype still cascade. That path reads the spawn staging chunks
    /// (<c>SpawnEntry.Loc[]</c>), not either index — cluster entities only reach the cluster SoA at commit — so it must stay untouched by the index-home fix.
    /// </summary>
    [Test]
    public void Destroy_PendingClusterBackedChildren_CascadeDeletes()
    {
        using var dbe = SetupEngine();

        using var t = dbe.CreateQuickTransaction();

        var bagId = t.Spawn<ClusterBag>(ClusterBag.Bag.Set(new ClusterBagData { Capacity = 10 }));
        var svChild = t.Spawn<ClusterItem>(ClusterItem.Item.Set(new SvItemData { Owner = bagId, Weight = 5 }));
        var mixedChild = t.Spawn<MixedItem>(MixedItem.Item.Set(new MixedItemData { Owner = bagId, Weight = 6 }),
            MixedItem.Tag.Set(new ItemTagData { Marker = 3 }));

        t.Destroy(bagId);

        Assert.Multiple(() =>
        {
            Assert.That(t.TryOpen(bagId, out _), Is.False, "bag should be destroyed");
            Assert.That(t.TryOpen(svChild, out _), Is.False, "pending SV cluster child should be cascade-destroyed");
            Assert.That(t.TryOpen(mixedChild, out _), Is.False, "pending mixed cluster child should be cascade-destroyed");
        });
    }

    /// <summary>
    /// AC: depth/recursion still descends THROUGH a cluster-backed layer — bag → cluster item → cluster part. A cluster middle layer that returns no children
    /// truncates the whole chain silently, so the grandchild is the real assertion here.
    /// </summary>
    [Test]
    public void Cascade_ClusterBackedMidChain_RecursesToGrandchildren()
    {
        using var dbe = SetupEngine();

        EntityId bagId, itemId, part1Id, part2Id;
        using (var t = dbe.CreateQuickTransaction())
        {
            bagId = t.Spawn<ClusterBag>(ClusterBag.Bag.Set(new ClusterBagData { Capacity = 10 }));
            itemId = t.Spawn<ClusterItem>(ClusterItem.Item.Set(new SvItemData { Owner = bagId, Weight = 5 }));
            part1Id = t.Spawn<ClusterPart>(ClusterPart.Part.Set(new PartData { Item = itemId, Serial = 1 }));
            part2Id = t.Spawn<ClusterPart>(ClusterPart.Part.Set(new PartData { Item = itemId, Serial = 2 }));
            t.Commit();
        }
        dbe.WriteTickFence(1);

        using (var t = dbe.CreateQuickTransaction())
        {
            t.Destroy(bagId);
            t.Commit();
        }

        using (var t = dbe.CreateQuickTransaction())
        {
            Assert.Multiple(() =>
            {
                Assert.That(t.IsAlive(bagId), Is.False, "bag should be destroyed");
                Assert.That(t.IsAlive(itemId), Is.False, "cluster-backed child should be cascade-destroyed");
                Assert.That(t.IsAlive(part1Id), Is.False, "grandchild 1 should be cascade-destroyed through the cluster layer");
                Assert.That(t.IsAlive(part2Id), Is.False, "grandchild 2 should be cascade-destroyed through the cluster layer");
            });
        }
    }

    /// <summary>
    /// The cycle/diamond validator runs over the WHOLE registry on every <c>InitializeArchetypes</c>, so the new edges above are already exercised by every
    /// other test here. This states that explicitly: ClusterBag fans out to three distinct children and one of them has its own child.
    /// </summary>
    [Test]
    public void CascadeGraph_ClusterBagFansOutToBothHomesWithoutDiamond()
    {
        using var dbe = SetupEngine();

        var bagMeta = ArchetypeRegistry.GetMetadata<ClusterBag>();
        Assert.That(bagMeta._cascadeTargets, Is.Not.Null);

        var childIds = new HashSet<ushort>();
        foreach (var target in bagMeta._cascadeTargets)
        {
            childIds.Add(target.ChildArchetypeId);
        }

        Assert.Multiple(() =>
        {
            Assert.That(childIds, Does.Contain(ArchetypeRegistry.GetMetadata<ClusterItem>().ArchetypeId));
            Assert.That(childIds, Does.Contain(ArchetypeRegistry.GetMetadata<MixedItem>().ArchetypeId));
            Assert.That(childIds, Does.Contain(ArchetypeRegistry.GetMetadata<FlatSvItem>().ArchetypeId));
            Assert.That(childIds.Count, Is.EqualTo(bagMeta._cascadeTargets.Count), "one edge per child archetype — a repeat would be a diamond");
        });

        var itemMeta = ArchetypeRegistry.GetMetadata<ClusterItem>();
        Assert.That(itemMeta._cascadeTargets, Is.Not.Null.And.Count.EqualTo(1), "the cluster-backed child is itself a parent");
        Assert.That(itemMeta._cascadeTargets[0].ChildArchetypeId, Is.EqualTo(ArchetypeRegistry.GetMetadata<ClusterPart>().ArchetypeId));
    }
}
