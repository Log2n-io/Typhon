using System;
using System.Numerics;
using System.Runtime.InteropServices;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests;

// ═══════════════════════════════════════════════════════════════════════════════
// ComponentCollection on a CLUSTER-ELIGIBLE archetype (Versioned CC component +
// SingleVersion spatial component). The SV slot makes the archetype cluster-eligible;
// the spatial field makes it migratable. The Versioned CC field's authoritative data
// lives in content chunks (the cluster slot is only an uncounted HEAD cache), so this
// verifies CC survives spawn / update / migration / destroy under clustering.
// In-session only — reopen persistence is tracked in #387.
// ═══════════════════════════════════════════════════════════════════════════════

[Component("Typhon.Test.ClCc.Pos", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct ClCcPos
{
    [Field]
    [SpatialIndex(1.0f)]
    public AABB2F Bounds;
}

[Component("Typhon.Test.ClCc.Bag", 1)] // Versioned (default)
[StructLayout(LayoutKind.Sequential)]
struct ClCcBag
{
    [Field]
    public int A;

    [Field]
    public ComponentCollection<int> Items;
}

[Archetype]
partial class ClCcUnit : Archetype<ClCcUnit>
{
    public static readonly Comp<ClCcPos> Pos = Register<ClCcPos>();
    public static readonly Comp<ClCcBag> Bag = Register<ClCcBag>();
}

[TestFixture]
[NonParallelizable]
class ClusterComponentCollectionTests : TestBase<ClusterComponentCollectionTests>
{
    private const float CellSize = 100f;
    private const float WorldMax = 1000f;

    [OneTimeSetUp]
    public void OneTimeSetup()
    {
    }

    private DatabaseEngine SetupEngineWithGrid()
    {
        var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<ClCcPos>();
        dbe.RegisterComponentFromAccessor<ClCcBag>();
        dbe.ConfigureSpatialGrid(new SpatialGridConfig(
            worldMin: new Vector2(0, 0),
            worldMax: new Vector2(WorldMax, WorldMax),
            cellSize: CellSize));
        dbe.InitializeArchetypes();

        // Sanity: the archetype MUST be cluster-eligible (SV slot present), otherwise the test would
        // silently exercise the legacy non-cluster path and prove nothing about clusters.
        var meta = Archetype<ClCcUnit>.Metadata;
        Assert.That(dbe._archetypeStates[meta.ArchetypeId].ClusterState, Is.Not.Null,
            "ClCcUnit must be cluster-eligible (it has an SV slot)");

        return dbe;
    }

    private static AABB2F PointAt(float x, float y) => new() { MinX = x, MinY = y, MaxX = x, MaxY = y };

    private static EntityId SpawnWithItems(DatabaseEngine dbe, float x, float y, int count, out int firstBufferRefcount)
    {
        using var t = dbe.CreateQuickTransaction();
        var pos = new ClCcPos { Bounds = PointAt(x, y) };
        var bag = new ClCcBag { A = 1 };
        {
            using var cca = t.CreateComponentCollectionAccessor(ref bag.Items);
            for (int i = 0; i < count; i++)
            {
                cca.Add(i);
            }
        }
        var id = t.Spawn<ClCcUnit>(ClCcUnit.Pos.Set(in pos), ClCcUnit.Bag.Set(in bag));
        firstBufferRefcount = t.GetComponentCollectionRefCounter(ref bag.Items);
        Assert.That(t.Commit(), Is.True, "spawn commit");
        return id;
    }

    private static int[] ReadItems(DatabaseEngine dbe, EntityId id)
    {
        using var t = dbe.CreateQuickTransaction();
        var bag = t.Open(id).Read(ClCcUnit.Bag);
        using var cca = t.CreateComponentCollectionAccessor(ref bag.Items);
        var items = new int[cca.ElementCount];
        cca.GetAllElements(items);
        return items;
    }

    [Test]
    public void ClusterV_Spawn_WithCollection_ReadsBack()
    {
        using var dbe = SetupEngineWithGrid();
        var id = SpawnWithItems(dbe, 50f, 50f, 10, out var rc);
        Assert.That(rc, Is.EqualTo(1), "freshly spawned buffer has refcount 1");

        var items = ReadItems(dbe, id);
        Assert.That(items.Length, Is.EqualTo(10), "all 10 items read back");
        for (int i = 0; i < 10; i++)
        {
            Assert.That(Array.IndexOf(items, i), Is.GreaterThanOrEqualTo(0), $"item {i} present");
        }
    }

    [Test]
    public void ClusterV_Update_AppendsAndReadsBack()
    {
        using var dbe = SetupEngineWithGrid();
        var id = SpawnWithItems(dbe, 50f, 50f, 10, out _);

        using (var t = dbe.CreateQuickTransaction())
        {
            var entity = t.OpenMut(id);
            ref var bag = ref entity.Write(ClCcUnit.Bag);
            using (var cca = t.CreateComponentCollectionAccessor(ref bag.Items))
            {
                for (int i = 10; i < 20; i++)
                {
                    cca.Add(i);
                }
            }
            Assert.That(t.Commit(), Is.True, "update commit");
        }

        var items = ReadItems(dbe, id);
        Assert.That(items.Length, Is.EqualTo(20), "all 20 items after append");
        for (int i = 0; i < 20; i++)
        {
            Assert.That(Array.IndexOf(items, i), Is.GreaterThanOrEqualTo(0), $"item {i} present");
        }
    }

    [Test]
    public void ClusterV_Migrate_PreservesCollection()
    {
        using var dbe = SetupEngineWithGrid();
        var id = SpawnWithItems(dbe, 50f, 50f, 10, out _);

        int srcCell = dbe.SpatialGrid.WorldToCellKey(50f, 50f);
        int dstCell = dbe.SpatialGrid.WorldToCellKey(350f, 350f);
        Assert.That(srcCell, Is.Not.EqualTo(dstCell), "positions must be in different cells");

        // Move the entity into a new cell (writes only the SV Pos), then run the tick fence to migrate it.
        using (var t = dbe.CreateQuickTransaction())
        {
            var entity = t.OpenMut(id);
            ref var pos = ref entity.Write(ClCcUnit.Pos);
            pos.Bounds = PointAt(350f, 350f);
            Assert.That(t.Commit(), Is.True, "move commit");
        }
        dbe.WriteTickFence(1);

        // The Versioned CC field's content chunk is untouched by migration (only the cluster-slot HEAD cache moved),
        // so the collection — and its refcount — must be intact.
        var items = ReadItems(dbe, id);
        Assert.That(items.Length, Is.EqualTo(10), "collection survives migration");
        for (int i = 0; i < 10; i++)
        {
            Assert.That(Array.IndexOf(items, i), Is.GreaterThanOrEqualTo(0), $"item {i} present after migration");
        }

        using (var t = dbe.CreateQuickTransaction())
        {
            var bag = t.Open(id).Read(ClCcUnit.Bag);
            Assert.That(t.GetComponentCollectionRefCounter(ref bag.Items), Is.EqualTo(1),
                "migration is a move — refcount must stay 1 (no spurious AddRef on the cluster HEAD-cache copy)");
        }
    }

    [Test]
    public void ClusterV_Destroy_FreesCollection()
    {
        using var dbe = SetupEngineWithGrid();
        var id = SpawnWithItems(dbe, 50f, 50f, 10, out _);

        ComponentCollection<int> handle;
        using (var t = dbe.CreateQuickTransaction())
        {
            handle = t.Open(id).Read(ClCcUnit.Bag).Items;
            Assert.That(t.GetComponentCollectionRefCounter(ref handle), Is.EqualTo(1), "live buffer refcount 1");
        }

        using (var t = dbe.CreateQuickTransaction())
        {
            t.Destroy(id);
            Assert.That(t.Commit(), Is.True, "destroy commit");
        }
        // Two passes: pass 1 cleans the revisions and ENQUEUES the content-chunk free (TSN-gated);
        // pass 2 (now mature) physically frees the chunk → releases the CC buffer.
        dbe.FlushDeferredCleanups();
        dbe.FlushDeferredCleanups();

        // The Versioned content chunk is freed via FreeContentChunk, which releases the buffer exactly once.
        // The cluster-slot HEAD-cache alias is NOT an owner (ReleaseSlot must not release it for Versioned slots),
        // so there is no double-free. After cleanup the buffer is gone (refcount 0).
        using (var t = dbe.CreateQuickTransaction())
        {
            Assert.That(t.GetComponentCollectionRefCounter(ref handle), Is.EqualTo(0),
                "buffer freed exactly once after destroy + cleanup");
        }
    }
}
