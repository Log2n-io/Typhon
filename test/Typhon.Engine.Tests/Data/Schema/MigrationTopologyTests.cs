using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Typhon.Engine.Internals;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests;

// ═════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════
// SCHEMA-12 (design 07 §6, #1394): a migrating open re-places every entity in OLD (chunk, slot) order — densely for a non-spatial archetype, and for a
// spatial one closing a cluster only where the home key (realm, AABB-centre cell) changes — from a pre-migration cluster that is ALWAYS loaded, at the
// geometry the persisted catalog records, and validated before the first claim. One rule for both paths: a field migration, and a component-set
// migration (the "set" rows at the end of the fixture).
//
// Every scenario owns its components: an archetype class is process-wide, so a component shared with another fixture would make that fixture's opens
// consume (and migrate) this one's archetypes too. Each V2 archetype class carries a name of its own and exists only to hand out typed V2 handles.
// ═════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════

#region Non-spatial, SingleVersion + Versioned: the SV component widens

[Component("Typhon.Schema.UnitTest.MtopoNsSv", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct MtopoNsSvV1
{
    public int A;
    public int Pad;
    public MtopoNsSvV1(int a) { A = a; Pad = 0; }
}

[Component("Typhon.Schema.UnitTest.MtopoNsSv", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct MtopoNsSvV2
{
    public int A;
    public int Pad;
    public long B;
}

[Component("Typhon.Schema.UnitTest.MtopoNsVer", 1)]
[StructLayout(LayoutKind.Sequential)]
struct MtopoNsVer
{
    public long V;
    public MtopoNsVer(long v) => V = v;
}

[Archetype]
partial class MtopoNsArch : Archetype<MtopoNsArch>
{
    public static readonly Comp<MtopoNsSvV1> Sv = Register<MtopoNsSvV1>();
    public static readonly Comp<MtopoNsVer> Ver = Register<MtopoNsVer>();
}

[Archetype]
partial class MtopoNsV2Arch : Archetype<MtopoNsV2Arch>
{
    public static readonly Comp<MtopoNsSvV2> Sv = Register<MtopoNsSvV2>();
    public static readonly Comp<MtopoNsVer> Ver = Register<MtopoNsVer>();
}

#endregion

#region Pure Versioned: the second component widens

[Component("Typhon.Schema.UnitTest.MtopoPvKey", 1)]
[StructLayout(LayoutKind.Sequential)]
struct MtopoPvKey
{
    public long K;
    public MtopoPvKey(long k) => K = k;
}

[Component("Typhon.Schema.UnitTest.MtopoPvVal", 1)]
[StructLayout(LayoutKind.Sequential)]
struct MtopoPvValV1
{
    public int V;
    public int Pad;
    public MtopoPvValV1(int v) { V = v; Pad = 0; }
}

[Component("Typhon.Schema.UnitTest.MtopoPvVal", 1)]
[StructLayout(LayoutKind.Sequential)]
struct MtopoPvValV2
{
    public int V;
    public int Pad;
    public long W;
}

[Archetype]
partial class MtopoPvArch : Archetype<MtopoPvArch>
{
    public static readonly Comp<MtopoPvKey> Key = Register<MtopoPvKey>();
    public static readonly Comp<MtopoPvValV1> Val = Register<MtopoPvValV1>();
}

[Archetype]
partial class MtopoPvV2Arch : Archetype<MtopoPvV2Arch>
{
    public static readonly Comp<MtopoPvKey> Key = Register<MtopoPvKey>();
    public static readonly Comp<MtopoPvValV2> Val = Register<MtopoPvValV2>();
}

#endregion

#region Spatial (realm 0): a non-spatial SV component widens

[Component("Typhon.Schema.UnitTest.MtopoSpPos", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct MtopoSpPos
{
    [Field]
    [SpatialIndex]
    public AABB2F Bounds;

    [Field]
    public int Tag;

    [Field]
    public int Pad;
}

[Component("Typhon.Schema.UnitTest.MtopoSpStat", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct MtopoSpStatV1
{
    public int Hp;
    public int Pad;
    public MtopoSpStatV1(int hp) { Hp = hp; Pad = 0; }
}

[Component("Typhon.Schema.UnitTest.MtopoSpStat", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct MtopoSpStatV2
{
    public int Hp;
    public int Pad;
    public long Xp;
}

[Archetype]
partial class MtopoSpArch : Archetype<MtopoSpArch>
{
    public static readonly Comp<MtopoSpPos> Pos = Register<MtopoSpPos>();
    public static readonly Comp<MtopoSpStatV1> Stat = Register<MtopoSpStatV1>();
}

[Archetype]
partial class MtopoSpV2Arch : Archetype<MtopoSpV2Arch>
{
    public static readonly Comp<MtopoSpPos> Pos = Register<MtopoSpPos>();
    public static readonly Comp<MtopoSpStatV2> Stat = Register<MtopoSpStatV2>();
}

#endregion

#region Set migration (#1394 S3): a component added — non-spatial, spatial, and sized so the cluster shrinks

[Component("Typhon.Schema.UnitTest.MtopoSaKeep", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct MtopoSaKeep
{
    public int A;
    public int Pad;
    public MtopoSaKeep(int a) { A = a; Pad = 0; }
}

[Component("Typhon.Schema.UnitTest.MtopoSaVer", 1)]
[StructLayout(LayoutKind.Sequential)]
struct MtopoSaVer
{
    public long V;
    public MtopoSaVer(long v) => V = v;
}

[Component("Typhon.Schema.UnitTest.MtopoSaAdded", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct MtopoSaAdded
{
    public long X;
}

[Archetype(1, Name = "Mtopo.SetAdd")]
class MtopoSaV1Arch : Archetype<MtopoSaV1Arch>
{
    public static readonly Comp<MtopoSaKeep> Keep = Register<MtopoSaKeep>();
    public static readonly Comp<MtopoSaVer> Ver = Register<MtopoSaVer>();
}

[Archetype(2, Name = "Mtopo.SetAdd")]
class MtopoSaV2Arch : Archetype<MtopoSaV2Arch>
{
    public static readonly Comp<MtopoSaKeep> Keep = Register<MtopoSaKeep>();
    public static readonly Comp<MtopoSaVer> Ver = Register<MtopoSaVer>();
    public static readonly Comp<MtopoSaAdded> Added = Register<MtopoSaAdded>();
}

[Component("Typhon.Schema.UnitTest.MtopoSsPos", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct MtopoSsPos
{
    [Field]
    [SpatialIndex]
    public AABB2F Bounds;

    [Field]
    public int Tag;

    [Field]
    public int Pad;
}

[Component("Typhon.Schema.UnitTest.MtopoSsStat", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct MtopoSsStat
{
    public int Hp;
    public int Pad;
    public MtopoSsStat(int hp) { Hp = hp; Pad = 0; }
}

[Component("Typhon.Schema.UnitTest.MtopoSsAdded", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct MtopoSsAdded
{
    public long Gear;
}

[Archetype(1, Name = "Mtopo.SpatialSetAdd")]
class MtopoSsV1Arch : Archetype<MtopoSsV1Arch>
{
    public static readonly Comp<MtopoSsPos> Pos = Register<MtopoSsPos>();
    public static readonly Comp<MtopoSsStat> Stat = Register<MtopoSsStat>();
}

[Archetype(2, Name = "Mtopo.SpatialSetAdd")]
class MtopoSsV2Arch : Archetype<MtopoSsV2Arch>
{
    public static readonly Comp<MtopoSsPos> Pos = Register<MtopoSsPos>();
    public static readonly Comp<MtopoSsStat> Stat = Register<MtopoSsStat>();
    public static readonly Comp<MtopoSsAdded> Added = Register<MtopoSsAdded>();
}

[Component("Typhon.Schema.UnitTest.MtopoShKeep", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct MtopoShKeep
{
    public int A;
    public int Pad;
    public long K0, K1, K2, K3, K4, K5, K6, K7, K8, K9, K10, K11;
    public MtopoShKeep(int a) : this() => A = a;
}

/// <summary>104 bytes, like <see cref="MtopoShKeep"/>: added, it roughly halves the cluster size (N' &lt; N). Each stays within the 112 bytes a spawn
/// carries inline.</summary>
[Component("Typhon.Schema.UnitTest.MtopoShBig", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct MtopoShBig
{
    public long B0, B1, B2, B3, B4, B5, B6, B7, B8, B9, B10, B11, B12;
}

[Archetype(1, Name = "Mtopo.Shrink")]
class MtopoShV1Arch : Archetype<MtopoShV1Arch>
{
    public static readonly Comp<MtopoShKeep> Keep = Register<MtopoShKeep>();
}

[Archetype(2, Name = "Mtopo.Shrink")]
class MtopoShV2Arch : Archetype<MtopoShV2Arch>
{
    public static readonly Comp<MtopoShKeep> Keep = Register<MtopoShKeep>();
    public static readonly Comp<MtopoShBig> Big = Register<MtopoShBig>();
}

#endregion

/// <summary>Reads where every entity of an archetype sits in its cluster segment — the placement the topology tests compare across a migration.</summary>
internal static class ClusterPlacementProbe
{
    /// <summary>Every occupied slot of every active cluster: entity id (raw) → (cluster chunk id, slot).</summary>
    public static unsafe Dictionary<long, (int Chunk, int Slot)> Locations(DatabaseEngine dbe, ushort archetypeId)
    {
        var cs = dbe._archetypeStates[archetypeId].ClusterState;
        var result = new Dictionary<long, (int, int)>();
        using var epoch = EpochGuard.Enter(dbe.EpochManager);
        var accessor = cs.ClusterSegment.CreateChunkAccessor();
        try
        {
            for (var i = 0; i < cs.ActiveClusterCount; i++)
            {
                var chunkId = cs.ActiveClusterIds[i];
                var clusterBase = accessor.GetChunkAddress(chunkId);
                for (var bits = *(ulong*)clusterBase; bits != 0; bits &= bits - 1)
                {
                    var slot = BitOperations.TrailingZeroCount(bits);
                    result[*(long*)(clusterBase + cs.Layout.EntityIdsOffset + slot * 8)] = (chunkId, slot);
                }
            }
        }
        finally
        {
            accessor.Dispose();
        }

        return result;
    }

    /// <summary>The entities of <paramref name="locations"/> sorted by (chunk, slot): an archetype's storage order.</summary>
    public static List<long> InStorageOrder(Dictionary<long, (int Chunk, int Slot)> locations)
    {
        var order = new List<long>(locations.Keys);
        order.Sort((a, b) =>
        {
            var (ca, sa) = locations[a];
            var (cb, sb) = locations[b];
            return ca != cb ? ca.CompareTo(cb) : sa.CompareTo(sb);
        });
        return order;
    }
}

[NonParallelizable]
class MigrationTopologyTests : TestBase<MigrationTopologyTests>
{
    private const float CellSize = 100f;
    private const float WorldMax = 1000f;

    private static ushort NsId => Archetype<MtopoNsArch>.Metadata.ArchetypeId;
    private static ushort PvId => Archetype<MtopoPvArch>.Metadata.ArchetypeId;
    private static ushort SpId => Archetype<MtopoSpArch>.Metadata.ArchetypeId;

    private static ArchetypeClusterState StateOf(DatabaseEngine dbe, ushort archetypeId) => dbe._archetypeStates[archetypeId].ClusterState;

    // ── non-spatial ──

    /// <summary>Spawns <paramref name="count"/> entities, then destroys every third so the old clusters have holes. Returns the survivors.</summary>
    private List<EntityId> SeedNonSpatial(int count)
    {
        var ids = new List<EntityId>();
        using var scope = ServiceProvider.CreateScope();
        using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<MtopoNsSvV1>();
        dbe.RegisterComponentFromAccessor<MtopoNsVer>();
        dbe.InitializeArchetypes();

        var all = new EntityId[count];
        using (var t = dbe.CreateQuickTransaction(DurabilityMode.Immediate))
        {
            for (var i = 0; i < count; i++)
            {
                all[i] = t.Spawn<MtopoNsArch>(MtopoNsArch.Sv.Set(new MtopoNsSvV1(i)), MtopoNsArch.Ver.Set(new MtopoNsVer(i * 1000L + 1)));
            }

            Assert.That(t.Commit(), Is.True);
        }

        using (var t = dbe.CreateQuickTransaction(DurabilityMode.Immediate))
        {
            for (var i = 0; i < count; i += 3)
            {
                t.Destroy(all[i]);
            }

            Assert.That(t.Commit(), Is.True);
        }

        for (var i = 0; i < count; i++)
        {
            if (i % 3 != 0)
            {
                ids.Add(all[i]);
            }
        }

        return ids;
    }

    private static Dictionary<long, (int Chunk, int Slot)> LocationsBeforeMigration(DatabaseEngine dbe, ushort archetypeId) =>
        ClusterPlacementProbe.Locations(dbe, archetypeId);

    /// <summary>
    /// Asserts that the archetype's storage order after the migration is its storage order before it, entity for entity, and that it is dense: every
    /// cluster full except the last, ⌈live / N'⌉ clusters.
    /// </summary>
    private static void AssertDenseInOldOrder(DatabaseEngine dbe, ushort archetypeId, List<long> oldOrder, string what)
    {
        var cs = StateOf(dbe, archetypeId);
        var after = ClusterPlacementProbe.Locations(dbe, archetypeId);
        var newOrder = ClusterPlacementProbe.InStorageOrder(after);
        var n = cs.Layout.ClusterSize;
        Assert.Multiple(() =>
        {
            Assert.That(newOrder, Is.EqualTo(oldOrder), $"{what}: the entities keep their old (chunk, slot) order");
            Assert.That(cs.ActiveClusterCount, Is.EqualTo((oldOrder.Count + n - 1) / n), $"{what}: ⌈live / N'⌉ clusters, N' = {n}");
            for (var i = 0; i < newOrder.Count; i++)
            {
                // Dense: the i-th entity in old order sits in the (i / N')-th cluster, at slot i % N'.
                Assert.That(after[newOrder[i]].Slot, Is.EqualTo(i % n), $"{what}: entity {i} in old order is at slot {i % n}");
            }
        });
    }

    [Test]
    [VerifiesRule("SCHEMA-12")]
    public void NonSpatial_FieldMigration_DenseInOldOrder()
    {
        var ids = SeedNonSpatial(300);

        List<long> oldOrder;
        using (var scope = ServiceProvider.CreateScope())
        {
            // A plain reopen to read the pre-migration placement: the old clusters, holes and all.
            using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
            dbe.RegisterComponentFromAccessor<MtopoNsSvV1>();
            dbe.RegisterComponentFromAccessor<MtopoNsVer>();
            dbe.InitializeArchetypes();
            var before = LocationsBeforeMigration(dbe, NsId);
            oldOrder = ClusterPlacementProbe.InStorageOrder(before);
            var n = StateOf(dbe, NsId).Layout.ClusterSize;
            Assert.That(before, Has.Count.EqualTo(ids.Count), "premise: every survivor is placed");
            Assert.That(StateOf(dbe, NsId).ActiveClusterCount, Is.GreaterThan((ids.Count + n - 1) / n),
                "premise: the destroys left holes, so the old clusters are not dense — a dense re-placement is observable");
        }

        using (var scope = ServiceProvider.CreateScope())
        {
            using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
            dbe.RegisterComponentFromAccessor<MtopoNsSvV2>();
            dbe.RegisterComponentFromAccessor<MtopoNsVer>();
            dbe.InitializeArchetypes();

            AssertDenseInOldOrder(dbe, NsId, oldOrder, "SV + Versioned field migration");
            Assert.That(dbe.LastOpenMigrationOrphanChainEntities, Is.Zero);

            using var t = dbe.CreateQuickTransaction();
            Assert.Multiple(() =>
            {
                foreach (var id in ids)
                {
                    var e = t.Open(id);
                    var a = e.Read(MtopoNsV2Arch.Sv).A;
                    Assert.That(e.Read(MtopoNsV2Arch.Ver).V, Is.EqualTo(a * 1000L + 1), $"entity {a}: its Versioned value moved with it");
                    Assert.That(e.Read(MtopoNsV2Arch.Sv).B, Is.Zero, $"entity {a}: premise — the added field zero-fills, so the open migrated");
                }
            });
        }
    }

    [Test]
    [VerifiesRule("SCHEMA-12")]
    public void CleanOpen_HasNoChainOnlyEntities()
    {
        var ids = SeedNonSpatial(220);
        using var scope = ServiceProvider.CreateScope();
        using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RecordRebuildPlacementForTest = true;
        dbe.RegisterComponentFromAccessor<MtopoNsSvV2>();
        dbe.RegisterComponentFromAccessor<MtopoNsVer>();
        dbe.InitializeArchetypes();

        Assert.That(dbe.LastOpenRebuildPlacementMap, Is.Not.Null.And.ContainKey(NsId), "premise: the archetype migrated");
        var placed = 0;
        foreach (var (_, sources) in dbe.LastOpenRebuildPlacementMap[NsId])
        {
            Assert.That(sources, Has.None.EqualTo(-1), "no cluster of chain-only entities on a clean open");
        }

        foreach (var (_, loc) in ClusterPlacementProbe.Locations(dbe, NsId))
        {
            placed += loc.Chunk > 0 ? 1 : 0;
        }

        Assert.Multiple(() =>
        {
            Assert.That(dbe.LastOpenMigrationOrphanChainEntities, Is.Zero, "every entity of a clean open has a slot in the cluster the last checkpoint wrote");
            Assert.That(placed, Is.EqualTo(ids.Count), "and every one was placed");
        });
    }

    // ── pure Versioned ──

    private List<EntityId> SeedPureVersioned(int count)
    {
        var ids = new List<EntityId>();
        using var scope = ServiceProvider.CreateScope();
        using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<MtopoPvKey>();
        dbe.RegisterComponentFromAccessor<MtopoPvValV1>();
        dbe.InitializeArchetypes();

        var all = new EntityId[count];
        using (var t = dbe.CreateQuickTransaction(DurabilityMode.Immediate))
        {
            for (var i = 0; i < count; i++)
            {
                all[i] = t.Spawn<MtopoPvArch>(MtopoPvArch.Key.Set(new MtopoPvKey(i)), MtopoPvArch.Val.Set(new MtopoPvValV1(i * 3)));
            }

            Assert.That(t.Commit(), Is.True);
        }

        using (var t = dbe.CreateQuickTransaction(DurabilityMode.Immediate))
        {
            for (var i = 3; i < count; i += 5)
            {
                t.Destroy(all[i]);
            }

            Assert.That(t.Commit(), Is.True);
        }

        for (var i = 0; i < count; i++)
        {
            if (i < 3 || (i - 3) % 5 != 0)
            {
                ids.Add(all[i]);
            }
        }

        return ids;
    }

    [Test]
    [VerifiesRule("SCHEMA-12")]
    public void FieldMigration_PureVersioned_SamePlacementRule()
    {
        var ids = SeedPureVersioned(260);

        List<long> oldOrder;
        using (var scope = ServiceProvider.CreateScope())
        {
            using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
            dbe.RegisterComponentFromAccessor<MtopoPvKey>();
            dbe.RegisterComponentFromAccessor<MtopoPvValV1>();
            dbe.InitializeArchetypes();
            oldOrder = ClusterPlacementProbe.InStorageOrder(LocationsBeforeMigration(dbe, PvId));
            Assert.That(oldOrder, Has.Count.EqualTo(ids.Count), "premise: every survivor is placed");
        }

        using (var scope = ServiceProvider.CreateScope())
        {
            using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
            dbe.RegisterComponentFromAccessor<MtopoPvKey>();
            dbe.RegisterComponentFromAccessor<MtopoPvValV2>();
            dbe.InitializeArchetypes();

            // Before #1394 a pure-Versioned archetype was re-placed in HashSet order from its chains: no old order existed to keep.
            AssertDenseInOldOrder(dbe, PvId, oldOrder, "pure-Versioned field migration");

            using var t = dbe.CreateQuickTransaction();
            Assert.Multiple(() =>
            {
                foreach (var id in ids)
                {
                    var e = t.Open(id);
                    var k = e.Read(MtopoPvV2Arch.Key).K;
                    Assert.That(e.Read(MtopoPvV2Arch.Val).V, Is.EqualTo(k * 3), $"entity {k}: its migrated value");
                }
            });
        }
    }

    [Test]
    public void OldClusterAlwaysLoaded_PureVersioned()
    {
        var ids = SeedPureVersioned(210);
        using var scope = ServiceProvider.CreateScope();
        using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RecordRebuildPlacementForTest = true;
        dbe.RegisterComponentFromAccessor<MtopoPvKey>();
        dbe.RegisterComponentFromAccessor<MtopoPvValV2>();
        dbe.InitializeArchetypes();

        // The placement map is built from the walk of the pre-migration cluster: an old chunk id in it is that cluster, loaded and read. Before #1394 an
        // archetype with no SingleVersion slot never loaded it — membership came from the chains alone.
        Assert.That(dbe.LastOpenRebuildPlacementMap, Is.Not.Null.And.ContainKey(PvId), "premise: the archetype migrated");
        var oldChunks = new HashSet<int>();
        foreach (var (_, sources) in dbe.LastOpenRebuildPlacementMap[PvId])
        {
            foreach (var oldChunk in sources)
            {
                oldChunks.Add(oldChunk);
            }
        }

        Assert.Multiple(() =>
        {
            Assert.That(oldChunks, Has.None.LessThan(1), "every entity came out of a pre-migration cluster");
            Assert.That(oldChunks.Count, Is.GreaterThan(1), "several old clusters were walked");
            Assert.That(ClusterPlacementProbe.Locations(dbe, PvId), Has.Count.EqualTo(ids.Count));
        });
    }

    [Test]
    public void GeometryMismatch_IsRefusedBeforeAnyClaim()
    {
        var ids = SeedNonSpatial(200);
        var name = Archetype<MtopoNsArch>.Metadata.Name;

        using (var scope = ServiceProvider.CreateScope())
        {
            using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
            dbe.RegisterComponentFromAccessor<MtopoNsSvV2>();
            dbe.RegisterComponentFromAccessor<MtopoNsVer>();
            // A stored size that is not the one the cluster was written at: the reconstructed stride is wrong, so every occupied slot's entity id is read at
            // the wrong place. Only this archetype: the V2 handle archetype holds the same components and must open.
            dbe.AdjustPersistedLayoutSizesForTest = (archetype, sizes) =>
            {
                if (archetype == name)
                {
                    sizes[0] += 128;
                }
            };

            var ex = Assert.Throws<ArchetypeSchemaException>(() => dbe.InitializeArchetypes());
            Assert.Multiple(() =>
            {
                Assert.That(ex!.Kind, Is.EqualTo(ArchetypeSchemaErrorKind.OldClusterUnreadable));
                Assert.That(ex.ArchetypeName, Is.EqualTo(name));
                Assert.That(ex.Message, Does.StartWith($"Archetype '{name}': its pre-migration cluster (SPI "));
                Assert.That(ex.Message, Does.Contain("the geometry rebuilt from the catalog does not match what the cluster was written at"));
            });
        }

        // The refusal came before any claim and before the commit: the file is the pre-migration image, and the correct build migrates it intact.
        using (var scope = ServiceProvider.CreateScope())
        {
            using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
            dbe.RegisterComponentFromAccessor<MtopoNsSvV2>();
            dbe.RegisterComponentFromAccessor<MtopoNsVer>();
            dbe.InitializeArchetypes();

            using var t = dbe.CreateQuickTransaction();
            Assert.Multiple(() =>
            {
                Assert.That(ClusterPlacementProbe.Locations(dbe, NsId), Has.Count.EqualTo(ids.Count), "every entity is there");
                foreach (var id in ids)
                {
                    var e = t.Open(id);
                    var a = e.Read(MtopoNsV2Arch.Sv).A;
                    Assert.That(e.Read(MtopoNsV2Arch.Ver).V, Is.EqualTo(a * 1000L + 1), $"entity {a}");
                }
            });
        }
    }

    // ── spatial ──

    private static void ConfigureSpatial(DatabaseEngine dbe, bool v2)
    {
        dbe.RecordRebuildPlacementForTest = true;
        dbe.RegisterComponentFromAccessor<MtopoSpPos>();
        if (v2)
        {
            dbe.RegisterComponentFromAccessor<MtopoSpStatV2>();
        }
        else
        {
            dbe.RegisterComponentFromAccessor<MtopoSpStatV1>();
        }

        // Repair off and no spawn sort: a re-pack would change the cluster shapes these tests read, and slot order is spawn order.
        dbe.ConfigureSpatialGrid(SpatialGridConfig.Flat(new Vector2(0, 0), new Vector2(WorldMax, WorldMax), CellSize, reclusterBudgetMs: 0f,
            batchSpawnSortThreshold: 0));
        dbe.InitializeArchetypes();
    }

    private static MtopoSpPos At(float x, float y, int tag) => new() { Bounds = new AABB2F { MinX = x, MinY = y, MaxX = x, MaxY = y }, Tag = tag };

    /// <summary>
    /// Seeds <paramref name="perCell"/> entities in each cell of <paramref name="cells"/>, spawned cell after cell (<paramref name="interleaved"/> false:
    /// a cell's old clusters are consecutive chunks) or round-robin across the cells (true: consecutive chunks are other cells), then destroys every
    /// fifth. Returns survivor → cell index.
    /// </summary>
    private Dictionary<EntityId, int> SeedSpatial((float X, float Y)[] cells, int perCell, bool interleaved)
    {
        var cellOf = new Dictionary<EntityId, int>();
        using var scope = ServiceProvider.CreateScope();
        using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
        ConfigureSpatial(dbe, false);

        var all = new List<(EntityId Id, int Cell)>();
        using (var t = dbe.CreateQuickTransaction(DurabilityMode.Immediate))
        {
            for (var k = 0; k < cells.Length * perCell; k++)
            {
                var c = interleaved ? k % cells.Length : k / perCell;
                var i = interleaved ? k / cells.Length : k % perCell;
                var (cx, cy) = cells[c];
                var id = t.Spawn<MtopoSpArch>(MtopoSpArch.Pos.Set(At(cx + 5 + i % 9 * 10, cy + 5 + i / 9 % 9 * 10, k)),
                    MtopoSpArch.Stat.Set(new MtopoSpStatV1(k)));
                all.Add((id, c));
            }

            Assert.That(t.Commit(), Is.True);
        }

        using (var t = dbe.CreateQuickTransaction(DurabilityMode.Immediate))
        {
            for (var k = 0; k < all.Count; k += 5)
            {
                t.Destroy(all[k].Id);
            }

            Assert.That(t.Commit(), Is.True);
        }

        for (var k = 0; k < all.Count; k++)
        {
            if (k % 5 != 0)
            {
                cellOf[all[k].Id] = all[k].Cell;
            }
        }

        dbe.WriteTickFence(1);
        return cellOf;
    }

    private static int OnlyCell(HashSet<int> cells)
    {
        foreach (var cell in cells)
        {
            return cell;
        }

        return -1;
    }

    /// <summary>Per old cluster, the cells of its entities, read on a plain reopen before the migration.</summary>
    private Dictionary<int, HashSet<int>> OldClusterCells(Dictionary<EntityId, int> cellOf)
    {
        using var scope = ServiceProvider.CreateScope();
        using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
        ConfigureSpatial(dbe, false);
        var homes = new Dictionary<int, HashSet<int>>();
        var locations = ClusterPlacementProbe.Locations(dbe, SpId);
        foreach (var (id, cell) in cellOf)
        {
            var chunk = locations[(long)id.RawValue].Chunk;
            if (!homes.TryGetValue(chunk, out var set))
            {
                homes[chunk] = set = [];
            }

            set.Add(cell);
        }

        return homes;
    }

    [Test]
    [VerifiesRule("SCHEMA-12")]
    public void Spatial_FieldMigration_NoClusterMixesHomeKeys()
    {
        (float, float)[] cells = [(100f, 100f), (300f, 100f), (100f, 500f), (700f, 700f)];
        var cellOf = SeedSpatial(cells, perCell: 150, interleaved: true);
        var oldHomes = OldClusterCells(cellOf);
        Assert.That(oldHomes.Values, Has.All.Count.EqualTo(1), "premise: an old cluster holds one cell (claimed by cell)");

        using var scope = ServiceProvider.CreateScope();
        using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
        ConfigureSpatial(dbe, true);
        var cs = StateOf(dbe, SpId);
        var placement = dbe.LastOpenRebuildPlacementMap[SpId];

        var newClusterCells = new Dictionary<int, HashSet<int>>();
        var locations = ClusterPlacementProbe.Locations(dbe, SpId);
        foreach (var (id, cell) in cellOf)
        {
            var chunk = locations[(long)id.RawValue].Chunk;
            if (!newClusterCells.TryGetValue(chunk, out var set))
            {
                newClusterCells[chunk] = set = [];
            }

            set.Add(cell);
        }

        Assert.Multiple(() =>
        {
            foreach (var (newChunk, sources) in placement)
            {
                var homes = new HashSet<int>();
                foreach (var oldChunk in sources)
                {
                    homes.UnionWith(oldHomes[oldChunk]);
                }

                Assert.That(homes, Has.Count.EqualTo(1), $"new cluster {newChunk} carved from old clusters [{string.Join(", ", sources)}] of one home");
            }

            Assert.That(newClusterCells.Values, Has.All.Count.EqualTo(1), "no new cluster holds entities of two cells");
            Assert.That(cs.LastRebuildForeignCellSlots, Is.Zero, "no stray filed for the first fence");
            Assert.That(locations, Has.Count.EqualTo(cellOf.Count), "every entity placed");
        });

        dbe.WriteTickFence(2);
        Assert.That(cs.LastTickMigrationCount, Is.Zero, "the first fence after the migration moves nothing");

        // Per-cell queries answer every entity of the cell.
        using var epoch = EpochGuard.Enter(dbe.EpochManager);
        for (var c = 0; c < cells.Length; c++)
        {
            var (cx, cy) = cells[c];
            var expected = new HashSet<EntityId>();
            foreach (var (id, cell) in cellOf)
            {
                if (cell == c)
                {
                    expected.Add(id);
                }
            }

            var found = new HashSet<EntityId>();
            foreach (var hit in cs.QueryAabb(dbe.Realm0Grid, cx, cy, double.NegativeInfinity, cx + CellSize, cy + CellSize, double.PositiveInfinity))
            {
                found.Add(hit.Entity);
            }

            Assert.That(found, Is.EquivalentTo(expected), $"cell {c}");
        }
    }

    [Test]
    public void Spatial_SameHomeRun_SharesClusters()
    {
        (float, float)[] cells = [(100f, 100f), (500f, 300f)];
        var cellOf = SeedSpatial(cells, perCell: 200, interleaved: false);
        var oldHomes = OldClusterCells(cellOf);
        var oldClustersPerCell = new int[cells.Length];
        foreach (var (_, set) in oldHomes)
        {
            foreach (var cell in set)
            {
                oldClustersPerCell[cell]++;
            }
        }

        Assert.That(oldClustersPerCell, Has.All.GreaterThan(2), "premise: a run of several old clusters per cell");

        using var scope = ServiceProvider.CreateScope();
        using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
        ConfigureSpatial(dbe, true);
        var cs = StateOf(dbe, SpId);
        var n = cs.Layout.ClusterSize;
        var placement = dbe.LastOpenRebuildPlacementMap[SpId];

        var livePerCell = new int[cells.Length];
        foreach (var (_, cell) in cellOf)
        {
            livePerCell[cell]++;
        }

        var newClustersPerCell = new int[cells.Length];
        var shared = 0;
        foreach (var (_, sources) in placement)
        {
            newClustersPerCell[OnlyCell(oldHomes[sources[0]])]++;
            shared += sources.Count > 1 ? 1 : 0;
        }

        Assert.Multiple(() =>
        {
            Assert.That(shared, Is.GreaterThan(0), "consecutive old clusters of one home share new clusters");
            for (var c = 0; c < cells.Length; c++)
            {
                Assert.That(newClustersPerCell[c], Is.EqualTo((livePerCell[c] + n - 1) / n), $"cell {c}: its run packs densely, ⌈live / N'⌉ clusters");
            }

            Assert.That(cs.LastRebuildForeignCellSlots, Is.Zero);
        });
    }

    // ── set migration (#1394 S3): the same placement rule, a component added ──

    private static ushort SaV1Id => Archetype<MtopoSaV1Arch>.Metadata.ArchetypeId;
    private static ushort SaV2Id => Archetype<MtopoSaV2Arch>.Metadata.ArchetypeId;

    [Test]
    [VerifiesRule("SCHEMA-12")]
    public void NonSpatial_SetAdd_DenseInOldOrder()
    {
        var ids = new List<EntityId>();
        List<long> oldOrder;
        using (var scope = ServiceProvider.CreateScope())
        {
            using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
            dbe.RegisterComponentFromAccessor<MtopoSaKeep>();
            dbe.RegisterComponentFromAccessor<MtopoSaVer>();
            dbe.InitializeArchetypes();
            var all = new EntityId[300];
            using (var t = dbe.CreateQuickTransaction(DurabilityMode.Immediate))
            {
                for (var i = 0; i < all.Length; i++)
                {
                    all[i] = t.Spawn<MtopoSaV1Arch>(MtopoSaV1Arch.Keep.Set(new MtopoSaKeep(i)), MtopoSaV1Arch.Ver.Set(new MtopoSaVer(i * 1000L + 1)));
                }

                Assert.That(t.Commit(), Is.True);
            }

            using (var t = dbe.CreateQuickTransaction(DurabilityMode.Immediate))
            {
                for (var i = 0; i < all.Length; i += 3)
                {
                    t.Destroy(all[i]);
                }

                Assert.That(t.Commit(), Is.True);
            }

            for (var i = 0; i < all.Length; i++)
            {
                if (i % 3 != 0)
                {
                    ids.Add(all[i]);
                }
            }
        }

        using (var scope = ServiceProvider.CreateScope())
        {
            // A plain reopen to read the pre-migration placement: the old clusters, holes and all.
            using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
            dbe.RegisterComponentFromAccessor<MtopoSaKeep>();
            dbe.RegisterComponentFromAccessor<MtopoSaVer>();
            dbe.InitializeArchetypes();
            oldOrder = ClusterPlacementProbe.InStorageOrder(LocationsBeforeMigration(dbe, SaV1Id));
            var n = StateOf(dbe, SaV1Id).Layout.ClusterSize;
            Assert.That(oldOrder, Has.Count.EqualTo(ids.Count), "premise: every survivor is placed");
            Assert.That(StateOf(dbe, SaV1Id).ActiveClusterCount, Is.GreaterThan((ids.Count + n - 1) / n), "premise: the destroys left holes");
        }

        using (var scope = ServiceProvider.CreateScope())
        {
            using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
            dbe.RegisterComponentFromAccessor<MtopoSaKeep>();
            dbe.RegisterComponentFromAccessor<MtopoSaVer>();
            dbe.RegisterComponentFromAccessor<MtopoSaAdded>();
            dbe.InitializeArchetypes();
            Assert.That(dbe.LastOpenArchetypeSetMigrations, Has.Count.EqualTo(1), "premise: the set migrated");

            AssertDenseInOldOrder(dbe, SaV2Id, oldOrder, "set add");
            Assert.That(dbe.LastOpenMigrationOrphanChainEntities, Is.Zero);
            using var t = dbe.CreateQuickTransaction();
            foreach (var id in ids)
            {
                var e = t.Open(id);
                var a = e.Read(MtopoSaV2Arch.Keep).A;
                var v = e.Read(MtopoSaV2Arch.Ver).V;
                Assert.That(v, Is.EqualTo(a * 1000L + 1), $"entity {a}: its Versioned value moved with it");
            }
        }
    }

    [Test]
    public void SetAdd_ShrinkingN_SplitsInSlotOrder()
    {
        var ids = new List<EntityId>();
        using (var scope = ServiceProvider.CreateScope())
        {
            using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
            dbe.RegisterComponentFromAccessor<MtopoShKeep>();
            dbe.InitializeArchetypes();
            using var t = dbe.CreateQuickTransaction(DurabilityMode.Immediate);
            for (var i = 0; i < 260; i++)
            {
                ids.Add(t.Spawn<MtopoShV1Arch>(MtopoShV1Arch.Keep.Set(new MtopoShKeep(i))));
            }

            Assert.That(t.Commit(), Is.True);
        }

        List<long> oldOrder;
        int oldN;
        using (var scope = ServiceProvider.CreateScope())
        {
            using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
            dbe.RegisterComponentFromAccessor<MtopoShKeep>();
            dbe.InitializeArchetypes();
            var id = Archetype<MtopoShV1Arch>.Metadata.ArchetypeId;
            oldOrder = ClusterPlacementProbe.InStorageOrder(LocationsBeforeMigration(dbe, id));
            oldN = StateOf(dbe, id).Layout.ClusterSize;
        }

        using (var scope = ServiceProvider.CreateScope())
        {
            using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
            dbe.RegisterComponentFromAccessor<MtopoShKeep>();
            dbe.RegisterComponentFromAccessor<MtopoShBig>();
            dbe.InitializeArchetypes();
            var id = Archetype<MtopoShV2Arch>.Metadata.ArchetypeId;
            var newN = StateOf(dbe, id).Layout.ClusterSize;
            Assert.That(newN, Is.LessThan(oldN), "premise: the added component shrinks the cluster");

            // Every full old cluster splits across new ones in slot order: the i-th entity in old order is at slot i % N' of the (i / N')-th cluster.
            AssertDenseInOldOrder(dbe, id, oldOrder, $"set add, N {oldN} -> {newN}");
            using var t = dbe.CreateQuickTransaction();
            for (var i = 0; i < ids.Count; i++)
            {
                var keep = t.Open(ids[i]).Read(MtopoShV2Arch.Keep).A;
                Assert.That(keep, Is.EqualTo(i));
            }
        }
    }

    private static void ConfigureSpatialSet(DatabaseEngine dbe, bool v2)
    {
        dbe.RecordRebuildPlacementForTest = true;
        dbe.RegisterComponentFromAccessor<MtopoSsPos>();
        dbe.RegisterComponentFromAccessor<MtopoSsStat>();
        if (v2)
        {
            dbe.RegisterComponentFromAccessor<MtopoSsAdded>();
        }

        dbe.ConfigureSpatialGrid(SpatialGridConfig.Flat(new Vector2(0, 0), new Vector2(WorldMax, WorldMax), CellSize, reclusterBudgetMs: 0f,
            batchSpawnSortThreshold: 0));
        dbe.InitializeArchetypes();
    }

    [Test]
    [VerifiesRule("SCHEMA-12")]
    public void Spatial_SetAdd_NoClusterMixesHomeKeys()
    {
        (float X, float Y)[] cells = [(100f, 100f), (300f, 100f), (100f, 500f), (700f, 700f)];
        const int perCell = 150;
        var cellOf = new Dictionary<EntityId, int>();
        var v1Id = Archetype<MtopoSsV1Arch>.Metadata.ArchetypeId;
        var v2Id = Archetype<MtopoSsV2Arch>.Metadata.ArchetypeId;

        // Round-robin across the cells, so consecutive old clusters are other cells; every fifth destroyed; closed right after a fence.
        using (var scope = ServiceProvider.CreateScope())
        {
            using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
            ConfigureSpatialSet(dbe, false);
            var all = new List<(EntityId Id, int Cell)>();
            using (var t = dbe.CreateQuickTransaction(DurabilityMode.Immediate))
            {
                for (var k = 0; k < cells.Length * perCell; k++)
                {
                    var c = k % cells.Length;
                    var i = k / cells.Length;
                    var (cx, cy) = cells[c];
                    var x = cx + 5 + i % 9 * 10;
                    var y = cy + 5 + i / 9 % 9 * 10;
                    var pos = new MtopoSsPos { Bounds = new AABB2F { MinX = x, MinY = y, MaxX = x, MaxY = y }, Tag = k };
                    all.Add((t.Spawn<MtopoSsV1Arch>(MtopoSsV1Arch.Pos.Set(pos), MtopoSsV1Arch.Stat.Set(new MtopoSsStat(k))), c));
                }

                Assert.That(t.Commit(), Is.True);
            }

            using (var t = dbe.CreateQuickTransaction(DurabilityMode.Immediate))
            {
                for (var k = 0; k < all.Count; k += 5)
                {
                    t.Destroy(all[k].Id);
                }

                Assert.That(t.Commit(), Is.True);
            }

            for (var k = 0; k < all.Count; k++)
            {
                if (k % 5 != 0)
                {
                    cellOf[all[k].Id] = all[k].Cell;
                }
            }

            dbe.WriteTickFence(1);
        }

        // Per old cluster, the cells of its entities, read on a plain reopen.
        var oldHomes = new Dictionary<int, HashSet<int>>();
        using (var scope = ServiceProvider.CreateScope())
        {
            using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
            ConfigureSpatialSet(dbe, false);
            var locations = ClusterPlacementProbe.Locations(dbe, v1Id);
            foreach (var (id, cell) in cellOf)
            {
                var chunk = locations[(long)id.RawValue].Chunk;
                if (!oldHomes.TryGetValue(chunk, out var set))
                {
                    oldHomes[chunk] = set = [];
                }

                set.Add(cell);
            }
        }

        Assert.That(oldHomes.Values, Has.All.Count.EqualTo(1), "premise: an old cluster holds one cell (claimed by cell)");

        using var scope2 = ServiceProvider.CreateScope();
        using var dbe2 = scope2.ServiceProvider.GetRequiredService<DatabaseEngine>();
        ConfigureSpatialSet(dbe2, true);
        Assert.That(dbe2.LastOpenArchetypeSetMigrations, Has.Count.EqualTo(1), "premise: the set migrated");
        var cs = StateOf(dbe2, v2Id);
        var placement = dbe2.LastOpenRebuildPlacementMap[v2Id];
        var newLocations = ClusterPlacementProbe.Locations(dbe2, v2Id);
        var newClusterCells = new Dictionary<int, HashSet<int>>();
        foreach (var (id, cell) in cellOf)
        {
            var chunk = newLocations[(long)id.RawValue].Chunk;
            if (!newClusterCells.TryGetValue(chunk, out var set))
            {
                newClusterCells[chunk] = set = [];
            }

            set.Add(cell);
        }

        Assert.Multiple(() =>
        {
            foreach (var (newChunk, sources) in placement)
            {
                var homes = new HashSet<int>();
                foreach (var oldChunk in sources)
                {
                    homes.UnionWith(oldHomes[oldChunk]);
                }

                Assert.That(homes, Has.Count.EqualTo(1), $"new cluster {newChunk} carved from old clusters [{string.Join(", ", sources)}] of one home");
            }

            Assert.That(newClusterCells.Values, Has.All.Count.EqualTo(1), "no new cluster holds entities of two cells");
            Assert.That(cs.LastRebuildForeignCellSlots, Is.Zero, "no stray filed for the first fence");
            Assert.That(newLocations, Has.Count.EqualTo(cellOf.Count), "every entity placed");
        });

        dbe2.WriteTickFence(2);
        Assert.That(cs.LastTickMigrationCount, Is.Zero, "the first fence after the set migration moves nothing");

        using var epoch = EpochGuard.Enter(dbe2.EpochManager);
        for (var c = 0; c < cells.Length; c++)
        {
            var (cx, cy) = cells[c];
            var expected = new HashSet<EntityId>();
            foreach (var (id, cell) in cellOf)
            {
                if (cell == c)
                {
                    expected.Add(id);
                }
            }

            var found = new HashSet<EntityId>();
            foreach (var hit in cs.QueryAabb(dbe2.Realm0Grid, cx, cy, double.NegativeInfinity, cx + CellSize, cy + CellSize, double.PositiveInfinity))
            {
                found.Add(hit.Entity);
            }

            Assert.That(found, Is.EquivalentTo(expected), $"cell {c}: every entity, after the set migration");
        }
    }

    // AC-13 on the set path, as Spatial_SameHomeRun_SharesClusters on the field path: a run of consecutive old clusters of one home shares new clusters and
    // packs densely — ⌈live / N'⌉ new clusters per home — instead of one new cluster per old one.
    [Test]
    [VerifiesRule("SCHEMA-12")]
    public void Spatial_SetAdd_SameHomeRun_PacksDensely()
    {
        (float X, float Y)[] cells = [(100f, 100f), (500f, 300f)];
        var cellOf = SeedSpatialSet(cells, perCell: 200, interleaved: false);
        var oldHomes = OldClusterCellsSet(cellOf);
        var oldClustersPerCell = new int[cells.Length];
        foreach (var (_, set) in oldHomes)
        {
            foreach (var cell in set)
            {
                oldClustersPerCell[cell]++;
            }
        }

        Assert.Multiple(() =>
        {
            Assert.That(oldHomes.Values, Has.All.Count.EqualTo(1), "premise: an old cluster holds one cell");
            Assert.That(oldClustersPerCell, Has.All.GreaterThan(2), "premise: a run of several old clusters per cell");
        });

        using var scope = ServiceProvider.CreateScope();
        using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
        ConfigureSpatialSet(dbe, true);
        var v2Id = Archetype<MtopoSsV2Arch>.Metadata.ArchetypeId;
        Assert.That(dbe.LastOpenArchetypeSetMigrations, Has.Count.EqualTo(1), "premise: the set migrated");
        var cs = StateOf(dbe, v2Id);
        var n = cs.Layout.ClusterSize;
        var placement = dbe.LastOpenRebuildPlacementMap[v2Id];

        var livePerCell = new int[cells.Length];
        foreach (var (_, cell) in cellOf)
        {
            livePerCell[cell]++;
        }

        var newClustersPerCell = new int[cells.Length];
        var shared = 0;
        foreach (var (_, sources) in placement)
        {
            newClustersPerCell[OnlyCell(oldHomes[sources[0]])]++;
            shared += sources.Count > 1 ? 1 : 0;
        }

        Assert.Multiple(() =>
        {
            Assert.That(shared, Is.GreaterThan(0), "consecutive old clusters of one home share new clusters");
            for (var c = 0; c < cells.Length; c++)
            {
                Assert.That(newClustersPerCell[c], Is.EqualTo((livePerCell[c] + n - 1) / n), $"cell {c}: its run packs densely, ⌈live / N'⌉ clusters");
            }

            Assert.That(cs.LastRebuildForeignCellSlots, Is.Zero);
            Assert.That(ClusterPlacementProbe.Locations(dbe, v2Id), Has.Count.EqualTo(cellOf.Count), "every entity placed");
        });
    }

    /// <summary>
    /// Seeds the set-path spatial archetype (revision 1) as <see cref="SeedSpatial"/> seeds the field-path one: <paramref name="perCell"/> entities per
    /// cell, cell after cell or round-robin, every fifth destroyed, closed right after a fence. Returns survivor → cell index.
    /// </summary>
    private Dictionary<EntityId, int> SeedSpatialSet((float X, float Y)[] cells, int perCell, bool interleaved)
    {
        var cellOf = new Dictionary<EntityId, int>();
        using var scope = ServiceProvider.CreateScope();
        using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
        ConfigureSpatialSet(dbe, false);
        var all = new List<(EntityId Id, int Cell)>();
        using (var t = dbe.CreateQuickTransaction(DurabilityMode.Immediate))
        {
            for (var k = 0; k < cells.Length * perCell; k++)
            {
                var c = interleaved ? k % cells.Length : k / perCell;
                var i = interleaved ? k / cells.Length : k % perCell;
                var (cx, cy) = cells[c];
                var x = cx + 5 + i % 9 * 10;
                var y = cy + 5 + i / 9 % 9 * 10;
                var pos = new MtopoSsPos { Bounds = new AABB2F { MinX = x, MinY = y, MaxX = x, MaxY = y }, Tag = k };
                all.Add((t.Spawn<MtopoSsV1Arch>(MtopoSsV1Arch.Pos.Set(pos), MtopoSsV1Arch.Stat.Set(new MtopoSsStat(k))), c));
            }

            Assert.That(t.Commit(), Is.True);
        }

        using (var t = dbe.CreateQuickTransaction(DurabilityMode.Immediate))
        {
            for (var k = 0; k < all.Count; k += 5)
            {
                t.Destroy(all[k].Id);
            }

            Assert.That(t.Commit(), Is.True);
        }

        for (var k = 0; k < all.Count; k++)
        {
            if (k % 5 != 0)
            {
                cellOf[all[k].Id] = all[k].Cell;
            }
        }

        dbe.WriteTickFence(1);
        return cellOf;
    }

    /// <summary>Per old cluster of the set-path archetype, the cells of its entities, read on a plain reopen before the migration.</summary>
    private Dictionary<int, HashSet<int>> OldClusterCellsSet(Dictionary<EntityId, int> cellOf)
    {
        using var scope = ServiceProvider.CreateScope();
        using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
        ConfigureSpatialSet(dbe, false);
        var homes = new Dictionary<int, HashSet<int>>();
        var locations = ClusterPlacementProbe.Locations(dbe, Archetype<MtopoSsV1Arch>.Metadata.ArchetypeId);
        foreach (var (id, cell) in cellOf)
        {
            var chunk = locations[(long)id.RawValue].Chunk;
            if (!homes.TryGetValue(chunk, out var set))
            {
                homes[chunk] = set = [];
            }

            set.Add(cell);
        }

        return homes;
    }
}
