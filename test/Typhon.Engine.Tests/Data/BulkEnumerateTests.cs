using System.Collections.Generic;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;

namespace Typhon.Engine.Tests;

class BulkEnumerateTests : TestBase<BulkEnumerateTests>
{
    [OneTimeSetUp]
    public void OneTimeSetup()
    {
    }

    [Test]
    public void PK_FullScan()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        dbe.InitializeArchetypes();

        var createdIds = new List<EntityId>();
        using (var tx = dbe.CreateQuickTransaction())
        {
            for (int i = 0; i < 10; i++)
            {
                var comp = new CompA(i * 100, i * 1.5f, i * 2.5);
                createdIds.Add(tx.Spawn<CompAArch>(CompAArch.A.Set(in comp)));
            }

            tx.Commit();
        }

        using (var tx = dbe.CreateQuickTransaction())
        {
            var results = tx.Query<CompAArch>().Execute();

            Assert.That(results.Count, Is.EqualTo(10));

            // Verify all created entity IDs are present
            foreach (var id in createdIds)
            {
                Assert.That(results, Does.Contain(id));
            }

            // Verify data integrity for the first entity (A == 0)
            var firstId = createdIds[0];
            var firstComp = tx.Open(firstId).Read(CompAArch.A);
            Assert.That(firstComp.A, Is.EqualTo(0));
            Assert.That(firstComp.B, Is.EqualTo(0f));
        }
    }

    [Test]
    public void Query_AllEntitiesReadable()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        dbe.InitializeArchetypes();

        var createdIds = new List<EntityId>();
        using (var tx = dbe.CreateQuickTransaction())
        {
            for (int i = 0; i < 10; i++)
            {
                var comp = new CompA(i);
                createdIds.Add(tx.Spawn<CompAArch>(CompAArch.A.Set(in comp)));
            }

            tx.Commit();
        }

        using (var tx = dbe.CreateQuickTransaction())
        {
            var results = tx.Query<CompAArch>().Execute();

            Assert.That(results.Count, Is.EqualTo(10));

            // Verify all entities are readable and contain expected data
            var readValues = new List<int>();
            foreach (var id in results)
            {
                var comp = tx.Open(id).Read(CompAArch.A);
                readValues.Add(comp.A);
            }

            // All original values [0..9] should be present
            readValues.Sort();
            for (int i = 0; i < 10; i++)
            {
                Assert.That(readValues[i], Is.EqualTo(i));
            }
        }
    }

    [Test]
    public void Query_NoEntities_ReturnsEmpty()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        dbe.InitializeArchetypes();

        using (var tx = dbe.CreateQuickTransaction())
        {
            for (int i = 0; i < 5; i++)
            {
                var comp = new CompA(i);
                tx.Spawn<CompAArch>(CompAArch.A.Set(in comp));
            }

            tx.Commit();
        }

        // Destroy all entities
        using (var tx = dbe.CreateQuickTransaction())
        {
            var ids = tx.Query<CompAArch>().Execute();
            foreach (var id in ids)
            {
                tx.Destroy(id);
            }

            tx.Commit();
        }

        // Query should return empty
        using (var tx = dbe.CreateQuickTransaction())
        {
            var results = tx.Query<CompAArch>().Execute();
            Assert.That(results.Count, Is.EqualTo(0));
        }
    }

    [Test]
    public void MVCC_Visibility()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        dbe.InitializeArchetypes();

        // tx1: Create 5 entities
        var entityIds = new EntityId[5];
        using (var tx1 = dbe.CreateQuickTransaction())
        {
            for (int i = 0; i < 5; i++)
            {
                var comp = new CompA(i);
                entityIds[i] = tx1.Spawn<CompAArch>(CompAArch.A.Set(in comp));
            }

            tx1.Commit();
        }

        // tx2: Delete entities at indices 1 and 3
        using (var tx2 = dbe.CreateQuickTransaction())
        {
            tx2.Destroy(entityIds[1]);
            tx2.Destroy(entityIds[3]);
            tx2.Commit();
        }

        // tx3: Query — should see only 3 entities (0, 2, 4)
        using (var tx3 = dbe.CreateQuickTransaction())
        {
            var results = tx3.Query<CompAArch>().Execute();

            Assert.That(results.Count, Is.EqualTo(3));
            Assert.That(results, Does.Contain(entityIds[0]));
            Assert.That(results, Does.Contain(entityIds[2]));
            Assert.That(results, Does.Contain(entityIds[4]));
            Assert.That(results, Does.Not.Contain(entityIds[1]));
            Assert.That(results, Does.Not.Contain(entityIds[3]));
        }
    }

    [Test]
    public void DeletedEntity_Skipped()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        dbe.InitializeArchetypes();

        EntityId entityId;
        using (var tx = dbe.CreateQuickTransaction())
        {
            var comp = new CompA(42);
            entityId = tx.Spawn<CompAArch>(CompAArch.A.Set(in comp));
            tx.Commit();
        }

        // Delete it
        using (var tx = dbe.CreateQuickTransaction())
        {
            tx.Destroy(entityId);
            tx.Commit();
        }

        // Query — should yield nothing
        using (var tx = dbe.CreateQuickTransaction())
        {
            var results = tx.Query<CompAArch>().Execute();
            Assert.That(results.Count, Is.EqualTo(0));
        }
    }

    [Test]
    public void SecondaryIndex_UniqueField()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        dbe.InitializeArchetypes();

        // CompD has [Index] int B (unique secondary index)
        using (var tx = dbe.CreateQuickTransaction())
        {
            for (int i = 0; i < 10; i++)
            {
                var comp = new CompD(i * 1.1f, i * 10, i * 2.2);
                tx.Spawn<CompDArch>(CompDArch.D.Set(in comp));
            }

            tx.Commit();
        }

        var indexRef = dbe.GetIndexRef<CompD, int>(d => d.B);

        using (var tx = dbe.CreateQuickTransaction())
        {
            var results = new List<(long PK, int Key, CompD Comp)>();
            using var enumerator = tx.EnumerateIndex<CompD, int>(indexRef, int.MinValue, int.MaxValue);
            foreach (var item in enumerator)
            {
                results.Add((item.EntityPK, item.Key, item.Component));
            }

            Assert.That(results.Count, Is.EqualTo(10));

            // Verify ascending key order
            for (int i = 1; i < results.Count; i++)
            {
                Assert.That(results[i].Key, Is.GreaterThanOrEqualTo(results[i - 1].Key));
            }

            // Verify data integrity
            var entry = results.Find(r => r.Key == 50);
            Assert.That(entry.Comp.A, Is.EqualTo(5 * 1.1f).Within(0.01f));
        }
    }

    [Test]
    public void SecondaryIndex_AllowMultiple()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        dbe.InitializeArchetypes();

        // CompD has [Index(AllowMultiple = true)] float A
        // Create entities with duplicate A values
        using (var tx = dbe.CreateQuickTransaction())
        {
            for (int i = 0; i < 6; i++)
            {
                // Groups: A=1.0 (3 entities), A=2.0 (3 entities)
                var comp = new CompD(i < 3 ? 1.0f : 2.0f, i * 10, i * 0.5);
                tx.Spawn<CompDArch>(CompDArch.D.Set(in comp));
            }

            tx.Commit();
        }

        var indexRef = dbe.GetIndexRef<CompD, float>(d => d.A);

        using (var tx = dbe.CreateQuickTransaction())
        {
            var results = new List<(long PK, float Key, CompD Comp)>();
            using var enumerator = tx.EnumerateIndex<CompD, float>(indexRef, float.MinValue, float.MaxValue);
            foreach (var item in enumerator)
            {
                results.Add((item.EntityPK, item.Key, item.Component));
            }

            Assert.That(results.Count, Is.EqualTo(6));

            // Verify ascending key order
            for (int i = 1; i < results.Count; i++)
            {
                Assert.That(results[i].Key, Is.GreaterThanOrEqualTo(results[i - 1].Key));
            }
        }
    }

    [Test]
    public void SecondaryIndex_PK_Recovery()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        dbe.InitializeArchetypes();

        var pkToValue = new Dictionary<long, int>();
        using (var tx = dbe.CreateQuickTransaction())
        {
            for (int i = 0; i < 5; i++)
            {
                var comp = new CompD(i * 1.0f, i * 100, 0.0);
                long pk = (long)tx.Spawn<CompDArch>(CompDArch.D.Set(in comp)).RawValue;
                pkToValue[pk] = i * 100;
            }

            tx.Commit();
        }

        var indexRef = dbe.GetIndexRef<CompD, int>(d => d.B);

        using (var tx = dbe.CreateQuickTransaction())
        {
            using var enumerator = tx.EnumerateIndex<CompD, int>(indexRef, int.MinValue, int.MaxValue);
            foreach (var item in enumerator)
            {
                // Verify the recovered PK matches the expected one for this B value
                Assert.That(pkToValue.ContainsKey(item.EntityPK), Is.True,
                    $"EntityPK {item.EntityPK} should exist in our created set");
                Assert.That(pkToValue[item.EntityPK], Is.EqualTo(item.Key),
                    "PK recovery from secondary index should yield the correct entity");
            }
        }
    }

    [Test]
    public void ReadOnlyTransaction_Query()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        dbe.InitializeArchetypes();

        var createdIds = new List<EntityId>();
        using (var tx = dbe.CreateQuickTransaction())
        {
            for (int i = 0; i < 5; i++)
            {
                var comp = new CompA(i);
                createdIds.Add(tx.Spawn<CompAArch>(CompAArch.A.Set(in comp)));
            }

            tx.Commit();
        }

        // Query with a read-only transaction
        using var rt = dbe.CreateReadOnlyTransaction();
        Assert.That(rt.IsReadOnly, Is.True);

        var results = rt.Query<CompAArch>().Execute();
        Assert.That(results.Count, Is.EqualTo(5));

        // Verify component data is readable via Open().Read()
        foreach (var id in createdIds)
        {
            Assert.That(results, Does.Contain(id));
            var comp = rt.Open(id).Read(CompAArch.A);
            Assert.That(comp.A, Is.GreaterThanOrEqualTo(0));
            Assert.That(comp.A, Is.LessThan(5));
        }
    }

    [Test]
    public void EpochRefresh_LargeDataset()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        dbe.InitializeArchetypes();

        // Create >256 entities to trigger multiple epoch refreshes (every 128 entities)
        using (var tx = dbe.CreateQuickTransaction())
        {
            for (int i = 0; i < 300; i++)
            {
                var comp = new CompA(i);
                tx.Spawn<CompAArch>(CompAArch.A.Set(in comp));
            }

            tx.Commit();
        }

        using (var tx = dbe.CreateQuickTransaction())
        {
            var results = tx.Query<CompAArch>().Execute();
            Assert.That(results.Count, Is.EqualTo(300));
        }
    }

    [Test]
    public void StaleIndexRef_Throws()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);

        var ct = dbe.GetComponentTable<CompD>();
        // Create a stale IndexRef with wrong layout version
        var staleRef = new IndexRef(0, ct, ct.IndexLayoutVersion - 1);

        using var tx = dbe.CreateQuickTransaction();

        Assert.Throws<System.InvalidOperationException>(() =>
            tx.EnumerateIndex<CompD, float>(staleRef, float.MinValue, float.MaxValue));
    }

    [Test]
    public void ZeroCopy_ReadReturnsRefIntoPageMemory()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        dbe.InitializeArchetypes();

        var createdIds = new List<EntityId>();
        using (var tx = dbe.CreateQuickTransaction())
        {
            for (int i = 0; i < 5; i++)
            {
                var comp = new CompA(i * 100, i * 1.5f, i * 2.5);
                createdIds.Add(tx.Spawn<CompAArch>(CompAArch.A.Set(in comp)));
            }

            tx.Commit();
        }

        using (var tx = dbe.CreateQuickTransaction())
        {
            var results = tx.Query<CompAArch>().Execute();
            Assert.That(results.Count, Is.EqualTo(5));

            // Verify each entity is readable via Open().Read() — returns ref readonly (zero-copy)
            foreach (var id in createdIds)
            {
                Assert.That(results, Does.Contain(id));
                ref readonly var comp = ref tx.Open(id).Read(CompAArch.A);
                Assert.That(comp.A % 100, Is.EqualTo(0));
            }

            // Verify specific data integrity by reading each created entity in order
            for (int i = 0; i < createdIds.Count; i++)
            {
                ref readonly var comp = ref tx.Open(createdIds[i]).Read(CompAArch.A);
                Assert.That(comp.A, Is.EqualTo(i * 100));
                Assert.That(comp.B, Is.EqualTo(i * 1.5f).Within(0.01f));
            }
        }
    }
}
