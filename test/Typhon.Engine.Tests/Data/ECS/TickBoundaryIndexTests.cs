using System.Runtime.InteropServices;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests;

// ═══════════════════════════════════════════════════════════════════════════════
// Test-only SV component with indexed field (320)
// ═══════════════════════════════════════════════════════════════════════════════

[Component("Typhon.Test.ECS.TB.SvIndexed", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct TbSvData
{
    [Index(AllowMultiple = true)]
    public int Category;
    public int Value;
    public TbSvData(int cat, int val) { Category = cat; Value = val; }
}

[Archetype(320)]
class TbSvArch : Archetype<TbSvArch>
{
    public static readonly Comp<TbSvData> Data = Register<TbSvData>();
}

// ═══════════════════════════════════════════════════════════════════════════════
// Tests: SV tick-boundary deferred index maintenance
// ═══════════════════════════════════════════════════════════════════════════════

[TestFixture]
[NonParallelizable]
class TickBoundaryIndexTests : TestBase<TickBoundaryIndexTests>
{
    private DatabaseEngine SetupEngine()
    {
        var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<TbSvData>();
        dbe.InitializeArchetypes();
        return dbe;
    }

    private static EntityId SpawnSv(Transaction tx, int category, int value = 0)
    {
        var d = new TbSvData(category, value);
        return tx.Spawn<TbSvArch>(TbSvArch.Data.Set(in d));
    }

    /// <summary>
    /// Verify index state by directly querying the B+Tree.
    /// Returns true if the index contains an entry with the given category key.
    /// For cluster-eligible archetypes, checks the per-archetype B+Tree.
    /// </summary>
    private unsafe bool IndexContainsKey(DatabaseEngine dbe, int category)
    {
        using var epoch = EpochGuard.Enter(dbe.EpochManager);
        var meta = Archetype<TbSvArch>.Metadata;

        // Phase 3a: cluster-eligible archetypes use per-archetype B+Trees
        if (meta.HasClusterIndexes)
        {
            var clusterState = dbe._archetypeStates[meta.ArchetypeId].ClusterState;
            if (clusterState?.IndexSlots != null && clusterState.IndexSlots.Length > 0)
            {
                ref var field = ref clusterState.IndexSlots[0].Fields[0];
                var accessor = field.Index.Segment.CreateChunkAccessor();
                try
                {
                    var result = field.Index.TryGet(&category, ref accessor);
                    return result.IsSuccess;
                }
                finally
                {
                    accessor.Dispose();
                }
            }
        }

        var table = dbe.GetComponentTable<TbSvData>();
        var ifi = table.IndexedFieldInfos[0]; // Category is the first (only) indexed field
        var accessor2 = ifi.PersistentIndex.Segment.CreateChunkAccessor();
        try
        {
            var result2 = ifi.PersistentIndex.TryGet(&category, ref accessor2);
            return result2.IsSuccess;
        }
        finally
        {
            accessor2.Dispose();
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // 1 — Basic SV mutation + tick boundary → index updated
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public void SvMutation_AfterTickFence_IndexReflectsNewValue()
    {
        using var dbe = SetupEngine();
        var comp = TbSvArch.Data;

        EntityId id;
        using (var tx = dbe.CreateQuickTransaction())
        {
            id = SpawnSv(tx, 10, 100);
            tx.Commit();
        }
        dbe.WriteTickFence(1);

        // Mutate Category 10 → 20
        using (var tx = dbe.CreateQuickTransaction())
        {
            tx.OpenMut(id).Write(comp) = new TbSvData(20, 200);
            tx.Commit();
        }
        dbe.WriteTickFence(2);

        Assert.That(IndexContainsKey(dbe, 20), Is.True, "New value in index");
        Assert.That(IndexContainsKey(dbe, 10), Is.False, "Old value removed from index");
    }

    // ═══════════════════════════════════════════════════════════════════════
    // 2 — Double mutation same tick → only final value in index
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public void SvDoubleMutation_SameTick_IndexHasFinalValue()
    {
        using var dbe = SetupEngine();
        var comp = TbSvArch.Data;

        EntityId id;
        using (var tx = dbe.CreateQuickTransaction())
        {
            id = SpawnSv(tx, 1);
            tx.Commit();
        }
        dbe.WriteTickFence(1);

        // Two mutations same tick: 1 → 5 → 9
        using (var tx = dbe.CreateQuickTransaction())
        {
            tx.OpenMut(id).Write(comp) = new TbSvData(5, 0);
            tx.Commit();
        }
        using (var tx = dbe.CreateQuickTransaction())
        {
            tx.OpenMut(id).Write(comp) = new TbSvData(9, 0);
            tx.Commit();
        }
        dbe.WriteTickFence(2);

        Assert.That(IndexContainsKey(dbe, 9), Is.True, "Final value");
        Assert.That(IndexContainsKey(dbe, 1), Is.False, "Original value");
        Assert.That(IndexContainsKey(dbe, 5), Is.False, "Intermediate value");
    }

    // ═══════════════════════════════════════════════════════════════════════
    // 3 — Spawn + mutate same tick → index has mutated value
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public void SpawnAndMutate_SameTick_IndexHasMutatedValue()
    {
        using var dbe = SetupEngine();
        var comp = TbSvArch.Data;

        EntityId id;
        using (var tx = dbe.CreateQuickTransaction())
        {
            id = SpawnSv(tx, 100);
            tx.Commit();
        }

        // Mutate same tick (no WriteTickFence between)
        using (var tx = dbe.CreateQuickTransaction())
        {
            tx.OpenMut(id).Write(comp) = new TbSvData(200, 0);
            tx.Commit();
        }
        dbe.WriteTickFence(1);

        Assert.That(IndexContainsKey(dbe, 200), Is.True);
        Assert.That(IndexContainsKey(dbe, 100), Is.False);
    }

    // ═══════════════════════════════════════════════════════════════════════
    // 4 — Destroy without write → index entry removed
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public void DestroyWithoutWrite_IndexEntryRemoved()
    {
        using var dbe = SetupEngine();

        EntityId id;
        using (var tx = dbe.CreateQuickTransaction())
        {
            id = SpawnSv(tx, 42);
            tx.Commit();
        }
        dbe.WriteTickFence(1);
        Assert.That(IndexContainsKey(dbe, 42), Is.True, "Pre-destroy");

        using (var tx = dbe.CreateQuickTransaction())
        {
            tx.Destroy(id);
            tx.Commit();
        }
        dbe.WriteTickFence(2);

        Assert.That(IndexContainsKey(dbe, 42), Is.False, "Post-destroy");
    }

    // ═══════════════════════════════════════════════════════════════════════
    // 5 — Write then destroy same tick → index entry removed
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public void WriteThenDestroy_SameTick_IndexEntryRemoved()
    {
        using var dbe = SetupEngine();
        var comp = TbSvArch.Data;

        EntityId id;
        using (var tx = dbe.CreateQuickTransaction())
        {
            id = SpawnSv(tx, 77);
            tx.Commit();
        }
        dbe.WriteTickFence(1);

        // Mutate then destroy — same tick
        using (var tx = dbe.CreateQuickTransaction())
        {
            tx.OpenMut(id).Write(comp) = new TbSvData(88, 0);
            tx.Commit();
        }
        using (var tx = dbe.CreateQuickTransaction())
        {
            tx.Destroy(id);
            tx.Commit();
        }
        dbe.WriteTickFence(2);

        Assert.That(IndexContainsKey(dbe, 77), Is.False, "Old value");
        Assert.That(IndexContainsKey(dbe, 88), Is.False, "Mutated value");
    }

    // ═══════════════════════════════════════════════════════════════════════
    // 6 — Write same value → index unchanged (no-op)
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public void WriteWithoutChange_IndexUnchanged()
    {
        using var dbe = SetupEngine();
        var comp = TbSvArch.Data;

        EntityId id;
        using (var tx = dbe.CreateQuickTransaction())
        {
            id = SpawnSv(tx, 55);
            tx.Commit();
        }
        dbe.WriteTickFence(1);

        // Write same Category, only Value changes
        using (var tx = dbe.CreateQuickTransaction())
        {
            tx.OpenMut(id).Write(comp) = new TbSvData(55, 999);
            tx.Commit();
        }
        dbe.WriteTickFence(2);

        Assert.That(IndexContainsKey(dbe, 55), Is.True);
    }

    // ═══════════════════════════════════════════════════════════════════════
    // 7 — Multiple entities, mixed mutation patterns
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public void MultipleEntities_MixedMutations_IndexCorrect()
    {
        using var dbe = SetupEngine();
        var comp = TbSvArch.Data;

        EntityId id1, id2, id3;
        using (var tx = dbe.CreateQuickTransaction())
        {
            id1 = SpawnSv(tx, 1);
            id2 = SpawnSv(tx, 2);
            id3 = SpawnSv(tx, 3);
            tx.Commit();
        }
        dbe.WriteTickFence(1);

        // Entity 1: mutate (1 → 10), Entity 2: unchanged, Entity 3: destroyed
        using (var tx = dbe.CreateQuickTransaction())
        {
            tx.OpenMut(id1).Write(comp) = new TbSvData(10, 0);
            tx.Commit();
        }
        using (var tx = dbe.CreateQuickTransaction())
        {
            tx.Destroy(id3);
            tx.Commit();
        }
        dbe.WriteTickFence(2);

        Assert.That(IndexContainsKey(dbe, 10), Is.True, "Entity 1 mutated");
        Assert.That(IndexContainsKey(dbe, 1), Is.False, "Entity 1 old value gone");
        Assert.That(IndexContainsKey(dbe, 2), Is.True, "Entity 2 unchanged");
        Assert.That(IndexContainsKey(dbe, 3), Is.False, "Entity 3 destroyed");
    }

    // ═══════════════════════════════════════════════════════════════════════
    // 8 — Multiple ticks accumulate correctly
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public void MultipleTicks_IndexTracksAcrossTicks()
    {
        using var dbe = SetupEngine();
        var comp = TbSvArch.Data;

        EntityId id;
        using (var tx = dbe.CreateQuickTransaction())
        {
            id = SpawnSv(tx, 1);
            tx.Commit();
        }
        dbe.WriteTickFence(1);

        // Tick 2: 1 → 2
        using (var tx = dbe.CreateQuickTransaction())
        {
            tx.OpenMut(id).Write(comp) = new TbSvData(2, 0);
            tx.Commit();
        }
        dbe.WriteTickFence(2);

        // Tick 3: 2 → 3
        using (var tx = dbe.CreateQuickTransaction())
        {
            tx.OpenMut(id).Write(comp) = new TbSvData(3, 0);
            tx.Commit();
        }
        dbe.WriteTickFence(3);

        Assert.That(IndexContainsKey(dbe, 3), Is.True);
        Assert.That(IndexContainsKey(dbe, 2), Is.False);
        Assert.That(IndexContainsKey(dbe, 1), Is.False);
    }

    // ═══════════════════════════════════════════════════════════════════════
    // 9 — WhereField Count on SV uses index directly (PipelineExecutor SV path)
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public void WhereField_Count_WorksForSV()
    {
        using var dbe = SetupEngine();

        using (var tx = dbe.CreateQuickTransaction())
        {
            SpawnSv(tx, 10);
            SpawnSv(tx, 20);
            SpawnSv(tx, 20);
            SpawnSv(tx, 30);
            tx.Commit();
        }
        dbe.WriteTickFence(1);

        using (var tx = dbe.CreateQuickTransaction())
        {
            Assert.That(tx.Query<TbSvArch>().WhereField<TbSvData>(d => d.Category == 20).Count(), Is.EqualTo(2));
            Assert.That(tx.Query<TbSvArch>().WhereField<TbSvData>(d => d.Category == 10).Count(), Is.EqualTo(1));
            Assert.That(tx.Query<TbSvArch>().WhereField<TbSvData>(d => d.Category == 99).Count(), Is.EqualTo(0));
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // 10 — WhereField Execute on SV uses broad scan fallback
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public void WhereField_Execute_WorksForSV()
    {
        using var dbe = SetupEngine();

        EntityId id1, id2;
        using (var tx = dbe.CreateQuickTransaction())
        {
            id1 = SpawnSv(tx, 42);
            SpawnSv(tx, 99);
            id2 = SpawnSv(tx, 42);
            tx.Commit();
        }
        dbe.WriteTickFence(1);

        using (var tx = dbe.CreateQuickTransaction())
        {
            var results = tx.Query<TbSvArch>().WhereField<TbSvData>(d => d.Category == 42).Execute();
            Assert.That(results, Has.Count.EqualTo(2));
            Assert.That(results, Does.Contain(id1));
            Assert.That(results, Does.Contain(id2));
        }
    }
}
