using System;
using System.Numerics;
using System.Runtime.InteropServices;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests;

// ═══════════════════════════════════════════════════════════════════════════════
// ComponentCollection on a SingleVersion component. An SV slot makes the archetype
// cluster-eligible, so the CC data lives in the cluster slot (sole owner, no revision
// chain). SV is last-writer-wins / no MVCC, so the collection is mutated IN PLACE
// (no copy-on-write) and freed when the slot is released on destroy.
// In-session only — reopen persistence is tracked in #387.
// ═══════════════════════════════════════════════════════════════════════════════

[Component("Typhon.Test.SvCc.Bag", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct SvCcBag
{
    [Field]
    public int A;

    [Field]
    public ComponentCollection<int> Items;
}

[Archetype(846)]
partial class SvCcUnit : Archetype<SvCcUnit>
{
    public static readonly Comp<SvCcBag> Bag = Register<SvCcBag>();
}

// Spatial SV component (migratable) carrying a ComponentCollection — for verifying migration moves the CC handle
// rather than freeing it.
[Component("Typhon.Test.SvCc.SpatialBag", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct SvCcSpatialBag
{
    [Field]
    [SpatialIndex(1.0f)]
    public AABB2F Bounds;

    [Field]
    public ComponentCollection<int> Items;
}

[Archetype(847)]
partial class SvCcSpatialUnit : Archetype<SvCcSpatialUnit>
{
    public static readonly Comp<SvCcSpatialBag> Bag = Register<SvCcSpatialBag>();
}

// Transient component carrying a ComponentCollection — must be rejected at registration (Transient CC is out of scope).
[Component("Typhon.Test.SvCc.TransientBag", 1, StorageMode = StorageMode.Transient)]
[StructLayout(LayoutKind.Sequential)]
struct TransientCcBag
{
    [Field]
    public int A;

    [Field]
    public ComponentCollection<int> Items;
}

[TestFixture]
[NonParallelizable]
class SvComponentCollectionTests : TestBase<SvComponentCollectionTests>
{
    [OneTimeSetUp]
    public void OneTimeSetup()
    {
    }

    private DatabaseEngine SetupEngine()
    {
        var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<SvCcBag>();
        dbe.InitializeArchetypes();

        // An SV slot makes the archetype cluster-eligible — the CC field's authoritative storage is the cluster slot.
        var meta = Archetype<SvCcUnit>.Metadata;
        Assert.That(dbe._archetypeStates[meta.ArchetypeId].ClusterState, Is.Not.Null,
            "SvCcUnit must be cluster-eligible (SV slot)");
        return dbe;
    }

    private static EntityId Spawn(DatabaseEngine dbe, int count, out int bufferId)
    {
        using var t = dbe.CreateQuickTransaction();
        var bag = new SvCcBag { A = 7 };
        {
            using var cca = t.CreateComponentCollectionAccessor(ref bag.Items);
            for (int i = 0; i < count; i++)
            {
                cca.Add(i);
            }
        }
        bufferId = bag.Items._bufferId;
        var id = t.Spawn<SvCcUnit>(SvCcUnit.Bag.Set(in bag));
        Assert.That(t.Commit(), Is.True, "spawn commit");
        return id;
    }

    private static int[] ReadItems(DatabaseEngine dbe, EntityId id, out int bufferId)
    {
        using var t = dbe.CreateQuickTransaction();
        var bag = t.Open(id).Read(SvCcUnit.Bag);
        bufferId = bag.Items._bufferId;
        using var cca = t.CreateComponentCollectionAccessor(ref bag.Items);
        var items = new int[cca.ElementCount];
        cca.GetAllElements(items);
        return items;
    }

    [Test]
    public void SvCc_Spawn_ReadsBack()
    {
        using var dbe = SetupEngine();
        var id = Spawn(dbe, 10, out var spawnBuffer);

        var items = ReadItems(dbe, id, out var readBuffer);
        Assert.That(items.Length, Is.EqualTo(10), "all 10 items read back");
        Assert.That(readBuffer, Is.EqualTo(spawnBuffer), "SV cluster slot holds the same buffer the spawn built");
        for (int i = 0; i < 10; i++)
        {
            Assert.That(Array.IndexOf(items, i), Is.GreaterThanOrEqualTo(0), $"item {i} present");
        }
    }

    [Test]
    public void SvCc_Update_InPlace_NoNewBuffer()
    {
        using var dbe = SetupEngine();
        var id = Spawn(dbe, 10, out var b1);

        using (var t = dbe.CreateQuickTransaction())
        {
            var entity = t.OpenMut(id);
            ref var bag = ref entity.Write(SvCcUnit.Bag);
            using (var cca = t.CreateComponentCollectionAccessor(ref bag.Items))
            {
                for (int i = 10; i < 20; i++)
                {
                    cca.Add(i);
                }
            }
            // SV is last-writer-wins: the collection must be mutated IN PLACE (same buffer), not cloned.
            Assert.That(bag.Items._bufferId, Is.EqualTo(b1), "SV update must mutate the buffer in place (no clone)");
            Assert.That(t.Commit(), Is.True, "update commit");
        }

        var items = ReadItems(dbe, id, out var b2);
        Assert.That(b2, Is.EqualTo(b1), "buffer id unchanged after in-place update");
        Assert.That(items.Length, Is.EqualTo(20), "all 20 items after in-place append");
        for (int i = 0; i < 20; i++)
        {
            Assert.That(Array.IndexOf(items, i), Is.GreaterThanOrEqualTo(0), $"item {i} present");
        }

        using (var t = dbe.CreateQuickTransaction())
        {
            var bag = t.Open(id).Read(SvCcUnit.Bag);
            Assert.That(t.GetComponentCollectionRefCounter(ref bag.Items), Is.EqualTo(1), "SV buffer refcount stays 1 (single owner)");
        }
    }

    [Test]
    public void SvCc_Destroy_FreesBuffer()
    {
        using var dbe = SetupEngine();
        var id = Spawn(dbe, 10, out _);

        ComponentCollection<int> handle;
        using (var t = dbe.CreateQuickTransaction())
        {
            handle = t.Open(id).Read(SvCcUnit.Bag).Items;
            Assert.That(t.GetComponentCollectionRefCounter(ref handle), Is.EqualTo(1), "live buffer refcount 1");
        }

        using (var t = dbe.CreateQuickTransaction())
        {
            t.Destroy(id);
            Assert.That(t.Commit(), Is.True, "destroy commit");
        }
        dbe.FlushDeferredCleanups();
        dbe.FlushDeferredCleanups();

        using (var t = dbe.CreateQuickTransaction())
        {
            Assert.That(t.GetComponentCollectionRefCounter(ref handle), Is.EqualTo(0),
                "destroying an SV entity must free its collection buffer (released by ReleaseSlot)");
        }
    }

    private DatabaseEngine SetupSpatialEngine()
    {
        var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<SvCcSpatialBag>();
        dbe.ConfigureSpatialGrid(new SpatialGridConfig(
            worldMin: new Vector2(0, 0), worldMax: new Vector2(1000, 1000), cellSize: 100f));
        dbe.InitializeArchetypes();
        return dbe;
    }

    [Test]
    public void SvCc_Migrate_PreservesCollection_NoDoubleFree()
    {
        using var dbe = SetupSpatialEngine();

        EntityId id;
        int bufferId;
        using (var t = dbe.CreateQuickTransaction())
        {
            var bag = new SvCcSpatialBag { Bounds = new AABB2F { MinX = 50, MinY = 50, MaxX = 50, MaxY = 50 } };
            using (var cca = t.CreateComponentCollectionAccessor(ref bag.Items))
            {
                for (int i = 0; i < 10; i++)
                {
                    cca.Add(i);
                }
            }
            bufferId = bag.Items._bufferId;
            id = t.Spawn<SvCcSpatialUnit>(SvCcSpatialUnit.Bag.Set(in bag));
            Assert.That(t.Commit(), Is.True, "spawn commit");
        }

        Assert.That(dbe.SpatialGrid.WorldToCellKey(50f, 50f), Is.Not.EqualTo(dbe.SpatialGrid.WorldToCellKey(350f, 350f)));

        using (var t = dbe.CreateQuickTransaction())
        {
            ref var bag = ref t.OpenMut(id).Write(SvCcSpatialUnit.Bag);
            bag.Bounds = new AABB2F { MinX = 350, MinY = 350, MaxX = 350, MaxY = 350 };
            Assert.That(t.Commit(), Is.True, "move commit");
        }
        dbe.WriteTickFence(1);

        // Migration is a MOVE: the CC handle was byte-copied to the destination slot and the source release
        // (deferFinalize:true) must NOT free the buffer. The collection — and its refcount — must be intact.
        using (var t = dbe.CreateQuickTransaction())
        {
            var bag = t.Open(id).Read(SvCcSpatialUnit.Bag);
            Assert.That(bag.Items._bufferId, Is.EqualTo(bufferId), "buffer handle preserved across migration (move)");
            Assert.That(t.GetComponentCollectionRefCounter(ref bag.Items), Is.EqualTo(1),
                "refcount stays 1 — migration must neither free nor double-count the buffer");
            using var cca = t.CreateComponentCollectionAccessor(ref bag.Items);
            Assert.That(cca.ElementCount, Is.EqualTo(10), "all items survive migration");
        }
    }

    [Test]
    public void SvCc_Rollback_FreesCollection()
    {
        using var dbe = SetupEngine();

        ComponentCollection<int> handle;
        using (var t = dbe.CreateQuickTransaction())
        {
            var bag = new SvCcBag { A = 1 };
            using (var cca = t.CreateComponentCollectionAccessor(ref bag.Items))
            {
                for (int i = 0; i < 10; i++)
                {
                    cca.Add(i);
                }
            }
            handle = bag.Items;
            t.Spawn<SvCcUnit>(SvCcUnit.Bag.Set(in bag));
            // Intentionally NOT committed — the transaction rolls back on dispose.
        }

        using (var t = dbe.CreateQuickTransaction())
        {
            Assert.That(t.GetComponentCollectionRefCounter(ref handle), Is.EqualTo(0),
                "a rolled-back spawn must free the accessor-allocated collection buffer");
        }
    }

    [Test]
    public void TransientCc_ThrowsAtRegistration()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        var ex = Assert.Throws<InvalidOperationException>(() => dbe.RegisterComponentFromAccessor<TransientCcBag>());
        Assert.That(ex.Message, Does.Contain("ComponentCollection"), "the error must name the unsupported feature");
    }
}
