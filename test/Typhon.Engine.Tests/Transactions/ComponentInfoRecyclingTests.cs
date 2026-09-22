using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using System;
using System.Runtime.InteropServices;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests;

[Component("Typhon.Test.Recycle.Ver", 1, StorageMode = StorageMode.Versioned)]
[StructLayout(LayoutKind.Sequential)]
public struct RecycleVer
{
    public int Value;
    public int Pad;
}

[Component("Typhon.Test.Recycle.Sv", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
public struct RecycleSv
{
    public int Value;
    public int Pad;
}

[Component("Typhon.Test.Recycle.Sv2", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
public struct RecycleSv2
{
    public int Value;
    public int Pad;
}

[Archetype]
class RecycleArch : Archetype<RecycleArch>
{
    public static readonly Comp<RecycleVer> Ver = Register<RecycleVer>();
    public static readonly Comp<RecycleSv> Sv = Register<RecycleSv>();
    public static readonly Comp<RecycleSv2> Sv2 = Register<RecycleSv2>();
}

/// <summary>
/// A pooled transaction keeps its per-component entries across leases and rebinds them, and an archetype accessor creates an entry only for a component that
/// is actually touched.
/// </summary>
/// <remarks>
/// Before this, <c>EntityAccessor.ResetCore</c> dropped every entry and <c>ArchetypeAccessor</c> warmed one for every slot of the archetype, so a system that
/// called <c>For&lt;T&gt;()</c> on the tick's transaction allocated an entry and a dictionary per component of the archetype, every tick — measured as the
/// largest steady-state allocation of the SWG server.
/// </remarks>
[TestFixture]
class ComponentInfoRecyclingTests : TestBase<ComponentInfoRecyclingTests>
{
    protected override void RegisterComponents(DatabaseEngine dbe)
    {
        base.RegisterComponents(dbe);
        dbe.RegisterComponentFromAccessor<RecycleVer>();
        dbe.RegisterComponentFromAccessor<RecycleSv>();
        dbe.RegisterComponentFromAccessor<RecycleSv2>();
    }

    private DatabaseEngine SetupEngine()
    {
        var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        dbe.InitializeArchetypes();
        return dbe;
    }

    private static EntityId SpawnOne(DatabaseEngine dbe, int value)
    {
        EntityId id;
        using (var tx = dbe.CreateQuickTransaction())
        {
            id = tx.Spawn<RecycleArch>(RecycleArch.Ver.Set(new RecycleVer { Value = value }), RecycleArch.Sv.Set(new RecycleSv { Value = value }),
                RecycleArch.Sv2.Set(new RecycleSv2 { Value = value }));
            tx.Commit();
        }

        dbe.WriteTickFence(1);
        return id;
    }

    /// <summary>What a system does with the tick's transaction: take the archetype, open an entity, read two of its three components.</summary>
    private static int ReadTwo(Transaction tx, EntityId id)
    {
        var accessor = tx.For<RecycleArch>();
        var entity = accessor.Open(id);
        return entity.Read(RecycleArch.Ver).Value + entity.Read(RecycleArch.Sv).Value;
    }

    [Test]
    public void APooledTransactionAllocatesNoComponentEntryForComponentsItTouchedBefore()
    {
        using var dbe = SetupEngine();
        var id = SpawnOne(dbe, 7);

        // Warm: JIT, and every pooled instance's own entries — the pool hands out its instances in rotation (a FIFO of 16), so each needs one lease
        // before any lease is a reuse.
        for (var i = 0; i < 40; i++)
        {
            using var tx = dbe.CreateQuickTransaction();
            Assert.That(ReadTwo(tx, id), Is.EqualTo(14));
        }

        // Three windows, judged on the smallest, so a tier-up landing in one window on a loaded suite does not fail a path that allocates nothing.
        const int Windows = 3;
        const int PerWindow = 32;
        var allocated = new long[Windows];
        for (var w = 0; w < Windows; w++)
        {
            for (var i = 0; i < PerWindow; i++)
            {
                using var tx = dbe.CreateQuickTransaction();
                var before = GC.GetAllocatedBytesForCurrentThread();
                ReadTwo(tx, id);
                allocated[w] += GC.GetAllocatedBytesForCurrentThread() - before;
            }
        }

        var least = Math.Min(allocated[0], Math.Min(allocated[1], allocated[2]));
        Assert.That(least, Is.Zero,
            $"taking the archetype and reading two components on a reused transaction allocated (windows: {string.Join(" / ", allocated)} bytes over "
            + $"{PerWindow} transactions each): the pooled transaction rebuilt its component entries instead of rebinding them");
    }

    [Test]
    public void ARecycledEntryCarriesNothingFromTheTransactionBefore()
    {
        using var dbe = SetupEngine();
        var id = SpawnOne(dbe, 1);

        // Committed: 10.
        using (var tx = dbe.CreateQuickTransaction())
        {
            tx.For<RecycleArch>().OpenMut(id).Write(RecycleArch.Ver).Value = 10;
            tx.Commit();
        }

        // Written and rolled back: 99 lives only in that transaction's revision cache.
        using (var tx = dbe.CreateQuickTransaction())
        {
            tx.For<RecycleArch>().OpenMut(id).Write(RecycleArch.Ver).Value = 99;
            tx.Rollback();
        }

        // Every later lease: the pool rotates its instances, so reading through all of them reaches the one whose entry cached 99.
        for (var i = 0; i < 20; i++)
        {
            using var tx = dbe.CreateQuickTransaction();
            var value = tx.For<RecycleArch>().Open(id).Read(RecycleArch.Ver).Value;
            Assert.That(value, Is.EqualTo(10), "the read saw the rolled-back transaction's value: its revision cache survived the recycling");
        }
    }

    [Test]
    public void TakingAnArchetypeCreatesAnEntryOnlyForTheComponentsRead()
    {
        using var dbe = SetupEngine();
        var id = SpawnOne(dbe, 3);

        using var tx = dbe.CreateQuickTransaction();
        var accessor = tx.For<RecycleArch>();
        Assert.That(tx.ComponentInfoCount, Is.Zero, "taking the archetype warmed entries for components nobody has touched");

        // Opening resolves the Versioned slot's revision chain, which needs its entry; reading a SingleVersion slot goes straight to the cluster and needs
        // none. What must not happen is an entry for the slot nobody read (Sv2) — the eager warm-up created all three.
        var entity = accessor.Open(id);
        _ = entity.Read(RecycleArch.Sv).Value;
        Assert.That(tx.ComponentInfoCount, Is.LessThan(RecycleArch.Metadata.ComponentCount), "an entry exists for a component nobody read");
    }
}
