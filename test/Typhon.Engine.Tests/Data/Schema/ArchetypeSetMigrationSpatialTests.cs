using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Typhon.Engine.Internals;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests;

// ═════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════
// SWG-shaped spatial migration (design 07 §6.3, AC-14/15/30; #1394 S2 rows). A realm-keyed spatial archetype with ClusterDurability.Checkpoint, two realms,
// many cells, entities sitting in the CC-02 hysteresis band at cluster slot 0. The owner's requirement (D7): a migrating open files no stray and the first
// tick fence migrates nothing. D11 makes the AABB-centre cell rule every rebuild's, so a PLAIN reopen of the same shape files no stray either.
//
// Two shapes: the placement component itself widens (a field migration, so the home keys are read through the migration's conversion), and the SWG
// Player's own change, a SingleVersion component added to the set (rev 3 -> 4 "PlayerGear", then 4 -> 5 "PlayerBuffs").
// ═════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════

#region Realm-keyed player: the placement component widens

[Component("Typhon.Schema.UnitTest.AsmsPos", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct AsmsPosV1
{
    [Field]
    [SpatialIndex]
    public AABB2F Bounds;

    [Field]
    [RealmKey]
    public ushort Realm;

    [Field]
    public ushort Pad;

    [Field]
    public int Tag;
}

[Component("Typhon.Schema.UnitTest.AsmsPos", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct AsmsPosV2
{
    [Field]
    [SpatialIndex]
    public AABB2F Bounds;

    [Field]
    [RealmKey]
    public ushort Realm;

    [Field]
    public ushort Pad;

    [Field]
    public int Tag;

    [Field]
    public long Extra;
}

[Component("Typhon.Schema.UnitTest.AsmsStats", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct AsmsStats
{
    public int Hp;
    public int Pad;
    public AsmsStats(int hp) { Hp = hp; Pad = 0; }
}

[Archetype(1, "Asms.Player", ClusterDurability = ClusterDurability.Checkpoint)]
partial class AsmsPlayer : Archetype<AsmsPlayer>
{
    public static readonly Comp<AsmsPosV1> Pos = Register<AsmsPosV1>();
    public static readonly Comp<AsmsStats> Stats = Register<AsmsStats>();
}

/// <summary>Typed V2 handles only: an archetype of its own name, empty, so D3 never sees two classes of one name.</summary>
[Archetype(1, "Asms.PlayerV2Handles", ClusterDurability = ClusterDurability.Checkpoint)]
partial class AsmsPlayerV2Handles : Archetype<AsmsPlayerV2Handles>
{
    public static readonly Comp<AsmsPosV2> Pos = Register<AsmsPosV2>();
    public static readonly Comp<AsmsStats> Stats = Register<AsmsStats>();
}

#endregion

#region SWG-shaped set adds: Player rev 3 -> 4 (+Gear) -> 5 (+Buffs)

[Component("Typhon.Schema.UnitTest.AsmsSwgPos", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct AsmsSwgPos
{
    [Field]
    [SpatialIndex]
    public AABB2F Bounds;

    [Field]
    [RealmKey]
    public ushort Realm;

    [Field]
    public ushort Pad;

    [Field]
    public int Tag;
}

[Component("Typhon.Schema.UnitTest.AsmsSwgStats", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct AsmsSwgStats
{
    public int Hp;
    public int Mind;
    public AsmsSwgStats(int hp) { Hp = hp; Mind = hp * 2; }
}

/// <summary>A Versioned component, carried by its chain: the SWG Player mixes both storage modes.</summary>
[Component("Typhon.Schema.UnitTest.AsmsSwgProfile", 1)]
[StructLayout(LayoutKind.Sequential)]
struct AsmsSwgProfile
{
    public long Account;
    public AsmsSwgProfile(long account) => Account = account;
}

/// <summary>"PlayerGear"-shaped: SingleVersion, 16 B.</summary>
[Component("Typhon.Schema.UnitTest.AsmsSwgGear", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct AsmsSwgGear
{
    public long Weapon;
    public long Armor;
}

/// <summary>"PlayerBuffs"-shaped: SingleVersion, 16 B.</summary>
[Component("Typhon.Schema.UnitTest.AsmsSwgBuffs", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct AsmsSwgBuffs
{
    public int Speed;
    public int Shield;
    public int Regen;
    public int Expires;
}

[Archetype(3, Name = "Asms.SwgPlayer", ClusterDurability = ClusterDurability.Checkpoint)]
class AsmsSwgV3 : Archetype<AsmsSwgV3>
{
    public static readonly Comp<AsmsSwgPos> Pos = Register<AsmsSwgPos>();
    public static readonly Comp<AsmsSwgStats> Stats = Register<AsmsSwgStats>();
    public static readonly Comp<AsmsSwgProfile> Profile = Register<AsmsSwgProfile>();
}

[Archetype(4, Name = "Asms.SwgPlayer", ClusterDurability = ClusterDurability.Checkpoint)]
class AsmsSwgV4 : Archetype<AsmsSwgV4>
{
    public static readonly Comp<AsmsSwgPos> Pos = Register<AsmsSwgPos>();
    public static readonly Comp<AsmsSwgStats> Stats = Register<AsmsSwgStats>();
    public static readonly Comp<AsmsSwgProfile> Profile = Register<AsmsSwgProfile>();
    public static readonly Comp<AsmsSwgGear> Gear = Register<AsmsSwgGear>();
}

[Archetype(5, Name = "Asms.SwgPlayer", ClusterDurability = ClusterDurability.Checkpoint)]
class AsmsSwgV5 : Archetype<AsmsSwgV5>
{
    public static readonly Comp<AsmsSwgPos> Pos = Register<AsmsSwgPos>();
    public static readonly Comp<AsmsSwgStats> Stats = Register<AsmsSwgStats>();
    public static readonly Comp<AsmsSwgProfile> Profile = Register<AsmsSwgProfile>();
    public static readonly Comp<AsmsSwgGear> Gear = Register<AsmsSwgGear>();
    public static readonly Comp<AsmsSwgBuffs> Buffs = Register<AsmsSwgBuffs>();
}

#endregion

#region A realm stray at the split boundary of a shrinking-N set add

[Component("Typhon.Schema.UnitTest.AsmsRsPos", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct AsmsRsPos
{
    [Field]
    [SpatialIndex]
    public AABB2F Bounds;

    [Field]
    [RealmKey]
    public ushort Realm;

    [Field]
    public ushort Pad;

    [Field]
    public int Tag;
}

/// <summary>Wide enough that adding it shrinks the cluster size N: every full old cluster splits across new ones.</summary>
[Component("Typhon.Schema.UnitTest.AsmsRsBig", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct AsmsRsBig
{
    public long B0, B1, B2, B3, B4, B5, B6, B7, B8, B9, B10, B11, B12;
}

[Archetype(1, Name = "Asms.RealmStray", ClusterDurability = ClusterDurability.Checkpoint)]
class AsmsRsV1 : Archetype<AsmsRsV1>
{
    public static readonly Comp<AsmsRsPos> Pos = Register<AsmsRsPos>();
}

[Archetype(2, Name = "Asms.RealmStray", ClusterDurability = ClusterDurability.Checkpoint)]
class AsmsRsV2 : Archetype<AsmsRsV2>
{
    public static readonly Comp<AsmsRsPos> Pos = Register<AsmsRsPos>();
    public static readonly Comp<AsmsRsBig> Big = Register<AsmsRsBig>();
}

#endregion

#region R12: an added component carries the spatial field, or the realm key

[Component("Typhon.Schema.UnitTest.AsmsR12Stats", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct AsmsR12Stats
{
    public int Hp;
    public int Pad;
    public AsmsR12Stats(int hp) { Hp = hp; Pad = 0; }
}

[Component("Typhon.Schema.UnitTest.AsmsR12Pos", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct AsmsR12Pos
{
    [Field]
    [SpatialIndex]
    public AABB2F Bounds;

    [Field]
    public int Tag;

    [Field]
    public int Pad;
}

/// <summary>A non-spatial archetype that gains its spatial component.</summary>
[Archetype(1, Name = "Asms.R12Spatial")]
class AsmsR12SpatialV1 : Archetype<AsmsR12SpatialV1>
{
    public static readonly Comp<AsmsR12Stats> Stats = Register<AsmsR12Stats>();
}

[Archetype(2, Name = "Asms.R12Spatial")]
class AsmsR12SpatialV2 : Archetype<AsmsR12SpatialV2>
{
    public static readonly Comp<AsmsR12Stats> Stats = Register<AsmsR12Stats>();
    public static readonly Comp<AsmsR12Pos> Pos = Register<AsmsR12Pos>();
}

[Component("Typhon.Schema.UnitTest.AsmsR12KPos", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct AsmsR12KPos
{
    [Field]
    [SpatialIndex]
    public AABB2F Bounds;

    [Field]
    public int Tag;

    [Field]
    public int Pad;
}

[Component("Typhon.Schema.UnitTest.AsmsR12Key", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct AsmsR12Key
{
    [Field]
    [RealmKey]
    public ushort Realm;

    [Field]
    public ushort Pad;

    [Field]
    public int Pad2;
}

/// <summary>An unkeyed spatial archetype that gains a realm key in a component of its own.</summary>
[Archetype(1, Name = "Asms.R12Keyed")]
class AsmsR12KeyedV1 : Archetype<AsmsR12KeyedV1>
{
    public static readonly Comp<AsmsR12KPos> Pos = Register<AsmsR12KPos>();
}

[Archetype(2, Name = "Asms.R12Keyed")]
class AsmsR12KeyedV2 : Archetype<AsmsR12KeyedV2>
{
    public static readonly Comp<AsmsR12KPos> Pos = Register<AsmsR12KPos>();
    public static readonly Comp<AsmsR12Key> Key = Register<AsmsR12Key>();
}

#endregion

[NonParallelizable]
class ArchetypeSetMigrationSpatialTests : TestBase<ArchetypeSetMigrationSpatialTests>
{
    private const float World = 1000f;
    private const float CellSize = 50f;
    private const float HysteresisRatio = 0.2f;
    private const float Band = CellSize * HysteresisRatio;   // 10
    private const int CellY = 5;
    private const int FirstCellX = 2;
    private const int CellCount = 10;
    private static readonly ushort[] RealmIds = [1, 2];

    private static ushort ArchetypeId => Archetype<AsmsPlayer>.Metadata.ArchetypeId;

    private static SpatialGridConfig Grid() =>
        SpatialGridConfig.Flat(new Vector2(0, 0), new Vector2(World, World), CellSize, migrationHysteresisRatio: HysteresisRatio, reclusterBudgetMs: 0f,
            batchSpawnSortThreshold: 0);

    private static void Configure(DatabaseEngine dbe, bool v2)
    {
        dbe.RecordRebuildPlacementForTest = true;
        if (v2)
        {
            dbe.RegisterComponentFromAccessor<AsmsPosV2>();
        }
        else
        {
            dbe.RegisterComponentFromAccessor<AsmsPosV1>();
        }

        dbe.RegisterComponentFromAccessor<AsmsStats>();
        dbe.ConfigureRealms(4);
        foreach (var realm in RealmIds)
        {
            dbe.Realms.Register(new RealmId(realm), RealmConfig.SimulatedAlways(Grid()));
        }

        dbe.InitializeArchetypes();
    }

    private static ArchetypeClusterState StateOf(DatabaseEngine dbe) => StateOf(dbe, ArchetypeId);

    private static ArchetypeClusterState StateOf(DatabaseEngine dbe, ushort archetypeId) => dbe._archetypeStates[archetypeId].ClusterState;

    /// <summary>Writes an entity's position (and realm) through the typed handle of the archetype under test.</summary>
    private delegate void WritePos(Transaction tx, EntityId id, Placed p);

    private static readonly WritePos WritePlayer = (tx, id, p) => tx.OpenMut(id).Set(AsmsPlayer.Pos, PosV1(p));

    /// <summary>Where the test put an entity: its realm and position, kept in step with every move.</summary>
    private sealed class Placed
    {
        public ushort Realm;
        public float X;
        public float Y;
        public int Tag;
    }

    private static AsmsPosV1 PosV1(Placed p) =>
        new() { Bounds = new AABB2F { MinX = p.X, MinY = p.Y, MaxX = p.X, MaxY = p.Y }, Realm = p.Realm, Tag = p.Tag };

    private static AABB2F PointBox(Placed p) => new() { MinX = p.X, MinY = p.Y, MaxX = p.X, MaxY = p.Y };

    private static float CellMinX(int cellIndex) => (FirstCellX + cellIndex) * CellSize;

    /// <summary>
    /// The <paramref name="k"/>-th spawn of <see cref="Spawn"/>'s pattern: round-robin across every (realm, cell), so consecutive old clusters are other
    /// homes, spread over each cell's interior.
    /// </summary>
    private static Placed PlacedAt(int k)
    {
        var homes = RealmIds.Length * CellCount;
        var home = k % homes;
        var i = k / homes;
        return new Placed
        {
            Realm = RealmIds[home / CellCount],
            X = CellMinX(home % CellCount) + 5 + i % 9 * 4.5f,
            Y = CellY * CellSize + 5 + i / 9 % 9 * 4.5f,
            Tag = k,
        };
    }

    /// <summary>
    /// Spawns <paramref name="perCell"/> entities in every (realm, cell), round-robin across them so consecutive old clusters are other homes, spread over
    /// each cell's interior, then runs a fence.
    /// </summary>
    private static Dictionary<EntityId, Placed> Spawn(DatabaseEngine dbe, int perCell)
    {
        var placed = new Dictionary<EntityId, Placed>();
        using (var tx = dbe.CreateQuickTransaction())
        {
            for (var k = 0; k < RealmIds.Length * CellCount * perCell; k++)
            {
                var p = PlacedAt(k);
                placed[tx.Spawn<AsmsPlayer>(AsmsPlayer.Pos.Set(PosV1(p)), AsmsPlayer.Stats.Set(new AsmsStats(k)))] = p;
            }

            Assert.That(tx.Commit(), Is.True);
        }

        dbe.WriteTickFence(1);
        return placed;
    }

    /// <summary>The entity at the lowest occupied slot of every active cluster — slot 0 of a cluster the spawn opened.</summary>
    private static unsafe List<EntityId> LowestSlotEntities(DatabaseEngine dbe, ushort archetypeId)
    {
        var cs = StateOf(dbe, archetypeId);
        var result = new List<EntityId>();
        using var epoch = EpochGuard.Enter(dbe.EpochManager);
        var accessor = cs.ClusterSegment.CreateChunkAccessor();
        try
        {
            for (var i = 0; i < cs.ActiveClusterCount; i++)
            {
                var clusterBase = accessor.GetChunkAddress(cs.ActiveClusterIds[i]);
                var occupancy = *(ulong*)clusterBase;
                if (occupancy == 0)
                {
                    continue;
                }

                var slot = BitOperations.TrailingZeroCount(occupancy);
                result.Add(EntityId.FromRawValue(*(ulong*)(clusterBase + cs.Layout.EntityIdsOffset + slot * 8)));
            }
        }
        finally
        {
            accessor.Dispose();
        }

        return result;
    }

    /// <summary>Moves <paramref name="ids"/> along X to <paramref name="newX"/>, in one transaction.</summary>
    private static void MoveX(DatabaseEngine dbe, Dictionary<EntityId, Placed> placed, IEnumerable<EntityId> ids, Func<Placed, float> newX,
        WritePos write = null)
    {
        write ??= WritePlayer;
        using var tx = dbe.CreateQuickTransaction();
        foreach (var id in ids)
        {
            var p = placed[id];
            p.X = newX(p);
            write(tx, id, p);
        }

        Assert.That(tx.Commit(), Is.True);
    }

    /// <summary>
    /// Moves every cluster's slot-0 entity into the hysteresis band of the next cell along X: inside that cell, within the write barrier's dead zone, so it
    /// stays in its cluster (CC-02 allows it) and the fence moves nothing.
    /// </summary>
    private static int MoveSlot0IntoTheBand(DatabaseEngine dbe, Dictionary<EntityId, Placed> placed) =>
        MoveSlot0IntoTheBand(dbe, placed, ArchetypeId, WritePlayer);

    private static int MoveSlot0IntoTheBand(DatabaseEngine dbe, Dictionary<EntityId, Placed> placed, ushort archetypeId, WritePos write)
    {
        var slot0 = LowestSlotEntities(dbe, archetypeId);
        MoveX(dbe, placed, slot0, p => MathF.Floor(p.X / CellSize) * CellSize + CellSize + Band / 2, write);
        dbe.WriteTickFence(2);
        Assert.That(StateOf(dbe, archetypeId).LastTickMigrationCount, Is.Zero,
            "premise: a band resident is inside the dead zone — the fence moved nothing");
        return slot0.Count;
    }

    /// <summary>
    /// How many clusters have their lowest-slot entity outside the cell they are filed in: the clusters the first-slot rule would have given the
    /// neighbour cell. Non-zero makes a zero stray count meaningful.
    /// </summary>
    private static int ClustersWhoseSlot0IsInTheBand(DatabaseEngine dbe, Dictionary<EntityId, Placed> placed) =>
        ClustersWhoseSlot0IsInTheBand(dbe, placed, ArchetypeId);

    private static int ClustersWhoseSlot0IsInTheBand(DatabaseEngine dbe, Dictionary<EntityId, Placed> placed, ushort archetypeId)
    {
        var cs = StateOf(dbe, archetypeId);
        var count = 0;
        var locations = ClusterPlacementProbe.Locations(dbe, archetypeId);
        foreach (var id in LowestSlotEntities(dbe, archetypeId))
        {
            var p = placed[id];
            var grid = dbe.RealmTable.Get(p.Realm).Grid;
            count += grid.WorldToCellKey(p.X, p.Y, 0) != cs.ClusterCellMap[locations[(long)id.RawValue].Chunk] ? 1 : 0;
        }

        return count;
    }

    /// <summary>Every (realm, cell) answers exactly the entities whose position it holds, and the cells of a realm answer all of its entities.</summary>
    private static void AssertPerCellQueriesComplete(DatabaseEngine dbe, Dictionary<EntityId, Placed> placed) =>
        AssertPerCellQueriesComplete<AsmsPlayer>(dbe, placed);

    private static void AssertPerCellQueriesComplete<TArch>(DatabaseEngine dbe, Dictionary<EntityId, Placed> placed) where TArch : Archetype<TArch>, new()
    {
        using var epoch = EpochGuard.Enter(dbe.EpochManager);
        foreach (var realm in RealmIds)
        {
            var all = new HashSet<EntityId>();
            for (var c = 0; c <= CellCount; c++)
            {
                var minX = CellMinX(c);
                var minY = CellY * CellSize;
                var expected = new HashSet<EntityId>();
                foreach (var (id, p) in placed)
                {
                    if (p.Realm == realm && p.X >= minX && p.X < minX + CellSize && p.Y >= minY && p.Y < minY + CellSize)
                    {
                        expected.Add(id);
                    }
                }

                var found = new HashSet<EntityId>();
                var box = new AABB2F { MinX = minX, MinY = minY, MaxX = minX + CellSize - 0.01f, MaxY = minY + CellSize - 0.01f };
                foreach (var hit in dbe.ClusterSpatialQuery<TArch>(new RealmId(realm)).AABB(box))
                {
                    found.Add(hit.Entity);
                }

                Assert.That(found, Is.EquivalentTo(expected), $"realm {realm}, cell {FirstCellX + c}");
                all.UnionWith(found);
            }

            var inRealm = 0;
            foreach (var (_, p) in placed)
            {
                inRealm += p.Realm == realm ? 1 : 0;
            }

            Assert.That(all, Has.Count.EqualTo(inRealm), $"realm {realm}: its cells answer every entity");
        }
    }

    [Test]
    [VerifiesRule("SCHEMA-12")]
    [CancelAfter(30_000)]
    public void SwgShaped_FieldMigration_NoForeignSlots()
    {
        Dictionary<EntityId, Placed> placed;
        using (var scope = ServiceProvider.CreateScope())
        {
            using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
            Configure(dbe, false);
            placed = Spawn(dbe, perCell: 100);
            Assert.That(placed, Has.Count.EqualTo(2_000));
            Assert.That(MoveSlot0IntoTheBand(dbe, placed), Is.GreaterThan(CellCount * RealmIds.Length), "premise: several old clusters per home");
            // Closed right after that fence: every entity is within its cluster's cell plus the band (CC-02).
        }

        using (var scope = ServiceProvider.CreateScope())
        {
            using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
            Configure(dbe, true);
            var cs = StateOf(dbe);

            Assert.That(dbe.LastOpenRebuildPlacementMap, Is.Not.Null.And.ContainKey(ArchetypeId), "premise: the open migrated the archetype");
            Assert.That(ClustersWhoseSlot0IsInTheBand(dbe, placed), Is.GreaterThan(0),
                "premise: band residents still sit at slot 0 of new clusters, so the first-slot rule would have filed their clusters' entities as strays");
            Assert.Multiple(() =>
            {
                Assert.That(cs.LastRebuildForeignCellSlots, Is.Zero, "no cell stray at the migrating open");
                Assert.That(cs.LastRebuildForeignRealmSlots, Is.Zero, "no realm stray at the migrating open");
                Assert.That(dbe.LastOpenMigrationOrphanChainEntities, Is.Zero);
                Assert.That(ClusterPlacementProbe.Locations(dbe, ArchetypeId), Has.Count.EqualTo(placed.Count), "every entity placed");
            });

            dbe.WriteTickFence(3);
            Assert.Multiple(() =>
            {
                Assert.That(cs.LastTickMigrationCount, Is.Zero, "the first tick fence after the migration migrates nothing (D7)");
                Assert.That(cs.LastTickRealmChanges, Is.Zero, "and changes no entity's realm (AC-14)");
            });
            AssertPerCellQueriesComplete(dbe, placed);

            using var tx = dbe.CreateQuickTransaction();
            Assert.Multiple(() =>
            {
                foreach (var (id, p) in placed)
                {
                    var pos = tx.Open(id).Read(AsmsPlayerV2Handles.Pos);
                    Assert.That((pos.Realm, pos.Tag, pos.Bounds.MinX, pos.Extra), Is.EqualTo((p.Realm, p.Tag, p.X, 0L)), $"entity {p.Tag}");
                }
            });
        }
    }

    [Test]
    [CancelAfter(30_000)]
    public void CarriedStrays_EqualAPlainReopen()
    {
        Dictionary<EntityId, Placed> placed;
        int strays;
        using (var scope = ServiceProvider.CreateScope())
        {
            using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
            Configure(dbe, false);
            placed = Spawn(dbe, perCell: 100);

            // After the last fence and before the close, the entity at slot 1 of every cluster crosses its cell's band (design 07 §6.3, "what is tolerated"):
            // stored outside its cluster's cell plus band, with no fence left to move it. Slot 1 keeps it in the first new cluster its old one is split
            // into, among entities spread over the whole cell.
            var slot1 = new List<EntityId>();
            var locations = ClusterPlacementProbe.Locations(dbe, ArchetypeId);
            foreach (var (id, _) in placed)
            {
                if (locations[(long)id.RawValue].Slot == 1)
                {
                    slot1.Add(id);
                }
            }

            strays = slot1.Count;
            MoveX(dbe, placed, slot1, p => MathF.Floor(p.X / CellSize) * CellSize + CellSize + Band + 5);
        }

        int plainCell;
        int plainRealm;
        using (var scope = ServiceProvider.CreateScope())
        {
            // The plain reopen of the same file. No fence runs, and the clean close writes back what it read: the migrating open below reads the same data.
            using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
            Configure(dbe, false);
            plainCell = StateOf(dbe).LastRebuildForeignCellSlots;
            plainRealm = StateOf(dbe).LastRebuildForeignRealmSlots;
        }

        using (var scope = ServiceProvider.CreateScope())
        {
            using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
            Configure(dbe, true);
            var cs = StateOf(dbe);
            Assert.That(dbe.LastOpenRebuildPlacementMap, Is.Not.Null.And.ContainKey(ArchetypeId), "premise: the open migrated the archetype");
            Assert.Multiple(() =>
            {
                // Not every crosser is a stray: in a cluster of a few entities near the edge, the crosser pulls the AABB centre into the next cell, where
                // every entity of the cluster is within the band (D11) — the cluster is filed there and nothing of it moves.
                Assert.That(plainCell, Is.InRange(1, strays), "premise: the plain reopen files the crossers its clusters' cells leave foreign");
                Assert.That(cs.LastRebuildForeignCellSlots, Is.EqualTo(plainCell), "the migration carries the strays as-is: no more, no fewer");
                Assert.That(cs.LastRebuildForeignRealmSlots, Is.EqualTo(plainRealm));
            });

            dbe.WriteTickFence(3);
            Assert.That(cs.LastTickMigrationCount, Is.EqualTo(plainCell), "the first fence moves the carried strays home, and nothing else");
            AssertPerCellQueriesComplete(dbe, placed);
        }
    }

    [Test]
    [VerifiesRule("CC-02")]
    [CancelAfter(30_000)]
    public void PlainReopen_BandResidentSlot0_NoStrays()
    {
        Dictionary<EntityId, Placed> placed;
        using (var scope = ServiceProvider.CreateScope())
        {
            using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
            Configure(dbe, false);
            placed = Spawn(dbe, perCell: 30);
            MoveSlot0IntoTheBand(dbe, placed);
        }

        using (var scope = ServiceProvider.CreateScope())
        {
            using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
            Configure(dbe, false);
            var cs = StateOf(dbe);
            Assert.That(dbe.LastOpenRebuildPlacementMap, Is.Null, "premise: a plain reopen, no migration");
            Assert.That(ClustersWhoseSlot0IsInTheBand(dbe, placed), Is.EqualTo(cs.ActiveClusterCount),
                "premise: every cluster's slot 0 is a band resident, which the first-slot rule filed under the neighbour cell");
            Assert.Multiple(() =>
            {
                Assert.That(cs.LastRebuildForeignCellSlots, Is.Zero, "the AABB-centre cell leaves no centre of a CC-02 cluster foreign (D11)");
                Assert.That(cs.LastRebuildForeignRealmSlots, Is.Zero);
            });

            dbe.WriteTickFence(3);
            Assert.That(cs.LastTickMigrationCount, Is.Zero, "and the first fence moves nothing");
            AssertPerCellQueriesComplete(dbe, placed);
        }
    }

    /// <summary>
    /// The strays the pure AABB-centre rule (D11 before its best-of amendment) would file for the clusters as they stand: per cluster, the entities outside
    /// the band of the cell holding the centre of its entities' box, in the cluster's realm.
    /// </summary>
    private static int StraysUnderThePureCentreRule(DatabaseEngine dbe, Dictionary<EntityId, Placed> placed)
    {
        var cs = StateOf(dbe);
        var byCluster = new Dictionary<int, List<Placed>>();
        foreach (var (id, loc) in ClusterPlacementProbe.Locations(dbe, ArchetypeId))
        {
            if (!byCluster.TryGetValue(loc.Chunk, out var members))
            {
                byCluster[loc.Chunk] = members = [];
            }

            members.Add(placed[EntityId.FromRawValue((ulong)id)]);
        }

        var strays = 0;
        foreach (var (chunk, members) in byCluster)
        {
            var grid = dbe.RealmTable.Get(cs.ClusterRealmMap[chunk]).Grid;
            float loX = float.MaxValue, loY = float.MaxValue, hiX = float.MinValue, hiY = float.MinValue;
            foreach (var p in members)
            {
                loX = MathF.Min(loX, p.X);
                loY = MathF.Min(loY, p.Y);
                hiX = MathF.Max(hiX, p.X);
                hiY = MathF.Max(hiY, p.Y);
            }

            grid.WorldToCellCoords((loX + hiX) / 2, (loY + hiY) / 2, 0, out var cx, out var cy, out var cz);
            grid.CellOriginFromCoords(cx, cy, cz, out var ox, out var oy, out _);
            foreach (var p in members)
            {
                strays += p.X < ox - Band || p.X > ox + CellSize + Band || p.Y < oy - Band || p.Y > oy + CellSize + Band ? 1 : 0;
            }
        }

        return strays;
    }

    /// <summary>
    /// D11 best-of (#1394): an entity that jumps five cells after the last fence and before the close pulls its cluster's box centre away from where its
    /// cluster mates are, so the pure centre rule filed the whole cluster. Best-of keeps the cell with the fewest strays, and files the jumper alone — what
    /// the first-slot rule filed for it.
    /// </summary>
    [Test]
    [VerifiesRule("CC-02")]
    [CancelAfter(30_000)]
    public void PlainReopen_TeleportedAfterTheFence_FilesNoMoreThanTheFirstSlotRule()
    {
        Dictionary<EntityId, Placed> placed;
        int teleported;
        using (var scope = ServiceProvider.CreateScope())
        {
            using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
            Configure(dbe, false);
            placed = Spawn(dbe, perCell: 30);

            // One entity of every cluster, never its slot 0 (so the first-slot rule keeps the cluster's own cell and files exactly the jumper).
            var jumpers = new List<EntityId>();
            foreach (var (id, loc) in ClusterPlacementProbe.Locations(dbe, ArchetypeId))
            {
                if (loc.Slot == 1)
                {
                    jumpers.Add(EntityId.FromRawValue((ulong)id));
                }
            }

            teleported = jumpers.Count;
            using var tx = dbe.CreateQuickTransaction();
            foreach (var id in jumpers)
            {
                var p = placed[id];
                p.Y += 5 * CellSize;
                tx.OpenMut(id).Set(AsmsPlayer.Pos, PosV1(p));
            }

            Assert.That(tx.Commit(), Is.True);
            // No fence: the close persists the jump with nothing left to move it.
        }

        using (var scope = ServiceProvider.CreateScope())
        {
            using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
            Configure(dbe, false);
            var cs = StateOf(dbe);
            Assert.Multiple(() =>
            {
                Assert.That(teleported, Is.EqualTo(cs.ActiveClusterCount), "premise: one jumper per cluster");
                Assert.That(StraysUnderThePureCentreRule(dbe, placed), Is.GreaterThan(teleported),
                    "premise: the pure centre rule files more than the jumpers here");
                Assert.That(cs.LastRebuildForeignCellSlots, Is.EqualTo(teleported), "best-of files the jumpers and nothing else");
                Assert.That(cs.LastRebuildForeignRealmSlots, Is.Zero);
            });

            dbe.WriteTickFence(3);
            Assert.That(cs.LastTickMigrationCount, Is.EqualTo(teleported), "the first fence moves the jumpers home, and only them");

            // Each jumper is found in a box around its new position only, and nothing was lost.
            using var epoch = EpochGuard.Enter(dbe.EpochManager);
            foreach (var realm in RealmIds)
            {
                var found = new HashSet<EntityId>();
                foreach (var hit in dbe.ClusterSpatialQuery<AsmsPlayer>(new RealmId(realm)).AABB(new AABB2F { MinX = 0, MinY = 0, MaxX = World, MaxY = World }))
                {
                    found.Add(hit.Entity);
                }

                var expected = new HashSet<EntityId>();
                foreach (var (id, p) in placed)
                {
                    if (p.Realm == realm)
                    {
                        expected.Add(id);
                    }
                }

                Assert.That(found, Is.EquivalentTo(expected), $"realm {realm}");
            }
        }
    }

    // ── SWG-shaped set adds (AC-10, AC-14): the Player gains "PlayerGear" (rev 3 -> 4), then "PlayerBuffs" (rev 4 -> 5) ────────────────────────────

    private const string GearName = "Typhon.Schema.UnitTest.AsmsSwgGear";
    private const string BuffsName = "Typhon.Schema.UnitTest.AsmsSwgBuffs";

    private static ushort IdOf<TArch>() where TArch : Archetype<TArch>, new() => Archetype<TArch>.Metadata.ArchetypeId;

    private static AsmsSwgPos SwgPos(Placed p) => new() { Bounds = PointBox(p), Realm = p.Realm, Tag = p.Tag };

    private static readonly WritePos WriteSwgV3 = (tx, id, p) => tx.OpenMut(id).Set(AsmsSwgV3.Pos, SwgPos(p));

    /// <summary>
    /// Opens the SWG-shaped player at <paramref name="revision"/>. Only that revision's components are registered, so a later revision does not resolve
    /// and the open consumes this one (D3 supersedes the lower revisions that do resolve).
    /// </summary>
    private static void ConfigureSwg(DatabaseEngine dbe, int revision)
    {
        dbe.RecordRebuildPlacementForTest = true;
        dbe.RegisterComponentFromAccessor<AsmsSwgPos>();
        dbe.RegisterComponentFromAccessor<AsmsSwgStats>();
        dbe.RegisterComponentFromAccessor<AsmsSwgProfile>();
        if (revision >= 4)
        {
            dbe.RegisterComponentFromAccessor<AsmsSwgGear>();
        }

        if (revision >= 5)
        {
            dbe.RegisterComponentFromAccessor<AsmsSwgBuffs>();
        }

        dbe.ConfigureRealms(4);
        foreach (var realm in RealmIds)
        {
            dbe.Realms.Register(new RealmId(realm), RealmConfig.SimulatedAlways(Grid()));
        }

        dbe.InitializeArchetypes();
    }

    /// <summary>
    /// The rev-3 world of the owner's AC: 2 000 entities over 2 realms and 10 cells each, every cluster's slot-0 entity moved into the hysteresis band of the
    /// next cell, closed right after that fence. Returns where every entity is and the oracle's snapshot of it.
    /// </summary>
    private (Dictionary<EntityId, Placed> Placed, Dictionary<long, Dictionary<string, ArchetypeOracle.Entry>> Snapshot) SeedSwgV3()
    {
        using var scope = ServiceProvider.CreateScope();
        using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
        ConfigureSwg(dbe, 3);
        var placed = new Dictionary<EntityId, Placed>();
        using (var tx = dbe.CreateQuickTransaction())
        {
            for (var k = 0; k < RealmIds.Length * CellCount * 100; k++)
            {
                var p = PlacedAt(k);
                var id = tx.Spawn<AsmsSwgV3>(AsmsSwgV3.Pos.Set(SwgPos(p)), AsmsSwgV3.Stats.Set(new AsmsSwgStats(k)),
                    AsmsSwgV3.Profile.Set(new AsmsSwgProfile(1_000_000L + k)));
                placed[id] = p;
            }

            Assert.That(tx.Commit(), Is.True);
        }

        dbe.WriteTickFence(1);
        Assert.That(placed, Has.Count.EqualTo(2_000));
        Assert.That(MoveSlot0IntoTheBand(dbe, placed, IdOf<AsmsSwgV3>(), WriteSwgV3), Is.GreaterThan(CellCount * RealmIds.Length),
            "premise: several old clusters per home");
        return (placed, ArchetypeOracle.Snapshot(dbe, IdOf<AsmsSwgV3>(), placed.Keys));
        // Closed right after that fence: every entity is within its cluster's cell plus the band (CC-02).
    }

    /// <summary>
    /// The owner's AC at one migrating open of the SWG-shaped player (AC-14): no stray at the open, nothing moved by the first fence, every cell answering
    /// its entities, every surviving component equal by name and <paramref name="added"/> absent. Returns the oracle's snapshot after the migration.
    /// </summary>
    private static Dictionary<long, Dictionary<string, ArchetypeOracle.Entry>> AssertSwgMigrationClean<TArch>(DatabaseEngine dbe,
        Dictionary<EntityId, Placed> placed, Dictionary<long, Dictionary<string, ArchetypeOracle.Entry>> before, string added, int fromRevision,
        int toRevision) where TArch : Archetype<TArch>, new()
    {
        var id = IdOf<TArch>();
        var cs = StateOf(dbe, id);
        Assert.That(dbe.LastOpenArchetypeSetMigrations, Has.Count.EqualTo(1), "premise: the open migrated the set");
        var report = dbe.LastOpenArchetypeSetMigrations[0];
        Assert.That((report.ArchetypeName, report.FromRevision, report.ToRevision, report.EntitiesMigrated),
            Is.EqualTo(("Asms.SwgPlayer", fromRevision, toRevision, placed.Count)));
        Assert.That(ClustersWhoseSlot0IsInTheBand(dbe, placed, id), Is.GreaterThan(0),
            "premise: band residents still sit at slot 0 of new clusters, so the first-slot rule would have filed their clusters' entities as strays");
        Assert.Multiple(() =>
        {
            Assert.That(cs.LastRebuildForeignCellSlots, Is.Zero, "no cell stray at the migrating open");
            Assert.That(cs.LastRebuildForeignRealmSlots, Is.Zero, "no realm stray at the migrating open");
            Assert.That(dbe.LastOpenMigrationOrphanChainEntities, Is.Zero);
            Assert.That(ClusterPlacementProbe.Locations(dbe, id), Has.Count.EqualTo(placed.Count), "every entity placed");
        });

        var after = ArchetypeOracle.Snapshot(dbe, id, placed.Keys);
        ArchetypeOracle.AssertSurvivorsEqual(before, after);
        ArchetypeOracle.AssertAbsent(after, added);

        dbe.WriteTickFence(3);
        Assert.Multiple(() =>
        {
            Assert.That(cs.LastTickMigrationCount, Is.Zero, "the first tick fence after the migration migrates nothing (D7)");
            Assert.That(cs.LastTickRealmChanges, Is.Zero, "and changes no entity's realm");
        });
        AssertPerCellQueriesComplete<TArch>(dbe, placed);
        return after;
    }

    [Test]
    [VerifiesRule("SCHEMA-12")]
    [CancelAfter(30_000)]
    public void SwgShaped_AddSv_NoForeignSlots_FirstFenceMigratesNothing()
    {
        var (placed, before) = SeedSwgV3();

        using var scope = ServiceProvider.CreateScope();
        using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
        ConfigureSwg(dbe, 4);
        Assert.That(dbe.LastOpenRebuildPlacementMap, Is.Not.Null.And.ContainKey(IdOf<AsmsSwgV4>()), "premise: the open re-placed the archetype");
        AssertSwgMigrationClean<AsmsSwgV4>(dbe, placed, before, GearName, 3, 4);

        using var tx = dbe.CreateQuickTransaction();
        Assert.Multiple(() =>
        {
            foreach (var (id, p) in placed)
            {
                var e = tx.Open(id);
                var pos = e.Read(AsmsSwgV4.Pos);
                Assert.That((pos.Realm, pos.Tag, pos.Bounds.MinX, e.Read(AsmsSwgV4.Stats).Hp, e.Read(AsmsSwgV4.Profile).Account, e.IsEnabled(AsmsSwgV4.Gear)),
                    Is.EqualTo((p.Realm, p.Tag, p.X, p.Tag, 1_000_000L + p.Tag, false)), $"entity {p.Tag}");
            }
        });
    }

    [Test]
    [CancelAfter(30_000)]
    public void SwgShaped_TwoSuccessiveAdds()
    {
        var (placed, beforeV4) = SeedSwgV3();

        // Rev 3 -> 4: +PlayerGear. Then every tenth player is given gear, so the next migration carries a supplied value of the first added component.
        Dictionary<long, Dictionary<string, ArchetypeOracle.Entry>> beforeV5;
        using (var scope = ServiceProvider.CreateScope())
        {
            using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
            ConfigureSwg(dbe, 4);
            AssertSwgMigrationClean<AsmsSwgV4>(dbe, placed, beforeV4, GearName, 3, 4);

            using (var tx = dbe.CreateQuickTransaction())
            {
                foreach (var (id, p) in placed)
                {
                    if (p.Tag % 10 == 0)
                    {
                        tx.OpenMut(id).Enable(AsmsSwgV4.Gear, new AsmsSwgGear { Weapon = p.Tag, Armor = -p.Tag });
                    }
                }

                Assert.That(tx.Commit(), Is.True);
            }

            dbe.WriteTickFence(4);
            beforeV5 = ArchetypeOracle.Snapshot(dbe, IdOf<AsmsSwgV4>(), placed.Keys);
            // Closed right after that fence.
        }

        // Rev 4 -> 5: +PlayerBuffs. The gear given above, and the absence of the rest, survive by name.
        using (var scope = ServiceProvider.CreateScope())
        {
            using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
            ConfigureSwg(dbe, 5);
            AssertSwgMigrationClean<AsmsSwgV5>(dbe, placed, beforeV5, BuffsName, 4, 5);

            using var tx = dbe.CreateQuickTransaction();
            Assert.Multiple(() =>
            {
                foreach (var (id, p) in placed)
                {
                    // A disabled component refuses Read; the oracle above already holds the absent ones at zero bytes.
                    var e = tx.Open(id);
                    var given = p.Tag % 10 == 0;
                    var enabled = e.IsEnabled(AsmsSwgV5.Gear);
                    var gear = enabled ? e.Read(AsmsSwgV5.Gear) : default;
                    Assert.That((enabled, gear.Weapon, gear.Armor, e.Read(AsmsSwgV5.Pos).Tag, e.IsEnabled(AsmsSwgV5.Buffs)),
                        Is.EqualTo((given, given ? p.Tag : 0L, given ? -p.Tag : 0L, p.Tag, false)), $"entity {p.Tag}");
                }
            });
        }
    }

    [Test]
    [CancelAfter(30_000)]
    public void ReopenAfterMigration_NoStrays()
    {
        var (placed, _) = SeedSwgV3();

        using (var scope = ServiceProvider.CreateScope())
        {
            using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
            ConfigureSwg(dbe, 4);
            Assert.That(dbe.LastOpenArchetypeSetMigrations, Has.Count.EqualTo(1), "premise: the open migrated the set");
            dbe.WriteTickFence(3);
            // Closed right after that fence.
        }

        using (var scope = ServiceProvider.CreateScope())
        {
            using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
            ConfigureSwg(dbe, 4);
            var id = IdOf<AsmsSwgV4>();
            var cs = StateOf(dbe, id);
            Assert.That(dbe.LastOpenRebuildPlacementMap, Is.Null, "premise: a plain reopen, no migration");
            Assert.That(dbe.LastOpenArchetypeSetMigrations, Is.Empty, "premise: the committed set is the runtime set");
            Assert.That(ClustersWhoseSlot0IsInTheBand(dbe, placed, id), Is.GreaterThan(0),
                "premise: the migration kept the band residents at slot 0, which the first-slot rule filed under the neighbour cell");
            Assert.Multiple(() =>
            {
                Assert.That(cs.LastRebuildForeignCellSlots, Is.Zero, "no cell stray at the plain reopen after the migration (AC-30)");
                Assert.That(cs.LastRebuildForeignRealmSlots, Is.Zero);
                Assert.That(ClusterPlacementProbe.Locations(dbe, id), Has.Count.EqualTo(placed.Count));
            });

            dbe.WriteTickFence(4);
            Assert.Multiple(() =>
            {
                Assert.That(cs.LastTickMigrationCount, Is.Zero, "and its first fence moves nothing");
                Assert.That(cs.LastTickRealmChanges, Is.Zero);
            });
            AssertPerCellQueriesComplete<AsmsSwgV4>(dbe, placed);
        }
    }

    // ── A realm stray at the split boundary of a shrinking-N set add (AC-14, design 07 §4.6) ───────────────────────────────────────────────────────

    private static AsmsRsPos RsPos(Placed p) => new() { Bounds = PointBox(p), Realm = p.Realm, Tag = p.Tag };

    private static void ConfigureRs(DatabaseEngine dbe, bool v2)
    {
        dbe.RegisterComponentFromAccessor<AsmsRsPos>();
        if (v2)
        {
            dbe.RegisterComponentFromAccessor<AsmsRsBig>();
        }

        dbe.ConfigureRealms(4);
        foreach (var realm in RealmIds)
        {
            dbe.Realms.Register(new RealmId(realm), RealmConfig.SimulatedAlways(Grid()));
        }

        dbe.InitializeArchetypes();
    }

    /// <summary>
    /// An old cluster's realm is its first valid key, and a stray written after the last fence carries another one. When the set add shrinks N, the old
    /// cluster splits, and the stray placed at the split boundary is the first entity of the second child: by the first-valid-key rule that child would take
    /// the stray's realm and file every other entity of it as a realm crossing. The migration's realm hint keeps the old cluster's realm, so the stray is
    /// the only foreign slot, as in a plain reopen.
    /// </summary>
    [Test]
    [CancelAfter(30_000)]
    public void RealmStrayInOldCluster_DoesNotFlipChildRealm()
    {
        var newN = ArchetypeClusterInfo.Compute(2, [Unsafe.SizeOf<AsmsRsPos>(), Unsafe.SizeOf<AsmsRsBig>()]).ClusterSize;
        var placed = new Dictionary<EntityId, Placed>();
        EntityId stray;
        using (var scope = ServiceProvider.CreateScope())
        {
            using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
            ConfigureRs(dbe, false);
            using (var tx = dbe.CreateQuickTransaction())
            {
                for (var k = 0; k < RealmIds.Length * CellCount * 100; k++)
                {
                    var p = PlacedAt(k);
                    placed[tx.Spawn<AsmsRsV1>(AsmsRsV1.Pos.Set(RsPos(p)))] = p;
                }

                Assert.That(tx.Commit(), Is.True);
            }

            dbe.WriteTickFence(1);
            var cs = StateOf(dbe, IdOf<AsmsRsV1>());
            Assert.That(newN, Is.LessThan(cs.Layout.ClusterSize), "premise: the added component shrinks the cluster, so a full old cluster splits");

            // The entity at slot N' of the first full realm-1 cluster: the first entity of that cluster's second child.
            stray = default;
            var strayChunk = int.MaxValue;
            foreach (var (raw, loc) in ClusterPlacementProbe.Locations(dbe, IdOf<AsmsRsV1>()))
            {
                if (loc.Slot == newN && cs.ClusterRealmMap[loc.Chunk] == RealmIds[0] && loc.Chunk < strayChunk)
                {
                    stray = EntityId.FromRawValue((ulong)raw);
                    strayChunk = loc.Chunk;
                }
            }

            Assert.That(stray.IsNull, Is.False, "premise: a realm-1 cluster holds more than N' entities");
            Assert.That(placed[stray].Realm, Is.EqualTo(RealmIds[0]));

            // After the last fence and before the close, the stray changes realm: stored in its realm-1 cluster with realm 2's key, no fence left to move it.
            placed[stray].Realm = RealmIds[1];
            using (var tx = dbe.CreateQuickTransaction())
            {
                tx.OpenMut(stray).Set(AsmsRsV1.Pos, RsPos(placed[stray]));
                Assert.That(tx.Commit(), Is.True);
            }
        }

        int plainRealm;
        int plainCell;
        using (var scope = ServiceProvider.CreateScope())
        {
            // The plain reopen of the same file, for the count a migration must carry as-is (AC-15).
            using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
            ConfigureRs(dbe, false);
            plainRealm = StateOf(dbe, IdOf<AsmsRsV1>()).LastRebuildForeignRealmSlots;
            plainCell = StateOf(dbe, IdOf<AsmsRsV1>()).LastRebuildForeignCellSlots;
        }

        using (var scope = ServiceProvider.CreateScope())
        {
            using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
            ConfigureRs(dbe, true);
            var id = IdOf<AsmsRsV2>();
            var cs = StateOf(dbe, id);
            Assert.That(cs.Layout.ClusterSize, Is.EqualTo(newN), "premise: the migrated geometry is the predicted one");
            Assert.That(dbe.LastOpenArchetypeSetMigrations, Has.Count.EqualTo(1), "premise: the open migrated the set");

            var locations = ClusterPlacementProbe.Locations(dbe, id);
            var strayLoc = locations[(long)stray.RawValue];
            Assert.That(strayLoc.Slot, Is.Zero, "premise: the stray opens the second child of its old cluster");
            Assert.Multiple(() =>
            {
                Assert.That(plainRealm, Is.EqualTo(1), "premise: a plain reopen files the stray, and only it");
                Assert.That(cs.ClusterRealmMap[strayLoc.Chunk], Is.EqualTo(RealmIds[0]), "the child keeps its old cluster's realm, not the stray's");
                Assert.That(cs.LastRebuildForeignRealmSlots, Is.EqualTo(plainRealm), "the stray is the only realm crossing, as in a plain reopen");
                Assert.That(cs.LastRebuildForeignCellSlots, Is.EqualTo(plainCell));
                Assert.That(locations, Has.Count.EqualTo(placed.Count), "every entity placed");
            });

            dbe.WriteTickFence(3);
            Assert.That(cs.LastTickRealmChanges, Is.EqualTo(1), "the first fence moves the stray to its realm, and nothing else changes realm");
            AssertPerCellQueriesComplete<AsmsRsV2>(dbe, placed);
        }
    }

    // ── R12 (owner, 2026-11-02): an added component carrying the spatial field or the realm key ─────────────────────────────────────────────────────

    private static SpatialGridConfig R12Grid() => SpatialGridConfig.Flat(new Vector2(0, 0), new Vector2(World, World), CellSize, reclusterBudgetMs: 0f);

    private static void OpenR12Spatial(DatabaseEngine dbe, bool v2)
    {
        dbe.RegisterComponentFromAccessor<AsmsR12Stats>();
        if (v2)
        {
            dbe.RegisterComponentFromAccessor<AsmsR12Pos>();
            dbe.ConfigureSpatialGrid(R12Grid());
        }

        dbe.InitializeArchetypes();
    }

    private List<EntityId> SeedR12Spatial(int count, bool destroyAll)
    {
        using var scope = ServiceProvider.CreateScope();
        using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
        OpenR12Spatial(dbe, false);
        var ids = new List<EntityId>();
        using (var tx = dbe.CreateQuickTransaction())
        {
            for (var i = 0; i < count; i++)
            {
                ids.Add(tx.Spawn<AsmsR12SpatialV1>(AsmsR12SpatialV1.Stats.Set(new AsmsR12Stats(i + 1))));
            }

            Assert.That(tx.Commit(), Is.True);
        }

        if (destroyAll)
        {
            using var tx = dbe.CreateQuickTransaction();
            foreach (var id in ids)
            {
                tx.Destroy(id);
            }

            Assert.That(tx.Commit(), Is.True);
        }

        return ids;
    }

    [Test]
    public void AddedSpatialComponent_PopulatedArchetype_IsRefused()
    {
        var ids = SeedR12Spatial(30, destroyAll: false);

        using (var scope = ServiceProvider.CreateScope())
        {
            using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
            var ex = Assert.Throws<ArchetypeSchemaException>(() => OpenR12Spatial(dbe, true));
            Assert.Multiple(() =>
            {
                Assert.That(ex!.Kind, Is.EqualTo(ArchetypeSchemaErrorKind.PlacementOnAddedComponent));
                Assert.That(ex.Message, Is.EqualTo(
                    "Archetype 'Asms.R12Spatial' gains component 'Typhon.Schema.UnitTest.AsmsR12Pos', which carries its spatial field. Its 30 existing "
                    + "entities have no value for it, so they would all be placed at position 0. Add it in a revision that recreates these entities, or add "
                    + "it before the archetype is populated; until then, open the database with the build that wrote it. Nothing of this archetype was "
                    + "modified."));
            });
        }

        // Nothing moved: the old schema opens the file as it was.
        using (var scope = ServiceProvider.CreateScope())
        {
            using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
            OpenR12Spatial(dbe, false);
            Assert.That(dbe._persistedArchetypes["Asms.R12Spatial"].Arch.Revision, Is.EqualTo(1));
            using var tx = dbe.CreateQuickTransaction();
            for (var i = 0; i < ids.Count; i++)
            {
                Assert.That(tx.Open(ids[i]).Read(AsmsR12SpatialV1.Stats).Hp, Is.EqualTo(i + 1));
            }
        }
    }

    [Test]
    public void AddedRealmKeyComponent_PopulatedArchetype_IsRefused()
    {
        static void Open(DatabaseEngine dbe, bool v2)
        {
            dbe.RegisterComponentFromAccessor<AsmsR12KPos>();
            if (v2)
            {
                dbe.RegisterComponentFromAccessor<AsmsR12Key>();
            }

            dbe.ConfigureRealms(4);
            dbe.Realms.Register(RealmId.Default, RealmConfig.SimulatedAlways(R12Grid()));
            dbe.Realms.Register(new RealmId(1), RealmConfig.SimulatedAlways(R12Grid()));
            dbe.InitializeArchetypes();
        }

        using (var scope = ServiceProvider.CreateScope())
        {
            using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
            Open(dbe, false);
            using var tx = dbe.CreateQuickTransaction();
            for (var i = 0; i < 25; i++)
            {
                var x = 100 + i * 7f;
                var pos = new AsmsR12KPos { Bounds = new AABB2F { MinX = x, MinY = 100, MaxX = x, MaxY = 100 }, Tag = i };
                tx.Spawn<AsmsR12KeyedV1>(AsmsR12KeyedV1.Pos.Set(pos));
            }

            Assert.That(tx.Commit(), Is.True);
        }

        using (var scope = ServiceProvider.CreateScope())
        {
            using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
            var ex = Assert.Throws<ArchetypeSchemaException>(() => Open(dbe, true));
            Assert.Multiple(() =>
            {
                Assert.That(ex!.Kind, Is.EqualTo(ArchetypeSchemaErrorKind.PlacementOnAddedComponent));
                Assert.That(ex.Message, Does.StartWith(
                    "Archetype 'Asms.R12Keyed' gains component 'Typhon.Schema.UnitTest.AsmsR12Key', which carries its realm key. Its 25 existing entities have "
                    + "no value for it, so they would all be placed in realm 0."));
            });
        }
    }

    [Test]
    public void AddedSpatialComponent_EmptyArchetype_Migrates()
    {
        SeedR12Spatial(12, destroyAll: true);

        EntityId spawned;
        using (var scope = ServiceProvider.CreateScope())
        {
            using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
            OpenR12Spatial(dbe, true);
            var row = dbe._persistedArchetypes["Asms.R12Spatial"].Arch;
            Assert.That((row.ComponentCount, row.Revision), Is.EqualTo(((byte)2, 2)), "the empty archetype migrated: its row names the new set");

            using var tx = dbe.CreateQuickTransaction();
            spawned = tx.Spawn<AsmsR12SpatialV2>(AsmsR12SpatialV2.Stats.Set(new AsmsR12Stats(7)),
                AsmsR12SpatialV2.Pos.Set(new AsmsR12Pos { Bounds = new AABB2F { MinX = 120, MinY = 130, MaxX = 120, MaxY = 130 }, Tag = 7 }));
            Assert.That(tx.Commit(), Is.True);
        }

        using (var scope = ServiceProvider.CreateScope())
        {
            using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
            OpenR12Spatial(dbe, true);
            Assert.That(dbe.LastOpenArchetypeSetMigrations, Is.Empty, "the committed set is the runtime set");
            using var epoch = EpochGuard.Enter(dbe.EpochManager);
            var found = new List<EntityId>();
            foreach (var hit in dbe.ClusterSpatialQuery<AsmsR12SpatialV2>().AABB(new AABB2F { MinX = 100, MinY = 100, MaxX = 150, MaxY = 150 }))
            {
                found.Add(hit.Entity);
            }

            Assert.That(found, Is.EqualTo(new[] { spawned }), "the archetype is spatial now, and answers its new entity");
        }
    }
}
