using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NUnit.Framework;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests;

// ── Test component structs for operational tooling ──

[Component("Typhon.Schema.UnitTest.OpsComp", 1)]
[StructLayout(LayoutKind.Sequential)]
struct OpsCompV1
{
    public int Health;
    public float Speed;

    public OpsCompV1(int health, float speed) { Health = health; Speed = speed; }
}

[Component("Typhon.Schema.UnitTest.OpsComp", 1)]
[StructLayout(LayoutKind.Sequential)]
struct OpsCompV2
{
    public int Health;
    public float Speed;
    public int Armor;

    public OpsCompV2(int health, float speed, int armor) { Health = health; Speed = speed; Armor = armor; }
}

// ── Archetype for V1 component (used for Spawn in first scope) ──

[Archetype(340)]
class OpsCompArch : Archetype<OpsCompArch>
{
    public static readonly Comp<OpsCompV1> Comp = Register<OpsCompV1>();
}

[NonParallelizable]
class OperationalToolingTests : TestBase<OperationalToolingTests>
{
    [OneTimeSetUp]
    public void OneTimeSetup()
    {
        Archetype<OpsCompArch>.Touch();
    }

    private string GetDatabasePath()
    {
        // DatabaseSchema.Inspect/ValidateEvolution take the bundle path (they strip it to name+dir), not the inner data file.
        var options = ServiceProvider.GetRequiredService<IOptions<ManagedPagedMMFOptions>>().Value;
        return options.BundleDirectory;
    }

    // ── Inspect() Tests ──

    [Test]
    public void Inspect_ReturnsComponentsAndFields()
    {
        // Create database with V1
        using (var scope = ServiceProvider.CreateScope())
        {
            using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
            dbe.RegisterComponentFromAccessor<OpsCompV1>();
            dbe.InitializeArchetypes();

            var comp = new OpsCompV1(100, 5.5f);
            using var t = dbe.CreateQuickTransaction(DurabilityMode.Immediate);
            t.Spawn<OpsCompArch>(OpsCompArch.Comp.Set(in comp));
            t.Commit();
        }

        // Inspect the database offline
        var dbPath = GetDatabasePath();
        var report = DatabaseSchema.Inspect(dbPath);

        Assert.That(report, Is.Not.Null);
        Assert.That(report.DatabaseName, Is.Not.Null.And.Not.Empty);
        Assert.That(report.SystemSchemaRevision, Is.EqualTo(1));

        // Find our user component (skip system components)
        var opsComp = report.Components.FirstOrDefault(c => c.Name == "Typhon.Schema.UnitTest.OpsComp");
        Assert.That(opsComp, Is.Not.Null);
        Assert.That(opsComp.EntityCount, Is.EqualTo(1));
        Assert.That(opsComp.Fields.Count, Is.EqualTo(2));

        var healthField = opsComp.Fields.FirstOrDefault(f => f.Name == "Health");
        Assert.That(healthField, Is.Not.Null);
        Assert.That(healthField.Type, Is.EqualTo(FieldType.Int));

        var speedField = opsComp.Fields.FirstOrDefault(f => f.Name == "Speed");
        Assert.That(speedField, Is.Not.Null);
        Assert.That(speedField.Type, Is.EqualTo(FieldType.Float));
    }

    // ── SchemaHistoryR1 Recording Tests ──

    [Test]
    public void SchemaHistory_RecordedOnFieldAdd()
    {
        // Create database with V1
        using (var scope = ServiceProvider.CreateScope())
        {
            using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
            dbe.RegisterComponentFromAccessor<OpsCompV1>();
            dbe.InitializeArchetypes();

            var comp = new OpsCompV1(50, 2.0f);
            using var t = dbe.CreateQuickTransaction(DurabilityMode.Immediate);
            t.Spawn<OpsCompArch>(OpsCompArch.Comp.Set(in comp));
            t.Commit();
        }

        // Reopen with V2 (adds Armor field)
        using (var scope = ServiceProvider.CreateScope())
        {
            using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
            dbe.RegisterComponentFromAccessor<OpsCompV2>();

            var history = dbe.GetSchemaHistory();
            Assert.That(history.Count, Is.GreaterThanOrEqualTo(1));

            var entry = history.FirstOrDefault(h => h.ComponentName.AsString == "Typhon.Schema.UnitTest.OpsComp");
            Assert.That(entry.ComponentName.AsString, Is.EqualTo("Typhon.Schema.UnitTest.OpsComp"));
            Assert.That(entry.FieldsAdded, Is.GreaterThanOrEqualTo(1));
            Assert.That(entry.Kind, Is.EqualTo(SchemaChangeKind.Compatible));
        }
    }

    // ── ValidateEvolution() Tests ──

    [Test]
    public void ValidateEvolution_CompatibleChange_IsValid()
    {
        // Create database with V1
        using (var scope = ServiceProvider.CreateScope())
        {
            using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
            dbe.RegisterComponentFromAccessor<OpsCompV1>();
            dbe.InitializeArchetypes();

            var comp = new OpsCompV1(10, 1.0f);
            using var t = dbe.CreateQuickTransaction(DurabilityMode.Immediate);
            t.Spawn<OpsCompArch>(OpsCompArch.Comp.Set(in comp));
            t.Commit();
        }

        // Validate evolution to V2
        var dbPath = GetDatabasePath();
        var result = DatabaseSchema.ValidateEvolution(dbPath, registrar =>
        {
            registrar.RegisterComponent<OpsCompV2>();
        });

        Assert.That(result.IsValid, Is.True);
        Assert.That(result.Errors, Is.Empty);
        Assert.That(result.Components.Count, Is.EqualTo(1));

        var comp2 = result.Components[0];
        Assert.That(comp2.ComponentName, Is.EqualTo("Typhon.Schema.UnitTest.OpsComp"));
        Assert.That(comp2.NeedsMigration, Is.True);
        Assert.That(comp2.HasMigrationPath, Is.True);
    }

    // ── MigrationProgress Event Tests ──

    [Test]
    public void MigrationProgress_EventsFiredInOrder()
    {
        // Create database with V1
        using (var scope = ServiceProvider.CreateScope())
        {
            using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
            dbe.RegisterComponentFromAccessor<OpsCompV1>();
            dbe.InitializeArchetypes();

            var comp = new OpsCompV1(42, 3.0f);
            using var t = dbe.CreateQuickTransaction(DurabilityMode.Immediate);
            t.Spawn<OpsCompArch>(OpsCompArch.Comp.Set(in comp));
            t.Commit();
        }

        // Reopen with V2, subscribe to progress events
        var phases = new List<MigrationPhase>();
        using (var scope = ServiceProvider.CreateScope())
        {
            using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
            dbe.OnMigrationProgress += (_, args) => phases.Add(args.Phase);
            dbe.RegisterComponentFromAccessor<OpsCompV2>();
        }

        // Verify events were fired in order
        Assert.That(phases.Count, Is.GreaterThanOrEqualTo(2));
        Assert.That(phases[0], Is.EqualTo(MigrationPhase.Analyzing));
        Assert.That(phases[^1], Is.EqualTo(MigrationPhase.Complete));

        // Verify monotonic phase progression
        for (int i = 1; i < phases.Count; i++)
        {
            Assert.That(phases[i], Is.GreaterThanOrEqualTo(phases[i - 1]));
        }
    }

    // ── Sparse PK / EnumerateLeaves Tests ──

    [Test]
    public void SchemaHistory_SparseKeys_ReturnsAll()
    {
        // Phase 1: Create database with V1, create many entities to advance the global PK counter
        using (var scope = ServiceProvider.CreateScope())
        {
            using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
            dbe.RegisterComponentFromAccessor<OpsCompV1>();
            dbe.InitializeArchetypes();

            // Create multiple entities so the global PK counter is well above 1
            using var t = dbe.CreateQuickTransaction(DurabilityMode.Immediate);
            for (int i = 0; i < 20; i++)
            {
                var comp = new OpsCompV1(i, i * 0.5f);
                t.Spawn<OpsCompArch>(OpsCompArch.Comp.Set(in comp));
            }
            t.Commit();
        }

        // Phase 2: Reopen with V2 — this triggers a schema history entry at a high PK value
        using (var scope = ServiceProvider.CreateScope())
        {
            using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
            dbe.RegisterComponentFromAccessor<OpsCompV2>();

            var history = dbe.GetSchemaHistory();

            // The schema history should contain at least the V1→V2 migration entry
            Assert.That(history.Count, Is.GreaterThanOrEqualTo(1));

            var entry = history.FirstOrDefault(h => h.ComponentName.AsString == "Typhon.Schema.UnitTest.OpsComp");
            Assert.That(entry.ComponentName.AsString, Is.EqualTo("Typhon.Schema.UnitTest.OpsComp"));
            Assert.That(entry.FieldsAdded, Is.GreaterThanOrEqualTo(1));
        }
    }
}
