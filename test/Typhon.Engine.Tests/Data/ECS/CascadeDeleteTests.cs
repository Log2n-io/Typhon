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
[StructLayout(LayoutKind.Sequential)]
struct ItemData
{
    [Index(AllowMultiple = true, OnParentDelete = CascadeAction.Delete)]
    public EntityLink<CascadeBag> Owner;
    public int Weight;
}

[Archetype(700)]
class CascadeBag : Archetype<CascadeBag>
{
    public static readonly Comp<BagData> Bag = Register<BagData>();
}

[Archetype(701)]
class CascadeItem : Archetype<CascadeItem>
{
    public static readonly Comp<ItemData> Item = Register<ItemData>();
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

        var bagMeta = ArchetypeRegistry.GetMetadata(700);
        Assert.That(bagMeta, Is.Not.Null);
        Assert.That(bagMeta._cascadeTargets, Is.Not.Null);
        Assert.That(bagMeta._cascadeTargets.Count, Is.GreaterThanOrEqualTo(1));

        var target = bagMeta._cascadeTargets[0];
        Assert.That(target.ChildArchetypeId, Is.EqualTo(701)); // CascadeItem
    }

    [Test]
    public void CascadeGraph_ItemHasNoCascadeTargets()
    {
        using var dbe = SetupEngine();
        var itemMeta = ArchetypeRegistry.GetMetadata(701);
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
}
