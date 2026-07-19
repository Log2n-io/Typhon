using System.Runtime.InteropServices;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests;

// ═══════════════════════════════════════════════════════════════════════════════
// Test-only Transient component with indexed field (360)
// ═══════════════════════════════════════════════════════════════════════════════

[Component("Typhon.Test.ECS.TI.TransientIndexed", 1, StorageMode = StorageMode.Transient)]
[StructLayout(LayoutKind.Sequential)]
struct TiTransientData
{
    [Index(AllowMultiple = true)]
    public int Category;
    [Index(AllowMultiple = true)]
    public int Value;
    public TiTransientData(int cat, int val) { Category = cat; Value = val; }
}

[Archetype]
class TiTransientArch : Archetype<TiTransientArch>
{
    public static readonly Comp<TiTransientData> Data = Register<TiTransientData>();
}

// ═══════════════════════════════════════════════════════════════════════════════
// Tests: Transient secondary index end-to-end
// ═══════════════════════════════════════════════════════════════════════════════

[TestFixture]
[NonParallelizable]
class TransientIndexTests : TestBase<TransientIndexTests>
{
    private DatabaseEngine SetupEngine()
    {
        var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<TiTransientData>();
        dbe.InitializeArchetypes();
        return dbe;
    }

    private static EntityId SpawnTransient(Transaction tx, int category, int value = 0)
    {
        var d = new TiTransientData(category, value);
        return tx.Spawn<TiTransientArch>(TiTransientArch.Data.Set(in d));
    }

    // ═══════════════════════════════════════════════════════════════════════
    // 1 — Spawn a Transient indexed entity, WhereField query finds it
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public void Spawn_TransientIndexed_QueryFindsEntity()
    {
        using var dbe = SetupEngine();

        EntityId id;
        using (var tx = dbe.CreateQuickTransaction())
        {
            id = SpawnTransient(tx, 42, 100);
            tx.Commit();
        }
        dbe.WriteTickFence(1);

        using (var tx = dbe.CreateQuickTransaction())
        {
            var results = tx.Query<TiTransientArch>().WhereField<TiTransientData>(d => d.Category == 42).Execute();
            Assert.That(results, Has.Count.EqualTo(1));
            Assert.That(results, Does.Contain(id));
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // 2 — Spawn multiple, Count matches
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public void Spawn_Multiple_QueryCountCorrect()
    {
        using var dbe = SetupEngine();

        using (var tx = dbe.CreateQuickTransaction())
        {
            SpawnTransient(tx, 10, 1);
            SpawnTransient(tx, 10, 2);
            SpawnTransient(tx, 20, 3);
            tx.Commit();
        }
        dbe.WriteTickFence(1);

        using (var tx = dbe.CreateQuickTransaction())
        {
            Assert.That(tx.Query<TiTransientArch>().WhereField<TiTransientData>(d => d.Category == 10).Count(), Is.EqualTo(2));
            Assert.That(tx.Query<TiTransientArch>().WhereField<TiTransientData>(d => d.Category == 20).Count(), Is.EqualTo(1));
            Assert.That(tx.Query<TiTransientArch>().WhereField<TiTransientData>(d => d.Category == 99).Count(), Is.EqualTo(0));
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // 3 — Write updates index after tick fence
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public void Write_UpdatesIndex_AfterTickFence()
    {
        using var dbe = SetupEngine();
        var comp = TiTransientArch.Data;

        EntityId id;
        using (var tx = dbe.CreateQuickTransaction())
        {
            id = SpawnTransient(tx, 10, 100);
            tx.Commit();
        }
        dbe.WriteTickFence(1);

        // Mutate Category 10 → 20
        using (var tx = dbe.CreateQuickTransaction())
        {
            tx.OpenMut(id).Write(comp) = new TiTransientData(20, 200);
            tx.Commit();
        }
        dbe.WriteTickFence(2);

        using (var tx = dbe.CreateQuickTransaction())
        {
            Assert.That(tx.Query<TiTransientArch>().WhereField<TiTransientData>(d => d.Category == 20).Count(), Is.EqualTo(1));
            Assert.That(tx.Query<TiTransientArch>().WhereField<TiTransientData>(d => d.Category == 10).Count(), Is.EqualTo(0));
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // 4 — Write same value doesn't corrupt index
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public void Write_SameValue_IndexUnchanged()
    {
        using var dbe = SetupEngine();
        var comp = TiTransientArch.Data;

        EntityId id;
        using (var tx = dbe.CreateQuickTransaction())
        {
            id = SpawnTransient(tx, 42, 100);
            tx.Commit();
        }
        dbe.WriteTickFence(1);

        // Write same Category value
        using (var tx = dbe.CreateQuickTransaction())
        {
            tx.OpenMut(id).Write(comp) = new TiTransientData(42, 200); // Category unchanged
            tx.Commit();
        }
        dbe.WriteTickFence(2);

        using (var tx = dbe.CreateQuickTransaction())
        {
            Assert.That(tx.Query<TiTransientArch>().WhereField<TiTransientData>(d => d.Category == 42).Count(), Is.EqualTo(1));
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // 5 — Destroy removes from index
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public void Destroy_RemovesFromIndex()
    {
        using var dbe = SetupEngine();

        EntityId id;
        using (var tx = dbe.CreateQuickTransaction())
        {
            id = SpawnTransient(tx, 42, 100);
            tx.Commit();
        }
        dbe.WriteTickFence(1);

        using (var tx = dbe.CreateQuickTransaction())
        {
            tx.Destroy(id);
            tx.Commit();
        }
        dbe.WriteTickFence(2);

        using (var tx = dbe.CreateQuickTransaction())
        {
            Assert.That(tx.Query<TiTransientArch>().WhereField<TiTransientData>(d => d.Category == 42).Count(), Is.EqualTo(0));
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // 6 — SpawnBatch + query finds all
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public void SpawnBatch_TransientIndexed_QueryFindsAll()
    {
        using var dbe = SetupEngine();

        using (var tx = dbe.CreateQuickTransaction())
        {
            for (int i = 0; i < 10; i++)
            {
                SpawnTransient(tx, 5, i);
            }
            tx.Commit();
        }
        dbe.WriteTickFence(1);

        using (var tx = dbe.CreateQuickTransaction())
        {
            Assert.That(tx.Query<TiTransientArch>().WhereField<TiTransientData>(d => d.Category == 5).Count(), Is.EqualTo(10));
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // 7 — WhereField Execute returns correct PKs
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public void WhereField_Execute_ReturnsCorrectPKs()
    {
        using var dbe = SetupEngine();

        EntityId id1, id2;
        using (var tx = dbe.CreateQuickTransaction())
        {
            id1 = SpawnTransient(tx, 42, 1);
            id2 = SpawnTransient(tx, 42, 2);
            SpawnTransient(tx, 99, 3); // different category
            tx.Commit();
        }
        dbe.WriteTickFence(1);

        using (var tx = dbe.CreateQuickTransaction())
        {
            var results = tx.Query<TiTransientArch>().WhereField<TiTransientData>(d => d.Category == 42).Execute();
            Assert.That(results, Has.Count.EqualTo(2));
            Assert.That(results, Does.Contain(id1));
            Assert.That(results, Does.Contain(id2));
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // 8 — Double mutation same tick → only final value in index
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public void DoubleMutation_SameTick_IndexHasFinalValue()
    {
        using var dbe = SetupEngine();
        var comp = TiTransientArch.Data;

        EntityId id;
        using (var tx = dbe.CreateQuickTransaction())
        {
            id = SpawnTransient(tx, 1, 100);
            tx.Commit();
        }
        dbe.WriteTickFence(1);

        // Two mutations in same tick: 1→2, then 2→3
        using (var tx = dbe.CreateQuickTransaction())
        {
            tx.OpenMut(id).Write(comp) = new TiTransientData(2, 200);
            tx.Commit();
        }
        using (var tx = dbe.CreateQuickTransaction())
        {
            tx.OpenMut(id).Write(comp) = new TiTransientData(3, 300);
            tx.Commit();
        }
        dbe.WriteTickFence(2);

        using (var tx = dbe.CreateQuickTransaction())
        {
            Assert.That(tx.Query<TiTransientArch>().WhereField<TiTransientData>(d => d.Category == 1).Count(), Is.EqualTo(0));
            Assert.That(tx.Query<TiTransientArch>().WhereField<TiTransientData>(d => d.Category == 2).Count(), Is.EqualTo(0));
            Assert.That(tx.Query<TiTransientArch>().WhereField<TiTransientData>(d => d.Category == 3).Count(), Is.EqualTo(1));
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // 9 — Mutate then destroy same tick → index clean
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public void MutateThenDestroy_SameTick_IndexClean()
    {
        using var dbe = SetupEngine();
        var comp = TiTransientArch.Data;

        EntityId id;
        using (var tx = dbe.CreateQuickTransaction())
        {
            id = SpawnTransient(tx, 10, 100);
            tx.Commit();
        }
        dbe.WriteTickFence(1);

        // Mutate then destroy in same tick
        using (var tx = dbe.CreateQuickTransaction())
        {
            tx.OpenMut(id).Write(comp) = new TiTransientData(20, 200);
            tx.Commit();
        }
        using (var tx = dbe.CreateQuickTransaction())
        {
            tx.Destroy(id);
            tx.Commit();
        }
        dbe.WriteTickFence(2);

        using (var tx = dbe.CreateQuickTransaction())
        {
            Assert.That(tx.Query<TiTransientArch>().WhereField<TiTransientData>(d => d.Category == 10).Count(), Is.EqualTo(0));
            Assert.That(tx.Query<TiTransientArch>().WhereField<TiTransientData>(d => d.Category == 20).Count(), Is.EqualTo(0));
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // 10 — High throughput spawn/destroy cycles, index stays consistent
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public void HighThroughput_SpawnDestroy_IndexConsistent()
    {
        using var dbe = SetupEngine();
        const int cycles = 100;

        for (int cycle = 0; cycle < cycles; cycle++)
        {
            EntityId id;
            using (var tx = dbe.CreateQuickTransaction())
            {
                id = SpawnTransient(tx, cycle % 10, cycle);
                tx.Commit();
            }

            if (cycle % 3 == 0)
            {
                using (var tx = dbe.CreateQuickTransaction())
                {
                    tx.Destroy(id);
                    tx.Commit();
                }
            }

            dbe.WriteTickFence(cycle + 1);
        }

        // Verify: count all categories 0-9, should sum to total alive entities
        int totalAlive = 0;
        using (var tx = dbe.CreateQuickTransaction())
        {
            for (int cat = 0; cat < 10; cat++)
            {
                totalAlive += tx.Query<TiTransientArch>().WhereField<TiTransientData>(d => d.Category == cat).Count();
            }
        }

        // Of 100 entities, every 3rd was destroyed (indices 0, 3, 6, ..., 99) = 34 destroyed
        Assert.That(totalAlive, Is.EqualTo(cycles - (cycles + 2) / 3));
    }

    // ═══════════════════════════════════════════════════════════════════════
    // 11 — Execute with non-primary filter (PipelineExecutor Transient path)
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public void WhereField_Execute_WithNonPrimaryFilter()
    {
        using var dbe = SetupEngine();

        using (var tx = dbe.CreateQuickTransaction())
        {
            SpawnTransient(tx, 5, 100);  // Category=5, Value=100
            SpawnTransient(tx, 5, 200);  // Category=5, Value=200
            SpawnTransient(tx, 5, 50);   // Category=5, Value=50
            SpawnTransient(tx, 9, 200);  // Category=9, Value=200 (different category)
            tx.Commit();
        }
        dbe.WriteTickFence(1);

        // Query with primary (Category == 5) AND non-primary filter (Value > 80)
        // This exercises ExecutePKsTypedNonVersioned + EvaluateFiltersNonVersioned
        using (var tx = dbe.CreateQuickTransaction())
        {
            var results = tx.Query<TiTransientArch>()
                .WhereField<TiTransientData>(d => d.Category == 5 && d.Value > 80)
                .Execute();
            Assert.That(results, Has.Count.EqualTo(2)); // Value=100 and Value=200
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // 12 — Count with non-primary filter (CountPKsTypedNonVersioned filter path)
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public void WhereField_Count_WithNonPrimaryFilter()
    {
        using var dbe = SetupEngine();

        using (var tx = dbe.CreateQuickTransaction())
        {
            SpawnTransient(tx, 3, 10);
            SpawnTransient(tx, 3, 20);
            SpawnTransient(tx, 3, 30);
            tx.Commit();
        }
        dbe.WriteTickFence(1);

        using (var tx = dbe.CreateQuickTransaction())
        {
            Assert.That(tx.Query<TiTransientArch>().WhereField<TiTransientData>(d => d.Category == 3 && d.Value >= 20).Count(), Is.EqualTo(2));
            Assert.That(tx.Query<TiTransientArch>().WhereField<TiTransientData>(d => d.Category == 3 && d.Value > 30).Count(), Is.EqualTo(0));
        }
    }
}
