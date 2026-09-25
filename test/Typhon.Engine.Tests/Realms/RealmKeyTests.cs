using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Typhon.Engine.Tests.Runtime;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests.Realms;

[Component("Typhon.Test.Realm.Pos", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct RealmPos
{
    [Field]
    [SpatialIndex]
    public AABB2F Bounds;

    [Field]
    [RealmKey]
    public ushort Realm;

    [Field]
    public int Tag;
}

[Archetype]
partial class RealmUnit : Archetype<RealmUnit>
{
    public static readonly Comp<RealmPos> Pos = Register<RealmPos>();
}

// Schema refusals — registered only by the test that expects the refusal.
[Component("Typhon.Test.Realm.NoSpatial", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct RealmKeyWithoutSpatial
{
    [Field]
    [RealmKey]
    public ushort Realm;

    [Field]
    public int Tag;
}

[Component("Typhon.Test.Realm.NotUShort", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct RealmKeyNotUShort
{
    [Field]
    [SpatialIndex]
    public AABB2F Bounds;

    [Field]
    [RealmKey]
    public int Realm;
}

[Component("Typhon.Test.Realm.Twice", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct RealmKeyTwice
{
    [Field]
    [SpatialIndex]
    public AABB2F Bounds;

    [Field]
    [RealmKey]
    public ushort Realm;

    [Field]
    [RealmKey]
    public ushort Other;
}

[Component("Typhon.Test.Realm.Indexed", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct RealmKeyIndexed
{
    [Field]
    [SpatialIndex]
    public AABB2F Bounds;

    [Field]
    [RealmKey]
    [Index(AllowMultiple = true)]
    public ushort Realm;
}

[Component("Typhon.Test.Realm.Versioned", 1, StorageMode = StorageMode.Versioned)]
[StructLayout(LayoutKind.Sequential)]
struct RealmKeyVersioned
{
    [Field]
    [SpatialIndex]
    public AABB2F Bounds;

    [Field]
    [RealmKey]
    public ushort Realm;
}

/// <summary>
/// Realms C1b: an archetype names each entity's realm with a <c>[RealmKey]</c> <see cref="ushort"/> beside its spatial field; the entity is placed in that
/// realm's grid, and every spatial query runs in exactly one realm — whatever the coordinates, another realm's entities never answer.
/// </summary>
[TestFixture]
class RealmKeyTests : TestBase<RealmKeyTests>
{
    private const float World = 100f;

    private static SpatialGridConfig Grid(double cellSize) => SpatialGridConfig.Flat(new Vector2(0, 0), new Vector2(World, World), cellSize);

    private static RealmConfig Simulated(SpatialGridConfig grid) => RealmConfig.SimulatedAlways(grid);

    // A world an f32 field cannot address: at 1e12 adjacent floats are 65 536 apart, against 100-unit cells.
    private static SpatialGridConfig FarWorld() =>
        new(new Vector3D(1e12, 1e12, 0), new Vector3D(1e12 + 1_000, 1e12 + 1_000, 100), 100d);

    private DatabaseEngine Engine() => ServiceProvider.GetRequiredService<DatabaseEngine>();

    /// <summary>Realm 0 (cell 10), realm 1 (cell 10 — the same cell keys) and realm 2 (cell 25 — another geometry), all over the same world box.</summary>
    private DatabaseEngine ThreeRealms()
    {
        var dbe = Engine();
        dbe.RegisterComponentFromAccessor<RealmPos>();
        dbe.RegisterComponentFromAccessor<TierPos>();
        dbe.ConfigureRealms(4);
        dbe.ConfigureSpatialGrid(Grid(10));
        dbe.Realms.Register(new RealmId(1), Simulated(Grid(10)));
        dbe.Realms.Register(new RealmId(2), Simulated(Grid(25)));
        dbe.InitializeArchetypes();
        return dbe;
    }

    private static RealmPos At(float x, float y, ushort realm, int tag = 0, float half = 0f) =>
        new() { Bounds = new AABB2F { MinX = x - half, MinY = y - half, MaxX = x + half, MaxY = y + half }, Realm = realm, Tag = tag };

    private static ArchetypeClusterState StateOf<TArch>(DatabaseEngine dbe) where TArch : Archetype<TArch>, new() =>
        dbe._archetypeStates[Archetype<TArch>.Metadata.ArchetypeId].ClusterState;

    // ── schema ──

    [Test]
    public void RealmKey_WithoutSpatialField_Refused()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => Engine().RegisterComponentFromAccessor<RealmKeyWithoutSpatial>());
        Assert.That(ex.Message, Does.Contain("[RealmKey]").And.Contain("[SpatialIndex]"));
    }

    [Test]
    public void RealmKey_NotUShort_Refused()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => Engine().RegisterComponentFromAccessor<RealmKeyNotUShort>());
        Assert.That(ex.Message, Does.Contain("requires type ushort"));
    }

    [Test]
    public void RealmKey_Twice_Refused()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => Engine().RegisterComponentFromAccessor<RealmKeyTwice>());
        Assert.That(ex.Message, Does.Contain("at most one"));
    }

    [Test]
    public void RealmKey_WithAnIndex_Refused() =>
        Assert.That(Assert.Throws<InvalidOperationException>(() => Engine().RegisterComponentFromAccessor<RealmKeyIndexed>()).Message,
            Does.Contain("cannot also carry [Index]"));

    [Test]
    public void RealmKey_OnAVersionedComponent_Refused() =>
        Assert.That(Assert.Throws<InvalidOperationException>(() => Engine().RegisterComponentFromAccessor<RealmKeyVersioned>()).Message,
            Does.Contain("Versioned"));

    [Test]
    public void GeneratedAndReflectedSchemas_AgreeOnTheRealmKey()
    {
        var generated = new DatabaseDefinitions().CreateFromAccessor<RealmPos>(null);
        var reflected = new DatabaseDefinitions().CreateFromAccessor(typeof(RealmPos), null);
        Assert.That(generated.RealmKeyField, Is.Not.Null, "the source generator must carry [RealmKey]");
        Assert.That(reflected.RealmKeyField, Is.Not.Null, "reflection must carry [RealmKey]");
        Assert.That(generated.RealmKeyField.Name, Is.EqualTo(nameof(RealmPos.Realm)));
        Assert.That(generated.RealmKeyField.OffsetInComponentStorage, Is.EqualTo(reflected.RealmKeyField.OffsetInComponentStorage));
        Assert.That(generated.FieldsByName[nameof(RealmPos.Tag)].IsRealmKey, Is.False);
    }

    // ── open ──

    [Test]
    public void RealmKeyedArchetype_OpensWithoutRealm0()
    {
        var dbe = Engine();
        dbe.RegisterComponentFromAccessor<RealmPos>();
        dbe.ConfigureRealms(4);
        dbe.Realms.Register(new RealmId(3), Simulated(Grid(10)));
        Assert.DoesNotThrow(() => dbe.InitializeArchetypes());
        Assert.That(dbe.Realm0Grid, Is.Null);

        using (var tx = dbe.CreateQuickTransaction())
        {
            tx.Spawn<RealmUnit>(RealmUnit.Pos.Set(At(5, 5, 3)));
            tx.Commit();
        }

        using (EpochGuard.Enter(dbe.EpochManager))
        {
            Assert.That(dbe.ClusterSpatialQuery<RealmUnit>(new RealmId(3)).AABB(new AABB2F { MinX = 0, MinY = 0, MaxX = 10, MaxY = 10 }).Count(),
                Is.EqualTo(1));
        }

        Assert.Throws<InvalidOperationException>(() => dbe.ClusterSpatialQuery<RealmUnit>(), "realm 0 is not registered: a query naming it is an error");
    }

    [Test]
    public void IncompatibleRealm_DoesNotFailTheOpen_ButRefusesEntry()
    {
        var dbe = Engine();
        dbe.RegisterComponentFromAccessor<RealmPos>();
        dbe.ConfigureRealms(2);
        dbe.ConfigureSpatialGrid(Grid(10));
        dbe.Realms.Register(new RealmId(1), Simulated(FarWorld()));
        Assert.DoesNotThrow(() => dbe.InitializeArchetypes(), "an f32 archetype need not fit every realm, only the ones it enters");

        using var tx = dbe.CreateQuickTransaction();
        var ex = Assert.Throws<InvalidOperationException>(() => tx.Spawn<RealmUnit>(RealmUnit.Pos.Set(At(5, 5, 1))));
        Assert.That(ex.Message, Does.Contain("Realm 1 cannot hold").And.Contain("AABB2F"));
        Assert.DoesNotThrow(() => tx.Spawn<RealmUnit>(RealmUnit.Pos.Set(At(5, 5, 0))));
    }

    [Test]
    public void UnkeyedArchetype_StillRequiresRealm0_AtOpen()
    {
        var dbe = Engine();
        dbe.RegisterComponentFromAccessor<TierPos>();
        dbe.ConfigureRealms(2);
        dbe.Realms.Register(new RealmId(1), Simulated(Grid(10)));
        Assert.Throws<InvalidOperationException>(() => dbe.InitializeArchetypes());
    }

    // ── spawn ──

    [TestCase(3)]
    [TestCase(65535)] // RealmId.None
    public void Spawn_InAnUnregisteredRealm_Throws_AtTheCall(int realmValue)
    {
        var realm = (ushort)realmValue;
        using var dbe = ThreeRealms();
        using var tx = dbe.CreateQuickTransaction();
        var ex = Assert.Throws<InvalidOperationException>(() => tx.Spawn<RealmUnit>(RealmUnit.Pos.Set(At(5, 5, realm))));
        Assert.That(ex.Message, Does.Contain("is not registered"));
    }

    [Test]
    public void Spawn_PlacesEachEntityInItsRealmsGrid_AndAnUnkeyedArchetypeInRealm0()
    {
        using var dbe = ThreeRealms();
        using (var tx = dbe.CreateQuickTransaction())
        {
            tx.Spawn<RealmUnit>(RealmUnit.Pos.Set(At(55, 55, 0)));
            tx.Spawn<RealmUnit>(RealmUnit.Pos.Set(At(55, 55, 1)));
            tx.Spawn<RealmUnit>(RealmUnit.Pos.Set(At(55, 55, 2)));
            tx.Spawn<TierUnit>(TierUnit.Pos.Set(new TierPos { Bounds = new AABB2F { MinX = 55, MinY = 55, MaxX = 55, MaxY = 55 } }));
            tx.Commit();
        }

        var cs = StateOf<RealmUnit>(dbe);
        Assert.That(cs.ActiveClusterCount, Is.EqualTo(3), "one cluster per realm: the same cell key in two realms is two cells");
        using var epoch = EpochGuard.Enter(dbe.EpochManager);
        for (ushort realm = 0; realm < 3; realm++)
        {
            var grid = dbe.RealmTable.Get(realm).Grid;
            var hits = dbe.ClusterSpatialQuery<RealmUnit>(new RealmId(realm)).AABB(new AABB2F { MinX = 50, MinY = 50, MaxX = 60, MaxY = 60 });
            var n = 0;
            foreach (var hit in hits)
            {
                n++;
                Assert.That(cs.ClusterRealmMap[hit.ClusterChunkId], Is.EqualTo(realm));
                Assert.That(cs.ClusterCellMap[hit.ClusterChunkId], Is.EqualTo(grid.WorldToCellKey(55, 55, 0)), "filed in its own realm's cell geometry");
                Assert.That(grid.GetCell(cs.ClusterCellMap[hit.ClusterChunkId]).EntityCount, Is.EqualTo(realm == 0 ? 2 : 1),
                    "a cell counts its realm's entities only: realm 0's also holds the unkeyed TierUnit");
            }

            Assert.That(n, Is.EqualTo(1), $"realm {realm}");
        }

        var tierCs = StateOf<TierUnit>(dbe);
        Assert.That(tierCs.PresentRealmSpatial.Length, Is.EqualTo(1));
        Assert.That(tierCs.RealmSpatial[1], Is.Null);
        Assert.That(tierCs.RealmSpatial[2], Is.Null);
    }

    [Test]
    public void Destroy_ReleasesTheSlot_InTheEntitysRealm()
    {
        using var dbe = ThreeRealms();
        EntityId inRealm2;
        using (var tx = dbe.CreateQuickTransaction())
        {
            inRealm2 = tx.Spawn<RealmUnit>(RealmUnit.Pos.Set(At(55, 55, 2)));
            tx.Spawn<RealmUnit>(RealmUnit.Pos.Set(At(55, 55, 0)));
            tx.Commit();
        }

        var realm0Cell = dbe.Realm0Grid.WorldToCellKey(55, 55, 0);
        var realm2Grid = dbe.RealmTable.Get(2).Grid;
        var realm2Cell = realm2Grid.WorldToCellKey(55, 55, 0);
        using (var tx = dbe.CreateQuickTransaction())
        {
            tx.Destroy(inRealm2);
            tx.Commit();
        }

        Assert.That(realm2Grid.GetCell(realm2Cell).EntityCount, Is.EqualTo(0), "realm 2's cell gave its entity back");
        Assert.That(dbe.Realm0Grid.GetCell(realm0Cell).EntityCount, Is.EqualTo(1), "realm 0's cell was not charged for it");
        using (EpochGuard.Enter(dbe.EpochManager))
        {
            Assert.That(dbe.ClusterSpatialQuery<RealmUnit>(new RealmId(2)).AABB(new AABB2F { MinX = 0, MinY = 0, MaxX = World, MaxY = World }).Count(),
                Is.EqualTo(0));
        }
    }

    // ── isolation oracle ──

    /// <summary>
    /// The same population, coordinate for coordinate, in three realms — two sharing a cell geometry, one not — spawned as one batch past the Morton-sort
    /// threshold. Every query shape, in every realm, must return exactly that realm's copy of the brute-force answer.
    /// </summary>
    [Test]
    [VerifiesRule("SQ-08")]
    public void EveryQueryShape_AnswersOnlyItsRealm_BruteForceOracle()
    {
        using var dbe = ThreeRealms();
        const int perRealm = 150;
        const int realmCount = 3;
        var rng = new Random(20260925);
        var boxes = new AABB2F[perRealm];
        var ids = new long[realmCount, perRealm];
        var byId = new Dictionary<long, (int realm, int index)>();

        using (var tx = dbe.CreateQuickTransaction())
        {
            for (var i = 0; i < perRealm; i++)
            {
                var x = (float)(rng.NextDouble() * (World - 2) + 1);
                var y = (float)(rng.NextDouble() * (World - 2) + 1);
                var half = (float)(rng.NextDouble() * 0.5);
                boxes[i] = At(x, y, 0, 0, half).Bounds;
                for (ushort r = 0; r < realmCount; r++)
                {
                    var id = tx.Spawn<RealmUnit>(RealmUnit.Pos.Set(At(x, y, r, i, half)));
                    ids[r, i] = unchecked((long)id.RawValue);
                    byId[ids[r, i]] = (r, i);
                }
            }

            Assert.That(perRealm * realmCount, Is.GreaterThanOrEqualTo(dbe.Realm0Grid.Config.BatchSpawnSortThreshold), "exercise the sorted batch path");
            tx.Commit();
        }

        var cs = StateOf<RealmUnit>(dbe);
        var rayHits = new (long entityId, double distance)[perRealm * realmCount];
        var frustumHits = new long[perRealm * realmCount];
        var knnHits = new (long entityId, double distSq)[8];

        for (var q = 0; q < 40; q++)
        {
            var cx = rng.NextDouble() * World;
            var cy = rng.NextDouble() * World;
            var ext = rng.NextDouble() * 20 + 1;
            var aabb = new AABB2F { MinX = (float)(cx - ext), MinY = (float)(cy - ext), MaxX = (float)(cx + ext), MaxY = (float)(cy + ext) };
            var sphere = new BSphere2F { CenterX = (float)cx, CenterY = (float)cy, Radius = (float)ext };
            var expectAabb = Indices(boxes, b => b.MinX <= aabb.MaxX && b.MaxX >= aabb.MinX && b.MinY <= aabb.MaxY && b.MaxY >= aabb.MinY);
            var expectRadius = Indices(boxes, b => DistSq(b, sphere.CenterX, sphere.CenterY) <= (double)sphere.Radius * sphere.Radius);

            // Ray and frustum semantics live in their own oracle tests; here the answer must be the SAME index set in every realm, and only that realm's ids.
            var dir = rng.NextDouble() * Math.PI * 2;
            var (dx, dy) = (Math.Cos(dir), Math.Sin(dir));
            double[] planes = [1, 0, -(cx - ext), -1, 0, cx + ext, 0, 1, -(cy - ext), 0, -1, cy + ext];
            var fMin = new Vector3Like(cx - ext, cy - ext, 0);
            var fMax = new Vector3Like(cx + ext, cy + ext, 0);
            HashSet<int> rayRef = null, frustumRef = null;
            double[] knnRef = null;

            for (ushort r = 0; r < realmCount; r++)
            {
                var realm = new RealmId(r);
                var grid = dbe.RealmTable.Get(r).Grid;
                var query = dbe.ClusterSpatialQuery<RealmUnit>(realm);
                var ctx = $"query {q}, realm {r}";

                using (EpochGuard.Enter(dbe.EpochManager))
                {
                    Assert.That(Collect(query.AABB(aabb), byId, r, ctx), Is.EquivalentTo(expectAabb), $"AABB, {ctx}");
                    Assert.That(Collect(query.Radius(sphere), byId, r, ctx), Is.EquivalentTo(expectRadius), $"radius, {ctx}");

                    var counts = new int[1];
                    query.CountRadius([sphere], counts);
                    Assert.That(counts[0], Is.EqualTo(expectRadius.Count), $"batched radius, {ctx}");

                    var n = cs.QueryRay(grid, cx, cy, 0, dx, dy, 0, ext * 2, rayHits, ordered: false);
                    var rayIdx = ToIndices(rayHits.AsSpan(0, n).ToArray().Select(h => h.entityId), byId, r, $"ray, {ctx}");
                    rayRef ??= rayIdx;
                    Assert.That(rayIdx, Is.EquivalentTo(rayRef), $"ray, {ctx}");

                    n = cs.QueryFrustum(grid, planes, 4, fMin, fMax, frustumHits);
                    var frIdx = ToIndices(frustumHits.AsSpan(0, n).ToArray(), byId, r, $"frustum, {ctx}");
                    frustumRef ??= frIdx;
                    Assert.That(frIdx, Is.EquivalentTo(frustumRef), $"frustum, {ctx}");
                    Assert.That(frIdx, Is.SupersetOf(expectAabb.Where(i => Contains(aabb, boxes[i]))), $"frustum covers the boxes inside it, {ctx}");

                    n = cs.QueryNearest(grid, cx, cy, 0, knnHits.Length, knnHits);
                    var knn = knnHits.AsSpan(0, n).ToArray();
                    ToIndices(knn.Select(h => h.entityId), byId, r, $"kNN, {ctx}");
                    var knnDist = knn.Select(h => h.distSq).OrderBy(d => d).ToArray();
                    knnRef ??= knnDist;
                    Assert.That(knnDist, Is.EqualTo(knnRef).Within(1e-6), $"kNN distances, {ctx}");
                }

                using var tx = dbe.CreateQuickTransaction();
                var viaQuery = tx.Query<RealmUnit>().InRealm(realm).WhereInAABB<RealmPos>(aabb.MinX, aabb.MinY, 0, aabb.MaxX, aabb.MaxY, 0).Execute();
                Assert.That(ToIndices(viaQuery.Select(e => unchecked((long)e.RawValue)), byId, r, $"EcsQuery AABB, {ctx}"), Is.EquivalentTo(expectAabb));
                var nearby = tx.Query<RealmUnit>().InRealm(realm).WhereNearby<RealmPos>(cx, cy, 0, ext).Execute();
                Assert.That(ToIndices(nearby.Select(e => unchecked((long)e.RawValue)), byId, r, $"EcsQuery radius, {ctx}"),
                    Is.EquivalentTo(expectRadius));
            }
        }
    }

    [Test]
    public void EcsQuery_InAnUnregisteredRealm_Throws()
    {
        using var dbe = ThreeRealms();
        using var tx = dbe.CreateQuickTransaction();
        Assert.Throws<InvalidOperationException>(() => tx.Query<RealmUnit>().InRealm(new RealmId(3)).WhereInAABB<RealmPos>(0, 0, 0, 1, 1, 0).Execute());
        Assert.Throws<InvalidOperationException>(() => dbe.ClusterSpatialQuery<RealmUnit>(RealmId.None));
    }

    private static bool Contains(AABB2F outer, AABB2F inner) =>
        inner.MinX >= outer.MinX && inner.MaxX <= outer.MaxX && inner.MinY >= outer.MinY && inner.MaxY <= outer.MaxY;

    private static double DistSq(AABB2F b, double x, double y)
    {
        var dx = Math.Max(Math.Max(b.MinX - x, 0), x - b.MaxX);
        var dy = Math.Max(Math.Max(b.MinY - y, 0), y - b.MaxY);
        return dx * dx + dy * dy;
    }

    private static HashSet<int> Indices(AABB2F[] boxes, Func<AABB2F, bool> match)
    {
        var set = new HashSet<int>();
        for (var i = 0; i < boxes.Length; i++)
        {
            if (match(boxes[i]))
            {
                set.Add(i);
            }
        }

        return set;
    }

    private static HashSet<int> Collect(AabbClusterEnumerator hits, Dictionary<long, (int realm, int index)> byId, int realm, string ctx)
    {
        var ids = new List<long>();
        foreach (var hit in hits)
        {
            ids.Add(unchecked((long)hit.Entity.RawValue));
        }

        return ToIndices(ids, byId, realm, ctx);
    }

    private static HashSet<int> ToIndices(IEnumerable<long> ids, Dictionary<long, (int realm, int index)> byId, int realm, string ctx)
    {
        var set = new HashSet<int>();
        foreach (var id in ids)
        {
            Assert.That(byId.TryGetValue(id, out var owner), Is.True, $"unknown entity, {ctx}");
            Assert.That(owner.realm, Is.EqualTo(realm), $"another realm's entity answered, {ctx}");
            Assert.That(set.Add(owner.index), Is.True, $"duplicate hit, {ctx}");
        }

        return set;
    }
}
