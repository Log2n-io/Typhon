using System;
using System.Numerics;
using System.Runtime.InteropServices;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Typhon.Engine.Internals;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests.Realms;

[Component("Typhon.Test.Realm.SepPos", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct SepPos
{
    [Field]
    [SpatialIndex]
    public AABB2F Bounds;
}

[Component("Typhon.Test.Realm.SepRealm", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct SepRealm
{
    [Field]
    [RealmKey]
    public ushort Realm;
}

[Component("Typhon.Test.Realm.SepRealm2", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct SepRealm2
{
    [Field]
    [RealmKey]
    public ushort Realm;
}

[Component("Typhon.Test.Realm.SepTag", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct SepTag
{
    [Field]
    public int Tag;
}

[Archetype]
partial class SepUnit : Archetype<SepUnit>
{
    public static readonly Comp<SepPos> Pos = Register<SepPos>();
    public static readonly Comp<SepRealm> Realm = Register<SepRealm>();
}

[Archetype]
partial class KeyedNoSpatialUnit : Archetype<KeyedNoSpatialUnit>
{
    public static readonly Comp<SepTag> Tag = Register<SepTag>();
    public static readonly Comp<SepRealm2> Realm = Register<SepRealm2>();
}

/// <summary>
/// Realms G1 (deviation): the <c>[RealmKey]</c> in a component of its own. The spatial component stays 16 bytes — the AABB2F SIMD narrowphase's
/// stride, worth ~20 % of an SWG tick — and every realm path reads the key from its own column: spawn, queries, the fence, Teleport, the rebuild.
/// </summary>
[TestFixture]
[NonParallelizable]
class RealmKeyComponentTests : TestBase<RealmKeyComponentTests>
{
    private static readonly AABB2F Everywhere = new() { MinX = 0, MinY = 0, MaxX = 100, MaxY = 100 };

    private static SpatialGridConfig Grid(double cellSize) => SpatialGridConfig.Flat(new Vector2(0, 0), new Vector2(100, 100), cellSize);

    private static SepPos At(float x, float y) => new() { Bounds = new AABB2F { MinX = x, MinY = y, MaxX = x, MaxY = y } };

    private static void Configure(DatabaseEngine dbe)
    {
        dbe.RegisterComponentFromAccessor<SepPos>();
        dbe.RegisterComponentFromAccessor<SepRealm>();
        dbe.ConfigureRealms(3);
        dbe.ConfigureSpatialGrid(Grid(10));
        dbe.Realms.Register(new RealmId(2), RealmConfig.SimulatedAlways(Grid(25)));
        dbe.InitializeArchetypes();
    }

    private static ArchetypeClusterState StateOf(DatabaseEngine dbe) => dbe._archetypeStates[Archetype<SepUnit>.Metadata.ArchetypeId].ClusterState;

    private static int Count(DatabaseEngine dbe, ushort realm)
    {
        using var epoch = EpochGuard.Enter(dbe.EpochManager);
        return dbe.ClusterSpatialQuery<SepUnit>(new RealmId(realm)).AABB(Everywhere).Count();
    }

    [Test]
    public void TheSpatialComponentKeepsTheSimdNarrowphase()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        Configure(dbe);
        var cs = StateOf(dbe);
        Assert.That(cs.SpatialSlot.HasRealmKey, Is.True);
        Assert.That(cs.SpatialSlot.RealmKeyInSpatialComponent, Is.False);
        if (NarrowphaseAabb2F.Best != NarrowphaseAabb2F.Kernel.None)
        {
            Assert.That(new ClusterFieldLayout(cs).Aabb2FBlocks, Is.GreaterThan(0), "a key of its own leaves the 16-byte stride the kernel needs");
        }
    }

    [Test]
    public void SpawnQueryFenceAndTeleport_ReadTheKeysOwnColumn()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        Configure(dbe);
        EntityId a, b, c;
        using (var tx = dbe.CreateQuickTransaction())
        {
            a = tx.Spawn<SepUnit>(SepUnit.Pos.Set(At(50, 50)), SepUnit.Realm.Set(new SepRealm { Realm = 2 }));
            b = tx.Spawn<SepUnit>(SepUnit.Pos.Set(At(50, 50)), SepUnit.Realm.Set(new SepRealm { Realm = 0 }));
            c = tx.Spawn<SepUnit>(SepUnit.Pos.Set(At(50, 50)));   // no key value: realm 0, validated at the call
            Assert.Throws<InvalidOperationException>(() => tx.Spawn<SepUnit>(SepUnit.Pos.Set(At(1, 1)), SepUnit.Realm.Set(new SepRealm { Realm = 1 })));
            tx.Commit();
        }

        dbe.WriteTickFence(1);
        Assert.That((Count(dbe, 0), Count(dbe, 2)), Is.EqualTo((2, 1)));

        // A raw write of the key component (non-barrier archetype): the dirty scan finds it and the fence moves the entity; RM-04 hides it meanwhile.
        using (var tx = dbe.CreateQuickTransaction())
        {
            tx.OpenMut(b).Write(SepUnit.Realm).Realm = 2;
            tx.Commit();
        }

        Assert.That(Count(dbe, 0), Is.EqualTo(1), "the moved entity no longer answers realm 0");
        dbe.WriteTickFence(2);
        Assert.That((Count(dbe, 0), Count(dbe, 2)), Is.EqualTo((1, 2)));

        // Teleport writes the key's own component.
        using (var tx = dbe.CreateQuickTransaction())
        {
            tx.Teleport(a, SepUnit.Pos, RealmId.Default, At(10, 10));
            tx.Commit();
        }

        dbe.WriteTickFence(3);
        Assert.That((Count(dbe, 0), Count(dbe, 2)), Is.EqualTo((2, 1)));
        using var rtx = dbe.CreateQuickTransaction();
        Assert.That(rtx.Open(a).Read(SepUnit.Realm).Realm, Is.EqualTo(0));
        // c was spawned without a key value: its key component is disabled (not supplied), and its zeroed column places it in realm 0 — counted above.
        Assert.That(rtx.Open(c).IsEnabled(SepUnit.Realm), Is.False);
    }

    [Test]
    public void TeleportMovesABarrierOnlyArchetype_AndTheReopenFilesByTheKeyColumn()
    {
        EntityId a;
        using (var scope = ServiceProvider.CreateScope())
        {
            using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
            Configure(dbe);
            dbe.SetSpatialBarrierOnly<SepUnit>();
            using (var tx = dbe.CreateQuickTransaction(DurabilityMode.Immediate))
            {
                a = tx.Spawn<SepUnit>(SepUnit.Pos.Set(At(30, 30)), SepUnit.Realm.Set(new SepRealm { Realm = 0 }));
                tx.Spawn<SepUnit>(SepUnit.Pos.Set(At(30, 30)), SepUnit.Realm.Set(new SepRealm { Realm = 2 }));
                tx.Commit();
            }

            dbe.WriteTickFence(1);
            using (var tx = dbe.CreateQuickTransaction(DurabilityMode.Immediate))
            {
                tx.Teleport(a, SepUnit.Pos, new RealmId(2), At(80, 80));
                tx.Commit();
            }

            dbe.WriteTickFence(2);
            Assert.That((Count(dbe, 0), Count(dbe, 2)), Is.EqualTo((0, 2)));
        }

        using var reopen = ServiceProvider.CreateScope();
        using var engine = reopen.ServiceProvider.GetRequiredService<DatabaseEngine>();
        Configure(engine);
        Assert.That((Count(engine, 0), Count(engine, 2)), Is.EqualTo((0, 2)), "the rebuild reads each cluster's realm from the key column");
    }

    [Test]
    public void AKeyOnANonSpatialArchetype_IsRefusedAtOpen()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<SepTag>();
        dbe.RegisterComponentFromAccessor<SepRealm2>();
        var ex = Assert.Throws<InvalidOperationException>(() => dbe.InitializeArchetypes());
        Assert.That(ex.Message, Does.Contain("no [SpatialIndex]"));
    }
}
