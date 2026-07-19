using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;

namespace Typhon.Engine.Tests;

// Covers the non-generic / runtime-decode surface used by tooling that knows components only by name / layout at runtime
// (e.g. the Workbench Data Browser): Transaction.EnumerateArchetypeEntities + EntityRef.ReadRaw / ComponentCount /
// GetComponentName. Reuses the EcsUnit (100) / EcsSoldier (101) archetypes defined in EntitySpawnTests.cs.
[NonParallelizable]
class NonGenericEntityAccessTests : TestBase<NonGenericEntityAccessTests>
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
        dbe.InitializeArchetypes();
        return dbe;
    }

    // EnumerateArchetypeEntities takes the per-DB routing id (the value in EntityId.ArchetypeId), not the catalog id.
    private static ushort UnitRoutingId(DatabaseEngine dbe) => dbe.RoutingIdOf(Archetype<EcsUnit>.Metadata);
    private static ushort SoldierRoutingId(DatabaseEngine dbe) => dbe.RoutingIdOf(Archetype<EcsSoldier>.Metadata);

    // The registered component name — the same string GetComponentName(slot) returns, and the join key the Workbench uses.
    private static string NameOf<T>(DatabaseEngine dbe) where T : unmanaged => dbe.GetComponentTable<T>().Definition.Name;

    // Resolve a component slot by its registered name.
    private static int SlotByName(in EntityRef e, string componentName)
    {
        for (int s = 0; s < e.ComponentCount; s++)
        {
            if (e.GetComponentName(s) == componentName)
            {
                return s;
            }
        }
        return -1;
    }

    // ───────────────────────────────────────────────────────────────────────
    // EnumerateArchetypeEntities
    // ───────────────────────────────────────────────────────────────────────

    [Test]
    public void EnumerateArchetypeEntities_ReturnsAllCommittedEntities()
    {
        using var dbe = SetupEngine();

        var spawned = new HashSet<EntityId>();
        using (var t = dbe.CreateQuickTransaction())
        {
            for (int i = 0; i < 25; i++)
            {
                spawned.Add(t.Spawn<EcsUnit>(EcsUnit.Position.Set(new EcsPosition(i, 0, 0))));
            }
            t.Commit();
        }

        using var read = dbe.CreateReadOnlyTransaction();
        var ids = read.EnumerateArchetypeEntities(UnitRoutingId(dbe));

        Assert.That(ids, Has.Count.EqualTo(25));
        Assert.That(new HashSet<EntityId>(ids), Is.EquivalentTo(spawned));
    }

    [Test]
    public void EnumerateArchetypeEntities_ExactArchetypeOnly_NoSubtree()
    {
        using var dbe = SetupEngine();

        EntityId unitId, soldierId;
        using (var t = dbe.CreateQuickTransaction())
        {
            unitId = t.Spawn<EcsUnit>(EcsUnit.Position.Set(new EcsPosition(1, 0, 0)));
            soldierId = t.Spawn<EcsSoldier>(
                EcsUnit.Position.Set(new EcsPosition(2, 0, 0)),
                EcsSoldier.Health.Set(new EcsHealth(50, 50)));
            t.Commit();
        }

        using var read = dbe.CreateReadOnlyTransaction();

        var units = read.EnumerateArchetypeEntities(UnitRoutingId(dbe));
        var soldiers = read.EnumerateArchetypeEntities(SoldierRoutingId(dbe));

        // Exact archetype: EcsUnit enumeration must NOT include the EcsSoldier (a descendant), and vice versa.
        Assert.That(units, Does.Contain(unitId));
        Assert.That(units, Does.Not.Contain(soldierId));
        Assert.That(soldiers, Does.Contain(soldierId));
        Assert.That(soldiers, Does.Not.Contain(unitId));
    }

    [Test]
    public void EnumerateArchetypeEntities_ExcludesDestroyed()
    {
        using var dbe = SetupEngine();

        EntityId[] ids = new EntityId[10];
        using (var t = dbe.CreateQuickTransaction())
        {
            t.SpawnBatch<EcsUnit>(ids, EcsUnit.Position.Set(new EcsPosition(1, 2, 3)));
            t.Commit();
        }
        using (var t = dbe.CreateQuickTransaction())
        {
            t.DestroyBatch(new ReadOnlySpan<EntityId>(ids, 0, 4));
            t.Commit();
        }

        using var read = dbe.CreateReadOnlyTransaction();
        var live = read.EnumerateArchetypeEntities(UnitRoutingId(dbe));

        Assert.That(live, Has.Count.EqualTo(6));
        for (int i = 0; i < 4; i++)
        {
            Assert.That(live, Does.Not.Contain(ids[i]));
        }
        for (int i = 4; i < 10; i++)
        {
            Assert.That(live, Does.Contain(ids[i]));
        }
    }

    [Test]
    public void EnumerateArchetypeEntities_UnknownArchetype_ReturnsEmpty()
    {
        using var dbe = SetupEngine();
        using var read = dbe.CreateReadOnlyTransaction();
        Assert.That(read.EnumerateArchetypeEntities(60000), Is.Empty);
    }

    // ───────────────────────────────────────────────────────────────────────
    // EntityRef.ReadRaw / ComponentCount / GetComponentName
    // ───────────────────────────────────────────────────────────────────────

    [Test]
    public void ReadRaw_DecodesComponentBytes_MatchesTypedRead()
    {
        using var dbe = SetupEngine();

        EntityId id;
        using (var t = dbe.CreateQuickTransaction())
        {
            id = t.Spawn<EcsUnit>(
                EcsUnit.Position.Set(new EcsPosition(10, 20, 30)),
                EcsUnit.Velocity.Set(new EcsVelocity(1, 2, 3)));
            t.Commit();
        }

        using var read = dbe.CreateReadOnlyTransaction();
        var e = read.Open(id);

        int posSlot = SlotByName(e, NameOf<EcsPosition>(dbe));
        Assert.That(posSlot, Is.GreaterThanOrEqualTo(0));

        var raw = e.ReadRaw(posSlot);
        Assert.That(raw.Length, Is.GreaterThanOrEqualTo(Unsafe.SizeOf<EcsPosition>()));

        // Raw bytes must reinterpret to exactly what the typed read returns — storage-mode-agnostic correctness.
        var decoded = MemoryMarshal.Read<EcsPosition>(raw);
        ref readonly var typed = ref e.Read(EcsUnit.Position);
        Assert.That(decoded.X, Is.EqualTo(typed.X));
        Assert.That(decoded.Y, Is.EqualTo(typed.Y));
        Assert.That(decoded.Z, Is.EqualTo(typed.Z));
        Assert.That(decoded.X, Is.EqualTo(10f));
        Assert.That(decoded.Y, Is.EqualTo(20f));
        Assert.That(decoded.Z, Is.EqualTo(30f));
    }

    [Test]
    public void ReadRaw_AllSlots_MultiComponentArchetype()
    {
        using var dbe = SetupEngine();

        EntityId id;
        using (var t = dbe.CreateQuickTransaction())
        {
            id = t.Spawn<EcsSoldier>(
                EcsUnit.Position.Set(new EcsPosition(5, 6, 7)),
                EcsUnit.Velocity.Set(new EcsVelocity(8, 9, 10)),
                EcsSoldier.Health.Set(new EcsHealth(75, 100)));
            t.Commit();
        }

        using var read = dbe.CreateReadOnlyTransaction();
        var e = read.Open(id);
        Assert.That(e.ComponentCount, Is.EqualTo(3));

        var pos = MemoryMarshal.Read<EcsPosition>(e.ReadRaw(SlotByName(e, NameOf<EcsPosition>(dbe))));
        var vel = MemoryMarshal.Read<EcsVelocity>(e.ReadRaw(SlotByName(e, NameOf<EcsVelocity>(dbe))));
        var hp = MemoryMarshal.Read<EcsHealth>(e.ReadRaw(SlotByName(e, NameOf<EcsHealth>(dbe))));

        Assert.That(pos.X, Is.EqualTo(5f));
        Assert.That(vel.Dz, Is.EqualTo(10f));
        Assert.That(hp.Current, Is.EqualTo(75));
        Assert.That(hp.Max, Is.EqualTo(100));
    }

    [Test]
    public void GetComponentName_ReturnsRegisteredNames()
    {
        using var dbe = SetupEngine();

        EntityId id;
        using (var t = dbe.CreateQuickTransaction())
        {
            id = t.Spawn<EcsUnit>(EcsUnit.Position.Set(new EcsPosition(0, 0, 0)));
            t.Commit();
        }

        using var read = dbe.CreateReadOnlyTransaction();
        var e = read.Open(id);

        var names = new HashSet<string>();
        for (int s = 0; s < e.ComponentCount; s++)
        {
            names.Add(e.GetComponentName(s));
        }
        Assert.That(names, Does.Contain(NameOf<EcsPosition>(dbe)));
        Assert.That(names, Does.Contain(NameOf<EcsVelocity>(dbe)));
    }

    [Test]
    public void ReadRaw_DisabledComponent_StillReadable_IsEnabledReportsFalse()
    {
        using var dbe = SetupEngine();

        EntityId id;
        using (var t = dbe.CreateQuickTransaction())
        {
            // Only Position provided → Velocity is disabled but its storage still exists.
            id = t.Spawn<EcsUnit>(EcsUnit.Position.Set(new EcsPosition(1, 1, 1)));
            t.Commit();
        }

        using var read = dbe.CreateReadOnlyTransaction();
        var e = read.Open(id);

        int velSlot = SlotByName(e, NameOf<EcsVelocity>(dbe));
        Assert.That(e.IsEnabled((byte)velSlot), Is.False);
        // ReadRaw works regardless of enabled state — the Data Browser renders disabled components greyed, with values.
        Assert.That(e.ReadRaw(velSlot).Length, Is.GreaterThanOrEqualTo(Unsafe.SizeOf<EcsVelocity>()));
    }

    [Test]
    public void ReadRaw_OutOfRangeSlot_Throws()
    {
        using var dbe = SetupEngine();

        EntityId id;
        using (var t = dbe.CreateQuickTransaction())
        {
            id = t.Spawn<EcsUnit>(EcsUnit.Position.Set(new EcsPosition(0, 0, 0)));
            t.Commit();
        }

        using var read = dbe.CreateReadOnlyTransaction();
        // EntityRef is a ref struct — it can't be captured by a lambda, so open it inside the delegate.
        Assert.Throws<ArgumentOutOfRangeException>(() =>
        {
            var e = read.Open(id);
            e.ReadRaw(99);
        });
    }

    [Test]
    public void PureRuntimeDecode_EnumerateThenOffsetDecode_NoCompileTimeComponentType()
    {
        // The headline scenario: a tool that has only the archetype id (ushort) and the field layout (offsets) — no compile-time
        // component type — enumerates entities and decodes field values directly from the raw bytes by offset.
        using var dbe = SetupEngine();

        using (var t = dbe.CreateQuickTransaction())
        {
            for (int i = 0; i < 5; i++)
            {
                t.Spawn<EcsUnit>(EcsUnit.Position.Set(new EcsPosition(i * 10, i * 10 + 1, i * 10 + 2)));
            }
            t.Commit();
        }

        using var read = dbe.CreateReadOnlyTransaction();
        string positionName = NameOf<EcsPosition>(dbe);

        var seenX = new HashSet<float>();
        foreach (var id in read.EnumerateArchetypeEntities(UnitRoutingId(dbe)))
        {
            var e = read.Open(id);
            int posSlot = SlotByName(e, positionName);
            var raw = e.ReadRaw(posSlot);

            // Offset-based decode (FieldDto.Offset path): X@0, Y@4, Z@8 — three little-endian floats.
            float x = BitConverter.ToSingle(raw.Slice(0, 4));
            float y = BitConverter.ToSingle(raw.Slice(4, 4));
            float z = BitConverter.ToSingle(raw.Slice(8, 4));
            Assert.That(y, Is.EqualTo(x + 1));
            Assert.That(z, Is.EqualTo(x + 2));
            seenX.Add(x);
        }

        Assert.That(seenX, Is.EquivalentTo(new[] { 0f, 10f, 20f, 30f, 40f }));
    }

    [Test]
    public void EnumerateAndReadRaw_ReadOnlyTransaction_WorkbenchPath()
    {
        // End-to-end mirror of the Data Browser server flow: read-only transaction, enumerate by archetype id, open + raw-read.
        using var dbe = SetupEngine();

        using (var t = dbe.CreateQuickTransaction())
        {
            t.Spawn<EcsSoldier>(
                EcsUnit.Position.Set(new EcsPosition(100, 200, 300)),
                EcsSoldier.Health.Set(new EcsHealth(1, 2)));
            t.Commit();
        }

        using var read = dbe.CreateReadOnlyTransaction();
        var ids = read.EnumerateArchetypeEntities(SoldierRoutingId(dbe));
        Assert.That(ids, Has.Count.EqualTo(1));

        var e = read.Open(ids[0]);
        var hp = MemoryMarshal.Read<EcsHealth>(e.ReadRaw(SlotByName(e, NameOf<EcsHealth>(dbe))));
        Assert.That(hp.Current, Is.EqualTo(1));
        Assert.That(hp.Max, Is.EqualTo(2));
    }
}
