using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using System.Runtime.InteropServices;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests;

// ── Test component structs for schema evolution testing ──
// Each V1/V2 pair shares the same [Component] name but has different field layouts.

[Component("Typhon.Schema.UnitTest.SchemaEvol", 1)]
[StructLayout(LayoutKind.Sequential)]
struct CompEvolV1
{
    public int A;
    public float B;

    public CompEvolV1(int a, float b) { A = a; B = b; }
}

[Component("Typhon.Schema.UnitTest.SchemaEvol", 1)]
[StructLayout(LayoutKind.Sequential)]
struct CompEvolV2AddField
{
    public int A;
    public int C;    // new field (inserted between A and B in declaration order)
    public float B;

    public CompEvolV2AddField(int a, int c, float b) { A = a; C = c; B = b; }
}

[Component("Typhon.Schema.UnitTest.SchemaRemove", 1)]
[StructLayout(LayoutKind.Sequential)]
struct CompRemoveV1
{
    public int A;
    public float B;
    public double C;

    public CompRemoveV1(int a, float b, double c) { A = a; B = b; C = c; }
}

[Component("Typhon.Schema.UnitTest.SchemaRemove", 1)]
[StructLayout(LayoutKind.Sequential)]
struct CompRemoveV2
{
    public int A;
    public double C;

    public CompRemoveV2(int a, double c) { A = a; C = c; }
}

[Component("Typhon.Schema.UnitTest.SchemaRename", 1)]
[StructLayout(LayoutKind.Sequential)]
struct CompRenameV1
{
    public int Hitpoints;
    public float Speed;

    public CompRenameV1(int hp, float spd) { Hitpoints = hp; Speed = spd; }
}

[Component("Typhon.Schema.UnitTest.SchemaRename", 1)]
[StructLayout(LayoutKind.Sequential)]
struct CompRenameV2
{
    [Field(PreviousName = "Hitpoints")]
    public int Health;
    public float Speed;

    public CompRenameV2(int hp, float spd) { Health = hp; Speed = spd; }
}

[Component("Typhon.Schema.UnitTest.SchemaIndex", 1)]
[StructLayout(LayoutKind.Sequential)]
struct CompIndexV1
{
    [Index]
    public int Score;
    public float Speed;

    public CompIndexV1(int score, float speed) { Score = score; Speed = speed; }
}

[Component("Typhon.Schema.UnitTest.SchemaIndex", 1)]
[StructLayout(LayoutKind.Sequential)]
struct CompIndexV2
{
    [Index]
    public int Score;
    public int Bonus;  // new unrelated field
    public float Speed;

    public CompIndexV2(int score, int bonus, float speed) { Score = score; Bonus = bonus; Speed = speed; }
}

// ── Archetypes for V1 components (used for Spawn in first scope) ──

[Archetype(430)]
class CompEvolArch : Archetype<CompEvolArch>
{
    public static readonly Comp<CompEvolV1> Comp = Register<CompEvolV1>();
}

[Archetype(331)]
class CompRemoveArch : Archetype<CompRemoveArch>
{
    public static readonly Comp<CompRemoveV1> Comp = Register<CompRemoveV1>();
}

[Archetype(332)]
class CompRenameArch : Archetype<CompRenameArch>
{
    public static readonly Comp<CompRenameV1> Comp = Register<CompRenameV1>();
}

[Archetype(333)]
class CompIndexArch : Archetype<CompIndexArch>
{
    public static readonly Comp<CompIndexV1> Comp = Register<CompIndexV1>();
}

/// <summary>
/// Integration tests verifying FieldId stability across database reopen with schema evolution.
/// Each test creates a database, writes data, closes, reopens with a different schema, and verifies.
/// </summary>
[NonParallelizable]
class FieldIdStabilityTests : TestBase<FieldIdStabilityTests>
{
    [OneTimeSetUp]
    public void OneTimeSetup()
    {
    }

    [Test]
    public void FieldR1_RoundTrip_SameSession()
    {
        // Verify that FieldR1 entries can be read back within the same session
        using var scope = ServiceProvider.CreateScope();
        using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<CompEvolV1>();
        dbe.InitializeArchetypes();

        // Read back the ComponentR1 entries from the system schema
        using var epochGuard = EpochGuard.Enter(dbe.EpochManager);
        var componentsTable = dbe.GetComponentTable<ComponentR1>();
        var segment = componentsTable.ComponentSegment;
        var capacity = segment.ChunkCapacity;
        for (int chunkId = 1; chunkId < capacity; chunkId++)
        {
            if (!segment.IsChunkAllocated(chunkId))
            {
                continue;
            }

            if (!SystemCrud.Read(componentsTable, chunkId, out ComponentR1 comp, dbe.EpochManager))
            {
                continue;
            }

            TestContext.Out.WriteLine($"ChunkId={chunkId}: Name={comp.Name.AsString}, Fields._bufferId={comp.Fields._bufferId}");

            if (comp.Fields._bufferId != 0)
            {
                var vsbs = dbe.GetComponentCollectionVSBS<FieldR1>();
                foreach (var f in vsbs.EnumerateBuffer(comp.Fields._bufferId))
                {
                    TestContext.Out.WriteLine($"  Field: {f.Name.AsString} = FieldId {f.FieldId}");
                }
            }
        }
    }

    [Test]
    public void StableAcrossReopen_NoChanges()
    {
        EntityId entityId;
        int originalFieldIdA, originalFieldIdB;

        // Phase 1: Create database, register component, write data
        using (var scope1 = ServiceProvider.CreateScope())
        {
            using var dbe = scope1.ServiceProvider.GetRequiredService<DatabaseEngine>();
            dbe.RegisterComponentFromAccessor<CompEvolV1>();
            dbe.InitializeArchetypes();

            var def = dbe.DBD.GetComponent("Typhon.Schema.UnitTest.SchemaEvol", 1);
            originalFieldIdA = def.GetFieldId("A");
            originalFieldIdB = def.GetFieldId("B");

            var comp = new CompEvolV1(42, 3.14f);
            using var t = dbe.CreateQuickTransaction(DurabilityMode.Immediate);
            entityId = t.Spawn<CompEvolArch>(CompEvolArch.Comp.Set(in comp));
            t.Commit();
        }

        // Phase 2: Reopen with SAME schema
        using (var scope2 = ServiceProvider.CreateScope())
        {
            using var dbe = scope2.ServiceProvider.GetRequiredService<DatabaseEngine>();
            dbe.RegisterComponentFromAccessor<CompEvolV1>();

            var def = dbe.DBD.GetComponent("Typhon.Schema.UnitTest.SchemaEvol", 1);
            Assert.That(def.GetFieldId("A"), Is.EqualTo(originalFieldIdA), "FieldId for A should be stable across reopen");
            Assert.That(def.GetFieldId("B"), Is.EqualTo(originalFieldIdB), "FieldId for B should be stable across reopen");

            dbe.InitializeArchetypes();

            // Verify data is intact
            using var t = dbe.CreateQuickTransaction();
            ref readonly var comp = ref t.Open(entityId).Read(CompEvolArch.Comp);
            Assert.That(comp.A, Is.EqualTo(42));
            Assert.That(comp.B, Is.EqualTo(3.14f));
        }
    }

    [Test]
    public void StableAcrossFieldAddition()
    {
        int originalFieldIdA, originalFieldIdB;

        // Phase 1: Create database with V1 (fields A, B)
        using (var scope1 = ServiceProvider.CreateScope())
        {
            using var dbe = scope1.ServiceProvider.GetRequiredService<DatabaseEngine>();
            dbe.RegisterComponentFromAccessor<CompEvolV1>();
            dbe.InitializeArchetypes();

            var def = dbe.DBD.GetComponent("Typhon.Schema.UnitTest.SchemaEvol", 1);
            originalFieldIdA = def.GetFieldId("A");
            originalFieldIdB = def.GetFieldId("B");

            Assert.That(originalFieldIdA, Is.EqualTo(0), "V1: A should be FieldId 0");
            Assert.That(originalFieldIdB, Is.EqualTo(1), "V1: B should be FieldId 1");

            var comp = new CompEvolV1(100, 2.5f);
            using var t = dbe.CreateQuickTransaction(DurabilityMode.Immediate);
            t.Spawn<CompEvolArch>(CompEvolArch.Comp.Set(in comp));
            t.Commit();
        }

        // Phase 2: Reopen with V2 (fields A, C, B) — C is new
        using (var scope2 = ServiceProvider.CreateScope())
        {
            using var dbe = scope2.ServiceProvider.GetRequiredService<DatabaseEngine>();
            dbe.RegisterComponentFromAccessor<CompEvolV2AddField>();

            var def = dbe.DBD.GetComponent("Typhon.Schema.UnitTest.SchemaEvol", 1);

            // A and B keep their original FieldIds; C gets max+1 = 2
            Assert.That(def.GetFieldId("A"), Is.EqualTo(originalFieldIdA), "A should keep FieldId 0");
            Assert.That(def.GetFieldId("B"), Is.EqualTo(originalFieldIdB), "B should keep FieldId 1");
            Assert.That(def.GetFieldId("C"), Is.EqualTo(2), "C (new field) should get FieldId 2");
        }
    }

    [Test]
    public void StableAcrossFieldRemoval()
    {
        int originalFieldIdA, originalFieldIdC;

        // Phase 1: Create database with V1 (fields A, B, C)
        using (var scope1 = ServiceProvider.CreateScope())
        {
            using var dbe = scope1.ServiceProvider.GetRequiredService<DatabaseEngine>();
            dbe.RegisterComponentFromAccessor<CompRemoveV1>();
            dbe.InitializeArchetypes();

            var def = dbe.DBD.GetComponent("Typhon.Schema.UnitTest.SchemaRemove", 1);
            originalFieldIdA = def.GetFieldId("A");
            originalFieldIdC = def.GetFieldId("C");

            var comp = new CompRemoveV1(10, 1.5f, 2.5);
            using var t = dbe.CreateQuickTransaction(DurabilityMode.Immediate);
            t.Spawn<CompRemoveArch>(CompRemoveArch.Comp.Set(in comp));
            t.Commit();
        }

        // Phase 2: Reopen with V2 (fields A, C) — B is removed
        using (var scope2 = ServiceProvider.CreateScope())
        {
            using var dbe = scope2.ServiceProvider.GetRequiredService<DatabaseEngine>();
            dbe.RegisterComponentFromAccessor<CompRemoveV2>();

            var def = dbe.DBD.GetComponent("Typhon.Schema.UnitTest.SchemaRemove", 1);

            // A and C keep their original FieldIds
            Assert.That(def.GetFieldId("A"), Is.EqualTo(originalFieldIdA), "A should keep its FieldId");
            Assert.That(def.GetFieldId("C"), Is.EqualTo(originalFieldIdC), "C should keep its FieldId");

            // B's slot is null in _fieldsById (FieldId 1 is gone)
            Assert.That(def.GetFieldId("B"), Is.EqualTo(-1), "B should not exist in the definition");
        }
    }

    [Test]
    public void StableAcrossFieldRename()
    {
        int originalHitpointsId;

        // Phase 1: Create database with V1 (fields Hitpoints, Speed)
        using (var scope1 = ServiceProvider.CreateScope())
        {
            using var dbe = scope1.ServiceProvider.GetRequiredService<DatabaseEngine>();
            dbe.RegisterComponentFromAccessor<CompRenameV1>();
            dbe.InitializeArchetypes();

            var def = dbe.DBD.GetComponent("Typhon.Schema.UnitTest.SchemaRename", 1);
            originalHitpointsId = def.GetFieldId("Hitpoints");

            var comp = new CompRenameV1(100, 5.0f);
            using var t = dbe.CreateQuickTransaction(DurabilityMode.Immediate);
            t.Spawn<CompRenameArch>(CompRenameArch.Comp.Set(in comp));
            t.Commit();
        }

        // Phase 2: Reopen with V2 (fields Health[PreviousName=Hitpoints], Speed)
        using (var scope2 = ServiceProvider.CreateScope())
        {
            using var dbe = scope2.ServiceProvider.GetRequiredService<DatabaseEngine>();
            dbe.RegisterComponentFromAccessor<CompRenameV2>();

            var def = dbe.DBD.GetComponent("Typhon.Schema.UnitTest.SchemaRename", 1);

            // Health should have the same FieldId as Hitpoints did
            Assert.That(def.GetFieldId("Health"), Is.EqualTo(originalHitpointsId),
                "Renamed field Health should reuse Hitpoints' FieldId");
            Assert.That(def.GetFieldId("Hitpoints"), Is.EqualTo(-1),
                "Old name Hitpoints should no longer exist");
        }
    }

    [Test]
    public void IndexSurvivesFieldAddition()
    {
        EntityId entityId;

        // Phase 1: Create database with V1, write an indexed entity
        using (var scope1 = ServiceProvider.CreateScope())
        {
            using var dbe = scope1.ServiceProvider.GetRequiredService<DatabaseEngine>();
            dbe.RegisterComponentFromAccessor<CompIndexV1>();
            dbe.InitializeArchetypes();

            var comp = new CompIndexV1(999, 1.0f);
            using var t = dbe.CreateQuickTransaction(DurabilityMode.Immediate);
            entityId = t.Spawn<CompIndexArch>(CompIndexArch.Comp.Set(in comp));
            t.Commit();

            // Verify index works in V1
            using var t2 = dbe.CreateQuickTransaction();
            var table = dbe.GetComponentTable<CompIndexV1>();
            var fieldDef = table.Definition.FieldsByName["Score"];
            Assert.That(fieldDef.HasIndex, Is.True, "Score should be indexed");
        }

        // Phase 2: Reopen with V2 (Score, Bonus, Speed) — Bonus is new
        using (var scope2 = ServiceProvider.CreateScope())
        {
            using var dbe = scope2.ServiceProvider.GetRequiredService<DatabaseEngine>();
            dbe.RegisterComponentFromAccessor<CompIndexV2>();

            var def = dbe.DBD.GetComponent("Typhon.Schema.UnitTest.SchemaIndex", 1);

            // Score keeps FieldId 0, Speed keeps FieldId 1, Bonus gets 2
            Assert.That(def.GetFieldId("Score"), Is.EqualTo(0), "Score should keep FieldId 0");
            Assert.That(def.GetFieldId("Speed"), Is.EqualTo(1), "Speed should keep FieldId 1");
            Assert.That(def.GetFieldId("Bonus"), Is.EqualTo(2), "Bonus should get FieldId 2");

            // Verify index structure is intact
            var table = dbe.GetComponentTable<CompIndexV2>();
            var scoreDef = table.Definition.FieldsByName["Score"];
            Assert.That(scoreDef.HasIndex, Is.True, "Score should still be indexed after field addition");
            Assert.That(scoreDef.FieldId, Is.EqualTo(0), "Score's FieldId should be stable");
        }
    }
}
