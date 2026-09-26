using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests;

// Own archetype: ArchetypeRegistry is process-global and unsynchronised across parallel fixtures (#720).
[Component("Typhon.Test.ClReach.Pos", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct ClReachPos
{
    [Field]
    [SpatialIndex]
    public AABB2F Bounds;
}

[Archetype]
partial class ClReachUnit : Archetype<ClReachUnit>
{
    public static readonly Comp<ClReachPos> Pos = Register<ClReachPos>();
}

/// <summary>
/// <c>SQ-01</c>'s two mechanisms for a cluster whose box leaves its cell: the reach every cell walk widens by, and the named outliers every query tests
/// instead of widening for them.
/// </summary>
/// <remarks>
/// <para>Before 2026-09-13 there was one mechanism, a running maximum over every cluster box. It never fell, so one transient outlier widened every later
/// query of the archetype for the rest of the process — measured at ~50x the cells walked on SWG Tatooine — and it counted the part of an edge-cell box that
/// lies outside the world, which widened every Creature query there by ~930 m from the first tick.</para>
/// <para>100-unit cells over a 1 000 x 1 000 world (a 10 x 10 grid), so the default hysteresis margin, the fold margin, is 5.</para>
/// </remarks>
[TestFixture]
[NonParallelizable]
class ClusterReachTests : TestBase<ClusterReachTests>
{
    private const float CellSize = 100f;
    private const float World = 1_000f;

    private static ArchetypeClusterState ClusterStateOf(DatabaseEngine dbe) =>
        dbe._archetypeStates[Archetype<ClReachUnit>.Metadata.ArchetypeId].ClusterState;

    private DatabaseEngine CreateEngine()
    {
        var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<ClReachPos>();
        dbe.ConfigureSpatialGrid(SpatialGridConfig.Flat(new Vector2(0, 0), new Vector2(World, World), CellSize));
        dbe.InitializeArchetypes();
        return dbe;
    }

    private static AABB2F Box(float cx, float cy, float half) => new() { MinX = cx - half, MinY = cy - half, MaxX = cx + half, MaxY = cy + half };

    private static EntityId Spawn(DatabaseEngine dbe, AABB2F box)
    {
        using var tx = dbe.CreateQuickTransaction();
        var id = tx.Spawn<ClReachUnit>(ClReachUnit.Pos.Set(new ClReachPos { Bounds = box }));
        tx.Commit();
        return id;
    }

    /// <summary>A point at the centre of each cell, so the walks have ordinary clusters around the ones under test.</summary>
    private static void SpawnCellCentres(DatabaseEngine dbe)
    {
        using var tx = dbe.CreateQuickTransaction();
        for (int y = 0; y < 10; y++)
        {
            for (int x = 0; x < 10; x++)
            {
                tx.Spawn<ClReachUnit>(ClReachUnit.Pos.Set(new ClReachPos { Bounds = Box((x * 100f) + 50f, (y * 100f) + 50f, 0f) }));
            }
        }

        tx.Commit();
    }

    private static void AssertCovered(DatabaseEngine dbe, string when) =>
        Assert.That(ClusterStateOf(dbe).ReachCoversIndex(out var why), Is.True, $"{when}: {why}");

    private static List<long> AabbHits(DatabaseEngine dbe, AABB2F box)
    {
        var hits = new List<long>();
        var e = dbe.ClusterSpatialQuery<ClReachUnit>().AABB(in box);
        try
        {
            while (e.MoveNext())
            {
                hits.Add(unchecked((long)e.Current.Entity.RawValue));
            }
        }
        finally
        {
            e.Dispose();
        }

        return hits;
    }

    private static int AabbCount(DatabaseEngine dbe, AABB2F box)
    {
        var e = dbe.ClusterSpatialQuery<ClReachUnit>().AABB(in box);
        try
        {
            return e.Count();
        }
        finally
        {
            e.Dispose();
        }
    }

    private static List<long> AabbFill(DatabaseEngine dbe, AABB2F box, int bufferSize)
    {
        var buffer = new ClusterSpatialQueryResult[bufferSize];
        var hits = new List<long>();
        var e = dbe.ClusterSpatialQuery<ClReachUnit>().AABB(in box);
        try
        {
            int n;
            while ((n = e.Fill(buffer)) > 0)
            {
                for (int i = 0; i < n; i++)
                {
                    hits.Add(unchecked((long)buffer[i].Entity.RawValue));
                }
            }
        }
        finally
        {
            e.Dispose();
        }

        return hits;
    }

    private static List<long> RadiusHits(DatabaseEngine dbe, float x, float y, float r)
    {
        var sphere = new BSphere2F { CenterX = x, CenterY = y, Radius = r };
        var hits = new List<long>();
        var e = dbe.ClusterSpatialQuery<ClReachUnit>().Radius(in sphere);
        try
        {
            while (e.MoveNext())
            {
                hits.Add(unchecked((long)e.Current.Entity.RawValue));
            }
        }
        finally
        {
            e.Dispose();
        }

        return hits;
    }

    /// <summary>Every live entity's cluster, in one pass over the clusters.</summary>
    private static unsafe Dictionary<long, int> ClustersByEntity(DatabaseEngine dbe)
    {
        var cs = ClusterStateOf(dbe);
        var map = new Dictionary<long, int>();
        using var epoch = EpochGuard.Enter(dbe.EpochManager);
        var accessor = cs.ClusterSegment.CreateChunkAccessor();
        try
        {
            for (int i = 0; i < cs.ActiveClusterCount; i++)
            {
                int cid = cs.ActiveClusterIds[i];
                byte* b = accessor.GetChunkAddress(cid);
                ulong occupancy = *(ulong*)b;
                while (occupancy != 0)
                {
                    int slot = BitOperations.TrailingZeroCount(occupancy);
                    occupancy &= occupancy - 1;
                    map[*(long*)(b + cs.Layout.EntityIdsOffset + (slot * 8))] = cid;
                }
            }
        }
        finally
        {
            accessor.Dispose();
        }

        return map;
    }

    private static unsafe int ClusterOf(DatabaseEngine dbe, EntityId id)
    {
        var cs = ClusterStateOf(dbe);
        using var epoch = EpochGuard.Enter(dbe.EpochManager);
        var accessor = cs.ClusterSegment.CreateChunkAccessor();
        try
        {
            for (int i = 0; i < cs.ActiveClusterCount; i++)
            {
                int cid = cs.ActiveClusterIds[i];
                byte* b = accessor.GetChunkAddress(cid);
                ulong occupancy = *(ulong*)b;
                while (occupancy != 0)
                {
                    int slot = BitOperations.TrailingZeroCount(occupancy);
                    occupancy &= occupancy - 1;
                    if (*(long*)(b + cs.Layout.EntityIdsOffset + (slot * 8)) == (long)id.RawValue)
                    {
                        return cid;
                    }
                }
            }
        }
        finally
        {
            accessor.Dispose();
        }

        return -1;
    }

    [Test]
    [VerifiesRule("SQ-01")]
    public void AnOverhangOutsideTheWorld_FromAnEdgeCell_WidensNothing_AndIsStillFound()
    {
        using var dbe = CreateEngine();

        // Two edge cells, one per side. East: centre (990, 550) in cell (9, 5), the box 20 past the world's max X. West: centre (10, 450) in cell (0, 4),
        // 20 past its min X. Neither reaches a neighbouring cell, and a query reaching for the out-of-world part is clamped into the same edge cell.
        var east = (long)Spawn(dbe, new AABB2F { MinX = 960f, MinY = 520f, MaxX = 1_020f, MaxY = 580f }).RawValue;
        var west = (long)Spawn(dbe, new AABB2F { MinX = -20f, MinY = 430f, MaxX = 40f, MaxY = 470f }).RawValue;
        var cs = ClusterStateOf(dbe);
        Assert.That(Volatile.Read(ref cs.Realm0Spatial.ClusterReach), Is.Zero, "the spawns' overhang is all outside the world, so it raises nothing");

        dbe.WriteTickFence(1);
        Assert.Multiple(() =>
        {
            Assert.That(Volatile.Read(ref cs.Realm0Spatial.ClusterReach), Is.Zero, "the fence's recompute counts only the in-world part, which is none");
            Assert.That(Volatile.Read(ref cs.Realm0Spatial.EscapedClusters).Count, Is.Zero, "nor is anything named");
        });
        AssertCovered(dbe, "after the fence");

        using var epoch = EpochGuard.Enter(dbe.EpochManager);
        Assert.Multiple(() =>
        {
            Assert.That(AabbHits(dbe, new AABB2F { MinX = 1_005f, MinY = 540f, MaxX = 1_015f, MaxY = 560f }), Is.EqualTo(new[] { east }),
                "a query entirely past the max X lands in the edge cell, and finds the part of the box that is there");
            Assert.That(AabbHits(dbe, new AABB2F { MinX = -15f, MinY = 440f, MaxX = -5f, MaxY = 460f }), Is.EqualTo(new[] { west }), "and past the min X");
            Assert.That(AabbHits(dbe, new AABB2F { MinX = 950f, MinY = 540f, MaxX = 965f, MaxY = 560f }), Is.EqualTo(new[] { east }));
        });
    }

    [Test]
    [VerifiesRule("SQ-01")]
    public void ABoxCrossingTheWorldBoundFromAnInnerCell_IsReachedFromBeyondTheBound()
    {
        using var dbe = CreateEngine();

        // Centre (895, 550): INNER cell (8, 5). The box runs 120 past its cell — 100 of it through the edge cell (9, 5), 20 more past the world. A query
        // past the bound is clamped into column 9 only after it is widened, so it walks column 8 only if the reach counts the whole 120: clipping the box at
        // the world for this cell would give 100, and 1 005 - 100 = 905 keeps the walk in column 9.
        var id = (long)Spawn(dbe, new AABB2F { MinX = 770f, MinY = 540f, MaxX = 1_020f, MaxY = 560f }).RawValue;
        var query = new AABB2F { MinX = 1_005f, MinY = 545f, MaxX = 1_015f, MaxY = 555f };
        AssertCovered(dbe, "after the spawn, before any fence");
        using (var epoch = EpochGuard.Enter(dbe.EpochManager))
        {
            Assert.That(AabbHits(dbe, query), Is.EqualTo(new[] { id }), "before any fence: the spawn raised the reach by the whole overhang");
        }

        dbe.WriteTickFence(1);
        AssertCovered(dbe, "after the fence");
        using (var epoch = EpochGuard.Enter(dbe.EpochManager))
        {
            Assert.That(AabbHits(dbe, query), Is.EqualTo(new[] { id }), "and after it, reached or named");
        }
    }

    [Test]
    [VerifiesRule("SQ-01")]
    public void ABoxEndingExactlyOnItsCellBoundary_IsFoundByAQueryStartingThere()
    {
        using var dbe = CreateEngine();

        // Centre (270, 250), cell (2, 2); the box ends EXACTLY at x = 300, the cell's boundary, so its overhang is zero and the reach stays zero. Boxes are
        // closed intervals, so a query starting at 300 touches it — but floor(300 / 100) is column 3, and without stepping the low side down the walk
        // never visits column 2.
        var id = (long)Spawn(dbe, new AABB2F { MinX = 240f, MinY = 240f, MaxX = 300f, MaxY = 260f }).RawValue;
        dbe.WriteTickFence(1);
        var cs = ClusterStateOf(dbe);
        Assert.That(Volatile.Read(ref cs.Realm0Spatial.ClusterReach), Is.Zero, "precondition: nothing reaches past a cell");

        using var epoch = EpochGuard.Enter(dbe.EpochManager);
        var rayBuffer = new (long entityId, double distance)[2];
        int rayHits = cs.QueryRay(dbe.Realm0Grid, 300d, 250d, 0d, 1d, 0d, 0d, 50d, rayBuffer, categoryMask: 0);
        Assert.Multiple(() =>
        {
            Assert.That(AabbHits(dbe, new AABB2F { MinX = 300f, MinY = 245f, MaxX = 310f, MaxY = 255f }), Is.EqualTo(new[] { id }), "AABB");
            Assert.That(RadiusHits(dbe, 305f, 250f, 5f), Is.EqualTo(new[] { id }), "radius: the box is exactly 5 away");
            Assert.That(rayHits, Is.EqualTo(1), "ray starting on the boundary");
        });
    }

    [Test]
    [VerifiesRule("SQ-01")]
    public void ANamedOutlier_IsFoundByEveryQueryShape_WithoutWideningTheWalk()
    {
        using var dbe = CreateEngine();
        SpawnCellCentres(dbe);

        // Centre (250, 250), cell (2, 2); a 180 half-extent reaches 130 past that cell on every side — two cells into its neighbours.
        var id = (long)Spawn(dbe, Box(250f, 250f, 180f)).RawValue;
        dbe.WriteTickFence(1);

        var cs = ClusterStateOf(dbe);
        Assert.Multiple(() =>
        {
            Assert.That(Volatile.Read(ref cs.Realm0Spatial.EscapedClusters).Count, Is.EqualTo(1),
                "one cluster reaches past its cell, far beyond a hysteresis margin");
            Assert.That(Volatile.Read(ref cs.Realm0Spatial.ClusterReach), Is.Zero, "and every other box is a point, so the walks do not widen at all");
        });
        AssertCovered(dbe, "after the fence");

        // Every query below covers only cell (4, 4) — [400, 500) on both axes. With a reach of zero the walk visits that cell alone, and the outlier's
        // home cell (2, 2) is not in it: only the name can find it. (450, 450), the cell's own point, is outside every query.
        using var epoch = EpochGuard.Enter(dbe.EpochManager);
        var box = new AABB2F { MinX = 410f, MinY = 410f, MaxX = 420f, MaxY = 420f };
        var rayBuffer = new (long entityId, double distance)[4];
        int rayHits = cs.QueryRay(dbe.Realm0Grid, 410d, 415d, 0d, 1d, 0d, 0d, 15d, rayBuffer, categoryMask: 0);
        double[] planes = [1d, 0d, -410d, -1d, 0d, 420d, 0d, 1d, -410d, 0d, -1d, 420d];
        var frustumBuffer = new long[4];
        int frustumHits = cs.QueryFrustum(dbe.Realm0Grid, planes, 4, new Vector3Like(410d, 410d, 0d), new Vector3Like(420d, 420d, 0d), frustumBuffer,
            categoryMask: 0);
        var knnBuffer = new (long entityId, double distSq)[1];
        int knnHits = cs.QueryNearest(dbe.Realm0Grid, 425d, 425d, 0d, k: 1, knnBuffer, categoryMask: 0);

        Assert.Multiple(() =>
        {
            Assert.That(AabbHits(dbe, box), Is.EqualTo(new[] { id }), "AABB, MoveNext");
            Assert.That(AabbCount(dbe, box), Is.EqualTo(1), "AABB, Count");
            Assert.That(AabbFill(dbe, box, 3), Is.EqualTo(new[] { id }), "AABB, Fill");
            Assert.That(RadiusHits(dbe, 425f, 425f, 10f), Is.EqualTo(new[] { id }), "radius: the box contains the centre");
            Assert.That(rayHits, Is.EqualTo(1), "ray");
            Assert.That(rayBuffer[0].entityId, Is.EqualTo(id), "ray");
            Assert.That(frustumHits, Is.EqualTo(1), "frustum");
            Assert.That(frustumBuffer[0], Is.EqualTo(id), "frustum");
            Assert.That(knnHits, Is.EqualTo(1), "kNN");
            Assert.That(knnBuffer[0].entityId, Is.EqualTo(id), "kNN: distance 0, nearer than (450, 450)");
        });

        // A walk hit and a named hit in one query, drained one result at a time: Fill resumes across the switch from the cell walk to the names (SQ-03).
        var both = new AABB2F { MinX = 410f, MinY = 410f, MaxX = 455f, MaxY = 455f };
        var byMoveNext = AabbHits(dbe, both);
        var byFill = AabbFill(dbe, both, 1);
        byMoveNext.Sort();
        byFill.Sort();
        Assert.Multiple(() =>
        {
            Assert.That(byMoveNext, Has.Count.EqualTo(2), "the cell's own point and the outlier");
            Assert.That(byFill, Is.EqualTo(byMoveNext), "Fill, one result per call");
            Assert.That(AabbCount(dbe, both), Is.EqualTo(2), "Count");
        });

        // A query whose walk DOES reach the outlier's home cell finds it once, not twice — for the enumerator and for kNN, whose rings reach it too.
        Assert.That(AabbHits(dbe, new AABB2F { MinX = 240f, MinY = 240f, MaxX = 420f, MaxY = 420f }).Count(h => h == id), Is.EqualTo(1),
            "reached by the walk and named, reported once");
        var knn5 = new (long entityId, double distSq)[5];
        int n5 = cs.QueryNearest(dbe.Realm0Grid, 425d, 425d, 0d, k: 5, knn5, categoryMask: 0);
        Assert.That(knn5.Take(n5).Select(r => r.entityId).Distinct().Count(), Is.EqualTo(n5), "kNN reports every entity once");
    }

    [Test]
    [VerifiesRule("SQ-01")]
    public void ANamedOutlier_IsFoundByAFrustumWhoseBoundingBoxIsGenerous()
    {
        using var dbe = CreateEngine();
        SpawnCellCentres(dbe);
        var id = (long)Spawn(dbe, Box(250f, 250f, 180f)).RawValue;
        dbe.WriteTickFence(1);
        var cs = ClusterStateOf(dbe);
        Assert.That(Volatile.Read(ref cs.Realm0Spatial.EscapedClusters).Count, Is.EqualTo(1), "precondition: named");

        // The planes select [410, 420] on both axes, but the caller's bounding box starts at (200, 200) — over-generous, as a camera frustum's box is. The
        // walk then covers the outlier's home cell (2, 2) and REJECTS it, since that cell grown by a reach of zero lies outside the planes. The name must
        // still be classified.
        using var epoch = EpochGuard.Enter(dbe.EpochManager);
        double[] planes = [1d, 0d, -410d, -1d, 0d, 420d, 0d, 1d, -410d, 0d, -1d, 420d];
        var buffer = new long[4];
        int n = cs.QueryFrustum(dbe.Realm0Grid, planes, 4, new Vector3Like(200d, 200d, 0d), new Vector3Like(420d, 420d, 0d), buffer, categoryMask: 0);
        Assert.Multiple(() =>
        {
            Assert.That(n, Is.EqualTo(1));
            Assert.That(buffer[0], Is.EqualTo(id));
        });
    }

    [Test]
    [VerifiesRule("SQ-01")]
    public void Knn_FindsAnEntitySpawnedIntoANamedClusterBeforeTheNextFence()
    {
        using var dbe = CreateEngine();

        // A thin, wide outlier filed in cell (2, 2): x -40..540 — its in-world part reaches 200 and 240 past the cell — and y 245..255.
        var outlier = Spawn(dbe, new AABB2F { MinX = -40f, MinY = 245f, MaxX = 540f, MaxY = 255f });
        dbe.WriteTickFence(1);
        var cs = ClusterStateOf(dbe);
        Assert.That(Volatile.Read(ref cs.Realm0Spatial.EscapedClusters).Count, Is.EqualTo(1), "precondition: named");

        // No fence from here. E joins the named cluster — same cell, first fit — and widens its box up to y = 290; F is alone in cell (2, 3).
        var e = Spawn(dbe, Box(250f, 290f, 0f));
        Spawn(dbe, Box(250f, 320f, 0f));
        Assert.That(ClusterOf(dbe, e), Is.EqualTo(ClusterOf(dbe, outlier)), "non-vacuity: E must be IN the named cluster");

        // From (250, 301): E is 11 away, F 19. The named cluster's fence-time box ends at y = 255, 46 away — keyed by that, it would sit on the heap behind
        // F, and the rings would declare the search covered before opening it.
        using var epoch = EpochGuard.Enter(dbe.EpochManager);
        var buffer = new (long entityId, double distSq)[1];
        int n = cs.QueryNearest(dbe.Realm0Grid, 250d, 301d, 0d, k: 1, buffer, categoryMask: 0);
        Assert.Multiple(() =>
        {
            Assert.That(n, Is.EqualTo(1));
            Assert.That(buffer[0].entityId, Is.EqualTo((long)e.RawValue), "found only if the named cluster is keyed by its LIVE box");
            Assert.That(buffer[0].distSq, Is.EqualTo(121d).Within(1e-6));
        });
    }

    [Test]
    [VerifiesRule("SQ-01")]
    public void TheReachFalls_OnceTheOutlierIsGone()
    {
        using var dbe = CreateEngine();
        SpawnCellCentres(dbe);
        var outlier = Spawn(dbe, Box(250f, 250f, 180f));
        dbe.WriteTickFence(1);

        var cs = ClusterStateOf(dbe);
        Assert.That(Volatile.Read(ref cs.Realm0Spatial.EscapedClusters).Count, Is.EqualTo(1), "precondition: the outlier is named");

        using (var tx = dbe.CreateQuickTransaction())
        {
            tx.Destroy(outlier);
            tx.Commit();
        }

        dbe.WriteTickFence(2);
        Assert.Multiple(() =>
        {
            Assert.That(Volatile.Read(ref cs.Realm0Spatial.EscapedClusters).Count, Is.Zero, "recomputed at the very next fence, not remembered");
            Assert.That(Volatile.Read(ref cs.Realm0Spatial.ClusterReach), Is.Zero);
        });
        AssertCovered(dbe, "after the destroy's fence");

        using var epoch = EpochGuard.Enter(dbe.EpochManager);
        Assert.That(AabbCount(dbe, new AABB2F { MinX = 410f, MinY = 410f, MaxX = 420f, MaxY = 420f }), Is.Zero, "and nothing stale is reported");
    }

    [Test]
    [VerifiesRule("SQ-01")]
    public void MoreOutliersThanTheCapacity_WidenTheReach_AndEveryQueryStaysExact()
    {
        using var dbe = CreateEngine();
        var rnd = new Random(20260913);
        var boxes = new Dictionary<long, AABB2F>();

        // 24 outliers at cell centres, reaching 10, 15, ... 125 past their cells, and 300 small entities scattered anywhere (at most 3 past a cell).
        using (var tx = dbe.CreateQuickTransaction())
        {
            for (int i = 0; i < 24; i++)
            {
                var b = Box(((i % 8) * 100f) + 150f, ((i / 8) * 300f) + 150f, 60f + (i * 5f));
                boxes[(long)tx.Spawn<ClReachUnit>(ClReachUnit.Pos.Set(new ClReachPos { Bounds = b })).RawValue] = b;
            }

            for (int i = 0; i < 300; i++)
            {
                var b = Box((float)(rnd.NextDouble() * World), (float)(rnd.NextDouble() * World), (float)(rnd.NextDouble() * 3));
                boxes[(long)tx.Spawn<ClReachUnit>(ClReachUnit.Pos.Set(new ClReachPos { Bounds = b })).RawValue] = b;
            }

            tx.Commit();
        }

        dbe.WriteTickFence(1);
        var cs = ClusterStateOf(dbe);

        // The 17 largest are 125 ... 45; the base is the 17th, 45, and 50 lies within one margin of it, so it folds: reach 50, and 125 ... 55 are named.
        Assert.Multiple(() =>
        {
            Assert.That(Volatile.Read(ref cs.Realm0Spatial.EscapedClusters).Count, Is.EqualTo(15));
            Assert.That(Volatile.Read(ref cs.Realm0Spatial.ClusterReach), Is.EqualTo(50f).Within(1e-3f));
        });
        AssertCovered(dbe, "after the fence");

        using var epoch = EpochGuard.Enter(dbe.EpochManager);
        for (int q = 0; q < 300; q++)
        {
            float x = (float)(rnd.NextDouble() * World), y = (float)(rnd.NextDouble() * World), h = 1f + (float)(rnd.NextDouble() * 40);
            var box = Box(x, y, h);
            var expected = boxes.Where(kv => kv.Value.MaxX >= box.MinX && kv.Value.MinX <= box.MaxX && kv.Value.MaxY >= box.MinY && kv.Value.MinY <= box.MaxY)
                .Select(kv => kv.Key).OrderBy(k => k).ToArray();
            var got = AabbHits(dbe, box);
            got.Sort();
            var filled = AabbFill(dbe, box, 7);
            filled.Sort();
            Assert.That(got, Is.EqualTo(expected), $"AABB query {q} at ({x}, {y}) half {h}");
            Assert.That(filled, Is.EqualTo(expected), $"Fill, query {q}");
            Assert.That(AabbCount(dbe, box), Is.EqualTo(expected.Length), $"Count, query {q}");

            var inRadius = boxes.Where(kv => DistSq(kv.Value, x, y) <= (double)h * h).Select(kv => kv.Key).OrderBy(k => k).ToArray();
            var gotRadius = RadiusHits(dbe, x, y, h);
            gotRadius.Sort();
            Assert.That(gotRadius, Is.EqualTo(inRadius), $"radius query {q} at ({x}, {y}) r {h}");

            var nearest = boxes.Values.Select(b => DistSq(b, x, y)).OrderBy(d => d).Take(5).ToArray();
            var knn = new (long entityId, double distSq)[5];
            int n = cs.QueryNearest(dbe.Realm0Grid, x, y, 0d, k: 5, knn, categoryMask: 0);
            Assert.That(knn.Take(n).Select(r => r.distSq).ToArray(), Is.EqualTo(nearest).Within(1e-6), $"kNN query {q} at ({x}, {y})");
        }
    }

    private static double DistSq(AABB2F b, double x, double y)
    {
        double dx = Math.Max(0d, Math.Max(b.MinX - x, x - b.MaxX));
        double dy = Math.Max(0d, Math.Max(b.MinY - y, y - b.MaxY));
        return (dx * dx) + (dy * dy);
    }

    [Test]
    [VerifiesRule("SQ-01")]
    public void ASpawnIsReachable_BeforeAnyFence()
    {
        using var dbe = CreateEngine();
        SpawnCellCentres(dbe);
        dbe.WriteTickFence(1);

        // No fence after this spawn: nothing has recomputed the reach, so the spawn itself must have raised it before the index made the entity visible.
        var id = (long)Spawn(dbe, Box(250f, 250f, 180f)).RawValue;
        var cs = ClusterStateOf(dbe);
        Assert.That(Volatile.Read(ref cs.Realm0Spatial.ClusterReach), Is.GreaterThanOrEqualTo(130f), "raised by the spawn's own in-world overhang");
        AssertCovered(dbe, "after the spawn, before any fence");

        using var epoch = EpochGuard.Enter(dbe.EpochManager);
        Assert.That(AabbHits(dbe, new AABB2F { MinX = 410f, MinY = 410f, MaxX = 420f, MaxY = 420f }), Is.EqualTo(new[] { id }));
    }

    [Test]
    [VerifiesRule("SQ-01")]
    public void FoldReach_FoldsAgainstTheBase_AndNamesTheRest()
    {
        static (double Reach, int Named) Fold(double fold, params double[] top) =>
            (ArchetypeClusterState.FoldReach(top, top.Length, fold, out int named), named);

        Assert.Multiple(() =>
        {
            Assert.That(Fold(5d), Is.EqualTo((0d, 0)), "nothing overhangs");
            Assert.That(Fold(5d, 10d), Is.EqualTo((0d, 1)), "one outlier past a margin: named");
            Assert.That(Fold(5d, 3d), Is.EqualTo((3d, 0)), "within a margin of zero: folded");
            Assert.That(Fold(0d, 3d), Is.EqualTo((0d, 1)), "a zero margin folds nothing above the base");

            var sixteen = Enumerable.Range(0, 16).Select(i => 100d - i).ToArray();
            Assert.That(Fold(5d, sixteen), Is.EqualTo((0d, 16)), "sixteen outliers fit the capacity");

            var seventeen = Enumerable.Range(0, 17).Select(i => 17d - i).ToArray();   // 17 ... 1
            Assert.That(Fold(5d, seventeen), Is.EqualTo((6d, 11)), "full: the base is the 17th (1); 6 ... 2 fold, 17 ... 7 stay named");

            var ladder = Enumerable.Range(0, 17).Select(i => 100d - (i * 5d)).ToArray();   // 100, 95 ... 20
            Assert.That(Fold(5d, ladder), Is.EqualTo((25d, 15)), "a ladder does not cascade: only 25 is within a margin of the base 20");

            var ties = Enumerable.Repeat(10d, 17).ToArray();
            Assert.That(Fold(0d, ties), Is.EqualTo((10d, 0)), "ties at the base fold");
        });
    }

    [Test]
    [VerifiesRule("SQ-01")]
    public void RejectBounds_PassNoBoxTheScanWouldKeep_AndAreTight()
    {
        // Cells and reaches whose negations and sums do not round-trip through float: the bounds must be rounded inward, by one step at most.
        var cases = new (double Cell, double Admit)[]
        {
            (64d, 0d), (64d, 0.1d), (1024d, 51.93d), (1024d, 1e-7d), (128d, 439.3000001d), (16384d, 3.3d), (0.75d, 1d / 3d),
        };
        Assert.Multiple(() =>
        {
            foreach (var (cell, admit) in cases)
            {
                ArchetypeClusterState.RejectBounds(cell, admit, out float lo, out float hi);
                Assert.That(-(double)lo, Is.LessThanOrEqualTo(admit), $"lo inward, cell {cell}, admit {admit}");
                Assert.That((double)hi - cell, Is.LessThanOrEqualTo(admit), $"hi inward, cell {cell}, admit {admit}");
                Assert.That(-(double)MathF.BitDecrement(lo), Is.GreaterThan(admit), $"lo tight, cell {cell}, admit {admit}");
                Assert.That((double)MathF.BitIncrement(hi) - cell, Is.GreaterThan(admit), $"hi tight, cell {cell}, admit {admit}");
            }
        });
    }

    [Test]
    [VerifiesRule("SQ-01")]
    public void AGrowOfClusterAabbs_BetweenAWriteAndItsCheck_SendsTheWriterRoundAgain()
    {
        using var dbe = CreateEngine();
        SpawnCellCentres(dbe);
        dbe.WriteTickFence(1);
        var cs = ClusterStateOf(dbe);

        var stamp = cs.BeginClusterAabbsWrite();
        Assert.That(stamp & 1, Is.Zero, "no grow in flight");
        Assert.That(cs.ClusterAabbsWriteLanded(stamp), Is.True, "nothing grew since the stamp");

        var before = cs.ClusterAabbs;
        cs.EnsureClusterAabbsCapacity(before.Length * 2);
        Assert.That(cs.ClusterAabbs, Is.Not.SameAs(before), "the grow replaced the array");
        Assert.That(cs.ClusterAabbsWriteLanded(stamp), Is.False, "the copy may have been taken without a write made into the old array: redo it");
        Assert.That(cs.BeginClusterAabbsWrite(), Is.EqualTo(stamp + 2), "one grow, two bumps, even again");
    }

    [Test]
    [VerifiesRule("SQ-01")]
    public void SpawnsWideningClusters_WhileClusterAabbsGrows_LoseNoWiden()
    {
        using var dbe = CreateEngine();
        SpawnCellCentres(dbe);
        dbe.WriteTickFence(1);
        var cs = ClusterStateOf(dbe);

        // A large array makes each grow's copy last a millisecond or more. The entries these spawns write are near its start, so the copy passes them at
        // once, and a spawn that reads the old array during the rest of the copy writes where the copy has already been: without the redo, that widen is
        // lost. The writers therefore keep spawning until both grows have published, so the copies cannot finish before or after them.
        cs.EnsureClusterAabbsCapacity(1 << 18);
        const int Writers = 6;
        const int MaxSpawnsPerWriter = 5_000;   // a safety bound only: the writers stop when the grows have published
        var spawned = new List<(EntityId Id, AABB2F Box)>[Writers];
        var spawnedSoFar = 0;
        var growsDone = 0;
        var grows = 0;
        using var gate = new ManualResetEventSlim(false);
        var tasks = new Task[Writers + 1];
        for (int w = 0; w < Writers; w++)
        {
            int writer = w;
            spawned[w] = new List<(EntityId, AABB2F)>(MaxSpawnsPerWriter);
            tasks[w] = Task.Run(() =>
            {
                var rng = new Random(9100 + writer);
                gate.Wait();
                while (Volatile.Read(ref growsDone) == 0 && spawned[writer].Count < MaxSpawnsPerWriter)
                {
                    // Off its cell's centre, so each spawn widens the box of the cluster the centre point already opened.
                    float x = (rng.Next(10) * 100f) + 10f + ((float)rng.NextDouble() * 80f);
                    float y = (rng.Next(10) * 100f) + 10f + ((float)rng.NextDouble() * 80f);
                    var box = Box(x, y, 3f);
                    spawned[writer].Add((Spawn(dbe, box), box));
                    Interlocked.Increment(ref spawnedSoFar);
                }
            });
        }

        tasks[Writers] = Task.Run(() =>
        {
            gate.Wait();
            var spin = new SpinWait();
            while (Volatile.Read(ref spawnedSoFar) < Writers * 4)
            {
                spin.SpinOnce();
            }

            for (int g = 0; g < 2; g++)
            {
                cs.EnsureClusterAabbsCapacity(Volatile.Read(ref cs.ClusterAabbs).Length * 2);
                grows++;
            }

            Volatile.Write(ref growsDone, 1);
        });

        gate.Set();
        Assert.That(Task.WaitAll(tasks, 20_000), Is.True, "a task hung");
        Assert.That(grows, Is.EqualTo(2), "the array grew under the spawns");
        Assert.That(spawned.Any(s => s.Count == MaxSpawnsPerWriter), Is.False, "a writer reached its safety bound before the grows published");

        var clusterOf = ClustersByEntity(dbe);
        var grid = dbe.Realm0Grid;
        var aabbs = cs.ClusterAabbs;
        Assert.Multiple(() =>
        {
            foreach (var (id, box) in spawned.SelectMany(s => s))
            {
                Assert.That(clusterOf.TryGetValue((long)id.RawValue, out int cid), Is.True, $"entity {id} has a cluster");
                if (cid < 0)
                {
                    continue;
                }

                grid.CellOrigin(cs.ClusterCellMap[cid], out double ox, out double oy, out _);
                ref readonly var b = ref aabbs[cid];
                bool inside = b.MinX <= ClusterSpatialAabb.ToCellRelativeMin(box.MinX, ox) && b.MinY <= ClusterSpatialAabb.ToCellRelativeMin(box.MinY, oy)
                              && b.MaxX >= ClusterSpatialAabb.ToCellRelativeMax(box.MaxX, ox) && b.MaxY >= ClusterSpatialAabb.ToCellRelativeMax(box.MaxY, oy);
                Assert.That(inside, Is.True, $"entity {id} lies outside cluster {cid}'s ClusterAabbs entry: a widen was lost to a grow");
            }
        });

        dbe.WriteTickFence(2);
        AssertCovered(dbe, "after the fence");
    }

    [Test]
    [VerifiesRule("SQ-01")]
    public void EscapedClusterSet_IsCurrent_RejectsAFreedOrReusedChunkId()
    {
        var set = new EscapedClusterSet(1);
        set.ChunkIds[0] = 5;
        set.HomeCellKeys[0] = 3;
        var map = new int[8];
        var realms = new ushort[8];
        var realm0 = RealmId.Default;
        Assert.Multiple(() =>
        {
            map[5] = 3;
            Assert.That(set.IsCurrent(0, map, realms, realm0), Is.True, "still filed where it was named");
            realms[5] = 2;
            Assert.That(set.IsCurrent(0, map, realms, realm0), Is.False,
                "reused in ANOTHER REALM's cell of the same key (Realms C1): opening it would answer with another world's entities");
            realms[5] = 0;
            map[5] = -1;
            Assert.That(set.IsCurrent(0, map, realms, realm0), Is.False, "freed");
            map[5] = 4;
            Assert.That(set.IsCurrent(0, map, realms, realm0), Is.False, "reused in another cell: opening it would report that cluster's entities twice");
            Assert.That(set.IsCurrent(0, new int[4], realms, realm0), Is.False, "beyond the map");
            Assert.That(set.IsCurrent(0, null, realms, realm0), Is.False, "no map");
        });
    }
}
