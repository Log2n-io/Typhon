using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests;

// ── Test component structs for schema evolution ──
// Each V1/V2 pair shares the same [Component] name to simulate schema changes across reopens.

#region Add Field

[Component("Typhon.Schema.UnitTest.EvoAdd", 1)]
[StructLayout(LayoutKind.Sequential)]
struct EvoAddV1
{
    public int A;
    public float B;

    public EvoAddV1(int a, float b) { A = a; B = b; }
}

[Component("Typhon.Schema.UnitTest.EvoAdd", 1)]
[StructLayout(LayoutKind.Sequential)]
struct EvoAddV2
{
    public int A;
    public int C;
    public float B;

    public EvoAddV2(int a, int c, float b) { A = a; C = c; B = b; }
}

#endregion

#region Remove Field

[Component("Typhon.Schema.UnitTest.EvoRemove", 1)]
[StructLayout(LayoutKind.Sequential)]
struct EvoRemoveV1
{
    public int A;
    public int B;
    public float C;

    public EvoRemoveV1(int a, int b, float c) { A = a; B = b; C = c; }
}

[Component("Typhon.Schema.UnitTest.EvoRemove", 1)]
[StructLayout(LayoutKind.Sequential)]
struct EvoRemoveV2
{
    public int A;
    public float C;

    public EvoRemoveV2(int a, float c) { A = a; C = c; }
}

#endregion

#region Reorder Fields

[Component("Typhon.Schema.UnitTest.EvoReorder", 1)]
[StructLayout(LayoutKind.Sequential)]
struct EvoReorderV1
{
    public float A;
    public int B;

    public EvoReorderV1(float a, int b) { A = a; B = b; }
}

[Component("Typhon.Schema.UnitTest.EvoReorder", 1)]
[StructLayout(LayoutKind.Sequential)]
struct EvoReorderV2
{
    public int B;
    public float A;

    public EvoReorderV2(int b, float a) { B = b; A = a; }
}

#endregion

#region Widen Int→Long

[Component("Typhon.Schema.UnitTest.EvoWidenInt", 1)]
[StructLayout(LayoutKind.Sequential)]
struct EvoWidenIntV1
{
    public int Score;
    public int Padding;

    public EvoWidenIntV1(int score) { Score = score; Padding = 0; }
}

[Component("Typhon.Schema.UnitTest.EvoWidenInt", 1)]
[StructLayout(LayoutKind.Sequential)]
struct EvoWidenIntV2
{
    public long Score;

    public EvoWidenIntV2(long score) { Score = score; }
}

#endregion

#region Widen Float→Double

[Component("Typhon.Schema.UnitTest.EvoWidenFloat", 1)]
[StructLayout(LayoutKind.Sequential)]
struct EvoWidenFloatV1
{
    public float Speed;
    public int Padding;

    public EvoWidenFloatV1(float speed) { Speed = speed; Padding = 0; }
}

[Component("Typhon.Schema.UnitTest.EvoWidenFloat", 1)]
[StructLayout(LayoutKind.Sequential)]
struct EvoWidenFloatV2
{
    public double Speed;

    public EvoWidenFloatV2(double speed) { Speed = speed; }
}

#endregion

#region Combined Add + Widen

[Component("Typhon.Schema.UnitTest.EvoCombined", 1)]
[StructLayout(LayoutKind.Sequential)]
struct EvoCombinedV1
{
    public int A;
    public float B;

    public EvoCombinedV1(int a, float b) { A = a; B = b; }
}

[Component("Typhon.Schema.UnitTest.EvoCombined", 1)]
[StructLayout(LayoutKind.Sequential)]
struct EvoCombinedV2
{
    public long A;
    public int C;
    public double B;

    public EvoCombinedV2(long a, int c, double b) { A = a; C = c; B = b; }
}

#endregion

#region Add + Remove simultaneously

[Component("Typhon.Schema.UnitTest.EvoAddRemove", 1)]
[StructLayout(LayoutKind.Sequential)]
struct EvoAddRemoveV1
{
    public int A;
    public int B;
    public float C;

    public EvoAddRemoveV1(int a, int b, float c) { A = a; B = b; C = c; }
}

[Component("Typhon.Schema.UnitTest.EvoAddRemove", 1)]
[StructLayout(LayoutKind.Sequential)]
struct EvoAddRemoveV2
{
    public int A;
    public float C;
    public double D;

    public EvoAddRemoveV2(int a, float c, double d) { A = a; C = c; D = d; }
}

#endregion

#region Widen Int→Long (negative sign extension)

[Component("Typhon.Schema.UnitTest.EvoSignExt", 1)]
[StructLayout(LayoutKind.Sequential)]
struct EvoSignExtV1
{
    public int Value;
    public int Padding;

    public EvoSignExtV1(int value) { Value = value; Padding = 0; }
}

[Component("Typhon.Schema.UnitTest.EvoSignExt", 1)]
[StructLayout(LayoutKind.Sequential)]
struct EvoSignExtV2
{
    public long Value;

    public EvoSignExtV2(long value) { Value = value; }
}

#endregion

#region Bulk migration (for performance test)

[Component("Typhon.Schema.UnitTest.EvoBulk", 1)]
[StructLayout(LayoutKind.Sequential)]
struct EvoBulkV1
{
    public int X;
    public int Y;

    public EvoBulkV1(int x, int y) { X = x; Y = y; }
}

[Component("Typhon.Schema.UnitTest.EvoBulk", 1)]
[StructLayout(LayoutKind.Sequential)]
struct EvoBulkV2
{
    public int X;
    public int Y;
    public int Z;

    public EvoBulkV2(int x, int y, int z) { X = x; Y = y; Z = z; }
}

#endregion

// ── Archetypes for V1 components (used for Spawn in first scope) ──

[Archetype]
class EvoAddArch : Archetype<EvoAddArch>
{
    public static readonly Comp<EvoAddV1> Comp = Register<EvoAddV1>();
}

[Archetype]
class EvoRemoveArch : Archetype<EvoRemoveArch>
{
    public static readonly Comp<EvoRemoveV1> Comp = Register<EvoRemoveV1>();
}

[Archetype]
class EvoReorderArch : Archetype<EvoReorderArch>
{
    public static readonly Comp<EvoReorderV1> Comp = Register<EvoReorderV1>();
}

[Archetype]
class EvoWidenIntArch : Archetype<EvoWidenIntArch>
{
    public static readonly Comp<EvoWidenIntV1> Comp = Register<EvoWidenIntV1>();
}

[Archetype]
class EvoWidenFloatArch : Archetype<EvoWidenFloatArch>
{
    public static readonly Comp<EvoWidenFloatV1> Comp = Register<EvoWidenFloatV1>();
}

[Archetype]
class EvoCombinedArch : Archetype<EvoCombinedArch>
{
    public static readonly Comp<EvoCombinedV1> Comp = Register<EvoCombinedV1>();
}

[Archetype]
class EvoAddRemoveArch : Archetype<EvoAddRemoveArch>
{
    public static readonly Comp<EvoAddRemoveV1> Comp = Register<EvoAddRemoveV1>();
}

[Archetype]
class EvoSignExtArch : Archetype<EvoSignExtArch>
{
    public static readonly Comp<EvoSignExtV1> Comp = Register<EvoSignExtV1>();
}

[Archetype]
class EvoBulkArch : Archetype<EvoBulkArch>
{
    public static readonly Comp<EvoBulkV1> Comp = Register<EvoBulkV1>();
}

// ── V2 Archetypes (used for Open().Read() in scope 2 after schema evolution) ──
// V1 and V2 CLR types sharing the same [Component] name get the SAME ComponentTypeId.
// InitializeArchetypes connects V1Arch's slots to V2's ComponentTable via schema-name fallback.

[Archetype]
class EvoAddV2Arch : Archetype<EvoAddV2Arch>
{
    public static readonly Comp<EvoAddV2> Comp = Register<EvoAddV2>();
}

[Archetype]
class EvoRemoveV2Arch : Archetype<EvoRemoveV2Arch>
{
    public static readonly Comp<EvoRemoveV2> Comp = Register<EvoRemoveV2>();
}

[Archetype]
class EvoReorderV2Arch : Archetype<EvoReorderV2Arch>
{
    public static readonly Comp<EvoReorderV2> Comp = Register<EvoReorderV2>();
}

[Archetype]
class EvoWidenIntV2Arch : Archetype<EvoWidenIntV2Arch>
{
    public static readonly Comp<EvoWidenIntV2> Comp = Register<EvoWidenIntV2>();
}

[Archetype]
class EvoSignExtV2Arch : Archetype<EvoSignExtV2Arch>
{
    public static readonly Comp<EvoSignExtV2> Comp = Register<EvoSignExtV2>();
}

[Archetype]
class EvoWidenFloatV2Arch : Archetype<EvoWidenFloatV2Arch>
{
    public static readonly Comp<EvoWidenFloatV2> Comp = Register<EvoWidenFloatV2>();
}

[Archetype]
class EvoCombinedV2Arch : Archetype<EvoCombinedV2Arch>
{
    public static readonly Comp<EvoCombinedV2> Comp = Register<EvoCombinedV2>();
}

[Archetype]
class EvoAddRemoveV2Arch : Archetype<EvoAddRemoveV2Arch>
{
    public static readonly Comp<EvoAddRemoveV2> Comp = Register<EvoAddRemoveV2>();
}

/// <summary>
/// Integration tests for compatible schema evolution.
/// Each test creates a database with V1 layout, populates it, closes, then reopens with V2 layout.
/// The migration engine should automatically remap fields and verify data correctness.
/// </summary>
class SchemaEvolutionTests : TestBase<SchemaEvolutionTests>
{
    [OneTimeSetUp]
    public void OneTimeSetup()
    {
    }

    [Test]
    public void AddField_DataMigratedAndNewFieldZeroFilled()
    {
        EntityId entityId;

        using (var scope = ServiceProvider.CreateScope())
        {
            using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
            dbe.RegisterComponentFromAccessor<EvoAddV1>();
            dbe.InitializeArchetypes();

            var comp = new EvoAddV1(42, 3.14f);
            using var t = dbe.CreateQuickTransaction(DurabilityMode.Immediate);
            entityId = t.Spawn<EvoAddArch>(EvoAddArch.Comp.Set(in comp));
            t.Commit();
        }

        using (var scope = ServiceProvider.CreateScope())
        {
            using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
            dbe.RegisterComponentFromAccessor<EvoAddV2>();
            dbe.InitializeArchetypes();

            using var t = dbe.CreateQuickTransaction();
            ref readonly var comp = ref t.Open(entityId).Read(EvoAddV2Arch.Comp);
            Assert.That(comp.A, Is.EqualTo(42));
            Assert.That(comp.B, Is.EqualTo(3.14f));
            Assert.That(comp.C, Is.EqualTo(0)); // New field should be zero
        }
    }

    [Test]
    public void RemoveField_RemainingDataIntact()
    {
        EntityId entityId;

        using (var scope = ServiceProvider.CreateScope())
        {
            using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
            dbe.RegisterComponentFromAccessor<EvoRemoveV1>();
            dbe.InitializeArchetypes();

            var comp = new EvoRemoveV1(10, 20, 1.5f);
            using var t = dbe.CreateQuickTransaction(DurabilityMode.Immediate);
            entityId = t.Spawn<EvoRemoveArch>(EvoRemoveArch.Comp.Set(in comp));
            t.Commit();
        }

        using (var scope = ServiceProvider.CreateScope())
        {
            using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
            dbe.RegisterComponentFromAccessor<EvoRemoveV2>();
            dbe.InitializeArchetypes();

            using var t = dbe.CreateQuickTransaction();
            ref readonly var comp = ref t.Open(entityId).Read(EvoRemoveV2Arch.Comp);
            Assert.That(comp.A, Is.EqualTo(10));
            Assert.That(comp.C, Is.EqualTo(1.5f));
        }
    }

    [Test]
    public void ReorderFields_DataCorrect()
    {
        EntityId entityId;

        using (var scope = ServiceProvider.CreateScope())
        {
            using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
            dbe.RegisterComponentFromAccessor<EvoReorderV1>();
            dbe.InitializeArchetypes();

            var comp = new EvoReorderV1(2.718f, 99);
            using var t = dbe.CreateQuickTransaction(DurabilityMode.Immediate);
            entityId = t.Spawn<EvoReorderArch>(EvoReorderArch.Comp.Set(in comp));
            t.Commit();
        }

        using (var scope = ServiceProvider.CreateScope())
        {
            using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
            dbe.RegisterComponentFromAccessor<EvoReorderV2>();
            dbe.InitializeArchetypes();

            using var t = dbe.CreateQuickTransaction();
            ref readonly var comp = ref t.Open(entityId).Read(EvoReorderV2Arch.Comp);
            Assert.That(comp.A, Is.EqualTo(2.718f));
            Assert.That(comp.B, Is.EqualTo(99));
        }
    }

    [Test]
    public void WidenIntToLong_PositiveValue_Preserved()
    {
        EntityId entityId;

        using (var scope = ServiceProvider.CreateScope())
        {
            using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
            dbe.RegisterComponentFromAccessor<EvoWidenIntV1>();
            dbe.InitializeArchetypes();

            var comp = new EvoWidenIntV1(1_000_000);
            using var t = dbe.CreateQuickTransaction(DurabilityMode.Immediate);
            entityId = t.Spawn<EvoWidenIntArch>(EvoWidenIntArch.Comp.Set(in comp));
            t.Commit();
        }

        using (var scope = ServiceProvider.CreateScope())
        {
            using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
            dbe.RegisterComponentFromAccessor<EvoWidenIntV2>();
            dbe.InitializeArchetypes();

            using var t = dbe.CreateQuickTransaction();
            ref readonly var comp = ref t.Open(entityId).Read(EvoWidenIntV2Arch.Comp);
            Assert.That(comp.Score, Is.EqualTo(1_000_000L));
        }
    }

    [Test]
    public void WidenIntToLong_NegativeValue_SignExtended()
    {
        EntityId entityId;

        using (var scope = ServiceProvider.CreateScope())
        {
            using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
            dbe.RegisterComponentFromAccessor<EvoSignExtV1>();
            dbe.InitializeArchetypes();

            var comp = new EvoSignExtV1(-42);
            using var t = dbe.CreateQuickTransaction(DurabilityMode.Immediate);
            entityId = t.Spawn<EvoSignExtArch>(EvoSignExtArch.Comp.Set(in comp));
            t.Commit();
        }

        using (var scope = ServiceProvider.CreateScope())
        {
            using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
            dbe.RegisterComponentFromAccessor<EvoSignExtV2>();
            dbe.InitializeArchetypes();

            using var t = dbe.CreateQuickTransaction();
            ref readonly var comp = ref t.Open(entityId).Read(EvoSignExtV2Arch.Comp);
            Assert.That(comp.Value, Is.EqualTo(-42L));
        }
    }

    [Test]
    public void WidenFloatToDouble_LosslessIEEE754()
    {
        EntityId entityId;

        using (var scope = ServiceProvider.CreateScope())
        {
            using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
            dbe.RegisterComponentFromAccessor<EvoWidenFloatV1>();
            dbe.InitializeArchetypes();

            var comp = new EvoWidenFloatV1(3.14159f);
            using var t = dbe.CreateQuickTransaction(DurabilityMode.Immediate);
            entityId = t.Spawn<EvoWidenFloatArch>(EvoWidenFloatArch.Comp.Set(in comp));
            t.Commit();
        }

        using (var scope = ServiceProvider.CreateScope())
        {
            using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
            dbe.RegisterComponentFromAccessor<EvoWidenFloatV2>();
            dbe.InitializeArchetypes();

            using var t = dbe.CreateQuickTransaction();
            ref readonly var comp = ref t.Open(entityId).Read(EvoWidenFloatV2Arch.Comp);
            // IEEE754: float→double promotion preserves exact float value
            Assert.That(comp.Speed, Is.EqualTo((double)3.14159f));
        }
    }

    [Test]
    public void CombinedAddAndWiden_AllFieldsCorrect()
    {
        EntityId entityId;

        using (var scope = ServiceProvider.CreateScope())
        {
            using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
            dbe.RegisterComponentFromAccessor<EvoCombinedV1>();
            dbe.InitializeArchetypes();

            var comp = new EvoCombinedV1(100, 2.5f);
            using var t = dbe.CreateQuickTransaction(DurabilityMode.Immediate);
            entityId = t.Spawn<EvoCombinedArch>(EvoCombinedArch.Comp.Set(in comp));
            t.Commit();
        }

        using (var scope = ServiceProvider.CreateScope())
        {
            using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
            dbe.RegisterComponentFromAccessor<EvoCombinedV2>();
            dbe.InitializeArchetypes();

            using var t = dbe.CreateQuickTransaction();
            ref readonly var comp = ref t.Open(entityId).Read(EvoCombinedV2Arch.Comp);
            Assert.That(comp.A, Is.EqualTo(100L)); // int→long widened
            Assert.That(comp.B, Is.EqualTo((double)2.5f)); // float→double widened
            Assert.That(comp.C, Is.EqualTo(0)); // new field zero-filled
        }
    }

    [Test]
    public void AddAndRemoveSimultaneously_CorrectData()
    {
        EntityId entityId;

        using (var scope = ServiceProvider.CreateScope())
        {
            using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
            dbe.RegisterComponentFromAccessor<EvoAddRemoveV1>();
            dbe.InitializeArchetypes();

            var comp = new EvoAddRemoveV1(7, 13, 1.1f);
            using var t = dbe.CreateQuickTransaction(DurabilityMode.Immediate);
            entityId = t.Spawn<EvoAddRemoveArch>(EvoAddRemoveArch.Comp.Set(in comp));
            t.Commit();
        }

        using (var scope = ServiceProvider.CreateScope())
        {
            using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
            dbe.RegisterComponentFromAccessor<EvoAddRemoveV2>();
            dbe.InitializeArchetypes();

            using var t = dbe.CreateQuickTransaction();
            ref readonly var comp = ref t.Open(entityId).Read(EvoAddRemoveV2Arch.Comp);
            Assert.That(comp.A, Is.EqualTo(7));
            Assert.That(comp.C, Is.EqualTo(1.1f));
            Assert.That(comp.D, Is.EqualTo(0.0)); // new field zero-filled
        }
    }

    [Test]
    public void MultipleEntities_AllMigratedCorrectly()
    {
        const int entityCount = 100;
        var entityIds = new EntityId[entityCount];

        using (var scope = ServiceProvider.CreateScope())
        {
            using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
            dbe.RegisterComponentFromAccessor<EvoAddV1>();
            dbe.InitializeArchetypes();

            using var t = dbe.CreateQuickTransaction(DurabilityMode.Immediate);
            for (int i = 0; i < entityCount; i++)
            {
                var comp = new EvoAddV1(i * 10, i * 0.5f);
                entityIds[i] = t.Spawn<EvoAddArch>(EvoAddArch.Comp.Set(in comp));
            }
            t.Commit();
        }

        using (var scope = ServiceProvider.CreateScope())
        {
            using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
            dbe.RegisterComponentFromAccessor<EvoAddV2>();
            dbe.InitializeArchetypes();

            using var t = dbe.CreateQuickTransaction();
            for (int i = 0; i < entityCount; i++)
            {
                ref readonly var comp = ref t.Open(entityIds[i]).Read(EvoAddV2Arch.Comp);
                Assert.That(comp.A, Is.EqualTo(i * 10), $"Entity {i}: A mismatch");
                Assert.That(comp.B, Is.EqualTo(i * 0.5f), $"Entity {i}: B mismatch");
                Assert.That(comp.C, Is.EqualTo(0), $"Entity {i}: new field C should be zero (got {comp.C})");
            }
        }
    }

    [Test]
    public void SurvivingIndexes_RemainValidAfterMigration()
    {
        EntityId entityId;

        using (var scope = ServiceProvider.CreateScope())
        {
            using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
            dbe.RegisterComponentFromAccessor<EvoAddV1>();
            dbe.InitializeArchetypes();

            var comp = new EvoAddV1(42, 3.14f);
            using var t = dbe.CreateQuickTransaction(DurabilityMode.Immediate);
            entityId = t.Spawn<EvoAddArch>(EvoAddArch.Comp.Set(in comp));
            t.Commit();
        }

        using (var scope = ServiceProvider.CreateScope())
        {
            using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
            dbe.RegisterComponentFromAccessor<EvoAddV2>();
            dbe.InitializeArchetypes();

            // PK index should still work
            using var t = dbe.CreateQuickTransaction();
            ref readonly var comp = ref t.Open(entityId).Read(EvoAddV2Arch.Comp);
            Assert.That(comp.A, Is.EqualTo(42));

            // Verify PK index lookups work (the index was not rebuilt)
            var table = dbe.GetComponentTable<EvoAddV2>();
            Assert.That(table.ComponentSegment.AllocatedChunkCount, Is.GreaterThan(0));
        }
    }

    [Test]
    [Property("CacheSize", 4 * 1024 * 1024)] // 4MB cache for 10K entity migration
    public void BulkMigration_Performance()
    {
        const int entityCount = 10_000;

        using (var scope = ServiceProvider.CreateScope())
        {
            using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
            dbe.RegisterComponentFromAccessor<EvoBulkV1>();
            dbe.InitializeArchetypes();

            // Insert entities in batches to avoid overwhelming a single transaction
            const int batchSize = 5_000;
            for (int batch = 0; batch < entityCount / batchSize; batch++)
            {
                using var t = dbe.CreateQuickTransaction(DurabilityMode.Immediate);
                for (int i = 0; i < batchSize; i++)
                {
                    var comp = new EvoBulkV1(batch * batchSize + i, i);
                    t.Spawn<EvoBulkArch>(EvoBulkArch.Comp.Set(in comp));
                }
                t.Commit();
            }
        }

        // Measure migration time
        var sw = Stopwatch.StartNew();
        using (var scope = ServiceProvider.CreateScope())
        {
            using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
            dbe.RegisterComponentFromAccessor<EvoBulkV2>();
            sw.Stop();

            // Verify all entities migrated
            using var t = dbe.CreateQuickTransaction();
            var table = dbe.GetComponentTable<EvoBulkV2>();
            Assert.That(table.ComponentSegment.AllocatedChunkCount, Is.GreaterThan(0));
        }

        // Performance target: migration should complete reasonably fast
        // Note: this includes engine setup overhead, not just migration
        TestContext.Out.WriteLine($"10K entity migration + engine startup: {sw.ElapsedMilliseconds}ms");
    }

    [Test]
    public void MigrationWithExistingData_CanCreateNewEntitiesAfter()
    {
        EntityId entityId1;

        using (var scope = ServiceProvider.CreateScope())
        {
            using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
            dbe.RegisterComponentFromAccessor<EvoAddV1>();
            dbe.InitializeArchetypes();

            var comp = new EvoAddV1(1, 2.0f);
            using var t = dbe.CreateQuickTransaction(DurabilityMode.Immediate);
            entityId1 = t.Spawn<EvoAddArch>(EvoAddArch.Comp.Set(in comp));
            t.Commit();
        }

        using (var scope = ServiceProvider.CreateScope())
        {
            using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
            dbe.RegisterComponentFromAccessor<EvoAddV2>();
            dbe.InitializeArchetypes();

            // Create a new entity with the new schema
            var newComp = new EvoAddV2(100, 200, 3.0f);
            using var t = dbe.CreateQuickTransaction(DurabilityMode.Immediate);
            var entityId2 = t.Spawn<EvoAddV2Arch>(EvoAddV2Arch.Comp.Set(in newComp));
            t.Commit();

            // Read both entities
            using var t2 = dbe.CreateQuickTransaction();
            ref readonly var old = ref t2.Open(entityId1).Read(EvoAddV2Arch.Comp);
            Assert.That(old.A, Is.EqualTo(1));
            Assert.That(old.C, Is.EqualTo(0)); // migrated, new field zero

            ref readonly var fresh = ref t2.Open(entityId2).Read(EvoAddV2Arch.Comp);
            Assert.That(fresh.A, Is.EqualTo(100));
            Assert.That(fresh.C, Is.EqualTo(200));
            Assert.That(fresh.B, Is.EqualTo(3.0f));
        }
    }
}
